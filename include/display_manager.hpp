#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "protocol.hpp"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief 板载 1.9 寸 ST7789 屏幕及 LVGL UI 架构管理模块
 * 负责接收上位机 0x20 屏幕同步帧，管理 UI 状态模型，
 * 并预留 LVGL 绘制任务管道，支持双核互斥更新与平滑渲染。
 */
class DisplayManager {
public:
    static DisplayManager& Instance();

    /**
     * @brief 初始化显示外设抽象层及 UI 任务上下文
     */
    void Init();

    /**
     * @brief 线程安全更新上位机同步而来的屏幕数据
     */
    void UpdateState(const ScreenSyncInfo &info);

    /**
     * @brief 线程安全获取当前屏幕状态数据
     */
    void GetState(ScreenSyncInfo &out_info);

    /**
     * @brief UI 周期性刷新与 LVGL 任务循环接口 (由 Core 0 执行)
     */
    void ProcessUi();

private:
    DisplayManager();
    ~DisplayManager() = default;

    DisplayManager(const DisplayManager&) = delete;
    DisplayManager& operator=(const DisplayManager&) = delete;

    Mutex m_mutex;
    ScreenSyncInfo m_state;
    bool m_dirty{true};
    bool m_initialized{false};
};

} // namespace LinkNexus

