#pragma once

#include "protocol.hpp"
#include "power_manager.hpp"
#include "telemetry_manager.hpp"
#include "display_manager.hpp"
#include "comm_manager.hpp"
#include "wifi_manager.hpp"
#include "cli_engine.hpp"
#include "freertos/FreeRTOS.h"
#include <atomic>

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

    /**
     * @brief 检查上位机通信链路是否处于活跃连接状态 (已收到握手心跳且未超时)
     */
    bool IsHostActive() const;

    /**
     * @brief 刷新上位机活跃时间戳
     */
    void TouchHostActivity();

    /**
     * @brief 设置/查询是否强制手动开启二进制遥测流 (供 CLI 调试使用)
     */
    void SetManualTelemetryStream(bool enable);
    bool IsManualTelemetryStream() const;
    uint32_t GetLastHostActivityTick() const;

    /**
     * @brief 设置/查询 VOFA+ FireWater 文本遥测流
     * 开启后每 250ms 输出一行逗号分隔浮点帧，直接对接上位机流式蓝图 COM 输入卡片与 VOFA+ 波形绘制。
     */
    void SetVofaStream(bool enable);
    bool IsVofaStream() const;

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
    std::atomic<bool> m_manual_telemetry_stream{false};
    std::atomic<bool> m_vofa_stream{false};
    std::atomic<uint32_t> m_last_host_activity_tick{0};
};

} // namespace LinkNexus

