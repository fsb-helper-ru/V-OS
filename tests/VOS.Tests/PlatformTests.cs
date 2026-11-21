using VOS.Core.Perf;
using VOS.Platform.Audio;
using VOS.Platform.Input;
using VOS.Platform.Media;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using Xunit;

namespace VOS.Tests;

/// <summary>Integration tests against the real Windows APIs. Read-only: they never change system state.</summary>
public class PlatformTests
{
    [Fact]
    public void App_catalog_lists_installed_apps_with_launch_targets()
    {
        var apps = AppCatalog.Get(refresh: true);
        Assert.True(apps.Count > 5, $"only {apps.Count} apps found");
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.ParsingName)));
        Assert.Contains(apps, a => a.ExePath != null);
    }

    [Fact]
    public void Shell_icons_are_extracted_with_alpha()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var img = IconService.Extract(notepad, 48, thumbnail: false);
        Assert.NotNull(img);
        Assert.Equal(img!.Width * img.Height * 4, img.Bgra.Length);
        var png = img.ToPng();
        var decoded = PngCodec.Decode(png);
        Assert.Equal(img.Width, decoded!.Width);
    }

    [Fact]
    public void Hardware_probe_describes_this_machine()
    {
        var h = HardwareProbe.Probe();
        Assert.True(h.LogicalCores >= 1);
        Assert.True(h.RamGb > 1);
        Assert.False(string.IsNullOrWhiteSpace(h.GpuName));
        Assert.True(h.TotalPixels > 0);
        var q = QualityResolver.Resolve(VOS.Core.Settings.PerformanceProfile.Adaptive, h);
        Assert.NotEqual(VOS.Core.Settings.PerformanceProfile.Adaptive, q.Profile);
    }

    [Fact]
    public void Metrics_are_in_valid_ranges()
    {
        using var m = new MetricsSampler();
        Thread.Sleep(300);
        var s = m.Sample();
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.InRange(s.MemoryPercent, 1, 100);
        Assert.True(s.MemoryTotalGb > 1);
        Assert.InRange(s.GpuPercent, 0, 100);
        Assert.InRange(s.DiskActivePercent, 0, 100);
    }

    [Fact]
    public void Window_enumeration_and_displays()
    {
        var tracker = new WindowTracker();
        tracker.Refresh();
        Assert.All(tracker.Windows, w => Assert.NotEqual(IntPtr.Zero, w.Hwnd));
        Assert.NotEmpty(Displays.All());
        Assert.True(Displays.Primary.Bounds.Width > 0);
    }

    [Fact]
    public void Audio_endpoint_reads_without_changing_anything()
    {
        using var audio = new AudioService();
        if (!audio.Available) return; // machines without audio devices
        Assert.InRange(audio.Volume, 0f, 1f);
        Assert.Contains(audio.Devices(), d => d.IsDefault);
    }

    [Fact]
    public async Task Media_sessions_start()
    {
        var media = new MediaSessionService();
        await media.StartAsync();
        Assert.NotNull(media.Current);
    }

    [Theory]
    [InlineData("Alt+Space", 0x1u, 0x20u)]
    [InlineData("Win+Alt+V", 0x9u, 0x56u)]
    [InlineData("Ctrl+Alt+Shift+F12", 0x7u, 0x7Bu)]
    public void Hotkey_gestures_parse(string text, uint mods, uint key)
    {
        Assert.True(Gesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    [Fact]
    public void Invalid_gestures_are_rejected()
    {
        Assert.False(Gesture.TryParse("Ctrl+Alt", out _));
        Assert.False(Gesture.TryParse("Win+Nope", out _));
    }

    [Fact]
    public void Status_snapshots()
    {
        var b = SystemStatusService.ReadBattery();
        Assert.InRange(b.Percent, 0, 100);
        var n = SystemStatusService.ReadNetwork();
        Assert.False(string.IsNullOrEmpty(n.Name));
    }
}
