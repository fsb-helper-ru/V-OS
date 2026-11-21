using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// BCL-only on purpose: the V-Launcher bootstrapper compiles this file too, so both verify releases with the same code.
namespace VOS.Core.Updates;

/// <summary>Release manifest published next to each release package (vos-release.json).</summary>
public sealed class ReleaseManifest
{
    public string Product { get; set; } = "V-OS";
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


/// <summary>Reads and writes release manifests with source-generated JSON (works in trimmed and AOT builds).</summary>
public static class ReleaseJson
{
    public static ReleaseManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.ReleaseManifest); }
        catch (JsonException) { return null; }
    }

    public static string Write(ReleaseManifest manifest) => JsonSerializer.Serialize(manifest, ReleaseJsonContext.Default.ReleaseManifest);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ReleaseManifest))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
