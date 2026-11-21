using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using VOS.Core;
using VOS.Core.Data;
using VOS.Core.Ipc;
using VOS.Core.Perf;
using VOS.Core.Search;
using VOS.Core.Security;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.Core.Updates;
using VOS.Core.Weather;
using Xunit;

namespace VOS.Tests;

internal static class TestEnv
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "vos-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        VosPaths.OverrideDataRoot(Root);
    }

    public static string NewFile(string name) => Path.Combine(Root, Guid.NewGuid().ToString("N")[..8] + "-" + name);
}

public class SettingsTests
{
    [Fact]
    public void Defaults_are_complete_and_sane()
    {
        var s = new SettingsStore(TestEnv.NewFile("settings.json")).Current;
        Assert.Equal(PerformanceProfile.Adaptive, s.Performance.Profile);
        Assert.NotEmpty(s.Dock.Pinned!);
        Assert.Equal(6, s.Workspaces.Count);
        Assert.NotNull(s.ActiveWorkspace);
        Assert.Contains(s.Hotkeys.Bindings, b => b.Action == "recovery");
    }

    [Fact]
    public void Update_persists_and_second_instance_sees_it()
    {
        var path = TestEnv.NewFile("settings.json");
        var a = new SettingsStore(path);
        a.Update(s => { s.Appearance.Theme = ThemeMode.Light; s.Dock.IconSize = 60; });
        var b = new SettingsStore(path);
        Assert.Equal(ThemeMode.Light, b.Current.Appearance.Theme);
        Assert.Equal(60, b.Current.Dock.IconSize);
    }

    [Fact]
    public void Concurrent_updates_from_two_instances_are_not_lost()
    {
        var path = TestEnv.NewFile("settings.json");
        var a = new SettingsStore(path);
        var b = new SettingsStore(path);
        a.Update(s => s.UserName = "Ana");
        b.Update(s => s.Dock.AutoHide = true); // b re-reads before writing
        var c = new SettingsStore(path).Current;
        Assert.Equal("Ana", c.UserName);
        Assert.True(c.Dock.AutoHide);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_backed_up()
    {
        var path = TestEnv.NewFile("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var s = new SettingsStore(path);
        Assert.Equal(PerformanceProfile.Adaptive, s.Current.Performance.Profile);
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!), f => f.StartsWith(path + ".corrupt-"));
    }

    [Fact]
    public void Normalize_clamps_and_repairs_hand_edited_values()
    {
        var path = TestEnv.NewFile("settings.json");
        File.WriteAllText(path, """{ "dock": { "iconSize": 500, "pinned": null }, "workspaces": [], "activeWorkspaceId": "nope" }""");
        var s = new SettingsStore(path).Current;
        Assert.Equal(80, s.Dock.IconSize);
        Assert.NotEmpty(s.Dock.Pinned!);
        Assert.Equal("personal", s.ActiveWorkspaceId);
    }

    [Fact]
    public void Atomic_write_round_trips_unicode()
    {
        var path = TestEnv.NewFile("atomic.txt");
        AtomicFile.WriteAllText(path, "Погода — 4°");
        AtomicFile.WriteAllText(path, "Привет, V-OS");
        Assert.Equal("Привет, V-OS", AtomicFile.TryReadAllText(path));
    }
}

public class QualityTests
{
    private static readonly HardwareSnapshot Reference = new()
    {
        LogicalCores = 8, RamGb = 16, GpuName = "NVIDIA GeForce GTX 970", GpuDedicatedMb = 4030, EffectsFast = true,
    };

    [Fact]
    public void Reference_machine_is_high_tier_and_adaptive_picks_quality()
    {
        var q = QualityResolver.Resolve(PerformanceProfile.Adaptive, Reference);
        Assert.Equal(HardwareTier.High, q.Tier);
        Assert.Equal(PerformanceProfile.Quality, q.Profile);
        Assert.True(q.Glass && q.Springs && q.AmbientAnimated);
    }

    [Fact]
    public void Adaptive_steps_down_on_battery_saver_and_remote_sessions()
    {
        Assert.Equal(PerformanceProfile.Balanced, QualityResolver.Resolve(PerformanceProfile.Adaptive, Reference with { OnBattery = true, HasBattery = true }).Profile);
        Assert.Equal(PerformanceProfile.Performance, QualityResolver.Resolve(PerformanceProfile.Adaptive, Reference with { BatterySaver = true }).Profile);
        Assert.Equal(PerformanceProfile.Minimal, QualityResolver.Resolve(PerformanceProfile.Adaptive, Reference with { RemoteSession = true }).Profile);
    }

    [Fact]
    public void Software_rendering_is_low_tier()
    {
        var q = QualityResolver.Resolve(PerformanceProfile.Adaptive, Reference with { GpuIsSoftware = true });
        Assert.Equal(HardwareTier.Low, q.Tier);
        Assert.False(q.Glass);
    }

    [Fact]
    public void Profiles_change_real_parameters()
    {
        var quality = QualityResolver.Resolve(PerformanceProfile.Quality, Reference);
        var minimal = QualityResolver.Resolve(PerformanceProfile.Minimal, Reference);
        Assert.True(quality.Glass && quality.DockMagnification && quality.AudioVisualizer);
        Assert.False(minimal.Glass || minimal.Mica || minimal.Ambient || minimal.LiveThumbnails);
        Assert.Equal(0, minimal.MotionScale);
        Assert.True(minimal.WeatherRefreshMinutes > quality.WeatherRefreshMinutes);
    }

    [Fact]
    public void Windows_preferences_and_overrides_are_respected()
    {
        var noAnim = QualityResolver.Resolve(PerformanceProfile.Quality, Reference with { WindowsAnimations = false });
        Assert.Equal(0, noAnim.MotionScale);
        var noTransparency = QualityResolver.Resolve(PerformanceProfile.Quality, Reference with { WindowsTransparency = false });
        Assert.False(noTransparency.Glass);
        var contrast = QualityResolver.Resolve(PerformanceProfile.Quality, Reference with { HighContrast = true });
        Assert.False(contrast.Glass || contrast.Lights || contrast.Ambient);
        var overridden = QualityResolver.Resolve(PerformanceProfile.Minimal, Reference, new PerformanceSettings { Backdrops = true });
        Assert.True(overridden.Glass);
    }
}

public class CommandParserTests
{
    [Theory]
    [InlineData("start a timer for 5 minutes", 300)]
    [InlineData("timer 1h 30m", 5400)]
    [InlineData("set timer for 90 seconds", 90)]
    [InlineData("таймер на 10 минут", 600)]
    [InlineData("remind me in 15 minutes to stretch", 900)]
    public void Timers(string input, int seconds)
    {
        var i = CommandParser.Parse(input);
        Assert.Equal(IntentKind.Timer, i.Kind);
        Assert.Equal(seconds.ToString(), i.Arg("seconds"));
    }

    [Theory]
    [InlineData("Switch to dark mode", IntentKind.Theme, "mode", "dark")]
    [InlineData("тёмная тема", IntentKind.Theme, "mode", "dark")]
    [InlineData("light theme", IntentKind.Theme, "mode", "light")]
    [InlineData("Open my gaming workspace", IntentKind.Workspace, "name", "gaming")]
    [InlineData("work mode", IntentKind.Workspace, "name", "work")]
    [InlineData("volume 30", IntentKind.Volume, "level", "30")]
    [InlineData("mute", IntentKind.Volume, "mute", "true")]
    [InlineData("brightness 70%", IntentKind.Brightness, "level", "70")]
    [InlineData("weather in Paris", IntentKind.Weather, "city", "paris")]
    [InlineData("погода в Кишинёве", IntentKind.Weather, "city", "кишиневе")]
    [InlineData("next track", IntentKind.Media, "action", "next")]
    [InlineData("focus on", IntentKind.Focus, "on", "true")]
    [InlineData("Open system performance", IntentKind.OpenModule, "module", "monitor")]
    [InlineData("show network usage", IntentKind.OpenModule, "section", "network")]
    [InlineData("balanced mode", IntentKind.Performance, "profile", "balanced")]
    [InlineData("open music", IntentKind.OpenApp, "name", "music")]
    [InlineData("google rust async", IntentKind.WebSearch, "query", "rust async")]
    [InlineData("note buy oat milk", IntentKind.Note, "text", "buy oat milk")]
    [InlineData("translate good morning to spanish", IntentKind.Translate, "lang", "spanish")]
    [InlineData("find report.pdf", IntentKind.FindFiles, "query", "report.pdf")]
    public void Intents(string input, IntentKind kind, string key, string value)
    {
        var i = CommandParser.Parse(input);
        Assert.Equal(kind, i.Kind);
        Assert.Equal(value, i.Arg(key));
    }

    [Theory]
    [InlineData("Find my latest downloaded file")]
    [InlineData("show my last download")]
    public void Latest_download(string input) => Assert.Equal(IntentKind.FindLatestDownload, CommandParser.Parse(input).Kind);

    [Theory]
    [InlineData("shut down")]
    [InlineData("restart")]
    [InlineData("empty recycle bin")]
    [InlineData("sign out")]
    public void Dangerous_actions_require_confirmation(string input)
    {
        var i = CommandParser.Parse(input);
        Assert.Equal(IntentKind.System, i.Kind);
        Assert.True(i.RequiresConfirmation);
    }

    [Fact]
    public void Lock_does_not_require_confirmation()
    {
        var i = CommandParser.Parse("lock");
        Assert.Equal(IntentKind.System, i.Kind);
        Assert.False(i.RequiresConfirmation);
    }

    [Theory]
    [InlineData("2+2*3", 8)]
    [InlineData("=(2+2)*3", 12)]
    [InlineData("2^10", 1024)]
    [InlineData("sqrt(144)+1", 13)]
    [InlineData("3,5*2", 7)]
    [InlineData("5!", 120)]
    [InlineData("max(3, 9, 4)", 9)]
    public void Calculator(string input, double expected)
    {
        var i = CommandParser.Parse(input);
        Assert.Equal(IntentKind.Calculate, i.Kind);
        Assert.Equal(expected, double.Parse(i.Arg("value"), System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("2024")]
    [InlineData("1/0")]
    public void Not_a_calculation(string input) => Assert.NotEqual(IntentKind.Calculate, CommandParser.Parse(input).Kind);

    [Theory]
    [InlineData("10 km to mi", 6.2137, 0.001)]
    [InlineData("72 f in c", 22.2222, 0.001)]
    [InlineData("1 GB in MB", 1000, 0.0001)]
    [InlineData("3 ft to cm", 91.44, 0.001)]
    [InlineData("100 км в мили", 62.137, 0.01)]
    public void Conversions(string input, double expected, double tolerance)
    {
        var i = CommandParser.Parse(input);
        Assert.Equal(IntentKind.Convert, i.Kind);
        Assert.InRange(double.Parse(i.Arg("value"), System.Globalization.CultureInfo.InvariantCulture), expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void Conversion_between_categories_is_rejected() => Assert.False(UnitConverter.TryParseQuery("10 kg to km", out _));

    [Fact]
    public void Urls_and_paths()
    {
        Assert.Equal(IntentKind.Url, CommandParser.Parse("github.com").Kind);
        Assert.Equal("https://github.com", CommandParser.Parse("github.com").Arg("url"));
        Assert.Equal(IntentKind.Path, CommandParser.Parse(@"C:\Users").Kind);
    }

    [Fact]
    public void Fuzzy_prefers_prefix_over_subsequence()
    {
        Assert.True(Fuzzy.Score("mus", "Music") > Fuzzy.Score("mus", "Microsoft Update Service"));
        Assert.True(Fuzzy.Score("vs", "Visual Studio") > 0);
        Assert.Equal(0, Fuzzy.Score("xyz", "Files"));
        Assert.True(Fuzzy.Score("наст", "Настройки") >= 850); // prefix match (diacritics folded)
    }
}

public class WeatherTests
{
    private const string Sample = """
    {"latitude":47.0,"longitude":28.8,"utc_offset_seconds":7200,"timezone":"Europe/Chisinau",
     "current":{"time":"2026-11-21T19:00","temperature_2m":4.2,"relative_humidity_2m":81,"apparent_temperature":1.1,"is_day":0,
       "precipitation":0.0,"weather_code":3,"cloud_cover":96,"pressure_msl":1019.4,"wind_speed_10m":12.5,"wind_direction_10m":290,"wind_gusts_10m":25.0,"uv_index":0.0},
     "hourly":{"time":["2026-11-21T19:00","2026-11-21T20:00"],"temperature_2m":[4.2,3.9],"precipitation_probability":[10,85],"precipitation":[0,1.2],
       "weather_code":[3,95],"wind_speed_10m":[12.5,14.0],"wind_gusts_10m":[25,75],"is_day":[0,0],"uv_index":[0,0]},
     "daily":{"time":["2026-11-21"],"weather_code":[63],"temperature_2m_max":[7.1],"temperature_2m_min":[2.0],
       "sunrise":["2026-11-21T07:05"],"sunset":["2026-11-21T16:30"],"uv_index_max":[1.2],"precipitation_sum":[24.0],
       "precipitation_probability_max":[90],"wind_speed_10m_max":[20.0],"wind_gusts_10m_max":[75.0]}}
    """;

    [Fact]
    public void Parses_open_meteo_payload()
    {
        using var doc = JsonDocument.Parse(Sample);
        var r = OpenMeteoClient.Parse(doc.RootElement, new WeatherLocation { Name = "Chișinău" });
        Assert.Equal(4.2, r.Now.TemperatureC);
        Assert.False(r.Now.IsDay);
        Assert.Equal(Condition.Overcast, r.Now.Condition);
        Assert.Equal(TimeSpan.FromHours(2), r.Now.Time.Offset);
        Assert.Equal(2, r.Hourly.Count);
        Assert.Equal(16, r.Daily[0].Sunset.Hour);
    }

    [Fact]
    public void Derives_unofficial_advisories()
    {
        using var doc = JsonDocument.Parse(Sample);
        var r = OpenMeteoClient.Parse(doc.RootElement, new WeatherLocation());
        var titles = r.Advisories().Select(a => a.Title).ToList();
        Assert.Contains("Thunderstorms expected", titles);
        Assert.Contains("Strong wind gusts", titles);
        Assert.Contains("Heavy rain likely", titles);
    }

    [Fact]
    public void Formats_units()
    {
        Assert.Equal(212, WeatherFormat.Temp(100, TemperatureUnit.Fahrenheit), 6);
        Assert.Equal("NW", WeatherFormat.Compass(315));
        Assert.Equal("10 m/s", WeatherFormat.Wind(36, WindUnit.Ms));
    }
}

public class UpdateTests
{
    private static (string priv, string pub) NewKey()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k.ExportPkcs8PrivateKeyPem(), k.ExportSubjectPublicKeyInfoPem());
    }

    private static ReleaseManifest Manifest() => new()
    {
        Version = "1.0.1", Published = new DateTimeOffset(2026, 11, 21, 18, 0, 0, TimeSpan.Zero), Notes = "Fixes",
        Package = new ReleasePackage { Url = "V-OS-1.0.1.zip", Size = 1234, Sha256 = "ABCDEF" },
    };

    [Fact]
    public void Signed_manifest_verifies_and_tampering_is_detected()
    {
        var (priv, pub) = NewKey();
        var m = Manifest();
        m.Signature = ReleaseSigning.Sign(m, priv);
        Assert.True(ReleaseSigning.Verify(m, pub));
        m.Package.Sha256 = "000000";
        Assert.False(ReleaseSigning.Verify(m, pub));
    }

    [Fact]
    public void Signature_from_another_key_is_rejected()
    {
        var (priv, _) = NewKey();
        var (_, otherPub) = NewKey();
        var m = Manifest();
        m.Signature = ReleaseSigning.Sign(m, priv);
        Assert.False(ReleaseSigning.Verify(m, otherPub));
        Assert.False(ReleaseSigning.Verify(new ReleaseManifest(), ReleaseKey.PublicKeyPem));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("v2.0", "1.9.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    public void Semver_ordering(string a, string b, int sign)
    {
        Assert.True(SemVer.TryParse(a, out var va));
        Assert.True(SemVer.TryParse(b, out var vb));
        Assert.Equal(sign, Math.Sign(va.CompareTo(vb)));
    }

    [Fact]
    public async Task Local_feed_update_check_and_verified_download()
    {
        var (priv, pub) = NewKey();
        var dir = Path.Combine(TestEnv.Root, "feed-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var pkg = Path.Combine(dir, "V-OS-9.9.9.zip");
        File.WriteAllBytes(pkg, RandomNumberGenerator.GetBytes(4096));
        var m = new ReleaseManifest
        {
            Version = "9.9.9", Published = DateTimeOffset.UtcNow,
            Package = new ReleasePackage { Url = "V-OS-9.9.9.zip", Size = 4096, Sha256 = ReleaseSigning.FileSha256(pkg) },
        };
        m.Signature = ReleaseSigning.Sign(m, priv);
        var feed = Path.Combine(dir, "vos-release.json");
        File.WriteAllText(feed, VosJson.Serialize(m));

        var client = new UpdateClient();
        var check = await client.CheckAsync(new Uri(feed).AbsoluteUri, "1.0.0", pub);
        Assert.True(check.Available);
        var dest = Path.Combine(dir, "download.zip");
        await client.DownloadAsync(check, dest);
        Assert.True(File.Exists(dest));

        var rejected = await client.CheckAsync(new Uri(feed).AbsoluteUri, "1.0.0", NewKey().pub);
        Assert.False(rejected.Available);
        Assert.Contains("signature", rejected.Message);
    }
}

public class TimerTests
{
    [Fact]
    public void Lifecycle()
    {
        var store = new TimerStore(TestEnv.NewFile("timers.json"));
        var t = store.Start(TimeSpan.FromMinutes(5), "Tea");
        Assert.Single(store.List());
        store.Pause(t.Id);
        var paused = store.List()[0];
        Assert.Equal(TimerState.Paused, paused.State);
        Assert.InRange(paused.RemainingSeconds, 298, 300);
        store.Resume(t.Id);
        Assert.Equal(TimerState.Running, store.List()[0].State);
        store.AddTime(t.Id, TimeSpan.FromMinutes(1));
        Assert.Equal(360, store.List()[0].DurationSeconds);
        store.Cancel(t.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Finished_timers_are_reported_once()
    {
        var store = new TimerStore(TestEnv.NewFile("timers.json"));
        store.Start(TimeSpan.FromMilliseconds(10), "Quick");
        Thread.Sleep(50);
        Assert.Single(store.CollectFinished());
        Assert.Empty(store.CollectFinished());
    }
}

public class PlatformNeutralServiceTests
{
    [Fact]
    public void Dpapi_round_trip()
    {
        var p = Secrets.Protect("sk-test-123");
        Assert.True(Secrets.IsProtected(p));
        Assert.DoesNotContain("sk-test", p);
        Assert.Equal("sk-test-123", Secrets.Unprotect(p));
        Assert.Null(Secrets.Unprotect("dpapi:not-base64!"));
    }

    [Fact]
    public void Sqlite_migrations_and_queries()
    {
        var db = new Db(TestEnv.NewFile("t.db"));
        db.Migrate("CREATE TABLE notes(id TEXT PRIMARY KEY, title TEXT);", "ALTER TABLE notes ADD COLUMN pinned INTEGER DEFAULT 0;");
        db.Migrate("CREATE TABLE notes(id TEXT PRIMARY KEY, title TEXT);", "ALTER TABLE notes ADD COLUMN pinned INTEGER DEFAULT 0;"); // idempotent
        db.Execute("INSERT INTO notes(id,title,pinned) VALUES($i,$t,1)", ("$i", "a"), ("$t", "Hello"));
        var rows = db.Query("SELECT title, pinned FROM notes", r => (Db.Str(r, "title"), Db.Bool(r, "pinned")));
        Assert.Equal(("Hello", true), rows.Single());
        Assert.Equal(2L, db.Scalar<long>("PRAGMA user_version;"));
    }

    [Fact]
    public async Task Ipc_round_trip_is_current_user_only()
    {
        var name = "VOS.Test." + Guid.NewGuid().ToString("N")[..8];
        using var server = new IpcServer(name, m => Task.FromResult<IpcMessage?>(m.Type == "ping" ? IpcMessage.Create("pong") : null));
        server.Start();
        IpcMessage? reply = null;
        for (int i = 0; i < 20 && reply == null; i++)
        {
            reply = await IpcClient.SendAsync(name, IpcMessage.Create("ping"), expectReply: true, 500);
            if (reply == null) await Task.Delay(50);
        }
        Assert.Equal("pong", reply?.Type);
    }

    [Fact]
    public void Snapshots_create_list_restore()
    {
        var store = SettingsStore.Instance;
        store.Update(s => s.UserName = "Before");
        var svc = new SnapshotService(Path.Combine(TestEnv.Root, "snaps-" + Guid.NewGuid().ToString("N")[..6]));
        var snap = svc.Create("Test point");
        store.Update(s => s.UserName = "After");
        svc.Restore(snap.Id, store);
        Assert.Equal("Before", store.Current.UserName);
        Assert.True(svc.List().Count >= 2); // includes the automatic "Before restoring" snapshot
    }

    [Fact]
    public void Process_diagnostics_report_sane_values()
    {
        var d = ProcessDiagnostics.Capture();
        Assert.True(d.PrivateBytes > d.ManagedHeap && d.ManagedHeap > 0);
        Assert.True(d.Threads > 0 && d.Handles > 0 && d.UptimeSeconds > 0);
        var json = VosJson.Serialize(d);
        Assert.Contains("\"privateBytes\"", json);
    }

    [Fact]
    public void Default_hotkeys_offer_parseable_alternatives()
    {
        // Common chords are often taken by other apps (RegisterHotKey fails with 1409), so the important actions ship
        // with "|"-separated fallbacks. Every alternative must be a valid gesture and no chord may repeat.
        var bindings = new HotkeySettings().Bindings;
        var all = bindings.SelectMany(b => b.Gesture.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        Assert.All(all, g => Assert.True(VOS.Platform.Input.Gesture.TryParse(g, out _), $"'{g}' does not parse"));
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(bindings, b => b.Action == "command" && b.Gesture.Contains('|'));
    }
}
