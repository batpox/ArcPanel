using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using ArcServer;
using ArcPanel.Server;

const int port = 5000;
const int maxLineLength = 4096;
var clients = new ConcurrentDictionary<string, DateTimeOffset>();
var temperatureUnits = new ConcurrentDictionary<string, string>();
using var buildInfoWriteLock = new SemaphoreSlim(1, 1);
var logGate = new object();
var logDirectory = Environment.GetEnvironmentVariable("ARC_LOG_DIRECTORY");
if (string.IsNullOrWhiteSpace(logDirectory))
    logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
var configPath = Environment.GetEnvironmentVariable("ARC_CONFIG_PATH");
if (string.IsNullOrWhiteSpace(configPath))
    configPath = Path.Combine(AppContext.BaseDirectory, "ArcPanelConfig.json");
using var configuration = JsonDocument.Parse(File.ReadAllText(configPath));
var buildInfoDirectory = Environment.GetEnvironmentVariable("ARC_BUILD_INFO_DIRECTORY");
if (string.IsNullOrWhiteSpace(buildInfoDirectory))
{
    // During development, locate the repository docs directory from the running binary.
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "DeveloperGuide.md")))
        directory = directory.Parent;
    buildInfoDirectory = directory is null
        ? Path.Combine(AppContext.BaseDirectory, "BuildInfo")
        : Path.Combine(directory.FullName, "docs", "BuildInfo");
}
var databasePath = Environment.GetEnvironmentVariable("ARC_DATABASE_PATH");
if (string.IsNullOrWhiteSpace(databasePath))
    databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arc", "ArcServer.db");
var registry = new ArcRegistryStore(databasePath);
using var feeds = new InformationFeeds();
using var ha = new HomeAssistantMonitor();
var startedUtc = DateTimeOffset.UtcNow;
var apiUrls = Environment.GetEnvironmentVariable("ARC_API_URLS");
if (string.IsNullOrWhiteSpace(apiUrls)) apiUrls = "http://127.0.0.1:5081";
var webBuilder = WebApplication.CreateBuilder(args);
webBuilder.WebHost.UseUrls(apiUrls);
var api = webBuilder.Build();
api.MapGet("/api/arc-ids", () => Results.Ok(registry.GetArcIds()));
api.MapGet("/api/devices", () => Results.Ok(registry.GetDevices()));
api.MapGet("/api/status", () =>
{
    var haSnapshot = ha.Current;
    var feedSnapshot = feeds.Current;
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(new
    {
        startedUtc,
        uptimeSeconds = (long)(now - startedUtc).TotalSeconds,
        connectedDevices = clients.Count(item => now - item.Value < TimeSpan.FromSeconds(10)),
        homeAssistantStatus = haSnapshot.Status,
        homeAssistantVersion = haSnapshot.Version,
        guestBedroomTempF = haSnapshot.GuestBedroomF,
        guestBedroomSetpointF = haSnapshot.GuestBedroomSetpointF,
        guestFloorOn = haSnapshot.GuestFloorOn,
        informationLocation = feedSnapshot.Location,
        weatherUpdated = feedSnapshot.WeatherUpdated,
        newsUpdated = feedSnapshot.NewsUpdated,
        alerts = haSnapshot.Alerts.Concat(feedSnapshot.Alerts).Take(10).ToArray()
    });
});
api.MapPost("/api/arc-ids", (ArcIdInput input) => registry.CreateArcId(input)
    ? Results.Created($"/api/arc-ids/{Uri.EscapeDataString(input.ArcId)}", input)
    : Results.Conflict(new { message = "ArcID is invalid or already exists." }));
api.MapPut("/api/arc-ids/{arcId}", (string arcId, ArcIdInput input) => registry.UpdateArcId(arcId, input)
    ? Results.NoContent() : Results.BadRequest(new { message = "ArcID does not exist or its values are invalid." }));
api.MapDelete("/api/arc-ids/{arcId}", (string arcId) => registry.DeleteArcId(arcId)
    ? Results.NoContent() : Results.Conflict(new { message = "ArcID is assigned or does not exist." }));
api.MapPost("/api/assignments", (ArcAssignmentRequest request) => registry.Reassign(request.ArcId, request.DeviceIdentity)
    ? Results.NoContent() : Results.BadRequest(new { message = "ArcID or device identity does not exist." }));
api.MapDelete("/api/assignments/{deviceIdentity}", (string deviceIdentity) => registry.Unassign(deviceIdentity)
    ? Results.NoContent() : Results.NotFound());
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var listener = new TcpListener(IPAddress.Any, port);

/// <summary>Writes a timestamped server diagnostic message.</summary>
void Log(string message)
{
    var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
    Console.WriteLine(line);
    try
    {
        lock (logGate)
        {
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, $"ArcServer-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"Could not write ArcServer log: {ex.Message}"); }
}

bool TryGetSetpointMapping(string? panelId, string? setpointName,
    out string entityId, out string argumentName, out double minimum, out double maximum)
{
    entityId = argumentName = string.Empty;
    minimum = maximum = 0;
    if (string.IsNullOrWhiteSpace(panelId) || string.IsNullOrWhiteSpace(setpointName) ||
        !configuration.RootElement.TryGetProperty("Panels", out var panels) ||
        !panels.TryGetProperty(panelId, out var panel) ||
        !panel.TryGetProperty("Setpoints", out var setpoints) ||
        !setpoints.TryGetProperty(setpointName, out var mapping)) return false;
    if (!mapping.TryGetProperty("EntityId", out var entity) || entity.ValueKind != JsonValueKind.String ||
        !mapping.TryGetProperty("ArgumentName", out var argument) || argument.ValueKind != JsonValueKind.String ||
        !mapping.TryGetProperty("Minimum", out var min) || !min.TryGetDouble(out minimum) ||
        !mapping.TryGetProperty("Maximum", out var max) || !max.TryGetDouble(out maximum)) return false;
    entityId = entity.GetString() ?? string.Empty;
    argumentName = argument.GetString() ?? string.Empty;
    return entityId.StartsWith("input_number.", StringComparison.Ordinal) &&
           !string.IsNullOrWhiteSpace(argumentName) && double.IsFinite(minimum) &&
           double.IsFinite(maximum) && minimum <= maximum;
}

try
{
    api.Lifetime.ApplicationStopping.Register(shutdown.Cancel);
    await api.StartAsync();
    Log($"ArcServer API listening at {apiUrls}");
    _ = feeds.RunAsync(shutdown.Token, Log);
    _ = ha.RunAsync(shutdown.Token, Log);
    listener.Start();
    Log($"ArcServer listening on TCP {port}");
    while (!shutdown.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(shutdown.Token); }
        catch (OperationCanceledException) { break; }
        catch (SocketException ex) { Log($"Accept error: {ex.Message}"); await Task.Delay(1000, shutdown.Token); continue; }
        _ = HandleClientAsync(client);
    }
}
catch (OperationCanceledException) { }
catch (Exception ex) { Log($"Server fatal: {ex}"); Environment.ExitCode = 1; }
finally { listener.Stop(); await api.StopAsync(); Log("ArcServer stopped."); }

/// <summary>Processes a connected ArcUnit's newline-delimited JSON messages until disconnect.</summary>
async Task HandleClientAsync(TcpClient client)
{
    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    using (client)
    {
        Log($"Connected: {endpoint}");
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            while (!shutdown.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) break;
                if (line.Length > maxLineLength) { Log($"Oversize message: {endpoint}"); break; }
                Log($"RX {endpoint}: {line}");
                string response;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var deviceId = root.TryGetProperty("deviceId", out var id) ? id.GetString() : null;
                    if (string.IsNullOrWhiteSpace(deviceId)) throw new JsonException("Missing deviceId");
                    if (deviceId.Length > 100 || deviceId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
                        throw new JsonException("Invalid deviceId");
                    clients[deviceId] = DateTimeOffset.UtcNow;
                    if (type == "build-info")
                        await SaveBuildInfoAsync(deviceId, root);
                    string? firmwareVersion = root.TryGetProperty("arduinoCoreVersion", out var coreVersion) && coreVersion.ValueKind == JsonValueKind.String
                        ? coreVersion.GetString() : null;
                    string? remoteIp = client.Client.RemoteEndPoint is IPEndPoint remoteEndpoint
                        ? remoteEndpoint.Address.ToString() : null;
                    registry.RegisterDevice(deviceId, remoteIp, firmwareVersion);
                    if (root.TryGetProperty("temperatureUnits", out var units) && units.ValueKind == JsonValueKind.String)
                    {
                        string? requestedUnits = units.GetString();
                        if (requestedUnits is "F" or "C")
                        {
                            if (!temperatureUnits.TryGetValue(deviceId, out var oldUnits) || oldUnits != requestedUnits)
                                Log($"TemperatureUnits {deviceId}: {requestedUnits}");
                            temperatureUnits[deviceId] = requestedUnits;
                        }
                    }
                    var assignment = registry.GetAssignment(deviceId);
                    string? roomId = assignment?.RoomId;
                    string? panelId = assignment?.ArcId;
                    string? commandId = null;
                    bool? commandAccepted = null;
                    string? commandResult = null;
                    bool? assignmentAccepted = null;
                    if (type == "claim-arc-id")
                    {
                        string requestedArcId = root.TryGetProperty("arcId", out var arcIdElement) && arcIdElement.ValueKind == JsonValueKind.String
                            ? arcIdElement.GetString() ?? string.Empty : string.Empty;
                        assignmentAccepted = registry.TryClaim(deviceId, requestedArcId);
                        assignment = registry.GetAssignment(deviceId);
                        roomId = assignment?.RoomId;
                        panelId = assignment?.ArcId;
                        commandResult = assignmentAccepted.Value ? $"Assigned {panelId}" : "That ArcID is not available.";
                        Log($"ArcID claim from {deviceId}: {requestedArcId}, accepted={assignmentAccepted}");
                    }
                    if (type == "command")
                    {
                        commandId = root.TryGetProperty("commandId", out var commandIdElement) ? commandIdElement.GetString() : null;
                        var command = root.TryGetProperty("command", out var commandElement) ? commandElement.GetString() : null;
                        if (command == "ChangeSetpoint" && assignment is { IsOperational: true, Enabled: true } && root.TryGetProperty("arguments", out var args) &&
                            args.ValueKind == JsonValueKind.Object &&
                            args.TryGetProperty("SetpointName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                        {
                            string setpointName = assignment?.SetpointName ?? string.Empty;
                            if (TryGetSetpointMapping(panelId, setpointName, out var entityId, out var argumentName, out var minimum, out var maximum) &&
                                args.TryGetProperty(argumentName, out var valueElement) && valueElement.ValueKind == JsonValueKind.Number &&
                                valueElement.TryGetDouble(out var requested) && double.IsFinite(requested) &&
                                requested >= minimum && requested <= maximum)
                            {
                                commandAccepted = await ha.SetSetpointAsync(entityId, requested, minimum, maximum, shutdown.Token);
                                commandResult = commandAccepted.Value ? $"Set {entityId} to {requested:F1}" : $"Home Assistant rejected update for {entityId}";
                            }
                            else commandResult = $"No valid setpoint mapping/value for panel '{panelId}', setpoint '{setpointName}'";
                        }
                        else commandResult = assignment is { IsOperational: true, Enabled: false }
                            ? "This Arc ID is disabled."
                            : assignment is not { IsOperational: true, Enabled: true }
                                ? "Setpoint changes are disabled for non-operational Arc IDs."
                                : $"Unsupported or malformed command '{command ?? "(null)"}'";
                        commandAccepted ??= false;
                        Log($"Command {commandId ?? "(no id)"} from device={deviceId} panel={panelId ?? "(null)"}: {command ?? "(null)"}, accepted={commandAccepted}, result={commandResult}");
                    }
                    if (root.TryGetProperty("roomTempF", out var rt) && root.TryGetProperty("roomHumidity", out var rh))
                        Log($"Room telemetry {roomId}: {rt} F, {rh}% humidity");
                    int optionsPage = type == "list-arc-ids" && root.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out int pageValue)
                        ? pageValue : 0;
                    response = JsonSerializer.Serialize(new {
                        type = "ack", deviceId, requestType = type,
                        serverTime = DateTimeOffset.UtcNow.ToString("O"),
                        roomId, roomName = assignment?.RoomName ?? "Unassigned",
                        panelId, arcId = panelId,
                        optionsPage,
                        operational = assignment?.IsOperational ?? false,
                        assignmentEnabled = assignment?.Enabled ?? false,
                        setpointName = assignment?.SetpointName,
                        assignmentAccepted, assignmentMessage = type == "claim-arc-id" ? commandResult : null,
                        temperatureUnits = temperatureUnits.GetOrAdd(deviceId, "F"),
                        commandId, commandAccepted, commandResult
                    });
                }
                catch (JsonException ex)
                {
                    Log($"Invalid JSON from {endpoint}: {ex.Message}");
                    response = JsonSerializer.Serialize(new { type = "error", message = "Invalid message" });
                }

                await writer.WriteLineAsync(response);
                Log($"TX {endpoint}: {response}");
                if (response.Contains("heartbeat") || response.Contains("list-arc-ids"))
                {
                    string key;
                    string responseDeviceId;
                    string requestType;
                    int requestedPage;
                    using (var reply = JsonDocument.Parse(response))
                    {
                        var responseRoot = reply.RootElement;
                        responseDeviceId = responseRoot.GetProperty("deviceId").GetString() ?? "UnknownDevice";
                        key = responseRoot.GetProperty("roomId").GetString() ?? responseDeviceId;
                        requestType = responseRoot.GetProperty("requestType").GetString() ?? string.Empty;
                        requestedPage = responseRoot.TryGetProperty("optionsPage", out var pageValue) && pageValue.TryGetInt32(out int parsedPage)
                            ? parsedPage : 0;
                    }
                    if (requestType == "heartbeat")
                    {
                        var data = feeds.Current;
                        var news = JsonSerializer.Serialize(new {
                            type = "news", schemaVersion = 1, outsideTempF = data.OutsideTempF,
                            outsideHumidity = data.OutsideHumidity, location = data.Location,
                            forecast = data.Forecast, forecastTicker = data.ForecastTicker, forecastTickerC = data.ForecastTickerC, alerts = data.Alerts,
                            headlines = data.Headlines, dow = data.Dow, nasdaq = data.Nasdaq,
                            weatherUpdated = data.WeatherUpdated, newsUpdated = data.NewsUpdated
                        });
                        var haInfo = JsonSerializer.Serialize(new {
                            type = "ha-info", roomId = key,
                            setpointF = key == "GuestBedroom" ? ha.Current.GuestBedroomSetpointF : null,
                            roomTempF = key == "GuestBedroom" ? ha.Current.GuestBedroomF : null,
                            guestFloorOn = key == "GuestBedroom" ? ha.Current.GuestFloorOn : null,
                            status = ha.Current.Status, version = ha.Current.Version,
                            livingRoomTempF = ha.Current.LivingRoomF, kitchenTempF = ha.Current.KitchenF,
                            alerts = ha.Current.Alerts
                        });
                        await writer.WriteLineAsync(news);
                        await writer.WriteLineAsync(haInfo);
                        Log($"TX {endpoint}: {news}");
                        Log($"TX {endpoint}: {haInfo}");
                    }
                    if (registry.GetAssignment(responseDeviceId) is null)
                    {
                        var available = registry.GetAvailableArcIds();
                        int pageSize = 3;
                        requestedPage = Math.Clamp(requestedPage, 0, Math.Max(0, (available.Count - 1) / pageSize));
                        var choices = available.Skip(requestedPage * pageSize).Take(pageSize).Select(x => new {
                            arcId = x.ArcId, roomId = x.RoomId, roomName = x.RoomName,
                            operational = x.IsOperational
                        });
                        string options = JsonSerializer.Serialize(new {
                            type = "assignment-options", deviceId = responseDeviceId,
                            page = requestedPage, pageCount = Math.Max(1, (available.Count + pageSize - 1) / pageSize), arcIds = choices
                        });
                        await writer.WriteLineAsync(options);
                        Log($"TX {endpoint}: {options}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { Log($"Timeout or shutdown: {endpoint}"); }
        catch (IOException ex) { Log($"I/O error {endpoint}: {ex.Message}"); }
        catch (SocketException ex) { Log($"Socket error {endpoint}: {ex.Message}"); }
        catch (Exception ex) { Log($"Client error {endpoint}: {ex}"); }
        finally { Log($"Disconnected: {endpoint}"); }
    }
}

 
/// <summary>Persists validated, non-secret ArcUnit build metadata when it changes.</summary>
async Task SaveBuildInfoAsync(string deviceId, JsonElement root)
{
    if (deviceId.Length > 100 || deviceId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        throw new JsonException("Invalid build-info deviceId");
    if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
        !root.TryGetProperty("arduinoCoreVersion", out var core) || core.ValueKind != JsonValueKind.String)
        throw new JsonException("Invalid build-info schema");
    // Explicit allowlist prevents credentials or unrelated telemetry being written to disk.
    var names = new[] { "schemaVersion", "deviceId", "panelId", "roomId", "platform", "board",
        "arduinoCoreVersion", "espIdfVersion", "chipModel", "chipRevision", "cpuMHz",
        "flashBytes", "buildDate", "buildTime", "arduinoJsonVersion" };
    var firmware = new JsonObject();
    foreach (var name in names)
        if (root.TryGetProperty(name, out var value))
            firmware[name] = JsonNode.Parse(value.GetRawText());
    var manifest = new JsonObject
    {
        ["firmware"] = firmware,
        ["server"] = new JsonObject
        {
            ["runtimeVersion"] = Environment.Version.ToString(),
            ["framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }
    };
    var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    await buildInfoWriteLock.WaitAsync(shutdown.Token);
    try
    {
        Directory.CreateDirectory(buildInfoDirectory!);
        var file = Path.Combine(buildInfoDirectory!, deviceId + ".json");
        if (File.Exists(file) && await File.ReadAllTextAsync(file, shutdown.Token) == json)
            return;
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, shutdown.Token);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Log($"BuildInfo updated: {file}");
    }
    finally { buildInfoWriteLock.Release(); }
}

public sealed record ArcAssignmentRequest(string ArcId, string DeviceIdentity);

