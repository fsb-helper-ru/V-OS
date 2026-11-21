namespace VOS.Core.Settings;

/// <summary>
/// Process-wide access to <see cref="VosSettings"/>. settings.json is the single source of truth:
/// writes are atomic and serialized across processes with a named mutex; other processes pick up
/// changes through a debounced file watcher.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly Lazy<SettingsStore> s_instance = new(() => new SettingsStore(VosPaths.SettingsFile));
    public static SettingsStore Instance => s_instance.Value;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Mutex _crossProcess;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastJson;

    public VosSettings Current { get; private set; } = new();

    /// <summary>Raised (on a worker thread) after settings changed in this or another process.</summary>
    public event Action<VosSettings>? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        _crossProcess = new Mutex(false, @"Local\VOS.Settings." + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(path)));
        Load();
    }

    public void Load()
    {
        lock (_gate)
        {
            var json = AtomicFile.TryReadAllText(_path);
            var loaded = Parse(json, out bool corrupt);
            if (corrupt) BackupCorrupt(json!);
            loaded.Normalize();
            Current = loaded;
            _lastJson = json;
        }
    }

    private static VosSettings Parse(string? json, out bool corrupt)
    {
        corrupt = false;
        if (string.IsNullOrWhiteSpace(json)) return new VosSettings();
        try { return VosJson.Deserialize<VosSettings>(json) ?? new VosSettings(); }
        catch (Exception ex)
        {
            Log.Error("settings.json is unreadable; defaults loaded and the file backed up", ex);
            corrupt = true;
            return new VosSettings();
        }
    }

    private void BackupCorrupt(string json)
    {
        try { File.WriteAllText(_path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", json); } catch { }
    }

    /// <summary>Read-modify-write under a cross-process lock so concurrent processes never lose each other's edits.</summary>
    public void Update(Action<VosSettings> mutate)
    {
        VosSettings snapshot;
        bool owned = false;
        try
        {
            try { owned = _crossProcess.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { owned = true; }
            lock (_gate)
            {
                var fresh = Parse(AtomicFile.TryReadAllText(_path), out _);
                fresh.Normalize();
                mutate(fresh);
                fresh.Normalize();
                var json = VosJson.Serialize(fresh);
                if (json != _lastJson)
                {
                    AtomicFile.WriteAllText(_path, json);
                    _lastJson = json;
                }
                Current = fresh;
                snapshot = fresh;
            }
        }
        finally
        {
            if (owned) _crossProcess.ReleaseMutex();
        }
        Raise(snapshot);
    }

    /// <summary>Writes defaults on first run so every process sees the same file.</summary>
    public void EnsureFile()
    {
        if (!File.Exists(_path)) Update(_ => { });
    }

    /// <summary>Starts watching settings.json for edits made by other V-OS processes.</summary>
    public void Watch()
    {
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        _debounce = new Timer(_ => ReloadIfChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _debounce.Change(150, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(150, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(150, Timeout.Infinite);
    }

    private void ReloadIfChanged()
    {
        VosSettings? updated = null;
        lock (_gate)
        {
            var json = AtomicFile.TryReadAllText(_path);
            if (json == null || json == _lastJson) return;
            var parsed = Parse(json, out bool corrupt);
            if (corrupt) return;
            parsed.Normalize();
            Current = parsed;
            _lastJson = json;
            updated = parsed;
        }
        Raise(updated);
    }

    private void Raise(VosSettings s)
    {
        try { Changed?.Invoke(s); }
        catch (Exception ex) { Log.Error("Settings change handler failed", ex); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _crossProcess.Dispose();
    }
}

/// <summary>V-Snapshots: restore points for V-OS configuration (settings, workspaces, dock, widgets).</summary>
public sealed class SnapshotService
{
    public sealed record Snapshot(string Id, string Label, DateTimeOffset Created, string Path, long Size, string Version);

    private readonly string _dir;
    public SnapshotService(string? dir = null) => _dir = dir ?? VosPaths.Snapshots;

    public Snapshot Create(string label)
    {
        Directory.CreateDirectory(_dir);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var safe = string.Concat(label.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
        var path = System.IO.Path.Combine(_dir, $"{id}.json");
        var payload = new SnapshotFile
        {
            Label = string.IsNullOrWhiteSpace(safe) ? "Snapshot" : safe,
            Created = DateTimeOffset.Now,
            Version = AppInfo.Version,
            Settings = AtomicFile.TryReadAllText(VosPaths.SettingsFile) ?? VosJson.Serialize(new VosSettings()),
        };
        AtomicFile.WriteAllText(path, VosJson.Serialize(payload));
        Prune(keep: 30);
        return Read(path)!;
    }

    public IReadOnlyList<Snapshot> List()
    {
        if (!Directory.Exists(_dir)) return [];
        return Directory.EnumerateFiles(_dir, "*.json")
            .Select(Read).Where(s => s != null).Cast<Snapshot>()
            .OrderByDescending(s => s.Created).ToList();
    }

    public void Restore(string id, SettingsStore store)
    {
        var path = System.IO.Path.Combine(_dir, id + ".json");
        var file = VosJson.TryDeserialize<SnapshotFile>(AtomicFile.TryReadAllText(path))
                   ?? throw new InvalidOperationException("Snapshot not found or unreadable.");
        var restored = VosJson.TryDeserialize<VosSettings>(file.Settings)
                       ?? throw new InvalidOperationException("Snapshot contains invalid settings.");
        Create("Before restoring " + file.Label);
        store.Update(s =>
        {
            var json = VosJson.Serialize(restored);
            var copy = VosJson.Deserialize<VosSettings>(json)!;
            foreach (var p in typeof(VosSettings).GetProperties().Where(p => p.CanWrite))
                p.SetValue(s, p.GetValue(copy));
        });
    }

    public void Delete(string id)
    {
        var path = System.IO.Path.Combine(_dir, id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private void Prune(int keep)
    {
        foreach (var old in List().Skip(keep)) { try { File.Delete(old.Path); } catch { } }
    }

    private static Snapshot? Read(string path)
    {
        var f = VosJson.TryDeserialize<SnapshotFile>(AtomicFile.TryReadAllText(path));
        if (f == null) return null;
        return new Snapshot(System.IO.Path.GetFileNameWithoutExtension(path), f.Label, f.Created, path, new FileInfo(path).Length, f.Version);
    }

    private sealed class SnapshotFile
    {
        public string Label { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public string Version { get; set; } = "";
        public string Settings { get; set; } = "";
    }
}
