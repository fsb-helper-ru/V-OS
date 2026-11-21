using System.Reflection;
using System.Text;

namespace S1;

/// <summary>Dumps the real API surface of the installed Windows App SDK for the types the shell depends on.</summary>
internal static class Probe
{
    private static readonly string[] Interesting =
    [
        "SystemBackdropElement", "SystemBackdrop", "ICompositionSupportsSystemBackdrop", "DesktopAcrylicBackdrop",
        "MicaBackdrop", "DesktopAcrylicController", "SystemBackdropConfiguration", "AppWindow", "OverlappedPresenter",
        "CompositionCapabilities", "Compositor", "SpringVector2NaturalMotionAnimation", "CompositionRoundedRectangleGeometry",
        "ElementCompositionPreview", "ContentExternalBackdropLink", "DesktopWindowXamlSource",
        "DesktopChildSiteBridge", "DesktopSiteBridge", "ContentIsland", "DesktopPopupSiteBridge", "Window",
    ];

    public static string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Process: {Environment.ProcessPath}");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            sb.AppendLine($"ASM {asm.GetName().Name} {asm.GetName().Version}");
        // Force-load projection assemblies that may not be loaded yet.
        foreach (var name in new[] { "Microsoft.WinUI", "Microsoft.Windows.SDK.NET", "Microsoft.InteractiveExperiences.Projection", "Microsoft.Graphics.Canvas.Interop" })
        {
            try { Assembly.Load(name); } catch (Exception ex) { sb.AppendLine($"load {name}: {ex.GetType().Name}"); }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetExportedTypes(); } catch { continue; }
            foreach (var t in types.Where(t => Interesting.Contains(t.Name)).OrderBy(t => t.FullName))
            {
                sb.AppendLine();
                sb.AppendLine($"TYPE {t.FullName}  (asm {asm.GetName().Name}) base={t.BaseType?.FullName}");
                foreach (var c in t.GetConstructors()) sb.AppendLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    sb.AppendLine($"  prop {(p.GetMethod?.IsStatic == true ? "static " : "")}{p.PropertyType.Name} {p.Name} {(p.CanWrite ? "{get;set;}" : "{get;}")}");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.NonPublic).Where(m => !m.IsSpecialName && (m.IsPublic || m.IsFamily)))
                    sb.AppendLine($"  {(m.IsStatic ? "static " : "")}{(m.IsFamily ? "protected " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) sb.AppendLine($"  event {e.EventHandlerType?.Name} {e.Name}");
            }
        }
        return sb.ToString();
    }
}
