using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VOS.Core.Updates;

/// <summary>Release manifest published next to each release package (vos-release.json).</summary>
public sealed class ReleaseManifest
{
    public string Product { get; set; } = AppInfo.ProductName;
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public DateTimeOffset Published { get; set; }
    public string Notes { get; set; } = "";
    public string MinimumWindowsBuild { get; set; } = "19045";
    public ReleasePackage Package { get; set; } = new();
    /// <summary>Base64 ECDSA P-256 / SHA-256 signature over <see cref="SigningPayload"/>.</summary>
    public string Signature { get; set; } = "";

    /// <summary>Canonical bytes that are signed. Field order is fixed; notes are covered by their hash.</summary>
    public byte[] SigningPayload()
    {
        var notesHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Notes ?? "")));
        var s = string.Join("\n",
            "VOS-RELEASE-V1", Product, Version, Channel,
            Published.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            MinimumWindowsBuild, Package.Url, Package.Size.ToString(CultureInfo.InvariantCulture), Package.Sha256.ToUpperInvariant(), notesHash);
        return Encoding.UTF8.GetBytes(s);
    }
}

public sealed class ReleasePackage
{
    /// <summary>Absolute URL, or a path relative to the manifest location.</summary>
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public static class ReleaseSigning
{
    public static string Sign(ReleaseManifest m, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.SignData(m.SigningPayload(), HashAlgorithmName.SHA256));
    }

    public static bool Verify(ReleaseManifest m, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(m.Signature) || string.IsNullOrWhiteSpace(publicKeyPem)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(m.SigningPayload(), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static string FileSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}

/// <summary>Semantic version comparison (major.minor.patch with optional pre-release; pre-releases sort before releases).</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Pre) : IComparable<SemVer>
{
    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V').Split('+')[0];
        var pre = "";
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        int P(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (i < parts.Length ? -1 : 0);
        int a = P(0), b = P(1), c = P(2);
        if (a < 0 || b < 0 || c < 0) return false;
        v = new SemVer(a, b, c, pre);
        return true;
    }

    public int CompareTo(SemVer o)
    {
        int c = Major.CompareTo(o.Major); if (c != 0) return c;
        c = Minor.CompareTo(o.Minor); if (c != 0) return c;
        c = Patch.CompareTo(o.Patch); if (c != 0) return c;
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (o.Pre.Length == 0) return -1;
        return string.CompareOrdinal(Pre, o.Pre);
    }

    public override string ToString() => Pre.Length > 0 ? $"{Major}.{Minor}.{Patch}-{Pre}" : $"{Major}.{Minor}.{Patch}";
}

public sealed record UpdateCheckResult(bool Available, ReleaseManifest? Manifest, string Message, Uri? PackageUri);

/// <summary>Checks the release feed and downloads verified packages. Never applies anything itself (the Launcher does).</summary>
public sealed class UpdateClient
{
    private static readonly HttpClient s_http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("V-OS-Updater", AppInfo.Version));
        return h;
    }

    public static string? ResolveFeed(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) ? configured : AppInfo.DefaultUpdateFeed;

    public async Task<UpdateCheckResult> CheckAsync(string? feedUrl, string currentVersion, string publicKeyPem, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return new(false, null, "No update source is configured.", null);
        string json;
        try
        {
            var uri = new Uri(feedUrl);
            json = uri.IsFile ? await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false)
                              : await s_http.GetStringAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            return new(false, null, "The update server could not be reached.", null);
        }
        var manifest = VosJson.TryDeserialize<ReleaseManifest>(json);
        if (manifest == null) return new(false, null, "The update information is not valid.", null);
        if (!ReleaseSigning.Verify(manifest, publicKeyPem)) return new(false, null, "The update signature is not valid. The update was rejected.", null);
        if (!SemVer.TryParse(manifest.Version, out var remote) || !SemVer.TryParse(currentVersion, out var local))
            return new(false, null, "The update version is not valid.", null);
        var pkg = new Uri(new Uri(feedUrl), manifest.Package.Url);
        return remote.CompareTo(local) > 0
            ? new(true, manifest, $"V-OS {manifest.Version} is available.", pkg)
            : new(false, manifest, "V-OS is up to date.", pkg);
    }

    /// <summary>Downloads the package to <paramref name="destination"/> and verifies size and SHA-256. Deletes it on mismatch.</summary>
    public async Task DownloadAsync(UpdateCheckResult check, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var m = check.Manifest ?? throw new InvalidOperationException("No manifest.");
        var uri = check.PackageUri ?? throw new InvalidOperationException("No package location.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (uri.IsFile)
        {
            File.Copy(uri.LocalPath, destination, true);
            progress?.Report(1);
        }
        else
        {
            using var resp = await s_http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? m.Package.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        var info = new FileInfo(destination);
        var hash = ReleaseSigning.FileSha256(destination);
        if (info.Length != m.Package.Size || !hash.Equals(m.Package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidDataException("The downloaded package failed integrity verification and was deleted.");
        }
    }
}
