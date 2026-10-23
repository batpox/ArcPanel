using System.Net.Http.Headers;
using System.Text.Json;

namespace ArcPanel.Server;

// Read-only Home Assistant REST monitor. Never send credentials to ArcUnits.
internal sealed class HomeAssistantMonitor : IDisposable
{
    internal sealed record Snapshot(string Status, string Version, double? LivingRoomF,
        double? KitchenF, string[] Alerts);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string? baseUrl = Environment.GetEnvironmentVariable("ARC_HA_URL")?.TrimEnd('/');
    private readonly string? token = Environment.GetEnvironmentVariable("ARC_HA_TOKEN");
    private readonly string? livingEntity = Environment.GetEnvironmentVariable("ARC_HA_LIVING_ROOM_ENTITY");
    private readonly string? kitchenEntity = Environment.GetEnvironmentVariable("ARC_HA_KITCHEN_ENTITY");
    private readonly string? alertEntity = Environment.GetEnvironmentVariable("ARC_HA_ALERT_ENTITY");
    private readonly object gate = new();
    private Snapshot snapshot = new("Not configured", "Unknown", null, null, []);
    public Snapshot Current { get { lock (gate) return snapshot; } }

    public async Task RunAsync(CancellationToken cancellation, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            log("HA monitor not configured; set ARC_HA_URL and ARC_HA_TOKEN.");
            return;
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                try
                {
                    using var config = await ReadAsync("/api/config", cancellation);
                    string version = config.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "Unknown" : "Unknown";
                    double? living = await ReadTemperatureAsync(livingEntity, cancellation);
                    double? kitchen = await ReadTemperatureAsync(kitchenEntity, cancellation);
                    string[] alerts = await ReadAlertsAsync(alertEntity, cancellation);
                    lock (gate) snapshot = new("Online", version, living, kitchen, alerts);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    lock (gate) snapshot = snapshot with { Status = "Offline", LivingRoomF = null, KitchenF = null, Alerts = [] };
                    log($"HA poll failed: {ex.Message}");
                }
            } while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) { }
    }

    private async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(baseUrl + path, cancellation);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    private async Task<double?> ReadTemperatureAsync(string? entity, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(entity)) return null;
        using var doc = await ReadAsync("/api/states/" + Uri.EscapeDataString(entity), cancellation);
        var root = doc.RootElement;
        if (!root.TryGetProperty("state", out var state) ||
            !double.TryParse(state.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        string unit = root.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var u)
            ? u.GetString() ?? "" : "";
        return unit is "°C" or "C" ? value * 9.0 / 5.0 + 32 : unit is "°F" or "F" ? value : null;
    }

    private async Task<string[]> ReadAlertsAsync(string? entity, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(entity)) return [];
        using var doc = await ReadAsync("/api/states/" + Uri.EscapeDataString(entity), cancellation);
        string? state = doc.RootElement.GetProperty("state").GetString();
        return state is null or "" or "off" or "0" or "unknown" or "unavailable" ? [] : [state];
    }

    public void Dispose() => http.Dispose();
}
