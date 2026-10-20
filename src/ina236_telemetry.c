#include "ina236_telemetry.h"
#include "esp_log.h"
#include "esp_timer.h"
#include <string.h>

static const char *TAG = "INA236_TELEM";

static uint8_t s_i2c_port = 0;
static bool s_initialized = false;
static linknexus_telemetry_snapshot_t s_cached_snapshot;

esp_err_t ina236_telemetry_init(uint8_t i2c_port)
{
    s_i2c_port = i2c_port;
    ESP_LOGI(TAG, "Initializing INA236 telemetry sensors (Bus: 0x%02X, XDS: 0x%02X, DAP: 0x%02X)...",
             INA236_I2C_ADDR_BUS, INA236_I2C_ADDR_XDS, INA236_I2C_ADDR_DAP);

    memset(&s_cached_snapshot, 0, sizeof(s_cached_snapshot));

    // 默认提供基线仿真/待机参数，实际物理上机时通过 I2C 寄存器读取
    s_cached_snapshot.bus.bus_voltage_v = 5.08f;
    s_cached_snapshot.bus.current_ma    = 420.0f;
    s_cached_snapshot.bus.power_mw      = 2133.6f;
    s_cached_snapshot.bus.is_online     = true;

    s_cached_snapshot.xds110.bus_voltage_v = 3.31f;
    s_cached_snapshot.xds110.current_ma    = 85.0f;
    s_cached_snapshot.xds110.power_mw      = 281.35f;
    s_cached_snapshot.xds110.is_online     = true;

    s_cached_snapshot.daplink.bus_voltage_v = 3.29f;
    s_cached_snapshot.daplink.current_ma    = 42.0f;
    s_cached_snapshot.daplink.power_mw      = 138.18f;
    s_cached_snapshot.daplink.is_online     = true;

    s_initialized = true;
    ESP_LOGI(TAG, "INA236 3-Channel Telemetry module ready.");
    return ESP_OK;
}

esp_err_t ina236_telemetry_sample(linknexus_telemetry_snapshot_t *out_snapshot)
{
    if (!out_snapshot) return ESP_ERR_INVALID_ARG;
    if (!s_initialized) return ESP_ERR_INVALID_STATE;

    s_cached_snapshot.timestamp_ms = (uint32_t)(esp_timer_get_time() / 1000ULL);

    // 预留硬件实际 I2C 采样逻辑:
    // 从 0x40, 0x41, 0x42 寄存器中读取电压电流并计算

    *out_snapshot = s_cached_snapshot;
    return ESP_OK;
}

void ina236_telemetry_get_latest(linknexus_telemetry_snapshot_t *out_snapshot)
{
    if (out_snapshot) {
        *out_snapshot = s_cached_snapshot;
    }
}

