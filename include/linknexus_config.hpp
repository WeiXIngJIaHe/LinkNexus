#pragma once

#include <stdint.h>
#include "driver/gpio.h"
#include "driver/uart.h"

namespace LinkNexus {

/**
 * @brief 固件版本号基线规范 (与 C# 上位机 LinkNexus 完全保持统一)
 */
#define LINKNEXUS_FIRMWARE_VERSION  "0.36-preview-2641"
#define LINKNEXUS_HARDWARE_MODEL    "LinkNexus-Pro-ESP32S3"
#define LINKNEXUS_CHIP_PART_NUMBER  "ESP32-S3FH4R2 (Bare Chip QFN56, 4MB Flash, 2MB PSRAM)"
#define LINKNEXUS_RF_ANTENNA_DESC   "Custom IPEX Receptacle with Pi-Network (Direct 0R Jumper)"
#define LINKNEXUS_DEFAULT_MAC       "d0:cf:13:30:90:30"

namespace Security {
    constexpr uint8_t TARGET_MAC[6] = { 0xD0, 0xCF, 0x13, 0x30, 0x90, 0x30 };
}

/**
 * @brief 自制板射频前向传输与天线调谐基线配置
 * 针对裸芯片 + 0Ω 直连未阻抗调谐天线，下调发射功率可显著降低驻波反射 (VSWR)、抑制 EVM 恶化与瞬态压降
 */
namespace RfConfig {
    // 默认 Wi-Fi 最大发射功率 (单位: 0.25 dBm，52 对应 13.00 dBm，约 20mW)
    constexpr int8_t DEFAULT_MAX_TX_POWER_RAW = 52; // 13.00 dBm
    constexpr int8_t SAFE_TEST_TX_POWER_RAW    = 40; // 10.00 dBm (低功率高容限测试模式)
    constexpr int8_t MIN_TX_POWER_RAW         = 32; // 8.00 dBm
    constexpr int8_t MAX_TX_POWER_RAW         = 80; // 20.00 dBm (芯片额定极限)
    constexpr uint8_t DEFAULT_CHANNEL         = 1;  // 2412 MHz (最低信道衰减)
}

/**
 * @brief 硬件通信引脚分配
 */
namespace Pins {
    // 通信 UART 配置 (同时支持 Native USB-Serial-JTAG CDC 与 UART0)
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

    // Core 0 (APP_CPU): 专注于 Wi-Fi 射频、网络事件与 TCP 调试服务
    constexpr BaseType_t SYSTEM_CORE        = 0;
}

} // namespace LinkNexus
