using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QRCoder;
using VOS.Core;
using VOS.Core.Notifications;
using VOS.Platform.Net;
using VOS.Platform.Shell;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Connect;

/// <summary>V-OS Connect: share files with a phone or tablet on the same network by scanning a QR code; receive files and text back.</summary>
public sealed class ConnectApp : IAppModule
{
    public AppFrame Frame { get; }
    private ShareServer? _server;
    private readonly Grid _body = new();
    private readonly List<string> _received = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _idle;

    public ConnectApp()
    {
        Frame = new AppFrame(VosApp.Find("connect")!, "Connect", 760, 560, 980, 720);
        Frame.Body.Content = _body;
        _idle = UiThread.Timer(TimeSpan.FromMinutes(1), () => { if (_server != null && DateTime.Now - _server.LastActivity > TimeSpan.FromMinutes(30)) Stop(); }, repeating: true);
        _idle.Start();
        Frame.Closed += () => { _idle.Stop(); _server?.Dispose(); };
        Render();
    }

    public void HandleArgs(AppArgs args)
    {
        var files = args.All("open").Where(File.Exists).ToList();
        if (files.Count == 0) return;
        Start();
        _server?.Shared.AddRange(files.Where(f => !_server.Shared.Contains(f)));
        Render();
    }

    private void Start()
    {
        if (_server != null) return;
        var address = ShareServer.PrivateLanAddress();
        if (address == null) { Render(noNetwork: true); return; }
        try
        {
            _server = new ShareServer(address);
            _server.Received += path => UiThread.Post(() =>
            {
                _received.Insert(0, path);
                AppHost.Notify(new VosNotification { AppId = "connect", AppName = "Connect", Title = "File received", Body = Path.GetFileName(path), Glyph = Glyphs.Download, Command = "file:" + path, Actions = [new NotificationAction { Label = "Show in folder", Command = "reveal:" + path }] });
                Render();
            });
            _server.TextReceived += text => UiThread.Post(() =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                AppHost.Notify(new VosNotification { AppId = "connect", AppName = "Connect", Title = "Text received and copied", Body = text.Length > 120 ? text[..120] + "…" : text, Glyph = Glyphs.Clipboard });
            });
        }
        catch (Exception ex) { Log.Warn("Connect could not start", ex); _server = null; Render(error: ex.Message); return; }
        Render();
    }

    private void Stop()
    {
        _server?.Dispose();
        _server = null;
        Render();
    }

    private void Render(bool noNetwork = false, string? error = null)
    {
        _body.Children.Clear();
        var page = Kit.Stack(6, Kit.PageHeader("Connect", "Move files between this PC and a phone or tablet on the same Wi-Fi. No app, account or cloud needed."));
        if (noNetwork) page.Children.Add(Kit.Info("No private network found", "Connect this PC to Wi-Fi or Ethernet at home or work, then try again.", InfoBarSeverity.Warning));
        if (error != null) page.Children.Add(Kit.Info("Connect could not start", error, InfoBarSeverity.Error));
        if (_server == null)
        {
            var start = Buttons.Accent(Kit.Row(8, Ui.Icon(Glyphs.Link, 14, "Vos.OnAccent"), Ui.Text("Start sharing", 14, "Vos.OnAccent")), Start, Tokens.RadiusControl, new Thickness(18, 10, 18, 10));
            page.Children.Add(Kit.Card(Kit.Stack(12,
                Ui.Text("How it works", 15, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold),
                Ui.Text("1. Start sharing and add files.\n2. Scan the code with your phone's camera.\n3. Download files, or send photos and text back to the PC.", 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap),
                Ui.Text("The link works only on your network and only while Connect is open. Windows may ask whether V-OS can communicate on private networks: allow it so your phone can connect.", 12.5, "Vos.TextTertiary", wrap: TextWrapping.Wrap),
                start), new Thickness(22)));
        }
        else
        {
            var grid = new Grid { ColumnSpacing = 22 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var qr = new Image { Width = 210, Height = 210 };
            _ = SetQrAsync(qr, _server.Url);
            grid.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Child = qr, VerticalAlignment = VerticalAlignment.Top });
            var info = Kit.Stack(8,
                Ui.Text("Scan with your phone", 17, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold),
                new TextBlock { Text = _server.Url, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Vos.AccentText") },
                Ui.Text($"Received files go to {_server.Inbox}", 12, "Vos.TextTertiary", wrap: TextWrapping.Wrap),
                Kit.Row(8, Buttons.Accent("Add files…", () => _ = AddAsync()), Buttons.Filled("Open received folder", () => ShellOps.Launch(_server.Inbox)), Buttons.Filled("Stop", Stop)));
            Grid.SetColumn(info, 1); grid.Children.Add(info);
            page.Children.Add(Kit.Card(grid, new Thickness(22)));
            page.Children.Add(Kit.Section($"Shared ({_server.Shared.Count})"));
            var shared = _server.Shared.Select((f, i) => (UIElement)Kit.Setting(Path.GetFileName(f), File.Exists(f) ? Fmt.Bytes(new FileInfo(f).Length) : "Missing",
                Buttons.Icon(Glyphs.Close, () => { _server.Shared.Remove(f); Render(); }, 30, 11, "Stop sharing"), Glyphs.Document)).ToList();
            if (shared.Count == 0) shared.Add(Kit.Setting("Nothing shared yet", "Add files, or drop them on this window."));
            page.Children.Add(Kit.Group([.. shared]));
        }
        if (_received.Count > 0)
        {
            page.Children.Add(Kit.Section("Received"));
            page.Children.Add(Kit.Group([.. _received.Take(20).Select(f => (UIElement)Kit.Setting(Path.GetFileName(f), Path.GetDirectoryName(f), Buttons.Filled("Open", () => ShellOps.Launch(f)), Glyphs.Download))]));
        }
        _body.Children.Add(Kit.Scroll(page, 860));
        _body.AllowDrop = true;
        _body.DragOver += (_, e) => { if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; };
        _body.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            Start();
            foreach (var p in items.Select(i => i.Path).Where(File.Exists)) if (_server?.Shared.Contains(p) == false) _server.Shared.Add(p);
            Render();
        };
    }

    private async Task AddAsync()
    {
        var files = await Pickers.FilesAsync(Frame.Hwnd, Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary);
        if (_server == null) return;
        foreach (var f in files) if (!_server.Shared.Contains(f)) _server.Shared.Add(f);
        Render();
    }

    private static async Task SetQrAsync(Image target, string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        target.Source = await Images.FromBytesAsync(png);
    }
}
