#include "power_manager.hpp"
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

    // 默认上电导通所有通道
    for (size_t i = 0; i < static_cast<size_t>(PowerChannel::Count); ++i) {
        gpio_set_level(s_channel_gpios[i], m_states[i] ? 1 : 0);
    }

    m_initialized = true;
    ESP_LOGI(TAG, "All 6 MOS power channels initialized (Default: ON).");
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

} // namespace LinkNexus

