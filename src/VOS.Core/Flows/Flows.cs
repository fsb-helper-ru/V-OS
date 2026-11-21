using System.Globalization;

namespace VOS.Core.Flows;

/// <summary>When a flow runs: kind is "time", "startup", "app", "focus", "power" or "workspace".</summary>
public sealed class FlowTrigger
{
    public string Kind { get; set; } = "time";
    /// <summary>"HH:mm" for time triggers.</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>Bit mask, bit 0 = Sunday; 0 = every day.</summary>
    public int Days { get; set; } = 0b0111110;
    /// <summary>Process name for app triggers (with or without .exe).</summary>
    public string App { get; set; } = "";
    /// <summary>"on"/"off" (focus), "battery"/"plugged" (power), or a workspace id.</summary>
    public string Value { get; set; } = "on";

    public string Describe()
    {
        string days = Days switch { 0 or 0b1111111 => "every day", 0b0111110 => "on weekdays", 0b1000001 => "at weekends", _ => "on " + string.Join(", ", Enumerable.Range(0, 7).Where(i => (Days & (1 << i)) != 0).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[i])) };
        return Kind switch
        {
            "time" => $"At {Time} {days}",
            "startup" => "When V-OS starts",
            "app" => $"When {App} opens",
            "focus" => Value == "off" ? "When Focus turns off" : "When Focus turns on",
            "power" => Value == "battery" ? "When the PC switches to battery" : "When the PC is plugged in",
            "workspace" => $"When the {Value} workspace opens",
            _ => Kind,
        };
    }
}

/// <summary>One step: kind + value, translated to a V-OS command string the shell runs.</summary>
public sealed class FlowAction
{
    public string Kind { get; set; } = "open-app";
    public string Value { get; set; } = "";

    public static readonly (string kind, string label, string hint)[] Kinds =
    [
        ("open-app", "Open a V-OS app", "files, browser, music, weather, notes, time…"),
        ("launch", "Open a program or file", "Full path"),
        ("open-url", "Open a website", "https://…"),
        ("workspace", "Switch workspace", "personal, work, study, gaming, media, minimal"),
        ("theme", "Set theme", "dark or light"),
        ("profile", "Set performance profile", "Adaptive, Quality, Balanced, Performance, Minimal"),
        ("focus", "Turn Focus on or off", "on or off"),
        ("timer", "Start a timer", "Minutes"),
        ("volume", "Set volume", "0–100"),
        ("media", "Control media", "play, pause, next, previous"),
        ("notify", "Show a notification", "Text"),
    ];

    public string ToCommand() => Kind switch
    {
        "open-app" => "app:" + Value.Trim().ToLowerInvariant(),
        "launch" => "launch:" + Value.Trim().Trim('"'),
        "open-url" => "url:" + (Value.Contains("://", StringComparison.Ordinal) ? Value.Trim() : "https://" + Value.Trim()),
        "workspace" => "workspace:" + Value.Trim().ToLowerInvariant(),
        "theme" => "theme:" + (Value.Trim().ToLowerInvariant() == "light" ? "light" : "dark"),
        "profile" => "perf:" + Value.Trim(),
        "focus" => "focus:" + (Value.Trim().ToLowerInvariant() == "off" ? "off" : "on"),
        "timer" => "timer:" + (int)(double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Max(1, m) * 60 : 300) + ":Flow timer",
        "volume" => "volume:" + Math.Clamp(int.TryParse(Value, out var v) ? v : 50, 0, 100),
        "media" => "media:" + Value.Trim().ToLowerInvariant(),
        "notify" => "notify:" + Value.Trim(),
        _ => "",
    };

    public string Describe() => (Kinds.FirstOrDefault(k => k.kind == Kind).label ?? Kind) + (Value.Length > 0 ? ": " + Value : "");
}

public sealed class Flow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "New flow";
    public bool Enabled { get; set; } = true;
    public FlowTrigger Trigger { get; set; } = new();
    public List<FlowAction> Actions { get; set; } = [];
    public DateTime? LastRun { get; set; }

    /// <summary>Whether a time trigger is due at <paramref name="now"/> (at or after its time today, not yet run today, within 15 minutes).</summary>
    public bool IsDue(DateTime now)
    {
        if (!Enabled || Trigger.Kind != "time" || !TimeOnly.TryParseExact(Trigger.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return false;
        if (Trigger.Days != 0 && (Trigger.Days & (1 << (int)now.DayOfWeek)) == 0) return false;
        var today = now.Date + at.ToTimeSpan();
        return now >= today && now - today < TimeSpan.FromMinutes(15) && (LastRun == null || LastRun.Value < today);
    }

    public static List<Flow> Templates() =>
    [
        new() { Name = "Work mode on weekday mornings", Trigger = new FlowTrigger { Kind = "time", Time = "09:00", Days = 0b0111110 },
            Actions = [new() { Kind = "workspace", Value = "work" }, new() { Kind = "focus", Value = "on" }, new() { Kind = "open-app", Value = "notes" }] },
        new() { Name = "Calm evening", Trigger = new FlowTrigger { Kind = "time", Time = "21:30", Days = 0 },
            Actions = [new() { Kind = "theme", Value = "dark" }, new() { Kind = "volume", Value = "35" }, new() { Kind = "notify", Value = "Time to wind down." }] },
        new() { Name = "Gaming when Steam opens", Trigger = new FlowTrigger { Kind = "app", App = "steam.exe" },
            Actions = [new() { Kind = "workspace", Value = "gaming" }] },
        new() { Name = "Save battery when unplugged", Trigger = new FlowTrigger { Kind = "power", Value = "battery" },
            Actions = [new() { Kind = "profile", Value = "Performance" }, new() { Kind = "notify", Value = "Battery: lighter effects." }] },
        new() { Name = "Start the day", Trigger = new FlowTrigger { Kind = "startup" },
            Actions = [new() { Kind = "open-app", Value = "weather" }] },
    ];
}
