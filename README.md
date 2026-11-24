# ArcPanel

**Area Resource Control** — ESP32-S3 touchscreen panels, a headless C#/.NET server, and the Arc Blazor monitoring app.

- **ArcServer:** .NET 10, TCP/JSON panel service, REST management API, and SQLite device/Arc ID registry.
- **Arc:** .NET 10 Blazor Server UI using Dan's Standard Layout to monitor devices and manage assignments.
- **ArcPanel:** Arduino firmware for ESP32-S3 touchscreen panels with touch-based Arc ID selection.
- **Home Assistant:** sole authority for radiant heating actuation and energy-train logic.

## Repository layout

```text
source/
  Arc/
    Arc.csproj
  Server/
    ArcServer.slnx
    ArcServer/
      ArcServer.csproj
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

1. Open `source/Server/ArcServer.slnx` in Visual Studio 2026 and run ArcServer and Arc as separate projects (or set both as startup projects). ArcServer listens for panels on TCP 5000 and exposes its local management API on port 5081. Arc serves its browser UI on port 5080 and prints a temporary management PIN at startup.
2. Copy `source/Client/ArcPanel/Secrets.example.h` to `Secrets.h`, then enter Wi-Fi credentials and the server's LAN IPv4 address. The panel's Arc ID and room configuration are assigned through its touchscreen and ArcServer.
3. Install **GFX Library for Arduino** and **ArduinoJson 7** from Arduino IDE Library Manager. Open `ArcPanel.ino`, select **ESP32S3 Dev Module**, enable **USB CDC On Boot**, upload and monitor at **115200 baud**.
4. Allow private-LAN inbound access to ArcServer TCP **5000** and Arc HTTP **5080** in Windows Firewall. The management API defaults to loopback port **5081**.

On first connection, a panel reports its MAC-derived hardware identity. ArcServer sends available IDs; the panel displays them as touch choices. Arc IDs use `Role-N` (for example, `GuestBedroom-1`); instance `-1` is operational, and other instances cannot change heating setpoints. Arc can add IDs, show discovered devices, and transfer an ID to another panel. Settings shows the Arc ID, hardware identity, heartbeat count, and latest acknowledgement. The 240x320 display uses ST7789 (Arduino_GFX) and CST816D (I2C); these pins are specific to Waveshare ESP32-S3-Touch-LCD-2. Do not commit `Secrets.h`.

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
increments (50–85°F). After 3 seconds without adjustment, the firmware sends
a separate command instead of adding the target to heartbeat telemetry. Until the SHT40 is wired into the firmware,
the actual room readings remain simulated.

### Temperature units and information tickers

The Settings tab now has a touch-selectable **TemperatureUnits** preference
(`F` or `C`). The ArcUnit persists it in ESP32 NVS using Arduino
`Preferences`, reports it on every heartbeat, and displays actual/setpoint
and outside temperatures in the selected units. The server records the
reported unit for each device and echoes it in the ACK. Existing wire
temperature fields remain Fahrenheit.

The News tab scrolls the next **four days** of forecast highs, lows and
maximum precipitation probability, using the selected temperature units.
The House tab scrolls **Home Assistant connection status and alerts**.
Scrolling happens locally, without a separate network message per frame.

The server reports unavailable values when HA cannot be reached or an entity
is not configured. The SHT40 sensor integration remains pending.

### Large color tickers and Home Assistant monitor

Tickers use the same 2× bitmap text as the orange location heading (12×16 px).
On News, a **blue weather ticker** replaces the forecast row, and a
**green financial ticker** replaces the fixed Dow/Nasdaq lines. On House,
a **cyan Home Assistant ticker** shows version, connection status,
living-room temperature, kitchen temperature, and any configured alert.
All three scroll locally on the ArcUnit; tickers need not be at the bottom.

The server has a Home Assistant REST monitor that polls
every 30 seconds. Configure environment variables on the **C# server**
before starting it:

- `ARC_HA_URL` — e.g. `http://homeassistant.local:8123`
- `ARC_HA_TOKEN` — Home Assistant long-lived access token (keep secret)
- `ARC_HA_LIVING_ROOM_ENTITY` — exact entity ID, e.g. `sensor.living_room_temperature`
- `ARC_HA_KITCHEN_ENTITY` — exact entity ID, e.g. `sensor.kitchen_temperature`
- `ARC_HA_ALERT_ENTITY` — optional entity ID for alert state

Entity IDs above are **examples**, not verified names from your HA installation.
The monitor reads HA's `/api/config` for the installed version and
`/api/states/{entity_id}` for temperature/alert states. It supports sensor
temperatures reported in °F or °C and converts to Fahrenheit for the
existing wire protocol; the ArcUnit displays the user's preferred units.
Unknown/unconfigured sensors display `--`, not fabricated readings.
If HA credentials are missing, the status is `Not configured`; connection
failures show `Offline`. The access token is never sent to an ArcUnit.

The server uses REST both to monitor HA and to call the input-number setpoint
action. It does not call any relay or switch control action.


### GuestBedroom live Room tab (Home Assistant)

Select `GuestBedroom-1` on the panel or assign it in Arc. The server registry
stores `RoomId`, display name and `SetpointName` for each Arc ID. Set
`ARC_HA_URL` and `ARC_HA_TOKEN`
on the Windows server before launching it. For this installation the expected
HA entities are:

- `sensor.guest_bedroom_temperature` — authoritative temperature template sensor, currently sourcing the Govee
- `input_number.guest_bedroom_temperature_setpoint` — editable setpoint helper (50–85°F, 1°F steps)
- `switch.zen16a_boiler_3_2` — GuestFloor relay **read-only**

The server polls HA every 30 seconds and sends `roomTempF`,
`setpointF`, and nullable `guestFloorOn` in the existing `ha-info` message.
The Room tab now displays HA temperature, HA setpoint, and the GuestFloor relay
state. Unavailable temperature and relay states display `--`; fake temperature
and humidity telemetry is no longer sent. Humidity remains unavailable until
a real HA humidity source or SHT40 integration is configured.

GuestBedroom setpoint changes travel as a `ChangeSetpoint` command. The server
maps the assigned Arc ID and registry `SetpointName` through `ArcPanelConfig.json`, checks
`TempF` against the configured range, then calls HA's `input_number.set_value`
action. It does not call a switch-control action. HA remains the heating
authority. ARC confirms the update by rereading the helper, and a later HA poll
synchronizes the panel. **This integration uses
30-second REST polling, not WebSocket updates**. Ensure the HA entity IDs
above actually exist; renaming a helper does not necessarily rename its ID.

The sample mapping is in `source/Server/ArcServer/ArcPanelConfig.json`.
Override its path with `ARC_CONFIG_PATH`. Server exchanges and diagnostics are
appended to daily files under `logs/`; override that folder with
`ARC_LOG_DIRECTORY`. Keep `ARC_HA_TOKEN` on the server, not on a panel.

### Arc registry and UI

ArcServer keeps configured IDs in SQLite table `ArcIds` and discovered
hardware identities plus current assignments in `ArcDevices`. The default DB
is `%LOCALAPPDATA%\\Arc\\ArcServer.db`; override it with `ARC_DATABASE_PATH`.
On first run, ArcServer creates `GuestBedroom-1` and `GuestBedroom-2` when
missing. `-1` is always marked operational; other role instances are
non-operational.

Arc is a separate .NET 10 Blazor Server app. It follows Dan's Standard Layout:
top actions, left explorer, center workspace tabs, right properties, and a
bottom status bar. Arc requires a six-digit PIN printed at startup; set
`ARC_ADMIN_PIN` on Arc to use a persistent PIN. Arc listens on port 5080.
ArcServer's REST API defaults to
loopback port 5081 because Arc calls it server-side; set `ArcServer:ApiBaseUrl`
if the server runs on another host. Set `Arc:UIUrls` to change the browser
listener. Keep TCP 5000 and HTTP 5080 restricted to the trusted private LAN.

Arc IDs are reusable and hardware identities stay with the physical ESP32.
Transferring an ID in Arc clears it from the previous device and assigns it to
the selected device; the panel receives the new assignment on its next
heartbeat. The hardware identity is for recognition, not authentication. The
panel protocol does not yet authenticate devices or encrypt traffic. Arc's PIN
protects management actions, but its HTTP session is not encrypted; keep both
applications on a trusted private LAN.

