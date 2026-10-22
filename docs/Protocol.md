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
{"type":"news","outsideTempF":58.5,"outsideHumidity":63,"headlines":["News feed not configured","Connect a news provider","Outside weather is demo data","Home Assistant is not connected"]}
{"type":"ha-info","roomId":"MasterBedroom","setpointF":70}
```
The server currently provides **demo outside weather and placeholder news**, not live feeds.
It keeps received setpoints in memory (lost on server restart), not in Home Assistant.
Do not interpret the displayed setpoint as a live heating control until HA integration exists.

Client serial logs use uptime seconds.milliseconds; server logs use wall-clock timestamps.
