using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VOS.Core;
using VOS.Core.Notifications;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Path = System.IO.Path;

namespace VOS.Apps.Capture;

/// <summary>
/// V-OS Capture: region, window and full-screen screenshots. The screen is frozen before any V-OS window appears,
/// so menus and tooltips can be captured. Every capture is copied to the clipboard and saved to Pictures\Screenshots,
/// then opens in an editor with pen, highlighter, rectangle and arrow.
/// </summary>
public sealed class CaptureApp : IAppModule
{
    public AppFrame Frame { get; }
    public bool ActivateOnOpen => !_capturing;
    private bool _capturing;
    private readonly Grid _body = new();

    // editor state
    private PixelImage? _image;
    private string? _savedPath;
    private CanvasBitmap? _bitmap;
    private CanvasControl? _canvas;
    private readonly List<Mark> _marks = [], _redo = [];
    private Mark? _drawing;
    private string _tool = "pen";
    private Color _color = Colors2.Hex("#FF5A5A");
    private float _width = 4;

    private sealed class Mark
    {
        public string Kind = "pen";
        public Color Color;
        public float Width;
        public List<Vector2> Points = [];
    }

    public CaptureApp()
    {
        Frame = new AppFrame(VosApp.Find("capture")!, "Capture", 720, 480, 1100, 760);
        Frame.Body.Content = _body;
        ShowHome();
    }

    public void HandleArgs(AppArgs args)
    {
        var mode = args.Get("mode");
        var open = args.All("open").FirstOrDefault(File.Exists);
        if (open != null) { OpenFile(open); return; }
        if (mode is "region" or "window" or "screen")
            _ = StartCaptureAsync(mode, int.TryParse(args.Get("delay"), out var d) ? d : 0);
    }

    // ------------------------------------------------------------------ capture flow

    private async Task StartCaptureAsync(string mode, int delaySeconds)
    {
        if (_capturing) return;
        _capturing = true;
        bool wasVisible = Frame.AppWindow.IsVisible;
        try
        {
            if (wasVisible) Frame.AppWindow.Hide();
            if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            else await Task.Delay(wasVisible ? 250 : 30); // let our own window disappear from the composed desktop
            Win32.GetCursorPos(out var cursor);
            var monitor = Displays.All().FirstOrDefault(m => cursor.X >= m.Bounds.Left && cursor.X < m.Bounds.Right && cursor.Y >= m.Bounds.Top && cursor.Y < m.Bounds.Bottom) ?? Displays.Primary;
            var bounds = new Win32.RECT { Left = monitor.Bounds.Left, Top = monitor.Bounds.Top, Right = monitor.Bounds.Right, Bottom = monitor.Bounds.Bottom };
            var shot = ScreenCapture.Capture(bounds);
            if (mode == "screen") { await FinishAsync(shot); return; }
            var windows = ScreenCapture.Windows([Frame.Hwnd]);
            var overlay = new SelectionOverlay(shot, bounds, monitor.Scale, windows, startInWindowMode: mode == "window");
            var rect = await overlay.SelectAsync();
            if (rect is not { } r) { if (wasVisible) Frame.Activate(); return; }
            await FinishAsync(Crop(shot, r));
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            if (wasVisible) Frame.Activate();
        }
        finally { _capturing = false; }
    }

    private static PixelImage Crop(PixelImage src, Win32.RECT r)
    {
        int x = Math.Clamp(r.Left, 0, src.Width - 1), y = Math.Clamp(r.Top, 0, src.Height - 1);
        int w = Math.Clamp(r.Right - r.Left, 1, src.Width - x), h = Math.Clamp(r.Bottom - r.Top, 1, src.Height - y);
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++) System.Buffer.BlockCopy(src.Bgra, ((y + row) * src.Width + x) * 4, dst, row * w * 4, w * 4);
        return new PixelImage(w, h, dst);
    }

    private async Task FinishAsync(PixelImage image)
    {
        _image = image;
        _marks.Clear(); _redo.Clear();
        _bitmap = null;
        var path = Path.Combine(ShellNative.Screenshots, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        try { await File.WriteAllBytesAsync(path, PngCodec.Encode(image)); _savedPath = path; }
        catch (Exception ex) { Log.Warn("Screenshot could not be saved", ex); _savedPath = null; }
        await CopyAsync();
        AppHost.Notify(new VosNotification
        {
            AppId = "capture", AppName = "Capture", Title = "Screenshot copied", Body = _savedPath != null ? "Saved to " + Path.GetFileName(_savedPath) : "Copied to the clipboard",
            Glyph = Glyphs.Camera, Priority = NotificationPriority.Low, Silent = true,
            Actions = _savedPath != null ? [new NotificationAction { Label = "Show in folder", Command = "reveal:" + _savedPath }] : [],
        });
        ShowEditor();
        Frame.Activate();
    }

    // ------------------------------------------------------------------ home

    private void ShowHome()
    {
        Frame.Title = "Capture";
        _body.Children.Clear();
        int delay = 0;
        var page = Kit.Stack(6, Kit.PageHeader("Capture", "Screenshots are copied to the clipboard and saved to Pictures › Screenshots."));
        var modes = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 3; i++) modes.ColumnDefinitions.Add(new ColumnDefinition());
        (string mode, string label, string glyph, string keys)[] items = [("region", "Region", Glyphs.Split, "Ctrl+Alt+S"), ("window", "Window", Glyphs.Window, "Ctrl+Alt+W"), ("screen", "Screen", Glyphs.Desktop, "Ctrl+Alt+P")];
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            var content = Kit.Stack(8, Kit.GlyphBadge(it.glyph, 44), Ui.Text(it.label, 15, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold), Ui.Text(it.keys, 12, "Vos.TextTertiary"));
            content.HorizontalAlignment = HorizontalAlignment.Center;
            foreach (var c in content.Children.OfType<FrameworkElement>()) c.HorizontalAlignment = HorizontalAlignment.Center;
            var b = Buttons.Filled(content, () => _ = StartCaptureAsync(it.mode, delay), Tokens.RadiusCard, new Thickness(16, 20, 16, 18));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            Buttons.AutomationName(b, "Capture " + it.label.ToLowerInvariant());
            Grid.SetColumn(b, i);
            modes.Children.Add(b);
        }
        page.Children.Add(modes);
        page.Children.Add(Kit.Row(10, Ui.Text("Delay", 13, "Vos.TextSecondary"), Kit.Segments([("0", "None"), ("3", "3 s"), ("5", "5 s"), ("10", "10 s")], "0", v => delay = int.Parse(v, CultureInfo.InvariantCulture))));
        page.Children.Add(Kit.Section("Recent screenshots"));
        var folder = ShellNative.Screenshots;
        var recent = new DirectoryInfo(folder).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTime).Take(12).ToList();
        if (recent.Count == 0) page.Children.Add(Ui.Text("Your screenshots will appear here.", 13, "Vos.TextSecondary"));
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 200, ItemHeight = 150 };
        foreach (var f in recent)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            var path = f.FullName;
            _ = IconService.GetAsync(path, 256, thumbnail: true).ContinueWith(t => { if (t.Result is { } p) UiThread.Post(() => img.Source = Images.ToBitmap(p)); }, TaskScheduler.Default);
            var cell = Kit.Stack(4, new Border { Height = 110, CornerRadius = new CornerRadius(Tokens.RadiusControl), Background = Ui.B("Vos.Card"), Child = img }, Ui.Text(f.LastWriteTime.ToString("g", CultureInfo.CurrentCulture), 11.5, "Vos.TextSecondary"));
            var b = Buttons.Subtle(cell, () => OpenFile(path), Tokens.RadiusControl, new Thickness(4));
            b.Margin = new Thickness(0, 0, 8, 8);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            Buttons.AutomationName(b, Path.GetFileName(path));
            grid.Children.Add(b);
        }
        page.Children.Add(grid);
        page.Children.Add(Kit.Link("Open the Screenshots folder", () => ShellOps.Launch(folder)));
        _body.Children.Add(Kit.Scroll(page));
    }

    private async void OpenFile(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var decoded = PngCodec.Decode(bytes);
            if (decoded == null)
            {
                // Not one of ours (any PNG/JPEG): decode through WIC via Win2D.
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bmp = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream);
                var raw = bmp.GetPixelBytes();
                decoded = new PixelImage((int)bmp.SizeInPixels.Width, (int)bmp.SizeInPixels.Height, raw);
            }
            _image = decoded;
            _savedPath = path;
            _marks.Clear(); _redo.Clear(); _bitmap = null;
            ShowEditor();
            Frame.Activate();
        }
        catch (Exception ex) { await Dialogs.InfoAsync(Frame.Root.XamlRoot, "The image could not be opened", ex.Message); }
    }

    // ------------------------------------------------------------------ editor

    private void ShowEditor()
    {
        if (_image == null) { ShowHome(); return; }
        Frame.Title = (_savedPath != null ? Path.GetFileName(_savedPath) : "Screenshot") + " — Capture";
        _body.Children.Clear();
        var root = new Grid { RowSpacing = 0 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        void Tool(string id, string glyph, string tip)
        {
            var b = Buttons.Icon(glyph, null, 36, 15, tip);
            b.Click += (_, _) => { _tool = id; StyleTools(tools); };
            b.Tag = id;
            tools.Children.Add(b);
        }
        Tool("pen", Glyphs.Wand, "Pen");
        Tool("highlighter", Glyphs.Tag, "Highlighter");
        Tool("rect", Glyphs.Layers, "Rectangle");
        Tool("arrow", Glyphs.Up, "Arrow");
        StyleTools(tools);
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        foreach (var hex in new[] { "#FF5A5A", "#FFC24B", "#4BD38A", "#5AA9FF", "#FFFFFF", "#111111" })
        {
            var c = Colors2.Hex(hex);
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(c), BorderBrush = Ui.B("Vos.Stroke"), BorderThickness = new Thickness(1) };
            var b = Buttons.Subtle(dot, () => { _color = c; StyleColors(colors); }, 13, new Thickness(2));
            b.Tag = c;
            Buttons.AutomationName(b, "Colour " + hex);
            colors.Children.Add(b);
        }
        StyleColors(colors);
        var width = Kit.Segments([("3", "Thin"), ("6", "Medium"), ("10", "Thick")], ((int)_width) switch { <= 4 => "3", <= 7 => "6", _ => "10" }, v => _width = float.Parse(v, CultureInfo.InvariantCulture));
        var undo = Buttons.Icon(Glyphs.Back, Undo, 36, 14, "Undo (Ctrl+Z)");
        var redo = Buttons.Icon(Glyphs.Forward, Redo, 36, 14, "Redo (Ctrl+Y)");
        var left = Kit.Row(4, tools, Separator(), colors, Separator(), width, Separator(), undo, redo);
        var newMenu = new MenuFlyout();
        foreach (var (mode, label) in new[] { ("region", "Region"), ("window", "Window"), ("screen", "Screen") })
        {
            var mi = new MenuFlyoutItem { Text = label };
            mi.Click += (_, _) => _ = StartCaptureAsync(mode, 0);
            newMenu.Items.Add(mi);
        }
        var newCapture = new DropDownButton { Content = Kit.Row(6, Ui.Icon(Glyphs.Camera, 13), Ui.Text("New", 13.5)), Flyout = newMenu };
        Buttons.AutomationName(newCapture, "New capture");
        var right = Kit.Row(6,
            newCapture,
            Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Copy, 13), Ui.Text("Copy", 13.5)), () => _ = CopyAsync()),
            Buttons.Accent(Kit.Row(6, Ui.Icon(Glyphs.Download, 13, "Vos.OnAccent"), Ui.Text("Save", 13.5, "Vos.OnAccent")), () => _ = SaveAsync(saveAs: false)),
            Buttons.Icon(Glyphs.More, null, 36, 14, "More"));
        var more = new MenuFlyout();
        var saveAs = new MenuFlyoutItem { Text = "Save as…", Icon = new FontIcon { Glyph = Glyphs.Download } }; saveAs.Click += (_, _) => _ = SaveAsync(saveAs: true); more.Items.Add(saveAs);
        var folder = new MenuFlyoutItem { Text = "Show in folder", Icon = new FontIcon { Glyph = Glyphs.Folder } }; folder.Click += (_, _) => { if (_savedPath != null) ShellOps.ShowInExplorer(_savedPath); else ShellOps.Launch(ShellNative.Screenshots); }; more.Items.Add(folder);
        var home = new MenuFlyoutItem { Text = "All screenshots", Icon = new FontIcon { Glyph = Glyphs.Grid } }; home.Click += (_, _) => ShowHome(); more.Items.Add(home);
        ((Button)right.Children[3]).Flyout = more;
        var bar = new Grid { Padding = new Thickness(14, 2, 14, 10) };
        bar.Children.Add(left);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(right);
        root.Children.Add(bar);

        _canvas = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent };
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (s, e) => _bitmap = null;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        var layer = new Border { Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(0, 1, 0, 0), Child = new Grid { Padding = new Thickness(20), Children = { _canvas } } };
        Grid.SetRow(layer, 1);
        root.Children.Add(layer);
        root.KeyDown += (_, e) =>
        {
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (ctrl && e.Key == VirtualKey.Z) { Undo(); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.Y) { Redo(); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.C) { _ = CopyAsync(); e.Handled = true; }
            else if (ctrl && e.Key == VirtualKey.S) { _ = SaveAsync(false); e.Handled = true; }
        };
        _body.Children.Add(root);
    }

    private void StyleTools(StackPanel tools)
    {
        foreach (var b in tools.Children.OfType<Button>())
            b.Background = (string)b.Tag == _tool ? Ui.B("Vos.AccentSubtle") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void StyleColors(StackPanel colors)
    {
        foreach (var b in colors.Children.OfType<Button>())
            ((Border)b.Content).BorderThickness = new Thickness((Color)b.Tag == _color ? 2.5 : 1);
    }

    private static Border Separator() => new() { Width = 1, Height = 20, Background = Ui.B("Vos.Stroke"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    // Image placement inside the canvas (uniform fit, centred, never upscaled beyond 1:1).
    private (float scale, Vector2 offset) Placement()
    {
        if (_image == null || _canvas == null) return (1, Vector2.Zero);
        float cw = (float)_canvas.ActualWidth, ch = (float)_canvas.ActualHeight;
        // Fit inside the canvas, but never larger than one screen pixel per image pixel.
        float raster = (float)(_canvas.XamlRoot?.RasterizationScale ?? 1);
        float s = Math.Min(1f / raster, Math.Min(cw / _image.Width, ch / _image.Height));
        if (s <= 0) s = 1f / raster;
        var size = new Vector2(_image.Width * s, _image.Height * s);
        return (s, new Vector2((cw - size.X) / 2, (ch - size.Y) / 2));
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_image == null) return;
        _bitmap ??= CanvasBitmap.CreateFromBytes(sender, _image.Bgra, _image.Width, _image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var (s, o) = Placement();
        var ds = args.DrawingSession;
        ds.FillRoundedRectangle(o.X - 1, o.Y - 1, _image.Width * s + 2, _image.Height * s + 2, 4, 4, Color.FromArgb(0x40, 0, 0, 0));
        ds.DrawImage(_bitmap, new Windows.Foundation.Rect(o.X, o.Y, _image.Width * s, _image.Height * s));
        ds.Transform = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(o);
        DrawMarks(ds, _marks.Concat(_drawing != null ? [_drawing] : []));
    }

    private static readonly CanvasStrokeStyle s_round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    private static void DrawMarks(CanvasDrawingSession ds, IEnumerable<Mark> marks)
    {
        foreach (var m in marks)
        {
            if (m.Points.Count == 0) continue;
            switch (m.Kind)
            {
                case "pen":
                case "highlighter":
                    var color = m.Kind == "highlighter" ? Color.FromArgb(0x60, m.Color.R, m.Color.G, m.Color.B) : m.Color;
                    float w = m.Kind == "highlighter" ? m.Width * 4 : m.Width;
                    if (m.Points.Count == 1) { ds.FillCircle(m.Points[0], w / 2, color); break; }
                    using (var pb = new CanvasPathBuilder(ds))
                    {
                        pb.BeginFigure(m.Points[0]);
                        for (int i = 1; i < m.Points.Count; i++) pb.AddLine(m.Points[i]);
                        pb.EndFigure(CanvasFigureLoop.Open);
                        using var geo = CanvasGeometry.CreatePath(pb);
                        ds.DrawGeometry(geo, color, w, s_round);
                    }
                    break;
                case "rect" when m.Points.Count >= 2:
                    var a = m.Points[0]; var b = m.Points[^1];
                    ds.DrawRoundedRectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y), 3, 3, m.Color, m.Width, s_round);
                    break;
                case "arrow" when m.Points.Count >= 2:
                    var from = m.Points[0]; var to = m.Points[^1];
                    var dir = to - from;
                    if (dir.Length() < 2) break;
                    dir = Vector2.Normalize(dir);
                    float head = Math.Max(12, m.Width * 4);
                    var left = to - head * Vector2.Transform(dir, Matrix3x2.CreateRotation(0.45f));
                    var right = to - head * Vector2.Transform(dir, Matrix3x2.CreateRotation(-0.45f));
                    ds.DrawLine(from, to, m.Color, m.Width, s_round);
                    ds.DrawLine(to, left, m.Color, m.Width, s_round);
                    ds.DrawLine(to, right, m.Color, m.Width, s_round);
                    break;
            }
        }
    }

    private Vector2 ToImage(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        var (s, o) = Placement();
        return new Vector2(((float)p.X - o.X) / s, ((float)p.Y - o.Y) / s);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_image == null) return;
        _canvas!.CapturePointer(e.Pointer);
        float scale = 1 / Placement().scale;
        _drawing = new Mark { Kind = _tool, Color = _color, Width = _width * Math.Max(1, scale * 0.8f), Points = [ToImage(e)] };
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing == null) return;
        var p = ToImage(e);
        if (_drawing.Kind is "pen" or "highlighter") { if (Vector2.Distance(_drawing.Points[^1], p) > 1.5f) _drawing.Points.Add(p); }
        else { if (_drawing.Points.Count > 1) _drawing.Points[^1] = p; else _drawing.Points.Add(p); }
        _canvas!.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing == null) return;
        _marks.Add(_drawing);
        _redo.Clear();
        _drawing = null;
        _canvas!.ReleasePointerCapture(e.Pointer);
        _canvas.Invalidate();
    }

    private void Undo() { if (_marks.Count == 0) return; _redo.Add(_marks[^1]); _marks.RemoveAt(_marks.Count - 1); _canvas?.Invalidate(); }
    private void Redo() { if (_redo.Count == 0) return; _marks.Add(_redo[^1]); _redo.RemoveAt(_redo.Count - 1); _canvas?.Invalidate(); }

    /// <summary>Renders the image with annotations at full resolution.</summary>
    private async Task<InMemoryRandomAccessStream> RenderPngAsync()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var rt = new CanvasRenderTarget(device, _image!.Width, _image.Height, 96);
        using (var bmp = CanvasBitmap.CreateFromBytes(device, _image.Bgra, _image.Width, _image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized))
        using (var ds = rt.CreateDrawingSession())
        {
            ds.DrawImage(bmp);
            DrawMarks(ds, _marks);
        }
        var stream = new InMemoryRandomAccessStream();
        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }

    private async Task CopyAsync()
    {
        if (_image == null) return;
        if (Environment.GetEnvironmentVariable("VOS_TEST_NO_CLIPBOARD") == "1") return; // automated tests leave the user's clipboard alone
        try
        {
            var stream = await RenderPngAsync();
            var dp = new DataPackage();
            dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(dp);
            Clipboard.Flush();
        }
        catch (Exception ex) { Log.Warn("Copy failed", ex); }
    }

    private async Task SaveAsync(bool saveAs)
    {
        if (_image == null) return;
        var path = _savedPath;
        if (saveAs || path == null)
        {
            path = await Pickers.SaveAsync(Frame.Hwnd, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}", "PNG image", ".png");
            if (path == null) return;
        }
        try
        {
            using var stream = await RenderPngAsync();
            using var file = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(file);
            _savedPath = path;
            Frame.Title = Path.GetFileName(path) + " — Capture";
        }
        catch (Exception ex) { await Dialogs.InfoAsync(Frame.Root.XamlRoot, "Save failed", ex.Message); }
    }
}

/// <summary>
/// Full-monitor selection over a frozen screenshot: drag for a region, click to take the window under the pointer,
/// Enter for the whole screen, Esc to cancel. The frozen image means menus and hover states can be captured.
/// </summary>
internal sealed class SelectionOverlay : SurfaceWindow
{
    private readonly TaskCompletionSource<Win32.RECT?> _done = new();
    private readonly Win32.RECT _bounds;
    private readonly double _scale;
    private readonly List<CapturableWindow> _windows;
    private readonly Border _selection = new() { BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed };
    private readonly Image _bright = new() { Stretch = Stretch.Fill };
    private readonly Border _label = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _labelText = new() { FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontFamily = Tokens.Instrument };
    private Windows.Foundation.Point? _start;
    private Win32.RECT? _hover;
    private bool _dragging;

    public SelectionOverlay(PixelImage shot, Win32.RECT bounds, double scale, List<CapturableWindow> windows, bool startInWindowMode)
        : base("V-OS Capture", topmost: true, noActivate: false)
    {
        _bounds = bounds;
        _scale = scale;
        _windows = windows;
        var bitmap = Images.ToBitmap(shot);
        Root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        Root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)) });
        _bright.Source = bitmap;
        _bright.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };
        Root.Children.Add(_bright);
        _selection.BorderBrush = Ui.B("Vos.Accent");
        _selection.HorizontalAlignment = HorizontalAlignment.Left;
        _selection.VerticalAlignment = VerticalAlignment.Top;
        Root.Children.Add(_selection);
        _label.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x0B, 0x0C, 0x0F));
        _label.Child = _labelText;
        _label.HorizontalAlignment = HorizontalAlignment.Left;
        _label.VerticalAlignment = VerticalAlignment.Top;
        Root.Children.Add(_label);
        var hint = new Border
        {
            CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 8, 16, 9), Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0C, 0x0F)),
            Child = new TextBlock { Text = (startInWindowMode ? "Click a window" : "Drag to select an area · click a window") + " · Enter for the whole screen · Esc to cancel", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255)) },
        };
        Root.Children.Add(hint);
        Root.PointerPressed += OnPressed;
        Root.PointerMoved += OnMoved;
        Root.PointerReleased += OnReleased;
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) Complete(null);
            else if (e.Key == VirtualKey.Enter) Complete(new Win32.RECT { Left = 0, Top = 0, Right = _bounds.Right - _bounds.Left, Bottom = _bounds.Bottom - _bounds.Top });
        };
        // A Grid cannot take focus; this invisible control receives the keyboard so Esc and Enter work.
        var keys = new ContentControl { IsTabStop = true, Width = 1, Height = 1, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Root.Children.Add(keys);
        keys.Loaded += (_, _) => keys.Focus(FocusState.Programmatic);
        MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        Show(activate: true);
    }

    public Task<Win32.RECT?> SelectAsync() => _done.Task;

    private void Complete(Win32.RECT? r)
    {
        if (_done.Task.IsCompleted) return;
        Hide();
        Close();
        _done.TrySetResult(r);
    }

    private Windows.Foundation.Point Physical(Windows.Foundation.Point dip) => new(dip.X * _scale, dip.Y * _scale);

    private void ShowRect(Win32.RECT r, bool window)
    {
        double x = r.Left / _scale, y = r.Top / _scale, w = (r.Right - r.Left) / _scale, h = (r.Bottom - r.Top) / _scale;
        _selection.Visibility = _label.Visibility = Visibility.Visible;
        _selection.Margin = new Thickness(x, y, 0, 0);
        _selection.Width = Math.Max(1, w);
        _selection.Height = Math.Max(1, h);
        _bright.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(x, y, Math.Max(0, w), Math.Max(0, h)) };
        _labelText.Text = $"{r.Right - r.Left} × {r.Bottom - r.Top}" + (window ? "  ·  click to capture" : "");
        _label.Margin = new Thickness(x, Math.Max(0, y - 30), 0, 0);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _start = e.GetCurrentPoint(Root).Position;
        _dragging = false;
        Root.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root).Position;
        if (_start is { } s && (Math.Abs(p.X - s.X) > 4 || Math.Abs(p.Y - s.Y) > 4)) _dragging = true;
        if (_dragging && _start is { } a)
        {
            var pa = Physical(a); var pb = Physical(p);
            ShowRect(new Win32.RECT { Left = (int)Math.Min(pa.X, pb.X), Top = (int)Math.Min(pa.Y, pb.Y), Right = (int)Math.Max(pa.X, pb.X), Bottom = (int)Math.Max(pa.Y, pb.Y) }, false);
            return;
        }
        // Not dragging: highlight the front-most window under the pointer.
        var phys = Physical(p);
        int sx = (int)phys.X + _bounds.Left, sy = (int)phys.Y + _bounds.Top;
        var hit = _windows.FirstOrDefault(w => sx >= w.Bounds.Left && sx < w.Bounds.Right && sy >= w.Bounds.Top && sy < w.Bounds.Bottom);
        if (hit == null) { _hover = null; _selection.Visibility = _label.Visibility = Visibility.Collapsed; _bright.Clip = new RectangleGeometry(); return; }
        var r = new Win32.RECT
        {
            Left = Math.Max(0, hit.Bounds.Left - _bounds.Left), Top = Math.Max(0, hit.Bounds.Top - _bounds.Top),
            Right = Math.Min(_bounds.Right - _bounds.Left, hit.Bounds.Right - _bounds.Left), Bottom = Math.Min(_bounds.Bottom - _bounds.Top, hit.Bounds.Bottom - _bounds.Top),
        };
        _hover = r;
        ShowRect(r, true);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        Root.ReleasePointerCapture(e.Pointer);
        var p = e.GetCurrentPoint(Root).Position;
        if (_dragging && _start is { } a)
        {
            var pa = Physical(a); var pb = Physical(p);
            var r = new Win32.RECT { Left = (int)Math.Min(pa.X, pb.X), Top = (int)Math.Min(pa.Y, pb.Y), Right = (int)Math.Max(pa.X, pb.X), Bottom = (int)Math.Max(pa.Y, pb.Y) };
            if (r.Right - r.Left >= 4 && r.Bottom - r.Top >= 4) { Complete(r); return; }
        }
        else if (_hover is { } h) { Complete(h); return; }
        _start = null;
        _dragging = false;
    }
}
