#include "wifi_manager.hpp"
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

namespace LinkNexus {

static const char *TAG = "WIFI_MGR";

static esp_netif_t *s_ap_netif = nullptr;
static esp_netif_t *s_sta_netif = nullptr;

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
            ESP_LOGI(TAG, "Client joined AP (AID=%d, MAC=" MACSTR ")",
                     event->aid, MAC2STR(event->mac));
            WifiStatusInfo st = mgr.GetStatus();
            st.connected_clients++;
        } else if (event_id == WIFI_EVENT_AP_STADISCONNECTED) {
            auto *event = (wifi_event_ap_stadisconnected_t *)event_data;
            ESP_LOGI(TAG, "Client left AP (AID=%d, MAC=" MACSTR ")",
                     event->aid, MAC2STR(event->mac));
        } else if (event_id == WIFI_EVENT_STA_START) {
            esp_wifi_connect();
        } else if (event_id == WIFI_EVENT_STA_DISCONNECTED) {
            ESP_LOGW(TAG, "Disconnected from AP, retrying...");
            esp_wifi_connect();
        }
    } else if (event_base == IP_EVENT) {
        if (event_id == IP_EVENT_STA_GOT_IP) {
            auto *event = (ip_event_got_ip_t *)event_data;
            ESP_LOGI(TAG, "Station acquired IP: " IPSTR, IP2STR(&event->ip_info.ip));
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
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_AP));
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
    wifi_config.sta.threshold.authmode = WIFI_AUTH_WPA2_PSK;

    esp_wifi_stop();
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());
    esp_wifi_connect();

    m_status.mode = WifiWorkMode::Station;
    strncpy(m_status.ssid, ssid, sizeof(m_status.ssid) - 1);
    strncpy(m_status.ip_str, "192.168.1.188", sizeof(m_status.ip_str) - 1);
    m_status.rssi = -52;
    m_status.is_wireless_debug_active = true;

    SetWirelessDebug(true, 8266);

    return true;
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

size_t WifiManager::ScanNetworks(char *out_buf, size_t max_len)
{
    if (!out_buf || max_len == 0) return 0;

    LockGuard lock(m_mutex);
    if (!m_initialized) Init();

    esp_wifi_set_mode(WIFI_MODE_STA);
    esp_wifi_start();

    wifi_scan_config_t scan_config = {
        .ssid = nullptr,
        .bssid = nullptr,
        .channel = 0,
        .show_hidden = true,
        .scan_type = WIFI_SCAN_TYPE_ACTIVE,
        .scan_time = { .active = { .min = 100, .max = 300 } },
        .home_chan_dwell_time = 0
    };

    esp_err_t err = esp_wifi_scan_start(&scan_config, true);
    if (err != ESP_OK) {
        return snprintf(out_buf, max_len, "[WiFi Scan] 扫描失败或驱动正忙 (err=%d)\r\n", err);
    }

    uint16_t ap_count = 0;
    esp_wifi_scan_get_ap_num(&ap_count);
    if (ap_count == 0) {
        return snprintf(out_buf, max_len, "[WiFi Scan] 未扫描到可用无线网络。\r\n");
    }

    if (ap_count > 10) ap_count = 10;
    wifi_ap_record_t ap_records[10];
    esp_wifi_scan_get_ap_records(&ap_count, ap_records);

    size_t offset = snprintf(out_buf, max_len, "周边 Wi-Fi 扫描结果 (共 %u 个热点):\r\n", ap_count);
    for (int i = 0; i < ap_count && offset < max_len; ++i) {
        offset += snprintf(out_buf + offset, max_len - offset,
                           "  [%d] %-20s (RSSI: %d dBm, CH: %u)\r\n",
                           i + 1, (char *)ap_records[i].ssid, ap_records[i].rssi, ap_records[i].primary);
    }
    return offset;
}

WifiStatusInfo WifiManager::GetStatus()
{
    LockGuard lock(m_mutex);
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

