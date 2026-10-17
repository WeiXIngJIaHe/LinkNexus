#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief ESP32-S3 Wi-Fi 工作模式枚举
 */
enum class WifiWorkMode : uint8_t {
    Off = 0,
    SoftAP = 1,     ///< 主动 AP 模式 (作为独立热点)
    Station = 2     ///< 从 AP 模式 (连接外部路由器/热点)
};

/**
 * @brief Wi-Fi 与无线调试运行状态模型
 */
struct WifiStatusInfo {
    WifiWorkMode mode{WifiWorkMode::Off};
    char ssid[33]{0};
    char ip_str[16]{0};
    int8_t rssi{0};
    uint8_t channel{1};
    uint8_t connected_clients{0};
    bool is_wireless_debug_active{false};
    uint16_t wireless_debug_port{8266};
};

/**
 * @brief ESP32-S3 Wi-Fi 硬件管理与无线调试引擎 (C++17 线程安全单例)
 * 遵循 FreeRTOS 双核架构，使用互斥锁保护 Wi-Fi 状态切换与事件回调
 */
class WifiManager {
public:
    static WifiManager& Instance();

    /**
     * @brief 初始化 Wi-Fi 协议栈、NVS 及事件驱动循环
     */
    void Init();

    /**
     * @brief 启动 SoftAP 模式 (ESP32-S3 主动作为热点)
     * @param ssid 热点 SSID (默认为 "LinkNexus-ESP32-AP")
     * @param password 接入密码 (至少 8 位，为空则开放)
     * @param channel Wi-Fi 信道 (1~13)
     */
    bool StartAp(const char *ssid, const char *password = "12345678", uint8_t channel = 1);

    /**
     * @brief 启动 Station 模式 (ESP32-S3 作为从设备连接外部 AP)
     * @param ssid 目标 AP 名称
     * @param password 目标 AP 密码
     */
    bool ConnectSta(const char *ssid, const char *password);

    /**
     * @brief 断开并关闭 Wi-Fi 射频输出
     */
    bool Disconnect();

    /**
     * @brief 扫描周围可用 Wi-Fi 热点并格式化返回 (支持一次性扫描与缓存复用)
     * @param out_buf 输出缓冲区
     * @param max_len 最大长度
     * @param force_rescan 是否强制重新扫描 (默认 false: 已扫描则直接复用缓存，确保仅需执行一次)
     */
    size_t ScanNetworks(char *out_buf, size_t max_len, bool force_rescan = false);

    /**
     * @brief 线程安全获取当前 Wi-Fi 与无线调试状态
     */
    WifiStatusInfo GetStatus();

    /**
     * @brief 启停无线 TCP 调试服务 (默认端口 8266)
     */
    void SetWirelessDebug(bool enable, uint16_t port = 8266);

    /**
     * @brief 启动系统开机初始 Wi-Fi 扫描任务 (后台单次执行，并在串口打印扫描结果)
     */
    void StartInitialScanTask();

    /**
     * @brief 供底层事件驱动回调更新内部状态模型
     */
    void OnStaGotIp(const void *ip);
    void OnStaDisconnected();
    void IncrementClients();
    void DecrementClients();
    void OnScanDone();

private:
    WifiManager();
    ~WifiManager() = default;

    WifiManager(const WifiManager&) = delete;
    WifiManager& operator=(const WifiManager&) = delete;

    static void WirelessDebugServerTask(void *pvParameters);
    static void InitialScanTaskEntry(void *pvParameters);

    Mutex m_mutex;
    bool m_initialized{false};
    WifiStatusInfo m_status;
    bool m_server_task_running{false};

    // Wi-Fi 扫描一次性执行与缓存机制 (彻底避免重复 3 秒阻塞)
    bool m_has_scanned{false};
    bool m_is_scanning{false};
    char m_cached_scan_result[1536]{0};
    size_t m_cached_scan_len{0};
    SemaphoreHandle_t m_scan_sem{nullptr};
};

} // namespace LinkNexus

