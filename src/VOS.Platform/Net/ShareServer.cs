using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VOS.Core;
using VOS.Platform.Shell;

namespace VOS.Platform.Net;

/// <summary>
/// A tiny one-session web server for moving files between this PC and a phone on the same network. It listens on
/// the private LAN address only, every URL carries a random 128-bit token, it serves only the files you shared,
/// uploads go to Downloads\V-OS Connect, and it stops when Connect closes or after 30 idle minutes.
/// </summary>
public sealed class ShareServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public IPAddress Address { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url => $"http://{Address}:{Port}/{Token}/";
    public List<string> Shared { get; } = [];
    public string Inbox { get; }
    public DateTime LastActivity { get; private set; } = DateTime.Now;
    public event Action<string>? Received;
    public event Action<string>? TextReceived;
    private const long MaxUpload = 4L * 1024 * 1024 * 1024;

    public ShareServer(IPAddress address, string? inbox = null)
    {
        Address = address;
        Inbox = Directory.CreateDirectory(inbox ?? Path.Combine(ShellNative.Downloads, "V-OS Connect")).FullName;
        _listener = new TcpListener(address, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    /// <summary>The PC's address on a private network (Wi-Fi or Ethernet), or null when there is none.</summary>
    public static IPAddress? PrivateLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;
            foreach (var a in props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                var b = a.GetAddressBytes();
                if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)) return a;
            }
        }
        return null;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 30000;
                var stream = client.GetStream();
                var (method, path, headers, leftover) = await ReadHeadAsync(stream);
                if (method == null) return;
                LastActivity = DateTime.Now;
                var prefix = "/" + Token + "/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await RespondAsync(stream, 404, "text/plain", "Not found"u8.ToArray()); return; }
                var route = path[prefix.Length..];
                if (method == "GET" && route.Length == 0) await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(null)));
                else if (method == "GET" && route.StartsWith("f/", StringComparison.Ordinal) && int.TryParse(route[2..], out var i) && i >= 0 && i < Shared.Count) await SendFileAsync(stream, Shared[i]);
                else if (method == "POST" && route == "upload") await ReceiveAsync(stream, headers, leftover);
                else if (method == "POST" && route == "text") await ReceiveTextAsync(stream, headers, leftover);
                else await RespondAsync(stream, 404, "text/plain", "Not found"u8.ToArray());
            }
            catch (Exception ex) { Log.Debug("Connect request failed", ex.Message); }
        }
    }

    private static async Task<(string? method, string path, Dictionary<string, string> headers, byte[] leftover)> ReadHeadAsync(NetworkStream s)
    {
        var buf = new byte[16384];
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read));
            if (n == 0) break;
            read += n;
            int end = IndexOf(buf, read, "\r\n\r\n"u8);
            if (end < 0) continue;
            var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = head[0].Split(' ');
            if (first.Length < 2) return (null, "", [], []);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in head.Skip(1)) { int c = h.IndexOf(':'); if (c > 0) headers[h[..c].Trim()] = h[(c + 1)..].Trim(); }
            return (first[0], Uri.UnescapeDataString(first[1].Split('?')[0]), headers, buf[(end + 4)..read]);
        }
        return (null, "", [], []);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) => data.AsSpan(0, length).IndexOf(pattern);

    private static async Task RespondAsync(NetworkStream s, int code, string type, byte[] body, string? extra = null)
    {
        var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 404 ? "Not Found" : code == 413 ? "Payload Too Large" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n{extra}\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    private async Task SendFileAsync(NetworkStream s, string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) { await RespondAsync(s, 404, "text/plain", "Gone"u8.ToArray()); return; }
        var name = Uri.EscapeDataString(fi.Name);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fi.Length}\r\nContent-Disposition: attachment; filename*=UTF-8''{name}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await using var f = fi.OpenRead();
        await f.CopyToAsync(s);
    }

    /// <summary>Streams a multipart/form-data upload to disk, one file at a time (never buffered whole in memory).</summary>
    private async Task ReceiveAsync(NetworkStream s, Dictionary<string, string> headers, byte[] leftover)
    {
        if (!headers.TryGetValue("Content-Type", out var ct) || !ct.Contains("boundary=", StringComparison.OrdinalIgnoreCase)) { await RespondAsync(s, 400, "text/plain", "Bad request"u8.ToArray()); return; }
        if (headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var len) && len > MaxUpload) { await RespondAsync(s, 413, "text/plain", "Too large"u8.ToArray()); return; }
        var boundary = Encoding.ASCII.GetBytes("\r\n--" + ct[(ct.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase) + 9)..].Trim('"'));
        var reader = new BufferedReader(s, leftover);
        var saved = new List<string>();
        // The body starts with "--boundary\r\n" (without the leading CRLF).
        await reader.SkipUntilAsync(boundary[2..]);
        while (true)
        {
            var after = await reader.ReadBytesAsync(2);
            if (after.Length < 2 || (after[0] == '-' && after[1] == '-')) break; // final boundary
            var partHead = await reader.ReadUntilAsync("\r\n\r\n"u8.ToArray(), 16384);
            if (partHead == null) break;
            var text = Encoding.UTF8.GetString(partHead);
            var fileName = System.Text.RegularExpressions.Regex.Match(text, "filename=\"([^\"]*)\"").Groups[1].Value;
            if (fileName.Length == 0) { await reader.SkipUntilAsync(boundary); continue; }
            fileName = Path.GetFileName(fileName.Replace('/', '\\'));
            fileName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (fileName.Length == 0) fileName = "file";
            var target = FileOps.UniqueName(Inbox, Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
            await using (var output = File.Create(target)) await reader.CopyUntilAsync(boundary, output, MaxUpload);
            saved.Add(target);
            Received?.Invoke(target);
        }
        await RespondAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(saved.Count == 0 ? "Nothing was received." : $"Sent {saved.Count} file{(saved.Count == 1 ? "" : "s")} to the PC.")));
    }

    private async Task ReceiveTextAsync(NetworkStream s, Dictionary<string, string> headers, byte[] leftover)
    {
        int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? Math.Min(n, 200_000) : leftover.Length;
        var body = new byte[length];
        Array.Copy(leftover, body, Math.Min(leftover.Length, length));
        int got = Math.Min(leftover.Length, length);
        while (got < length) { int r = await s.ReadAsync(body.AsMemory(got)); if (r == 0) break; got += r; }
        var form = Encoding.UTF8.GetString(body, 0, got);
        var text = form.StartsWith("text=", StringComparison.Ordinal) ? WebUtility.UrlDecode(form[5..]) : "";
        if (text.Length > 0) TextReceived?.Invoke(text);
        await RespondAsync(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page(text.Length > 0 ? "Text sent to the PC." : "Nothing was sent.")));
    }

    private string Page(string? message)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>V-OS Connect</title><style>");
        sb.Append("body{font-family:system-ui,-apple-system,Segoe UI,sans-serif;background:#0f1013;color:#f1f2f4;margin:0;padding:22px;}h1{font-size:22px;margin:0 0 4px}p{color:#a9adb7;margin:0 0 18px}");
        sb.Append(".card{background:#17181c;border:1px solid #2a2c33;border-radius:14px;padding:16px;margin-bottom:14px}a.f{display:flex;justify-content:space-between;gap:10px;color:#f1f2f4;text-decoration:none;padding:12px 4px;border-bottom:1px solid #22242a}");
        sb.Append("a.f:last-child{border:0}small{color:#757a86}button,input[type=submit]{background:#8ea2ff;color:#0b0c0f;border:0;border-radius:10px;padding:12px 18px;font-size:16px;font-weight:600;width:100%;margin-top:10px}");
        sb.Append("textarea{width:100%;box-sizing:border-box;background:#0f1013;color:#f1f2f4;border:1px solid #2a2c33;border-radius:10px;padding:10px;font-size:15px}.msg{background:#1f2a1f;color:#9be0a0;border-radius:10px;padding:10px 12px;margin-bottom:14px}</style></head><body>");
        sb.Append("<h1>V-OS Connect</h1><p>Files from ").Append(WebUtility.HtmlEncode(Environment.MachineName)).Append(". This link works only while Connect is open.</p>");
        if (message != null) sb.Append("<div class=msg>").Append(WebUtility.HtmlEncode(message)).Append("</div>");
        sb.Append("<div class=card><b>Shared with you</b>");
        if (Shared.Count == 0) sb.Append("<p style='margin:8px 0 0'>Nothing shared yet.</p>");
        for (int i = 0; i < Shared.Count; i++)
        {
            var fi = new FileInfo(Shared[i]);
            sb.Append("<a class=f href='f/").Append(i).Append("'><span>").Append(WebUtility.HtmlEncode(fi.Name)).Append("</span><small>").Append(fi.Exists ? Fmt.Bytes(fi.Length) : "").Append("</small></a>");
        }
        sb.Append("</div><div class=card><b>Send files to the PC</b><form method=post action='upload' enctype='multipart/form-data'><input type=file name=f multiple style='margin-top:12px;width:100%'><input type=submit value='Send'></form></div>");
        sb.Append("<div class=card><b>Send text</b><form method=post action='text'><textarea name=text rows=3 placeholder='A link, a code, a note…'></textarea><input type=submit value='Send text'></form></div></body></html>");
        return sb.ToString();
    }

    public void Dispose() { _cts.Cancel(); try { _listener.Stop(); } catch { } }

    /// <summary>Byte reader over a network stream with look-ahead for boundary scanning.</summary>
    private sealed class BufferedReader(NetworkStream s, byte[] initial)
    {
        private byte[] _buf = initial.Length > 0 ? initial : new byte[0];
        private int _pos;

        private async Task<bool> FillAsync(int need)
        {
            while (_buf.Length - _pos < need)
            {
                var chunk = new byte[65536];
                int n = await s.ReadAsync(chunk);
                if (n == 0) return false;
                var merged = new byte[_buf.Length - _pos + n];
                Array.Copy(_buf, _pos, merged, 0, _buf.Length - _pos);
                Array.Copy(chunk, 0, merged, _buf.Length - _pos, n);
                _buf = merged; _pos = 0;
            }
            return true;
        }

        public async Task<byte[]> ReadBytesAsync(int n) { await FillAsync(n); int take = Math.Min(n, _buf.Length - _pos); var r = _buf[_pos..(_pos + take)]; _pos += take; return r; }

        public async Task<byte[]?> ReadUntilAsync(byte[] pattern, int max)
        {
            while (true)
            {
                int i = _buf.AsSpan(_pos).IndexOf(pattern);
                if (i >= 0) { var r = _buf[_pos..(_pos + i)]; _pos += i + pattern.Length; return r; }
                if (_buf.Length - _pos > max || !await FillAsync(_buf.Length - _pos + 1)) return null;
            }
        }

        public async Task SkipUntilAsync(byte[] pattern) => await ReadUntilAsync(pattern, int.MaxValue);

        public async Task CopyUntilAsync(byte[] pattern, Stream output, long max)
        {
            long written = 0;
            while (true)
            {
                int i = _buf.AsSpan(_pos).IndexOf(pattern);
                if (i >= 0) { await output.WriteAsync(_buf.AsMemory(_pos, i)); _pos += i + pattern.Length; return; }
                // Keep the last pattern.Length-1 bytes: the boundary may straddle reads.
                int safe = _buf.Length - _pos - (pattern.Length - 1);
                if (safe > 0) { await output.WriteAsync(_buf.AsMemory(_pos, safe)); _pos += safe; written += safe; }
                if (written > max) throw new IOException("Upload too large");
                if (!await FillAsync(_buf.Length - _pos + 1)) { await output.WriteAsync(_buf.AsMemory(_pos)); _pos = _buf.Length; return; }
            }
        }
    }
}
