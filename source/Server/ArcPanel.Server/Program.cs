using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ArcPanel.Server;

const int port = 5000;
const int maxLineLength = 4096;
var clients = new ConcurrentDictionary<string, DateTimeOffset>();
var setpoints = new ConcurrentDictionary<string, double>();
var temperatureUnits = new ConcurrentDictionary<string, string>();
using var feeds = new InformationFeeds();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var listener = new TcpListener(IPAddress.Any, port);

void Log(string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");

try
{
    _ = feeds.RunAsync(shutdown.Token, Log);
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
                        setpoints[roomId ?? deviceId] = requested;
                        Log($"Setpoint from {deviceId} ({roomId}): {requested:F1} F");
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
                        type = "ha-info", roomId = key, setpointF = setpoints.GetOrAdd(key, 70.0),
                        status = "Not connected", alerts = Array.Empty<string>()
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
