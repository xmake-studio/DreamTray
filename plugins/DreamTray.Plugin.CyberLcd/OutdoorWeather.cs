using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DreamTray.Plugins.CyberLcd;

internal readonly record struct WeatherLocation(string Name, double Latitude, double Longitude)
{
    public string Display => $"{Name} ({Latitude:F3}, {Longitude:F3})";
    public override string ToString() => Display;
}

internal static class OutdoorWeather
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<List<WeatherLocation>> SearchAsync(string query, CancellationToken token = default)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query.Trim())
            + "&count=8&language=en&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, token));
        var result = new List<WeatherLocation>();
        if (!doc.RootElement.TryGetProperty("results", out var places)) return result;
        foreach (var place in places.EnumerateArray())
        {
            string name = place.GetProperty("name").GetString() ?? "";
            if (place.TryGetProperty("admin1", out var region) && !string.IsNullOrWhiteSpace(region.GetString()))
                name += ", " + region.GetString();
            if (place.TryGetProperty("country", out var country)) name += ", " + country.GetString();
            result.Add(new WeatherLocation(name, place.GetProperty("latitude").GetDouble(),
                place.GetProperty("longitude").GetDouble()));
        }
        return result;
    }

    public static async Task<float?> ReadCelsiusAsync(WeatherLocation location, CancellationToken token)
    {
        foreach (var provider in new Func<WeatherLocation, CancellationToken, Task<float?>>[]
                 { MetarAsync, OpenMeteoAsync, WttrAsync, SevenTimerAsync })
        {
            try
            {
                float? value = await provider(location, token);
                if (value is >= -90 and <= 60) return value;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* Try the next independent public provider. */ }
        }
        return null;
    }

    private static string Coord(double value) => value.ToString("F5", CultureInfo.InvariantCulture);

    /// <summary>Airport stations farther than this are not representative of the chosen place.</summary>
    private const double MetarRadiusKm = 60;

    /// <summary>METARs are issued every 30-60 minutes; anything older is stale.</summary>
    private static readonly TimeSpan MetarMaxAge = TimeSpan.FromMinutes(90);

    /// <summary>
    /// Measured temperature from nearby airport METARs (aviationweather.gov).
    /// Model providers like Open-Meteo are forecasts and miss local events such
    /// as rain showers by several degrees, so real observations come first.
    /// Stations within <see cref="MetarRadiusKm"/> are blended by inverse squared
    /// distance, so a city between several airports gets a sensible value.
    /// </summary>
    private static async Task<float?> MetarAsync(WeatherLocation place, CancellationToken token)
    {
        double dLat = MetarRadiusKm / 111.0;
        double dLon = MetarRadiusKm / (111.0 * Math.Max(0.1, Math.Cos(place.Latitude * Math.PI / 180)));
        string bbox = string.Join(",",
            Coord(Math.Max(-90, place.Latitude - dLat)), Coord(Math.Max(-180, place.Longitude - dLon)),
            Coord(Math.Min(90, place.Latitude + dLat)), Coord(Math.Min(180, place.Longitude + dLon)));
        string body = await Http.GetStringAsync("https://aviationweather.gov/api/data/metar?format=json&bbox=" + bbox, token);
        if (string.IsNullOrWhiteSpace(body)) return null;

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double weightSum = 0, valueSum = 0;
        foreach (var station in doc.RootElement.EnumerateArray())
        {
            if (!station.TryGetProperty("temp", out var temp) || temp.ValueKind != JsonValueKind.Number) continue;
            if (!station.TryGetProperty("obsTime", out var obs) || !obs.TryGetInt64(out long obsTime)) continue;
            if (now - obsTime > MetarMaxAge.TotalSeconds) continue;
            if (!station.TryGetProperty("lat", out var lat) || !station.TryGetProperty("lon", out var lon)) continue;

            double km = DistanceKm(place.Latitude, place.Longitude, lat.GetDouble(), lon.GetDouble());
            if (km > MetarRadiusKm) continue;
            double weight = 1 / Math.Max(1, km * km);
            weightSum += weight;
            valueSum += weight * temp.GetDouble();
        }
        return weightSum > 0 ? (float)(valueSum / weightSum) : null;
    }

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371, rad = Math.PI / 180;
        double a = Math.Pow(Math.Sin((lat2 - lat1) * rad / 2), 2)
            + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin((lon2 - lon1) * rad / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    private static async Task<float?> OpenMeteoAsync(WeatherLocation place, CancellationToken token)
    {
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={Coord(place.Latitude)}&longitude={Coord(place.Longitude)}&current=temperature_2m";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, token));
        return doc.RootElement.GetProperty("current").GetProperty("temperature_2m").GetSingle();
    }

    private static async Task<float?> WttrAsync(WeatherLocation place, CancellationToken token)
    {
        string url = $"https://wttr.in/{Coord(place.Latitude)},{Coord(place.Longitude)}?format=%t&m";
        string body = await Http.GetStringAsync(url, token);
        var match = Regex.Match(body, @"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
        return match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value : null;
    }

    private static async Task<float?> SevenTimerAsync(WeatherLocation place, CancellationToken token)
    {
        string url = $"https://www.7timer.info/bin/api.pl?lon={Coord(place.Longitude)}&lat={Coord(place.Latitude)}&product=civil&output=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, token));
        var root = doc.RootElement;
        string init = root.GetProperty("init").GetString() ?? "";
        if (!DateTime.TryParseExact(init, "yyyyMMddHH", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var initial)) return null;
        float? nearest = null;
        double best = double.MaxValue;
        foreach (var entry in root.GetProperty("dataseries").EnumerateArray())
        {
            double distance = Math.Abs((initial.AddHours(entry.GetProperty("timepoint").GetInt32()) - DateTime.UtcNow).TotalMinutes);
            if (distance >= best) continue;
            best = distance;
            nearest = entry.GetProperty("temp2m").GetSingle();
        }
        return best <= 360 ? nearest : null;
    }
}
