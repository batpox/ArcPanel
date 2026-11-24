# User Guide

Arc consists of ESP32 touchscreen panels, the headless ArcServer, the Blazor management UI, and Home Assistant. Home Assistant remains the authority for heating.

## Screens

- **Room** — displays the assigned room's available temperature and humidity, plus a setpoint adjusted with up/down controls. Current Home Assistant-backed room telemetry is implemented for **GuestBedroom**; unavailable readings show placeholders. Setpoint requests are debounced for approximately three seconds and then sent to the server.
- **House** — Home Assistant connection status, version, selected sensor readings, and alert ticker where configured.
- **News** — outside conditions, four-day forecast ticker, news headlines, and Dow/Nasdaq quotes. Feeds are best-effort and can be delayed or unavailable.
- **Settings** — Arc ID, hardware identity, heartbeat/connection status, and Fahrenheit/Celsius preference persisted in the ESP32.

## Operation

Start ArcServer and Arc. On first connection, tap one of the available Arc IDs shown on the panel. ArcServer records the ESP32 hardware identity and assignment. Arc's browser UI is at `http://<server-ip>:5080`; enter the temporary PIN printed in Arc's startup console. Set `ARC_ADMIN_PIN` to use your own persistent PIN. In **Arc IDs**, add room roles and instances. In **Devices**, select an Arc ID to transfer it to another physical panel. Every change becomes authoritative on the panel at its next heartbeat.

IDs are formatted `Role-N`. `-1` is operational and may change Home Assistant setpoints. Other numbered IDs are useful for test or spare panels and cannot change setpoints.

## Limitations

SHT40 local room sensing remains unimplemented in the current firmware; do not treat missing humidity as a valid measurement. GuestBedroom setpoint changes use the Home Assistant `input_number.set_value` service and are verified by reading the helper. There is no direct relay-control command. The server polls Home Assistant over REST rather than subscribing via WebSocket. Panel TCP traffic is not authenticated or encrypted; use only on a trusted private network. The management UI requires a PIN, but its HTTP traffic is not encrypted. This is not an emergency weather-alerting device.

[Documentation Index](README.md) · [Developer Guide](DeveloperGuide.md)
