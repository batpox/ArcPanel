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

## Information services and display compatibility

The .NET server now refreshes outside weather (Open-Meteo), local severe-weather alerts
(National Weather Service), national/world headlines (BBC RSS), and Dow/Nasdaq
index values (best-effort Stooq quotes) every 15 minutes. It caches successful
responses and sends them to every connected ArcUnit in the existing `news`
message. Internet access is required on the **server**, not the ESP32.
Market data may be delayed or unavailable; this is not a trading feed.
NWS alerts are advisory only; ARC is not an emergency-warning device.

Default location is approximately Franklin, PA. Override via server environment
variables `ARC_LATITUDE`, `ARC_LONGITUDE` and `ARC_LOCATION_NAME`.
The room SHT40 remains separate from outside weather and is not yet read by firmware.

For future displays, keep the network message schema independent of physical
screen dimensions. The firmware centralizes `SCREEN_W`, `SCREEN_H`, text scaling
and drawing helpers; larger screens can use a different layout without changing
the server's wire protocol. The current four tabs deliberately retain compact
labels on the 240-pixel screen.

### Audio

The current ArcUnit firmware provides **no voice or sound**. The ESP32-S3
can support audio with appropriate hardware (for example an external I2S
amplifier/speaker and microphone). Do not assume the existing board has a
speaker, amplifier or microphone; verify the exact Waveshare revision and
available GPIOs before selecting accessories.

### Market quotes and tab labels

Dow Jones Industrial Average (`^DJI`) and Nasdaq Composite (`^IXIC`)
are fetched from Yahoo Finance's unofficial chart endpoint with Stooq as
a fallback. These public endpoints may throttle or fail; when unavailable,
the server retains the last successful quote or shows `Unavailable`.
The Nasdaq Composite is **not** the Nasdaq-100. Values are rounded to whole
index points and are not guaranteed real-time.

On the 240×320 display, the four top tab targets are now 58 pixels tall
(previously 40 pixels). Tab lettering uses the built-in narrow/tall
1× horizontal / 2× vertical text scale so eight-character labels fit
within the 60-pixel columns without an additional font library.

### ArcUnit room controls

The Room screen uses two side-by-side panels rather than concentric rings:
actual measured room temperature and humidity on the left, and the desired
setpoint on the right. Large up/down chevrons adjust the target in 0.5°F
increments (50–85°F). The existing 3-second idle debounce and heartbeat
setpoint reporting are unchanged. Until the SHT40 is wired into the firmware,
the actual room readings remain simulated.
