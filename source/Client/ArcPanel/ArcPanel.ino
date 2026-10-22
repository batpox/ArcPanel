#include <WiFi.h>
#include <Preferences.h>
#include <Wire.h>
#include <Arduino_GFX_Library.h>
#include <ArduinoJson.h>
#include <math.h>
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
unsigned long lastAck = 0, lastDraw = 0, lastTouch = 0, setpointChangedAt = 0;
float roomTemp = 70.5f, roomHumidity = 46.0f, setpoint = 70.0f;
float outsideTemp = NAN, outsideHumidity = NAN;
String headlines[4] = {"Waiting for news...", "", "", ""};
String forecastText = "Forecast unavailable", weatherAlert = "", dowText = "--", nasdaqText = "--", weatherLocation = "";
static constexpr uint8_t BODY_SCALE = 2; // 12px character width; tabs retain scale 1
bool pendingSetpoint = false, haveHaInfo = false;
float haSetpoint = NAN;
uint32_t heartbeatCount = 0;
bool wasWifiConnected = false, screenReady = false, dirty = true;
String lastStatus, lastNewsPayload;
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

// Draw a ring without repainting the whole display on each heartbeat.
void drawRing(int cx, int cy, int radius, float degrees, uint16_t color, int thickness) {
  degrees = constrain(degrees, 0.0f, 360.0f);
  for (int d = 0; d < (int)degrees; d += 3) {
    float a = (d - 90) * PI / 180.0f;
    for (int t = 0; t < thickness; ++t)
      gfx->drawLine(cx + (int)((radius-t)*cosf(a)), cy + (int)((radius-t)*sinf(a)),
                    cx + (int)((radius-t)*cosf(a+3*PI/180.0f)), cy + (int)((radius-t)*sinf(a+3*PI/180.0f)), color);
  }
}

// Layout is intentionally based on SCREEN_W/SCREEN_H, ready for display profiles.
String clipped(const String& value, int maxChars) {
  return value.length() <= (unsigned)maxChars ? value : value.substring(0, maxChars);
}

void drawTabs() {
  for (int i = 0; i < 4; ++i) {
    int x = i * 60;
    gfx->fillRoundRect(x + 1, 2, 58, 40, 5, i == selectedTab ? ACCENT : PANEL);
    drawText(x + 4, 17, clipped(tabNames[i], 8), i == selectedTab ? BG : WHITE_TEXT);
  }
  gfx->drawFastHLine(0, 45, SCREEN_W, ACCENT);
}

void drawContent() {
  gfx->fillRect(0, 47, SCREEN_W, 247, BG);
  if (selectedTab == 0) {
    drawText(10, 56, clipped(roomName, 19), ACCENT, 2);
    drawText(12, 85, String("Room: ") + String(roomTemp, 1) + " F", WHITE_TEXT, BODY_SCALE);
    drawText(12, 108, String("RH: ") + String(roomHumidity, 0) + "%", WHITE_TEXT, BODY_SCALE);
    // Outer ring: setpoint. Inner ring: measured temperature, normalized to 50..85 F.
    drawRing(120, 205, 80, 360, PANEL, 7);
    drawRing(120, 205, 80, 360 * (setpoint - 50) / 35, ACCENT, 7);
    drawRing(120, 205, 65, 360, PANEL, 5);
    drawRing(120, 205, 65, 360 * (roomTemp - 50) / 35, GREEN_TEXT, 5);
    drawText(79, 194, String(setpoint, 1) + " F", WHITE_TEXT, 2);
    drawText(70, 224, "Setpoint", MUTED);
    drawText(8, 280, "Tap outer ring to adjust", MUTED);
  } else if (selectedTab == 1) {
    drawText(12, 70, "ARC House", ACCENT, 2);
    drawText(12, 115, "ARC House", WHITE_TEXT, BODY_SCALE);
    if (haveHaInfo) drawText(12, 143, "HA: " + String(haSetpoint, 1) + " F", WHITE_TEXT, BODY_SCALE);
  } else if (selectedTab == 2) {
    drawText(8, 53, clipped(weatherLocation.length() ? weatherLocation : "Local weather", 19), ACCENT, BODY_SCALE);
    drawText(8, 78, isnan(outsideTemp) ? "Temp: --  RH: --" :
      "Temp:" + String(outsideTemp, 0) + " RH:" + String(outsideHumidity, 0) + "%", WHITE_TEXT, BODY_SCALE);
    drawText(8, 103, clipped(forecastText, 19), WHITE_TEXT, BODY_SCALE);
    if (weatherAlert.length())
      drawText(8, 126, clipped("ALERT: " + weatherAlert, 19), ACCENT, BODY_SCALE);
    else drawText(8, 126, "No active NWS alert", MUTED);
    drawText(8, 151, clipped("Dow: " + dowText, 19), WHITE_TEXT, BODY_SCALE);
    drawText(8, 175, clipped("Nasdaq: " + nasdaqText, 19), WHITE_TEXT, BODY_SCALE);
    drawText(8, 200, "National / World", ACCENT);
    for (int i = 0; i < 4; ++i)
      drawText(8, 217 + i * 19, clipped(headlines[i], 19), WHITE_TEXT, BODY_SCALE);
  } else {
    drawText(12, 60, "Settings", ACCENT, 2);
    drawText(12, 98, "ID:" + clipped(roomId, 16), WHITE_TEXT, BODY_SCALE);
    drawText(12, 125, clipped(roomName, 18), WHITE_TEXT, BODY_SCALE);
    drawText(12, 158, "Beats: " + String(heartbeatCount), WHITE_TEXT, BODY_SCALE);
    drawText(12, 185, String("ACK: ") + (lastAck ? String((millis()-lastAck)/1000) + "s ago" : "none"), WHITE_TEXT, BODY_SCALE);
    drawText(12, 212, "Interval: 2 seconds", MUTED);
  }
}

void updateSettingsTelemetry() {
  if (!screenReady || selectedTab != 3) return;
  gfx->fillRect(8, 152, 226, 55, BG);
  drawText(12, 158, "Beats: " + String(heartbeatCount), WHITE_TEXT, BODY_SCALE);
  drawText(12, 185, String("ACK: ") + (lastAck ? String((millis()-lastAck)/1000) + "s ago" : "none"), WHITE_TEXT, BODY_SCALE);
}

void drawStatus() {
  String status = WiFi.status() != WL_CONNECTED ? "Wi-Fi offline" :
                  client.connected() ? "TCP connected" : "TCP disconnected";
  if (status == lastStatus) return;
  gfx->fillRect(0, 295, SCREEN_W, 25, BG);
  gfx->drawFastHLine(0, 295, SCREEN_W, PANEL);
  drawText(9, 303, status, client.connected() ? GREEN_TEXT : MUTED);
  lastStatus = status;
}

void drawScreen() {
  if (!screenReady) return;
  // Full repaint only when changing tabs, not for heartbeat/ACK updates.
  gfx->fillScreen(BG);
  drawTabs();
  drawContent();
  lastStatus = "";
  drawStatus();
  dirty = false;
  lastDraw = millis();
}

void refreshContent() {
  if (!screenReady) return;
  drawContent();
  drawStatus();
  lastDraw = millis();
}

void handleServerMessage(const String& line) {
  JsonDocument doc;
  DeserializationError error = deserializeJson(doc, line);
  if (error) { logMessage(String("Invalid server JSON: ") + error.c_str()); return; }
  const char* type = doc["type"] | "";
  if (strcmp(type, "ack") == 0) {
    lastAck = millis();
    if (selectedTab == 3) updateSettingsTelemetry();
  } else if (strcmp(type, "news") == 0) {
    if (line == lastNewsPayload) return; // Server sends cached news with each heartbeat.
    lastNewsPayload = line;
    outsideTemp = doc["outsideTempF"].is<double>() || doc["outsideTempF"].is<int>() ? doc["outsideTempF"].as<float>() : NAN;
    outsideHumidity = doc["outsideHumidity"].is<double>() || doc["outsideHumidity"].is<int>() ? doc["outsideHumidity"].as<float>() : NAN;
    forecastText = doc["forecast"] | "Forecast unavailable";
    weatherLocation = doc["location"] | "Local weather";
    dowText = doc["dow"] | "Unavailable";
    nasdaqText = doc["nasdaq"] | "Unavailable";
    JsonArray alerts = doc["alerts"].as<JsonArray>();
    weatherAlert = alerts.size() ? alerts[0].as<String>() : "";
    JsonArray stories = doc["headlines"].as<JsonArray>();
    for (int i = 0; i < 4; ++i) headlines[i] = i < stories.size() ? stories[i].as<String>() : "";
    if (selectedTab == 2) refreshContent();
  } else if (strcmp(type, "ha-info") == 0) {
    if (doc["setpointF"].is<double>() || doc["setpointF"].is<int>()) {
      haSetpoint = doc["setpointF"].as<float>();
      haveHaInfo = true;
      // Don't overwrite a local adjustment waiting to be transmitted.
      if (!pendingSetpoint && fabsf(setpoint - haSetpoint) > 0.01f) { setpoint = haSetpoint; if (selectedTab == 0) refreshContent(); }
      if (selectedTab == 1) refreshContent();
    }
  }
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
  if (now - lastTouch < 200) return;
  uint16_t x, y;
  if (!readTouch(x, y)) return;
  if (y < 45) {
    uint8_t tab = x / 60;
    if (tab < 4 && tab != selectedTab) {
      selectedTab = tab;
      dirty = true;
      logMessage(String("Selected tab: ") + tabNames[tab]);
    }
  } else if (selectedTab == 0) {
    float dx = (float)x - 120, dy = (float)y - 205;
    float radius = sqrtf(dx*dx + dy*dy);
    if (radius >= 68 && radius <= 91) {
      float angle = atan2f(dy, dx) * 180.0f / PI + 90;
      if (angle < 0) angle += 360;
      float requested = roundf((50.0f + 35.0f * angle / 360.0f) * 2) / 2.0f;
      if (fabsf(requested - setpoint) >= 0.25f) {
        setpoint = requested;
        setpointChangedAt = now;
        pendingSetpoint = true;
        refreshContent();
        logMessage("Local setpoint: " + String(setpoint, 1) + " F");
      }
    }
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
        JsonDocument hb;
        hb["type"] = "heartbeat";
        hb["deviceId"] = deviceId;
        hb["roomId"] = roomId;
        hb["uptimeMs"] = now;
        hb["roomTempF"] = roomTemp;
        hb["roomHumidity"] = roomHumidity;
        bool sendSetpoint = pendingSetpoint && now - setpointChangedAt >= 3000;
        if (sendSetpoint) hb["setpointF"] = setpoint;
        String payload;
        serializeJson(hb, payload);
        if (client.println(payload) == 0) {
          logMessage("TCP send failed");
          client.stop();
          dirty = true;
        } else {
          ++heartbeatCount;
          if (sendSetpoint) pendingSetpoint = false;
          logMessage("TX: " + payload);
          if (selectedTab == 3) updateSettingsTelemetry();
        }
      }
      while (client.available()) {
        char ch = (char)client.read();
        if (ch == '\n') {
          receiveBuffer.trim();
          if (receiveBuffer.length()) {
            logMessage("RX: " + receiveBuffer);
            handleServerMessage(receiveBuffer);
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
  if (dirty) drawScreen();
  else if (screenReady) drawStatus();
  if (screenReady && selectedTab == 3 && now - lastDraw >= 1000) { updateSettingsTelemetry(); lastDraw = now; }
  delay(10);
}
