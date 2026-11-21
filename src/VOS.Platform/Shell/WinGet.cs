using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using VOS.Core;

namespace VOS.Platform.Shell;

public sealed record WinGetPackage(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// The Windows Package Manager (winget) through its command line. Tables are parsed by column position, taken from
/// the header above the dashed rule, so it works in every display language. Installs run with agreements accepted
/// on the user's behalf only after they click Install, and never elevate by themselves (installers that need
/// administrator rights show the Windows prompt).
/// </summary>
public static partial class WinGet
{
    private static bool? s_available;

    public static async Task<bool> IsAvailableAsync()
    {
        if (s_available is bool b) return b;
        try { var (code, _) = await RunAsync(["--version"], null, CancellationToken.None, TimeSpan.FromSeconds(10)); s_available = code == 0; }
        catch { s_available = false; }
        return s_available.Value;
    }

    public static async Task<List<WinGetPackage>> SearchAsync(string query, CancellationToken ct = default)
    {
        var (_, output) = await RunAsync(["search", "--query", query, "--source", "winget", "--accept-source-agreements", "--disable-interactivity"], null, ct);
        return ParseTable(output);
    }

    public static async Task<List<WinGetPackage>> InstalledAsync(CancellationToken ct = default)
    {
        var (_, output) = await RunAsync(["list", "--accept-source-agreements", "--disable-interactivity"], null, ct);
        return ParseTable(output);
    }

    public static async Task<List<WinGetPackage>> UpgradesAsync(CancellationToken ct = default)
    {
        var (_, output) = await RunAsync(["upgrade", "--accept-source-agreements", "--disable-interactivity"], null, ct);
        return ParseTable(output).Where(p => p.Available.Length > 0).ToList();
    }

    /// <summary>install / upgrade / uninstall one package. Reports progress 0..1 when winget prints it. Returns (success, last message).</summary>
    public static async Task<(bool ok, string message)> ApplyAsync(string verb, string id, IProgress<double>? progress, CancellationToken ct = default)
    {
        string[] args = verb switch
        {
            "install" => ["install", "--id", id, "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            "upgrade" => ["upgrade", "--id", id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            "uninstall" => ["uninstall", "--id", id, "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity"],
            _ => throw new ArgumentException("Unknown verb", nameof(verb)),
        };
        string last = "";
        var (code, output) = await RunAsync(args, line =>
        {
            if (Progress(line) is double p) progress?.Report(p);
            var clean = line.Trim();
            if (clean.Length > 3 && !clean.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒')) last = clean;
        }, ct, TimeSpan.FromMinutes(30));
        bool ok = code == 0;
        return (ok, ok ? last : $"winget exited with code 0x{code:X8}. {last}");
    }

    /// <summary>"2.00 MB / 5.00 MB" or "45%" → 0..1.</summary>
    public static double? Progress(string line)
    {
        var m = SizeRatio().Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
                      && double.TryParse(m.Groups[3].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0)
            return Math.Clamp(a * Unit(m.Groups[2].Value) / (b * Unit(m.Groups[4].Value)), 0, 1);
        var pct = Percent().Match(line);
        if (pct.Success && int.TryParse(pct.Groups[1].Value, out var v)) return Math.Clamp(v / 100.0, 0, 1);
        return null;
        static double Unit(string u) => u.ToUpperInvariant() switch { "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, _ => 1 };
    }

    [GeneratedRegex(@"([\d.,]+)\s*(KB|MB|GB|B)\s*/\s*([\d.,]+)\s*(KB|MB|GB|B)", RegexOptions.IgnoreCase)] private static partial Regex SizeRatio();
    [GeneratedRegex(@"(\d{1,3})\s*%")] private static partial Regex Percent();

    /// <summary>Parses winget's fixed-width tables: column starts come from the header line above the dashed rule.</summary>
    public static List<WinGetPackage> ParseTable(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();
        int rule = lines.FindIndex(l => l.Length >= 10 && l.Trim().Length >= 10 && l.Trim().All(c => c == '-'));
        if (rule < 1) return [];
        var header = lines[rule - 1];
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 3) return [];
        var result = new List<WinGetPackage>();
        foreach (var line in lines.Skip(rule + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < starts[1]) continue;
            string Col(int i) => starts[i] >= line.Length ? "" : line[starts[i]..(i + 1 < starts.Count ? Math.Min(line.Length, starts[i + 1]) : line.Length)].Trim();
            var name = Col(0);
            var id = Col(1);
            if (id.Length == 0 || id.Contains(' ')) continue; // footer lines ("3 upgrades available.")
            // search: Name Id Version Match [Source] · list: Name Id Version [Available] Source · upgrade: Name Id Version Available Source
            string version = Col(2);
            string available = "", source = "";
            if (starts.Count >= 5) { available = Col(3); source = Col(4); }
            else if (starts.Count == 4) source = Col(3);
            if (available.StartsWith("Tag:", StringComparison.OrdinalIgnoreCase) || available.StartsWith("Moniker:", StringComparison.OrdinalIgnoreCase) || available.Contains(':')) available = "";
            result.Add(new WinGetPackage(name, id, version, available, source));
        }
        return result;
    }

    private static async Task<(int code, string output)> RunAsync(string[] args, Action<string>? onLine, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("winget") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget could not be started");
        var sb = new StringBuilder();
        var buffer = new char[1024];
        var reader = p.StandardOutput;
        var line = new StringBuilder();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        int n;
        while ((n = await reader.ReadAsync(buffer, ct)) > 0)
        {
            sb.Append(buffer, 0, n);
            if (onLine != null)
                for (int i = 0; i < n; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n') { if (line.Length > 0) { onLine(line.ToString()); line.Clear(); } }
                    else line.Append(c);
                }
            if (DateTime.UtcNow > deadline) { try { p.Kill(true); } catch { } break; }
        }
        await p.WaitForExitAsync(ct);
        try { sb.Append(await stderr); } catch { }
        return (p.ExitCode, sb.ToString());
    }
}
