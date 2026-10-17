#include "power_manager.hpp"
#include "config_manager.hpp"
#include "esp_log.h"
#include <string.h>

namespace LinkNexus {

static const char *TAG = "POWER_MGR";

static const gpio_num_t s_channel_gpios[static_cast<size_t>(PowerChannel::Count)] = {
    Pins::POWER_CH0_XDS,
    Pins::POWER_CH1_DAP,
    Pins::POWER_CH2_FT2232,
    Pins::POWER_CH3_UART,
    Pins::POWER_CH4_EXT1,
    Pins::POWER_CH5_EXT2
};

PowerManager& PowerManager::Instance()
{
    static PowerManager instance;
    return instance;
}

void PowerManager::Init()
{
    LockGuard lock(m_mutex);
    if (m_initialized) return;

    ESP_LOGI(TAG, "Initializing 6-Channel MOS power controllers...");

    uint64_t pin_mask = 0;
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        pin_mask |= (1ULL << s_channel_gpios[i]);
    }

    gpio_config_t io_conf = {
        .pin_bit_mask = pin_mask,
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_ENABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    gpio_config(&io_conf);

    // 从 ConfigManager (NVS 持久化存储) 加载开机初始供电掩码配置
    uint8_t init_mask = ConfigManager::Instance().GetMosMask();
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        m_states[i] = ((init_mask & (1U << i)) != 0);
        gpio_set_level(s_channel_gpios[i], m_states[i] ? 1 : 0);
    }

    m_initialized = true;
    ESP_LOGI(TAG, "All 6 MOS power channels initialized (Mask: 0x%02X).", init_mask);
}

bool PowerManager::SetPower(PowerChannel channel, bool enable)
{
    return SetPower(static_cast<uint8_t>(channel), enable);
}

bool PowerManager::SetPower(uint8_t channel_idx, bool enable)
{
    if (channel_idx >= static_cast<uint8_t>(PowerChannel::Count)) {
        ESP_LOGW(TAG, "Attempted to set invalid channel: %u", channel_idx);
        return false;
    }

    LockGuard lock(m_mutex);
    m_states[channel_idx] = enable;
    gpio_set_level(s_channel_gpios[channel_idx], enable ? 1 : 0);

    ESP_LOGI(TAG, "Channel #%u power state updated -> %s (GPIO %d = %d)",
             channel_idx, enable ? "ENABLED (ON)" : "ISOLATED (OFF)",
             s_channel_gpios[channel_idx], enable ? 1 : 0);
    return true;
}

bool PowerManager::GetPower(PowerChannel channel)
{
    return GetPower(static_cast<uint8_t>(channel));
}

bool PowerManager::GetPower(uint8_t channel_idx)
{
    if (channel_idx >= static_cast<uint8_t>(PowerChannel::Count)) return false;

    LockGuard lock(m_mutex);
    return m_states[channel_idx];
}

void PowerManager::GetAllStates(bool out_states[static_cast<size_t>(PowerChannel::Count)])
{
    if (!out_states) return;
    LockGuard lock(m_mutex);
    memcpy(out_states, m_states, sizeof(m_states));
}

uint8_t PowerManager::GetPowerMask()
{
    LockGuard lock(m_mutex);
    uint8_t mask = 0;
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        if (m_states[i]) mask |= (1U << i);
    }
    return mask;
}

bool PowerManager::SetAllPower(bool enable)
{
    LockGuard lock(m_mutex);
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        m_states[i] = enable;
        if (m_initialized) {
            gpio_set_level(s_channel_gpios[i], enable ? 1 : 0);
        }
    }
    ESP_LOGI(TAG, "All 6 channels updated to -> %s", enable ? "true (ON)" : "false (OFF)");
    return true;
}

bool PowerManager::SetPowerMask(uint8_t mask)
{
    LockGuard lock(m_mutex);
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        bool en = (mask & (1U << i)) != 0;
        m_states[i] = en;
        if (m_initialized) {
            gpio_set_level(s_channel_gpios[i], en ? 1 : 0);
        }
    }
    ESP_LOGI(TAG, "Channels mask updated to -> 0x%02X", mask);
    return true;
}

} // namespace LinkNexus

