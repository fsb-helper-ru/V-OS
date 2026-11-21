using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VOS.Core.Weather;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace VOS.UI;

/// <summary>
/// V-OS weather icons: vector line icons drawn on a 24-unit grid in the same stroke language as the system icons
/// (the Windows icon font has no weather set). Stroke weight is tuned per size so large icons stay elegant.
/// </summary>
public sealed class WeatherIcon : Grid
{
    private const string Cloud = "M7.2 18.6h10.1a3.9 3.9 0 0 0 .55-7.76A5.4 5.4 0 0 0 7.5 9.4a4.6 4.6 0 0 0-.3 9.2z";
    private const string CloudHigh = "M7.2 14.6h10.1a3.9 3.9 0 0 0 .55-7.76A5.4 5.4 0 0 0 7.5 5.4a4.6 4.6 0 0 0-.3 9.2z";
    private const string Sun = "M12 7.6a4.4 4.4 0 1 0 0 8.8 4.4 4.4 0 0 0 0-8.8zM12 2.4v2.1M12 19.5v2.1M21.6 12h-2.1M4.5 12H2.4M18.8 5.2l-1.5 1.5M6.7 17.3l-1.5 1.5M18.8 18.8l-1.5-1.5M6.7 6.7 5.2 5.2";
    private const string Moon = "M15.6 3.6a8.6 8.6 0 1 0 4.9 14.9A7.2 7.2 0 0 1 15.6 3.6z";
    private const string SmallSun = "M8.6 5.6a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM8.6 1.8v1.4M3.4 8.6H2M4.9 4.9 3.9 3.9M12.3 4.9l1-1";
    private const string SmallMoon = "M10.6 2.6a5.4 5.4 0 1 0 3 9.6 4.6 4.6 0 0 1-3-9.6z";
    private const string CloudLowRight = "M9.2 20.2h9.3a3.6 3.6 0 0 0 .5-7.15 5 5 0 0 0-9.6-1.3 4.3 4.3 0 0 0-.2 8.45z";
    private const string Rain = "M8.4 17.2l-1.2 3.4M12.4 17.2l-1.2 3.4M16.4 17.2l-1.2 3.4";
    private const string HeavyRain = "M7.4 17.2l-1.6 4.4M10.9 17.2l-1.6 4.4M14.4 17.2l-1.6 4.4M17.9 17.2l-1.6 4.4";
    private const string Drizzle = "M8.6 18.2v.6M12 19.4v.6M15.4 18.2v.6M10.3 21.4v.6M13.7 21.4v.6";
    private const string Snow = "M8.5 17.6v3.2M6.9 19.2h3.2M15.5 17.6v3.2M13.9 19.2h3.2M12 20.4v2M11 21.4h2";
    private const string Sleet = "M8.4 17.2l-1.2 3.4M15.5 17.6v3.2M13.9 19.2h3.2M11.5 20.6v.6";
    private const string Bolt = "M12.9 14.6l-2.6 4h3.2l-2.3 4.2";
    private const string Fog = "M4 12.5h16M6 16h12M4 19.5h16M7.2 9h9.6";

    public WeatherIcon(Condition condition, bool isDay, double size, Brush? brush = null)
    {
        Width = Height = size;
        var stroke = brush ?? Ui.B("Vos.TextPrimary");
        double thickness = 1.55 * Math.Pow(24 / Math.Max(12, size), 0.4);
        var canvas = new Canvas { Width = 24, Height = 24 };
        void Add(string data) => canvas.Children.Add(new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        switch (condition)
        {
            case Condition.Clear: Add(isDay ? Sun : Moon); break;
            case Condition.MostlyClear:
            case Condition.PartlyCloudy: Add(isDay ? SmallSun : SmallMoon); Add(CloudLowRight); break;
            case Condition.Overcast: Add(Cloud); break;
            case Condition.Fog: Add(Fog); break;
            case Condition.Drizzle: Add(CloudHigh); Add(Drizzle); break;
            case Condition.Rain:
            case Condition.Showers: Add(CloudHigh); Add(Rain); break;
            case Condition.HeavyRain: Add(CloudHigh); Add(HeavyRain); break;
            case Condition.FreezingRain: Add(CloudHigh); Add(Sleet); break;
            case Condition.Snow:
            case Condition.SnowShowers:
            case Condition.HeavySnow: Add(CloudHigh); Add(Snow); break;
            case Condition.Thunderstorm:
            case Condition.Hail: Add(CloudHigh); Add(Bolt); break;
            default: Add(Cloud); break;
        }
        Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, WeatherCodes.Describe(condition, isDay));
    }
}
