#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief CAP1208 8 通道电容触摸与滑条传感器硬件驱动 (I2C 接口)
 * 硬件预留驱动模块：支持检测物理触摸按键与电容滑条的左右滑动、滑动速度及按下松开事件。
 */

#define CAP1208_I2C_ADDR_DEFAULT       0x28 // ADDR 引脚悬空/接地默认从机地址

// CAP1208 核心寄存器映射
#define CAP1208_REG_MAIN_CONTROL       0x00
#define CAP1208_REG_GENERAL_STATUS     0x02
#define CAP1208_REG_SENSOR_INPUT_STAT  0x03
#define CAP1208_REG_NOISE_FLAG_STAT    0x0A
#define CAP1208_REG_SENSOR_INPUT_DELTA 0x10
#define CAP1208_REG_SENSITIVITY_CTRL   0x1F
#define CAP1208_REG_CONFIG             0x20
#define CAP1208_REG_SENSOR_INPUT_EN    0x21
#define CAP1208_REG_SLIDER_VELOCITY    0x22
#define CAP1208_REG_MULTITOUCH_CONFIG  0x2A
#define CAP1208_REG_PRODUCT_ID         0xFD
#define CAP1208_REG_MANUFACTURER_ID    0xFE
#define CAP1208_REG_REVISION           0xFF

// 滑动方向事件定义
typedef enum {
    CAP1208_SLIDE_NONE = 0,
    CAP1208_SLIDE_LEFT,
    CAP1208_SLIDE_RIGHT,
    CAP1208_TOUCH_PRESS,
    CAP1208_TOUCH_RELEASE
} cap1208_event_t;

// 滑条滑动事件回调函数指针定义
typedef void (*cap1208_callback_t)(cap1208_event_t event, uint8_t position, void *user_data);

/**
 * @brief 初始化 CAP1208 电容滑条芯片
 * @param i2c_port I2C 硬件端口号
 * @param i2c_addr 从机 I2C 地址 (一般为 0x28)
 * @return esp_err_t ESP_OK 表示初始化成功
 */
esp_err_t cap1208_init(uint8_t i2c_port, uint8_t i2c_addr);

/**
 * @brief 注册触摸/滑动事件监听回调
 */
void cap1208_register_callback(cap1208_callback_t cb, void *user_data);

/**
 * @brief 周期性轮询读取 CAP1208 传感器状态与滑动判定 (或在外部中断触发时调用)
 * @param out_event 输出滑动/触摸事件
 * @param out_position 当前触摸滑块相对位置 (0~255)
 * @return esp_err_t ESP_OK 成功
 */
esp_err_t cap1208_poll_event(cap1208_event_t *out_event, uint8_t *out_position);

/**
 * @brief 读取 8 个触摸通道的原始位掩码 (Bit 0~7 对应 CS1~CS8)
 */
esp_err_t cap1208_read_raw_inputs(uint8_t *inputs_mask);

#ifdef __cplusplus
}
#endif

