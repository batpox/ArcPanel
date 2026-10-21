#include <WiFi.h>
#include <Preferences.h>
#include "Secrets.h"

// Waveshare ESP32-S3-Touch-LCD-2, Arduino ESP32S3 Dev Module.
// Tools > USB CDC On Boot > Enabled.
static constexpr uint16_t SERVER_PORT = 5000;
static constexpr unsigned long SEND_MS = 2000;
static constexpr unsigned long RETRY_MS = 5000;
static constexpr size_t MAX_REPLY = 4096;

WiFiClient client;
Preferences preferences;
String deviceId;
String roomName;
String receiveBuffer;
unsigned long lastSend = 0;
unsigned long lastRetry = 0;
unsigned long lastWifiRetry = 0;
bool wasWifiConnected = false;

void logMessage(const String& message) {
  unsigned long ms = millis();
  Serial.printf("[%lu.%03lu] %s\n", ms / 1000, ms % 1000, message.c_str());
}

void setup() {
  Serial.begin(115200);
  preferences.begin("arcpanel", false);
  uint64_t mac = ESP.getEfuseMac();
  char id[32];
  snprintf(id, sizeof(id), "ESP32-%012llX", (unsigned long long)mac);
  deviceId = id;
  roomName = preferences.getString("room", "Unassigned");
  logMessage("Device: " + deviceId + " | Room: " + roomName);
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  logMessage("Wi-Fi connecting...");
}

void loop() {
  unsigned long now = millis();
  if (WiFi.status() != WL_CONNECTED) {
    if (wasWifiConnected) { logMessage("Wi-Fi disconnected"); wasWifiConnected = false; }
    client.stop();
    receiveBuffer = "";
    if (now - lastWifiRetry >= RETRY_MS) {
      lastWifiRetry = now;
      WiFi.disconnect();
      WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
      logMessage("Retrying Wi-Fi");
    }
    delay(20);
    return;
  }
  if (!wasWifiConnected) {
    wasWifiConnected = true;
    logMessage("Wi-Fi connected: " + WiFi.localIP().toString());
  }
  if (!client.connected()) {
    client.stop();
    receiveBuffer = "";
    if (now - lastRetry >= RETRY_MS) {
      lastRetry = now;
      logMessage("Connecting TCP...");
      if (client.connect(SERVER_IP, SERVER_PORT, 2000)) {
        logMessage("TCP connected");
        lastSend = millis() - SEND_MS;
      } else logMessage("TCP connection failed");
    }
    delay(10);
    return;
  }
  if (now - lastSend >= SEND_MS) {
    lastSend = now;
    String payload = "{\"type\":\"heartbeat\",\"deviceId\":\"" + deviceId +
                     "\",\"uptimeMs\":" + String(now) + "}";
    if (client.println(payload) == 0) {
      logMessage("TCP send failed");
      client.stop();
    } else logMessage("TX: " + payload);
  }
  while (client.available()) {
    char ch = (char)client.read();
    if (ch == '\n') {
      receiveBuffer.trim();
      if (receiveBuffer.length()) {
        logMessage("RX: " + receiveBuffer);
        // Room assignments will be applied only after versioned config validation.
      }
      receiveBuffer = "";
    } else if (receiveBuffer.length() >= MAX_REPLY) {
      logMessage("RX message too large; disconnecting");
      receiveBuffer = "";
      client.stop();
      break;
    } else if (ch != '\r') receiveBuffer += ch;
  }
  delay(10);
}
