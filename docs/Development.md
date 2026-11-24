# Development

## Server

Visual Studio 2026: open `source/Server/ArcServer.slnx`, target .NET 10, and run ArcServer and Arc as separate projects. ArcServer accepts panel TCP connections on port 5000 and serves its loopback management API on port 5081. Arc serves the Blazor UI on port 5080. On first start, Arc prints a temporary PIN to its console; set `ARC_ADMIN_PIN` to use a persistent PIN. Allow TCP 5000 and HTTP 5080 only on trusted private networks. Keep the API loopback-only unless Arc runs on another host; then configure `ARC_API_URLS` and `ArcServer:ApiBaseUrl` accordingly.

## Client

Arduino IDE (exact installed version not recorded): install Espressif's ESP32 board package; choose **ESP32S3 Dev Module** for Waveshare ESP32-S3-Touch-LCD-2 (not 2.1). Enable **USB CDC On Boot**. Copy `Secrets.example.h` to `Secrets.h`, set Wi-Fi and the Windows LAN IPv4 address. Arc ID and room identity are assigned after connection. Open `source/Client/ArcPanel/ArcPanel.ino`, upload, then open Serial Monitor at 115200 baud.

Never commit `Secrets.h`. The Windows PC may remain Ethernet-only while panels connect over Wi-Fi, provided both networks permit LAN communication.

## Conventions

C# (.NET 10), Arduino C++, timestamped logs on both ends, recoverable network errors, descriptive documentation, and version numbers formatted `yy.mm.dd.HH`. Prefer small, independently testable increments.


For the full IDE/toolchain version matrix, build commands, configuration, and XML/Doxygen documentation policy, see [Developer Guide](DeveloperGuide.md).
