using System.Globalization;
using System.Text.Json;

namespace VOS.Core.Assistant;

/// <summary>How a validated assistant action is carried out.</summary>
public enum ActionKind
{
    /// <summary>A shell command string ("timer:300", "theme:dark"...) run by the V-OS shell.</summary>
    Shell,
    /// <summary>A weather lookup answered inside the assistant (Open-Meteo, the same source as the Weather app).</summary>
    Weather,
}

/// <summary>A tool call that passed validation. <see cref="Summary"/> is what the user sees in the transcript.</summary>
public sealed record AssistantAction(ActionKind Kind, string Command, string Summary)
{
    /// <summary>The shell asks the user before running it (sleep, restart, shut down, sign out).</summary>
    public bool NeedsConfirmation { get; init; }
    /// <summary>For <see cref="ActionKind.Weather"/>: the place asked for, or null for the user's own location.</summary>
    public string? City { get; init; }
}

/// <summary>A client tool offered to the model: name, description and JSON Schema properties.</summary>
public sealed record ToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required);

/// <summary>
/// The actions the assistant may take, as tool definitions plus strict validation of the model's input. Inputs arrive
/// with eager input streaming (no server-side validation), so every field is checked here before anything runs; an
/// input that does not fit the schema is never executed.
/// </summary>
public static class AssistantActions
{
    public static readonly string[] VosApps =
        ["files", "browser", "music", "weather", "notes", "time", "monitor", "capture", "store", "gaming", "translate", "connect", "flow", "settings"];
    public static readonly string[] SettingsSections =
        ["general", "appearance", "dock", "desktop", "performance", "notifications", "shortcuts", "weather", "clipboard", "apps", "workspaces", "privacy", "snapshots", "updates", "about"];
    private static readonly string[] Themes = ["dark", "light", "auto", "toggle"];
    private static readonly string[] MediaActions = ["play", "pause", "toggle", "next", "previous"];
    private static readonly string[] Profiles = ["adaptive", "quality", "balanced", "performance", "minimal"];
    private static readonly string[] PowerActions = ["lock", "sleep", "restart", "shutdown", "signout"];
    public const int MaxTimerSeconds = 24 * 3600;

    /// <summary>The tool definitions. Workspace names are listed so the model can pick a real one.</summary>
    public static IReadOnlyList<ToolSpec> Tools(IReadOnlyCollection<string> workspaces) =>
    [
        Tool("open_app", "Open an app. V-OS apps: " + string.Join(", ", VosApps) + ". Any other installed Windows app can be opened by its name, for example \"Spotify\" or \"Visual Studio Code\".",
            ["name"], ("name", Str("The V-OS app id or the installed app's name."))),
        Tool("start_timer", "Start a countdown timer shown in the V-OS top bar.",
            ["seconds"], ("seconds", Int($"Duration in seconds, 1 to {MaxTimerSeconds}.", 1, MaxTimerSeconds)), ("label", Str("Optional short label, such as \"Tea\"."))),
        Tool("set_theme", "Switch the V-OS colour theme.", ["mode"], ("mode", Enum("dark, light, auto (follow the time of day) or toggle.", Themes))),
        Tool("set_volume", "Set the system volume, or mute and unmute. Give level, mute, or both.",
            [], ("level", Int("Volume from 0 to 100.", 0, 100)), ("mute", Bool("true to mute, false to unmute."))),
        Tool("media_control", "Control whatever is playing (Music, a browser, Spotify...).", ["action"], ("action", Enum("play, pause, toggle, next or previous.", MediaActions))),
        Tool("set_focus", "Turn Focus (do not disturb) on or off.", ["on"], ("on", Bool("true to turn Focus on."))),
        Tool("switch_workspace", "Switch to another V-OS workspace (theme, wallpaper, dock and apps for a context). The user's workspaces: " + string.Join(", ", workspaces) + ".",
            ["name"], ("name", Str("The workspace name."))),
        Tool("set_performance_profile", "Change the V-OS performance profile. adaptive picks automatically; quality has full effects; balanced; performance reduces effects; minimal is lightest.",
            ["profile"], ("profile", Enum("The profile.", Profiles))),
        Tool("open_settings", "Open V-OS Settings, optionally at a page.", [], ("section", Enum("The page.", SettingsSections))),
        Tool("open_website", "Open a web page in the user's browser.", ["url"], ("url", Str("An absolute http or https URL."))),
        Tool("search_web", "Search the web with the user's search engine in their browser.", ["query"], ("query", Str("What to search for."))),
        Tool("create_note", "Create a note in the Notes app with the given text.", ["text"], ("text", Str("The note text (Markdown allowed)."))),
        Tool("get_weather", "Get current weather and a 3-day forecast. Without a city, uses the location set in the Weather app.",
            [], ("city", Str("Optional city or place name."))),
        Tool("power", "Lock the PC, put it to sleep, restart, shut down or sign out. V-OS asks the user to confirm everything except lock.",
            ["action"], ("action", Enum("lock, sleep, restart, shutdown or signout.", PowerActions))),
    ];

    /// <summary>
    /// Validates one tool call. Returns false with a message for the model when the input does not fit; such an input
    /// must not run. <paramref name="malformed"/> separates shape problems (missing or mistyped fields, which are
    /// reported like invalid JSON) from values that are well-formed but not usable (an unknown workspace).
    /// </summary>
    public static bool TryValidate(string tool, IReadOnlyDictionary<string, JsonElement>? input, IReadOnlyCollection<(string id, string name)> workspaces,
        out AssistantAction action, out string error, out bool malformed)
    {
        action = null!;
        error = "";
        malformed = true;
        var v = new Reader(input ?? new Dictionary<string, JsonElement>());
        switch (tool)
        {
            case "open_app":
                if (!v.String("name", 1, 80, out var app, ref error)) return false;
                action = new(ActionKind.Shell, "openapp:" + app, "Open " + Cap(app));
                return true;

            case "start_timer":
                if (!v.Int("seconds", 1, MaxTimerSeconds, out var secs, ref error)) return false;
                if (!v.OptionalString("label", 60, out var label, ref error)) return false;
                var words = Fmt.DurationWords(TimeSpan.FromSeconds(secs));
                action = new(ActionKind.Shell, string.Create(CultureInfo.InvariantCulture, $"timer:{secs}") + (label != null ? ":" + label : ""),
                    label != null ? $"Start a {words} timer “{label}”" : $"Start a {words} timer");
                return true;

            case "set_theme":
                if (!v.Enum("mode", Themes, out var mode, ref error)) return false;
                action = new(ActionKind.Shell, "theme:" + mode, mode switch { "toggle" => "Toggle the theme", "auto" => "Use the automatic theme", _ => $"Switch to the {mode} theme" });
                return true;

            case "set_volume":
                if (!v.OptionalInt("level", 0, 100, out var level, ref error)) return false;
                if (!v.OptionalBool("mute", out var mute, ref error)) return false;
                if (level == null && mute == null) { error = "Give level (0-100), mute (true or false), or both."; return false; }
                if (mute == true) action = new(ActionKind.Shell, "volume:mute", "Mute the sound");
                else if (level is int l) action = new(ActionKind.Shell, string.Create(CultureInfo.InvariantCulture, $"volume:{l}"), $"Set the volume to {l}%");
                else action = new(ActionKind.Shell, "volume:unmute", "Unmute the sound");
                return true;

            case "media_control":
                if (!v.Enum("action", MediaActions, out var media, ref error)) return false;
                action = new(ActionKind.Shell, "media:" + (media == "toggle" ? "playpause" : media),
                    media switch { "play" => "Play", "pause" => "Pause", "next" => "Next track", "previous" => "Previous track", _ => "Play or pause" });
                return true;

            case "set_focus":
                if (!v.Bool("on", out var on, ref error)) return false;
                action = new(ActionKind.Shell, on ? "focus:on" : "focus:off", on ? "Turn Focus on" : "Turn Focus off");
                return true;

            case "switch_workspace":
                if (!v.String("name", 1, 60, out var wsName, ref error)) return false;
                malformed = false;
                var ws = workspaces.FirstOrDefault(w => w.name.Equals(wsName, StringComparison.OrdinalIgnoreCase) || w.id.Equals(wsName, StringComparison.OrdinalIgnoreCase));
                if (ws.id == null) { error = $"There is no workspace named “{wsName}”. The user's workspaces: {string.Join(", ", workspaces.Select(w => w.name))}."; return false; }
                action = new(ActionKind.Shell, "workspace:" + ws.id, $"Switch to the {ws.name} workspace");
                return true;

            case "set_performance_profile":
                if (!v.Enum("profile", Profiles, out var profile, ref error)) return false;
                action = new(ActionKind.Shell, "perf:" + profile, $"Use the {Cap(profile)} performance profile");
                return true;

            case "open_settings":
                if (!v.OptionalEnum("section", SettingsSections, out var section, ref error)) return false;
                action = new(ActionKind.Shell, "settings:" + (section ?? ""), section == null ? "Open Settings" : $"Open Settings › {Cap(section)}");
                return true;

            case "open_website":
                if (!v.String("url", 4, 2048, out var url, ref error)) return false;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
                {
                    malformed = false;
                    error = "Only absolute http and https URLs can be opened.";
                    return false;
                }
                action = new(ActionKind.Shell, "url:" + uri.AbsoluteUri, "Open " + uri.Host);
                return true;

            case "search_web":
                if (!v.String("query", 1, 400, out var query, ref error)) return false;
                action = new(ActionKind.Shell, "search:" + query, $"Search the web for “{query}”");
                return true;

            case "create_note":
                if (!v.String("text", 1, 20_000, out var text, ref error, singleLine: false)) return false;
                action = new(ActionKind.Shell, "app:notes|new=1\u001Ftext=" + text, "Create a note");
                return true;

            case "get_weather":
                if (!v.OptionalString("city", 100, out var city, ref error)) return false;
                action = new(ActionKind.Weather, "", city != null ? $"Check the weather in {city}" : "Check the weather") { City = city };
                return true;

            case "power":
                if (!v.Enum("action", PowerActions, out var power, ref error)) return false;
                action = new(ActionKind.Shell, "system:" + power, power switch
                {
                    "lock" => "Lock this PC", "sleep" => "Put this PC to sleep", "restart" => "Restart this PC", "shutdown" => "Shut down this PC", _ => "Sign out",
                }) { NeedsConfirmation = power != "lock" };
                return true;
        }
        malformed = false;
        error = $"Unknown tool “{tool}”.";
        return false;
    }

    /// <summary>The error content returned for an input that does not fit the schema: the raw input, so the model can
    /// see and correct what it sent, plus the reason. Built with the JSON writer so quotes are escaped.</summary>
    public static string InvalidInputContent(IReadOnlyDictionary<string, JsonElement>? input, string reason) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["INVALID_JSON"] = input == null ? "" : JsonSerializer.Serialize(input),
            ["error"] = reason,
        });

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    private static ToolSpec Tool(string name, string description, string[] required, params (string name, JsonElement schema)[] props) =>
        new(name, description, props.ToDictionary(p => p.name, p => p.schema), required);

    private static JsonElement Str(string description) => JsonSerializer.SerializeToElement(new { type = "string", description });
    private static JsonElement Int(string description, int min, int max) => JsonSerializer.SerializeToElement(new { type = "integer", description, minimum = min, maximum = max });
    private static JsonElement Bool(string description) => JsonSerializer.SerializeToElement(new { type = "boolean", description });
    private static JsonElement Enum(string description, string[] values) => JsonSerializer.SerializeToElement(new { type = "string", description, @enum = values });

    /// <summary>Strict field reader. Accepts integers written as numeric strings (a common model slip), nothing looser.</summary>
    private readonly struct Reader(IReadOnlyDictionary<string, JsonElement> input)
    {
        public bool String(string key, int min, int max, out string value, ref string error, bool singleLine = true)
        {
            value = "";
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) { error = $"“{key}” is required."; return false; }
            if (e.ValueKind != JsonValueKind.String) { error = $"“{key}” must be a string."; return false; }
            var s = Clean(e.GetString()!, singleLine);
            if (s.Length < min) { error = $"“{key}” must not be empty."; return false; }
            if (s.Length > max) { error = $"“{key}” must be at most {max} characters."; return false; }
            value = s;
            return true;
        }

        public bool OptionalString(string key, int max, out string? value, ref string error)
        {
            value = null;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) return true;
            if (e.ValueKind != JsonValueKind.String) { error = $"“{key}” must be a string."; return false; }
            var s = Clean(e.GetString()!, singleLine: true);
            if (s.Length > max) { error = $"“{key}” must be at most {max} characters."; return false; }
            value = s.Length == 0 ? null : s;
            return true;
        }

        public bool Int(string key, int min, int max, out int value, ref string error)
        {
            value = 0;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) { error = $"“{key}” is required."; return false; }
            return ReadInt(key, e, min, max, out value, ref error);
        }

        public bool OptionalInt(string key, int min, int max, out int? value, ref string error)
        {
            value = null;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) return true;
            if (!ReadInt(key, e, min, max, out var i, ref error)) return false;
            value = i;
            return true;
        }

        public bool Bool(string key, out bool value, ref string error)
        {
            value = false;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) { error = $"“{key}” is required."; return false; }
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"“{key}” must be true or false."; return false; }
            value = e.GetBoolean();
            return true;
        }

        public bool OptionalBool(string key, out bool? value, ref string error)
        {
            value = null;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) return true;
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"“{key}” must be true or false."; return false; }
            value = e.GetBoolean();
            return true;
        }

        public bool Enum(string key, string[] allowed, out string value, ref string error)
        {
            value = "";
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) { error = $"“{key}” is required: one of {string.Join(", ", allowed)}."; return false; }
            return ReadEnum(key, e, allowed, out value, ref error);
        }

        public bool OptionalEnum(string key, string[] allowed, out string? value, ref string error)
        {
            value = null;
            if (!input.TryGetValue(key, out var e) || e.ValueKind == JsonValueKind.Null) return true;
            if (!ReadEnum(key, e, allowed, out var s, ref error)) return false;
            value = s;
            return true;
        }

        private static bool ReadEnum(string key, JsonElement e, string[] allowed, out string value, ref string error)
        {
            value = "";
            var s = e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim().ToLowerInvariant() : null;
            if (s == null || Array.IndexOf(allowed, s) < 0) { error = $"“{key}” must be one of {string.Join(", ", allowed)}."; return false; }
            value = s;
            return true;
        }

        private static bool ReadInt(string key, JsonElement e, int min, int max, out int value, ref string error)
        {
            value = 0;
            bool ok = e.ValueKind switch
            {
                JsonValueKind.Number => e.TryGetInt32(out value),
                JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
                _ => false,
            };
            if (!ok || value < min || value > max) { error = $"“{key}” must be a whole number from {min} to {max}."; return false; }
            return true;
        }

        /// <summary>Removes control characters (the shell's argument separator among them); keeps line breaks in long text.</summary>
        private static string Clean(string s, bool singleLine)
        {
            var chars = s.Where(c => !char.IsControl(c) || (!singleLine && c is '\n' or '\t')).ToArray();
            return new string(chars).Trim();
        }
    }
}
