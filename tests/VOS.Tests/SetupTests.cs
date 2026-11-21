using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VOS.Core.Security;
using VOS.Core.Setup;
using VOS.Core.Updates;
using Xunit;

namespace VOS.Tests;

/// <summary>The install layout shared by V-Launcher.exe and the launcher UI: staging, swap, rollback, state.</summary>
public class SetupTests
{
    private static InstallLayout NewLayout() => new(Path.Combine(TestEnv.Root, "install-" + Guid.NewGuid().ToString("N")[..8]));

    private static string Package(params (string path, string text)[] files)
    {
        var zip = TestEnv.NewFile("package.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (path, text) in files)
        {
            using var w = new StreamWriter(archive.CreateEntry(path).Open());
            w.Write(text);
        }
        return zip;
    }

    [Fact]
    public void Staging_swap_and_rollback_keep_one_previous_version()
    {
        var layout = NewLayout();
        layout.ExtractToStaging(Package(("VOS.exe", "v1"), ("sub/data.txt", "a")));
        layout.PromoteStaging();
        Assert.Equal("v1", File.ReadAllText(layout.AppExe));
        Assert.Equal("a", File.ReadAllText(Path.Combine(layout.App, "sub", "data.txt")));
        Assert.False(layout.HasRollback);

        layout.ExtractToStaging(Package(("VOS.exe", "v2")));
        layout.PromoteStaging();
        Assert.Equal("v2", File.ReadAllText(layout.AppExe));
        Assert.True(layout.HasRollback);
        Assert.False(Directory.Exists(layout.Staging));

        layout.ExtractToStaging(Package(("VOS.exe", "v3")));
        layout.PromoteStaging();
        Assert.Equal("v2", File.ReadAllText(Path.Combine(layout.Previous, "VOS.exe"))); // v1 is gone: one rollback copy

        Assert.True(layout.RollBack());
        Assert.Equal("v2", File.ReadAllText(layout.AppExe));
        Assert.False(layout.HasRollback);
        Assert.False(layout.RollBack());
    }

    [Fact]
    public void Packages_cannot_write_outside_staging_and_must_contain_the_app()
    {
        var layout = NewLayout();
        Assert.Throws<InvalidDataException>(() => layout.ExtractToStaging(Package(("VOS.exe", "x"), ("../evil.txt", "x"))));
        Assert.False(File.Exists(Path.Combine(layout.Root, "evil.txt")));
        Assert.Throws<InvalidDataException>(() => layout.ExtractToStaging(Package(("readme.txt", "no app"))));
        Assert.Throws<InvalidOperationException>(() => NewLayout().PromoteStaging());
    }

    [Fact]
    public void Install_state_round_trips_and_paths_are_rooted()
    {
        var layout = NewLayout();
        Assert.Null(layout.ReadState());
        var now = DateTimeOffset.Now;
        layout.WriteState(new InstallState { Version = "1.2.3", PreviousVersion = "1.2.2", InstalledAt = now, PackageFile = "V-OS-1.2.3-win-x64.zip", Registered = true });
        var s = layout.ReadState()!;
        Assert.Equal("1.2.3", s.Version);
        Assert.Equal("1.2.2", s.PreviousVersion);
        Assert.True(s.Registered);
        Assert.Contains("\"previousVersion\"", File.ReadAllText(layout.StatePath)); // camelCase like the rest of V-OS
        Assert.True(layout.IsActiveApp(layout.App + "\\"));
        Assert.False(layout.IsActiveApp(layout.Previous));
        Assert.EndsWith("V-Launcher.exe", layout.Bootstrapper);
    }

    [Fact]
    public void Release_manifest_round_trips_through_source_generated_json_and_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = new ReleaseManifest
        {
            Version = "2.0.0", Channel = "beta", Published = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), Notes = "Notes",
            Package = new ReleasePackage { Url = "V-OS-2.0.0-win-x64.zip", Size = 42, Sha256 = "AB" },
        };
        m.Signature = ReleaseSigning.Sign(m, key.ExportPkcs8PrivateKeyPem());
        var json = ReleaseJson.Write(m);
        Assert.Contains("\"package\"", json);
        var back = ReleaseJson.TryParse(json)!;
        Assert.True(ReleaseSigning.Verify(back, key.ExportSubjectPublicKeyInfoPem()));
        Assert.False(ReleaseSigning.Verify(back, ReleaseKey.PublicKeyPem)); // a test key is not the release key
        back.Package.Size = 43;
        Assert.False(ReleaseSigning.Verify(back, key.ExportSubjectPublicKeyInfoPem()));
        Assert.Null(ReleaseJson.TryParse("{ not json"));
    }

    [Fact]
    public void Diagnostics_exports_never_contain_secrets()
    {
        const string settings = """
        { "assistant": { "claudeApiKey": "dpapi:SECRET1", "ollamaModel": "llama3.2" },
          "translate": { "deepLKey": "SECRET2", "provider": "deepl" },
          "accounts": [ { "refreshToken": "SECRET3", "name": "x" } ], "weather": { "apiKey": null } }
        """;
        var redacted = Redaction.RedactJson(settings);
        Assert.DoesNotContain("SECRET", redacted);
        Assert.Contains("llama3.2", redacted);
        Assert.Contains("deepl", redacted);
        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal("(removed)", doc.RootElement.GetProperty("assistant").GetProperty("claudeApiKey").GetString());
        Assert.Equal("(settings could not be read)", Redaction.RedactJson("{ broken"));
    }
}
