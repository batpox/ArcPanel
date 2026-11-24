# ArcServer wire protocol — assignment iteration

TCP port 5000, newline-delimited UTF-8 JSON; one JSON object per line.
The panel's MAC-derived `deviceId` identifies the physical unit. `panelId` is
the current logical Arc ID and is only a cached client hint; ArcServer resolves
the authoritative assignment from SQLite. The trusted LAN protocol has no
device authentication or encryption yet.

Every 2 seconds, the ArcUnit sends a heartbeat:
```json
{"type":"heartbeat","deviceId":"ESP32-001122334455","panelId":"GuestBedroom-1","roomId":"GuestBedroom","uptimeMs":12345,"roomHumidity":46,"temperatureUnits":"F"}
```
The `panelId` and `roomId` fields are hints from the panel; the server does not
trust them to route commands. The database assignment supplies Arc ID, room,
display name and setpoint metadata.
Fake room temperature/humidity are explicitly demonstration data until SHT40 is wired.

After a user changes the setpoint and leaves it untouched for 3 seconds, the
ArcUnit sends a separate command:
```json
{"type":"command","deviceId":"ESP32-001122334455","panelId":"GuestBedroom-1","roomId":"GuestBedroom","commandId":"ESP32-001122334455-1","command":"ChangeSetpoint","arguments":{"SetpointName":"GuestBedroomSetpoint","TempF":68}}
```
The server resolves the assigned Arc ID and stored `SetpointName`, then looks
up the Home Assistant input-number entity in `ArcPanelConfig.json`. Only an
operational `-1` Arc ID may issue setpoint commands. The configuration names
the value argument and minimum/maximum accepted temperatures. The server calls
only `input_number.set_value`; it does not command a relay. The response
is an `ack` containing the `commandId`, `commandAccepted`, and `commandResult`.
An unacknowledged or rejected command remains pending on the panel and is retried.

Example configuration:
```json
{
  "Panels": {
    "GuestBedroom-1": {
      "Setpoints": {
        "GuestBedroomSetpoint": {
          "EntityId": "input_number.guest_bedroom_temperature_setpoint",
          "ArgumentName": "TempF",
          "Minimum": 50,
          "Maximum": 85
        }
      }
    }
  }
}
```
Copy and edit ArcServer's `ArcPanelConfig.json` for the installation. Its
panel keys are Arc IDs such as `GuestBedroom-1`. Set `ARC_CONFIG_PATH` to use a
different file. Panels no longer contain room or panel IDs in `Secrets.h`.

## First connection and ID selection

The panel sends a `build-info` message and then heartbeats using its persistent
MAC-derived `deviceId`. ArcServer creates or updates an `ArcDevices` row for
that hardware. If the device has no Arc ID, the server sends a page of
currently available enabled IDs (three per page):

```json
{"type":"assignment-options","deviceId":"ESP32-001122334455","arcIds":[{"arcId":"GuestBedroom-1","roomId":"GuestBedroom","roomName":"Guest Bedroom","operational":true},{"arcId":"GuestBedroom-2","roomId":"GuestBedroom","roomName":"Guest Bedroom (Test)","operational":false}]}
```

After a user taps one, the panel sends:

```json
{"type":"claim-arc-id","deviceId":"ESP32-001122334455","arcId":"GuestBedroom-1"}
```

To request another page, the panel sends `{"type":"list-arc-ids","deviceId":"ESP32-001122334455","page":1}`. ArcServer atomically assigns only an available ID and acknowledges with
`assignmentAccepted`, `arcId`, `roomId`, `roomName`, and `setpointName`. A
failed claim is rejected and the panel receives the free list again on its
next heartbeat. Arc's management UI can also transfer an ID to a different
discovered physical device; a transfer clears the previous assignment.

ArcServer exposes a loopback-only HTTP management API on port 5081 by default:
`GET/POST /api/arc-ids`, `PUT/DELETE /api/arc-ids/{arcId}`,
`GET /api/devices`, `GET /api/status`, `POST /api/assignments`, and
`DELETE /api/assignments/{deviceIdentity}`. Arc calls this API server-side;
the browser uses Arc's HTTP port 5080.

Server sends three newline-delimited JSON messages per heartbeat (the command
gets its own acknowledgement):
```json
{"type":"ack","deviceId":"ESP32-001122334455","requestType":"heartbeat","serverTime":"2026-10-22T12:00:00Z","roomId":"MasterBedroom","roomName":"MasterBedroom","temperatureUnits":"F"}
{"type":"news","schemaVersion":1,"location":"Franklin, PA","outsideTempF":58.5,"outsideHumidity":63,"forecast":"Today H:65 L:43 F Rain:20%","forecastTicker":"Today: H65 L43F Rain 20%  |  Fri: H61 L41F Rain 30%  |  Sat: H58 L39F Rain 10%  |  Sun: H60 L42F Rain 15%","forecastTickerC":"Today: H18 L6C Rain 20%  |  Fri: H16 L5C Rain 30%  |  Sat: H14 L4C Rain 10%  |  Sun: H16 L6C Rain 15%","alerts":[],"headlines":["National headline","World headline"],"dow":"47000","nasdaq":"23000","weatherUpdated":"2026-10-22T12:00:00Z","newsUpdated":"2026-10-22T12:00:00Z"}
{"type":"ha-info","roomId":"MasterBedroom","setpointF":70,"status":"Not connected","alerts":[]}
```
The server now attempts real Open-Meteo current/forecast data, NWS active weather alerts, BBC World/US RSS headlines and Stooq Dow/Nasdaq index quotes. Values above are illustrative, not actual observations or quotes. Unavailable values remain absent or marked unavailable; the server caches the last successful values. Set `ARC_LATITUDE`, `ARC_LONGITUDE` and `ARC_LOCATION_NAME` on the server to customize its location (defaults to approximate Franklin, PA). Refresh interval: 15 minutes. Quotes may be delayed, and Stooq availability varies.
Client serial logs use uptime seconds.milliseconds. The server writes its
timestamped diagnostics, received messages, sent messages, and command outcomes
to daily `logs/ArcServer-YYYY-MM-DD.log` files. `ARC_LOG_DIRECTORY` overrides
the log directory. The HA access token remains on the server.

## Client preferences and tickers

`TemperatureUnits` is `F` or `C`, changed by tapping the Settings temperature row. It is saved in ESP32 Preferences/NVS under namespace `arcpanel`, key `TemperatureUnits` (default `F`), and sent on **every heartbeat**. The server validates and records the per-device preference in memory and echoes it in the ACK. Temperatures and setpoints on the wire remain Fahrenheit for compatibility; the client converts displayed values to Celsius when requested. The setpoint chevrons change by 0.5 degrees in the selected display unit.

The News screen displays the 4-day Open-Meteo high/low/rain ticker, in the selected unit, instead of the previous single-day forecast line. The House screen displays a HA status/alert ticker. Both are locally animated on the ArcUnit and do not generate additional network messages. When HA is unavailable, the server reports its connection status and the client displays unavailable values.

Weather forecasts and financial/news data are fetched by the server every 15 minutes. Four-day forecast text is provided in both `forecastTicker` (F) and `forecastTickerC` (C); these are cached strings. No settings are erased on ordinary firmware upgrades.

## Colored tickers and HA REST snapshot

News now has two independent locally animated tickers: blue four-day
weather at y=116 and green Dow/Nasdaq at y=176. House has a cyan HA ticker
at y=190. Ticker glyphs are 12×16 pixels, matching the News location
heading; the financial values are no longer fixed labels.

`ha-info` adds `version`, `livingRoomTempF`, and `kitchenTempF`
(nullable), along with `status` and `alerts`. Example:

```json
{"type":"ha-info","roomId":"LivingRoom","setpointF":70,"status":"Online","version":"2026.10.0","livingRoomTempF":70.1,"kitchenTempF":71.4,"alerts":[]}
```

These values come from the Home Assistant REST monitor.
Until `ARC_HA_URL`, `ARC_HA_TOKEN` and relevant exact sensor entity IDs
are configured, status is `Not configured` and temperatures are null.
See README for configuration. HA version above is illustrative only.

## Firmware build metadata

On each TCP connection the ArcUnit sends a one-time `build-info` message before heartbeats:

```json
{"type":"build-info","schemaVersion":1,"deviceId":"ESP32-001122334455","roomId":"GuestBedroom","platform":"ESP32","board":"Waveshare ESP32-S3-Touch-LCD-2","arduinoCoreVersion":"3.x","espIdfVersion":"5.x","chipModel":"ESP32-S3","chipRevision":0,"cpuMHz":240,"flashBytes":8388608,"buildDate":"Nov 6 2026","buildTime":"10:00:00","arduinoJsonVersion":"7.4.3"}
```

Version strings and numeric values above are illustrative. The server acknowledges the message normally and persists an allowlisted per-device JSON manifest. See [Developer Guide](DeveloperGuide.md) for the output path and override. USB CDC is not required.

