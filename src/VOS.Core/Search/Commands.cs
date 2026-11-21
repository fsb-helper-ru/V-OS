using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VOS.Core.Search;

public enum IntentKind
{
    None, OpenApp, OpenModule, Workspace, Theme, Timer, FindLatestDownload, FindFiles, System, Volume, Brightness,
    Focus, Media, Weather, Translate, Calculate, Convert, WebSearch, Settings, Note, Performance, Url, Path,
}

/// <summary>A parsed command. The shell maps it to an action; dangerous ones carry <see cref="RequiresConfirmation"/>.</summary>
public sealed record CommandIntent(IntentKind Kind, string Title, string Detail = "")
{
    public Dictionary<string, string> Args { get; init; } = [];
    public bool RequiresConfirmation { get; init; }
    public string Arg(string key) => Args.TryGetValue(key, out var v) ? v : "";
    public static CommandIntent None { get; } = new(IntentKind.None, "");
}

/// <summary>
/// Rule-based natural-language command parser (English, with common Russian synonyms). Deterministic, local, instant.
/// </summary>
public static partial class CommandParser
{
    private static readonly string[] Workspaces = ["personal", "work", "study", "gaming", "game", "media", "minimal"];

    public static CommandIntent Parse(string input)
    {
        var raw = input.Trim();
        if (raw.Length == 0) return CommandIntent.None;
        var s = Normalize(raw);

        if (UrlRegex().IsMatch(raw)) return new(IntentKind.Url, "Open " + raw, "Website") { Args = { ["url"] = raw.Contains("://") ? raw : "https://" + raw } };
        if (PathRegex().IsMatch(raw)) return new(IntentKind.Path, "Open " + raw, "Location") { Args = { ["path"] = raw } };

        if (TryCalculate(raw, out var value))
            return new(IntentKind.Calculate, Format(value), raw.TrimStart('=') + " =") { Args = { ["value"] = value.ToString("R", CultureInfo.InvariantCulture) } };

        if (UnitConverter.TryParseQuery(raw, out var conv))
            return new(IntentKind.Convert, conv.Result, conv.Query) { Args = { ["value"] = conv.Value.ToString("R", CultureInfo.InvariantCulture) } };

        if (TryTimer(s, out var span, out var label))
            return new(IntentKind.Timer, $"Start a {Fmt.DurationWords(span)} timer", string.IsNullOrEmpty(label) ? "Timer" : label)
            { Args = { ["seconds"] = ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture), ["label"] = label } };

        if (Has(s, "dark mode", "dark theme", "switch to dark", "go dark", "тёмная тема", "темная тема", "тёмный режим", "темный режим"))
            return new(IntentKind.Theme, "Switch to dark theme") { Args = { ["mode"] = "dark" } };
        if (Has(s, "light mode", "light theme", "switch to light", "светлая тема", "светлый режим"))
            return new(IntentKind.Theme, "Switch to light theme") { Args = { ["mode"] = "light" } };
        if (Has(s, "toggle theme", "switch theme", "сменить тему"))
            return new(IntentKind.Theme, "Toggle theme") { Args = { ["mode"] = "toggle" } };

        var ws = WorkspaceRegex().Match(s);
        if (ws.Success)
        {
            var name = ws.Groups["name"].Value;
            if (Workspaces.Contains(name)) return new(IntentKind.Workspace, $"Open {Cap(name == "game" ? "gaming" : name)} workspace") { Args = { ["name"] = name == "game" ? "gaming" : name } };
        }

        if (Has(s, "lock", "lock screen", "lock computer", "заблокировать", "блокировка") && s.Split(' ').Length <= 3)
            return new(IntentKind.System, "Lock this PC") { Args = { ["action"] = "lock" } };
        if (Exact(s, "sleep", "go to sleep", "сон", "спящий режим"))
            return new(IntentKind.System, "Put this PC to sleep") { Args = { ["action"] = "sleep" }, RequiresConfirmation = true };
        if (Exact(s, "restart", "reboot", "restart computer", "restart pc", "перезагрузка", "перезагрузить"))
            return new(IntentKind.System, "Restart this PC", "Open apps will be closed") { Args = { ["action"] = "restart" }, RequiresConfirmation = true };
        if (Exact(s, "shut down", "shutdown", "power off", "turn off", "turn off computer", "выключить", "выключение"))
            return new(IntentKind.System, "Shut down this PC", "Open apps will be closed") { Args = { ["action"] = "shutdown" }, RequiresConfirmation = true };
        if (Exact(s, "sign out", "log off", "log out", "logout", "выйти из системы"))
            return new(IntentKind.System, "Sign out", "Open apps will be closed") { Args = { ["action"] = "signout" }, RequiresConfirmation = true };
        if (Has(s, "empty recycle bin", "empty trash", "empty the trash", "empty bin", "очистить корзину"))
            return new(IntentKind.System, "Empty the Recycle Bin", "Deleted files cannot be restored afterwards") { Args = { ["action"] = "emptybin" }, RequiresConfirmation = true };

        var vol = VolumeRegex().Match(s);
        if (vol.Success) return new(IntentKind.Volume, $"Set volume to {vol.Groups["n"].Value}%") { Args = { ["level"] = vol.Groups["n"].Value } };
        if (Exact(s, "mute", "mute sound", "без звука", "выключи звук")) return new(IntentKind.Volume, "Mute sound") { Args = { ["mute"] = "true" } };
        if (Exact(s, "unmute", "unmute sound", "включи звук")) return new(IntentKind.Volume, "Unmute sound") { Args = { ["mute"] = "false" } };
        if (Exact(s, "volume up", "louder", "громче")) return new(IntentKind.Volume, "Volume up") { Args = { ["step"] = "10" } };
        if (Exact(s, "volume down", "quieter", "тише")) return new(IntentKind.Volume, "Volume down") { Args = { ["step"] = "-10" } };

        var bri = BrightnessRegex().Match(s);
        if (bri.Success) return new(IntentKind.Brightness, $"Set brightness to {bri.Groups["n"].Value}%") { Args = { ["level"] = bri.Groups["n"].Value } };

        if (Has(s, "focus on", "focus mode on", "turn on focus", "enable focus", "do not disturb", "не беспокоить", "фокус вкл"))
            return new(IntentKind.Focus, "Turn on Focus", "Only important notifications") { Args = { ["on"] = "true" } };
        if (Has(s, "focus off", "focus mode off", "turn off focus", "disable focus", "фокус выкл"))
            return new(IntentKind.Focus, "Turn off Focus") { Args = { ["on"] = "false" } };

        if (Exact(s, "play", "resume", "play music", "играть", "воспроизвести")) return new(IntentKind.Media, "Play") { Args = { ["action"] = "play" } };
        if (Exact(s, "pause", "pause music", "stop music", "пауза")) return new(IntentKind.Media, "Pause") { Args = { ["action"] = "pause" } };
        if (Exact(s, "next", "next track", "next song", "skip", "следующий трек", "дальше")) return new(IntentKind.Media, "Next track") { Args = { ["action"] = "next" } };
        if (Exact(s, "previous", "previous track", "previous song", "back", "предыдущий трек")) return new(IntentKind.Media, "Previous track") { Args = { ["action"] = "previous" } };

        var weather = WeatherRegex().Match(s);
        if (weather.Success)
        {
            var city = weather.Groups["city"].Value.Trim();
            return new(IntentKind.Weather, city.Length > 0 ? $"Weather in {Cap(city)}" : "Weather") { Args = { ["city"] = city } };
        }

        var tr = TranslateRegex().Match(raw);
        if (tr.Success)
            return new(IntentKind.Translate, $"Translate to {Cap(tr.Groups["lang"].Value)}", tr.Groups["text"].Value)
            { Args = { ["text"] = tr.Groups["text"].Value.Trim(' ', '"', '\''), ["lang"] = tr.Groups["lang"].Value } };

        if (LatestDownloadRegex().IsMatch(s)) return new(IntentKind.FindLatestDownload, "Show my latest download", "Downloads");

        if (Has(s, "system performance", "show performance", "task manager", "performance monitor", "диспетчер задач", "производительность системы"))
            return new(IntentKind.OpenModule, "Open Monitor", "System performance") { Args = { ["module"] = "monitor", ["section"] = "overview" } };
        if (Has(s, "network usage", "network activity", "сетевая активность", "использование сети"))
            return new(IntentKind.OpenModule, "Show network usage", "Monitor") { Args = { ["module"] = "monitor", ["section"] = "network" } };

        var perf = PerformanceRegex().Match(s);
        if (perf.Success) return new(IntentKind.Performance, $"Use {Cap(perf.Groups["p"].Value)} performance profile") { Args = { ["profile"] = perf.Groups["p"].Value } };

        var note = NoteRegex().Match(raw);
        if (note.Success) return new(IntentKind.Note, "Create note", note.Groups["text"].Value) { Args = { ["text"] = note.Groups["text"].Value.Trim() } };

        var web = WebRegex().Match(raw);
        if (web.Success) return new(IntentKind.WebSearch, "Search the web", web.Groups["q"].Value) { Args = { ["query"] = web.Groups["q"].Value.Trim() } };

        var set = SettingsRegex().Match(s);
        if (set.Success) return new(IntentKind.Settings, "Open Settings", set.Groups["section"].Value) { Args = { ["section"] = set.Groups["section"].Value.Trim() } };

        var find = FindRegex().Match(raw);
        if (find.Success) return new(IntentKind.FindFiles, $"Find “{find.Groups["q"].Value.Trim()}”", "Files") { Args = { ["query"] = find.Groups["q"].Value.Trim() } };

        var open = OpenRegex().Match(raw);
        if (open.Success) return new(IntentKind.OpenApp, "Open " + open.Groups["name"].Value.Trim()) { Args = { ["name"] = open.Groups["name"].Value.Trim() } };

        return CommandIntent.None;
    }

    private static bool TryCalculate(string raw, out double value)
    {
        value = 0;
        var t = raw.Trim();
        bool explicitCalc = t.StartsWith('=');
        if (explicitCalc) t = t[1..];
        if (!t.Any(char.IsDigit) && !t.Contains("pi", StringComparison.OrdinalIgnoreCase)) return false;
        bool looksLikeMath = explicitCalc || t.IndexOfAny(['+', '*', '/', '^', '%', '(', '×', '÷', '!']) >= 0 ||
                             Regex.IsMatch(t, @"\d\s*-\s*\d") || MathEvaluator.HasFunction(t);
        if (!looksLikeMath) return false;
        return MathEvaluator.TryEvaluate(t, out value);
    }

    private static bool TryTimer(string s, out TimeSpan span, out string label)
    {
        span = TimeSpan.Zero; label = "";
        var m = TimerRegex().Match(s);
        if (!m.Success) return false;
        span = ParseDuration(m.Groups["dur"].Value);
        label = m.Groups["label"].Success ? m.Groups["label"].Value.Trim() : "";
        return span > TimeSpan.Zero && span <= TimeSpan.FromHours(24);
    }

    public static TimeSpan ParseDuration(string text)
    {
        var total = TimeSpan.Zero;
        foreach (Match m in DurationPartRegex().Matches(text))
        {
            double n = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var u = m.Groups["u"].Value;
            total += u switch
            {
                var x when x.StartsWith('h') || x.StartsWith('ч') => TimeSpan.FromHours(n),
                var x when x.StartsWith('s') || x.StartsWith('с') => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMinutes(n),
            };
        }
        return total;
    }

    private static string Normalize(string s)
    {
        var lower = s.ToLower(CultureInfo.InvariantCulture).Replace('ё', 'е');
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower) sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '.' or ',' or '%' ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
    }

    private static bool Has(string s, params string[] phrases) => phrases.Any(p => s.Contains(p.Replace('ё', 'е'), StringComparison.Ordinal));
    private static bool Exact(string s, params string[] phrases) => phrases.Any(p => s == p.Replace('ё', 'е') || s == "please " + p);
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
    private static string Format(double v) => Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0)
        ? v.ToString("E6", CultureInfo.CurrentCulture)
        : Math.Round(v, 10).ToString("#,0.##########", CultureInfo.CurrentCulture);

    [GeneratedRegex(@"^(https?://\S+|www\.\S+\.\S+|[a-z0-9-]+\.(com|org|net|io|dev|app|ru|md|ro|de|uk|edu|gov)(/\S*)?)$", RegexOptions.IgnoreCase)] private static partial Regex UrlRegex();
    [GeneratedRegex(@"^([a-zA-Z]:\\|\\\\|%[a-z]+%)", RegexOptions.IgnoreCase)] private static partial Regex PathRegex();
    [GeneratedRegex(@"^(?:(?:please )?(?:start|set|create|begin|запусти|поставь|установи)\s+)?(?:a |an |the )?(?:timer|таймер)\s+(?:for |на )?(?<dur>(?:\d+(?:[.,]\d+)?\s*(?:hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|час(?:а|ов)?|ч|минут(?:а|ы|у)?|мин|м|секунд(?:а|ы|у)?|сек|с)\s*)+)(?:(?:for|called|named|label|для)\s+(?<label>.+))?$|^(?:remind me in|напомни через)\s+(?<dur>(?:\d+(?:[.,]\d+)?\s*(?:hours?|h|minutes?|mins?|m|seconds?|s|час(?:а|ов)?|минут(?:а|ы|у)?|мин|секунд(?:а|ы|у)?)\s*)+)(?:to |чтобы )?(?<label>.*)$")] private static partial Regex TimerRegex();
    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s|час(?:а|ов)?|ч|минут(?:а|ы|у)?|мин|м|секунд(?:а|ы|у)?|сек|с)")] private static partial Regex DurationPartRegex();
    [GeneratedRegex(@"^(?:(?:open|switch to|go to|start|use|launch|activate|открой|включи|переключи на)\s+)?(?:my |мой |моё |мое )?(?<name>[a-z]+)\s+(?:workspace|mode|space|рабочее пространство)$")] private static partial Regex WorkspaceRegex();
    [GeneratedRegex(@"^(?:set )?(?:volume|громкость)(?: to| на)?\s+(?<n>\d{1,3})\s*%?$")] private static partial Regex VolumeRegex();
    [GeneratedRegex(@"^(?:set )?(?:brightness|яркость)(?: to| на)?\s+(?<n>\d{1,3})\s*%?$")] private static partial Regex BrightnessRegex();
    [GeneratedRegex(@"^(?:what is the |what's the |show )?(?:weather|forecast|погода|прогноз)(?:\s+(?:in|for|at|в|во)\s+(?<city>.+))?(?:\s+(?:today|now|сейчас|сегодня))?$")] private static partial Regex WeatherRegex();
    [GeneratedRegex(@"^(?:translate|переведи)\s+(?<text>.+?)\s+(?:to|into|на)\s+(?<lang>[\p{L}]+)$", RegexOptions.IgnoreCase)] private static partial Regex TranslateRegex();
    [GeneratedRegex(@"(?:find|show|open|где|найди|покажи).{0,12}(?:latest|last|newest|recent|most recent|последн\w*)\s+(?:download\w*|загрузк\w*|скачанн\w*)")] private static partial Regex LatestDownloadRegex();
    [GeneratedRegex(@"^(?:use |switch to |set )?(?<p>adaptive|quality|balanced|performance|minimal)\s+(?:mode|profile|режим)$")] private static partial Regex PerformanceRegex();
    [GeneratedRegex(@"^(?:new note|note|take a note|заметка|новая заметка)[:\s]+(?<text>.+)$", RegexOptions.IgnoreCase)] private static partial Regex NoteRegex();
    [GeneratedRegex(@"^(?:search (?:the )?web(?: for)?|web search|google|bing|duckduckgo|ddg|найди в интернете|поиск в интернете)\s+(?<q>.+)$", RegexOptions.IgnoreCase)] private static partial Regex WebRegex();
    [GeneratedRegex(@"^(?:open )?(?:settings|preferences|настройки|параметры)(?:\s+(?:for |of |для )?(?<section>.*))?$")] private static partial Regex SettingsRegex();
    [GeneratedRegex(@"^(?:find|search for|search|where is|locate|найди|поиск|где)\s+(?:files? |file named |файл )?(?<q>.+)$", RegexOptions.IgnoreCase)] private static partial Regex FindRegex();
    [GeneratedRegex(@"^(?:open|launch|start|run|show|открой|запусти|покажи)\s+(?<name>.+)$", RegexOptions.IgnoreCase)] private static partial Regex OpenRegex();
}

/// <summary>Recursive-descent calculator: + - * / % ^, parentheses, constants and common functions.</summary>
public static class MathEvaluator
{
    private static readonly string[] Functions = ["sqrt", "abs", "sin", "cos", "tan", "asin", "acos", "atan", "ln", "log", "exp", "round", "floor", "ceil", "min", "max", "pow"];

    public static bool HasFunction(string s) => Functions.Any(f => s.Contains(f + "(", StringComparison.OrdinalIgnoreCase));

    public static bool TryEvaluate(string expression, out double value)
    {
        value = 0;
        try
        {
            var text = expression.Replace('×', '*').Replace('÷', '/').Replace(" ", "");
            if (!text.Any(char.IsLetter)) text = Regex.Replace(text, @"(\d),(\d)", "$1.$2"); // decimal comma
            var p = new Parser(text);
            value = p.ParseExpression();
            if (!p.AtEnd || double.IsNaN(value) || double.IsInfinity(value)) return false;
            return true;
        }
        catch { return false; }
    }

    private sealed class Parser(string s)
    {
        private int _i;
        public bool AtEnd => _i >= s.Length;
        private char Peek => _i < s.Length ? s[_i] : '\0';

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (Peek is '+' or '-') { char op = s[_i++]; double r = ParseTerm(); v = op == '+' ? v + r : v - r; }
            return v;
        }

        private double ParseTerm()
        {
            double v = ParsePower();
            while (Peek is '*' or '/' or '%')
            {
                char op = s[_i++];
                double r = ParsePower();
                v = op switch { '*' => v * r, '/' => r == 0 ? throw new DivideByZeroException() : v / r, _ => v % r };
            }
            return v;
        }

        private double ParsePower()
        {
            double b = ParseUnary();
            if (Peek == '^') { _i++; return Math.Pow(b, ParsePower()); }
            return b;
        }

        private double ParseUnary()
        {
            if (Peek == '-') { _i++; return -ParseUnary(); }
            if (Peek == '+') { _i++; return ParseUnary(); }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Peek == '(')
            {
                _i++;
                double v = ParseExpression();
                Expect(')');
                return Postfix(v);
            }
            if (char.IsDigit(Peek) || Peek == '.')
            {
                int start = _i;
                while (char.IsDigit(Peek) || Peek == '.') _i++;
                if (Peek is 'e' or 'E' && _i + 1 < s.Length && (char.IsDigit(s[_i + 1]) || s[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (char.IsDigit(Peek)) _i++;
                }
                return Postfix(double.Parse(s[start.._i], CultureInfo.InvariantCulture));
            }
            if (char.IsLetter(Peek))
            {
                int start = _i;
                while (char.IsLetter(Peek)) _i++;
                var name = s[start.._i].ToLowerInvariant();
                if (name == "pi" || name == "π") return Postfix(Math.PI);
                if (name == "e") return Postfix(Math.E);
                Expect('(');
                var args = new List<double> { ParseExpression() };
                while (Peek == ',') { _i++; args.Add(ParseExpression()); }
                Expect(')');
                double Arg(int k) => k < args.Count ? args[k] : throw new FormatException();
                return Postfix(name switch
                {
                    "sqrt" => Math.Sqrt(Arg(0)), "abs" => Math.Abs(Arg(0)),
                    "sin" => Math.Sin(Arg(0)), "cos" => Math.Cos(Arg(0)), "tan" => Math.Tan(Arg(0)),
                    "asin" => Math.Asin(Arg(0)), "acos" => Math.Acos(Arg(0)), "atan" => Math.Atan(Arg(0)),
                    "ln" => Math.Log(Arg(0)), "log" => args.Count > 1 ? Math.Log(Arg(0), Arg(1)) : Math.Log10(Arg(0)),
                    "exp" => Math.Exp(Arg(0)), "round" => Math.Round(Arg(0), args.Count > 1 ? (int)Arg(1) : 0, MidpointRounding.AwayFromZero),
                    "floor" => Math.Floor(Arg(0)), "ceil" => Math.Ceiling(Arg(0)),
                    "min" => args.Min(), "max" => args.Max(), "pow" => Math.Pow(Arg(0), Arg(1)),
                    _ => throw new FormatException(),
                });
            }
            throw new FormatException();
        }

        private double Postfix(double v)
        {
            if (Peek == '!')
            {
                _i++;
                if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException();
                double f = 1; for (int k = 2; k <= (int)v; k++) f *= k; return f;
            }
            return v;
        }

        private void Expect(char c) { if (Peek != c) throw new FormatException(); _i++; }
    }
}

/// <summary>Unit conversion for search queries such as "10 km to mi" or "72 f in c".</summary>
public static partial class UnitConverter
{
    public sealed record Conversion(string Query, string Result, double Value);

    private sealed record Unit(string Name, string Category, double Factor, double Offset = 0);

    private static readonly Dictionary<string, Unit> Units = Build();

    private static Dictionary<string, Unit> Build()
    {
        var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string name, double factor, params string[] aliases)
        {
            var u = new Unit(name, cat, factor);
            d[name] = u;
            foreach (var a in aliases) d[a] = u;
        }
        Add("length", "mm", 0.001, "millimeter", "millimeters", "millimetre", "millimetres", "мм");
        Add("length", "cm", 0.01, "centimeter", "centimeters", "centimetre", "centimetres", "см");
        Add("length", "m", 1, "meter", "meters", "metre", "metres", "м");
        Add("length", "km", 1000, "kilometer", "kilometers", "kilometre", "kilometres", "км");
        Add("length", "in", 0.0254, "inch", "inches", "\"", "дюйм", "дюймов");
        Add("length", "ft", 0.3048, "foot", "feet", "'", "фут", "футов");
        Add("length", "yd", 0.9144, "yard", "yards");
        Add("length", "mi", 1609.344, "mile", "miles", "миля", "мили", "миль", "милю");
        Add("length", "nmi", 1852, "nautical mile", "nautical miles");
        Add("mass", "mg", 1e-6, "milligram", "milligrams", "мг");
        Add("mass", "g", 0.001, "gram", "grams", "гр", "г");
        Add("mass", "kg", 1, "kilogram", "kilograms", "kilo", "kilos", "кг");
        Add("mass", "t", 1000, "ton", "tons", "tonne", "tonnes", "т");
        Add("mass", "oz", 0.028349523125, "ounce", "ounces");
        Add("mass", "lb", 0.45359237, "lbs", "pound", "pounds", "фунт", "фунтов");
        Add("mass", "st", 6.35029318, "stone", "stones");
        Add("volume", "ml", 0.001, "milliliter", "milliliters", "millilitre", "мл");
        Add("volume", "l", 1, "liter", "liters", "litre", "litres", "л");
        Add("volume", "gal", 3.785411784, "gallon", "gallons");
        Add("volume", "qt", 0.946352946, "quart", "quarts");
        Add("volume", "pt", 0.473176473, "pint", "pints");
        Add("volume", "cup", 0.2365882365, "cups");
        Add("volume", "floz", 0.0295735295625, "fl oz", "fluid ounce", "fluid ounces");
        Add("speed", "km/h", 1 / 3.6, "kmh", "kph", "км/ч");
        Add("speed", "mph", 0.44704);
        Add("speed", "m/s", 1, "mps", "м/с");
        Add("speed", "kn", 0.514444, "knot", "knots");
        Add("data", "B", 1, "byte", "bytes", "байт");
        Add("data", "KB", 1e3, "kilobyte", "kilobytes", "кб");
        Add("data", "MB", 1e6, "megabyte", "megabytes", "мб");
        Add("data", "GB", 1e9, "gigabyte", "gigabytes", "гб");
        Add("data", "TB", 1e12, "terabyte", "terabytes", "тб");
        Add("data", "KiB", 1024, "kibibyte");
        Add("data", "MiB", 1048576, "mebibyte");
        Add("data", "GiB", 1073741824, "gibibyte");
        Add("data", "TiB", 1099511627776, "tebibyte");
        Add("time", "ms", 0.001, "millisecond", "milliseconds", "мс");
        Add("time", "s", 1, "sec", "secs", "second", "seconds", "сек", "секунд");
        Add("time", "min", 60, "mins", "minute", "minutes", "мин", "минут");
        Add("time", "h", 3600, "hr", "hrs", "hour", "hours", "ч", "час", "часов");
        Add("time", "d", 86400, "day", "days", "дн", "дней");
        Add("time", "wk", 604800, "week", "weeks", "нед");
        Add("time", "yr", 31557600, "year", "years", "лет", "год");
        Add("area", "m²", 1, "m2", "sqm", "sq m", "м2", "м²");
        Add("area", "km²", 1e6, "km2", "sq km", "км2", "км²");
        Add("area", "ha", 1e4, "hectare", "hectares", "га");
        Add("area", "acre", 4046.8564224, "acres");
        Add("area", "ft²", 0.09290304, "ft2", "sqft", "sq ft");
        d["c"] = d["°c"] = d["celsius"] = d["цельсий"] = new Unit("°C", "temperature", 1);
        d["f"] = d["°f"] = d["fahrenheit"] = d["фаренгейт"] = new Unit("°F", "temperature", 1);
        d["k"] = d["kelvin"] = d["кельвин"] = new Unit("K", "temperature", 1);
        return d;
    }

    public static bool TryParseQuery(string query, out Conversion conversion)
    {
        conversion = null!;
        var m = QueryRegex().Match(query.Trim());
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
        if (!Units.TryGetValue(m.Groups["from"].Value.Trim(), out var from) || !Units.TryGetValue(m.Groups["to"].Value.Trim(), out var to)) return false;
        if (!TryConvert(n, from, to, out var result)) return false;
        string fmt(double v) => Math.Round(v, Math.Abs(v) >= 100 ? 2 : 4).ToString("#,0.####", CultureInfo.CurrentCulture);
        conversion = new Conversion($"{fmt(n)} {from.Name} in {to.Name}", $"{fmt(result)} {to.Name}", result);
        return true;
    }

    private static bool TryConvert(double v, Unit from, Unit to, out double result)
    {
        result = 0;
        if (from.Category != to.Category) return false;
        if (from.Category == "temperature")
        {
            double c = from.Name switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
            result = to.Name switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
            return true;
        }
        result = v * from.Factor / to.Factor;
        return true;
    }

    [GeneratedRegex(@"^(?<n>-?\d+(?:[.,]\d+)?)\s*(?<from>[\p{L}°/²'""]+(?:\s?[\p{L}²]+)?)\s+(?:to|in|into|в|во)\s+(?<to>[\p{L}°/²'""]+(?:\s?[\p{L}²]+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex QueryRegex();
}

/// <summary>Fuzzy ranking for launcher results: exact &gt; prefix &gt; word initials &gt; substring &gt; subsequence.</summary>
public static class Fuzzy
{
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(candidate)) return 0;
        var q = Fold(query.Trim());
        var c = Fold(candidate);
        if (c == q) return 1000;
        if (c.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(100, c.Length - q.Length);
        var words = c.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 820 - Math.Min(100, c.Length - q.Length);
        var initials = new string(words.Select(w => w[0]).ToArray());
        if (q.Length >= 2 && initials.StartsWith(q, StringComparison.Ordinal)) return 780;
        int idx = c.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 700 - Math.Min(200, idx * 4);
        int qi = 0, gaps = 0, last = -1;
        for (int ci = 0; ci < c.Length && qi < q.Length; ci++)
        {
            if (c[ci] == q[qi]) { if (last >= 0 && ci - last > 1) gaps++; last = ci; qi++; }
        }
        return qi == q.Length ? Math.Max(1, 400 - gaps * 25 - (c.Length - q.Length)) : 0;
    }

    private static string Fold(string s)
    {
        var normalized = s.ToLowerInvariant().Replace('ё', 'е').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }
}
