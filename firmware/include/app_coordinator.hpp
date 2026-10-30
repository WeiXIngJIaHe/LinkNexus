#pragma once

#include <stdint.h>
#include <stdbool.h>
#include <atomic>
#include "protocol.hpp"
#include "comm_manager.hpp"
#include "wifi_manager.hpp"
#include "cli_engine.hpp"
#include "freertos/FreeRTOS.h"

namespace LinkNexus {

/**
 * @brief 系统主控中枢与 FreeRTOS 任务协调器
 * 精简架构：仅保留 CDC 串口、CLI 与上位机通信以及 Wi-Fi 功能
 *
 * 核心调度设计：
 * - Core 1: comm_rx 任务 (高速 USB CDC / UART0 串口帧解包、低时延响应与协议分发)
 * - Core 0: Wi-Fi 任务与后台 TCP CLI 服务
 */
class AppCoordinator {
public:
    static AppCoordinator& Instance();

    /**
     * @brief 初始化通信硬件层、Wi-Fi 与 CLI 引擎
     */
    void Init();

    /**
     * @brief 创建并启动 FreeRTOS 任务
     */
    void Start();

    /**
     * @brief 检查上位机通信链路是否处于活跃连接状态 (已收到握手心跳且未超时)
     */
    bool IsHostActive() const;

    /**
     * @brief 刷新上位机活跃时间戳
     */
    void TouchHostActivity();

    uint32_t GetLastHostActivityTick() const;

private:
    AppCoordinator();
    ~AppCoordinator() = default;

    AppCoordinator(const AppCoordinator&) = delete;
    AppCoordinator& operator=(const AppCoordinator&) = delete;

    void OnFrameReceived(uint8_t cmd, const uint8_t *payload, uint8_t len);
    void ExecuteSerialCli(const char *cmd);

    static void CommRxTaskEntry(void *pvParameters);

    ProtocolParser m_parser;
    bool m_initialized{false};
    bool m_started{false};
    std::atomic<uint32_t> m_last_host_activity_tick{0};
};

} // namespace LinkNexus
