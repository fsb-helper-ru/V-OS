using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VOS.Core;

namespace VOS.Platform.Sys;

public sealed record Game(string Key, string Name, string Source, string LaunchUri, string? InstallDir, string? Exe, string? Cover);

/// <summary>
/// Finds installed games from the launchers' own records (read-only): Steam (libraryfolders.vdf + appmanifest_*.acf,
/// covers from Steam's local cache), Epic (manifest .item files) and GOG (registry). Games start through their
/// launcher, so DRM, overlays and cloud saves behave exactly as usual.
/// </summary>
public static partial class GameLibrary
{
    private static readonly HashSet<string> s_steamTools = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100", "1826330", "1161040"];

    public static List<Game> Scan()
    {
        var list = new List<Game>();
        try { list.AddRange(Steam()); } catch (Exception ex) { Log.Debug("Steam library unreadable", ex.Message); }
        try { list.AddRange(Epic()); } catch (Exception ex) { Log.Debug("Epic library unreadable", ex.Message); }
        try { list.AddRange(Gog()); } catch (Exception ex) { Log.Debug("GOG library unreadable", ex.Message); }
        return list.GroupBy(g => g.Key).Select(g => g.First()).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string? SteamPath()
    {
        var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return p != null && Directory.Exists(p) ? Path.GetFullPath(p) : null;
    }

    private static IEnumerable<Game> Steam()
    {
        var steam = SteamPath();
        if (steam == null) yield break;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                string? Field(string name) => Regex.Match(text, "\"" + name + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } x ? x.Groups[1].Value : null;
                var id = Field("appid");
                var name = Field("name");
                if (id == null || name == null || s_steamTools.Contains(id) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.Ordinal) || name.StartsWith("Steam Linux Runtime", StringComparison.Ordinal)) continue;
                var dir = Field("installdir") is { } d ? Path.Combine(apps, "common", d) : null;
                yield return new Game("steam:" + id, name, "Steam", "steam://rungameid/" + id, dir, null, SteamCover(steam, id));
            }
        }
    }

    private static string? SteamCover(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        foreach (var candidate in new[] { Path.Combine(cache, $"{id}_library_600x900.jpg"), Path.Combine(cache, id, "library_600x900.jpg") })
            if (File.Exists(candidate)) return candidate;
        var folder = Path.Combine(cache, id);
        if (Directory.Exists(folder))
            return Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(folder, "*.jpg", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).StartsWith("library", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static IEnumerable<Game> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(item)).RootElement; } catch { continue; }
            string S(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("DisplayName");
            var app = S("AppName");
            if (name.Length == 0 || app.Length == 0) continue;
            if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array && !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
            var install = S("InstallLocation");
            var exe = S("LaunchExecutable") is { Length: > 0 } e && install.Length > 0 ? Path.Combine(install, e) : null;
            var uri = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(S("CatalogNamespace"))}%3A{Uri.EscapeDataString(S("CatalogItemId"))}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
            yield return new Game("epic:" + app, name, "Epic Games", uri, install.Length > 0 ? install : null, exe, null);
        }
    }

    private static IEnumerable<Game> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("gameName") is not string name) continue;
            var exe = k.GetValue("exe") as string;
            var path = k.GetValue("path") as string;
            if (exe == null || !File.Exists(exe)) continue;
            yield return new Game("gog:" + id, name, "GOG", exe, path, exe, null);
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)] private static partial Regex VdfPath();
}
