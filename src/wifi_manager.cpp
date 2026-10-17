#include "wifi_manager.hpp"
#include "comm_manager.hpp"
#include "cli_engine.hpp"
#include "linknexus_config.hpp"
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
    m_status.mode = WifiWorkMode::Off;
    strncpy(m_status.ssid, "LinkNexus-ESP32-AP", sizeof(m_status.ssid) - 1);
    strncpy(m_status.ip_str, "--", sizeof(m_status.ip_str) - 1);
    m_status.rssi = 0;
    m_status.channel = 1;
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = false;
    m_status.wireless_debug_port = 8266;
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
            ESP_LOGI(TAG, "Client left SoftAP (AID=%d, MAC=" MACSTR ")",
                     event->aid, MAC2STR(event->mac));
            mgr.DecrementClients();
        } else if (event_id == WIFI_EVENT_STA_START) {
            ESP_LOGI(TAG, "Wi-Fi Station interface started (idle / ready for scan or connect).");
        } else if (event_id == WIFI_EVENT_STA_DISCONNECTED) {
            ESP_LOGW(TAG, "Station disconnected from AP.");
            mgr.OnStaDisconnected();
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

    m_initialized = true;
    ESP_LOGI(TAG, "Wi-Fi subsystem initialized. Ready for AP/STA and wireless CLI.");
}

bool WifiManager::StartAp(const char *ssid, const char *password, uint8_t channel)
{
    LockGuard lock(m_mutex);
    if (!m_initialized) Init();

    if (!ssid || strlen(ssid) == 0) {
        ssid = "LinkNexus-ESP32-AP";
    }

    ESP_LOGI(TAG, "Configuring ESP32-S3 as SoftAP (SSID: '%s', Ch: %u)...", ssid, channel);

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

    ESP_LOGI(TAG, "Connecting to external AP (SSID: '%s')...", ssid);

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
    esp_wifi_connect();

    m_status.mode = WifiWorkMode::Station;
    strncpy(m_status.ssid, ssid, sizeof(m_status.ssid) - 1);
    strncpy(m_status.ip_str, "Connecting...", sizeof(m_status.ip_str) - 1);
    m_status.rssi = 0;
    m_status.connected_clients = 0;
    m_status.is_wireless_debug_active = false;

    return true;
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

void WifiManager::OnStaDisconnected()
{
    LockGuard lock(m_mutex);
    if (m_status.mode == WifiWorkMode::Station) {
        strncpy(m_status.ip_str, "Disconnected", sizeof(m_status.ip_str) - 1);
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

size_t WifiManager::ScanNetworks(char *out_buf, size_t max_len, bool force_rescan)
{
    if (!out_buf || max_len == 0) return 0;

    LockGuard lock(m_mutex);
    if (!m_initialized) Init();

    // 如果已执行过扫描且未指定强制重扫，直接复用已扫描结果（确保仅需执行一次！）
    if (m_has_scanned && !force_rescan && m_cached_scan_len > 0) {
        size_t copy_len = (m_cached_scan_len < max_len - 1) ? m_cached_scan_len : max_len - 1;
        memcpy(out_buf, m_cached_scan_result, copy_len);
        out_buf[copy_len] = '\0';
        ESP_LOGI(TAG, "Returning cached Wi-Fi scan result (executed once, len=%u).", copy_len);
        CommManager::Instance().Printf("[WIFI] 上位机获取 AP 扫描结果 (复用已扫描记录，无需重复扫描)\r\n");
        return copy_len;
    }

    if (m_is_scanning) {
        return snprintf(out_buf, max_len, "[WiFi Scan] 正在扫描射频信道中，请稍候...\r\n");
    }

    m_is_scanning = true;
    ESP_LOGI(TAG, "Starting hardware Wi-Fi channel scan (executing once)...");
    CommManager::Instance().Print("[WIFI] 正在执行真实周边 Wi-Fi 射频信道扫描 (仅需执行一次)...\r\n");

    wifi_mode_t cur_mode = WIFI_MODE_NULL;
    esp_wifi_get_mode(&cur_mode);
    bool restore_ap = false;

    if (cur_mode == WIFI_MODE_AP) {
        esp_wifi_set_mode(WIFI_MODE_APSTA);
        restore_ap = true;
    } else if (cur_mode != WIFI_MODE_APSTA && cur_mode != WIFI_MODE_STA) {
        esp_wifi_set_mode(WIFI_MODE_STA);
        esp_wifi_start();
    }

    wifi_scan_config_t scan_config = {
        .ssid = nullptr,
        .bssid = nullptr,
        .channel = 0,
        .show_hidden = true,
        .scan_type = WIFI_SCAN_TYPE_ACTIVE,
        .scan_time = { .active = { .min = 100, .max = 250 } },
        .home_chan_dwell_time = 0
    };

    esp_err_t err = esp_wifi_scan_start(&scan_config, true);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "esp_wifi_scan_start failed: %d", err);
        if (restore_ap) {
            esp_wifi_set_mode(WIFI_MODE_AP);
        }
        m_is_scanning = false;
        return snprintf(out_buf, max_len, "[WiFi Scan] 扫描失败或 Wi-Fi 驱动正忙 (错误代码: %d)\r\n", err);
    }

    uint16_t ap_count = 0;
    esp_wifi_scan_get_ap_num(&ap_count);
    if (ap_count == 0) {
        if (restore_ap) {
            esp_wifi_set_mode(WIFI_MODE_AP);
        }
        m_is_scanning = false;
        m_has_scanned = true;
        size_t len = snprintf(out_buf, max_len, "[WiFi Scan] 扫描完成: 周围未发现可用的 2.4GHz Wi-Fi 热点。\r\n");
        m_cached_scan_len = (len < sizeof(m_cached_scan_result) - 1) ? len : sizeof(m_cached_scan_result) - 1;
        memcpy(m_cached_scan_result, out_buf, m_cached_scan_len);
        m_cached_scan_result[m_cached_scan_len] = '\0';
        CommManager::Instance().Print(out_buf);
        return len;
    }

    uint16_t records_to_get = (ap_count > 16) ? 16 : ap_count;
    wifi_ap_record_t ap_records[16];
    esp_wifi_scan_get_ap_records(&records_to_get, ap_records);

    std::sort(ap_records, ap_records + records_to_get, [](const wifi_ap_record_t &a, const wifi_ap_record_t &b) {
        return a.rssi > b.rssi;
    });

    size_t offset = snprintf(out_buf, max_len,
        "================ 周边真实 Wi-Fi 热点扫描结果 ================\r\n"
        "  序号  SSID                      RSSI     信道  加密认证\r\n"
        "----------------------------------------------------------\r\n");

    for (uint16_t i = 0; i < records_to_get && offset < max_len - 80; ++i) {
        const char *auth_str = AuthModeToString(ap_records[i].authmode);
        char ssid_str[33];
        strncpy(ssid_str, (const char *)ap_records[i].ssid, sizeof(ssid_str) - 1);
        ssid_str[sizeof(ssid_str) - 1] = '\0';
        if (strlen(ssid_str) == 0) {
            strcpy(ssid_str, "<Hidden-SSID>");
        }

        offset += snprintf(out_buf + offset, max_len - offset,
                           "  [%02u] %-24s  %4d dBm  CH%2u  %-10s\r\n",
                           i + 1, ssid_str, ap_records[i].rssi, ap_records[i].primary, auth_str);
    }

    offset += snprintf(out_buf + offset, max_len - offset,
                       "----------------------------------------------------------\r\n"
                       "  扫描完成: 检索到 %u 个真实无线网络 (显示前 %u 个)\r\n"
                       "==========================================================\r\n",
                       ap_count, records_to_get);

    if (restore_ap) {
        esp_wifi_set_mode(WIFI_MODE_AP);
    }

    m_cached_scan_len = (offset < sizeof(m_cached_scan_result) - 1) ? offset : sizeof(m_cached_scan_result) - 1;
    memcpy(m_cached_scan_result, out_buf, m_cached_scan_len);
    m_cached_scan_result[m_cached_scan_len] = '\0';
    m_has_scanned = true;
    m_is_scanning = false;

    // 串口打印AP扫描结果
    CommManager::Instance().Print(out_buf);

    return offset;
}

void WifiManager::InitialScanTaskEntry(void *pvParameters)
{
    (void)pvParameters;
    vTaskDelay(pdMS_TO_TICKS(1000)); // 等待系统各子系统及串口初始化完全就绪

    // 1. 开机默认启动 SoftAP (独立主热点)，使手机/电脑能在 Wi-Fi 列表立即搜索并连接
    WifiManager::Instance().StartAp("LinkNexus-ESP32-AP", "12345678", 1);
    CommManager::Instance().Print("\r\n[WIFI] 开机默认 SoftAP 热点已启动: 'LinkNexus-ESP32-AP' (密码: 12345678, IP: 192.168.4.1)\r\n");

    vTaskDelay(pdMS_TO_TICKS(500));

    // 2. 后台执行一次周边真实 2.4GHz Wi-Fi 射频信道扫描并缓存 (采用堆动态分配，严防任务栈溢出)
    char *scan_buf = static_cast<char*>(malloc(1536));
    if (scan_buf) {
        ESP_LOGI(TAG, "Starting system initial one-time background Wi-Fi scan...");
        CommManager::Instance().Print("[WIFI] 正在执行系统开机初始周边 Wi-Fi 射频扫描 (仅需执行一次)...\r\n");
        WifiManager::Instance().ScanNetworks(scan_buf, 1536, true);
        free(scan_buf);
    }
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
    if (m_status.mode == WifiWorkMode::Station && strcmp(m_status.ip_str, "--") != 0 && strcmp(m_status.ip_str, "Disconnected") != 0 && strcmp(m_status.ip_str, "Connecting...") != 0) {
        int rssi = 0;
        if (esp_wifi_sta_get_rssi(&rssi) == ESP_OK) {
            m_status.rssi = static_cast<int8_t>(rssi);
        }
    }
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

} // namespace LinkNexus

