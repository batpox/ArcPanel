using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcPanel.Server;

const int port = 5000;
const int maxLineLength = 4096;
var clients = new ConcurrentDictionary<string, DateTimeOffset>();
var temperatureUnits = new ConcurrentDictionary<string, string>();
using var buildInfoWriteLock = new SemaphoreSlim(1, 1);
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
using var feeds = new InformationFeeds();
using var ha = new HomeAssistantMonitor();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var listener = new TcpListener(IPAddress.Any, port);

/// <summary>Writes a timestamped server diagnostic message.</summary>
void Log(string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");

try
{
    _ = feeds.RunAsync(shutdown.Token, Log);
    _ = ha.RunAsync(shutdown.Token, Log);
    listener.Start();
    Log($"ArcPanel server listening on TCP {port}");
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
finally { listener.Stop(); Log("Server stopped."); }

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
                    clients[deviceId] = DateTimeOffset.UtcNow;
                    if (type == "build-info")
                        await SaveBuildInfoAsync(deviceId, root);
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
                    var roomId = root.TryGetProperty("roomId", out var rid) ? rid.GetString() : null;
                    if (root.TryGetProperty("setpointF", out var sp) && sp.ValueKind == JsonValueKind.Number &&
                        sp.TryGetDouble(out var requested) && requested >= 50 && requested <= 85)
                    {
                        if (roomId == "GuestBedroom")
                        {
                            bool accepted = await ha.SetGuestBedroomSetpointAsync(requested, shutdown.Token);
                            Log($"GuestBedroom setpoint request from {deviceId}: {requested:F1} F, accepted={accepted}");
                        }
                        else Log($"Ignored setpoint request from unmapped room {roomId ?? "(null)"}");
                    }
                    if (root.TryGetProperty("roomTempF", out var rt) && root.TryGetProperty("roomHumidity", out var rh))
                        Log($"Room telemetry {roomId}: {rt} F, {rh}% humidity");
                    response = JsonSerializer.Serialize(new {
                        type = "ack", deviceId, requestType = type,
                        serverTime = DateTimeOffset.UtcNow.ToString("O"),
                        roomId, roomName = roomId ?? "Unassigned",
                        temperatureUnits = temperatureUnits.GetOrAdd(deviceId, "F")
                    });
                }
                catch (JsonException ex)
                {
                    Log($"Invalid JSON from {endpoint}: {ex.Message}");
                    response = JsonSerializer.Serialize(new { type = "error", message = "Invalid message" });
                }

                await writer.WriteLineAsync(response);
                Log($"TX {endpoint}: {response}");
                if (response.Contains("heartbeat"))
                {
                    string key;
                    using (var reply = JsonDocument.Parse(response))
                        key = reply.RootElement.GetProperty("roomId").GetString() ??
                              reply.RootElement.GetProperty("deviceId").GetString() ?? "Unassigned";
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
    var names = new[] { "schemaVersion", "deviceId", "roomId", "platform", "board",
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
