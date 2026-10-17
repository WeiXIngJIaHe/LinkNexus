#include "wifi_manager.hpp"
#include "comm_manager.hpp"
#include "cli_engine.hpp"
#include "linknexus_config.hpp"
#include "config_manager.hpp"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "nvs_flash.h"
#include "lwip/err.h"
#include "lwip/sockets.h"
#include "lwip/sys.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>
#include <stdio.h>
#include <algorithm>
#include <memory>

namespace LinkNexus {

static const char *TAG = "WIFI_MGR";

static esp_netif_t *s_ap_netif = nullptr;
static esp_netif_t *s_sta_netif = nullptr;

static const char* AuthModeToString(wifi_auth_mode_t authmode)
{
    switch (authmode) {
        case WIFI_AUTH_OPEN:            return "OPEN";
        case WIFI_AUTH_WEP:             return "WEP";
        case WIFI_AUTH_WPA_PSK:         return "WPA-PSK";
        case WIFI_AUTH_WPA2_PSK:        return "WPA2-PSK";
        case WIFI_AUTH_WPA_WPA2_PSK:    return "WPA/WPA2";
        case WIFI_AUTH_WPA2_ENTERPRISE: return "WPA2-ENT";
        case WIFI_AUTH_WPA3_PSK:        return "WPA3-PSK";
        case WIFI_AUTH_WPA2_WPA3_PSK:   return "WPA2/WPA3";
        case WIFI_AUTH_WAPI_PSK:        return "WAPI";
        case WIFI_AUTH_OWE:             return "OWE";
        default:                        return "UNKNOWN";
    }
}

WifiManager& WifiManager::Instance()
{
    static WifiManager instance;
    return instance;
}

WifiManager::WifiManager()
{
    auto &cfg = ConfigManager::Instance();
    m_status.mode = WifiWorkMode::Off;
    strncpy(m_status.ssid, cfg.GetApSsid(), sizeof(m_status.ssid) - 1);
    strncpy(m_status.ip_str, "--", sizeof(m_status.ip_str) - 1);
    m_status.rssi = 0;
    m_status.channel = cfg.GetChannel();
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = false;
    m_status.wireless_debug_port = 8266;
    m_tx_power_raw = cfg.GetTxPowerRaw();
    m_status.tx_power_dbm = static_cast<float>(m_tx_power_raw) / 4.0f;
    m_protocol_mask = cfg.GetProtocol();
    m_status.protocol_mask = m_protocol_mask;
    m_last_disconnect_reason = 0;
    m_status.last_disconnect_reason = 0;
}

static void wifi_event_handler(void *arg, esp_event_base_t event_base,
                               int32_t event_id, void *event_data)
{
    auto &mgr = WifiManager::Instance();

    if (event_base == WIFI_EVENT) {
        if (event_id == WIFI_EVENT_AP_STACONNECTED) {
            auto *event = (wifi_event_ap_staconnected_t *)event_data;
            ESP_LOGI(TAG, "Client joined SoftAP (AID=%d, MAC=" MACSTR ")",
                     event->aid, MAC2STR(event->mac));
            mgr.IncrementClients();
        } else if (event_id == WIFI_EVENT_AP_STADISCONNECTED) {
            auto *event = (wifi_event_ap_stadisconnected_t *)event_data;
            uint8_t reason = event ? event->reason : 0;
            ESP_LOGW(TAG, "Client left SoftAP (AID=%d, MAC=" MACSTR ", Reason: %u - %s)",
                     event->aid, MAC2STR(event->mac), reason, WifiManager::GetDisconnectReasonDesc(reason));
            mgr.DecrementClients();
        } else if (event_id == WIFI_EVENT_STA_START) {
            ESP_LOGI(TAG, "Wi-Fi Station interface started (idle / ready for scan or connect).");
        } else if (event_id == WIFI_EVENT_STA_DISCONNECTED) {
            auto *event = (wifi_event_sta_disconnected_t *)event_data;
            uint8_t reason = event ? event->reason : 0;
            ESP_LOGW(TAG, "Station disconnected from AP. Reason code: %u (%s)",
                     reason, WifiManager::GetDisconnectReasonDesc(reason));
            if (reason == 2 || reason == 15 || reason == 202 || reason == 203 || reason == 204) {
                ESP_LOGW(TAG, ">> RF天线诊断: 收到Beacon但握手/认证超时，属于典型 0Ω 直连天线阻抗失配或发射EVM恶化。");
                ESP_LOGW(TAG, ">> 建议对策: 可在控制台输入 'wifi txpower 10' (降为10dBm) 或 'wifi protocol bg' (降速至11b/g)。");
            }
            mgr.OnStaDisconnected(reason);
        } else if (event_id == WIFI_EVENT_SCAN_DONE) {
            ESP_LOGI(TAG, "Hardware Wi-Fi channel scan completed (WIFI_EVENT_SCAN_DONE).");
            mgr.NotifyScanDone();
        }
    } else if (event_base == IP_EVENT) {
        if (event_id == IP_EVENT_STA_GOT_IP) {
            auto *event = (ip_event_got_ip_t *)event_data;
            ESP_LOGI(TAG, "Station acquired real IP: " IPSTR, IP2STR(&event->ip_info.ip));
            mgr.OnStaGotIp(&event->ip_info.ip);
        }
    }
}

void WifiManager::Init()
{
    LockGuard lock(m_mutex);
    if (m_initialized) return;

    ESP_LOGI(TAG, "Initializing Wi-Fi subsystem (NVS & Netif)...");

    if (!m_scan_sem) {
        m_scan_sem = xSemaphoreCreateBinary();
    }

    // 1. 初始化 NVS 闪存
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    // 2. 初始化网络接口
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());

    s_ap_netif = esp_netif_create_default_wifi_ap();
    s_sta_netif = esp_netif_create_default_wifi_sta();

    // 3. 初始化 Wi-Fi 驱动底层
    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));

    ESP_ERROR_CHECK(esp_event_handler_instance_register(WIFI_EVENT,
                                                        ESP_EVENT_ANY_ID,
                                                        &wifi_event_handler,
                                                        nullptr,
                                                        nullptr));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(IP_EVENT,
                                                        IP_EVENT_STA_GOT_IP,
                                                        &wifi_event_handler,
                                                        nullptr,
                                                        nullptr));

    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));

    // 4. 针对裸芯片 ESP32-S3FH4R2 与自制 IPEX 0Ω 直连天线，配置高容限射频基准
    //    - 默认发射功率下调至安全测试基线 13.00dBm (raw 52)，抑制反射波与 EVM 恶化
    //    - 强制锁定信道带宽为 20MHz (WIFI_BW_HT20)，提升功率谱密度并减少带外衰落
    esp_wifi_set_max_tx_power(m_tx_power_raw);
    esp_wifi_set_bandwidth(WIFI_IF_AP, WIFI_BW_HT20);
    esp_wifi_set_bandwidth(WIFI_IF_STA, WIFI_BW_HT20);
    esp_wifi_set_protocol(WIFI_IF_AP, m_protocol_mask);
    esp_wifi_set_protocol(WIFI_IF_STA, m_protocol_mask);

    m_initialized = true;
    ESP_LOGI(TAG, "Wi-Fi subsystem initialized (TxPower: %.2f dBm, Bandwidth: HT20). Ready for AP/STA and wireless CLI.",
             static_cast<float>(m_tx_power_raw) / 4.0f);
}

bool WifiManager::StartAp(const char *ssid, const char *password, uint8_t channel)
{
    LockGuard lock(m_mutex);
    if (!m_initialized) Init();

    if (!ssid || strlen(ssid) == 0) {
        ssid = "LinkNexus-ESP32-AP";
    }

    ESP_LOGI(TAG, "Configuring ESP32-S3 as SoftAP (SSID: '%s', Ch: %u, TxPower: %.2f dBm)...",
             ssid, channel, static_cast<float>(m_tx_power_raw) / 4.0f);

    wifi_config_t wifi_config = {};
    strncpy((char *)wifi_config.ap.ssid, ssid, sizeof(wifi_config.ap.ssid) - 1);
    wifi_config.ap.ssid_len = strlen(ssid);
    wifi_config.ap.channel = (channel >= 1 && channel <= 13) ? channel : 1;
    wifi_config.ap.max_connection = 4;

    if (password && strlen(password) >= 8) {
        strncpy((char *)wifi_config.ap.password, password, sizeof(wifi_config.ap.password) - 1);
        wifi_config.ap.authmode = WIFI_AUTH_WPA2_PSK;
    } else {
        wifi_config.ap.authmode = WIFI_AUTH_OPEN;
    }

    esp_wifi_stop();
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_APSTA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_AP, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());

    // 应用射频参数：功率、带宽与协议模式
    esp_wifi_set_max_tx_power(m_tx_power_raw);
    esp_wifi_set_bandwidth(WIFI_IF_AP, WIFI_BW_HT20);
    esp_wifi_set_protocol(WIFI_IF_AP, m_protocol_mask);

    m_status.mode = WifiWorkMode::SoftAP;
    strncpy(m_status.ssid, ssid, sizeof(m_status.ssid) - 1);
    m_status.channel = wifi_config.ap.channel;
    strncpy(m_status.ip_str, "192.168.4.1", sizeof(m_status.ip_str) - 1);
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = true;

    // 启动无线调试 Socket 服务
    SetWirelessDebug(true, 8266);

    ESP_LOGI(TAG, "SoftAP active at IP %s, Wireless CLI listening on port %u.",
             m_status.ip_str, m_status.wireless_debug_port);
    return true;
}

bool WifiManager::ConnectSta(const char *ssid, const char *password)
{
    LockGuard lock(m_mutex);
    if (!m_initialized) Init();

    if (!ssid || strlen(ssid) == 0) return false;

    ESP_LOGI(TAG, "Connecting to external AP (SSID: '%s', TxPower: %.2f dBm)...",
             ssid, static_cast<float>(m_tx_power_raw) / 4.0f);

    wifi_config_t wifi_config = {};
    strncpy((char *)wifi_config.sta.ssid, ssid, sizeof(wifi_config.sta.ssid) - 1);
    if (password && strlen(password) > 0) {
        strncpy((char *)wifi_config.sta.password, password, sizeof(wifi_config.sta.password) - 1);
    }
    wifi_config.sta.threshold.authmode = (password && strlen(password) > 0) ? WIFI_AUTH_WPA2_PSK : WIFI_AUTH_OPEN;

    esp_wifi_stop();
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());

    // 应用射频参数：功率、带宽与协议模式
    esp_wifi_set_max_tx_power(m_tx_power_raw);
    esp_wifi_set_bandwidth(WIFI_IF_STA, WIFI_BW_HT20);
    esp_wifi_set_protocol(WIFI_IF_STA, m_protocol_mask);

    esp_wifi_connect();

    m_status.mode = WifiWorkMode::Station;
    strncpy(m_status.ssid, ssid, sizeof(m_status.ssid) - 1);
    strncpy(m_status.ip_str, "Connecting...", sizeof(m_status.ip_str) - 1);
    m_status.rssi = 0;
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = false;

    return true;
}

bool WifiManager::IsStaConnected(const char *target_ssid)
{
    LockGuard lock(m_mutex);
    if (m_status.mode != WifiWorkMode::Station) {
        return false;
    }
    // 检查 IP 是否有效（非 Connecting...、非 Disc、非 -- 且非空）
    if (strcmp(m_status.ip_str, "Connecting...") == 0 ||
        strstr(m_status.ip_str, "Disc") != nullptr ||
        strcmp(m_status.ip_str, "--") == 0 ||
        m_status.ip_str[0] == '\0') {
        return false;
    }
    if (target_ssid != nullptr && target_ssid[0] != '\0') {
        return (strcmp(m_status.ssid, target_ssid) == 0);
    }
    return true;
}

bool WifiManager::IsStaConnecting()
{
    LockGuard lock(m_mutex);
    return (m_status.mode == WifiWorkMode::Station &&
            strcmp(m_status.ip_str, "Connecting...") == 0);
}

void WifiManager::OnStaGotIp(const void *ip)
{
    LockGuard lock(m_mutex);
    const esp_ip4_addr_t *ip_addr = static_cast<const esp_ip4_addr_t*>(ip);
    esp_ip4addr_ntoa(ip_addr, m_status.ip_str, sizeof(m_status.ip_str));

    int rssi = 0;
    if (esp_wifi_sta_get_rssi(&rssi) == ESP_OK) {
        m_status.rssi = static_cast<int8_t>(rssi);
    }
    m_status.mode = WifiWorkMode::Station;
    m_status.is_wireless_debug_active = true;

    SetWirelessDebug(true, 8266);
}

void WifiManager::OnStaDisconnected(uint8_t reason)
{
    LockGuard lock(m_mutex);
    m_last_disconnect_reason = reason;
    m_status.last_disconnect_reason = reason;
    if (m_status.mode == WifiWorkMode::Station) {
        if (reason > 0) {
            snprintf(m_status.ip_str, sizeof(m_status.ip_str), "Disc (code %u)", reason);
        } else {
            strncpy(m_status.ip_str, "Disconnected", sizeof(m_status.ip_str) - 1);
        }
        m_status.rssi = 0;
    }
}

void WifiManager::IncrementClients()
{
    LockGuard lock(m_mutex);
    if (m_status.connected_clients < 255) {
        m_status.connected_clients++;
    }
}

void WifiManager::DecrementClients()
{
    LockGuard lock(m_mutex);
    if (m_status.connected_clients > 0) {
        m_status.connected_clients--;
    }
}

bool WifiManager::Disconnect()
{
    LockGuard lock(m_mutex);
    esp_wifi_disconnect();
    esp_wifi_stop();

    m_status.mode = WifiWorkMode::Off;
    strncpy(m_status.ip_str, "--", sizeof(m_status.ip_str) - 1);
    m_status.rssi = 0;
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = false;

    ESP_LOGI(TAG, "Wi-Fi disconnected and RF power-down.");
    return true;
}

void WifiManager::NotifyScanDone()
{
    if (m_scan_sem) {
        xSemaphoreGive(m_scan_sem);
    }
}

size_t WifiManager::PerformScanAndFormat(bool print_to_console)
{
    // 确保 Wi-Fi 模式为 APSTA 或 STA，以保证 Station 射频扫描接口处于就绪态
    wifi_mode_t cur_mode = WIFI_MODE_NULL;
    esp_wifi_get_mode(&cur_mode);
    if (cur_mode == WIFI_MODE_AP) {
        esp_wifi_set_mode(WIFI_MODE_APSTA);
    } else if (cur_mode != WIFI_MODE_APSTA && cur_mode != WIFI_MODE_STA) {
        esp_wifi_set_mode(WIFI_MODE_STA);
        esp_wifi_start();
    }

    if (m_scan_sem) {
        xSemaphoreTake(m_scan_sem, 0); // 清空历史信号量
    }

    {
        LockGuard lock(m_mutex);
        m_is_scanning = true;
    }

    // 调用非阻塞异步扫描 (block = false, config = nullptr 采用乐鑫官方最优 dwell 时间)
    esp_err_t err = esp_wifi_scan_start(nullptr, false);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_scan_start failed: %d", err);
        LockGuard lock(m_mutex);
        m_is_scanning = false;
        m_cached_scan_len = snprintf(m_cached_scan_result, sizeof(m_cached_scan_result),
                                     "[WiFi Scan] 启动扫描失败 (错误码: %d)\r\n", err);
        return m_cached_scan_len;
    }

    // 等待后台扫描完成信号量 (最多等待 4500ms)，等待期间任务处于阻塞态，零消耗 CPU
    bool scan_ok = (m_scan_sem && xSemaphoreTake(m_scan_sem, pdMS_TO_TICKS(4500)) == pdTRUE);
    if (!scan_ok) {
        ESP_LOGW(TAG, "Wi-Fi scan wait timeout.");
        LockGuard lock(m_mutex);
        m_is_scanning = false;
        m_cached_scan_len = snprintf(m_cached_scan_result, sizeof(m_cached_scan_result),
                                     "[WiFi Scan] 扫描等待超时，建议稍后重试。\r\n");
        return m_cached_scan_len;
    }

    // 扫描成功完成，开始获取并解析 AP 记录 (在此具有 8KB+ 充足栈深的工作任务中安全执行)
    uint16_t ap_count = 0;
    esp_wifi_scan_get_ap_num(&ap_count);

    LockGuard lock(m_mutex);
    size_t offset = 0;

    if (ap_count == 0) {
        offset = snprintf(m_cached_scan_result, sizeof(m_cached_scan_result),
            "\r\n================ 周边 2.4GHz Wi-Fi 热点扫描结果 ================\r\n"
            "  未扫描到周围可用的 2.4GHz Wi-Fi 无线网络。\r\n"
            "=================================================================\r\n");
    } else {
        uint16_t records_to_get = (ap_count > 16) ? 16 : ap_count;
        // 使用堆内存分配 ap_records，杜绝消耗任何任务栈深
        auto ap_records = std::unique_ptr<wifi_ap_record_t[]>(new (std::nothrow) wifi_ap_record_t[records_to_get]);
        if (ap_records) {
            esp_wifi_scan_get_ap_records(&records_to_get, ap_records.get());

            std::sort(ap_records.get(), ap_records.get() + records_to_get, [](const wifi_ap_record_t &a, const wifi_ap_record_t &b) {
                return a.rssi > b.rssi;
            });

            offset = snprintf(m_cached_scan_result, sizeof(m_cached_scan_result),
                "\r\n================ 周边 2.4GHz Wi-Fi 热点扫描结果 ================\r\n"
                "  序号  SSID                      RSSI     信道  加密认证\r\n"
                "-----------------------------------------------------------------\r\n");

            for (uint16_t i = 0; i < records_to_get && offset < sizeof(m_cached_scan_result) - 80; ++i) {
                const char *auth_str = AuthModeToString(ap_records[i].authmode);
                char ssid_str[33];
                strncpy(ssid_str, (const char *)ap_records[i].ssid, sizeof(ssid_str) - 1);
                ssid_str[sizeof(ssid_str) - 1] = '\0';
                if (strlen(ssid_str) == 0) {
                    strcpy(ssid_str, "<Hidden-SSID>");
                }

                offset += snprintf(m_cached_scan_result + offset, sizeof(m_cached_scan_result) - offset,
                                   "  [%02u] %-24s  %4d dBm  CH%2u  %-10s\r\n",
                                   i + 1, ssid_str, ap_records[i].rssi, ap_records[i].primary, auth_str);
            }

            offset += snprintf(m_cached_scan_result + offset, sizeof(m_cached_scan_result) - offset,
                               "-----------------------------------------------------------------\r\n"
                               "  扫描完成: 检索到 %u 个无线网络 (已展示信号最强的前 %u 个)\r\n"
                               "=================================================================\r\n",
                               ap_count, records_to_get);
        } else {
            offset = snprintf(m_cached_scan_result, sizeof(m_cached_scan_result),
                              "[WiFi Scan] 内存不足，无法解析 AP 列表。\r\n");
        }
    }

    m_cached_scan_len = (offset < sizeof(m_cached_scan_result) - 1) ? offset : sizeof(m_cached_scan_result) - 1;
    m_cached_scan_result[m_cached_scan_len] = '\0';
    m_has_scanned = true;
    m_is_scanning = false;

    if (print_to_console) {
        CommManager::Instance().Print(m_cached_scan_result);
        ESP_LOGI(TAG, "Wi-Fi scan cached & printed to console (%u networks detected).", ap_count);
    }

    return m_cached_scan_len;
}

size_t WifiManager::ScanNetworks(char *out_buf, size_t max_len, bool force_rescan)
{
    if (!out_buf || max_len == 0) return 0;

    // 1. 若已有扫描结果且未指定强制重扫，直接快速复用缓存 (0ms 延迟，不阻塞通信)
    {
        LockGuard lock(m_mutex);
        if (m_has_scanned && !force_rescan && m_cached_scan_len > 0) {
            size_t copy_len = (m_cached_scan_len < max_len - 1) ? m_cached_scan_len : max_len - 1;
            memcpy(out_buf, m_cached_scan_result, copy_len);
            out_buf[copy_len] = '\0';
            ESP_LOGI(TAG, "Returning cached Wi-Fi scan result (len=%u).", copy_len);
            return copy_len;
        }

        if (m_is_scanning) {
            return snprintf(out_buf, max_len, "[WiFi Scan] 正在扫描周边无线射频信道中，请稍候...\r\n");
        }
    }

    if (!m_initialized) Init();

    ESP_LOGI(TAG, "Starting on-demand background Wi-Fi channel scan...");
    CommManager::Instance().Print("[WIFI] 启动按需周边 Wi-Fi 射频信道扫描...\r\n");

    size_t scan_len = PerformScanAndFormat(false);

    LockGuard lock(m_mutex);
    size_t copy_len = (scan_len < max_len - 1) ? scan_len : max_len - 1;
    memcpy(out_buf, m_cached_scan_result, copy_len);
    out_buf[copy_len] = '\0';
    return copy_len;
}

void WifiManager::InitialScanTaskEntry(void *pvParameters)
{
    (void)pvParameters;
    vTaskDelay(pdMS_TO_TICKS(800)); // 等待系统各子系统及串口初始化完全就绪

    // 1. 开机默认启动 SoftAP (独立主热点)，使手机/电脑能在 Wi-Fi 列表立即搜索并连接
    WifiManager::Instance().StartAp("LinkNexus-ESP32-AP", "12345678", 1);
    CommManager::Instance().Print("\r\n[WIFI] 开机默认 SoftAP 热点已启动: 'LinkNexus-ESP32-AP' (密码: 12345678, IP: 192.168.4.1)\r\n");

    vTaskDelay(pdMS_TO_TICKS(500));

    // 2. 触发周边 Wi-Fi 射频扫描并等待结果 (在当前任务 8192 栈深中安全处理与输出，完全解耦事件循环 sys_evt)
    ESP_LOGI(TAG, "Triggering initial non-blocking background Wi-Fi scan in dedicated worker task...");
    CommManager::Instance().Print("[WIFI] 正在执行系统开机初始周边 Wi-Fi 射频扫描 (异步无阻塞)...\r\n");

    WifiManager::Instance().PerformScanAndFormat(true);

    // 扫描并在控制台安全输出完成后，自毁临时任务释放 8KB 堆内存
    ESP_LOGI(TAG, "Initial Wi-Fi scan task completed and exiting.");
    vTaskDelete(nullptr);
}

void WifiManager::StartInitialScanTask()
{
    xTaskCreatePinnedToCore(InitialScanTaskEntry,
                            "init_wifi_scan",
                            8192,
                            nullptr,
                            3,
                            nullptr,
                            Cores::SYSTEM_CORE);
}

WifiStatusInfo WifiManager::GetStatus()
{
    LockGuard lock(m_mutex);
    if (m_status.mode == WifiWorkMode::Station && strcmp(m_status.ip_str, "--") != 0 && strncmp(m_status.ip_str, "Disc", 4) != 0 && strcmp(m_status.ip_str, "Connecting...") != 0) {
        int rssi = 0;
        if (esp_wifi_sta_get_rssi(&rssi) == ESP_OK) {
            m_status.rssi = static_cast<int8_t>(rssi);
        }
    }
    m_status.tx_power_dbm = static_cast<float>(m_tx_power_raw) / 4.0f;
    m_status.protocol_mask = m_protocol_mask;
    m_status.last_disconnect_reason = m_last_disconnect_reason;
    return m_status;
}

void WifiManager::SetWirelessDebug(bool enable, uint16_t port)
{
    m_status.is_wireless_debug_active = enable;
    m_status.wireless_debug_port = port;

    if (enable && !m_server_task_running) {
        m_server_task_running = true;
        xTaskCreatePinnedToCore(WirelessDebugServerTask,
                                "wifi_cli_srv",
                                4096,
                                this,
                                4,
                                nullptr,
                                Cores::SYSTEM_CORE);
    }
}

void WifiManager::WirelessDebugServerTask(void *pvParameters)
{
    auto *self = static_cast<WifiManager*>(pvParameters);
    uint16_t port = self->m_status.wireless_debug_port;

    int listen_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_IP);
    if (listen_sock < 0) {
        ESP_LOGE(TAG, "Unable to create wireless debug socket: errno %d", errno);
        self->m_server_task_running = false;
        vTaskDelete(nullptr);
        return;
    }

    int opt = 1;
    setsockopt(listen_sock, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    struct sockaddr_in dest_addr;
    dest_addr.sin_addr.s_addr = htonl(INADDR_ANY);
    dest_addr.sin_family = AF_INET;
    dest_addr.sin_port = htons(port);

    int err = bind(listen_sock, (struct sockaddr *)&dest_addr, sizeof(dest_addr));
    if (err != 0) {
        ESP_LOGE(TAG, "Socket unable to bind port %u: errno %d", port, errno);
        close(listen_sock);
        self->m_server_task_running = false;
        vTaskDelete(nullptr);
        return;
    }

    err = listen(listen_sock, 1);
    if (err != 0) {
        ESP_LOGE(TAG, "Error occurred during listen: errno %d", errno);
        close(listen_sock);
        self->m_server_task_running = false;
        vTaskDelete(nullptr);
        return;
    }

    ESP_LOGI(TAG, "Wireless Debugging CLI Server listening on port %u...", port);

    char rx_buffer[256];
    char tx_buffer[512];

    while (self->m_status.is_wireless_debug_active) {
        struct sockaddr_in source_addr;
        socklen_t addr_len = sizeof(source_addr);
        int sock = accept(listen_sock, (struct sockaddr *)&source_addr, &addr_len);
        if (sock < 0) {
            vTaskDelay(pdMS_TO_TICKS(100));
            continue;
        }

        ESP_LOGI(TAG, "Wireless Debug client connected from %s", inet_ntoa(source_addr.sin_addr));

        const char *welcome = "================================================\r\n"
                              "  LinkNexus ESP32-S3 Wireless Debug CLI\r\n"
                              "  Type 'help' for available commands.\r\n"
                              "================================================\r\n> ";
        send(sock, welcome, strlen(welcome), 0);

        while (1) {
            int len = recv(sock, rx_buffer, sizeof(rx_buffer) - 1, 0);
            if (len <= 0) {
                break;
            }
            rx_buffer[len] = '\0';

            size_t resp_len = CliEngine::Instance().Execute(rx_buffer, tx_buffer, sizeof(tx_buffer) - 4);
            if (resp_len > 0) {
                strcat(tx_buffer, "> ");
                send(sock, tx_buffer, strlen(tx_buffer), 0);
            } else {
                const char *prompt = "> ";
                send(sock, prompt, strlen(prompt), 0);
            }
        }

        shutdown(sock, 0);
        close(sock);
        ESP_LOGI(TAG, "Wireless Debug client disconnected.");
    }

    close(listen_sock);
    self->m_server_task_running = false;
    vTaskDelete(nullptr);
}

bool WifiManager::SetTxPowerRaw(int8_t power_raw)
{
    LockGuard lock(m_mutex);
    if (power_raw < RfConfig::MIN_TX_POWER_RAW) power_raw = RfConfig::MIN_TX_POWER_RAW;
    if (power_raw > RfConfig::MAX_TX_POWER_RAW) power_raw = RfConfig::MAX_TX_POWER_RAW;

    m_tx_power_raw = power_raw;
    m_status.tx_power_dbm = static_cast<float>(power_raw) / 4.0f;

    if (m_initialized) {
        esp_err_t err = esp_wifi_set_max_tx_power(power_raw);
        ESP_LOGI(TAG, "Wi-Fi max TX power set to %.2f dBm (raw: %d, ret: %d)",
                 m_status.tx_power_dbm, power_raw, err);
        return (err == ESP_OK);
    }
    return true;
}

bool WifiManager::SetTxPowerDbm(float dbm)
{
    int8_t raw = static_cast<int8_t>(dbm * 4.0f + 0.5f);
    return SetTxPowerRaw(raw);
}

bool WifiManager::SetProtocol(uint8_t protocol_mask)
{
    LockGuard lock(m_mutex);
    if (protocol_mask == 0) protocol_mask = 7;
    m_protocol_mask = protocol_mask;
    m_status.protocol_mask = protocol_mask;

    if (m_initialized) {
        esp_wifi_set_protocol(WIFI_IF_AP, protocol_mask);
        esp_wifi_set_protocol(WIFI_IF_STA, protocol_mask);
        ESP_LOGI(TAG, "Wi-Fi protocol set to 0x%02X (%s)", protocol_mask, GetProtocolName());
    }
    return true;
}

const char* WifiManager::GetProtocolName() const
{
    if (m_protocol_mask == WIFI_PROTOCOL_11B) {
        return "802.11b (CCK DSSS 1~11Mbps - 极高阻抗失配容限)";
    } else if (m_protocol_mask == (WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G)) {
        return "802.11b/g (CCK/OFDM 1~54Mbps - 剔除11n高阶MCS)";
    } else if (m_protocol_mask & WIFI_PROTOCOL_11N) {
        return "802.11b/g/n (混合全速模式)";
    }
    return "Custom Mode";
}

bool WifiManager::SetChannel(uint8_t channel)
{
    LockGuard lock(m_mutex);
    if (channel < 1 || channel > 13) return false;
    m_status.channel = channel;
    if (m_initialized) {
        esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE);
        ESP_LOGI(TAG, "Wi-Fi channel set to CH%u (%u MHz)", channel, GetChannelFreqMhz(channel));
    }
    return true;
}

const char* WifiManager::GetDisconnectReasonDesc(uint8_t reason)
{
    switch (reason) {
        case 1:   return "UNSPECIFIED (未指定原因)";
        case 2:   return "AUTH_EXPIRE (认证超时 - 上行认证帧对端未收到/天线失配)";
        case 3:   return "AUTH_LEAVE (对端主动退出认证)";
        case 4:   return "ASSOC_EXPIRE (关联超时)";
        case 5:   return "ASSOC_TOOMANY (AP客户端数已满)";
        case 6:   return "NOT_AUTHED (尚未完成认证)";
        case 7:   return "NOT_ASSOCED (尚未完成关联)";
        case 8:   return "ASSOC_LEAVE (主动断开关联)";
        case 15:  return "4WAY_HANDSHAKE_TIMEOUT (四次握手超时 - 密钥交换发射帧丢失，典型阻抗失配)";
        case 16:  return "GROUP_KEY_UPDATE_TIMEOUT (组播组密钥更新超时)";
        case 200: return "BEACON_TIMEOUT (AP心跳信标超时丢失)";
        case 201: return "NO_AP_FOUND (未找到目标SSID热点)";
        case 202: return "AUTH_FAIL (认证失败 - 密码错误或协议不匹配)";
        case 203: return "ASSOC_FAIL (关联失败 - 射频握手被拒)";
        case 204: return "HANDSHAKE_TIMEOUT (握手超时 - 射频上行发送包损坏丢弃)";
        case 205: return "CONNECTION_FAIL (物理层连接失败)";
        default:  return "UNKNOWN_REASON";
    }
}

size_t WifiManager::FormatRfDiagnostics(char *out_buf, size_t max_len)
{
    if (!out_buf || max_len < 256) return 0;
    LockGuard lock(m_mutex);

    int8_t actual_raw = 0;
    esp_wifi_get_max_tx_power(&actual_raw);
    float actual_dbm = static_cast<float>(actual_raw) / 4.0f;
    uint32_t freq_mhz = GetChannelFreqMhz(m_status.channel);

    int len = snprintf(out_buf, max_len,
        "=================================================================================\r\n"
        "  LinkNexus ESP32-S3 射频前端与自制天线诊断台 (RF Diagnostics)\r\n"
        "=================================================================================\r\n"
        "  核心芯片型号 : %s\r\n"
        "  天线射频电路 : %s\r\n"
        "  工作信道频率 : CH%u (%u MHz - 2.4GHz ISM频段)\r\n"
        "  设定发射功率 : %.2f dBm (底层寄存器: %d, 范围: 8.00~20.00 dBm)\r\n"
        "  实际生效功率 : %.2f dBm (硬件实际输出: raw %d)\r\n"
        "  Wi-Fi协议模式: %s\r\n"
        "  信道基带带宽 : HT20 (20MHz 窄带高信噪比)\r\n"
        "  最近断连原因 : [代码 %u] %s\r\n"
        "---------------------------------------------------------------------------------\r\n"
        "  【自制板 0Ω 直连 Π 网天线失配调谐建议】:\r\n"
        "  1. 现象: 能够扫描到周围 AP，但自身无法连接路由器或手机连不上 SoftAP；\r\n"
        "  2. 原因: 接收灵敏度高达 -98dBm (可捕获强Beacon)，但未经 LC 阻抗调谐的 0Ω 直连\r\n"
        "     导致发射端阻抗严重偏离 50Ω (VSWR 偏高)，在 20dBm 满功率发射时 PA 严重失真\r\n"
        "     (EVM 恶化) 且瞬间拉低电源轨，导致上行 Auth/Assoc/4次握手报文在对端校验失败。\r\n"
        "  3. 对策指令:\r\n"
        "     - 'wifi txpower 10'   : 将发射功率降至 10dBm (压制反射与EVM失真，验证ANT线)\r\n"
        "     - 'wifi txpower 13'   : 设置为 13dBm 标准测试基线功率\r\n"
        "     - 'wifi protocol bg'  : 降低调制速率至 11b/g (剔除 11n 高阶 OFDM 64QAM)\r\n"
        "     - 'wifi protocol b'   : 强制 802.11b CCK 调制 (对天线阻抗失配容忍度最高)\r\n"
        "=================================================================================\r\n",
        LINKNEXUS_CHIP_PART_NUMBER,
        LINKNEXUS_RF_ANTENNA_DESC,
        m_status.channel, freq_mhz,
        static_cast<float>(m_tx_power_raw) / 4.0f, m_tx_power_raw,
        actual_dbm, actual_raw,
        GetProtocolName(),
        m_last_disconnect_reason, GetDisconnectReasonDesc(m_last_disconnect_reason)
    );

    return (len > 0) ? static_cast<size_t>(len) : 0;
}

} // namespace LinkNexus

