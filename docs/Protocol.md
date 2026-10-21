# Protocol v0 — learning baseline

Transport: TCP port 5000, newline-delimited UTF-8 JSON. One JSON object per line. No encryption or authentication yet: use on a trusted LAN only.

Client example:
```json
{"type":"heartbeat","deviceId":"ESP32-001122334455","uptimeMs":12345}
```

Server reply:
```json
{"type":"ack","deviceId":"ESP32-001122334455","requestType":"heartbeat","serverTime":"2026-10-21T18:00:00.0000000+00:00","roomId":null,"roomName":"Unassigned"}
```

This is an initial connectivity demonstration, **not** the finalized versioned configuration protocol. The client logs replies but does not yet apply room assignments or read the SHT40. Message length is bounded after reading on the server; a future increment will enforce bounds during streaming and add schema validation, heartbeats and configuration revisions. Logs use timestamp prefixes (client uptime seconds.milliseconds; server wall-clock milliseconds with offset).
