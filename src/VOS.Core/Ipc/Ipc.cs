using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VOS.Core.Ipc;

/// <summary>One JSON line on a V-OS pipe.</summary>
public sealed class IpcMessage
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public JsonElement? Data { get; set; }

    public static IpcMessage Create<T>(string type, T data) =>
        new() { Type = type, Id = Guid.NewGuid().ToString("N")[..12], Data = JsonSerializer.SerializeToElement(data, VosJson.Compact) };

    public static IpcMessage Create(string type) => new() { Type = type, Id = Guid.NewGuid().ToString("N")[..12] };

    public T? Read<T>() => Data is { } d ? d.Deserialize<T>(VosJson.Compact) : default;
}

public static class IpcNames
{
    private static readonly string s_user = ResolveUser();
    /// <summary>The always-running shell process.</summary>
    public static string Shell => $"VOS.Shell.{s_user}";
    /// <summary>The app host (one per user session).</summary>
    public static string Apps => $"VOS.Apps.{s_user}";
    public static string Isolated(string id) => $"VOS.App.{id}.{s_user}";

    private static string ResolveUser()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { return Environment.UserName; }
    }
}

/// <summary>Named-pipe server restricted to the current user (PipeOptions.CurrentUserOnly).</summary>
public sealed class IpcServer : IDisposable
{
    private readonly string _name;
    private readonly Func<IpcMessage, Task<IpcMessage?>> _handler;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(string name, Func<IpcMessage, Task<IpcMessage?>> handler)
    {
        _name = name;
        _handler = handler;
    }

    public void Start()
    {
        for (int i = 0; i < 2; i++) _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => Serve(connected));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Warn($"IPC accept failed on {_name}", ex);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length > 1_000_000) break; // defensive limit
                    IpcMessage? msg;
                    try { msg = JsonSerializer.Deserialize<IpcMessage>(line, VosJson.Compact); }
                    catch (JsonException) { continue; }
                    if (msg == null || string.IsNullOrWhiteSpace(msg.Type)) continue;
                    IpcMessage? reply = null;
                    try { reply = await _handler(msg).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Error($"IPC handler failed for '{msg.Type}'", ex); }
                    if (reply != null)
                    {
                        reply.Id ??= msg.Id;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(reply, VosJson.Compact)).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("IPC connection error", ex); }
        }
    }

    public void Dispose() => _cts.Cancel();
}

public static class IpcClient
{
    /// <summary>Sends one message; returns the reply when <paramref name="expectReply"/> is set. Never throws.</summary>
    public static async Task<IpcMessage?> SendAsync(string pipeName, IpcMessage message, bool expectReply = false, int timeoutMs = 1500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, VosJson.Compact)).ConfigureAwait(false);
            if (!expectReply) return null;
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return line == null ? null : JsonSerializer.Deserialize<IpcMessage>(line, VosJson.Compact);
        }
        catch { return null; }
    }

    public static bool IsListening(string pipeName) => SendAsync(pipeName, IpcMessage.Create("ping"), expectReply: true, 400).GetAwaiter().GetResult()?.Type == "pong";
}

/// <summary>Message types understood by the shell.</summary>
public static class ShellMessages
{
    public const string Ping = "ping";
    public const string Notify = "notify";
    public const string ActivityUpdate = "activity.update";
    public const string ActivityEnd = "activity.end";
    public const string Command = "command";
    /// <summary>Runs a command string and replies "ok" or "failed" once it has run. Actions that need confirmation
    /// reply after the user answers the shell's dialog. Used by the assistant so it can report what really happened.</summary>
    public const string Run = "run";
    public const string Open = "open";
    public const string Shutdown = "shutdown";
    /// <summary>Runs a hotkey action by name ("control", "clipboard", "switcher"…): used by automation and UI tests.</summary>
    public const string Action = "action";
    /// <summary>Returns process diagnostics (memory, GC, threads, handles, uptime) as JSON.</summary>
    public const string Diagnostics = "diag";
}

/// <summary>Process diagnostics reported over IPC (used by Monitor, the checkup command and dev tooling).</summary>
public sealed record ProcessDiagnostics(
    long PrivateBytes, long WorkingSet, long ManagedHeap, long GcCommitted, long GcFragmented, int Gen0, int Gen1, int Gen2,
    int Threads, int Handles, double UptimeSeconds, double CpuSeconds)
{
    public static ProcessDiagnostics Capture()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new ProcessDiagnostics(p.PrivateMemorySize64, p.WorkingSet64, GC.GetTotalMemory(false), gc.TotalCommittedBytes, gc.FragmentedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), p.Threads.Count, p.HandleCount,
            (DateTime.Now - p.StartTime).TotalSeconds, p.TotalProcessorTime.TotalSeconds);
    }
}
