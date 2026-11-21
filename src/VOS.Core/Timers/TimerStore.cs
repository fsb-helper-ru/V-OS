namespace VOS.Core.Timers;

public enum TimerState { Running, Paused, Done, Cancelled }

public sealed class TimerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Label { get; set; } = "Timer";
    public int DurationSeconds { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public int RemainingSeconds { get; set; }
    public TimerState State { get; set; } = TimerState.Running;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public bool Alerted { get; set; }

    public TimeSpan Remaining(DateTimeOffset now) => State switch
    {
        TimerState.Running when EndsAt is { } e => e > now ? e - now : TimeSpan.Zero,
        TimerState.Paused => TimeSpan.FromSeconds(RemainingSeconds),
        _ => TimeSpan.Zero,
    };

    public double Progress(DateTimeOffset now) => DurationSeconds <= 0 ? 1 : 1 - Remaining(now).TotalSeconds / DurationSeconds;
}

/// <summary>
/// Timers shared by the Time app and the shell (which shows them in the V-Pill and raises the alert).
/// Stored as absolute end times, so every process computes the same remaining time.
/// </summary>
public sealed class TimerStore : IDisposable
{
    private readonly string _path;
    private readonly Mutex _mutex = new(false, @"Local\VOS.Timers");
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public event Action? Changed;

    public TimerStore(string? path = null) => _path = path ?? Path.Combine(VosPaths.Data, "timers.json");

    public List<TimerItem> List() => VosJson.TryDeserialize<List<TimerItem>>(AtomicFile.TryReadAllText(_path)) ?? [];

    public TimerItem Start(TimeSpan duration, string? label = null)
    {
        var t = new TimerItem
        {
            Label = string.IsNullOrWhiteSpace(label) ? $"{Fmt.DurationWords(duration)} timer" : label.Trim(),
            DurationSeconds = (int)Math.Round(duration.TotalSeconds),
            EndsAt = DateTimeOffset.Now + duration,
            State = TimerState.Running,
        };
        Mutate(list => list.Add(t));
        return t;
    }

    public void Pause(string id) => Mutate(list =>
    {
        var t = list.FirstOrDefault(x => x.Id == id);
        if (t is { State: TimerState.Running })
        {
            t.RemainingSeconds = (int)Math.Ceiling(t.Remaining(DateTimeOffset.Now).TotalSeconds);
            t.EndsAt = null;
            t.State = TimerState.Paused;
        }
    });

    public void Resume(string id) => Mutate(list =>
    {
        var t = list.FirstOrDefault(x => x.Id == id);
        if (t is { State: TimerState.Paused })
        {
            t.EndsAt = DateTimeOffset.Now.AddSeconds(t.RemainingSeconds);
            t.State = TimerState.Running;
        }
    });

    public void AddTime(string id, TimeSpan extra) => Mutate(list =>
    {
        var t = list.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        if (t.State == TimerState.Running && t.EndsAt is { } e) t.EndsAt = (e > DateTimeOffset.Now ? e : DateTimeOffset.Now) + extra;
        else if (t.State == TimerState.Paused) t.RemainingSeconds += (int)extra.TotalSeconds;
        else if (t.State == TimerState.Done) { t.EndsAt = DateTimeOffset.Now + extra; t.State = TimerState.Running; t.Alerted = false; }
        t.DurationSeconds += (int)extra.TotalSeconds;
    });

    public void Cancel(string id) => Mutate(list => list.RemoveAll(x => x.Id == id));

    /// <summary>Marks finished timers as done; returns those that just finished and have not been alerted yet.</summary>
    public List<TimerItem> CollectFinished()
    {
        var finished = new List<TimerItem>();
        Mutate(list =>
        {
            var now = DateTimeOffset.Now;
            foreach (var t in list.Where(t => t.State == TimerState.Running && t.EndsAt <= now && !t.Alerted))
            {
                t.State = TimerState.Done;
                t.Alerted = true;
                finished.Add(t);
            }
            list.RemoveAll(t => t.State == TimerState.Done && now - (t.EndsAt ?? now) > TimeSpan.FromHours(6));
        }, raiseIfUnchanged: false);
        return finished;
    }

    private void Mutate(Action<List<TimerItem>> change, bool raiseIfUnchanged = true)
    {
        bool owned = false;
        try
        {
            try { owned = _mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { owned = true; }
            var before = AtomicFile.TryReadAllText(_path);
            var list = VosJson.TryDeserialize<List<TimerItem>>(before) ?? [];
            change(list);
            var after = VosJson.Serialize(list);
            if (after != before) AtomicFile.WriteAllText(_path, after);
            else if (!raiseIfUnchanged) return;
        }
        finally { if (owned) _mutex.ReleaseMutex(); }
        Changed?.Invoke();
    }

    public void Watch()
    {
        if (_watcher != null) return;
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _debounce.Change(100, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(100, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(100, Timeout.Infinite);
    }

    public void Dispose() { _watcher?.Dispose(); _debounce?.Dispose(); _mutex.Dispose(); }
}
