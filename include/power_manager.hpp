#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "linknexus_config.hpp"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief 6 路 MOS 供电通道硬件管理模块
 * 提供基于 FreeRTOS 互斥锁的线程安全保护，防止双核并发调用引起状态竞态与 GPIO 异常
 */
class PowerManager {
public:
    static PowerManager& Instance();

    /**
     * @brief 初始化 6 路 MOS 控制 GPIO (开漏/推挽输出配置)
     */
    void Init();

    /**
     * @brief 设置指定通道的供电状态
     * @param channel 通道枚举
     * @param enable true 为通电导通，false 为断电隔离
     * @return 操作是否成功
     */
    bool SetPower(PowerChannel channel, bool enable);
    bool SetPower(uint8_t channel_idx, bool enable);

    /**
     * @brief 查询指定通道当前的供电状态
     */
    bool GetPower(PowerChannel channel);
    bool GetPower(uint8_t channel_idx);

    /**
     * @brief 原子性批量读取所有通道供电状态
     */
    void GetAllStates(bool out_states[static_cast<size_t>(PowerChannel::Count)]);

    /**
     * @brief 获取 6 路 MOS 当前状态位掩码 (0x3F 为全开)
     */
    uint8_t GetPowerMask();

    /**
     * @brief 批量切换所有 6 路 MOS 通道供电状态
     */
    bool SetAllPower(bool enable);

    /**
     * @brief 按位掩码批量更新 6 路 MOS 通道供电状态
     */
    bool SetPowerMask(uint8_t mask);

private:
    PowerManager() = default;
    ~PowerManager() = default;

    PowerManager(const PowerManager&) = delete;
    PowerManager& operator=(const PowerManager&) = delete;

    Mutex m_mutex;
    bool m_states[static_cast<size_t>(PowerChannel::Count)]{
        true, true, true, true, true, true
    };
    bool m_initialized{false};
};

} // namespace LinkNexus

