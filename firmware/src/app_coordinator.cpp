#include "app_coordinator.hpp"
#include "config_manager.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>

namespace LinkNexus {

static const char *TAG = "APP_COORD";

AppCoordinator& AppCoordinator::Instance()
{
    static AppCoordinator instance;
    return instance;
}

AppCoordinator::AppCoordinator()
{
    m_parser.SetHandler([this](uint8_t cmd, const uint8_t *payload, uint8_t len) {
        OnFrameReceived(cmd, payload, len);
    });
}

void AppCoordinator::Init()
{
    if (m_initialized) return;

    ESP_LOGI(TAG, "==========================================================");
    ESP_LOGI(TAG, "  LinkNexus Dual-Core Firmware [ESP32-S3 Core]");
    ESP_LOGI(TAG, "  Baseline Version : %s", LINKNEXUS_FIRMWARE_VERSION);
    ESP_LOGI(TAG, "  Hardware Model   : %s", LINKNEXUS_HARDWARE_MODEL);
    ESP_LOGI(TAG, "  Modules          : USB CDC Serial | CLI Engine | Wi-Fi");
    ESP_LOGI(TAG, "==========================================================");

    // 0. 初始化配置中心并从 NVS 闪存加载 Wi-Fi 参数
    ConfigManager::Instance().Init();

    // 1. 初始化全双工通信硬件层 (UART0 与原生 USB-Serial-JTAG CDC 双通道)
    CommManager::Instance().Init();

    // 2. 初始化 Wi-Fi 与无线调试底层
    WifiManager::Instance().Init();

    // 3. 初始化基于 esp_console 的原生 CLI 引擎
    CliEngine::Instance().Init();

    m_initialized = true;
    ESP_LOGI(TAG, "Subsystems initialized. Ready to orchestrate communication tasks.");
}

void AppCoordinator::OnFrameReceived(uint8_t cmd, const uint8_t *payload, uint8_t len)
{
    bool was_active = IsHostActive();
    TouchHostActivity();

    uint8_t tx_buf[512];
    size_t tx_len = 0;

    switch (static_cast<ProtocolCmd>(cmd)) {
        case ProtocolCmd::Heartbeat: {
            uint8_t seq = (len > 0) ? payload[0] : 0;
            if (!was_active) {
                ESP_LOGI(TAG, "Host connected (Heartbeat ACK sent, MAC: %s).", LINKNEXUS_DEFAULT_MAC);
            }
            tx_len = ProtocolUtils::PackHeartbeatAck(seq, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            break;
        }

        case ProtocolCmd::WifiControl: {
            // 0x40: Wi-Fi 控制帧 [Action(1B), Channel(1B), SsidLen(1B), Ssid(NB), PwdLen(1B), Pwd(NB)]
            if (len >= 1) {
                uint8_t action = payload[0];
                uint8_t channel = (len > 1 && payload[1] >= 1 && payload[1] <= 13) ? payload[1] : 1;
                char ssid[33] = {0};
                char pwd[65] = {0};

                if (len >= 3) {
                    uint8_t ssid_len = payload[2];
                    if (ssid_len > 32) ssid_len = 32;
                    if (len >= 3 + ssid_len) {
                        memcpy(ssid, &payload[3], ssid_len);
                        ssid[ssid_len] = '\0';

                        size_t pwd_len_idx = 3 + ssid_len;
                        if (len > pwd_len_idx) {
                            uint8_t pwd_len = payload[pwd_len_idx];
                            if (pwd_len > 64) pwd_len = 64;
                            if (len >= pwd_len_idx + 1 + pwd_len) {
                                memcpy(pwd, &payload[pwd_len_idx + 1], pwd_len);
                                pwd[pwd_len] = '\0';
                            }
                        }
                    }
                }

                const char *act_desc = (action == 0) ? "查询状态" :
                                       (action == 1) ? "启动SoftAP" :
                                       (action == 2) ? "连接Station" :
                                       (action == 3) ? "断开Wi-Fi" :
                                       (action == 4) ? "扫描周边热点" : "未知动作";
                CommManager::Instance().Printf("[上位机指令] 收到 0x40 Wi-Fi控制 -> 动作: 0x%02X (%s), 信道: %u, SSID: '%s'\r\n",
                                               action, act_desc, channel, ssid);

                if (action == 1) {
                    if (strlen(ssid) == 0) strcpy(ssid, "LinkNexus-ESP32-AP");
                    if (strlen(pwd) == 0) strcpy(pwd, "12345678");
                    WifiManager::Instance().StartAp(ssid, pwd, channel);
                } else if (action == 2) {
                    if (strlen(ssid) > 0) {
                        WifiManager::Instance().ConnectSta(ssid, pwd);
                    }
                } else if (action == 3) {
                    WifiManager::Instance().Disconnect();
                } else if (action == 4) {
                    char scan_buf[1536];
                    size_t scan_len = WifiManager::Instance().ScanNetworks(scan_buf, sizeof(scan_buf));
                    if (scan_len > 0) {
                        size_t sent_bytes = 0;
                        while (sent_bytes < scan_len) {
                            size_t chunk = scan_len - sent_bytes;
                            if (chunk > 240) chunk = 240;
                            tx_len = ProtocolUtils::PackFrame(static_cast<uint8_t>(ProtocolCmd::CliTransfer),
                                                              reinterpret_cast<const uint8_t*>(scan_buf + sent_bytes),
                                                              static_cast<uint8_t>(chunk),
                                                              tx_buf);
                            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
                            sent_bytes += chunk;
                            if (sent_bytes < scan_len) {
                                vTaskDelay(pdMS_TO_TICKS(10));
                            }
                        }
                    }
                }

                WifiStatusInfo st = WifiManager::Instance().GetStatus();
                tx_len = ProtocolUtils::PackWifiStatus(static_cast<uint8_t>(st.mode), st.channel, st.rssi,
                                                       st.connected_clients, st.wireless_debug_port,
                                                       st.ssid, st.ip_str, tx_buf);
                CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            }
            break;
        }

        case ProtocolCmd::CliTransfer: {
            if (len > 0) {
                char cmd_str[256];
                uint8_t copy_len = (len < sizeof(cmd_str) - 1) ? len : sizeof(cmd_str) - 1;
                memcpy(cmd_str, payload, copy_len);
                cmd_str[copy_len] = '\0';

                CommManager::Instance().Printf("[CLI] 执行: \"%s\"\r\n", cmd_str);

                char response_str[1536];
                size_t resp_len = CliEngine::Instance().Execute(cmd_str, response_str, sizeof(response_str));
                if (resp_len > 0) {
                    size_t sent_bytes = 0;
                    while (sent_bytes < resp_len) {
                        size_t chunk = resp_len - sent_bytes;
                        if (chunk > 240) chunk = 240;
                        tx_len = ProtocolUtils::PackFrame(static_cast<uint8_t>(ProtocolCmd::CliTransfer),
                                                          reinterpret_cast<const uint8_t*>(response_str + sent_bytes),
                                                          static_cast<uint8_t>(chunk),
                                                          tx_buf);
                        CommManager::Instance().SendRawFrame(tx_buf, tx_len);
                        sent_bytes += chunk;
                        if (sent_bytes < resp_len) {
                            vTaskDelay(pdMS_TO_TICKS(10));
                        }
                    }
                }
            }
            break;
        }

        default:
            ESP_LOGW(TAG, "Received unhandled command: 0x%02X (Len: %u)", cmd, len);
            break;
    }
}

void AppCoordinator::ExecuteSerialCli(const char *cmd)
{
    if (!cmd || strlen(cmd) == 0) return;

    static char s_cli_response_str[2048];

    char header_buf[128];
    snprintf(header_buf, sizeof(header_buf), "\r\n[串口控制台] 收到指令: \"%s\"\r\n", cmd);
    CommManager::Instance().Print(header_buf);

    size_t resp_len = CliEngine::Instance().Execute(cmd, s_cli_response_str, sizeof(s_cli_response_str) - 4);
    if (resp_len > 0) {
        if (resp_len < sizeof(s_cli_response_str) - 2 && s_cli_response_str[resp_len - 1] != '\n') {
            s_cli_response_str[resp_len++] = '\r';
            s_cli_response_str[resp_len++] = '\n';
            s_cli_response_str[resp_len] = '\0';
        }
        CommManager::Instance().Print(s_cli_response_str);
    }
    CommManager::Instance().Print("\r\n> ");
}

void AppCoordinator::CommRxTaskEntry(void *pvParameters)
{
    auto *self = static_cast<AppCoordinator*>(pvParameters);
    uint8_t rx_buffer[256];
    char cli_line[256];
    size_t cli_len = 0;
    TickType_t last_char_tick = 0;

    ESP_LOGI(TAG, "CommRxTask running on Core %d (Dual-Channel: UART0 & USB-Serial-JTAG CDC).", xPortGetCoreID());

    while (1) {
        int bytes_read = CommManager::Instance().ReadBytes(rx_buffer, sizeof(rx_buffer), pdMS_TO_TICKS(20));
        if (bytes_read > 0) {
            for (int i = 0; i < bytes_read; ++i) {
                uint8_t b = rx_buffer[i];

                if (self->m_parser.IsParsingFrame()) {
                    bool consumed = self->m_parser.FeedByte(b);
                    if (!consumed && (b >= 32 && b <= 126)) {
                        if (cli_len < sizeof(cli_line) - 1) {
                            cli_line[cli_len++] = static_cast<char>(b);
                            last_char_tick = xTaskGetTickCount();
                        }
                    } else if (consumed) {
                        cli_len = 0;
                        last_char_tick = 0;
                    }
                } else if (b == PROTOCOL_HEADER1) {
                    self->m_parser.FeedByte(b);
                } else {
                    if (b == '\r' || b == '\n') {
                        if (cli_len > 0) {
                            cli_line[cli_len] = '\0';
                            self->ExecuteSerialCli(cli_line);
                            cli_len = 0;
                            last_char_tick = 0;
                        } else {
                            CommManager::Instance().Print("\r\n> ");
                        }
                    } else if (b == '\b' || b == 0x7F) {
                        if (cli_len > 0) {
                            cli_len--;
                            CommManager::Instance().Print("\b \b");
                        }
                    } else if (b >= 32 && b <= 126) {
                        if (cli_len < sizeof(cli_line) - 1) {
                            cli_line[cli_len++] = static_cast<char>(b);
                            last_char_tick = xTaskGetTickCount();

                            char echo[2] = { static_cast<char>(b), '\0' };
                            CommManager::Instance().Print(echo);
                        }
                    }
                }
            }
        } else {
            if (cli_len >= 3 && last_char_tick > 0 && (xTaskGetTickCount() - last_char_tick > pdMS_TO_TICKS(800))) {
                cli_line[cli_len] = '\0';
                self->ExecuteSerialCli(cli_line);
                cli_len = 0;
                last_char_tick = 0;
            }
        }
    }
}

bool AppCoordinator::IsHostActive() const
{
    uint32_t last_tick = m_last_host_activity_tick.load();
    if (last_tick == 0) return false;
    return (xTaskGetTickCount() - last_tick) < pdMS_TO_TICKS(3500);
}

void AppCoordinator::TouchHostActivity()
{
    bool was_active = IsHostActive();
    m_last_host_activity_tick.store(xTaskGetTickCount());
    if (!was_active) {
        ESP_LOGI(TAG, "LinkNexus C# Host connected.");
    }
}

uint32_t AppCoordinator::GetLastHostActivityTick() const
{
    return m_last_host_activity_tick.load();
}

void AppCoordinator::Start()
{
    if (m_started) return;
    if (!m_initialized) {
        Init();
    }

    ESP_LOGI(TAG, "Spawning communications and Wi-Fi services...");

    // 1. Core 1 专用于高速协议解析与串口收发 (实时性要求极高)
    xTaskCreatePinnedToCore(CommRxTaskEntry,
                            "comm_rx",
                            8192,
                            this,
                            10,
                            nullptr,
                            Cores::COMMUNICATION_CORE);

    // 2. Core 0 启动开机初始 Wi-Fi 扫描任务 (后台单次执行并串口打印 AP 列表)
    WifiManager::Instance().StartInitialScanTask();

    m_started = true;
    ESP_LOGI(TAG, "Communication tasks spawned and running smoothly.");

    // 3. 输出系统就绪通知
    const char *ready_msg = "[LINKNEXUS] SYSTEM_READY (MAC: " LINKNEXUS_DEFAULT_MAC ").\r\n";
    CommManager::Instance().Print(ready_msg);

    if (IsHostActive()) {
        uint8_t ready_frame[16];
        size_t ready_len = ProtocolUtils::PackHeartbeatAck(0x00, ready_frame);
        CommManager::Instance().SendRawFrame(ready_frame, ready_len);

        uint8_t cli_frame[128];
        size_t cli_len = ProtocolUtils::PackFrame(static_cast<uint8_t>(ProtocolCmd::CliTransfer),
                                                  reinterpret_cast<const uint8_t*>(ready_msg),
                                                  strlen(ready_msg),
                                                  cli_frame);
        CommManager::Instance().SendRawFrame(cli_frame, cli_len);
    }
}

} // namespace LinkNexus
