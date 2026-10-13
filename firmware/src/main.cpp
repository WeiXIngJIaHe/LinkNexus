/**
 * @file main.cpp
 * @brief LinkNexus ESP32-S3 双核嵌入式固件主入口
 * @note 严格遵循模块化架构规范：main 仅负责顶层调度器的初始化与启动，不直接构建底层外设逻辑。
 */

#include "app_coordinator.hpp"

extern "C" void app_main(void)
{
    // 1. 初始化各功能子系统与状态机
    LinkNexus::AppCoordinator::Instance().Init();

    // 2. 启动 FreeRTOS 双核任务调度 (Core 0 / Core 1)
    LinkNexus::AppCoordinator::Instance().Start();
}

