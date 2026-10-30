#include "config_manager.hpp"
#include "wifi_manager.hpp"
#include "nvs_flash.h"
#include "nvs.h"
#include "esp_log.h"
#include <string.h>
#include <stdio.h>

namespace LinkNexus {

static const char *TAG = "CONFIG_MGR";
static const char *NVS_NAMESPACE = "ln_config";

ConfigManager& ConfigManager::Instance()
{
    static ConfigManager instance;
    return instance;
}

ConfigManager::ConfigManager()
{
}

void ConfigManager::Init()
{
    if (m_initialized) return;

    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_LOGW(TAG, "NVS flash partition was truncated/corrupted. Erasing...");
        nvs_flash_erase();
        err = nvs_flash_init();
    }
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "Failed to initialize NVS flash: %s", esp_err_to_name(err));
        return;
    }

    nvs_handle_t handle;
    err = nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle);
    if (err == ESP_OK) {
        int8_t tx_pwr = 0;
        if (nvs_get_i8(handle, "tx_pwr", &tx_pwr) == ESP_OK) {
            m_tx_power_raw = tx_pwr;
        }

        uint8_t proto = 0;
        if (nvs_get_u8(handle, "proto", &proto) == ESP_OK) {
            m_protocol = proto;
        }

        uint8_t chan = 0;
        if (nvs_get_u8(handle, "chan", &chan) == ESP_OK) {
            m_channel = chan;
        }

        size_t len = sizeof(m_ap_ssid);
        nvs_get_str(handle, "ap_ssid", m_ap_ssid, &len);

        len = sizeof(m_ap_pwd);
        nvs_get_str(handle, "ap_pwd", m_ap_pwd, &len);

        uint8_t ap_ch = 0;
        if (nvs_get_u8(handle, "ap_chan", &ap_ch) == ESP_OK) {
            m_ap_chan = ap_ch;
        }

        len = sizeof(m_sta_ssid);
        nvs_get_str(handle, "sta_ssid", m_sta_ssid, &len);

        len = sizeof(m_sta_pwd);
        nvs_get_str(handle, "sta_pwd", m_sta_pwd, &len);

        uint8_t sta_auto = 0;
        if (nvs_get_u8(handle, "sta_auto", &sta_auto) == ESP_OK) {
            m_sta_auto = (sta_auto != 0);
        }

        nvs_close(handle);
        ESP_LOGI(TAG, "Loaded Wi-Fi config from NVS: tx_pwr=%d (%.2f dBm), proto=%u, chan=%u",
                 m_tx_power_raw, static_cast<float>(m_tx_power_raw) / 4.0f, m_protocol, m_channel);
    } else {
        ESP_LOGI(TAG, "No existing NVS namespace found. Initialized with factory safe defaults.");
    }

    m_initialized = true;
}

bool ConfigManager::SaveTxPowerRaw(int8_t val)
{
    LockGuard lock(m_mutex);
    m_tx_power_raw = val;
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_i8(handle, "tx_pwr", val);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed tx_pwr = %d (%.2f dBm)", val, static_cast<float>(val) / 4.0f);
    return (err == ESP_OK);
}

bool ConfigManager::SaveProtocol(uint8_t mask)
{
    LockGuard lock(m_mutex);
    m_protocol = mask;
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_u8(handle, "proto", mask);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed protocol mask = %u", mask);
    return (err == ESP_OK);
}

bool ConfigManager::SaveChannel(uint8_t ch)
{
    LockGuard lock(m_mutex);
    m_channel = ch;
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_u8(handle, "chan", ch);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed channel = %u", ch);
    return (err == ESP_OK);
}

bool ConfigManager::SaveSoftAp(const char *ssid, const char *pwd, uint8_t ch)
{
    if (!ssid || strlen(ssid) == 0) return false;
    LockGuard lock(m_mutex);
    strncpy(m_ap_ssid, ssid, sizeof(m_ap_ssid) - 1);
    m_ap_ssid[sizeof(m_ap_ssid) - 1] = '\0';
    if (pwd) {
        strncpy(m_ap_pwd, pwd, sizeof(m_ap_pwd) - 1);
        m_ap_pwd[sizeof(m_ap_pwd) - 1] = '\0';
    }
    m_ap_chan = ch;

    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_str(handle, "ap_ssid", m_ap_ssid);
    nvs_set_str(handle, "ap_pwd", m_ap_pwd);
    nvs_set_u8(handle, "ap_chan", m_ap_chan);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed SoftAP config: SSID='%s', chan=%u", m_ap_ssid, m_ap_chan);
    return (err == ESP_OK);
}

bool ConfigManager::SaveSta(const char *ssid, const char *pwd, bool auto_conn)
{
    if (!ssid || strlen(ssid) == 0) return false;
    LockGuard lock(m_mutex);
    strncpy(m_sta_ssid, ssid, sizeof(m_sta_ssid) - 1);
    m_sta_ssid[sizeof(m_sta_ssid) - 1] = '\0';
    if (pwd) {
        strncpy(m_sta_pwd, pwd, sizeof(m_sta_pwd) - 1);
        m_sta_pwd[sizeof(m_sta_pwd) - 1] = '\0';
    }
    m_sta_auto = auto_conn;

    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_str(handle, "sta_ssid", m_sta_ssid);
    nvs_set_str(handle, "sta_pwd", m_sta_pwd);
    nvs_set_u8(handle, "sta_auto", m_sta_auto ? 1 : 0);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed Station config: SSID='%s', auto=%s", m_sta_ssid, m_sta_auto ? "true" : "false");
    return (err == ESP_OK);
}

bool ConfigManager::SaveAllCurrent()
{
    auto &wifi = WifiManager::Instance();

    int8_t tx_pwr = wifi.GetTxPowerRaw();
    uint8_t proto = wifi.GetProtocol();
    uint8_t chan  = wifi.GetChannel();

    LockGuard lock(m_mutex);
    m_tx_power_raw = tx_pwr;
    m_protocol = proto;
    m_channel = chan;

    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) != ESP_OK) return false;
    nvs_set_i8(handle, "tx_pwr", m_tx_power_raw);
    nvs_set_u8(handle, "proto", m_protocol);
    nvs_set_u8(handle, "chan", m_channel);
    nvs_set_str(handle, "ap_ssid", m_ap_ssid);
    nvs_set_str(handle, "ap_pwd", m_ap_pwd);
    nvs_set_u8(handle, "ap_chan", m_ap_chan);
    esp_err_t err = nvs_commit(handle);
    nvs_close(handle);
    ESP_LOGI(TAG, "NVS committed all current Wi-Fi configurations.");
    return (err == ESP_OK);
}

bool ConfigManager::ResetToDefaults()
{
    LockGuard lock(m_mutex);
    nvs_handle_t handle;
    if (nvs_open(NVS_NAMESPACE, NVS_READWRITE, &handle) == ESP_OK) {
        nvs_erase_all(handle);
        nvs_commit(handle);
        nvs_close(handle);
    }
    m_tx_power_raw = RfConfig::DEFAULT_MAX_TX_POWER_RAW;
    m_protocol = 7;
    m_channel = RfConfig::DEFAULT_CHANNEL;
    strncpy(m_ap_ssid, "LinkNexus-ESP32-AP", sizeof(m_ap_ssid) - 1);
    strncpy(m_ap_pwd, "12345678", sizeof(m_ap_pwd) - 1);
    m_ap_chan = 1;
    m_sta_ssid[0] = '\0';
    m_sta_pwd[0] = '\0';
    m_sta_auto = false;
    ESP_LOGI(TAG, "NVS namespace erased. Reset to factory safe defaults.");
    return true;
}

size_t ConfigManager::FormatConfigSummary(char *out_buf, size_t max_len)
{
    if (!out_buf || max_len == 0) return 0;
    LockGuard lock(m_mutex);

    return snprintf(out_buf, max_len,
        "================ LinkNexus NVS 持久化参数看板 ================\r\n"
        "  最大发射功率   : %.2f dBm (raw: %d, 范围: 8~20 dBm)\r\n"
        "  默认协议模式   : %s (掩码: %u)\r\n"
        "  默认工作信道   : CH%u (%u MHz)\r\n"
        "  SoftAP 热点    : SSID='%s', 密码='%s', 信道=CH%u\r\n"
        "  目标 Station   : SSID='%s', 开机自动回连=%s\r\n"
        "  说明           : 执行命令附加 'save' 后缀即可直接固化至 NVS\r\n"
        "================================================================\r\n",
        static_cast<float>(m_tx_power_raw) / 4.0f, m_tx_power_raw,
        (m_protocol == 1) ? "802.11b (CCK)" : (m_protocol == 3) ? "802.11b/g" : "802.11b/g/n", m_protocol,
        m_channel, WifiManager::GetChannelFreqMhz(m_channel),
        m_ap_ssid, m_ap_pwd, m_ap_chan,
        m_sta_ssid[0] ? m_sta_ssid : "(未配置)", m_sta_auto ? "true (开启)" : "false (关闭)");
}

} // namespace LinkNexus
