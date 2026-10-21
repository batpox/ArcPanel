# Architecture

ArcPanel (Area Resource Control) consists of multiple Waveshare ESP32-S3-Touch-LCD-2 room panels, a Windows 11 / .NET 10 coordination server, and Linux Home Assistant.

- Panels: SHT40 temperature/humidity, setpoint input, touch UI, cached identity/configuration, always-visible room label.
- Server: multiple concurrent TCP connections, device registry, authoritative room configuration, HA integration, weather/news aggregation.
- Home Assistant: owns radiant-zone actuation and energy-train control. Panels never directly actuate heating hardware.

## Phase 1

Multiple clients are required from the outset. Each device has a persistent hardware-derived ID; server assigns room ID/name. Unknown devices display **Unassigned**. Local configuration is cached for offline display, but server configuration takes precedence.

## Safety

Setpoint changes are requests, not direct heating commands. HA is authoritative for accepted setpoints. No local relay actuation is implemented.
