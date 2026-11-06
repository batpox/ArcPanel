# User Guide

ArcPanel consists of small touchscreen **ArcUnits** communicating with a Windows .NET server. Home Assistant remains the authority for heating.

## Screens

- **Room** — displays the assigned room's available temperature and humidity, plus a setpoint adjusted with up/down controls. Current Home Assistant-backed room telemetry is implemented for **GuestBedroom**; unavailable readings show placeholders. Setpoint requests are debounced for approximately three seconds and then sent to the server.
- **House** — Home Assistant connection status, version, selected sensor readings, and alert ticker where configured.
- **News** — outside conditions, four-day forecast ticker, news headlines, and Dow/Nasdaq quotes. Feeds are best-effort and can be delayed or unavailable.
- **Settings** — device identity, heartbeat/connection status, and Fahrenheit/Celsius preference persisted in the ESP32.

## Operation

Start the Windows server and ensure the ArcUnit has Wi-Fi connectivity and can reach TCP port 5000. The panel sends periodic heartbeats and receives acknowledgments, news, and Home Assistant status. Room name is currently derived from the reported room ID; generalized server-managed assignment is not complete.

## Limitations

SHT40 local room sensing remains unimplemented in the current firmware; do not treat missing humidity as a valid measurement. GuestBedroom setpoint changes use the Home Assistant `input_number.set_value` service and are verified by reading the helper. There is no direct relay-control command. The server polls Home Assistant over REST rather than subscribing via WebSocket. The TCP protocol is not authenticated or encrypted; use only on a trusted private network. This is not an emergency weather-alerting device.

[Documentation Index](README.md) · [Developer Guide](DeveloperGuide.md)
