using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VOS.Core.Updates;

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
        var manifest = ReleaseJson.TryParse(json);
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
