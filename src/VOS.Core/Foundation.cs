using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace VOS.Core;

/// <summary>Product identity and build information.</summary>
public static class AppInfo
{
    public const string ProductName = "V-OS";
    public const string Publisher = "V-OS Project";
    public const string Codename = "Aurora";

    public static string Version { get; } = ResolveVersion();

    public static Version NumericVersion =>
        System.Version.TryParse(Version.Split('-', '+')[0], out var v) ? v : new Version(1, 0, 0);

    public static string? DefaultUpdateFeed { get; } = ReadMetadata("VosUpdateFeed");
    public static string? BuildTime { get; } = ReadMetadata("VosBuildTime");

    private static string ResolveVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "1.0.0" : info.Split('+')[0];
    }

    private static string? ReadMetadata(string key)
    {
        foreach (var a in typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (a.Key == key && !string.IsNullOrWhiteSpace(a.Value)) return a.Value;
        return null;
    }
}

/// <summary>Well-known V-OS locations. User data never lives next to the binaries.</summary>
public static class VosPaths
{
    private static string _dataRoot = ResolveDataRoot();

    public static string DataRoot => _dataRoot;
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string InstallRoot => Path.Combine(LocalAppData, "Programs", "V-OS");
    public static string AppDir => AppContext.BaseDirectory;
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string Logs => Ensure("logs");
    public static string Cache => Ensure("cache");
    public static string Data => Ensure("data");
    public static string Snapshots => Ensure("snapshots");
    public static string Crashes => Ensure("crashes");
    public static string Temp => Ensure("temp");
    public static string State => Ensure("state");
    public static string Db(string name) => Path.Combine(Data, name + ".db");

    private static string ResolveDataRoot()
    {
        var o = Environment.GetEnvironmentVariable("VOS_DATA_ROOT");
        return !string.IsNullOrWhiteSpace(o) ? o : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-OS");
    }

    /// <summary>Test hook: redirect all user data to another folder.</summary>
    public static void OverrideDataRoot(string path) => _dataRoot = path;

    private static string Ensure(string sub)
    {
        var p = Path.Combine(DataRoot, sub);
        Directory.CreateDirectory(p);
        return p;
    }
}

public static class VosJson
{
    public static readonly JsonSerializerOptions Options = Create(true);
    public static readonly JsonSerializerOptions Compact = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Crash-safe file writes: write a temp file, then atomically replace.</summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void WriteAllText(string path, string content) => WriteAllBytes(path, Utf8.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(content, 0, content.Length);
            fs.Flush(true);
        }
        Retry(() =>
        {
            if (File.Exists(path)) File.Replace(tmp, path, null, true);
            else File.Move(tmp, path);
        });
    }

    public static string? TryReadAllText(string path)
    {
        string? result = null;
        try
        {
            Retry(() =>
            {
                if (!File.Exists(path)) { result = null; return; }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Utf8);
                result = sr.ReadToEnd();
            });
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return result;
    }

    public static void Retry(Action action, int attempts = 10)
    {
        for (int i = 0; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(15 + i * 15); }
            catch (UnauthorizedAccessException) when (i < attempts - 1) { Thread.Sleep(15 + i * 15); }
        }
    }
}

public enum LogLevel { Debug, Info, Warn, Error, Fatal }

/// <summary>
/// Structured, asynchronous JSON-lines logger. Writing never blocks the caller (UI threads stay responsive);
/// one file per component per day, rotated at 5 MB, 10 days retained.
/// </summary>
public static class Log
{
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly object Gate = new();
    private static string _component = "vos";
    private static Task? _writer;
    private static int _pending;
    private const long MaxBytes = 5 * 1024 * 1024;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static string Component => _component;
    public static string CurrentFile => Path.Combine(VosPaths.Logs, $"{_component}-{DateTime.Now:yyyyMMdd}.log");

    public static void Init(string component)
    {
        _component = component;
        if (Environment.GetEnvironmentVariable("VOS_DEBUG_LOG") == "1") MinLevel = LogLevel.Debug;
        EnsureWriter();
        _ = Task.Run(CleanupOldLogs);
    }

    public static void Debug(string message, object? data = null) => Write(LogLevel.Debug, message, null, data);
    public static void Info(string message, object? data = null) => Write(LogLevel.Info, message, null, data);
    public static void Warn(string message, Exception? ex = null, object? data = null) => Write(LogLevel.Warn, message, ex, data);
    public static void Error(string message, Exception? ex = null, object? data = null) => Write(LogLevel.Error, message, ex, data);
    public static void Fatal(string message, Exception? ex = null, object? data = null) => Write(LogLevel.Fatal, message, ex, data);

    public static void Write(LogLevel level, string message, Exception? ex, object? data)
    {
        if (level < MinLevel) return;
        var sb = new StringBuilder(160);
        sb.Append("{\"t\":\"").Append(DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture))
          .Append("\",\"lvl\":\"").Append(level.ToString().ToLowerInvariant())
          .Append("\",\"src\":").Append(JsonSerializer.Serialize(_component))
          .Append(",\"pid\":").Append(Environment.ProcessId)
          .Append(",\"msg\":").Append(JsonSerializer.Serialize(message));
        if (ex != null) sb.Append(",\"ex\":").Append(JsonSerializer.Serialize(ex.ToString()));
        if (data != null)
        {
            try { sb.Append(",\"data\":").Append(JsonSerializer.Serialize(data, VosJson.Compact)); }
            catch { sb.Append(",\"data\":\"<unserializable>\""); }
        }
        sb.Append('}');
        var line = sb.ToString();
        System.Diagnostics.Debug.WriteLine(line);
        Interlocked.Increment(ref _pending);
        Queue.Writer.TryWrite(line);
        EnsureWriter();
    }

    /// <summary>Blocks until queued lines are on disk (used at shutdown and on crash).</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
    }

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        lock (Gate) _writer ??= Task.Run(WriterLoop);
    }

    private static async Task WriterLoop()
    {
        var reader = Queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                var path = CurrentFile;
                RotateIfNeeded(path);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                while (reader.TryRead(out var line))
                {
                    sw.WriteLine(line);
                    Interlocked.Decrement(ref _pending);
                }
            }
            catch
            {
                // Logging must never crash the host; drop what we could not write.
                while (reader.TryRead(out _)) Interlocked.Decrement(ref _pending);
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var rotated = Path.ChangeExtension(path, $".{DateTime.Now:HHmmss}.log");
            File.Move(path, rotated, true);
        }
        catch { /* another process may be rotating; fine */ }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-10);
            foreach (var f in Directory.EnumerateFiles(VosPaths.Logs, "*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            foreach (var f in Directory.EnumerateFiles(VosPaths.Crashes, "*.txt"))
                if (File.GetLastWriteTime(f) < cutoff.AddDays(-20)) File.Delete(f);
        }
        catch { }
    }
}

/// <summary>Writes a human-readable crash report and makes sure logs reach the disk.</summary>
public static class CrashReporter
{
    private static int _installed;
    public static event Action<Exception>? Crashed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, fatal: e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    public static string? Report(Exception ex, bool fatal)
    {
        try
        {
            Log.Write(fatal ? LogLevel.Fatal : LogLevel.Error, "Unhandled exception", ex, null);
            var file = Path.Combine(VosPaths.Crashes, $"{Log.Component}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"V-OS {AppInfo.Version} crash report");
            sb.AppendLine($"Component: {Log.Component}  PID: {Environment.ProcessId}  Fatal: {fatal}");
            sb.AppendLine($"Time: {DateTimeOffset.Now:o}");
            sb.AppendLine($"OS: {Environment.OSVersion}  64-bit: {Environment.Is64BitOperatingSystem}  CLR: {Environment.Version}");
            sb.AppendLine($"Memory (working set): {Environment.WorkingSet / 1048576} MB");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(file, sb.ToString());
            Log.Flush();
            try { Crashed?.Invoke(ex); } catch { }
            return file;
        }
        catch { return null; }
    }
}

/// <summary>Human-friendly formatting helpers. Display uses the user's culture; storage never does.</summary>
public static class Fmt
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[u]}";
    }

    public static string BytesPerSecond(double bps) => Bytes((long)bps) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string DurationWords(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min";
        if (t.TotalHours < 24) return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalDays} d {t.Hours} h";
    }

    public static string Relative(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.Now;
        var d = n - time;
        if (d.TotalSeconds < 45) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24 && time.Date == n.Date) return time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        if (time.Date == n.Date.AddDays(-1)) return "Yesterday " + time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        if (d.TotalDays < 7) return time.ToLocalTime().ToString("dddd", CultureInfo.CurrentCulture);
        return time.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    public static string Percent(double fraction) => $"{Math.Round(fraction * 100):0}%";

    public static string Invariant(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);
}
