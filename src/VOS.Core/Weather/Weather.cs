using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using VOS.Core.Settings;

namespace VOS.Core.Weather;

public enum Condition { Clear, MostlyClear, PartlyCloudy, Overcast, Fog, Drizzle, Rain, HeavyRain, FreezingRain, Snow, HeavySnow, Showers, SnowShowers, Thunderstorm, Hail }

public sealed class WeatherNow
{
    public DateTimeOffset Time { get; set; }
    public double TemperatureC { get; set; }
    public double ApparentC { get; set; }
    public int Humidity { get; set; }
    public double WindKmh { get; set; }
    public int WindDirection { get; set; }
    public double GustKmh { get; set; }
    public double PrecipitationMm { get; set; }
    public int Code { get; set; }
    public bool IsDay { get; set; } = true;
    public int CloudCover { get; set; }
    public double PressureHpa { get; set; }
    public double UvIndex { get; set; }
    public Condition Condition => WeatherCodes.Map(Code);
}

public sealed class HourlyPoint
{
    public DateTimeOffset Time { get; set; }
    public double TemperatureC { get; set; }
    public int PrecipProbability { get; set; }
    public double PrecipMm { get; set; }
    public int Code { get; set; }
    public double WindKmh { get; set; }
    public double GustKmh { get; set; }
    public bool IsDay { get; set; }
    public double UvIndex { get; set; }
}

public sealed class DailyPoint
{
    public DateTime Date { get; set; }
    public double MaxC { get; set; }
    public double MinC { get; set; }
    public int Code { get; set; }
    public double PrecipMm { get; set; }
    public int PrecipProbability { get; set; }
    public DateTimeOffset Sunrise { get; set; }
    public DateTimeOffset Sunset { get; set; }
    public double UvMax { get; set; }
    public double WindMaxKmh { get; set; }
    public double GustMaxKmh { get; set; }
}

public sealed class AirQuality
{
    public int? EuropeanAqi { get; set; }
    public int? UsAqi { get; set; }
    public double? Pm25 { get; set; }
    public double? Pm10 { get; set; }
}

public sealed record WeatherAdvisory(string Title, string Detail, string Severity);

public sealed class WeatherReport
{
    public WeatherLocation Location { get; set; } = new();
    public DateTimeOffset FetchedAt { get; set; }
    public string TimeZone { get; set; } = "";
    public int UtcOffsetSeconds { get; set; }
    public WeatherNow Now { get; set; } = new();
    public List<HourlyPoint> Hourly { get; set; } = [];
    public List<DailyPoint> Daily { get; set; } = [];
    public AirQuality? Air { get; set; }
    public bool Stale { get; set; }

    public DailyPoint? Today => Daily.FirstOrDefault();
    public IEnumerable<HourlyPoint> Next(int hours) => Hourly.Where(h => h.Time >= Now.Time.AddMinutes(-30)).Take(hours);

    /// <summary>Forecast-derived advisories. Open-Meteo has no official warnings; the UI labels these as unofficial.</summary>
    public List<WeatherAdvisory> Advisories()
    {
        var list = new List<WeatherAdvisory>();
        var next24 = Next(24).ToList();
        if (next24.Any(h => WeatherCodes.Map(h.Code) is Condition.Thunderstorm or Condition.Hail))
            list.Add(new("Thunderstorms expected", "Storm activity is forecast within the next 24 hours.", "warning"));
        double gust = next24.Count > 0 ? next24.Max(h => h.GustKmh) : 0;
        if (gust >= 70) list.Add(new("Strong wind gusts", $"Gusts up to {Math.Round(gust)} km/h are forecast.", gust >= 90 ? "severe" : "warning"));
        if (Today is { } t)
        {
            if (t.PrecipMm >= 20 && t.PrecipProbability >= 70) list.Add(new("Heavy rain likely", $"About {Math.Round(t.PrecipMm)} mm expected today.", "warning"));
            if (t.MaxC >= 35) list.Add(new("Extreme heat", $"Highs near {Math.Round(t.MaxC)} °C. Stay hydrated.", "warning"));
            if (t.MinC <= -15) list.Add(new("Severe cold", $"Lows near {Math.Round(t.MinC)} °C.", "warning"));
            if (t.UvMax >= 8) list.Add(new("Very high UV", $"UV index up to {Math.Round(t.UvMax)}. Use sun protection.", "info"));
            if (WeatherCodes.Map(t.Code) == Condition.HeavySnow) list.Add(new("Heavy snow", "Heavy snowfall is forecast today.", "warning"));
        }
        return list;
    }
}

public static class WeatherCodes
{
    public static Condition Map(int code) => code switch
    {
        0 => Condition.Clear,
        1 => Condition.MostlyClear,
        2 => Condition.PartlyCloudy,
        3 => Condition.Overcast,
        45 or 48 => Condition.Fog,
        51 or 53 or 55 => Condition.Drizzle,
        56 or 57 or 66 or 67 => Condition.FreezingRain,
        61 or 63 => Condition.Rain,
        65 => Condition.HeavyRain,
        71 or 73 or 77 => Condition.Snow,
        75 => Condition.HeavySnow,
        80 or 81 or 82 => Condition.Showers,
        85 or 86 => Condition.SnowShowers,
        95 => Condition.Thunderstorm,
        96 or 99 => Condition.Hail,
        _ => Condition.Overcast,
    };

    public static string Describe(Condition c, bool isDay = true) => c switch
    {
        Condition.Clear => isDay ? "Sunny" : "Clear",
        Condition.MostlyClear => isDay ? "Mostly sunny" : "Mostly clear",
        Condition.PartlyCloudy => "Partly cloudy",
        Condition.Overcast => "Cloudy",
        Condition.Fog => "Fog",
        Condition.Drizzle => "Drizzle",
        Condition.Rain => "Rain",
        Condition.HeavyRain => "Heavy rain",
        Condition.FreezingRain => "Freezing rain",
        Condition.Snow => "Snow",
        Condition.HeavySnow => "Heavy snow",
        Condition.Showers => "Showers",
        Condition.SnowShowers => "Snow showers",
        Condition.Thunderstorm => "Thunderstorm",
        Condition.Hail => "Thunderstorm with hail",
        _ => "Cloudy",
    };

}

public static class WeatherFormat
{
    public static double Temp(double c, TemperatureUnit u) => u == TemperatureUnit.Fahrenheit ? c * 9 / 5 + 32 : c;
    public static string Temperature(double c, TemperatureUnit u, bool unit = false) =>
        $"{Math.Round(Temp(c, u)).ToString("0", CultureInfo.CurrentCulture)}°{(unit ? (u == TemperatureUnit.Fahrenheit ? "F" : "C") : "")}";

    public static string Wind(double kmh, WindUnit u) => u switch
    {
        WindUnit.Mph => $"{Math.Round(kmh / 1.609344)} mph",
        WindUnit.Ms => $"{Math.Round(kmh / 3.6)} m/s",
        WindUnit.Knots => $"{Math.Round(kmh / 1.852)} kn",
        _ => $"{Math.Round(kmh)} km/h",
    };

    public static string Precip(double mm, PrecipUnit u) =>
        u == PrecipUnit.Inch ? $"{(mm / 25.4).ToString("0.00", CultureInfo.CurrentCulture)} in" : $"{mm.ToString("0.#", CultureInfo.CurrentCulture)} mm";

    public static string Compass(int degrees)
    {
        string[] d = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return d[(int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8];
    }
}

/// <summary>Open-Meteo client: forecast, geocoding and air quality. No key needed for non-commercial use.</summary>
public sealed class OpenMeteoClient
{
    private static readonly HttpClient s_http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("V-OS", AppInfo.Version));
        return c;
    }

    public async Task<WeatherReport> FetchAsync(WeatherLocation loc, string endpoint, string? apiKey, CancellationToken ct = default)
    {
        string inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        var url = $"{endpoint.TrimEnd('/')}?latitude={inv(loc.Latitude)}&longitude={inv(loc.Longitude)}" +
                  "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index" +
                  "&hourly=temperature_2m,precipitation_probability,precipitation,weather_code,wind_speed_10m,wind_gusts_10m,is_day,uv_index" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,uv_index_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max" +
                  "&timezone=auto&forecast_days=14&forecast_hours=48";
        if (!string.IsNullOrWhiteSpace(apiKey)) url += "&apikey=" + Uri.EscapeDataString(apiKey);
        using var resp = await s_http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var report = Parse(doc.RootElement, loc);
        report.Air = await TryFetchAirAsync(loc, ct).ConfigureAwait(false);
        return report;
    }

    internal static WeatherReport Parse(JsonElement root, WeatherLocation loc)
    {
        int offset = root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
        var span = TimeSpan.FromSeconds(offset);
        DateTimeOffset T(string s) => new(DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None), span);
        double D(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
        int I(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : 0;

        var r = new WeatherReport
        {
            Location = loc,
            FetchedAt = DateTimeOffset.Now,
            TimeZone = root.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "" : "",
            UtcOffsetSeconds = offset,
        };
        if (root.TryGetProperty("current", out var c))
        {
            r.Now = new WeatherNow
            {
                Time = T(c.GetProperty("time").GetString()!),
                TemperatureC = D(c.GetProperty("temperature_2m")),
                ApparentC = D(c.GetProperty("apparent_temperature")),
                Humidity = I(c.GetProperty("relative_humidity_2m")),
                WindKmh = D(c.GetProperty("wind_speed_10m")),
                WindDirection = I(c.GetProperty("wind_direction_10m")),
                GustKmh = c.TryGetProperty("wind_gusts_10m", out var g) ? D(g) : 0,
                PrecipitationMm = D(c.GetProperty("precipitation")),
                Code = I(c.GetProperty("weather_code")),
                IsDay = I(c.GetProperty("is_day")) == 1,
                CloudCover = I(c.GetProperty("cloud_cover")),
                PressureHpa = D(c.GetProperty("pressure_msl")),
                UvIndex = c.TryGetProperty("uv_index", out var uv) ? D(uv) : 0,
            };
        }
        if (root.TryGetProperty("hourly", out var h))
        {
            var times = h.GetProperty("time");
            for (int i = 0; i < times.GetArrayLength(); i++)
            {
                r.Hourly.Add(new HourlyPoint
                {
                    Time = T(times[i].GetString()!),
                    TemperatureC = D(h.GetProperty("temperature_2m")[i]),
                    PrecipProbability = I(h.GetProperty("precipitation_probability")[i]),
                    PrecipMm = D(h.GetProperty("precipitation")[i]),
                    Code = I(h.GetProperty("weather_code")[i]),
                    WindKmh = D(h.GetProperty("wind_speed_10m")[i]),
                    GustKmh = h.TryGetProperty("wind_gusts_10m", out var hg) ? D(hg[i]) : 0,
                    IsDay = I(h.GetProperty("is_day")[i]) == 1,
                    UvIndex = h.TryGetProperty("uv_index", out var hu) ? D(hu[i]) : 0,
                });
            }
        }
        if (root.TryGetProperty("daily", out var d))
        {
            var times = d.GetProperty("time");
            for (int i = 0; i < times.GetArrayLength(); i++)
            {
                r.Daily.Add(new DailyPoint
                {
                    Date = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture),
                    MaxC = D(d.GetProperty("temperature_2m_max")[i]),
                    MinC = D(d.GetProperty("temperature_2m_min")[i]),
                    Code = I(d.GetProperty("weather_code")[i]),
                    PrecipMm = D(d.GetProperty("precipitation_sum")[i]),
                    PrecipProbability = I(d.GetProperty("precipitation_probability_max")[i]),
                    Sunrise = T(d.GetProperty("sunrise")[i].GetString()!),
                    Sunset = T(d.GetProperty("sunset")[i].GetString()!),
                    UvMax = D(d.GetProperty("uv_index_max")[i]),
                    WindMaxKmh = D(d.GetProperty("wind_speed_10m_max")[i]),
                    GustMaxKmh = d.TryGetProperty("wind_gusts_10m_max", out var dg) ? D(dg[i]) : 0,
                });
            }
        }
        return r;
    }

    private static async Task<AirQuality?> TryFetchAirAsync(WeatherLocation loc, CancellationToken ct)
    {
        try
        {
            string inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            var url = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={inv(loc.Latitude)}&longitude={inv(loc.Longitude)}&current=european_aqi,us_aqi,pm2_5,pm10&timezone=auto";
            using var doc = JsonDocument.Parse(await s_http.GetStringAsync(url, ct).ConfigureAwait(false));
            var c = doc.RootElement.GetProperty("current");
            int? N(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;
            double? F(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            return new AirQuality { EuropeanAqi = N("european_aqi"), UsAqi = N("us_aqi"), Pm25 = F("pm2_5"), Pm10 = F("pm10") };
        }
        catch { return null; }
    }

    public async Task<List<WeatherLocation>> SearchAsync(string query, string language = "en", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=8&language={language}&format=json";
        using var doc = JsonDocument.Parse(await s_http.GetStringAsync(url, ct).ConfigureAwait(false));
        var list = new List<WeatherLocation>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            list.Add(new WeatherLocation
            {
                Name = r.GetProperty("name").GetString() ?? "",
                Region = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "",
                Country = r.TryGetProperty("country", out var co) ? co.GetString() ?? "" : "",
                Latitude = r.GetProperty("latitude").GetDouble(),
                Longitude = r.GetProperty("longitude").GetDouble(),
                TimeZone = r.TryGetProperty("timezone", out var t) ? t.GetString() : null,
            });
        }
        return list;
    }
}

/// <summary>Cached weather access shared by the shell and the Weather app (cache files in %LOCALAPPDATA%\V-OS\cache).</summary>
public sealed class WeatherService
{
    private readonly OpenMeteoClient _client = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static WeatherService Shared { get; } = new();

    private static string CachePath(WeatherLocation l) => Path.Combine(VosPaths.Cache, $"weather-{l.Key.Replace(',', '_')}.json");

    public WeatherReport? ReadCache(WeatherLocation loc)
    {
        var r = VosJson.TryDeserialize<WeatherReport>(AtomicFile.TryReadAllText(CachePath(loc)));
        if (r != null) r.Location = loc;
        return r;
    }

    /// <summary>Returns a fresh-enough report (cache within <paramref name="maxAge"/>), refreshing from the network otherwise.
    /// Falls back to the stale cache when offline.</summary>
    public async Task<WeatherReport?> GetAsync(WeatherLocation loc, WeatherSettings settings, TimeSpan maxAge, bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = ReadCache(loc);
            if (!force && cached != null && DateTimeOffset.Now - cached.FetchedAt < maxAge) return cached;
            try
            {
                var fresh = await _client.FetchAsync(loc, settings.ForecastEndpoint, settings.ApiKey, ct).ConfigureAwait(false);
                AtomicFile.WriteAllText(CachePath(loc), VosJson.Serialize(fresh, indented: false));
                return fresh;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Warn("Weather refresh failed; using cached data if available", ex);
                if (cached != null) cached.Stale = true;
                return cached;
            }
        }
        finally { _gate.Release(); }
    }

    public Task<List<WeatherLocation>> SearchAsync(string query, CancellationToken ct = default) =>
        _client.SearchAsync(query, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } l ? l : "en", ct);
}
