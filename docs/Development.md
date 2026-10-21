# Development

## Server

Visual Studio 2026: open `source/Server/ArcPanel.Server.slnx`, target .NET 10, run the console app. TCP listener uses port 5000. Allow inbound TCP 5000 only on trusted private networks.

## Client

Arduino IDE: install Espressif's ESP32 board package; choose **ESP32S3 Dev Module** for Waveshare ESP32-S3-Touch-LCD-2 (not 2.1). Enable **USB CDC On Boot**. Copy `Secrets.example.h` to `Secrets.h`, set Wi-Fi and the Windows Ethernet IPv4 address. Open `source/Client/ArcPanel/ArcPanel.ino`, upload, then open Serial Monitor at 115200 baud.

Never commit `Secrets.h`. The Windows PC may remain Ethernet-only while panels connect over Wi-Fi, provided both networks permit LAN communication.

## Conventions

C# (.NET 10), Arduino C++, timestamped logs on both ends, recoverable network errors, descriptive documentation, and version numbers formatted `yy.mm.dd.HH`. Prefer small, independently testable increments.
