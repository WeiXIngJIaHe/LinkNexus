#include "display_manager.hpp"
#include "esp_log.h"
#include <string.h>

namespace LinkNexus {

static const char *TAG = "DISPLAY_MGR";

DisplayManager::DisplayManager()
{
    m_state.active_channel = 0;
    m_state.flash_progress = 0;
    m_state.status_flags = 0;
    m_state.name_len = 15;
    strncpy(m_state.device_name, "LinkNexus Ready", sizeof(m_state.device_name) - 1);
}

DisplayManager& DisplayManager::Instance()
{
    static DisplayManager instance;
    return instance;
}

void DisplayManager::Init()
{
    LockGuard lock(m_mutex);
    if (m_initialized) return;

    ESP_LOGI(TAG, "Initializing 1.9-inch ST7789 display & LVGL UI framework...");
    // 预留 ST7789 SPI LCD 与 LVGL 图形缓冲区初始化钩子
    // 后续引入 lvgl 组件时可无缝在此激活 lv_init() 与 disp_drv 驱动
    m_initialized = true;
    m_dirty = true;
    ESP_LOGI(TAG, "Display & UI Manager initialized successfully.");
}

void DisplayManager::UpdateState(const ScreenSyncInfo &info)
{
    LockGuard lock(m_mutex);
    m_state = info;
    m_dirty = true;

    ESP_LOGI(TAG, "[UI Sync] Channel: %u, Flash Progress: %u%%, Flags: 0x%02X, Device: '%s'",
             info.active_channel, info.flash_progress, info.status_flags, info.device_name);
}

void DisplayManager::GetState(ScreenSyncInfo &out_info)
{
    LockGuard lock(m_mutex);
    out_info = m_state;
}

void DisplayManager::ProcessUi()
{
    LockGuard lock(m_mutex);
    if (m_dirty) {
        // 当 UI 状态有更新时在此触发局部脏矩形或控件状态刷新
        m_dirty = false;
    }

    // 预留 LVGL 心跳与定时调度接口
    // #if CONFIG_LINKNEXUS_ENABLE_LVGL
    // lv_timer_handler();
    // #endif
}

} // namespace LinkNexus

