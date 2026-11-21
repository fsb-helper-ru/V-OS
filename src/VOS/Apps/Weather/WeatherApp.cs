using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Core.Weather;
using VOS.UI;
using VOS.UI.Theme;
using Windows.UI;

namespace VOS.Apps.Weather;

/// <summary>
/// V-OS Weather: the sky behind the window follows the conditions and the time of day at the location. Now, the next
/// 24 hours, 10 days, details and advisories from Open-Meteo, with the cache shown first and offline fallback.
/// </summary>
public sealed class WeatherApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly Grid _content = new();
    private readonly GradientStop _skyTop = new() { Offset = 0 }, _skyBottom = new() { Offset = 0.72 }, _skyFloor = new() { Offset = 1 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;
    private readonly Action<VosSettings> _settingsChanged;
    private WeatherReport? _report;
    private int _index;
    private bool _loading;
    private string _status = "";

    private static readonly SolidColorBrush W1 = new(Color.FromArgb(0xF5, 255, 255, 255)), W2 = new(Color.FromArgb(0xB8, 255, 255, 255)),
        W3 = new(Color.FromArgb(0x80, 255, 255, 255)), CardFill = new(Color.FromArgb(0x1C, 255, 255, 255)), CardRim = new(Color.FromArgb(0x24, 255, 255, 255)),
        Rain = new(Color.FromArgb(0xFF, 0x8C, 0xC8, 0xFF));

    private static WeatherSettings W => SettingsStore.Instance.Current.Weather;
    private static bool NetworkAllowed => W.Enabled && SettingsStore.Instance.Current.Privacy.WeatherNetworkConsent;

    public WeatherApp()
    {
        Frame = new AppFrame(VosApp.Find("weather")!, "Weather", 720, 540, 1120, 800);
        var sky = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0.25, 1) };
        sky.GradientStops.Add(_skyTop); sky.GradientStops.Add(_skyBottom); sky.GradientStops.Add(_skyFloor);
        SetSky(Condition.PartlyCloudy, true, animate: false);
        Frame.UseCustomBackground(sky);
        Frame.Body.Content = _content;
        _index = Math.Clamp(W.SelectedIndex, 0, Math.Max(0, (W.Locations?.Count ?? 1) - 1));

        _refresh = UiThread.Timer(TimeSpan.FromMinutes(Math.Max(10, W.RefreshMinutes)), () => _ = LoadAsync(force: false), repeating: true);
        _refresh.Start();
        _settingsChanged = changed => UiThread.Post(() => { _index = Math.Clamp(_index, 0, Math.Max(0, (W.Locations?.Count ?? 1) - 1)); _ = LoadAsync(force: false); });
        SettingsStore.Instance.Changed += _settingsChanged;
        Frame.Closed += () => { _refresh.Stop(); SettingsStore.Instance.Changed -= _settingsChanged; };
        Frame.Root.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 40) Render(); };
        _ = LoadAsync(force: false);
    }

    public void HandleArgs(AppArgs args)
    {
        if (args.Get("location") is { } name && W.Locations is { } list)
        {
            int i = list.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { _index = i; _ = LoadAsync(force: false); }
        }
    }

    private WeatherLocation? Location => W.Locations is { Count: > 0 } l ? l[Math.Clamp(_index, 0, l.Count - 1)] : null;

    private async Task LoadAsync(bool force)
    {
        var loc = Location;
        if (loc == null) { _report = null; Render(); return; }
        // Cached data first: the window is useful immediately, even offline.
        var cached = WeatherService.Shared.ReadCache(loc);
        if (cached != null && (_report == null || _report.Location.Key != loc.Key)) { _report = cached; Render(); }
        if (!NetworkAllowed) { _report = cached; _status = cached != null ? "Updates are off · showing saved data" : ""; Render(); return; }
        if (_loading) return;
        _loading = true;
        _status = "Updating…";
        RenderStatusOnly();
        try
        {
            var fresh = await WeatherService.Shared.GetAsync(loc, W, TimeSpan.FromMinutes(Math.Max(10, W.RefreshMinutes)), force);
            if (Location?.Key != loc.Key) return; // the user switched location meanwhile
            _report = fresh;
            _status = fresh == null ? "Weather is unavailable (offline?)" : fresh.Stale ? $"Offline · updated {Lower(Fmt.Relative(fresh.FetchedAt))}" : $"Updated {Lower(Fmt.Relative(fresh.FetchedAt))}";
        }
        catch (Exception ex)
        {
            Log.Warn("Weather load failed", ex);
            _status = "Weather is unavailable right now";
        }
        finally { _loading = false; }
        Render();
    }

    // ------------------------------------------------------------------ rendering

    private TextBlock? _statusText;

    private void RenderStatusOnly() { if (_statusText != null) _statusText.Text = _status; }

    private void Render()
    {
        _content.Children.Clear();
        var loc = Location;
        if (loc == null) { _content.Children.Add(AddFirstLocation()); SetSky(Condition.PartlyCloudy, IsDaytimeHere(), true); return; }
        if (_report == null)
        {
            _content.Children.Add(NetworkAllowed
                ? Centered(Kit.Stack(12, new ProgressRing { IsActive = true, Width = 32, Height = 32, Foreground = W1 }, T("Getting the forecast for " + loc.Display + "…", 14, W2)))
                : ConsentCard());
            return;
        }
        var r = _report;
        SetSky(r.Now.Condition, r.Now.IsDay, true);
        bool wide = Frame.Root.ActualWidth >= 940 || Frame.Root.ActualWidth == 0;

        var page = new StackPanel { Spacing = 14, MaxWidth = 1040, Padding = new Thickness(28, 4, 28, 28) };
        page.Children.Add(TopRow(loc));
        page.Children.Add(Hero(r));
        foreach (var a in r.Advisories()) page.Children.Add(Advisory(a));
        page.Children.Add(Hourly(r));
        if (wide)
        {
            var two = new Grid { ColumnSpacing = 14 };
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var daily = Daily(r);
            var details = Details(r, 2);
            Grid.SetColumn(details, 1);
            two.Children.Add(daily);
            two.Children.Add(details);
            page.Children.Add(two);
        }
        else
        {
            page.Children.Add(Daily(r));
            page.Children.Add(Details(r, 2));
        }
        page.Children.Add(T("Weather data by Open-Meteo.com (CC BY 4.0)", 11.5, W3));
        _content.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled });
    }

    private Grid TopRow(WeatherLocation loc)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var menu = new MenuFlyout();
        var list = W.Locations ?? [];
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            var item = new ToggleMenuFlyoutItem { Text = list[i].Display, IsChecked = i == _index };
            item.Click += (_, _) => { _index = index; _report = null; _ = LoadAsync(false); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Add or manage locations…", Icon = new FontIcon { Glyph = Glyphs.Settings } };
        manage.Click += (_, _) => AppHost.Open("settings", ["section=weather"]);
        menu.Items.Add(manage);
        var picker = new DropDownButton
        {
            Content = Kit.Row(8, new FontIcon { Glyph = Glyphs.Pin, FontSize = 14, Foreground = W1 }, T(loc.Display, 15, W1, FontWeights.SemiBold)),
            Flyout = menu,
            Background = new SolidColorBrush(Colors2.WithAlpha(Color.FromArgb(255, 255, 255, 255), 0x14)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6, 10, 6),
            Foreground = W1,
        };
        Buttons.AutomationName(picker, "Location");
        g.Children.Add(picker);
        _statusText = T(_status, 12.5, W2);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        var refresh = Buttons.Icon(Glyphs.Refresh, () => _ = LoadAsync(force: true), 34, 14, "Refresh");
        refresh.Foreground = W1;
        var right = Kit.Row(8, _statusText, refresh);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(right);
        return g;
    }

    private Grid Hero(WeatherReport r)
    {
        var unit = W.TemperatureUnit;
        var today = r.Today;
        var g = new Grid { ColumnSpacing = 22, Margin = new Thickness(4, 6, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new WeatherIcon(r.Now.Condition, r.Now.IsDay, 112, W1) { VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        var temp = new TextBlock { Text = WeatherFormat.Temperature(r.Now.TemperatureC, unit), FontSize = 92, FontFamily = Tokens.Display, FontWeight = FontWeights.Light, Foreground = W1, LineHeight = 96, Margin = new Thickness(-4, -12, 0, -6) };
        text.Children.Add(temp);
        text.Children.Add(T(WeatherCodes.Describe(r.Now.Condition, r.Now.IsDay), 20, W1, FontWeights.SemiBold));
        var line = $"Feels like {WeatherFormat.Temperature(r.Now.ApparentC, unit)}";
        if (today != null) line += $"   ·   H {WeatherFormat.Temperature(today.MaxC, unit)}  L {WeatherFormat.Temperature(today.MinC, unit)}";
        text.Children.Add(T(line, 14, W2));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    private Border Advisory(WeatherAdvisory a)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = a.Severity == "high" ? Glyphs.Warning : Glyphs.Info, FontSize = 18, Foreground = a.Severity == "high" ? new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xC8, 0x67)) : W1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var t = Kit.Stack(2, T(a.Title, 14, W1, FontWeights.SemiBold), Wrap(T(a.Detail, 13, W2)));
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        return Card(row);
    }

    private Border Hourly(WeatherReport r)
    {
        var unit = W.TemperatureUnit;
        var offset = TimeSpan.FromSeconds(r.UtcOffsetSeconds);
        bool h24 = SettingsStore.Instance.Current.Shell.Use24Hour;
        var hours = r.Next(25).ToList();
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (int i = 0; i < hours.Count; i++)
        {
            var h = hours[i];
            var local = h.Time.ToOffset(offset);
            var col = new StackPanel { Width = 58, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(Center(T(i == 0 ? "Now" : local.ToString(h24 ? "HH:mm" : "h tt", CultureInfo.CurrentCulture), 12.5, i == 0 ? W1 : W2, i == 0 ? FontWeights.SemiBold : FontWeights.Normal)));
            col.Children.Add(new WeatherIcon(WeatherCodes.Map(h.Code), h.IsDay, 28, W1) { HorizontalAlignment = HorizontalAlignment.Center });
            col.Children.Add(Center(T(WeatherFormat.Temperature(i == 0 ? r.Now.TemperatureC : h.TemperatureC, unit), 15, W1, FontWeights.SemiBold)));
            col.Children.Add(Center(T(h.PrecipProbability >= 20 ? $"{h.PrecipProbability}%" : " ", 11.5, Rain)));
            strip.Children.Add(col);
        }
        var scroll = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 0, 10) };
        return Card(Kit.Stack(12, Header(Glyphs.Clock, "Next 24 hours"), scroll));
    }

    private Border Daily(WeatherReport r)
    {
        var unit = W.TemperatureUnit;
        var days = r.Daily.Take(10).ToList();
        double lo = days.Count > 0 ? days.Min(d => d.MinC) : 0, hi = days.Count > 0 ? days.Max(d => d.MaxC) : 1;
        double span = Math.Max(1, hi - lo);
        var list = new StackPanel { Spacing = 2 };
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var row = new Grid { ColumnSpacing = 12, Height = 40 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            Add(row, T(i == 0 ? "Today" : Capitalize(d.Date.ToString("ddd", CultureInfo.CurrentCulture)), 14, W1, i == 0 ? FontWeights.SemiBold : FontWeights.Normal), 0);
            Add(row, new WeatherIcon(WeatherCodes.Map(d.Code), true, 24, W1), 1);
            Add(row, T(d.PrecipProbability >= 20 ? $"{d.PrecipProbability}%" : "", 12, Rain), 2);
            var min = T(WeatherFormat.Temperature(d.MinC, unit), 14, W2); min.HorizontalAlignment = HorizontalAlignment.Right; Add(row, min, 3);
            Add(row, RangeBar((d.MinC - lo) / span, (d.MaxC - lo) / span, i == 0 && _report != null ? (_report.Now.TemperatureC - lo) / span : null), 4);
            Add(row, T(WeatherFormat.Temperature(d.MaxC, unit), 14, W1, FontWeights.SemiBold), 5);
            list.Children.Add(row);
            if (i < days.Count - 1) list.Children.Add(new Border { Height = 1, Background = CardRim, Margin = new Thickness(0, 0, 0, 0) });
        }
        return Card(Kit.Stack(10, Header(Glyphs.Calendar, $"{days.Count}-day forecast"), list));

        static void Add(Grid g, FrameworkElement e, int col) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }
    }

    /// <summary>Temperature range bar: the day's min–max inside the period's min–max, with "now" as a dot.</summary>
    private static Grid RangeBar(double from, double to, double? now)
    {
        from = Math.Clamp(from, 0, 1); to = Math.Clamp(Math.Max(to, from + 0.02), 0, 1);
        var g = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(from, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(to - from, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - to, GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 0),
                GradientStops = { new GradientStop { Color = TempColor(from), Offset = 0 }, new GradientStop { Color = TempColor(to), Offset = 1 } },
            },
        };
        Grid.SetColumn(fill, 1);
        g.Children.Add(fill);
        if (now is double n)
        {
            var dotHost = new Grid { ColumnSpacing = 0 };
            dotHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(n, 0, 1), GridUnitType.Star) });
            dotHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dotHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(n, 0, 1), GridUnitType.Star) });
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = W1, BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderThickness = new Thickness(1), Margin = new Thickness(-4, -1, -4, -1) };
            Grid.SetColumn(dot, 1);
            dotHost.Children.Add(dot);
            Grid.SetColumnSpan(dotHost, 3);
            g.Children.Add(dotHost);
        }
        return g;
    }

    private static Color TempColor(double t) => t switch
    {
        < 0.25 => Colors2.Mix(Colors2.Hex("#6FB4FF"), Colors2.Hex("#62D2E8"), t / 0.25),
        < 0.5 => Colors2.Mix(Colors2.Hex("#62D2E8"), Colors2.Hex("#9BE0A0"), (t - 0.25) / 0.25),
        < 0.75 => Colors2.Mix(Colors2.Hex("#9BE0A0"), Colors2.Hex("#FFC867"), (t - 0.5) / 0.25),
        _ => Colors2.Mix(Colors2.Hex("#FFC867"), Colors2.Hex("#FF8A5B"), Math.Min(1, (t - 0.75) / 0.25)),
    };

    private Grid Details(WeatherReport r, int columns)
    {
        var now = r.Now;
        var today = r.Today;
        var offset = TimeSpan.FromSeconds(r.UtcOffsetSeconds);
        bool h24 = SettingsStore.Instance.Current.Shell.Use24Hour;
        string Time(DateTimeOffset t) => t.ToOffset(offset).ToString(h24 ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        var tiles = new List<Border>
        {
            Tile(Glyphs.Flow, "Wind", WeatherFormat.Wind(now.WindKmh, W.WindUnit), $"{WeatherFormat.Compass(now.WindDirection)} · gusts {WeatherFormat.Wind(now.GustKmh, W.WindUnit)}"),
            Tile(Glyphs.Weather, "Humidity", $"{now.Humidity}%", $"Cloud cover {now.CloudCover}%"),
            Tile(Glyphs.Sun, "UV index", $"{Math.Round(now.UvIndex):0}", UvLabel(today?.UvMax ?? now.UvIndex) + (today != null ? $" · max {today.UvMax:0}" : "")),
            Tile(Glyphs.Gauge, "Pressure", $"{now.PressureHpa:0} hPa", now.PressureHpa switch { < 1000 => "Low", > 1022 => "High", _ => "Normal" }),
        };
        if (today != null)
        {
            tiles.Add(Tile(Glyphs.Sun, "Sunrise", Time(today.Sunrise), $"Sunset {Time(today.Sunset)}"));
            tiles.Add(Tile(Glyphs.Weather, "Precipitation", WeatherFormat.Precip(today.PrecipMm, W.PrecipUnit), today.PrecipProbability > 0 ? $"{today.PrecipProbability}% chance today" : "None expected today"));
        }
        tiles.Add(Tile(Glyphs.Info, "Feels like", WeatherFormat.Temperature(now.ApparentC, W.TemperatureUnit),
            now.ApparentC < now.TemperatureC - 1.5 ? "Wind makes it feel colder" : now.ApparentC > now.TemperatureC + 1.5 ? "Humidity makes it feel warmer" : "Similar to the actual temperature"));
        if (r.Air is { } air && (air.EuropeanAqi ?? air.UsAqi) is int aqi)
            tiles.Add(Tile(Glyphs.Leaf, "Air quality", $"{aqi}", AqiLabel(air.EuropeanAqi, air.UsAqi) + (air.Pm25 is double pm ? $" · PM2.5 {pm:0}" : "")));

        var g = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        for (int c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i / columns >= g.RowDefinitions.Count) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(tiles[i], i % columns);
            Grid.SetRow(tiles[i], i / columns);
            g.Children.Add(tiles[i]);
        }
        return g;
    }

    private static string UvLabel(double uv) => uv switch { < 3 => "Low", < 6 => "Moderate", < 8 => "High", < 11 => "Very high", _ => "Extreme" };

    private static string AqiLabel(int? eu, int? us) => eu is int e
        ? e switch { <= 20 => "Good", <= 40 => "Fair", <= 60 => "Moderate", <= 80 => "Poor", <= 100 => "Very poor", _ => "Extremely poor" }
        : us is int u ? u switch { <= 50 => "Good", <= 100 => "Moderate", <= 150 => "Unhealthy for some", <= 200 => "Unhealthy", _ => "Very unhealthy" } : "";

    private static Border Tile(string glyph, string caption, string value, string detail) =>
        Card(Kit.Stack(6,
            Kit.Row(6, new FontIcon { Glyph = glyph, FontSize = 12, Foreground = W2 }, T(caption.ToUpperInvariant(), 11.5, W2, FontWeights.SemiBold)),
            new TextBlock { Text = value, FontSize = 26, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = W1 },
            Wrap(T(detail, 12.5, W2))), new Thickness(16, 14, 16, 14));

    // ------------------------------------------------------------------ first run, consent

    private UIElement AddFirstLocation()
    {
        var results = new StackPanel { Spacing = 2 };
        var box = new TextBox { PlaceholderText = "City or place", Width = 300 };
        var search = Buttons.Accent("Search", null);
        async void Search()
        {
            var q = box.Text.Trim();
            if (q.Length < 2) return;
            if (!NetworkAllowed) SettingsStore.Instance.Update(s => { s.Privacy.WeatherNetworkConsent = true; s.Weather.Enabled = true; });
            results.Children.Clear();
            results.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, Foreground = W1 });
            try
            {
                var found = await WeatherService.Shared.SearchAsync(q);
                results.Children.Clear();
                if (found.Count == 0) results.Children.Add(T("No places found.", 13, W2));
                foreach (var place in found)
                {
                    var p = place;
                    var row = Kit.ListRow(new FontIcon { Glyph = Glyphs.Pin, FontSize = 14, Foreground = W2 }, p.Display, $"{p.Country} · {p.Latitude:0.00}, {p.Longitude:0.00}", null, () =>
                    {
                        SettingsStore.Instance.Update(s => { s.Weather.Locations ??= []; if (s.Weather.Locations.All(z => z.Key != p.Key)) s.Weather.Locations.Add(p); s.Weather.SelectedIndex = s.Weather.Locations.FindIndex(z => z.Key == p.Key); });
                        _index = W.Locations!.FindIndex(z => z.Key == p.Key);
                        _ = LoadAsync(false);
                    });
                    row.Foreground = W1;
                    results.Children.Add(row);
                }
            }
            catch { results.Children.Clear(); results.Children.Add(T("Search is unavailable right now (offline?).", 13, W2)); }
        }
        search.Click += (_, _) => Search();
        Kit.OnEnter(box, Search);
        var card = Card(Kit.Stack(14,
            new WeatherIcon(Condition.PartlyCloudy, IsDaytimeHere(), 64, W1),
            T("Where are you?", 24, W1, FontWeights.SemiBold),
            Wrap(T("Add a place to see its forecast. Only the place's coordinates are sent to Open-Meteo; no account and no tracking.", 13.5, W2)),
            Kit.Row(8, box, search),
            results), new Thickness(28));
        card.MaxWidth = 520;
        return Centered(card);
    }

    private UIElement ConsentCard()
    {
        var card = Card(Kit.Stack(12,
            T("Weather updates are off", 20, W1, FontWeights.SemiBold),
            Wrap(T("V-OS sends only the coordinates of your saved places to Open-Meteo to get forecasts. Nothing else leaves this PC.", 13.5, W2)),
            Buttons.Accent("Turn on weather", () => SettingsStore.Instance.Update(s => { s.Privacy.WeatherNetworkConsent = true; s.Weather.Enabled = true; }))), new Thickness(28));
        card.MaxWidth = 480;
        return Centered(card);
    }

    // ------------------------------------------------------------------ sky

    private void SetSky(Condition c, bool day, bool animate)
    {
        var (top, bottom) = Sky(c, day);
        var floor = Colors2.Darken(bottom, 0.35);
        if (!animate || !UI.Quality.Motion.Enabled) { _skyTop.Color = top; _skyBottom.Color = bottom; _skyFloor.Color = floor; return; }
        var sb = new Storyboard();
        void Anim(GradientStop stop, Color to)
        {
            var a = new ColorAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(900)), EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            Storyboard.SetTarget(a, stop);
            Storyboard.SetTargetProperty(a, "Color");
            sb.Children.Add(a);
        }
        Anim(_skyTop, top); Anim(_skyBottom, bottom); Anim(_skyFloor, floor);
        sb.Begin();
    }

    private static (Color top, Color bottom) Sky(Condition c, bool day)
    {
        (string a, string b) = c switch
        {
            Condition.Clear or Condition.MostlyClear => day ? ("#2D6BD0", "#5FA3EC") : ("#0A1430", "#1C355F"),
            Condition.PartlyCloudy => day ? ("#3767AA", "#7EA3CF") : ("#0F182B", "#283752"),
            Condition.Overcast or Condition.Fog => day ? ("#4A586B", "#7D8A9C") : ("#171C24", "#2F3743"),
            Condition.Drizzle or Condition.Rain or Condition.Showers or Condition.FreezingRain => day ? ("#334B68", "#56708E") : ("#101823", "#223043"),
            Condition.HeavyRain or Condition.Thunderstorm or Condition.Hail => ("#1A202C", "#363F52"),
            Condition.Snow or Condition.HeavySnow or Condition.SnowShowers => day ? ("#536C8A", "#97ABC2") : ("#18202D", "#36435A"),
            _ => ("#293850", "#465A7A"),
        };
        return (Colors2.Hex(a), Colors2.Hex(b));
    }

    private static bool IsDaytimeHere() => DateTime.Now.Hour is >= 7 and < 19;
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ helpers

    private static TextBlock T(string text, double size, Brush brush, Windows.UI.Text.FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal, FontFamily = Tokens.Text, TextTrimming = TextTrimming.CharacterEllipsis };

    private static TextBlock Wrap(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t; }
    private static TextBlock Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; return t; }

    private static StackPanel Header(string glyph, string text) =>
        Kit.Row(8, new FontIcon { Glyph = glyph, FontSize = 13, Foreground = W2 }, T(text.ToUpperInvariant(), 11.5, W2, FontWeights.SemiBold));

    private static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Child = child,
        Padding = padding ?? new Thickness(18, 14, 18, 14),
        Background = CardFill,
        BorderBrush = CardRim,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Tokens.RadiusCard),
    };

    private static Grid Centered(UIElement e)
    {
        if (e is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
        return new Grid { Children = { e }, Padding = new Thickness(24) };
    }
}
