# Developer Guide

## IDEs and Toolchain

| Component | IDE / Tool | Version |
| --- | --- | --- |
| Windows .NET server | **Visual Studio 2026** | 2026 family; exact installed build not recorded |
| Server SDK and target framework | **.NET 10** | `net10.0`; installed SDK patch not recorded |
| ESP32 firmware | **Arduino IDE** | Exact IDE version not recorded |
| ESP32 board support | Espressif ESP32 Arduino core | Installed package version not recorded |
| Firmware JSON | **ArduinoJson** | **7.4.3** was used during development; verify installed version |
| Firmware graphics | GFX Library for Arduino | Installed version not recorded |
| Hardware | Waveshare ESP32-S3-Touch-LCD-2 | 2.0-inch model, ST7789 / CST816D |

Do not infer an exact IDE build or board-package revision from source files. Firmware build metadata is captured automatically over TCP; IDE versions can be recorded manually.

## Repository Structure

`source/Server/ArcPanel.Server.slnx` is the C# solution; `source/Server/ArcPanel.Server/` contains the TCP server, `HomeAssistantMonitor.cs`, and `InformationFeeds.cs`. `source/Client/ArcPanel/ArcPanel.ino` contains the Arduino firmware. `docs/` contains project documentation.

## Configuration and Secrets

Copy `source/Client/ArcPanel/Secrets.example.h` to `Secrets.h`, and configure Wi-Fi, server LAN IPv4 address, and room ID. Never commit `Secrets.h`.

The server listens on TCP port **5000** on the LAN. Set optional environment variables `ARC_LATITUDE`, `ARC_LONGITUDE`, `ARC_LOCATION_NAME` for public information feeds. Home Assistant integration uses `ARC_HA_URL`, `ARC_HA_TOKEN`, `ARC_HA_LIVING_ROOM_ENTITY`, `ARC_HA_KITCHEN_ENTITY`, and `ARC_HA_ALERT_ENTITY`. Keep tokens server-side. GuestBedroom entity IDs are currently hard-coded in `HomeAssistantMonitor.cs`; review before deployment to a different installation.

## Build and Run

Open the server solution in Visual Studio 2026, or run:

```powershell
dotnet restore source/Server/ArcPanel.Server.slnx
dotnet build source/Server/ArcPanel.Server.slnx
dotnet run --project source/Server/ArcPanel.Server
```

In Arduino IDE, install the Espressif ESP32 board support, GFX Library for Arduino, and ArduinoJson 7. Select **ESP32S3 Dev Module**, retain the board's existing **USB CDC On Boot** setting, upload `ArcPanel.ino`, and monitor serial at **115200 baud**. The Windows server can use Ethernet while the panel uses Wi-Fi if both are on a reachable trusted LAN.

## Automatic Build Metadata

The ArcUnit sends a `build-info` JSON message immediately after each successful TCP connection, before the first heartbeat. The server stores a per-device manifest in `docs/BuildInfo/<deviceId>.json` when run from the repository. For deployments outside the repository, it defaults to a `BuildInfo` subfolder beside the server binary. Override the destination using `ARC_BUILD_INFO_DIRECTORY`.

Each manifest records the firmware build date/time, ESP32 Arduino core, ESP-IDF, ArduinoJson, chip information, and server .NET runtime/OS. The server writes only an explicit allowlist of non-secret fields and updates the file only when content changes. The JSON files may be committed using GitHub Desktop. No serial monitor, reset, or IDE setting change is required. **Keep USB CDC On Boot disabled** as in the working configuration.

The Arduino IDE version is not available in compiled firmware. The build date gives useful context; record an exact IDE version manually only if needed.

## Code Documentation Standard

- C#: use XML `/// <summary>` for all public methods, all methods returning `Task` or `Task<T>`, and substantial asynchronous routines, with `<param>` and `<returns>` where they clarify behavior. This provides IntelliSense tooltips.
- Arduino C++: document public-facing functions and substantial internal routines using Doxygen-compatible `/** ... */` with `@brief`, relevant `@param`, and `@return`. Keep hardware assumptions and units explicit.
- Record IDE and SDK versions in this guide when upgrading.
- Prefer timestamped diagnostics on both sides, maintain protocol compatibility, and do not log credentials.

## Testing and Operational Limits

There is no dedicated automated test project in this repository at this checkpoint. Validate .NET compilation, Arduino compilation/upload, connection/reconnection, touch operation, temperature units, heartbeat, Home Assistant read/write confirmation, and failure handling before deployment. The server uses unauthenticated, unencrypted TCP; restrict it to a trusted LAN. The Home Assistant token has broader privileges than the intended setpoint-only workflow. Panels must never control heating relays directly.

## Related

[Architecture](Architecture.md) · [Protocol](Protocol.md) · [User Guide](UserGuide.md)

## ArcUnit Display Updates

Room now displays a green, smooth-font local time and date above a smooth-font room name. Time is synchronized using NTP and the `EST5EDT` timezone rule (automatic daylight saving transitions); the clock remains blank until time synchronization succeeds. The Room controls were compacted to make space. News weather and financial tickers use larger proportional fonts; tap either ticker to pause or resume both. The ticker remains paused until tapped again. The firmware uses GFX FreeSans fonts; ensure the headers are available in the Arduino build environment.
