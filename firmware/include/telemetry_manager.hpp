#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "protocol.hpp"
#include "power_manager.hpp"
#include "freertos_mutex.hpp"

namespace LinkNexus {

struct ImuMotionData {
    float roll_deg;
    float pitch_deg;
    float yaw_deg;
    float accel_x;
    float accel_y;
    float accel_z;
    bool is_hardware_present;
};

/**
 * @brief INA236 与 IMU 空间动捕遥测管理模块
 * 负责 3 路物理 INA236 (整机主母线、XDS110、DAPLink) 与 6 轴空间动捕姿态采样与动态回退保护
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

    /**
     * @brief 采集 6 轴 IMU 空间姿态与加速度动捕数据
     * 支持物理 I2C IMU (MPU6050/ICM-42688 0x68/0x69) 探测；未挂载时输出平滑旋转动力学仿真
     */
    void ReadImuMotion(ImuMotionData &out_imu);

private:
    TelemetryManager() = default;
    ~TelemetryManager() = default;

    TelemetryManager(const TelemetryManager&) = delete;
    TelemetryManager& operator=(const TelemetryManager&) = delete;

    bool ReadIna236Raw(uint8_t addr, uint16_t &out_v_mv, int16_t &out_i_ma);
    bool ProbeImuHardware(uint8_t &out_addr);

    Mutex m_mutex;
    bool m_i2c_initialized{false};
    bool m_imu_hardware_checked{false};
    bool m_imu_present{false};
    uint8_t m_imu_addr{0x68};
    float m_sim_yaw{0.0f};
    float m_sim_phase{0.0f};
};

} // namespace LinkNexus
