# Architecture

Arc (Area Resource Control) consists of multiple Waveshare ESP32-S3-Touch-LCD-2 panels, the headless .NET 10 ArcServer, the Blazor Server Arc UI, and Linux Home Assistant.

- Panels: SHT40 temperature/humidity, setpoint input, touch UI, cached identity/configuration, always-visible room label.
- ArcServer: multi-client TCP listener, SQLite device and Arc ID registry, loopback management API, authoritative room configuration, HA integration, weather/news aggregation.
- Arc: Dan's Standard Layout monitoring and registry management UI, served to browsers on the LAN.
- Home Assistant: owns radiant-zone actuation and energy-train control. Panels never directly actuate heating hardware.

## Phase 1

Multiple clients are supported. Each ESP32 reports a persistent MAC-derived hardware identity. ArcServer sends available logical IDs and the panel selects one by touch. Arc IDs use `Role-N`; `-1` is operational and other instance numbers are non-operational. Arc can transfer an ID between physical devices. ArcServer is authoritative; a panel caches its current assignment for display while reconnecting.

## Safety

Setpoint changes are requests, not direct heating commands. HA is authoritative for accepted setpoints. No local relay actuation is implemented.

## Implementation Checkpoint (November 2026)

ArcServer stores configured IDs in `ArcIds` and discovered hardware in `ArcDevices` in SQLite. Arc shows registry state, panel presence, and the Home Assistant status/sensors/relay snapshot. ArcServer polls Home Assistant over REST and supports GuestBedroom temperature, setpoint and read-only GuestFloor state; setpoint requests call the HA input-number service and verify the resulting helper value. Only an enabled `-1` Arc ID may issue a setpoint command. Weather/news/market feeds are server-side best-effort services. SHT40 firmware reading, authenticated/encrypted transport, and HA WebSocket updates remain future work. The TCP panel listener is restricted by deployment network policy, not application authentication; the management API defaults to loopback.

See [Developer Guide](DeveloperGuide.md), [User Guide](UserGuide.md), and [Protocol](Protocol.md).
