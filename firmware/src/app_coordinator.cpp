#include "app_coordinator.hpp"
#include "config_manager.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

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
    ESP_LOGI(TAG, "  Architecture     : FreeRTOS SMP Dual-Core (C++17 Engine)");
    ESP_LOGI(TAG, "==========================================================");

    // 0. 初始化配置中心并从 NVS 闪存加载所有参数
    ConfigManager::Instance().Init();

    // 1. 初始化供电管理模块 (6路 MOS)
    PowerManager::Instance().Init();

    // 2. 初始化高精度遥测模块 (INA236 I2C)
    TelemetryManager::Instance().Init();

    // 3. 初始化显示与 UI 管理模块 (ST7789 & LVGL 架构)
    DisplayManager::Instance().Init();

    // 4. 初始化全双工通信硬件层 (UART0)
    CommManager::Instance().Init();

    // 5. 初始化 Wi-Fi 与无线调试底层
    WifiManager::Instance().Init();

    // 6. 初始化基于 esp_console 的原生 CLI 引擎
    CliEngine::Instance().Init();

    // 若 NVS 中配置了开机默认开启遥测广播流，则应用该配置
    if (ConfigManager::Instance().GetTelemetryStream()) {
        SetManualTelemetryStream(true);
    }

    m_initialized = true;
    ESP_LOGI(TAG, "Subsystems initialized. Ready to orchestrate dual-core tasks.");
}

void AppCoordinator::OnFrameReceived(uint8_t cmd, const uint8_t *payload, uint8_t len)
{
    // 收到合法二进制协议帧，立刻标记上位机链路活跃 (保持 250ms 遥测上报流)
    TouchHostActivity();

    uint8_t tx_buf[512];
    size_t tx_len = 0;

    switch (static_cast<ProtocolCmd>(cmd)) {
        case ProtocolCmd::Heartbeat: {
            uint8_t seq = (len > 0) ? payload[0] : 0;
            ESP_LOGI(TAG, "Heartbeat ping received (Seq: 0x%02X), returning ACK with MAC %s.", seq, LINKNEXUS_DEFAULT_MAC);
            tx_len = ProtocolUtils::PackHeartbeatAck(seq, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            break;
        }

        case ProtocolCmd::TelemetryReport: {
            CommManager::Instance().Printf("[上位机指令] 收到 0x10 遥测上报即时刷新请求\r\n");
            TelemetryPayload telemetry;
            TelemetryManager::Instance().ReadTelemetry(telemetry, PowerManager::Instance());
            tx_len = ProtocolUtils::PackTelemetry(telemetry, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            break;
        }

        case ProtocolCmd::ScreenSync: {
            ScreenSyncInfo sync_info;
            if (ProtocolUtils::ParseScreenSync(payload, len, sync_info)) {
                CommManager::Instance().Printf("[上位机指令] 收到 0x20 屏幕同步 -> 通道: #%u, 烧录: %u%%, 设备: '%s', 正在烧录: %s\r\n",
                                               sync_info.active_channel, sync_info.flash_progress, sync_info.device_name,
                                               (sync_info.status_flags & 0x01) ? "是" : "否");
                DisplayManager::Instance().UpdateState(sync_info);
            }
            break;
        }

        case ProtocolCmd::PowerControl: {
            if (len >= 2) {
                uint8_t channel = payload[0];
                bool enable = (payload[1] != 0x00);
                CommManager::Instance().Printf("[上位机指令] 收到 0x30 供电控制 -> 通道 #%u MOS 切换为: %s\r\n",
                                               channel, enable ? "导通 (ON)" : "隔离 (OFF)");
                bool ok = PowerManager::Instance().SetPower(channel, enable);
                if (ok) {
                    tx_len = ProtocolUtils::PackPowerAck(channel, enable, tx_buf);
                    CommManager::Instance().SendRawFrame(tx_buf, tx_len);
                }
            }
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

                if (action == 1) { // 启动 SoftAP (主动 AP)
                    if (strlen(ssid) == 0) strcpy(ssid, "LinkNexus-ESP32-AP");
                    if (strlen(pwd) == 0) strcpy(pwd, "12345678");
                    WifiManager::Instance().StartAp(ssid, pwd, channel);
                } else if (action == 2) { // 启动 Station (从 AP)
                    if (strlen(ssid) > 0) {
                        WifiManager::Instance().ConnectSta(ssid, pwd);
                    }
                } else if (action == 3) { // 断开 Wi-Fi
                    WifiManager::Instance().Disconnect();
                } else if (action == 4) { // 扫描周边真实 Wi-Fi 热点 (仅需执行一次与复用缓存)
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

                // 立即回复上位机当前 0x40 状态响应包 (完全对齐 ParseWifiStatusPayload)
                WifiStatusInfo st = WifiManager::Instance().GetStatus();
                tx_len = ProtocolUtils::PackWifiStatus(static_cast<uint8_t>(st.mode), st.channel, st.rssi,
                                                       st.connected_clients, st.wireless_debug_port,
                                                       st.ssid, st.ip_str, tx_buf);
                CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            }
            break;
        }

        case ProtocolCmd::CliTransfer: {
            // 0x50: 交互式 CLI 命令与应答 [Payload: 指令字符串]
            if (len > 0) {
                char cmd_str[256];
                uint8_t copy_len = (len < sizeof(cmd_str) - 1) ? len : sizeof(cmd_str) - 1;
                memcpy(cmd_str, payload, copy_len);
                cmd_str[copy_len] = '\0';

                CommManager::Instance().Printf("[上位机指令] 收到 0x50 CLI交互传输 -> 执行命令: \"%s\"\r\n", cmd_str);

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
            ESP_LOGW(TAG, "Received unknown command code: 0x%02X (Len: %u)", cmd, len);
            break;
    }
}

void AppCoordinator::ExecuteSerialCli(const char *cmd)
{
    if (!cmd || strlen(cmd) == 0) return;

    // 静态输出缓冲区，彻底避免在任务栈上分配 1.5KB 数组引发爆栈崩溃
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

    ESP_LOGI(TAG, "CommRxTask running on Core %d (Dual-Channel: UART0 & USB-Serial-JTAG).", xPortGetCoreID());

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

                            // 终端字符本地回显 (Local Echo - 由 CommManager 统一发送，杜绝与 stdout 重复)
                            char echo[2] = { static_cast<char>(b), '\0' };
                            CommManager::Instance().Print(echo);
                        }
                    }
                }
            }
        } else {
            // 超时自动提交执行机制 (防止部分串口工具/ESP Decoder 发送时未勾选“加回车换行符”)
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
    if (m_manual_telemetry_stream.load()) {
        return true;
    }
    uint32_t last_tick = m_last_host_activity_tick.load();
    if (last_tick == 0) return false;
    // 上位机每 1000ms 发送一次心跳，若超过 3500ms 未收到任何协议包，则判定上位机离线
    return (xTaskGetTickCount() - last_tick) < pdMS_TO_TICKS(3500);
}

void AppCoordinator::TouchHostActivity()
{
    bool was_active = IsHostActive();
    m_last_host_activity_tick.store(xTaskGetTickCount());
    if (!was_active) {
        ESP_LOGI(TAG, "LinkNexus C# Host connected. Activated 250ms telemetry binary stream.");
    }
}

void AppCoordinator::SetManualTelemetryStream(bool enable)
{
    m_manual_telemetry_stream.store(enable);
    ESP_LOGI(TAG, "Manual telemetry stream set to: %s", enable ? "ON" : "OFF");
}

bool AppCoordinator::IsManualTelemetryStream() const
{
    return m_manual_telemetry_stream.load();
}

uint32_t AppCoordinator::GetLastHostActivityTick() const
{
    return m_last_host_activity_tick.load();
}

void AppCoordinator::TelemetryTaskEntry(void *pvParameters)
{
    auto *self = static_cast<AppCoordinator*>(pvParameters);
    uint8_t tx_buf[32];
    TelemetryPayload telemetry;
    ESP_LOGI(TAG, "TelemetryTask running on Core %d (Priority: 5, Period: 250ms).", xPortGetCoreID());

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(250));

        // 仅在上位机处于活跃握手状态 (已收到心跳且未超时) 或 开发者开启手动遥测流时才发送二进制帧
        // 彻底杜绝纯文本终端 (ESP Decoder) 刷屏乱码，并节约双核 CPU 与总线资源
        if (self && self->IsHostActive()) {
            TelemetryManager::Instance().ReadTelemetry(telemetry, PowerManager::Instance());
            size_t len = ProtocolUtils::PackTelemetry(telemetry, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, len);
        }
    }
}

void AppCoordinator::UiTaskEntry(void *pvParameters)
{
    (void)pvParameters;
    ESP_LOGI(TAG, "UiTask running on Core %d (Priority: 4).", xPortGetCoreID());

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(50));
        DisplayManager::Instance().ProcessUi();
    }
}

void AppCoordinator::Start()
{
    if (m_started) return;
    if (!m_initialized) {
        Init();
    }

    ESP_LOGI(TAG, "Pinning tasks across ESP32-S3 dual cores...");

    // 1. Core 1 专用于高速协议解析与串口收发 (实时性要求极高)
    xTaskCreatePinnedToCore(CommRxTaskEntry,
                            "comm_rx",
                            8192,
                            this,
                            10,
                            nullptr,
                            Cores::COMMUNICATION_CORE);

    // 2. Core 0 专用于传感器 I2C 遥测采集 (250ms 周期，仅在握手在线时上报)
    xTaskCreatePinnedToCore(TelemetryTaskEntry,
                            "telemetry",
                            4096,
                            this,
                            5,
                            nullptr,
                            Cores::SYSTEM_CORE);

    // 3. Core 0 运行 UI 刷新与 LVGL 任务循环
    xTaskCreatePinnedToCore(UiTaskEntry,
                            "ui_task",
                            4096,
                            nullptr,
                            4,
                            nullptr,
                            Cores::SYSTEM_CORE);

    // 4. Core 0 启动开机初始 Wi-Fi 扫描任务 (后台单次执行并串口打印 AP 列表，彻底避免阻塞)
    WifiManager::Instance().StartInitialScanTask();

    m_started = true;
    ESP_LOGI(TAG, "All dual-core tasks spawned and running smoothly.");

    // 5. 双核编排完成：向串口与控制台输出系统就绪通知 (保证文本终端绝对干净整洁)
    const char *ready_msg = "[LINKNEXUS] SYSTEM_READY: ESP32-S3 Dual-Core Orchestrator Initialized (MAC: " LINKNEXUS_DEFAULT_MAC ").\r\n";
    CommManager::Instance().Print(ready_msg);

    // 5.1 若上位机此时已连接，则补发二进制心跳响应与 CLI 激活通知
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

