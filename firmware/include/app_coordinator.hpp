#pragma once

#include "protocol.hpp"
#include "power_manager.hpp"
#include "telemetry_manager.hpp"
#include "display_manager.hpp"
#include "comm_manager.hpp"
#include "wifi_manager.hpp"
#include "cli_engine.hpp"

namespace LinkNexus {

/**
 * @brief 系统主控中枢与 FreeRTOS 双核任务调度协调器
 * 负责各子系统单例生命周期管理、协议分发回调以及双核任务调度编排
 *
 * 核心调度设计：
 * - Core 1: comm_rx 任务 (高速串口帧解包、低时延响应与协议分发)
 * - Core 0: telemetry 任务 (250ms INA236 I2C 采样与上报) + ui_task 任务 (LVGL/屏幕刷新循环)
 */
class AppCoordinator {
public:
    static AppCoordinator& Instance();

    /**
     * @brief 初始化所有硬件模块与通信引擎
     */
    void Init();

    /**
     * @brief 创建并启动 FreeRTOS 双核并发任务
     */
    void Start();

private:
    AppCoordinator();
    ~AppCoordinator() = default;

    AppCoordinator(const AppCoordinator&) = delete;
    AppCoordinator& operator=(const AppCoordinator&) = delete;

    void OnFrameReceived(uint8_t cmd, const uint8_t *payload, uint8_t len);
    void ExecuteSerialCli(const char *cmd);

    static void CommRxTaskEntry(void *pvParameters);
    static void TelemetryTaskEntry(void *pvParameters);
    static void UiTaskEntry(void *pvParameters);

    ProtocolParser m_parser;
    bool m_initialized{false};
    bool m_started{false};
};

} // namespace LinkNexus

