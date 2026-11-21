using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VOS.Platform.Shell;
using VOS.UI.Theme;
using Windows.UI;

namespace VOS.UI;

/// <summary>Segoe Fluent Icons codepoints used across V-OS (system font: crisp, hinted, consistent).</summary>
public static class Glyphs
{
    public const string Home = "", Search = "", Settings = "", Folder = "", FolderOpen = "", Document = "",
        Music = "", Globe = "", Weather = "", Note = "", Clock = "", Timer = "", Calendar = "",
        Monitor = "", Store = "", Game = "", Sparkle = "", Translate = "", Link = "", Flow = "",
        Camera = "", Shield = "", Volume = "", VolumeMute = "", Volume1 = "", Volume2 = "", Volume3 = "",
        Wifi = "", WifiOff = "", Ethernet = "", Bluetooth = "", Battery = "", BatteryCharging = "",
        Brightness = "", Moon = "", Sun = "", Focus = "", Bell = "", BellOff = "", Power = "",
        Lock = "", Restart = "", SignOut = "", Sleep = "", Play = "", Pause = "", Next = "",
        Previous = "", Close = "", Add = "", Delete = "", More = "", Pin = "", Unpin = "",
        Grid = "", List = "", Up = "", Down = "", Back = "", Forward = "", Refresh = "",
        Copy = "", Cut = "", Paste = "", Rename = "", Share = "", Download = "", Clipboard = "",
        Trash = "", Layers = "", Speed = "", Leaf = "", Palette = "", Picture = "", Apps = "",
        Desktop = "", Window = "", Keyboard = "", Info = "", Warning = "", Error = "", Check = "",
        ChevronRight = "", ChevronLeft = "", ChevronDown = "", ChevronUp = "", Star = "", StarFilled = "",
        Heart = "", HeartFilled = "", Shuffle = "", Repeat = "", RepeatOne = "", Mic = "",
        Cpu = "", Memory = "", Disk = "", Network = "", Briefcase = "", Book = "", Video = "",
        Archive = "", Filter = "", Sort = "", Tag = "", History = "", Bookmark = "", Tab = "",
        Split = "", Reader = "", Puzzle = "", Device = "", Send = "", Wand = "", Gauge = "";
}

/// <summary>Identity of a built-in V-OS application: name, glyph and its two-tone colour.</summary>
public sealed record VosApp(string Id, string Name, string Glyph, string From, string To, string Description)
{
    public static readonly IReadOnlyList<VosApp> All =
    [
        new("files", "Files", Glyphs.Folder, "#5B8CFF", "#2F5BE0", "Browse, organise and preview your files"),
        new("browser", "Browser", Glyphs.Globe, "#3DD6C6", "#118C98", "A focused web browser with vertical tabs"),
        new("music", "Music", Glyphs.Music, "#FF7AA8", "#D2306E", "Your library, playlists and now playing"),
        new("weather", "Weather", Glyphs.Weather, "#6CC4FF", "#2A7FE0", "Forecasts, radar-free and calm"),
        new("notes", "Notes", Glyphs.Note, "#FFC867", "#E3901E", "Markdown and rich-text notes"),
        new("time", "Time", Glyphs.Clock, "#9C8CFF", "#5B46D9", "Clocks, timers, stopwatch and calendar"),
        new("monitor", "Monitor", Glyphs.Gauge, "#5ED6A0", "#1D9C68", "Performance and processes"),
        new("capture", "Capture", Glyphs.Camera, "#FF9A6A", "#D9542A", "Screenshots, annotation and recording"),
        new("store", "Store", Glyphs.Store, "#7FA8FF", "#3D66D6", "Discover and update apps with winget"),
        new("gaming", "Gaming", Glyphs.Game, "#B58CFF", "#6A3FD9", "Your games, profiles and sessions"),
        new("assistant", "Assistant", Glyphs.Sparkle, "#A4B4FF", "#5867E0", "Ask V-OS for help"),
        new("translate", "Translate", Glyphs.Translate, "#62D2E8", "#1F8FB2", "Translate text, clipboard and documents"),
        new("connect", "Connect", Glyphs.Link, "#7CD9A8", "#2C9E6B", "Share files with nearby devices"),
        new("flow", "Flow", Glyphs.Flow, "#FFB27A", "#D9742A", "Automate everyday tasks"),
        new("settings", "Settings", Glyphs.Settings, "#AAB2C2", "#5C6577", "Personalise V-OS"),
    ];

    public static VosApp? Find(string id) => All.FirstOrDefault(a => a.Id == id);
    public string AppUserModelId => "VOS.App." + Id;
}

/// <summary>V-OS app icon: two-tone squircle with a light rim and a white glyph. Vector, crisp at any size.</summary>
public sealed class AppIconView : Grid
{
    public AppIconView(VosApp app, double size)
    {
        Width = Height = size;
        double r = size * 0.27;
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(r),
            Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0.15, 0),
                EndPoint = new global::Windows.Foundation.Point(0.85, 1),
                GradientStops = { new GradientStop { Color = Colors2.Hex(app.From), Offset = 0 }, new GradientStop { Color = Colors2.Hex(app.To), Offset = 1 } },
            },
        });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(r),
            BorderThickness = new Thickness(Math.Max(1, size / 48)),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0),
                EndPoint = new global::Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = Color.FromArgb(0x66, 255, 255, 255), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0x10, 255, 255, 255), Offset = 1 } },
            },
        });
        Children.Add(new FontIcon
        {
            Glyph = app.Glyph,
            FontFamily = Tokens.Icons,
            FontSize = size * 0.46,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF5, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }
}

/// <summary>Image helpers for pixels coming from V-Platform (icons, thumbnails, clipboard images).</summary>
public static class Images
{
    public static WriteableBitmap ToBitmap(PixelImage img)
    {
        var bmp = new WriteableBitmap(img.Width, img.Height);
        using (var s = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bmp.PixelBuffer))
            s.Write(img.Bgra, 0, img.Bgra.Length);
        bmp.Invalidate();
        return bmp;
    }

    /// <summary>Loads a shell icon on the icon thread and assigns it to <paramref name="target"/> when ready (left empty when unavailable).</summary>
    public static void LoadIcon(Image target, string parsingName, int pixels)
    {
        _ = IconService.GetAsync(parsingName, pixels).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } icon) UiThread.Post(() => target.Source = ToBitmap(icon));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Same as <see cref="LoadIcon"/> for recycled rows in virtualized lists: the image remembers what it was asked
    /// to show, and a late result for a row that has since been re-bound to another item is dropped.
    /// </summary>
    public static void LoadIconFor(Image target, string? parsingName, int pixels)
    {
        target.Tag = parsingName;
        target.Source = null;
        if (string.IsNullOrEmpty(parsingName)) return;
        if (IconService.TryGetCached(parsingName, pixels) is { } hit) { target.Source = ToBitmap(hit); return; }
        _ = IconService.GetAsync(parsingName, pixels).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } icon)
                UiThread.Post(() => { if (Equals(target.Tag, parsingName)) target.Source = ToBitmap(icon); });
        }, TaskScheduler.Default);
    }

    public static async Task<BitmapImage?> FromBytesAsync(byte[]? bytes, int decodeWidth = 0)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var img = new BitmapImage();
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            await img.SetSourceAsync(stream);
            return img;
        }
        catch { return null; }
    }
}

/// <summary>Small factories that keep views consistent with the design tokens.</summary>
public static class Ui
{
    public static SolidColorBrush B(string key) => ThemeService.Current.Brush(key);

    public static TextBlock Text(string text, double size = 14, string brush = "Vos.TextPrimary", FontFamily? font = null,
        global::Windows.UI.Text.FontWeight? weight = null, TextWrapping wrap = TextWrapping.NoWrap) => new()
    {
        Text = text, FontSize = size, Foreground = B(brush), FontFamily = font ?? Tokens.Text,
        FontWeight = weight ?? Microsoft.UI.Text.FontWeights.Normal, TextWrapping = wrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static FontIcon Icon(string glyph, double size = 16, string brush = "Vos.TextPrimary") => new()
    {
        Glyph = glyph, FontSize = size, FontFamily = Tokens.Icons, Foreground = B(brush),
    };
}
