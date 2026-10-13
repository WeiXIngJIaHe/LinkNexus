#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "protocol.hpp"
#include "power_manager.hpp"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief INA236 高精度电流/电压遥测管理模块
 * 负责 3 路物理 INA236 (整机主母线、XDS110、DAPLink) 的 I2C 采样与仿真回退保护
 * 内置互斥锁保护 I2C 硬件总线与数据缓存，杜绝双核竞争导致的 I2C 驱动死锁
 */
class TelemetryManager {
public:
    static TelemetryManager& Instance();

    /**
     * @brief 初始化 I2C 主机外设总线
     */
    void Init();

    /**
     * @brief 采集遥测数据
     * @param out_payload 写入 12 字节标准遥测结构体
     * @param power_mgr 供电管理模块引用 (用于联动通道断电时的电压/电流归零)
     */
    void ReadTelemetry(TelemetryPayload &out_payload, PowerManager &power_mgr);

private:
    TelemetryManager() = default;
    ~TelemetryManager() = default;

    TelemetryManager(const TelemetryManager&) = delete;
    TelemetryManager& operator=(const TelemetryManager&) = delete;

    bool ReadIna236Raw(uint8_t addr, uint16_t &out_v_mv, int16_t &out_i_ma);

    Mutex m_mutex;
    bool m_i2c_initialized{false};
};

} // namespace LinkNexus

