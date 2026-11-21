namespace VOS.Core.Notifications;

public enum NotificationPriority { Low, Normal, High, Critical }

public sealed class NotificationAction
{
    public string Label { get; set; } = "";
    /// <summary>V-OS command string executed by the shell (see Commands), e.g. "app:files?path=C:\\Users".</summary>
    public string Command { get; set; } = "";
}

/// <summary>A notification raised by any V-OS module. The shell stores it, applies focus rules and shows it.</summary>
public sealed class VosNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppId { get; set; } = "vos";
    public string AppName { get; set; } = "V-OS";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Segoe Fluent Icons glyph (single character) or empty for the app icon.</summary>
    public string Glyph { get; set; } = "";
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string? Command { get; set; }
    public List<NotificationAction> Actions { get; set; } = [];
    public bool Silent { get; set; }
    public bool Read { get; set; }
}

public enum ActivityKind { Media, Timer, Download, Transfer, Recording, Capture, System, Custom }

/// <summary>
/// A live activity shown in the V-Pill (download progress, running timer, recording...).
/// Producers send updates through IPC; the shell arbitrates which one owns the capsule.
/// </summary>
public sealed class LiveActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppId { get; set; } = "vos";
    public ActivityKind Kind { get; set; } = ActivityKind.Custom;
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>0..1, or null for indeterminate.</summary>
    public double? Progress { get; set; }
    public string Glyph { get; set; } = "";
    public string? AccentHex { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? EndsAt { get; set; }
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public List<NotificationAction> Actions { get; set; } = [];
}
