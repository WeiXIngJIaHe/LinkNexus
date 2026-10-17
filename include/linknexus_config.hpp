#pragma once

#include <stdint.h>
#include "driver/gpio.h"
#include "driver/uart.h"
#include "driver/i2c.h"

namespace LinkNexus {

/**
 * @brief 固件版本号基线规范 (与 C# 上位机 LinkNexus 完全保持统一)
 */
#define LINKNEXUS_FIRMWARE_VERSION  "0.23-preview-2641"
#define LINKNEXUS_HARDWARE_MODEL    "LinkNexus-Pro-ESP32S3"

/**
 * @brief 6 路物理通道枚举
 */
enum class PowerChannel : uint8_t {
    XDS110  = 0,
    DAPLink = 1,
    FT2232  = 2,
    UART    = 3,
    EXT1    = 4,
    EXT2    = 5,
    Count   = 6
};

/**
 * @brief 硬件引脚分配 (对齐 PCB 硬件设计与上位机 HardwareConfig.json)
 */
namespace Pins {
    // 6 路 MOS 电源控制 GPIO
    constexpr gpio_num_t POWER_CH0_XDS    = GPIO_NUM_4;
    constexpr gpio_num_t POWER_CH1_DAP    = GPIO_NUM_5;
    constexpr gpio_num_t POWER_CH2_FT2232 = GPIO_NUM_6;
    constexpr gpio_num_t POWER_CH3_UART   = GPIO_NUM_7;
    constexpr gpio_num_t POWER_CH4_EXT1   = GPIO_NUM_15;
    constexpr gpio_num_t POWER_CH5_EXT2   = GPIO_NUM_16;

    // INA236 I2C 采样总线
    constexpr i2c_port_t INA236_I2C_PORT   = I2C_NUM_0;
    constexpr gpio_num_t INA236_SDA_GPIO   = GPIO_NUM_8;
    constexpr gpio_num_t INA236_SCL_GPIO   = GPIO_NUM_9;
    constexpr uint32_t   INA236_I2C_FREQ   = 400000; // 400 kHz

    // I2C 设备从机地址
    constexpr uint8_t INA236_ADDR_BUS      = 0x40; // 整机主母线
    constexpr uint8_t INA236_ADDR_XDS      = 0x41; // XDS110 通道
    constexpr uint8_t INA236_ADDR_DAP      = 0x42; // DAPLink 通道

    // 通信 UART 配置
    constexpr uart_port_t COMM_UART_PORT   = UART_NUM_0;
    constexpr uint32_t    COMM_UART_BAUD   = 115200;
    constexpr size_t      COMM_RX_BUF_SIZE = 1024;
    constexpr size_t      COMM_TX_BUF_SIZE = 1024;
}

/**
 * @brief FreeRTOS 任务双核绑定策略
 */
namespace Cores {
    // Core 1 (PRO_CPU): 专注于高速全双工串口通信与协议防粘包流式解析
    constexpr BaseType_t COMMUNICATION_CORE = 1;

    // Core 0 (APP_CPU): 专注于传感器 I2C 采样、硬件安全状态机与 UI/LVGL 渲染循环
    constexpr BaseType_t SYSTEM_CORE        = 0;
}

} // namespace LinkNexus

