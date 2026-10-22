# ARC wire protocol — iteration 1

TCP port 5000, newline-delimited UTF-8 JSON; one JSON object per line.
Trusted LAN only; no authentication or encryption yet.

Every 2 seconds, the ArcUnit sends:
```json
{"type":"heartbeat","deviceId":"ESP32-001122334455","roomId":"MasterBedroom","uptimeMs":12345,"roomTempF":70.5,"roomHumidity":46}
```
After a user changes the circular setpoint and leaves it untouched for 3 seconds,
the next heartbeat includes `"setpointF":72.0` (one-shot change notification).
Fake room temperature/humidity are explicitly demonstration data until SHT40 is wired.

Server sends three newline-delimited JSON messages per heartbeat:
```json
{"type":"ack","deviceId":"ESP32-001122334455","requestType":"heartbeat","serverTime":"2026-10-22T12:00:00Z","roomId":"MasterBedroom","roomName":"MasterBedroom"}
{"type":"news","schemaVersion":1,"location":"Franklin, PA","outsideTempF":58.5,"outsideHumidity":63,"forecast":"Today H:65 L:43 F Rain:20%","alerts":[],"headlines":["National headline","World headline"],"dow":"47000","nasdaq":"23000","weatherUpdated":"2026-10-22T12:00:00Z","newsUpdated":"2026-10-22T12:00:00Z"}
{"type":"ha-info","roomId":"MasterBedroom","setpointF":70}
```
The server now attempts real Open-Meteo current/forecast data, NWS active weather alerts, BBC World/US RSS headlines and Stooq Dow/Nasdaq index quotes. Values above are illustrative, not actual observations or quotes. Unavailable values remain absent or marked unavailable; the server caches the last successful values. Set `ARC_LATITUDE`, `ARC_LONGITUDE` and `ARC_LOCATION_NAME` on the server to customize its location (defaults to approximate Franklin, PA). Refresh interval: 15 minutes. Quotes may be delayed, and Stooq availability varies.
It keeps received setpoints in memory (lost on server restart), not in Home Assistant.
Do not interpret the displayed setpoint as a live heating control until HA integration exists.

Client serial logs use uptime seconds.milliseconds; server logs use wall-clock timestamps.
