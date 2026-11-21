using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using VOS.Core.Perf;

namespace VOS.UI.Quality;

/// <summary>The effective quality for this process. Views read it; the governor (shell) or settings update it.</summary>
public static class VisualQuality
{
    private static EffectiveQuality s_current = EffectiveQuality.Default;
    public static EffectiveQuality Current => s_current;
    public static event Action? Changed;

    public static void Set(EffectiveQuality q)
    {
        if (q == s_current) return;
        s_current = q;
        Changed?.Invoke();
    }

    /// <summary>Composition capability check: whether effects are supported and fast on this GPU/session.</summary>
    /// <summary>
    /// GPU effect capabilities. Only meaningful once a XAML window exists in the process: before that,
    /// CompositionCapabilities reports effects as "not fast" (a bare Compositor is not enough), so callers probe
    /// again after creating their first window.
    /// </summary>
    public static (bool supported, bool fast) Capabilities()
    {
        try
        {
            var caps = new CompositionCapabilities();
            return (caps.AreEffectsSupported(), caps.AreEffectsFast());
        }
        catch { return (true, true); }
    }
}

/// <summary>Compositor-driven motion helpers. Durations scale with <see cref="EffectiveQuality.MotionScale"/>; 0 means instant.</summary>
public static class Motion
{
    public static TimeSpan Scaled(double ms) => TimeSpan.FromMilliseconds(Math.Max(1, ms * Math.Max(0, VisualQuality.Current.MotionScale)));
    public static bool Enabled => VisualQuality.Current.MotionScale > 0.01;

    public static Visual VisualOf(UIElement e) => ElementCompositionPreview.GetElementVisual(e);

    public static void Fade(UIElement e, float to, double ms = 180, double delayMs = 0)
    {
        var v = VisualOf(e);
        if (!Enabled) { v.Opacity = to; return; }
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, Standard(v.Compositor));
        a.Duration = Scaled(ms);
        a.DelayTime = Scaled(delayMs);
        v.StartAnimation("Opacity", a);
    }

    /// <summary>Slides by an offset while fading in (used for panel entrances). Uses the Translation property.</summary>
    public static void Enter(UIElement e, Vector3 from, double ms = 260, double delayMs = 0)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var v = VisualOf(e);
        if (!Enabled) { v.Opacity = 1; v.Properties.InsertVector3("Translation", Vector3.Zero); return; }
        var c = v.Compositor;
        v.Opacity = 0;
        v.Properties.InsertVector3("Translation", from);
        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1f, Vector3.Zero, Decelerate(c));
        move.Duration = Scaled(ms);
        move.DelayTime = Scaled(delayMs);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, Decelerate(c));
        fade.Duration = Scaled(ms * 0.8);
        fade.DelayTime = Scaled(delayMs);
        v.StartAnimation("Translation", move);
        v.StartAnimation("Opacity", fade);
    }

    /// <summary>Spring to a scale (hover lift, press). Falls back to a short ease when springs are disabled.</summary>
    public static void ScaleTo(UIElement e, float scale, Vector3? center = null, float damping = 0.72f, double periodMs = 45)
    {
        var v = VisualOf(e);
        if (center is { } cp) v.CenterPoint = cp;
        var target = new Vector3(scale, scale, 1);
        if (!Enabled) { v.Scale = target; return; }
        if (VisualQuality.Current.Springs)
        {
            var s = v.Compositor.CreateSpringVector3Animation();
            s.FinalValue = target;
            s.DampingRatio = damping;
            s.Period = TimeSpan.FromMilliseconds(periodMs);
            v.StartAnimation("Scale", s);
        }
        else
        {
            var a = v.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1f, target, Standard(v.Compositor));
            a.Duration = Scaled(140);
            v.StartAnimation("Scale", a);
        }
    }

    public static void TranslateTo(UIElement e, Vector3 to, float damping = 0.75f, double periodMs = 50)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(e, true);
        var v = VisualOf(e);
        if (!Enabled) { v.Properties.InsertVector3("Translation", to); return; }
        if (VisualQuality.Current.Springs)
        {
            var s = v.Compositor.CreateSpringVector3Animation();
            s.FinalValue = to;
            s.DampingRatio = damping;
            s.Period = TimeSpan.FromMilliseconds(periodMs);
            v.StartAnimation("Translation", s);
        }
        else
        {
            var a = v.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1f, to, Standard(v.Compositor));
            a.Duration = Scaled(160);
            v.StartAnimation("Translation", a);
        }
    }

    /// <summary>Implicit offset animation: children glide to new layout positions (dock reorder, list changes).</summary>
    public static void EnableImplicitOffset(UIElement e, double ms = 260)
    {
        var v = VisualOf(e);
        if (!Enabled) { v.ImplicitAnimations = null; return; }
        var c = v.Compositor;
        var offset = c.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1f, "this.FinalValue", Decelerate(c));
        offset.Duration = Scaled(ms);
        var coll = c.CreateImplicitAnimationCollection();
        coll["Offset"] = offset;
        v.ImplicitAnimations = coll;
    }

    public static CompositionEasingFunction Standard(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
    public static CompositionEasingFunction Decelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
    public static CompositionEasingFunction Accelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
}
