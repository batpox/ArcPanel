using System.Net.Http.Json;

namespace Arc.Services;

public sealed class ArcApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<ArcIdRecord>> GetArcIdsAsync(CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<List<ArcIdRecord>>("api/arc-ids", cancellationToken) ?? [];

    public async Task<IReadOnlyList<ArcDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<List<ArcDeviceRecord>>("api/devices", cancellationToken) ?? [];

    public async Task<ArcServerStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<ArcServerStatus>("api/status", cancellationToken);

    public async Task<HttpResponseMessage> CreateArcIdAsync(ArcIdInput input, CancellationToken cancellationToken = default)
        => await http.PostAsJsonAsync("api/arc-ids", input, cancellationToken);

    public async Task<HttpResponseMessage> UpdateArcIdAsync(string id, ArcIdInput input, CancellationToken cancellationToken = default)
        => await http.PutAsJsonAsync($"api/arc-ids/{Uri.EscapeDataString(id)}", input, cancellationToken);

    public async Task<HttpResponseMessage> DeleteArcIdAsync(string id, CancellationToken cancellationToken = default)
        => await http.DeleteAsync($"api/arc-ids/{Uri.EscapeDataString(id)}", cancellationToken);

    public async Task<HttpResponseMessage> AssignAsync(string arcId, string deviceIdentity, CancellationToken cancellationToken = default)
        => await http.PostAsJsonAsync("api/assignments", new ArcAssignmentRequest(arcId, deviceIdentity), cancellationToken);

    public async Task<HttpResponseMessage> UnassignAsync(string deviceIdentity, CancellationToken cancellationToken = default)
        => await http.DeleteAsync($"api/assignments/{Uri.EscapeDataString(deviceIdentity)}", cancellationToken);
}

public sealed record ArcIdRecord(string ArcId, string Role, int InstanceNumber, bool IsOperational,
    string RoomId, string RoomName, string SetpointName, bool Enabled, string? DeviceIdentity,
    string? LastSeenUtc, string? LastKnownIp, string? FirmwareVersion);
public sealed record ArcDeviceRecord(string DeviceIdentity, string? ArcId, string? RoomName,
    string LastSeenUtc, string? LastKnownIp, string? FirmwareVersion);
public sealed record ArcIdInput(string ArcId, string RoomId, string RoomName, string SetpointName, bool Enabled = true);
public sealed record ArcAssignmentRequest(string ArcId, string DeviceIdentity);
public sealed record ArcServerStatus(DateTimeOffset StartedUtc, long UptimeSeconds, int ConnectedDevices,
    string HomeAssistantStatus, string HomeAssistantVersion, double? GuestBedroomTempF,
    double? GuestBedroomSetpointF, bool? GuestFloorOn, string InformationLocation,
    DateTimeOffset? WeatherUpdated, DateTimeOffset? NewsUpdated, string[] Alerts);
