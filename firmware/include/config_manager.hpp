#pragma once

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "linknexus_config.hpp"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief 运行时默认配置管理与 NVS 闪存持久化模块 (方案 B 架构)
 * 允许在现有 CLI 命令下附加 save 参数进行固化，开机自动从 NVS 闪存加载
 */
class ConfigManager {
public:
    static ConfigManager& Instance();

    /**
     * @brief 初始化并从 NVS 闪存读取所有已保存的配置项
     */
    void Init();

    // Wi-Fi 射频参数
    int8_t GetTxPowerRaw() const { return m_tx_power_raw; }
    void SetTxPowerRaw(int8_t val) { m_tx_power_raw = val; }
    bool SaveTxPowerRaw(int8_t val);

    uint8_t GetProtocol() const { return m_protocol; }
    void SetProtocol(uint8_t mask) { m_protocol = mask; }
    bool SaveProtocol(uint8_t mask);

    uint8_t GetChannel() const { return m_channel; }
    void SetChannel(uint8_t ch) { m_channel = ch; }
    bool SaveChannel(uint8_t ch);

    // SoftAP 参数
    const char* GetApSsid() const { return m_ap_ssid; }
    const char* GetApPassword() const { return m_ap_pwd; }
    uint8_t GetApChannel() const { return m_ap_chan; }
    bool SaveSoftAp(const char *ssid, const char *pwd, uint8_t ch);

    // Station 外部连接参数
    const char* GetStaSsid() const { return m_sta_ssid; }
    const char* GetStaPassword() const { return m_sta_pwd; }
    bool GetStaAuto() const { return m_sta_auto; }
    bool SaveSta(const char *ssid, const char *pwd, bool auto_conn = true);

    // 6 路 MOS 供电开机默认掩码 (0x3F 为全开)
    uint8_t GetMosMask() const { return m_mos_mask; }
    void SetMosMask(uint8_t mask) { m_mos_mask = mask; }
    bool SaveMosMask(uint8_t mask);

    // 遥测广播流开机默认开关
    bool GetTelemetryStream() const { return m_tel_stream; }
    void SetTelemetryStream(bool en) { m_tel_stream = en; }
    bool SaveTelemetryStream(bool en);

    /**
     * @brief 一键将当前 Wi-Fi、MOS 与遥测参数固化保存至 NVS
     */
    bool SaveAllCurrent();

    /**
     * @brief 擦除 NVS 命名空间，恢复系统出厂默认配置
     */
    bool ResetToDefaults();

    /**
     * @brief 格式化当前 NVS 配置看板
     */
    size_t FormatConfigSummary(char *out_buf, size_t max_len);

private:
    ConfigManager();
    ~ConfigManager() = default;

    ConfigManager(const ConfigManager&) = delete;
    ConfigManager& operator=(const ConfigManager&) = delete;

    mutable Mutex m_mutex;
    bool m_initialized{false};

    int8_t m_tx_power_raw{RfConfig::DEFAULT_MAX_TX_POWER_RAW}; // 默认 52 (13.00 dBm)
    uint8_t m_protocol{7};                                     // 默认 11b|11g|11n
    uint8_t m_channel{RfConfig::DEFAULT_CHANNEL};               // 默认 1
    char m_ap_ssid[33]{"LinkNexus-ESP32-AP"};
    char m_ap_pwd[65]{"12345678"};
    uint8_t m_ap_chan{1};
    char m_sta_ssid[33]{""};
    char m_sta_pwd[65]{""};
    bool m_sta_auto{false};
    uint8_t m_mos_mask{0x3F};                                  // 默认 6 通道全开
    bool m_tel_stream{false};                                  // 默认静音保护终端
};

} // namespace LinkNexus
