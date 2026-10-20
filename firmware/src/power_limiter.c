#include "power_limiter.h"
#include "esp_log.h"
#include "driver/gpio.h"

static const char *TAG = "POWER_LIMITER";

static uint8_t s_channel_state_mask = 0x3F; // 默认 6 路全部开启 (0b00111111)

// 对应硬件 GPIO 引脚 (CH0~CH5: GPIO 4, 5, 6, 7, 15, 16)
static const gpio_num_t s_limiter_pins[POWER_LIMITER_CHANNEL_COUNT] = {
    GPIO_NUM_4,  // CH0: XDS110
    GPIO_NUM_5,  // CH1: DAPLink
    GPIO_NUM_6,  // CH2: FT2232
    GPIO_NUM_7,  // CH3: UART
    GPIO_NUM_15, // CH4: EXT1
    GPIO_NUM_16  // CH5: EXT2
};

esp_err_t power_limiter_init(void)
{
    ESP_LOGI(TAG, "Initializing 6-Channel Current Limiter GPIO controllers...");

    gpio_config_t io_conf = {
        .pin_bit_mask = 0,
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_ENABLE,
        .intr_type = GPIO_INTR_DISABLE
    };

    for (int i = 0; i < POWER_LIMITER_CHANNEL_COUNT; i++) {
        io_conf.pin_bit_mask |= (1ULL << s_limiter_pins[i]);
    }

    gpio_config(&io_conf);

    // 默认高电平导通输出
    for (int i = 0; i < POWER_LIMITER_CHANNEL_COUNT; i++) {
        gpio_set_level(s_limiter_pins[i], 1);
    }

    s_channel_state_mask = 0x3F;
    ESP_LOGI(TAG, "All 6 Current Limiter channels initialized (Default: ON).");
    return ESP_OK;
}

esp_err_t power_limiter_set_channel(power_limiter_channel_t channel, bool enable)
{
    if ((int)channel < 0 || (int)channel >= POWER_LIMITER_CHANNEL_COUNT) {
        return ESP_ERR_INVALID_ARG;
    }

    gpio_set_level(s_limiter_pins[channel], enable ? 1 : 0);

    if (enable) {
        s_channel_state_mask |= (1 << channel);
    } else {
        s_channel_state_mask &= ~(1 << channel);
    }

    ESP_LOGI(TAG, "Limiter Channel #%d -> %s", (int)channel, enable ? "ENABLED (ON)" : "DISABLED (OFF)");
    return ESP_OK;
}

bool power_limiter_get_channel(power_limiter_channel_t channel)
{
    if ((int)channel < 0 || (int)channel >= POWER_LIMITER_CHANNEL_COUNT) {
        return false;
    }
    return (s_channel_state_mask & (1 << channel)) != 0;
}

uint8_t power_limiter_get_all_mask(void)
{
    return s_channel_state_mask;
}

esp_err_t power_limiter_set_all_mask(uint8_t channel_mask)
{
    s_channel_state_mask = channel_mask & 0x3F;
    for (int i = 0; i < POWER_LIMITER_CHANNEL_COUNT; i++) {
        bool en = (s_channel_state_mask & (1 << i)) != 0;
        gpio_set_level(s_limiter_pins[i], en ? 1 : 0);
    }
    ESP_LOGI(TAG, "Batch set limiter channels mask: 0x%02X", s_channel_state_mask);
    return ESP_OK;
}

