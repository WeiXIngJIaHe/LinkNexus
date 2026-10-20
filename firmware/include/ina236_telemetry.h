#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief 三路 INA236 高精度电流/电压/功率硬件遥测驱动预留模块
 * 监测目标通道:
 * 1. 整机主母线 (Bus)  - I2C 地址: 0x40
 * 2. TI XDS110 通道     - I2C 地址: 0x41
 * 3. ARM DAPLink 通道   - I2C 地址: 0x42
 */

#define INA236_I2C_ADDR_BUS            0x40
#define INA236_I2C_ADDR_XDS            0x41
#define INA236_I2C_ADDR_DAP            0x42

typedef struct {
    float bus_voltage_v;     // 母线电压 (V)
    float current_ma;        // 回路电流 (mA)
    float power_mw;          // 实时功率 (mW)
    bool  is_online;         // 传感器是否通讯在线
} ina236_channel_data_t;

typedef struct {
    ina236_channel_data_t bus;     // 总路 (0x40)
    ina236_channel_data_t xds110;  // XDS110 (0x41)
    ina236_channel_data_t daplink; // DAPLink (0x42)
    uint32_t timestamp_ms;         // 采样时间戳
} linknexus_telemetry_snapshot_t;

/**
 * @brief 初始化 3 路 INA236 遥测传感器
 */
esp_err_t ina236_telemetry_init(uint8_t i2c_port);

/**
 * @brief 周期性触发 3 路传感器采样并返回最新快照
 */
esp_err_t ina236_telemetry_sample(linknexus_telemetry_snapshot_t *out_snapshot);

/**
 * @brief 获取最近一次缓存的遥测数据快照 (无阻塞)
 */
void ina236_telemetry_get_latest(linknexus_telemetry_snapshot_t *out_snapshot);

#ifdef __cplusplus
}
#endif

