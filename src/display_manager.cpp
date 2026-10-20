#include "display_manager.hpp"
#include "ui_main.h"
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
    
    // 初始化 1.9 寸 (320x170) UI 核心模型与三段式页面架构
    ui_main_init();

    m_initialized = true;
    m_dirty = true;
    ESP_LOGI(TAG, "Display & UI Manager initialized successfully.");
}

void DisplayManager::UpdateState(const ScreenSyncInfo &info)
{
    LockGuard lock(m_mutex);
    m_state = info;
    m_dirty = true;

    // 同步更新 UI 状态栏与活动通道
    ui_update_status_bar(nullptr, info.device_name, info.flash_progress);

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

    // 调度 UI 渲染与定时器循环 (16ms 约 60FPS)
    ui_main_process(16);
}

} // namespace LinkNexus
