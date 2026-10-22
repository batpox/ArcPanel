using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace ArcPanel.Server;

// Shared, cached external information. Panels never contact public APIs directly.
internal sealed class InformationFeeds : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly object gate = new();
    private readonly double latitude, longitude;
    private readonly string locationName;
    private Snapshot snapshot = new();
    public InformationFeeds()
    {
        latitude = double.TryParse(Environment.GetEnvironmentVariable("ARC_LATITUDE"), CultureInfo.InvariantCulture, out var lat) ? lat : 41.397;
        longitude = double.TryParse(Environment.GetEnvironmentVariable("ARC_LONGITUDE"), CultureInfo.InvariantCulture, out var lon) ? lon : -79.832;
        locationName = Environment.GetEnvironmentVariable("ARC_LOCATION_NAME") ?? "Franklin, PA";
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcPanel/0.2 (local information display; contact: arcpanel@example.invalid)");
    }

    internal sealed record Snapshot
    {
        public double? OutsideTempF { get; init; }
        public double? OutsideHumidity { get; init; }
        public string Forecast { get; init; } = "Forecast unavailable";
        public string[] Alerts { get; init; } = [];
        public string[] Headlines { get; init; } = [];
        public string Dow { get; init; } = "Unavailable";
        public string Nasdaq { get; init; } = "Unavailable";
        public string Location { get; init; } = "";
        public DateTimeOffset? WeatherUpdated { get; init; }
        public DateTimeOffset? NewsUpdated { get; init; }
    }

    public Snapshot Current { get { lock (gate) return snapshot; } }

    public async Task RunAsync(CancellationToken token, Action<string> log)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            await RefreshAsync(log, token);
            try { if (!await timer.WaitForNextTickAsync(token)) break; }
            catch (OperationCanceledException) { break; }
        } while (!token.IsCancellationRequested);
    }

    private async Task RefreshAsync(Action<string> log, CancellationToken token)
    {
        var current = Current;
        double? temperature = current.OutsideTempF, humidity = current.OutsideHumidity;
        string forecast = current.Forecast;
        var alerts = current.Alerts;
        var headlines = current.Headlines;
        string dow = current.Dow, nasdaq = current.Nasdaq;
        var weatherUpdated = current.WeatherUpdated;
        var newsUpdated = current.NewsUpdated;

        try
        {
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(CultureInfo.InvariantCulture)}&longitude={longitude.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&temperature_unit=fahrenheit&timezone=America%2FNew_York&forecast_days=2";
            using var json = JsonDocument.Parse(await http.GetStringAsync(url, token));
            var root = json.RootElement;
            temperature = root.GetProperty("current").GetProperty("temperature_2m").GetDouble();
            humidity = root.GetProperty("current").GetProperty("relative_humidity_2m").GetDouble();
            var daily = root.GetProperty("daily");
            var high = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            var low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
            var rain = daily.GetProperty("precipitation_probability_max")[0].GetInt32();
            forecast = $"Today H:{high:0} L:{low:0} F Rain:{rain}%";
            weatherUpdated = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"Weather fetch failed: {ex.Message}"); }

        try
        {
            // NWS active-alert feed; severity and event text are supplied by the source.
            string url = $"https://api.weather.gov/alerts/active?point={latitude.ToString(CultureInfo.InvariantCulture)},{longitude.ToString(CultureInfo.InvariantCulture)}";
            using var json = JsonDocument.Parse(await http.GetStringAsync(url, token));
            alerts = json.RootElement.GetProperty("features").EnumerateArray()
                .Select(x => x.GetProperty("properties"))
                .Where(x => x.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("event").GetString() ?? "")
                .Where(x => x.Length > 0).Distinct().Take(3).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"Weather alerts fetch failed: {ex.Message}"); }

        try
        {
            var sources = new[] { "https://feeds.bbci.co.uk/news/world/rss.xml", "https://feeds.bbci.co.uk/news/us_and_canada/rss.xml" };
            var titles = new List<string>();
            foreach (var url in sources)
            {
                var xml = XDocument.Parse(await http.GetStringAsync(url, token));
                titles.AddRange(xml.Descendants("item").Select(item => (string?)item.Element("title"))
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).Take(4));
            }
            headlines = titles.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
            newsUpdated = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"RSS fetch failed: {ex.Message}"); }

        // Index quotes are best-effort and explicitly marked unavailable when inaccessible.
        dow = await FetchIndexAsync("^dji", current.Dow, log, token);
        nasdaq = await FetchIndexAsync("^ndq", current.Nasdaq, log, token);
        lock (gate)
            snapshot = new Snapshot {
                OutsideTempF = temperature, OutsideHumidity = humidity, Forecast = forecast,
                Alerts = alerts, Headlines = headlines, Dow = dow, Nasdaq = nasdaq,
                Location = locationName, WeatherUpdated = weatherUpdated, NewsUpdated = newsUpdated
            };
        log($"Feeds refreshed: weather={(temperature.HasValue ? "available" : "unavailable")}, alerts={alerts.Length}, headlines={headlines.Length}, Dow={dow}, Nasdaq={nasdaq}");
    }

    private async Task<string> FetchIndexAsync(string symbol, string previous, Action<string> log, CancellationToken token)
    {
        try
        {
            string csv = await http.GetStringAsync($"https://stooq.com/q/l/?s={Uri.EscapeDataString(symbol)}&f=sd2t2l&h&e=csv", token);
            var lines = csv.Trim().Split('\n');
            if (lines.Length < 2) return previous;
            var fields = lines[1].Trim().Split(',');
            if (fields.Length < 4 || !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return previous;
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"Market quote {symbol} failed: {ex.Message}"); return previous; }
    }

    public void Dispose() => http.Dispose();
}
