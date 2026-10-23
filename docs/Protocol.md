# ARC wire protocol — iteration 1

TCP port 5000, newline-delimited UTF-8 JSON; one JSON object per line.
Trusted LAN only; no authentication or encryption yet.

Every 2 seconds, the ArcUnit sends:
```json
{"type":"heartbeat","deviceId":"ESP32-001122334455","roomId":"MasterBedroom","uptimeMs":12345,"roomTempF":70.5,"roomHumidity":46,"temperatureUnits":"F"}
```
After a user changes the up/down setpoint and leaves it untouched for 3 seconds,
the next heartbeat includes `"setpointF":72.0` (one-shot change notification).
Fake room temperature/humidity are explicitly demonstration data until SHT40 is wired.

Server sends three newline-delimited JSON messages per heartbeat:
```json
{"type":"ack","deviceId":"ESP32-001122334455","requestType":"heartbeat","serverTime":"2026-10-22T12:00:00Z","roomId":"MasterBedroom","roomName":"MasterBedroom","temperatureUnits":"F"}
{"type":"news","schemaVersion":1,"location":"Franklin, PA","outsideTempF":58.5,"outsideHumidity":63,"forecast":"Today H:65 L:43 F Rain:20%","forecastTicker":"Today: H65 L43F Rain 20%  |  Fri: H61 L41F Rain 30%  |  Sat: H58 L39F Rain 10%  |  Sun: H60 L42F Rain 15%","forecastTickerC":"Today: H18 L6C Rain 20%  |  Fri: H16 L5C Rain 30%  |  Sat: H14 L4C Rain 10%  |  Sun: H16 L6C Rain 15%","alerts":[],"headlines":["National headline","World headline"],"dow":"47000","nasdaq":"23000","weatherUpdated":"2026-10-22T12:00:00Z","newsUpdated":"2026-10-22T12:00:00Z"}
{"type":"ha-info","roomId":"MasterBedroom","setpointF":70,"status":"Not connected","alerts":[]}
```
The server now attempts real Open-Meteo current/forecast data, NWS active weather alerts, BBC World/US RSS headlines and Stooq Dow/Nasdaq index quotes. Values above are illustrative, not actual observations or quotes. Unavailable values remain absent or marked unavailable; the server caches the last successful values. Set `ARC_LATITUDE`, `ARC_LONGITUDE` and `ARC_LOCATION_NAME` on the server to customize its location (defaults to approximate Franklin, PA). Refresh interval: 15 minutes. Quotes may be delayed, and Stooq availability varies.
It keeps received setpoints in memory (lost on server restart), not in Home Assistant.
Do not interpret the displayed setpoint as a live heating control until HA integration exists.

Client serial logs use uptime seconds.milliseconds; server logs use wall-clock timestamps.

## Client preferences and tickers

`TemperatureUnits` is `F` or `C`, changed by tapping the Settings temperature row. It is saved in ESP32 Preferences/NVS under namespace `arcpanel`, key `TemperatureUnits` (default `F`), and sent on **every heartbeat**. The server validates and records the per-device preference in memory and echoes it in the ACK. Temperatures and setpoints on the wire remain Fahrenheit for compatibility; the client converts displayed values to Celsius when requested. The setpoint chevrons change by 0.5 degrees in the selected display unit.

The News screen displays the 4-day Open-Meteo high/low/rain ticker, in the selected unit, instead of the previous single-day forecast line. The House screen displays a HA status/alert ticker. Both are locally animated on the ArcUnit and do not generate additional network messages. **Home Assistant API integration has not yet been implemented:** the server explicitly sends `status: "Not connected"` and an empty `alerts` array. The client displays `Alerts: Unavailable` rather than falsely claiming there are no HA alerts. The server must be connected to HA before real status and alerts can appear.

Weather forecasts and financial/news data are fetched by the server every 15 minutes. Four-day forecast text is provided in both `forecastTicker` (F) and `forecastTickerC` (C); these are cached strings. No settings are erased on ordinary firmware upgrades.
