// V-OS release tool, used by build/build.ps1.
//   release --app <dir> --out <dir> --version <v> --channel <c> --time <unix-seconds> --key <pem> [--notes <file>] [--min-build <n>]
// Writes V-OS-<v>-win-x64.zip (deterministic: fixed entry order and timestamps) and the signed vos-release.json, then
// verifies both with the public key compiled into V-OS. The private key is read from outside the repository.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VOS.Core.Updates;

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
if (args.Length == 0 || args[0] != "release") { Console.Error.WriteLine("usage: release --app <dir> --out <dir> --version <v> --channel <c> --time <unix> --key <pem> [--notes <file>]"); return 2; }
string Need(string k) => opts.TryGetValue(k, out var v) && v.Length > 0 ? v : throw new ArgumentException($"--{k} is required");

var app = Path.GetFullPath(Need("app"));
var outDir = Path.GetFullPath(Need("out"));
var version = Need("version");
var channel = opts.GetValueOrDefault("channel", "stable");
var time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Need("time"), System.Globalization.CultureInfo.InvariantCulture));
var keyPath = Need("key");
if (!SemVer.TryParse(version, out _)) throw new ArgumentException("Invalid version " + version);
if (!File.Exists(Path.Combine(app, "VOS.exe"))) throw new FileNotFoundException("VOS.exe not found in " + app);
if (!File.Exists(keyPath)) { Console.Error.WriteLine($"Signing key not found: {keyPath}. Releases must be signed (ADR-006)."); return 3; }

Directory.CreateDirectory(outDir);
var zipName = $"V-OS-{version}-win-x64.zip";
var zipPath = Path.Combine(outDir, zipName);
if (File.Exists(zipPath)) File.Delete(zipPath);

// Deterministic archive: ordinal path order, forward slashes, the commit time on every entry.
var files = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
    .Select(f => (full: f, rel: Path.GetRelativePath(app, f).Replace('\\', '/')))
    .OrderBy(f => f.rel, StringComparer.Ordinal).ToList();
using (var fs = new FileStream(zipPath, FileMode.CreateNew))
using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
{
    foreach (var (full, rel) in files)
    {
        var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
        entry.LastWriteTime = time;
        using var src = File.OpenRead(full);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }
}

string notes = $"V-OS {version}.";
if (opts.TryGetValue("notes", out var notesFile) && File.Exists(notesFile))
{
    var text = File.ReadAllText(notesFile);
    // A "## ... <version> ..." section of RELEASE_NOTES.md, or the whole file for a dedicated notes file.
    var m = Regex.Match(text, @"^##[^\n]*\b" + Regex.Escape(version) + @"\b[^\n]*\n(?<body>.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
    bool changelog = Path.GetFileName(notesFile).Equals("RELEASE_NOTES.md", StringComparison.OrdinalIgnoreCase);
    if (m.Success) notes = m.Groups["body"].Value.Trim();
    else if (!changelog) notes = text.Trim();
    else Console.WriteLine($"warning  RELEASE_NOTES.md has no section for {version}; the manifest says only \"{notes}\"");
}

var manifest = new ReleaseManifest
{
    Version = version,
    Channel = channel,
    Published = time,
    Notes = notes,
    MinimumWindowsBuild = opts.GetValueOrDefault("min-build", "19041"),
    Package = new ReleasePackage { Url = zipName, Size = new FileInfo(zipPath).Length, Sha256 = Sha(zipPath) },
};
manifest.Signature = ReleaseSigning.Sign(manifest, File.ReadAllText(keyPath));
var manifestPath = Path.Combine(outDir, "vos-release.json");
File.WriteAllText(manifestPath, ReleaseJson.Write(manifest), new UTF8Encoding(false));

// Verify what was written, the way an installed V-OS will.
var check = ReleaseJson.TryParse(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Manifest does not parse");
if (!ReleaseSigning.Verify(check, ReleaseKey.PublicKeyPem)) { Console.Error.WriteLine("Signature does not verify with the public key in ReleaseKey.cs (wrong private key?)."); return 4; }
if (!Sha(zipPath).Equals(check.Package.Sha256, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Package hash mismatch."); return 5; }
Console.WriteLine($"package  {zipName}  {check.Package.Size / 1048576.0:0.0} MB  {files.Count} files");
Console.WriteLine($"sha256   {check.Package.Sha256}");
Console.WriteLine($"manifest vos-release.json  signed, verified with the embedded public key");
return 0;

static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
