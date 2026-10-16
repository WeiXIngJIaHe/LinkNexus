#include "app_coordinator.hpp"
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

    m_initialized = true;
    ESP_LOGI(TAG, "Subsystems initialized. Ready to orchestrate dual-core tasks.");
}

void AppCoordinator::OnFrameReceived(uint8_t cmd, const uint8_t *payload, uint8_t len)
{
    uint8_t tx_buf[512];
    size_t tx_len = 0;

    switch (static_cast<ProtocolCmd>(cmd)) {
        case ProtocolCmd::Heartbeat: {
            uint8_t seq = (len > 0) ? payload[0] : 0;
            ESP_LOGD(TAG, "[RX] Heartbeat packet received (Seq: 0x%02X)", seq);
            tx_len = ProtocolUtils::PackHeartbeatAck(seq, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            break;
        }

        case ProtocolCmd::TelemetryReport: {
            // 上位机主动下发 0x10 请求即时刷新
            TelemetryPayload telemetry;
            TelemetryManager::Instance().ReadTelemetry(telemetry, PowerManager::Instance());
            tx_len = ProtocolUtils::PackTelemetry(telemetry, tx_buf);
            CommManager::Instance().SendRawFrame(tx_buf, tx_len);
            break;
        }

        case ProtocolCmd::ScreenSync: {
            ScreenSyncInfo sync_info;
            if (ProtocolUtils::ParseScreenSync(payload, len, sync_info)) {
                DisplayManager::Instance().UpdateState(sync_info);
            }
            break;
        }

        case ProtocolCmd::PowerControl: {
            if (len >= 2) {
                uint8_t channel = payload[0];
                bool enable = (payload[1] != 0x00);
                bool ok = PowerManager::Instance().SetPower(channel, enable);
                if (ok) {
                    tx_len = ProtocolUtils::PackPowerAck(channel, enable, tx_buf);
                    CommManager::Instance().SendRawFrame(tx_buf, tx_len);
                }
            }
            break;
        }

        case ProtocolCmd::WifiControl: {
            // 0x40: Wi-Fi 控制帧 [SubCmd(1B), ParamStr(NB)]
            if (len >= 1) {
                uint8_t sub_cmd = payload[0];
                if (sub_cmd == 0x01) { // 启动 AP 模式 (主动 AP)
                    char ssid[33] = "LinkNexus-ESP32-AP";
                    if (len > 1) {
                        uint8_t copy_len = (len - 1 < 32) ? len - 1 : 32;
                        memcpy(ssid, payload + 1, copy_len);
                        ssid[copy_len] = '\0';
                    }
                    WifiManager::Instance().StartAp(ssid);
                } else if (sub_cmd == 0x02) { // 启动 STA 模式 (从 AP)
                    char ssid[33] = {0};
                    if (len > 1) {
                        uint8_t copy_len = (len - 1 < 32) ? len - 1 : 32;
                        memcpy(ssid, payload + 1, copy_len);
                        ssid[copy_len] = '\0';
                    }
                    WifiManager::Instance().ConnectSta(ssid, "");
                } else if (sub_cmd == 0x00) { // 关闭 Wi-Fi
                    WifiManager::Instance().Disconnect();
                }
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

                char response_str[512];
                size_t resp_len = CliEngine::Instance().Execute(cmd_str, response_str, sizeof(response_str));
                if (resp_len > 0) {
                    uint8_t pack_len = (resp_len > 255) ? 255 : static_cast<uint8_t>(resp_len);
                    tx_len = ProtocolUtils::PackFrame(static_cast<uint8_t>(ProtocolCmd::CliTransfer),
                                                      reinterpret_cast<const uint8_t*>(response_str),
                                                      pack_len,
                                                      tx_buf);
                    CommManager::Instance().SendRawFrame(tx_buf, tx_len);
                }
            }
            break;
        }

        default:
            ESP_LOGW(TAG, "Received unknown command code: 0x%02X (Len: %u)", cmd, len);
            break;
    }
}

void AppCoordinator::CommRxTaskEntry(void *pvParameters)
{
    auto *self = static_cast<AppCoordinator*>(pvParameters);
    uint8_t rx_buffer[256];
    ESP_LOGI(TAG, "CommRxTask running on Core %d (Priority: 10).", xPortGetCoreID());

    while (1) {
        int bytes_read = CommManager::Instance().ReadBytes(rx_buffer, sizeof(rx_buffer), pdMS_TO_TICKS(20));
        if (bytes_read > 0) {
            self->m_parser.Feed(rx_buffer, static_cast<size_t>(bytes_read));
        }
    }
}

void AppCoordinator::TelemetryTaskEntry(void *pvParameters)
{
    (void)pvParameters;
    uint8_t tx_buf[32];
    TelemetryPayload telemetry;
    ESP_LOGI(TAG, "TelemetryTask running on Core %d (Priority: 5, Period: 250ms).", xPortGetCoreID());

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(250));

        TelemetryManager::Instance().ReadTelemetry(telemetry, PowerManager::Instance());
        size_t len = ProtocolUtils::PackTelemetry(telemetry, tx_buf);
        CommManager::Instance().SendRawFrame(tx_buf, len);
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
                            4096,
                            this,
                            10,
                            nullptr,
                            Cores::COMMUNICATION_CORE);

    // 2. Core 0 专用于传感器 I2C 遥测采集 (250ms 周期)
    xTaskCreatePinnedToCore(TelemetryTaskEntry,
                            "telemetry",
                            4096,
                            nullptr,
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

    m_started = true;
    ESP_LOGI(TAG, "All dual-core tasks spawned and running smoothly.");
}

} // namespace LinkNexus

