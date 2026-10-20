#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief 6 通道可控限流供电芯片驱动抽象 (高低电平 GPIO 控制)
 * 硬件预留驱动模块：管理 6 路限流/负载开关芯片的启闭、故障保护检测与通道状态缓存。
 */

#define POWER_LIMITER_CHANNEL_COUNT   6

typedef enum {
    LIMITER_CH0_XDS110  = 0,
    LIMITER_CH1_DAPLINK = 1,
    LIMITER_CH2_FT2232  = 2,
    LIMITER_CH3_UART    = 3,
    LIMITER_CH4_EXT1    = 4,
    LIMITER_CH5_EXT2    = 5
} power_limiter_channel_t;

/**
 * @brief 初始化 6 路限流控制 GPIO 引脚 (默认高电平开启或低电平关断)
 */
esp_err_t power_limiter_init(void);

/**
 * @brief 控制单个限流通道开关
 * @param channel 通道编号 0~5
 * @param enable true: 通电开启; false: 关断
 */
esp_err_t power_limiter_set_channel(power_limiter_channel_t channel, bool enable);

/**
 * @brief 获取单个限流通道开关状态
 */
bool power_limiter_get_channel(power_limiter_channel_t channel);

/**
 * @brief 获取全部 6 通道的开关位掩码 (Bit 0~5 对应 CH0~CH5)
 */
uint8_t power_limiter_get_all_mask(void);

/**
 * @brief 批量设置 6 通道的开关状态
 */
esp_err_t power_limiter_set_all_mask(uint8_t channel_mask);

#ifdef __cplusplus
}
#endif

