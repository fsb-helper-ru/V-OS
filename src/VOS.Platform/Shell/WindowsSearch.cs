using System.Data.OleDb;
using VOS.Core;

namespace VOS.Platform.Shell;

public sealed record FileHit(string Path, string Name, DateTime Modified, bool IsFolder);

/// <summary>
/// Queries the Windows Search index (OLE DB provider Search.CollatorDSO): instant name search across indexed
/// locations without crawling the disk. Falls back to recent files when the service is unavailable.
/// </summary>
public static class WindowsSearch
{
    public static List<FileHit> Query(string text, int max = 12, CancellationToken ct = default)
    {
        var term = new string(text.Where(c => c != '\'' && c != '"' && c != '*' && !char.IsControl(c)).ToArray()).Trim();
        if (term.Length < 2) return [];
        var hits = new List<FileHit>();
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var contains = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
            var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.ItemType " +
                      $"FROM SystemIndex WHERE SCOPE='file:' AND CONTAINS(System.ItemNameDisplay, '{contains}') ORDER BY System.DateModified DESC";
            using var cmd = new OleDbCommand(sql, conn) { CommandTimeout = 3 };
            using var r = cmd.ExecuteReader();
            while (r != null && r.Read() && !ct.IsCancellationRequested)
            {
                var path = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrEmpty(path)) continue;
                var name = r.IsDBNull(1) ? Path.GetFileName(path) : r.GetString(1);
                var modified = r.IsDBNull(2) ? DateTime.MinValue : r.GetDateTime(2);
                var type = r.IsDBNull(3) ? "" : r.GetString(3);
                hits.Add(new FileHit(path, name, modified, type.Equals("Directory", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path)));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Windows Search unavailable: " + ex.Message);
            hits.AddRange(Recent(term, max));
        }
        return hits;
    }

    /// <summary>Recent documents from the user's Recent folder (shortcut names), filtered by a term.</summary>
    public static IEnumerable<FileHit> Recent(string term, int max)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles("*.lnk")
            .Where(f => f.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime).Take(max)
            .Select(f => new FileHit(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime, false));
    }
}
