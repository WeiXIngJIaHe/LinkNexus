#include "ui_main.h"
#include "power_limiter.h"
#include "ina236_telemetry.h"
#include "esp_log.h"
#include <string.h>
#include <stdio.h>

static const char *TAG = "UI_MAIN";

// UI 全局模型状态
static ui_page_t s_current_page = UI_PAGE_POWER_SWITCH;
static char s_top_wifi_mode[16] = "SoftAP";
static char s_bottom_status_text[48] = "系统就绪 | 6路MOS全开";
static uint8_t s_progress_pct = 100;

// 遥测波形点缓存 (用于曲线图绘制，循环保存最近 32 个点)
#define TELEMETRY_HISTORY_LEN 32
static float s_wave_history[TELEMETRY_HISTORY_LEN] = {0};
static uint8_t s_wave_head = 0;

// 遥测实时参数快照
static struct {
    float bus_v, bus_ma, bus_w;
    float xds_v, xds_ma, xds_w;
    float dap_v, dap_ma, dap_w;
} s_ui_telem = {
    5.08f, 420.0f, 2.13f,
    3.31f, 85.0f,  0.28f,
    3.29f, 42.0f,  0.14f
};

// Wi-Fi GUI 参数快照
static struct {
    char ssid[32];
    char ip[24];
    char scan_summary[64];
} s_ui_wifi = {
    "LinkNexus-ESP32-AP",
    "192.168.4.1",
    "已扫描: 13 个热点就绪"
};

void ui_main_init(void)
{
    ESP_LOGI(TAG, "Initializing LinkNexus 1.9-inch (320x170) UI Model & View Architecture...");

    // 默认初始化波形曲线基线
    for (int i = 0; i < TELEMETRY_HISTORY_LEN; i++) {
        s_wave_history[i] = 400.0f + (float)(i % 5) * 8.0f;
    }

    s_current_page = UI_PAGE_POWER_SWITCH;
    ESP_LOGI(TAG, "UI initialized. Default Page: 0 (Power Limiter Switch). Ready for CAP1208 slider / simulator.");
}

void ui_main_process(uint32_t delta_ms)
{
    (void)delta_ms;
    // 渲染循环钩子：当移植 LVGL 时调用 lv_timer_handler() 或更新脏矩形
}

void ui_switch_page(ui_page_t page)
{
    if (page >= UI_PAGE_COUNT) page = UI_PAGE_POWER_SWITCH;
    s_current_page = page;

    const char *page_names[UI_PAGE_COUNT] = {
        "1.限流开关控制 (6-CH)",
        "2.遥测数据与动态曲线 (INA236)",
        "3.Wi-Fi网络GUI管理 (AP/STA)"
    };

    ESP_LOGI(TAG, "[UI Page Switch] -> Page %d: %s", (int)page, page_names[page]);
}

ui_page_t ui_get_current_page(void)
{
    return s_current_page;
}

void ui_slide_previous(void)
{
    int next_page = ((int)s_current_page - 1 + UI_PAGE_COUNT) % UI_PAGE_COUNT;
    ui_switch_page((ui_page_t)next_page);
}

void ui_slide_next(void)
{
    int next_page = ((int)s_current_page + 1) % UI_PAGE_COUNT;
    ui_switch_page((ui_page_t)next_page);
}

void ui_update_status_bar(const char *wifi_mode, const char *status_text, uint8_t progress_pct)
{
    if (wifi_mode) {
        strncpy(s_top_wifi_mode, wifi_mode, sizeof(s_top_wifi_mode) - 1);
    }
    if (status_text) {
        strncpy(s_bottom_status_text, status_text, sizeof(s_bottom_status_text) - 1);
    }
    s_progress_pct = progress_pct > 100 ? 100 : progress_pct;
}

void ui_update_power_switch_state(uint8_t channel_mask)
{
    power_limiter_set_all_mask(channel_mask);
}

void ui_update_telemetry_data(float bus_v, float bus_ma, float bus_w,
                              float xds_v, float xds_ma, float xds_w,
                              float dap_v, float dap_ma, float dap_w)
{
    s_ui_telem.bus_v  = bus_v;  s_ui_telem.bus_ma = bus_ma; s_ui_telem.bus_w = bus_w;
    s_ui_telem.xds_v  = xds_v;  s_ui_telem.xds_ma = xds_ma; s_ui_telem.xds_w = xds_w;
    s_ui_telem.dap_v  = dap_v;  s_ui_telem.dap_ma = dap_ma; s_ui_telem.dap_w = dap_w;

    // 记录动态曲线点
    s_wave_history[s_wave_head] = bus_ma;
    s_wave_head = (s_wave_head + 1) % TELEMETRY_HISTORY_LEN;
}

void ui_update_wifi_gui_info(const char *ssid, const char *ip_str, const char *scan_summary)
{
    if (ssid) strncpy(s_ui_wifi.ssid, ssid, sizeof(s_ui_wifi.ssid) - 1);
    if (ip_str) strncpy(s_ui_wifi.ip, ip_str, sizeof(s_ui_wifi.ip) - 1);
    if (scan_summary) strncpy(s_ui_wifi.scan_summary, scan_summary, sizeof(s_ui_wifi.scan_summary) - 1);
}

