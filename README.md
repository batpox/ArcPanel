# ArcPanel

**Area Resource Control** — networked ESP32-S3 touchscreen room panels and a C#/.NET coordination server.

- **Windows 11 server:** .NET 10, TCP/JSON, multi-client device registry; future integration with Linux Home Assistant.
- **ESP32-S3 panels:** Arduino C++, local SHT40 temperature/humidity, room identity, touchscreen setpoints.
- **Home Assistant:** sole authority for radiant heating actuation and energy-train logic.

## Repository layout

```text
source/
  Server/
    ArcPanel.Server.slnx
    ArcPanel.Server/
      ArcPanel.Server.csproj
      Program.cs
  Client/
    ArcPanel/
      ArcPanel.ino
      Secrets.example.h
docs/
  Architecture.md
  Protocol.md
  Development.md
```

## Getting started

1. Open `source/Server/ArcPanel.Server.slnx` in Visual Studio 2026 and run the .NET 10 console server.
2. Copy `source/Client/ArcPanel/Secrets.example.h` to `Secrets.h`, then enter Wi-Fi credentials, the server's Ethernet IPv4 address, and a unique ROOM_ID such as MasterBedroom.
3. Install **GFX Library for Arduino** and **ArduinoJson 7** from Arduino IDE Library Manager. Open `ArcPanel.ino`, select **ESP32S3 Dev Module**, enable **USB CDC On Boot**, upload and monitor at **115200 baud**.
4. The server listens on TCP port **5000**. Allow private-LAN inbound access in Windows Firewall.

The ESP32 display has four touch-selectable tabs in one row: ARC Room, ARC House, News, and Settings. Settings heartbeat counters update without full-screen redraw. ARC Room shows simulated temperature/humidity and a touch-adjustable circular setpoint. The setpoint is sent once in a heartbeat after 3 seconds without adjustment. News shows server-sent outside conditions and four short news lines; the server currently sends sample weather and placeholder headlines, not live feeds. ARC House displays a placeholder and the HA-Info setpoint. Settings displays the local RoomId, locally cached RoomName, heartbeat count and latest server acknowledgement. The other tabs are placeholders. The 240x320 display uses ST7789 (Arduino_GFX) and CST816D (I2C); these pins are specific to Waveshare ESP32-S3-Touch-LCD-2. The server currently acknowledges heartbeats but does not yet assign a RoomName. This is not yet a finished environmental controller; SHT40, Home Assistant integration, server-managed configuration and authentication are future increments. Do not commit `Secrets.h`.

See [development instructions](docs/Development.md), [architecture](docs/Architecture.md), and [protocol](docs/Protocol.md).

License: Apache-2.0.
