using VOS.Core.Settings;

namespace VOS.Core.Perf;

public enum HardwareTier { Low, Medium, High }

/// <summary>Everything the adaptive-quality pipeline needs to know about the machine and its current state.</summary>
public sealed record HardwareSnapshot
{
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    public double RamGb { get; init; } = 8;
    public string GpuName { get; init; } = "";
    public long GpuDedicatedMb { get; init; }
    public bool GpuIsSoftware { get; init; }
    public bool EffectsSupported { get; init; } = true;
    public bool EffectsFast { get; init; } = true;
    public bool HasBattery { get; init; }
    public bool OnBattery { get; init; }
    public bool BatterySaver { get; init; }
    public bool RemoteSession { get; init; }
    public int MonitorCount { get; init; } = 1;
    public long TotalPixels { get; init; } = 1920L * 1080;
    public int MaxRefreshHz { get; init; } = 60;
    public bool WindowsAnimations { get; init; } = true;
    public bool WindowsTransparency { get; init; } = true;
    public bool HighContrast { get; init; }
}

/// <summary>Concrete effect parameters every visual component reads. Profiles change these, never just labels.</summary>
public sealed record EffectiveQuality
{
    public PerformanceProfile Profile { get; init; }
    public HardwareTier Tier { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Acrylic glass on shell surfaces; otherwise tinted solid surfaces.</summary>
    public bool Glass { get; init; }
    /// <summary>Mica on app windows; otherwise solid window background.</summary>
    public bool Mica { get; init; }
    /// <summary>0 = no motion (instant/crossfade), 1 = full motion.</summary>
    public double MotionScale { get; init; }
    public bool Springs { get; init; }
    public bool Ambient { get; init; }
    public bool AmbientAnimated { get; init; }
    public int ParticleFps { get; init; }
    public double Particles { get; init; }
    public bool Lights { get; init; }
    public bool Shadows { get; init; }
    public bool LiveThumbnails { get; init; }
    public bool DockMagnification { get; init; }
    public bool AudioVisualizer { get; init; }
    public int WeatherRefreshMinutes { get; init; }
    public bool BackgroundIndexing { get; init; }

    public static EffectiveQuality Default { get; } = QualityResolver.Resolve(PerformanceProfile.Balanced, new HardwareSnapshot());
}

public static class QualityResolver
{
    public static HardwareTier Classify(HardwareSnapshot h)
    {
        if (h.GpuIsSoftware || !h.EffectsSupported || !h.EffectsFast) return HardwareTier.Low;
        int score = 0;
        score += h.LogicalCores >= 8 ? 2 : h.LogicalCores >= 4 ? 1 : 0;
        score += h.RamGb >= 15 ? 2 : h.RamGb >= 7.5 ? 1 : 0;
        score += h.GpuDedicatedMb >= 3500 ? 2 : h.GpuDedicatedMb >= 1500 ? 1 : 0;
        if (h.TotalPixels > 8_300_000 && h.GpuDedicatedMb < 3500) score -= 1; // 4K+ on modest GPUs
        return score >= 5 ? HardwareTier.High : score >= 3 ? HardwareTier.Medium : HardwareTier.Low;
    }

    public static PerformanceProfile ResolveAdaptive(HardwareSnapshot h, HardwareTier tier, out string reason)
    {
        if (h.RemoteSession) { reason = "Remote session"; return PerformanceProfile.Minimal; }
        if (h.BatterySaver) { reason = "Battery saver is on"; return PerformanceProfile.Performance; }
        switch (tier)
        {
            case HardwareTier.High:
                reason = h.OnBattery ? "Capable hardware on battery" : "Capable hardware";
                return h.OnBattery ? PerformanceProfile.Balanced : PerformanceProfile.Quality;
            case HardwareTier.Medium:
                reason = h.OnBattery ? "Mainstream hardware on battery" : "Mainstream hardware";
                return h.OnBattery ? PerformanceProfile.Performance : PerformanceProfile.Balanced;
            default:
                reason = h.GpuIsSoftware || !h.EffectsFast ? "Graphics acceleration is limited" : "Entry-level hardware";
                return PerformanceProfile.Performance;
        }
    }

    public static EffectiveQuality Resolve(PerformanceProfile requested, HardwareSnapshot h, PerformanceSettings? overrides = null, bool reduceMotion = false)
    {
        var tier = Classify(h);
        string reason = "Chosen in Settings";
        var profile = requested == PerformanceProfile.Adaptive ? ResolveAdaptive(h, tier, out reason) : requested;

        var q = profile switch
        {
            PerformanceProfile.Quality => new EffectiveQuality
            {
                Glass = true, Mica = true, MotionScale = 1, Springs = true, Ambient = true, AmbientAnimated = true, ParticleFps = 60,
                Particles = 1, Lights = true, Shadows = true, LiveThumbnails = true, DockMagnification = true, AudioVisualizer = true,
                WeatherRefreshMinutes = 20, BackgroundIndexing = true,
            },
            PerformanceProfile.Balanced => new EffectiveQuality
            {
                Glass = true, Mica = true, MotionScale = 1, Springs = true, Ambient = true, AmbientAnimated = true, ParticleFps = 30,
                Particles = 0.5, Lights = true, Shadows = true, LiveThumbnails = true, DockMagnification = true, AudioVisualizer = true,
                WeatherRefreshMinutes = 30, BackgroundIndexing = true,
            },
            PerformanceProfile.Performance => new EffectiveQuality
            {
                Glass = false, Mica = true, MotionScale = 0.6, Springs = false, Ambient = true, AmbientAnimated = false, ParticleFps = 0,
                Particles = 0, Lights = false, Shadows = false, LiveThumbnails = true, DockMagnification = false, AudioVisualizer = false,
                WeatherRefreshMinutes = 60, BackgroundIndexing = true,
            },
            _ => new EffectiveQuality
            {
                Glass = false, Mica = false, MotionScale = 0, Springs = false, Ambient = false, AmbientAnimated = false, ParticleFps = 0,
                Particles = 0, Lights = false, Shadows = false, LiveThumbnails = false, DockMagnification = false, AudioVisualizer = false,
                WeatherRefreshMinutes = 120, BackgroundIndexing = false,
            },
        };

        // Respect Windows accessibility and personalization preferences.
        if (!h.WindowsAnimations || reduceMotion) q = q with { MotionScale = 0, Springs = false, AmbientAnimated = false, ParticleFps = 0, Particles = 0 };
        if (!h.WindowsTransparency) q = q with { Glass = false, Mica = false };
        if (h.HighContrast) q = q with { Glass = false, Mica = false, Ambient = false, AmbientAnimated = false, Lights = false, Shadows = false, Particles = 0 };
        if (h.BatterySaver) q = q with { AmbientAnimated = false, ParticleFps = 0, Particles = 0, AudioVisualizer = false };

        if (overrides != null)
        {
            if (overrides.Backdrops is bool b) q = q with { Glass = b && !h.HighContrast, Mica = b && !h.HighContrast };
            if (overrides.Animations is bool a) q = q with { MotionScale = a ? Math.Max(q.MotionScale, 1) : 0, Springs = a && q.Springs };
            if (overrides.Ambient is bool am) q = q with { Ambient = am, AmbientAnimated = am && q.AmbientAnimated };
            if (overrides.LiveThumbnails is bool lt) q = q with { LiveThumbnails = lt };
            if (overrides.AudioVisualizer is bool av) q = q with { AudioVisualizer = av };
            if (overrides.AmbientFps is int fps) q = q with { ParticleFps = Math.Clamp(fps, 0, 120) };
        }
        return q with { Profile = profile, Tier = tier, Reason = reason };
    }
}
