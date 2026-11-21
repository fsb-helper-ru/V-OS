using System.Globalization;
using System.Text;
using VOS.Core.Data;

namespace VOS.Core.Timers;

/// <summary>
/// A small JSON list shared between processes (the Time app edits it, the shell acts on it): atomic writes under a
/// named mutex, and a file watcher that raises <see cref="Changed"/> when another process saves.
/// </summary>
public sealed class SharedJsonList<T> : IDisposable where T : class
{
    private readonly string _path;
    private readonly Mutex _mutex;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    public event Action? Changed;

    public SharedJsonList(string fileName, string? directory = null)
    {
        _path = Path.Combine(directory ?? VosPaths.Data, fileName);
        _mutex = new Mutex(false, @"Local\VOS." + Path.GetFileNameWithoutExtension(fileName));
    }

    public List<T> List() => VosJson.TryDeserialize<List<T>>(AtomicFile.TryReadAllText(_path)) ?? [];

    public void Mutate(Action<List<T>> change)
    {
        bool owned = false;
        try
        {
            try { owned = _mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { owned = true; }
            var before = AtomicFile.TryReadAllText(_path);
            var list = VosJson.TryDeserialize<List<T>>(before) ?? [];
            change(list);
            var after = VosJson.Serialize(list);
            if (after == before) return;
            AtomicFile.WriteAllText(_path, after);
        }
        finally { if (owned) _mutex.ReleaseMutex(); }
        Changed?.Invoke();
    }

    public void Watch()
    {
        if (_watcher != null) return;
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => _debounce.Change(120, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(120, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(120, Timeout.Infinite);
    }

    public void Dispose() { _watcher?.Dispose(); _debounce?.Dispose(); _mutex.Dispose(); }
}

/// <summary>An alarm at a local time of day; <see cref="Days"/> is a bit mask (bit 0 = Sunday … bit 6 = Saturday), 0 = once.</summary>
public sealed class Alarm
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public int Days { get; set; }
    public string Label { get; set; } = "Alarm";
    public bool Enabled { get; set; } = true;
    /// <summary>Local date-time of the last ring (prevents ringing twice for the same occurrence).</summary>
    public DateTime? LastFired { get; set; }
    public DateTime? SnoozedUntil { get; set; }

    public static readonly string[] DayLetters = ["S", "M", "T", "W", "T", "F", "S"];

    public bool RepeatsOn(DayOfWeek d) => (Days & (1 << (int)d)) != 0;

    /// <summary>Next local time this alarm rings, or null when it is off or a one-time alarm that already rang.</summary>
    public DateTime? Next(DateTime now)
    {
        if (!Enabled) return null;
        if (SnoozedUntil is { } s && s > now) return s;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            var at = day.AddHours(Hour).AddMinutes(Minute);
            if (at <= now) continue;
            // One-time alarms are switched off by the shell when they ring, so the next matching time is the answer.
            if (Days == 0 || RepeatsOn(day.DayOfWeek)) return at;
        }
        return null;
    }

    public string DaysText => Days switch
    {
        0 => "Once",
        0b1111111 => "Every day",
        0b0111110 => "Weekdays",
        0b1000001 => "Weekends",
        _ => string.Join(" ", Enumerable.Range(0, 7).Where(i => (Days & (1 << i)) != 0).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[i])),
    };
}

public sealed class WorldClock
{
    public string Name { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
}

public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }
    public string Location { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>"local", or "ics:&lt;file name&gt;" for imported calendars.</summary>
    public string Source { get; set; } = "local";
    public string Uid { get; set; } = "";
    public string? RRule { get; set; }
}

/// <summary>Local calendar (SQLite) with simple recurrence expansion. Times are stored as local wall-clock times.</summary>
public sealed class CalendarStore
{
    private readonly Db _db;

    public CalendarStore(string? path = null)
    {
        _db = path != null ? new Db(path) : Db.ForModule("calendar");
        _db.Migrate("CREATE TABLE events(id TEXT PRIMARY KEY, title TEXT NOT NULL, start TEXT NOT NULL, end TEXT NOT NULL, allday INTEGER NOT NULL, location TEXT, notes TEXT, source TEXT NOT NULL, uid TEXT, rrule TEXT);" +
                    "CREATE INDEX events_start ON events(start);");
    }

    public void Save(CalendarEvent e) =>
        _db.Execute("INSERT INTO events(id,title,start,end,allday,location,notes,source,uid,rrule) VALUES($id,$t,$s,$e,$a,$l,$n,$src,$u,$r) " +
                    "ON CONFLICT(id) DO UPDATE SET title=$t,start=$s,end=$e,allday=$a,location=$l,notes=$n,source=$src,uid=$u,rrule=$r",
            ("$id", e.Id), ("$t", e.Title), ("$s", e.Start.ToString("s", CultureInfo.InvariantCulture)), ("$e", e.End.ToString("s", CultureInfo.InvariantCulture)),
            ("$a", e.AllDay ? 1 : 0), ("$l", e.Location), ("$n", e.Notes), ("$src", e.Source), ("$u", e.Uid), ("$r", e.RRule));

    public void Delete(string id) => _db.Execute("DELETE FROM events WHERE id=$id", ("$id", id));
    public int DeleteSource(string source) => _db.Execute("DELETE FROM events WHERE source=$s", ("$s", source));
    public List<string> Sources() => _db.Query("SELECT DISTINCT source FROM events ORDER BY source", r => Db.Str(r, "source")!);

    /// <summary>Events (and occurrences of recurring events) overlapping [from, to).</summary>
    public List<CalendarEvent> Range(DateTime from, DateTime to)
    {
        var all = _db.Query("SELECT * FROM events WHERE (rrule IS NOT NULL AND rrule <> '') OR (start < $to AND end > $from)", Map,
            ("$from", from.ToString("s", CultureInfo.InvariantCulture)), ("$to", to.ToString("s", CultureInfo.InvariantCulture)));
        var result = new List<CalendarEvent>();
        foreach (var e in all)
        {
            if (string.IsNullOrEmpty(e.RRule)) { result.Add(e); continue; }
            result.AddRange(Recurrence.Expand(e, from, to));
        }
        return result.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Imports an .ics file, replacing events previously imported from a file of the same name.</summary>
    public int ImportIcs(string path)
    {
        var source = "ics:" + Path.GetFileName(path);
        var events = Ics.Parse(File.ReadAllText(path), source);
        _db.InTransaction((c, tx) =>
        {
            Db.Exec(c, "DELETE FROM events WHERE source=$s", tx, ("$s", source));
            foreach (var e in events)
                Db.Exec(c, "INSERT OR REPLACE INTO events(id,title,start,end,allday,location,notes,source,uid,rrule) VALUES($id,$t,$s,$e,$a,$l,$n,$src,$u,$r)", tx,
                    ("$id", e.Id), ("$t", e.Title), ("$s", e.Start.ToString("s", CultureInfo.InvariantCulture)), ("$e", e.End.ToString("s", CultureInfo.InvariantCulture)),
                    ("$a", e.AllDay ? 1 : 0), ("$l", e.Location), ("$n", e.Notes), ("$src", e.Source), ("$u", e.Uid), ("$r", e.RRule));
        });
        return events.Count;
    }

    private static CalendarEvent Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = Db.Str(r, "id")!, Title = Db.Str(r, "title") ?? "", AllDay = Db.Bool(r, "allday"),
        Start = DateTime.Parse(Db.Str(r, "start")!, CultureInfo.InvariantCulture), End = DateTime.Parse(Db.Str(r, "end")!, CultureInfo.InvariantCulture),
        Location = Db.Str(r, "location") ?? "", Notes = Db.Str(r, "notes") ?? "", Source = Db.Str(r, "source") ?? "local", Uid = Db.Str(r, "uid") ?? "", RRule = Db.Str(r, "rrule"),
    };
}

/// <summary>Minimal iCalendar (RFC 5545) reader: VEVENT with SUMMARY, DTSTART/DTEND/DURATION (date, local, UTC, TZID), LOCATION, DESCRIPTION, UID, RRULE.</summary>
public static class Ics
{
    public static List<CalendarEvent> Parse(string text, string source)
    {
        var lines = Unfold(text);
        var events = new List<CalendarEvent>();
        Dictionary<string, (string value, Dictionary<string, string> p)>? cur = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null && ToEvent(cur, source) is { } e) events.Add(e);
                cur = null;
                continue;
            }
            if (cur == null) continue;
            int colon = IndexOfUnquoted(line, ':');
            if (colon <= 0) continue;
            var head = line[..colon].Split(';');
            var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in head.Skip(1)) { int eq = part.IndexOf('='); if (eq > 0) p[part[..eq]] = part[(eq + 1)..].Trim('"'); }
            cur.TryAdd(head[0], (line[(colon + 1)..], p));
        }
        return events;
    }

    private static CalendarEvent? ToEvent(Dictionary<string, (string value, Dictionary<string, string> p)> f, string source)
    {
        if (!f.TryGetValue("DTSTART", out var ds) || ParseDate(ds.value, ds.p) is not (DateTime start, bool allDay)) return null;
        DateTime end;
        if (f.TryGetValue("DTEND", out var de) && ParseDate(de.value, de.p) is (DateTime e, _)) end = e;
        else if (f.TryGetValue("DURATION", out var du) && ParseDuration(du.value) is { } d) end = start + d;
        else end = allDay ? start.AddDays(1) : start.AddHours(1);
        var uid = f.TryGetValue("UID", out var u) ? u.value : "";
        return new CalendarEvent
        {
            Id = uid.Length > 0 ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(source + "|" + uid + "|" + start.Ticks)))[..24] : Guid.NewGuid().ToString("N"),
            Title = Unescape(f.TryGetValue("SUMMARY", out var s) ? s.value : "(No title)"),
            Start = start, End = end > start ? end : start.AddMinutes(30), AllDay = allDay,
            Location = Unescape(f.TryGetValue("LOCATION", out var l) ? l.value : ""),
            Notes = Unescape(f.TryGetValue("DESCRIPTION", out var n) ? n.value : ""),
            Source = source, Uid = uid,
            RRule = f.TryGetValue("RRULE", out var r) ? r.value : null,
        };
    }

    /// <summary>Returns a local wall-clock time and whether it is a date (all-day) value.</summary>
    public static (DateTime value, bool allDay)? ParseDate(string v, Dictionary<string, string> p)
    {
        v = v.Trim();
        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (d, true);
        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        if (utc) return (DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(), false);
        if (p.TryGetValue("TZID", out var tzid))
        {
            var tz = FindZone(tzid);
            if (tz != null) return (TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local), false);
        }
        return (t, false);
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        try { if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
        return null;
    }

    public static TimeSpan? ParseDuration(string v)
    {
        // P1DT2H30M, PT45M, P2W
        var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var t = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        return m.Groups[1].Value == "-" ? -t : t;
    }

    private static List<string> Unfold(string text)
    {
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>();
        foreach (var l in raw)
        {
            if ((l.StartsWith(' ') || l.StartsWith('\t')) && lines.Count > 0) lines[^1] += l[1..];
            else if (l.Length > 0) lines.Add(l);
        }
        return lines;
    }

    private static int IndexOfUnquoted(string s, char c)
    {
        bool q = false;
        for (int i = 0; i < s.Length; i++) { if (s[i] == '"') q = !q; else if (s[i] == c && !q) return i; }
        return -1;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}

/// <summary>RRULE expansion for the common cases: FREQ=DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL and BYDAY (weekly).</summary>
public static class Recurrence
{
    public static IEnumerable<CalendarEvent> Expand(CalendarEvent e, DateTime from, DateTime to)
    {
        var rule = (e.RRule ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
        if (!rule.TryGetValue("FREQ", out var freq)) { if (e.Start < to && e.End > from) yield return e; yield break; }
        int interval = rule.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var ivn) && ivn > 0 ? ivn : 1;
        int? count = rule.TryGetValue("COUNT", out var c) && int.TryParse(c, out var cn) ? cn : null;
        DateTime? until = rule.TryGetValue("UNTIL", out var u) && Ics.ParseDate(u, new Dictionary<string, string>()) is (DateTime ud, _) ? ud : null;
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(d => d[^2..]).ToHashSet() : null;
        var length = e.End - e.Start;
        int emitted = 0;
        var cursor = e.Start;
        for (int guard = 0; guard < 5000; guard++)
        {
            if (until is { } un && cursor > un) yield break;
            if (cursor >= to) yield break;
            IEnumerable<DateTime> candidates = freq == "WEEKLY" && byDay is { Count: > 0 }
                ? Enumerable.Range(0, 7).Select(i => cursor.Date.AddDays(i - (int)cursor.DayOfWeek) + e.Start.TimeOfDay).Where(d => byDay.Contains(Code(d.DayOfWeek)) && d >= e.Start)
                : [cursor];
            foreach (var start in candidates.OrderBy(d => d))
            {
                if (until is { } un2 && start > un2) yield break;
                if (count is { } cnt && emitted >= cnt) yield break;
                emitted++;
                if (start < to && start + length > from)
                    yield return new CalendarEvent { Id = e.Id + "@" + start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture), Title = e.Title, Start = start, End = start + length, AllDay = e.AllDay, Location = e.Location, Notes = e.Notes, Source = e.Source, Uid = e.Uid, RRule = e.RRule };
            }
            cursor = freq switch
            {
                "DAILY" => cursor.AddDays(interval),
                "WEEKLY" => cursor.AddDays(7 * interval),
                "MONTHLY" => cursor.AddMonths(interval),
                "YEARLY" => cursor.AddYears(interval),
                _ => to,
            };
        }
    }

    private static string Code(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU", DayOfWeek.Wednesday => "WE", DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR", DayOfWeek.Saturday => "SA", _ => "SU",
    };
}
