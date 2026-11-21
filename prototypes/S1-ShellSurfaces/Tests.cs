using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace S1;

/// <summary>Behavioural tests of the shell surfaces on the real desktop. Every check measures pixels or OS state.</summary>
internal static class Tests
{
    private static int ScreenW => Native.GetSystemMetrics(0);
    private static int ScreenH => Native.GetSystemMetrics(1);
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    private static Task<double> FirstFrameAfter(Action show)
    {
        var tcs = new TaskCompletionSource<double>();
        var sw = Stopwatch.StartNew();
        EventHandler<object>? handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; tcs.TrySetResult(sw.Elapsed.TotalMilliseconds); };
        CompositionTarget.Rendering += handler;
        show();
        return tcs.Task;
    }

    public static async Task<Dictionary<string, object?>> Pill(string outDir)
    {
        var r = new Dictionary<string, object?>();
        int x = (ScreenW - PillSurface.W) / 2, y = 0, row = (int)PillSurface.Top + 15;
        var bounds = new RectInt32(x, y, PillSurface.W, PillSurface.H);
        var foreground = Native.GetForegroundWindow();
        var pattern = new PatternSurface(bounds);
        pattern.Surface.Show();
        await Task.Delay(600);
        var background = Native.Capture(x, y, PillSurface.W, PillSurface.H);
        background.SavePng(Path.Combine(outDir, "pill-0-background.png"));

        var pill = new PillSurface(bounds);
        r["pill.showToFirstFrameMs"] = Math.Round(await FirstFrameAfter(pill.Surface.Show), 1);
        await Task.Delay(500);
        r["pill.foregroundUnchanged"] = Native.GetForegroundWindow() == foreground;
        r["pill.style.toolWindow"] = (pill.Surface.ExStyle & Native.WS_EX_TOOLWINDOW) != 0;
        r["pill.style.noActivate"] = (pill.Surface.ExStyle & Native.WS_EX_NOACTIVATE) != 0;
        r["pill.style.topmost"] = (pill.Surface.ExStyle & Native.WS_EX_TOPMOST) != 0;
        r["pill.style.noRedirectionBitmap"] = (pill.Surface.ExStyle & Native.WS_EX_NOREDIRECTIONBITMAP) != 0;
        r["pill.shownInSwitchers"] = pill.Surface.AppWindow.IsShownInSwitchers;

        var collapsed = Native.Capture(x, y, PillSurface.W, PillSurface.H);
        collapsed.SavePng(Path.Combine(outDir, "pill-1-collapsed.png"));
        r["pill.collapsedWidthPx"] = collapsed.DarkRunWidth(row);
        r["pill.transparency.matchBelowCapsule"] = Math.Round(collapsed.MatchRatio(background, 0, 60, PillSurface.W, PillSurface.H - 60), 4);
        r["pill.transparency.matchBesideCapsule"] = Math.Round(collapsed.MatchRatio(background, 0, 0, 150, 60), 4);
        r["pill.inkLuma"] = Math.Round(collapsed.LumaStats(PillSurface.W / 2 - 86, row - 3, 6, 6).mean, 1);

        // Hit testing: capsule-shaped window region.
        pill.FitRegion(PillSurface.Collapsed);
        await Task.Delay(150);
        r["pill.hit.insideCapsule"] = Native.RootAt(x + PillSurface.W / 2, y + row) == pill.Surface.Hwnd;
        r["pill.hit.besideCapsulePassesThrough"] = Native.RootAt(x + PillSurface.W / 2 - 140, y + row) == pattern.Surface.Hwnd;
        r["pill.hit.belowCapsulePassesThrough"] = Native.RootAt(x + PillSurface.W / 2, y + 120) == pattern.Surface.Hwnd;
        var afterRegion = Native.Capture(x, y, PillSurface.W, 60);
        r["pill.region.capsuleIntactWidthPx"] = afterRegion.DarkRunWidth(row);

        // Spring morph sampled from a worker thread while the UI thread is deliberately blocked.
        pill.FitRegion(PillSurface.Expanded);
        var samples = new List<(double t, int w)>();
        var clock = Stopwatch.StartNew();
        var sampler = Task.Run(() =>
        {
            while (clock.ElapsedMilliseconds < 900)
            {
                var f = Native.Capture(x, y, PillSurface.W, 60);
                lock (samples) samples.Add((clock.Elapsed.TotalMilliseconds, f.DarkRunWidth(row)));
                Thread.Sleep(12);
            }
        });
        pill.Morph(true);
        await Task.Delay(30);
        double blockStart = clock.Elapsed.TotalMilliseconds;
        Thread.Sleep(350); // UI thread frozen on purpose
        double blockEnd = clock.Elapsed.TotalMilliseconds;
        await sampler;
        var during = samples.Where(s => s.t >= blockStart && s.t <= blockEnd).Select(s => s.w).ToList();
        r["pill.morph.samples"] = samples.Select(s => $"{s.t:0}ms:{s.w}").ToArray();
        r["pill.morph.widthChangeWhileUiThreadBlocked"] = during.Count > 1 ? during.Max() - during.Min() : 0;
        r["pill.morph.compositorDriven"] = during.Count > 1 && during.Max() - during.Min() > 40;
        r["pill.morph.peakWidth"] = samples.Max(s => s.w);
        var expanded = Native.Capture(x, y, PillSurface.W, PillSurface.H);
        expanded.SavePng(Path.Combine(outDir, "pill-2-expanded.png"));
        r["pill.morph.settledWidth"] = expanded.DarkRunWidth(row);

        // GPU evidence while animating.
        var (adapter, vram) = Gpu.DefaultAdapter();
        r["gpu.defaultAdapter"] = adapter;
        r["gpu.dedicatedVramMB"] = vram;
        var caps = new Microsoft.UI.Composition.CompositionCapabilities();
        r["composition.effectsSupported"] = caps.AreEffectsSupported();
        r["composition.effectsFast"] = caps.AreEffectsFast();
        using (var gpu = new Gpu.EngineSampler())
        {
            for (int i = 0; i < 6; i++) { pill.Morph(i % 2 == 1); await Task.Delay(350); }
            r["gpu.engineUtilizationPercentDuringMorphs"] = gpu.Read(Environment.ProcessId);
        }
        pill.Morph(false);
        await Task.Delay(800);
        pill.FitRegion(PillSurface.Collapsed);
        await Task.Delay(150);
        var final = Native.Capture(x, y, PillSurface.W, PillSurface.H);
        final.SavePng(Path.Combine(outDir, "pill-3-collapsed-again.png"));
        r["pill.collapsedAgainWidthPx"] = final.DarkRunWidth(row);
        pill.Surface.Close();
        pattern.Surface.Close();
        return r;
    }

    public static async Task<Dictionary<string, object?>> Dock(string outDir)
    {
        var r = new Dictionary<string, object?>();
        int x = (ScreenW - DockSurface.W) / 2, y = ScreenH - DockSurface.H - 64;
        var bounds = new RectInt32(x, y, DockSurface.W, DockSurface.H);
        var pattern = new PatternSurface(bounds);
        pattern.Surface.Show();
        var source = new PatternSurface(new RectInt32(40, ScreenH / 2 - 60, 200, 120), Palette.Rgb(255, 0, 200));
        source.Surface.Show();
        await Task.Delay(600);
        var background = Native.Capture(x, y, DockSurface.W, DockSurface.H);
        background.SavePng(Path.Combine(outDir, "dock-0-background.png"));

        var dock = new DockSurface(bounds);
        r["dock.showToFirstFrameMs"] = Math.Round(await FirstFrameAfter(dock.Surface.Show), 1);
        await Task.Delay(900);
        var shot = Native.Capture(x, y, DockSurface.W, DockSurface.H);
        shot.SavePng(Path.Combine(outDir, "dock-1-glass.png"));
        r["dock.transparency.matchAboveSlab"] = Math.Round(shot.MatchRatio(background, 200, 10, DockSurface.W - 220, DockSurface.SlabTop - 40), 4);

        int gx = (DockSurface.W - DockSurface.SlabW) / 2 + 12, gw = DockSurface.IconLeft(0) - gx - 8, gy = DockSurface.SlabTop + 12, gh = DockSurface.SlabH - 24;
        var (bMean, bStd) = background.LumaStats(gx, gy, gw, gh);
        var (sMean, sStd) = shot.LumaStats(gx, gy, gw, gh);
        r["dock.glass.backgroundLuma"] = new[] { Math.Round(bMean, 1), Math.Round(bStd, 1) };
        r["dock.glass.slabLuma"] = new[] { Math.Round(sMean, 1), Math.Round(sStd, 1) };
        r["dock.glass.contrastRetained"] = Math.Round(sStd / Math.Max(bStd, 0.01), 3);
        var (cMean, cStd) = shot.LumaStats(24, 22, 130, 28);
        r["dock.stockAcrylicChipLuma"] = new[] { Math.Round(cMean, 1), Math.Round(cStd, 1) };

        dock.Magnify(3, 1.6f);
        await Task.Delay(700);
        var mag = Native.Capture(x, y, DockSurface.W, DockSurface.H);
        mag.SavePng(Path.Combine(outDir, "dock-2-magnified.png"));
        int cx = DockSurface.IconLeft(3) + DockSurface.Icon / 2, oy = DockSurface.IconTop - 18;
        var (ob, og, or) = mag.At(cx, oy);
        var (bb, bg, br) = background.At(cx, oy);
        r["dock.magnify.overflowPixel"] = $"rgb({or},{og},{ob}) vs background rgb({br},{bg},{bb})";
        r["dock.magnify.overflowDrawn"] = Math.Abs(ob - bb) + Math.Abs(og - bg) + Math.Abs(or - br) > 60;

        dock.FitRegion(3, 1.6f);
        await Task.Delay(150);
        r["dock.hit.magnifiedIconOverflow"] = Native.RootAt(x + cx, y + oy) == dock.Surface.Hwnd;
        r["dock.hit.slab"] = Native.RootAt(x + (DockSurface.W - DockSurface.SlabW) / 2 + 20, y + DockSurface.SlabTop + 36) == dock.Surface.Hwnd;
        r["dock.hit.emptyAreaPassesThrough"] = Native.RootAt(x + DockSurface.W - 30, y + 20) == pattern.Surface.Hwnd;
        r["dock.hit.chipAreaPassesThrough"] = Native.RootAt(x + 60, y + 30) == pattern.Surface.Hwnd;
        var regioned = Native.Capture(x, y, DockSurface.W, DockSurface.H);
        regioned.SavePng(Path.Combine(outDir, "dock-3-region.png"));

        // DWM live thumbnail rendered inside the WinUI surface (window previews for dock and switcher).
        Native.SetWindowRgn(dock.Surface.Hwnd, IntPtr.Zero, true);
        int tx = DockSurface.W - 200, ty = 12, tw = 160, th = 96;
        int hr = Native.DwmRegisterThumbnail(dock.Surface.Hwnd, source.Surface.Hwnd, out IntPtr thumb);
        r["thumbnail.registerHr"] = hr;
        if (hr == 0)
        {
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8,
                rcDestination = new Native.RECT { Left = tx, Top = ty, Right = tx + tw, Bottom = ty + th },
                opacity = 255,
                fVisible = 1,
            };
            r["thumbnail.updateHr"] = Native.DwmUpdateThumbnailProperties(thumb, ref props);
            await Task.Delay(500);
            var th1 = Native.Capture(x, y, DockSurface.W, DockSurface.H);
            th1.SavePng(Path.Combine(outDir, "dock-4-thumbnail.png"));
            int magenta = 0, total = 0;
            for (int yy = ty + 8; yy < ty + th - 8; yy += 2)
                for (int xx = tx + 8; xx < tx + tw - 8; xx += 2)
                {
                    var (b, g, rr) = th1.At(xx, yy);
                    total++;
                    if (rr > 200 && g < 60 && b > 150) magenta++;
                }
            r["thumbnail.magentaCoverage"] = Math.Round((double)magenta / total, 3);
            Native.DwmUnregisterThumbnail(thumb);
        }
        dock.Surface.Close();
        source.Surface.Close();
        pattern.Surface.Close();
        return r;
    }

    public static async Task<Dictionary<string, object?>> AppBar(string outDir)
    {
        var r = new Dictionary<string, object?>();
        var before = Native.WorkArea();
        var bar = new BarSurface(new RectInt32(0, 0, ScreenW, 34));
        bar.Surface.Show();
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = bar.Surface.Hwnd, uCallbackMessage = 0x8000 + 77 };
        try
        {
            r["appbar.registered"] = Native.SHAppBarMessage(Native.ABM_NEW, ref abd) != UIntPtr.Zero;
            abd.uEdge = Native.ABE_TOP;
            abd.rc = new Native.RECT { Left = 0, Top = 0, Right = ScreenW, Bottom = 34 };
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
            abd.rc.Bottom = abd.rc.Top + 34;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
            bar.Surface.MoveAndResize(new RectInt32(abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, 34));
            await Task.Delay(900);
            var during = Native.WorkArea();
            Native.Capture(0, 0, Math.Min(ScreenW, 900), 34).SavePng(Path.Combine(outDir, "bar-glass.png"));
            r["appbar.workAreaTop"] = new[] { before.Top, during.Top };
            r["appbar.reservesSpace"] = during.Top == before.Top + 34;
        }
        finally
        {
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        }
        await Task.Delay(500);
        r["appbar.restoredWorkAreaTop"] = Native.WorkArea().Top;
        bar.Surface.Close();
        return r;
    }

    /// <summary>Shows bar, capsule and dock exactly as the shell would, then idles (or animates) for measurement.</summary>
    public static async Task Ambient(string outDir, int seconds, bool animate)
    {
        var bar = new BarSurface(new RectInt32(0, 0, ScreenW, 34));
        int px = (ScreenW - PillSurface.W) / 2;
        var pill = new PillSurface(new RectInt32(px, 0, PillSurface.W, PillSurface.H));
        int dx = (ScreenW - DockSurface.W) / 2;
        var dock = new DockSurface(new RectInt32(dx, ScreenH - DockSurface.H - 64, DockSurface.W, DockSurface.H));
        double firstFrame = await FirstFrameAfter(() => { bar.Surface.Show(); pill.Surface.Show(); dock.Surface.Show(); });
        pill.FitRegion(animate ? PillSurface.Expanded : PillSurface.Collapsed);
        dock.FitRegion(3, animate ? 1.6f : 1.0f);
        await Task.Delay(300);
        var ready = new Dictionary<string, object?>
        {
            ["processStartToFirstFrameMs"] = Math.Round((DateTime.Now - ProcessStart).TotalMilliseconds, 0),
            ["showToFirstFrameMs"] = Math.Round(firstFrame, 1),
            ["mode"] = animate ? "animate" : "idle",
        };
        File.WriteAllText(Path.Combine(outDir, animate ? "anim-ready.json" : "idle-ready.json"), System.Text.Json.JsonSerializer.Serialize(ready));
        var end = DateTime.UtcNow.AddSeconds(seconds);
        int i = 0;
        while (DateTime.UtcNow < end)
        {
            if (animate)
            {
                pill.Morph(i % 2 == 0);
                dock.Magnify(i % DockSurface.Count, 1.6f);
                if (i > 0) dock.Magnify((i - 1) % DockSurface.Count, 1.0f);
                i++;
                await Task.Delay(1000);
            }
            else await Task.Delay(500);
        }
        bar.Surface.Close();
        pill.Surface.Close();
        dock.Surface.Close();
    }
}
