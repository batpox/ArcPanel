#include <WiFi.h>
#include <Preferences.h>
#include <Wire.h>
#include <Arduino_GFX_Library.h>
#include "Secrets.h"

// Waveshare ESP32-S3-Touch-LCD-2: ST7789 240x320 and CST816D (0x15).
// Arduino IDE: ESP32S3 Dev Module, USB CDC On Boot enabled.
// Install "GFX Library for Arduino" using Library Manager.
static constexpr uint16_t SERVER_PORT = 5000;
static constexpr unsigned long SEND_MS = 2000;
static constexpr unsigned long RETRY_MS = 5000;
static constexpr size_t MAX_REPLY = 4096;
static constexpr int SCREEN_W = 240, SCREEN_H = 320;
static constexpr int TOUCH_SDA = 48, TOUCH_SCL = 47;
static constexpr uint8_t TOUCH_ADDR = 0x15;
static constexpr int LCD_BL = 1;
static constexpr uint16_t BG = 0x0841, PANEL = 0x18E3, ACCENT = 0xFCA0;
static constexpr uint16_t WHITE_TEXT = 0xFFFF, MUTED = 0xBDF7, GREEN_TEXT = 0x07E0;

Arduino_DataBus *bus = new Arduino_ESP32SPI(42, 45, 39, 38, 40);
Arduino_GFX *gfx = new Arduino_ST7789(bus, GFX_NOT_DEFINED, 0, true, SCREEN_W, SCREEN_H);
WiFiClient client;
Preferences preferences;
String deviceId, roomId, roomName, receiveBuffer;
unsigned long lastSend = 0, lastRetry = 0, lastWifiRetry = 0;
unsigned long lastAck = 0, lastDraw = 0, lastTouch = 0;
uint32_t heartbeatCount = 0;
bool wasWifiConnected = false, screenReady = false, dirty = true;
uint8_t selectedTab = 0;
const char* tabNames[] = {"ARC Room", "ARC House", "News", "Settings"};

void logMessage(const String& message) {
  unsigned long ms = millis();
  Serial.printf("[%lu.%03lu] %s\n", ms / 1000, ms % 1000, message.c_str());
}

void drawText(int x, int y, const String& s, uint16_t color, uint8_t scale = 1) {
  gfx->setTextSize(scale);
  gfx->setTextColor(color);
  gfx->setCursor(x, y);
  gfx->print(s);
}

void drawScreen() {
  if (!screenReady) return;
  gfx->fillScreen(BG);
  // Four large touch targets, arranged in two rows for the narrow 240px screen.
  for (int i = 0; i < 4; ++i) {
    int x = (i % 2) * 120, y = (i / 2) * 46;
    gfx->fillRoundRect(x + 3, y + 3, 114, 40, 7, i == selectedTab ? ACCENT : PANEL);
    drawText(x + 12, y + 17, tabNames[i], i == selectedTab ? BG : WHITE_TEXT);
  }
  gfx->drawFastHLine(0, 98, SCREEN_W, ACCENT);
  drawText(12, 112, tabNames[selectedTab], ACCENT, 2);
  if (selectedTab != 3) {
    drawText(12, 159, String("This is tab ") + tabNames[selectedTab], WHITE_TEXT);
  } else {
    drawText(12, 149, "RoomId:", MUTED);
    drawText(12, 165, roomId, WHITE_TEXT);
    drawText(12, 192, "RoomName:", MUTED);
    drawText(12, 208, roomName, WHITE_TEXT);
    drawText(12, 235, "Heartbeat:", MUTED);
    drawText(12, 251, String(heartbeatCount) + " sent / 2 sec", WHITE_TEXT);
    drawText(12, 272, String("ACK: ") + (lastAck ? String((millis() - lastAck) / 1000) + "s ago" : "none"), WHITE_TEXT);
  }
  gfx->drawFastHLine(0, 294, SCREEN_W, PANEL);
  String status = WiFi.status() != WL_CONNECTED ? "Wi-Fi offline" :
                  client.connected() ? "TCP connected" : "TCP disconnected";
  drawText(9, 302, status, client.connected() ? GREEN_TEXT : MUTED);
  dirty = false;
  lastDraw = millis();
}

// CST816D touch reports coordinates in registers 0x03..0x06.
// Polling avoids a dependency on a board-specific touch library.
bool readTouch(uint16_t& x, uint16_t& y) {
  Wire.beginTransmission(TOUCH_ADDR);
  Wire.write((uint8_t)0x02);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom(TOUCH_ADDR, (uint8_t)5) != 5) return false;
  uint8_t count = Wire.read();
  uint8_t xh = Wire.read(), xl = Wire.read(), yh = Wire.read(), yl = Wire.read();
  if ((count & 0x0F) == 0) return false;
  x = ((xh & 0x0F) << 8) | xl;
  y = ((yh & 0x0F) << 8) | yl;
  return x < SCREEN_W && y < SCREEN_H;
}

void pollTouch(unsigned long now) {
  if (now - lastTouch < 250) return; // debounce held fingers
  uint16_t x, y;
  if (!readTouch(x, y) || y >= 92) return;
  uint8_t tab = (y / 46) * 2 + (x / 120);
  if (tab < 4 && tab != selectedTab) {
    selectedTab = tab;
    dirty = true;
    logMessage(String("Selected tab: ") + tabNames[tab]);
  }
  lastTouch = now;
}

void setup() {
  Serial.begin(115200);
  preferences.begin("arcpanel", false);
  uint64_t mac = ESP.getEfuseMac();
  char id[32];
  snprintf(id, sizeof(id), "ESP32-%012llX", (unsigned long long)mac);
  deviceId = id;
  roomId = ROOM_ID;
  // A locally cached server-assigned display name may override the default.
  roomName = preferences.getString("room", roomId);
  if (roomName == "Unassigned" || roomName.isEmpty()) roomName = roomId;
  logMessage("Device: " + deviceId + " | RoomId: " + roomId + " | RoomName: " + roomName);

  pinMode(LCD_BL, OUTPUT);
  digitalWrite(LCD_BL, HIGH);
  Wire.begin(TOUCH_SDA, TOUCH_SCL);
  Wire.setClock(400000);
  screenReady = gfx->begin();
  if (!screenReady) logMessage("LCD initialization failed");
  else drawScreen();

  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  logMessage("Wi-Fi connecting...");
}

void loop() {
  unsigned long now = millis();
  pollTouch(now);
  if (WiFi.status() != WL_CONNECTED) {
    if (wasWifiConnected) { logMessage("Wi-Fi disconnected"); wasWifiConnected = false; dirty = true; }
    client.stop();
    receiveBuffer = "";
    if (now - lastWifiRetry >= RETRY_MS) {
      lastWifiRetry = now;
      WiFi.disconnect();
      WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
      logMessage("Retrying Wi-Fi");
    }
  } else {
    if (!wasWifiConnected) {
      wasWifiConnected = true;
      dirty = true;
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
          dirty = true;
        } else logMessage("TCP connection failed");
      }
    } else {
      if (now - lastSend >= SEND_MS) {
        lastSend = now;
        String payload = "{\"type\":\"heartbeat\",\"deviceId\":\"" + deviceId +
                         "\",\"roomId\":\"" + roomId + "\",\"uptimeMs\":" + String(now) + "}";
        if (client.println(payload) == 0) {
          logMessage("TCP send failed");
          client.stop();
          dirty = true;
        } else {
          ++heartbeatCount;
          logMessage("TX: " + payload);
          if (selectedTab == 3) dirty = true;
        }
      }
      while (client.available()) {
        char ch = (char)client.read();
        if (ch == '\n') {
          receiveBuffer.trim();
          if (receiveBuffer.length()) {
            logMessage("RX: " + receiveBuffer);
            if (receiveBuffer.indexOf("\"type\":\"ack\"") >= 0) {
              lastAck = millis();
              if (selectedTab == 3) dirty = true;
            }
            // Server configuration is not yet trusted/applied; preserve local RoomId.
          }
          receiveBuffer = "";
        } else if (receiveBuffer.length() >= MAX_REPLY) {
          logMessage("RX message too large; disconnecting");
          receiveBuffer = "";
          client.stop();
          dirty = true;
          break;
        } else if (ch != '\r') receiveBuffer += ch;
      }
    }
  }
  if (dirty || (selectedTab == 3 && now - lastDraw >= 1000)) drawScreen();
  delay(10);
}
