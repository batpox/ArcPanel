#include <WiFi.h>
#include <Preferences.h>
#include <Wire.h>
#include <Arduino_GFX_Library.h>
#include <Adafruit_GFX.h> // Makes Arduino resolve the library that owns Fonts/*.h.
#include <ArduinoJson.h>
#include <esp_arduino_version.h>
#include <math.h>
#include <time.h>
#include <Fonts/FreeSans9pt7b.h>
#include <Fonts/FreeSansBold12pt7b.h>
#include "Secrets.h"

// Waveshare ESP32-S3-Touch-LCD-2: ST7789 240x320 and CST816D (0x15).
// Arduino IDE: ESP32S3 Dev Module, USB CDC On Boot disabled (preserve working configuration).
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
float roomTemp = NAN, roomHumidity = NAN, setpoint = 70.0f;
int8_t guestFloorState = -1; // -1 unknown, 0 off, 1 on
float outsideTemp = NAN, outsideHumidity = NAN;
String headlines[4] = {"Waiting for news...", "", "", ""};
String forecastText = "Forecast unavailable", weatherAlert = "", dowText = "--", nasdaqText = "--", weatherLocation = "";
static constexpr uint8_t BODY_SCALE = 2;
static constexpr int TAB_HEIGHT = 66, TAB_BOTTOM = TAB_HEIGHT + 3; // 69px touch targets
// Body text is 12px wide; tab labels use large 12x16px glyphs.
bool pendingSetpoint = false, haveHaInfo = false;
float haSetpoint = NAN;
uint32_t heartbeatCount = 0;
bool wasWifiConnected = false, screenReady = false, dirty = true;
String lastStatus, lastNewsPayload;
String forecastTickerF = "Forecast pending", forecastTickerC = "Forecast pending";
String haStatus = "Not configured", haVersion = "Unknown", haAlerts = "";
float haLivingF = NAN, haKitchenF = NAN;
char temperatureUnits = 'F';
unsigned long lastTickerDraw = 0, tickerStart = 0, tickerPausedAt = 0, lastClockDraw = 0;
bool tickerPaused = false;
String lastClockMinute;
static constexpr int TICKER_H = 30, TICKER_SPEED = 32;
static constexpr uint16_t WEATHER_BLUE = 0x4DDF, FINANCE_GREEN = 0x07E0, HA_CYAN = 0x07FF;
uint8_t selectedTab = 0;
const char* tabNames[] = {"Room", "House", "News", "Settings"};

/**
 * @brief Writes an uptime-stamped message to the serial monitor.
 * @param message message input or output value.
 */
void logMessage(const String& message) {
  unsigned long ms = millis();
  Serial.printf("[%lu.%03lu] %s\n", ms / 1000, ms % 1000, message.c_str());
}

/**
 * @brief Sends the firmware build metadata once per TCP connection.
 * @details Uses the existing newline-delimited JSON protocol; no USB CDC or secrets required.
 * @return True when the complete message was accepted by the TCP client.
 */
bool SendBuildInfo() {
  JsonDocument info;
  info["type"] = "build-info";
  info["schemaVersion"] = 1;
  info["deviceId"] = deviceId;
  info["roomId"] = roomId;
  info["platform"] = "ESP32";
  info["board"] = "Waveshare ESP32-S3-Touch-LCD-2";
  info["arduinoCoreVersion"] = ESP_ARDUINO_VERSION_STR;
  info["espIdfVersion"] = ESP.getSdkVersion();
  info["chipModel"] = ESP.getChipModel();
  info["chipRevision"] = ESP.getChipRevision();
  info["cpuMHz"] = ESP.getCpuFreqMHz();
  info["flashBytes"] = ESP.getFlashChipSize();
  info["buildDate"] = __DATE__;
  info["buildTime"] = __TIME__;
  info["arduinoJsonVersion"] = String(ARDUINOJSON_VERSION_MAJOR) + "." +
      String(ARDUINOJSON_VERSION_MINOR) + "." + String(ARDUINOJSON_VERSION_REVISION);
  String payload;
  serializeJson(info, payload);
  return client.println(payload) == payload.length() + 2;
}

/**
 * @brief Converts a Fahrenheit temperature to the selected display units.
 * @param fahrenheit fahrenheit input or output value.
 * @return The computed value or operation result.
 */
float displayTemp(float fahrenheit) { return temperatureUnits == 'C' ? (fahrenheit - 32.0f) * 5.0f / 9.0f : fahrenheit; }
/**
 * @brief Formats a Fahrenheit temperature with its selected unit.
 * @param fahrenheit fahrenheit input or output value.
 * @return The computed value or operation result.
 */
String tempLabel(float fahrenheit) { return String(displayTemp(fahrenheit), 1) + String(temperatureUnits); }

/**
 * @brief Draws scaled text at a display coordinate.
 * @param x x input or output value.
 * @param y y input or output value.
 * @param s s input or output value.
 * @param color color input or output value.
 * @param scale scale input or output value.
 */
void drawText(int x, int y, const String& s, uint16_t color, uint8_t scale = 1) {
  gfx->setTextSize(scale);
  gfx->setTextColor(color);
  gfx->setCursor(x, y);
  gfx->print(s);
}

/**
 * @brief Draws smooth proportional text using a bundled GFX font.
 * @param x Left-side cursor position in pixels.
 * @param baseline Baseline position in pixels.
 * @param value Text to render.
 * @param color RGB565 foreground color.
 * @param large Selects the larger bold face.
 */
void DrawSmoothText(int x, int baseline, const String& value, uint16_t color, bool large = false) {
  gfx->setFont(large ? &FreeSansBold12pt7b : &FreeSans9pt7b);
  gfx->setTextSize(1);
  gfx->setTextColor(color);
  gfx->setCursor(x, baseline);
  gfx->print(value);
  gfx->setFont();
}

/**
 * @brief Refreshes the local date and clock only when the displayed minute changes.
 */
void DrawRoomClock() {
  if (!screenReady || selectedTab != 0) return;
  struct tm local;
  if (!getLocalTime(&local, 0)) return;
  char timeText[24], dateText[32];
  strftime(timeText, sizeof(timeText), "%I:%M %p", &local);
  strftime(dateText, sizeof(dateText), "%a, %b %d, %Y", &local);
  String minuteKey = String(dateText) + timeText;
  if (minuteKey == lastClockMinute) return;
  lastClockMinute = minuteKey;
  gfx->fillRect(0, 72, SCREEN_W, 51, BG);
  DrawSmoothText(9, 96, timeText, GREEN_TEXT, true);
  DrawSmoothText(9, 118, dateText, GREEN_TEXT);
}

// High-contrast chevron, sized for a finger rather than a stylus.
/**
 * @brief Draws a directional setpoint control chevron.
 * @param cx cx input or output value.
 * @param cy cy input or output value.
 * @param up up input or output value.
 * @param color color input or output value.
 */
void drawChevron(int cx, int cy, bool up, uint16_t color) {
  int tipY = up ? cy - 10 : cy + 10;
  int baseY = up ? cy + 7 : cy - 7;
  for (int offset = -2; offset <= 2; ++offset) {
    gfx->drawLine(cx - 17, baseY + offset, cx, tipY + offset, color);
    gfx->drawLine(cx, tipY + offset, cx + 17, baseY + offset, color);
  }
}

// Layout is intentionally based on SCREEN_W/SCREEN_H, ready for display profiles.
/**
 * @brief Truncates text to the specified display character count.
 * @param value value input or output value.
 * @param maxChars maxChars input or output value.
 * @return The computed value or operation result.
 */
String clipped(const String& value, int maxChars) {
  return value.length() <= (unsigned)maxChars ? value : value.substring(0, maxChars);
}

// Simple vector gear: independent of installed fonts and display resolution.
/**
 * @brief Draws the settings gear icon.
 * @param cx cx input or output value.
 * @param cy cy input or output value.
 * @param color color input or output value.
 */
void drawSettingsGear(int cx, int cy, uint16_t color) {
  gfx->fillCircle(cx, cy, 15, color);
  for (int i = 0; i < 8; ++i) {
    float angle = i * PI / 4.0f;
    int x1 = cx + (int)(13 * cosf(angle));
    int y1 = cy + (int)(13 * sinf(angle));
    int x2 = cx + (int)(21 * cosf(angle));
    int y2 = cy + (int)(21 * sinf(angle));
    gfx->drawLine(x1, y1, x2, y2, color);
    gfx->fillCircle(x2, y2, 3, color);
  }
  gfx->fillCircle(cx, cy, 7, selectedTab == 3 ? ACCENT : PANEL);
}

/**
 * @brief Draws the four touch-selectable navigation tabs.
 */
void drawTabs() {
  for (int i = 0; i < 4; ++i) {
    int x = i * 60;
    gfx->fillRoundRect(x + 1, 2, 58, TAB_HEIGHT, 6, i == selectedTab ? ACCENT : PANEL);
    uint16_t ink = i == selectedTab ? BG : WHITE_TEXT;
    if (i == 3) {
      drawSettingsGear(x + 30, 34, ink);
    } else {
      gfx->setTextSize(2);
      gfx->setTextColor(ink);
      gfx->setCursor(x + (60 - (int)strlen(tabNames[i]) * 12) / 2, 27);
      gfx->print(tabNames[i]);
    }
  }
  gfx->drawFastHLine(0, TAB_BOTTOM, SCREEN_W, ACCENT);
}

// 2x text (12x16 pixels), matching the Franklin, PA header.
// Independently scroll weather, financial and HA information in their own rows.
/**
 * @brief Draws one scrolling ticker line at the requested vertical position.
 * @param content content input or output value.
 * @param y y input or output value.
 * @param color color input or output value.
 * @param now now input or output value.
 */
void drawTickerLine(const String& content, int y, uint16_t color, unsigned long now) {
  // Proportional font: measure glyph advances rather than assuming 12px columns.
  gfx->setFont(&FreeSansBold12pt7b);
  gfx->setTextSize(1);
  int16_t bx, by;
  uint16_t bw, bh;
  const int count = content.length();
  int totalWidth = SCREEN_W;
  for (int i = 0; i < count; ++i) {
    gfx->getTextBounds(content.substring(i, i + 1), 0, 0, &bx, &by, &bw, &bh);
    totalWidth += max(1, (int)bw + 1);
  }
  unsigned long elapsed = (tickerPaused ? tickerPausedAt : now) - tickerStart;
  int offset = ((uint64_t)elapsed * TICKER_SPEED / 1000ULL) % max(1, totalWidth);
  int x = SCREEN_W - offset;
  // Match the News tab background; a contrasting ticker strip looked like a colored block.
  gfx->fillRect(0, y, SCREEN_W, TICKER_H, BG);
  gfx->setTextColor(color);
  for (int i = 0; i < count && x < SCREEN_W; ++i) {
    String letter = content.substring(i, i + 1);
    gfx->getTextBounds(letter, 0, 0, &bx, &by, &bw, &bh);
    int advance = max(1, (int)bw + 1);
    if (x + advance >= 0) {
      gfx->setCursor(x, y + 24);
      gfx->print(letter);
    }
    x += advance;
  }
  gfx->setFont();
}

/**
 * @brief Draws the weather, financial, and Home Assistant ticker content.
 * @param now now input or output value.
 */
void drawTickers(unsigned long now) {
  if (!screenReady) return;
  if (selectedTab == 2) {
    drawTickerLine(temperatureUnits == 'C' ? forecastTickerC : forecastTickerF, 116, WEATHER_BLUE, now);
    drawTickerLine("Dow " + dowText + "  |  Nasdaq " + nasdaqText, 176, FINANCE_GREEN, now);
  } else if (selectedTab == 1) {
    String living = isnan(haLivingF) ? "--" : tempLabel(haLivingF);
    String kitchen = isnan(haKitchenF) ? "--" : tempLabel(haKitchenF);
    String content = "HA " + haStatus + "  |  Version " + haVersion +
      "  |  L/R " + living + "  |  Kitchen " + kitchen +
      "  |  Alerts " + (haAlerts.length() ? haAlerts : "Unavailable");
    drawTickerLine(content, 190, HA_CYAN, now);
  }
}

/**
 * @brief Draws the currently selected tab content.
 */
void drawContent() {
  gfx->fillRect(0, TAB_BOTTOM + 2, SCREEN_W, 294 - TAB_BOTTOM - 2, BG);
  if (selectedTab == 0) {
    // Two-zone layout: live measurement left; adjustable target right.
    lastClockMinute = "";
    DrawRoomClock();
    DrawSmoothText(9, 145, clipped(roomName, 20), ACCENT);
    gfx->fillRoundRect(7, 151, 106, 131, 10, PANEL);
    gfx->fillRoundRect(122, 151, 111, 131, 10, PANEL);
    drawText(19, 157, "Actual", MUTED);
    drawText(130, 157, "Setpoint", MUTED);
    // Large live temperature, with separate fractional digit.
    drawText(13, 195, isnan(roomTemp) ? "--" : tempLabel(roomTemp), WHITE_TEXT, 2);
    drawText(24, 228, String(temperatureUnits), GREEN_TEXT, 2);
    drawText(17, 259, isnan(roomHumidity) ? "RH --" : "RH " + String(roomHumidity, 0) + "%", MUTED);
    // Two large touch targets, with the requested temperature between them.
    gfx->fillRoundRect(135, 175, 85, 31, 9, ACCENT);
    drawChevron(177, 190, true, BG);
    drawText(132, 212, !haveHaInfo && !pendingSetpoint ? "--" : String(displayTemp(setpoint), 1), WHITE_TEXT, 2);
    drawText(206, 217, String(temperatureUnits), MUTED);
    gfx->fillRoundRect(135, 232, 85, 31, 9, ACCENT);
    drawChevron(177, 247, false, BG);
    if (pendingSetpoint) drawText(129, 267, "Pending", ACCENT);
    else drawText(129, 273, guestFloorState < 0 ? "Heat: --" : (guestFloorState ? "Heat: ON" : "Heat: OFF"), MUTED);
  } else if (selectedTab == 1) {
    drawText(12, 76, "ARC House", ACCENT, 2);
    drawText(12, 115, "ARC House", WHITE_TEXT, BODY_SCALE);
    if (haveHaInfo) drawText(12, 143, "HA: " + tempLabel(haSetpoint), WHITE_TEXT, BODY_SCALE);
  } else if (selectedTab == 2) {
    drawText(8, 76, clipped(weatherLocation.length() ? weatherLocation : "Local weather", 19), ACCENT, BODY_SCALE);
    drawText(8, 97, isnan(outsideTemp) ? "Temp: --  RH: --" :
      "Temp:" + String(displayTemp(outsideTemp), 0) + String(temperatureUnits) + " RH:" + String(outsideHumidity, 0) + "%", WHITE_TEXT, BODY_SCALE);

    if (weatherAlert.length())
      drawText(8, 146, clipped("ALERT: " + weatherAlert, 19), ACCENT, BODY_SCALE);
    else drawText(8, 146, "No active NWS alert", MUTED);
    drawText(8, 208, "National / World", ACCENT);
    for (int i = 0; i < 2; ++i)
      drawText(8, 225 + i * 19, clipped(headlines[i], 19), WHITE_TEXT, BODY_SCALE);
  } else {
    drawText(12, 76, "Settings", ACCENT, 2);
    drawText(12, 98, "ID:" + clipped(roomId, 16), WHITE_TEXT, BODY_SCALE);
    drawText(12, 125, clipped(roomName, 18), WHITE_TEXT, BODY_SCALE);
    drawText(12, 158, "Beats: " + String(heartbeatCount), WHITE_TEXT, BODY_SCALE);
    drawText(12, 185, String("ACK: ") + (lastAck ? String((millis()-lastAck)/1000) + "s ago" : "none"), WHITE_TEXT, BODY_SCALE);
    drawText(12, 212, "Interval: 2 seconds", MUTED);
    gfx->fillRoundRect(12, 240, 216, 45, 7, PANEL);
    drawText(22, 250, String("Temperature: ") + temperatureUnits + "   [F/C]", WHITE_TEXT);
  }
  if (selectedTab == 1 || selectedTab == 2) drawTickers(millis());
}

/**
 * @brief Refreshes the changing Settings telemetry without redrawing the entire screen.
 */
void updateSettingsTelemetry() {
  if (!screenReady || selectedTab != 3) return;
  gfx->fillRect(8, 152, 226, 55, BG);
  drawText(12, 158, "Beats: " + String(heartbeatCount), WHITE_TEXT, BODY_SCALE);
  drawText(12, 185, String("ACK: ") + (lastAck ? String((millis()-lastAck)/1000) + "s ago" : "none"), WHITE_TEXT, BODY_SCALE);
}

/**
 * @brief Draws the current connectivity and status indicators.
 */
void drawStatus() {
  String status = WiFi.status() != WL_CONNECTED ? "Wi-Fi offline" :
                  client.connected() ? "TCP connected" : "TCP disconnected";
  if (status == lastStatus) return;
  gfx->fillRect(0, 295, SCREEN_W, 25, BG);
  gfx->drawFastHLine(0, 295, SCREEN_W, PANEL);
  drawText(9, 303, status, client.connected() ? GREEN_TEXT : MUTED);
  lastStatus = status;
}

/**
 * @brief Renders the complete ArcUnit screen.
 */
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

/**
 * @brief Refreshes the current tab content when needed.
 */
void refreshContent() {
  if (!screenReady) return;
  drawContent();
  drawStatus();
  lastDraw = millis();
}

/**
 * @brief Parses an incoming JSON protocol message and updates cached display state.
 * @param line line input or output value.
 */
void handleServerMessage(const String& line) {
  JsonDocument doc;
  DeserializationError error = deserializeJson(doc, line);
  if (error) { logMessage(String("Invalid server JSON: ") + error.c_str()); return; }
  const char* type = doc["type"] | "";
  if (strcmp(type, "ack") == 0) {
    lastAck = millis();
    if (selectedTab == 3) updateSettingsTelemetry();
    // Server echoes the accepted units; device preference remains authoritative.
  } else if (strcmp(type, "news") == 0) {
    if (line == lastNewsPayload) return; // Server sends cached news with each heartbeat.
    lastNewsPayload = line;
    outsideTemp = doc["outsideTempF"].is<double>() || doc["outsideTempF"].is<int>() ? doc["outsideTempF"].as<float>() : NAN;
    outsideHumidity = doc["outsideHumidity"].is<double>() || doc["outsideHumidity"].is<int>() ? doc["outsideHumidity"].as<float>() : NAN;
    forecastText = doc["forecast"] | "Forecast unavailable";
    forecastTickerF = doc["forecastTicker"] | "Forecast unavailable";
    forecastTickerC = doc["forecastTickerC"] | "Forecast unavailable";
    weatherLocation = doc["location"] | "Local weather";
    dowText = doc["dow"] | "Unavailable";
    nasdaqText = doc["nasdaq"] | "Unavailable";
    JsonArray alerts = doc["alerts"].as<JsonArray>();
    weatherAlert = alerts.size() ? alerts[0].as<String>() : "";
    JsonArray stories = doc["headlines"].as<JsonArray>();
    for (int i = 0; i < 4; ++i) headlines[i] = i < stories.size() ? stories[i].as<String>() : "";
    if (selectedTab == 2) refreshContent();
  } else if (strcmp(type, "ha-info") == 0) {
    String newHaStatus = doc["status"] | "Not configured";
    String newHaVersion = doc["version"] | "Unknown";
    float newLiving = doc["livingRoomTempF"].is<double>() || doc["livingRoomTempF"].is<int>() ? doc["livingRoomTempF"].as<float>() : NAN;
    float newKitchen = doc["kitchenTempF"].is<double>() || doc["kitchenTempF"].is<int>() ? doc["kitchenTempF"].as<float>() : NAN;
    JsonArray haAlertArray = doc["alerts"].as<JsonArray>();
    String newHaAlerts = haAlertArray.size() ? haAlertArray[0].as<String>() : "";
    bool haChanged = newHaStatus != haStatus || newHaAlerts != haAlerts || newHaVersion != haVersion ||
      (isnan(newLiving) != isnan(haLivingF)) || (!isnan(newLiving) && fabsf(newLiving - haLivingF) > 0.01f) ||
      (isnan(newKitchen) != isnan(haKitchenF)) || (!isnan(newKitchen) && fabsf(newKitchen - haKitchenF) > 0.01f);
    haStatus = newHaStatus;
    haVersion = newHaVersion;
    haLivingF = newLiving;
    haKitchenF = newKitchen;
    haAlerts = newHaAlerts;
    if (haChanged && selectedTab == 1) { tickerStart = millis(); refreshContent(); }
    // HA owns the authoritative GuestBedroom room temperature and relay state.
    if (roomId == "GuestBedroom") {
      float newTemp = doc["roomTempF"].is<float>() || doc["roomTempF"].is<double>() ||
                      doc["roomTempF"].is<int>() ? doc["roomTempF"].as<float>() : NAN;
      int8_t newFloor = doc["guestFloorOn"].is<bool>() ? (doc["guestFloorOn"].as<bool>() ? 1 : 0) : -1;
      bool changed = (isnan(newTemp) != isnan(roomTemp)) ||
                     (!isnan(newTemp) && fabsf(newTemp - roomTemp) > 0.01f) ||
                     newFloor != guestFloorState;
      roomTemp = newTemp;
      guestFloorState = newFloor;
      if (changed && selectedTab == 0) refreshContent();
    }
    if (doc["setpointF"].is<double>() || doc["setpointF"].is<int>()) {
      haSetpoint = doc["setpointF"].as<float>();
      haveHaInfo = true;
      // Don't overwrite a local adjustment waiting to be transmitted.
      if (!pendingSetpoint && fabsf(setpoint - haSetpoint) > 0.01f) { setpoint = haSetpoint; if (selectedTab == 0) refreshContent(); }

    }
  }
}

// CST816D touch reports coordinates in registers 0x03..0x06.
// Polling avoids a dependency on a board-specific touch library.
/**
 * @brief Reads the touch controller and returns the detected coordinates.
 * @param x x input or output value.
 * @param y y input or output value.
 * @return The computed value or operation result.
 */
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

/**
 * @brief Processes touch input, including tabs, setpoint controls, and settings.
 * @param now now input or output value.
 */
void pollTouch(unsigned long now) {
  if (now - lastTouch < 200) return;
  uint16_t x, y;
  if (!readTouch(x, y)) return;
  if (y <= TAB_BOTTOM) {
    uint8_t tab = x / 60;
    if (tab < 4 && tab != selectedTab) {
      selectedTab = tab;
      dirty = true;
      logMessage(String("Selected tab: ") + tabNames[tab]);
    }
  } else if (selectedTab == 2 && ((y >= 116 && y < 146) || (y >= 176 && y < 206))) {
    tickerPaused = !tickerPaused;
    if (tickerPaused) tickerPausedAt = now;
    else tickerStart += now - tickerPausedAt;
    drawTickers(now);
  } else if (selectedTab == 3) {
    if (x >= 12 && x <= 228 && y >= 237 && y <= 289) {
      temperatureUnits = temperatureUnits == 'F' ? 'C' : 'F';
      preferences.putString("TemperatureUnits", String(temperatureUnits));
      logMessage(String("TemperatureUnits changed to ") + temperatureUnits);
      refreshContent();
    }
  } else if (selectedTab == 0) {
    // Half-degree increments in selected units; wire protocol remains Fahrenheit.
    if (x >= 130 && x <= 225) {
      float step = temperatureUnits == 'C' ? 1.8f : 1.0f; // HA helper uses 1°F steps
      float requested = setpoint;
      if (y >= 175 && y <= 208) requested += step;
      else if (y >= 232 && y <= 264) requested -= step;
      requested = constrain(requested, 50.0f, 85.0f);
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

/**
 * @brief Initializes serial diagnostics, hardware, stored settings, Wi-Fi, and display.
 */
void setup() {
  Serial.begin(115200);
  preferences.begin("arcpanel", false);
  uint64_t mac = ESP.getEfuseMac();
  char id[32];
  snprintf(id, sizeof(id), "ESP32-%012llX", (unsigned long long)mac);
  deviceId = id;
  roomId = ROOM_ID;
  String savedUnits = preferences.getString("TemperatureUnits", "F");
  temperatureUnits = savedUnits == "C" ? 'C' : 'F';
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
  // Eastern time with automatic DST; NTP runs in the background after Wi-Fi connects.
  configTzTime("EST5EDT,M3.2.0/2,M11.1.0/2", "pool.ntp.org", "time.nist.gov");
  logMessage("Wi-Fi connecting...");
}

/**
 * @brief Runs the Arduino event loop, network heartbeat, touch polling, and display updates.
 */
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
          if (!SendBuildInfo()) {
            logMessage("BuildInfo send failed; reconnecting");
            client.stop();
          }
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
        // Do not send fabricated room measurements before SHT40 is connected.
        if (!isnan(roomHumidity)) hb["roomHumidity"] = roomHumidity;
        hb["temperatureUnits"] = String(temperatureUnits);
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
  if (selectedTab == 0 && now - lastClockDraw >= 1000) { DrawRoomClock(); lastClockDraw = now; }
  else if (screenReady) drawStatus();
  if (screenReady && (selectedTab == 1 || selectedTab == 2) && now - lastTickerDraw >= 80) {
    drawTickers(now);
    lastTickerDraw = now;
  }
  if (screenReady && selectedTab == 3 && now - lastDraw >= 1000) { updateSettingsTelemetry(); lastDraw = now; }
  delay(10);
}
