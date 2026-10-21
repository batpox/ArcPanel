using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

const int port = 5000;
const int maxLineLength = 4096;
var clients = new ConcurrentDictionary<string, DateTimeOffset>();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var listener = new TcpListener(IPAddress.Any, port);

void Log(string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");

try
{
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
                    response = JsonSerializer.Serialize(new {
                        type = "ack", deviceId, requestType = type,
                        serverTime = DateTimeOffset.UtcNow.ToString("O"),
                        roomId = (string?)null, roomName = "Unassigned"
                    });
                }
                catch (JsonException ex)
                {
                    Log($"Invalid JSON from {endpoint}: {ex.Message}");
                    response = JsonSerializer.Serialize(new { type = "error", message = "Invalid message" });
                }
                await writer.WriteLineAsync(response);
                Log($"TX {endpoint}: {response}");
            }
        }
        catch (OperationCanceledException) { Log($"Timeout or shutdown: {endpoint}"); }
        catch (IOException ex) { Log($"I/O error {endpoint}: {ex.Message}"); }
        catch (SocketException ex) { Log($"Socket error {endpoint}: {ex.Message}"); }
        catch (Exception ex) { Log($"Client error {endpoint}: {ex}"); }
        finally { Log($"Disconnected: {endpoint}"); }
    }
}
