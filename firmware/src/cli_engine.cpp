#include "cli_engine.hpp"
#include "esp_console.h"
#include "wifi_manager.hpp"
#include "power_manager.hpp"
#include "telemetry_manager.hpp"
#include "app_coordinator.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <stdarg.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>

namespace LinkNexus {

static const char *TAG = "CLI_ENGINE";

static char *s_response_buf = nullptr;
static size_t s_response_max = 0;
static size_t s_response_len = 0;

CliEngine& CliEngine::Instance()
{
    static CliEngine instance;
    return instance;
}

CliEngine::CliEngine()
{
    Init();
}

void CliEngine::PrintResponse(const char *fmt, ...)
{
    if (!s_response_buf || s_response_len >= s_response_max) return;
    va_list args;
    va_start(args, fmt);
    int remaining = static_cast<int>(s_response_max - s_response_len);
    int written = vsnprintf(s_response_buf + s_response_len, remaining, fmt, args);
    va_end(args);
    if (written > 0) {
        s_response_len += static_cast<size_t>(written);
        if (s_response_len >= s_response_max) {
            s_response_len = s_response_max - 1;
            s_response_buf[s_response_len] = '\0';
        }
    }
}

static int cmd_help(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    CliEngine::PrintResponse(
        "================ LinkNexus ESP32-S3 CLI 指令手册 (esp_console) ================\r\n"
        "  help                         - 显示 CLI 指令帮助列表\r\n"
        "  wifi ap <ssid> [pwd] [ch]    - 启动 ESP32 为 SoftAP 独立热点 (主动 AP 模式)\r\n"
        "  wifi connect <ssid> <pwd>    - 控制 ESP32 链接指定局域网 AP (从 AP / STA 模式)\r\n"
        "  wifi status                  - 查询当前 Wi-Fi 工作模式、射频功率与连接状态\r\n"
        "  wifi txpower [dbm]           - 查询/设置 Wi-Fi 最大发射功率 (8~20 dBm，抗失配调谐)\r\n"
        "  wifi protocol [b|bg|bgn]     - 设置调制协议 (b: CCK极低速率高抗失配, bg: 54M, bgn: 全速)\r\n"
        "  wifi channel <1-13>          - 设置射频工作信道频率 (1~13)\r\n"
        "  wifi diag                    - 显示自制板天线 Π 网 0Ω 射频阻抗与握手失败诊断报告\r\n"
        "  wifi scan                    - 扫描并列出周围环境可用 Wi-Fi 热点 (复用单次扫描缓存)\r\n"
        "  wifi disconnect              - 断开并停止 Wi-Fi 与无线调试监听\r\n"
        "  power <ch 0-5> <0|1>         - 控制指定通道 MOS 供电开通/隔离\r\n"
        "  status                       - 查询 ESP32-S3 双核系统资源与运行状态\r\n"
        "  telemetry [on|off]           - 查询 INA236 文本遥测采样或控制二进制广播流\r\n"
        "  reboot                       - 软复位重启 ESP32-S3\r\n"
        "=================================================================================\r\n");
    return 0;
}

static int cmd_wifi(int argc, char **argv)
{
    if (argc < 2) {
        CliEngine::PrintResponse("错误: 请输入子指令 (ap, connect, status, txpower, protocol, channel, diag, scan, disconnect)。输入 'help' 获取指南。\r\n");
        return 1;
    }

    const char *sub_cmd = argv[1];
    if (strcasecmp(sub_cmd, "ap") == 0) {
        const char *ssid = (argc > 2) ? argv[2] : "LinkNexus-ESP32-AP";
        const char *pwd  = (argc > 3) ? argv[3] : "12345678";
        uint8_t ch = (argc > 4 && atoi(argv[4]) >= 1 && atoi(argv[4]) <= 13) ? static_cast<uint8_t>(atoi(argv[4])) : 1;

        bool ok = WifiManager::Instance().StartAp(ssid, pwd, ch);
        WifiStatusInfo st = WifiManager::Instance().GetStatus();
        uint32_t freq_mhz = WifiManager::GetChannelFreqMhz(st.channel);
        CliEngine::PrintResponse(
            "[%s] ESP32-S3 主动 AP (SoftAP) 已启动:\r\n"
            "  SSID      : %s\r\n"
            "  本机 IP   : %s\r\n"
            "  信道频率  : CH%u (%u MHz)\r\n"
            "  发射功率  : %.2f dBm (raw: %d)\r\n"
            "  调制协议  : %s\r\n"
            "  无线调试  : TCP %s:%u [监听中]\r\n",
            ok ? "SUCCESS" : "WARN", st.ssid, st.ip_str, st.channel, freq_mhz,
            st.tx_power_dbm, WifiManager::Instance().GetTxPowerRaw(),
            WifiManager::Instance().GetProtocolName(),
            st.ip_str, st.wireless_debug_port);
        return 0;
    }
    else if (strcasecmp(sub_cmd, "connect") == 0 || strcasecmp(sub_cmd, "sta") == 0) {
        if (argc < 3) {
            CliEngine::PrintResponse("语法错误: wifi connect <SSID> [密码]\r\n");
            return 1;
        }
        const char *ssid = argv[2];
        const char *pwd  = (argc > 3) ? argv[3] : "";

        // 检测是否已处于连接就绪状态 (避免重复连接并直接返回当前连接状态)
        if (WifiManager::Instance().IsStaConnected(ssid)) {
            WifiStatusInfo st = WifiManager::Instance().GetStatus();
            uint32_t freq_mhz = WifiManager::GetChannelFreqMhz(st.channel);
            CliEngine::PrintResponse(
                "[ALREADY_CONNECTED] Wi-Fi 已连接目标网络 (无需重复发起连接):\r\n"
                "  目标 SSID    : %s\r\n"
                "  IP 地址      : %s\r\n"
                "  信号强度     : %d dBm\r\n"
                "  信道频率     : CH%u (%u MHz)\r\n"
                "  发射功率     : %.2f dBm (raw: %d)\r\n"
                "  调制协议     : %s\r\n"
                "  无线调试服务 : %s (TCP :%u)\r\n",
                st.ssid, st.ip_str, st.rssi,
                st.channel, freq_mhz,
                st.tx_power_dbm, WifiManager::Instance().GetTxPowerRaw(),
                WifiManager::Instance().GetProtocolName(),
                st.is_wireless_debug_active ? "已启动 [LISTENING]" : "未运行",
                st.wireless_debug_port);
            return 0;
        }

        // 检测是否正与其它 AP 连接或处于协商中
        if (WifiManager::Instance().IsStaConnected(nullptr)) {
            WifiStatusInfo st = WifiManager::Instance().GetStatus();
            CliEngine::PrintResponse(
                "[SWITCHING] 当前已连接至 '%s' (%s)，正在切换至新目标 '%s'...\r\n",
                st.ssid, st.ip_str, ssid);
            WifiManager::Instance().Disconnect();
        } else if (WifiManager::Instance().IsStaConnecting()) {
            CliEngine::PrintResponse(
                "[CONNECTING] Wi-Fi 正在与目标网络进行握手协商，请稍候...\r\n");
            return 0;
        }

        bool ok = WifiManager::Instance().ConnectSta(ssid, pwd);
        CliEngine::PrintResponse(
            "[%s] ESP32-S3 从 AP (Station) 正在连接目标网络:\r\n"
            "  目标 SSID : %s\r\n"
            "  发射功率  : %.2f dBm (raw: %d)\r\n"
            "  连接状态  : 握手协商中 (DHCP 获取 IP 中...)\r\n"
            "  提示      : 若握手超时失败，可使用 'wifi txpower 10' 降低发射功率再试\r\n",
            ok ? "SUCCESS" : "FAILED", ssid,
            WifiManager::Instance().GetTxPowerDbm(),
            WifiManager::Instance().GetTxPowerRaw());
        return 0;
    }
    else if (strcasecmp(sub_cmd, "status") == 0) {
        WifiStatusInfo st = WifiManager::Instance().GetStatus();
        const char *mode_str = (st.mode == WifiWorkMode::SoftAP) ? "SoftAP (主动独立热点)" :
                               (st.mode == WifiWorkMode::Station) ? "Station (从 AP / 已接入外部网络)" : "OFF (关闭)";
        uint32_t freq_mhz = WifiManager::GetChannelFreqMhz(st.channel);
        CliEngine::PrintResponse(
            "ESP32-S3 Wi-Fi & 射频天线状态:\r\n"
            "  硬件芯片     : %s\r\n"
            "  天线射频电路 : %s\r\n"
            "  工作模式     : %s\r\n"
            "  当前 SSID    : %s\r\n"
            "  IP 地址      : %s\r\n"
            "  信道频率     : CH%u (%u MHz)\r\n"
            "  发射功率     : %.2f dBm (raw: %d)\r\n"
            "  调制协议     : %s\r\n"
            "  RSSI 信号    : %d dBm\r\n"
            "  已连客户端   : %u\r\n"
            "  无线调试服务 : %s (TCP :%u)\r\n",
            LINKNEXUS_CHIP_PART_NUMBER,
            LINKNEXUS_RF_ANTENNA_DESC,
            mode_str, st.ssid, st.ip_str,
            st.channel, freq_mhz,
            st.tx_power_dbm, WifiManager::Instance().GetTxPowerRaw(),
            WifiManager::Instance().GetProtocolName(),
            st.rssi, st.connected_clients,
            st.is_wireless_debug_active ? "已启动 [LISTENING]" : "未运行",
            st.wireless_debug_port);
        return 0;
    }
    else if (strcasecmp(sub_cmd, "txpower") == 0 || strcasecmp(sub_cmd, "power") == 0 || strcasecmp(sub_cmd, "pwr") == 0) {
        if (argc > 2) {
            float val = atof(argv[2]);
            if (val <= 25.0f && val >= 2.0f) {
                WifiManager::Instance().SetTxPowerDbm(val);
            } else if (val >= 28.0f && val <= 84.0f) {
                WifiManager::Instance().SetTxPowerRaw(static_cast<int8_t>(val));
            } else {
                CliEngine::PrintResponse("参数错误: 请输入 dBm 功率 (8~20) 或底层寄存器值 (32~80)。\r\n");
                return 1;
            }
            CliEngine::PrintResponse(
                "[WiFi RF] 最大发射功率已更新:\r\n"
                "  设定功率 : %.2f dBm (底层寄存器: %d)\r\n"
                "  调谐提示 : 针对 0Ω 直连 IPEX 自制板，降低发射功率可显著减少天线反射与 EVM 失真。\r\n",
                WifiManager::Instance().GetTxPowerDbm(),
                WifiManager::Instance().GetTxPowerRaw());
            return 0;
        }

        CliEngine::PrintResponse(
            "[WiFi RF] 当前 Wi-Fi 发射功率配置:\r\n"
            "  当前功率 : %.2f dBm (底层寄存器: %d, 范围: 8.00~20.00 dBm / raw 32~80)\r\n"
            "  推荐基准 : 10.00 ~ 13.00 dBm (抑制未调谐 0Ω Π 网阻抗失配反射)\r\n"
            "  修改语法 : wifi txpower <dBm值> (例如: wifi txpower 10 或 wifi txpower 13)\r\n",
            WifiManager::Instance().GetTxPowerDbm(),
            WifiManager::Instance().GetTxPowerRaw());
        return 0;
    }
    else if (strcasecmp(sub_cmd, "protocol") == 0 || strcasecmp(sub_cmd, "proto") == 0 || strcasecmp(sub_cmd, "mode") == 0) {
        if (argc > 2) {
            const char *mode = argv[2];
            if (strcasecmp(mode, "b") == 0 || strcasecmp(mode, "11b") == 0) {
                WifiManager::Instance().SetProtocol(1); // WIFI_PROTOCOL_11B
            } else if (strcasecmp(mode, "bg") == 0 || strcasecmp(mode, "11bg") == 0) {
                WifiManager::Instance().SetProtocol(1 | 2); // WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G
            } else if (strcasecmp(mode, "bgn") == 0 || strcasecmp(mode, "11bgn") == 0 || strcasecmp(mode, "all") == 0) {
                WifiManager::Instance().SetProtocol(1 | 2 | 4); // WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G | WIFI_PROTOCOL_11N
            } else {
                CliEngine::PrintResponse("参数错误: 支持 'b' (极高抗失配), 'bg' (54M兼容), 'bgn' (全速)。\r\n");
                return 1;
            }
            CliEngine::PrintResponse("[WiFi RF] 调制协议模式已切换为: %s\r\n", WifiManager::Instance().GetProtocolName());
            return 0;
        }

        CliEngine::PrintResponse(
            "[WiFi RF] 当前 Wi-Fi 调制协议模式: %s\r\n"
            "  切换语法 : wifi protocol <b | bg | bgn>\r\n"
            "  说明     : 模式 'b' 采用 CCK 调制 (1~11Mbps)，对天线失配和高驻波具有极强抗干扰能力\r\n",
            WifiManager::Instance().GetProtocolName());
        return 0;
    }
    else if (strcasecmp(sub_cmd, "channel") == 0 || strcasecmp(sub_cmd, "ch") == 0) {
        if (argc > 2) {
            uint8_t ch = static_cast<uint8_t>(atoi(argv[2]));
            if (ch >= 1 && ch <= 13) {
                WifiManager::Instance().SetChannel(ch);
                CliEngine::PrintResponse("[WiFi RF] 工作信道频率已设置为 CH%u (%u MHz)\r\n",
                                         ch, WifiManager::GetChannelFreqMhz(ch));
                return 0;
            } else {
                CliEngine::PrintResponse("参数错误: 2.4GHz Wi-Fi 信道范围为 1~13。\r\n");
                return 1;
            }
        }
        CliEngine::PrintResponse("[WiFi RF] 当前信道频率: CH%u (%u MHz)\r\n",
                                 WifiManager::Instance().GetChannel(),
                                 WifiManager::GetChannelFreqMhz(WifiManager::Instance().GetChannel()));
        return 0;
    }
    else if (strcasecmp(sub_cmd, "diag") == 0 || strcasecmp(sub_cmd, "rf") == 0 || strcasecmp(sub_cmd, "ant") == 0) {
        static char s_diag_buf[1536];
        size_t dlen = WifiManager::Instance().FormatRfDiagnostics(s_diag_buf, sizeof(s_diag_buf));
        if (dlen > 0) {
            CliEngine::PrintResponse("%s", s_diag_buf);
        } else {
            CliEngine::PrintResponse("[WiFi RF] 无法生成诊断报告。\r\n");
        }
        return 0;
    }
    else if (strcasecmp(sub_cmd, "scan") == 0 || strcasecmp(sub_cmd, "rescan") == 0) {
        bool force = (argc > 2 && (strcasecmp(argv[2], "force") == 0 || strcasecmp(argv[2], "-f") == 0)) ||
                     (strcasecmp(sub_cmd, "rescan") == 0);
        
        // 直接从 WifiManager 获取格式化结果指针，0 栈内存消耗，彻底根除栈溢出崩溃
        if (!force && WifiManager::Instance().HasScanned()) {
            const char *cached = WifiManager::Instance().GetCachedScanResult();
            if (cached && strlen(cached) > 0) {
                CliEngine::PrintResponse("%s", cached);
                return 0;
            }
        }

        size_t scan_len = WifiManager::Instance().PerformScanAndFormat(false);
        const char *res = WifiManager::Instance().GetCachedScanResult();
        if (scan_len > 0 && res) {
            CliEngine::PrintResponse("%s", res);
        } else {
            CliEngine::PrintResponse("[WiFi Scan] 未扫描到可用无线网络。\r\n");
        }
        return 0;
    }
    else if (strcasecmp(sub_cmd, "disconnect") == 0 || strcasecmp(sub_cmd, "stop") == 0 || strcasecmp(sub_cmd, "off") == 0) {
        WifiManager::Instance().Disconnect();
        CliEngine::PrintResponse("[WiFi] 已断开 Wi-Fi 连接并关闭无线调试端口。\r\n");
        return 0;
    }
    else {
        CliEngine::PrintResponse("未知 wifi 子指令 '%s'。输入 'help' 获取命令帮助。\r\n", sub_cmd);
        return 1;
    }
}

static int cmd_power(int argc, char **argv)
{
    if (argc < 3) {
        CliEngine::PrintResponse("语法: power <通道 0-5> <0|1> (例: power 0 1)\r\n");
        return 1;
    }

    uint8_t ch = static_cast<uint8_t>(atoi(argv[1]));
    bool en = (atoi(argv[2]) != 0);
    bool ok = PowerManager::Instance().SetPower(ch, en);
    CliEngine::PrintResponse("[MOS] 通道 %u 供电状态已更新为: %s (%s)\r\n",
                            ch, en ? "导通 (ON)" : "隔离 (OFF)", ok ? "OK" : "FAILED");
    return ok ? 0 : 1;
}

static int cmd_status(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    WifiStatusInfo st = WifiManager::Instance().GetStatus();
    bool host_active = AppCoordinator::Instance().IsHostActive();
    CliEngine::PrintResponse(
        "ESP32-S3 双核系统遥测 (esp_console):\r\n"
        "  CPU 架构     : Xtensa LX7 双核 @ 240MHz (Core 0 + Core 1)\r\n"
        "  FreeRTOS     : SMP 抢占式多任务内核 (Tick: 1000Hz)\r\n"
        "  系统剩余堆栈 : %u Bytes (SRAM)\r\n"
        "  固件版本号   : %s\r\n"
        "  Wi-Fi IP     : %s (模式: %s)\r\n"
        "  无线调试服务 : %s\r\n"
        "  上位机链路   : %s\r\n"
        "  遥测广播流   : %s\r\n",
        static_cast<unsigned int>(esp_get_free_heap_size()),
        LINKNEXUS_FIRMWARE_VERSION,
        st.ip_str,
        (st.mode == WifiWorkMode::SoftAP) ? "主动 AP" : (st.mode == WifiWorkMode::Station) ? "从 AP (STA)" : "OFF",
        st.is_wireless_debug_active ? "TCP :8266 监听中" : "未启动",
        host_active ? "在线 (C# Heartbeat 活跃)" : "离线 (CLI独立终端模式)",
        host_active ? "广播中 (Active)" : "已静音 (保护终端杜绝乱码)");
    return 0;
}

static int cmd_telemetry(int argc, char **argv)
{
    if (argc > 1) {
        const char *arg = argv[1];
        if (strcasecmp(arg, "on") == 0 || strcasecmp(arg, "stream") == 0) {
            AppCoordinator::Instance().SetManualTelemetryStream(true);
            CliEngine::PrintResponse(
                "[遥测控制] 已手动强制开启 250ms 二进制遥测广播流。\r\n"
                "  提示: 纯文本终端将开始接收二进制原始数据帧并显示字符。\r\n"
                "  输入 'telemetry off' 可随时关闭并恢复智能静音模式。\r\n");
            return 0;
        } else if (strcasecmp(arg, "off") == 0) {
            AppCoordinator::Instance().SetManualTelemetryStream(false);
            CliEngine::PrintResponse("[遥测控制] 已关闭手动遥测流，恢复智能静音保护模式 (杜绝终端乱码)。\r\n");
            return 0;
        }
    }

    TelemetryPayload tel;
    TelemetryManager::Instance().ReadTelemetry(tel, PowerManager::Instance());

    bool host_active = AppCoordinator::Instance().IsHostActive();
    bool manual = AppCoordinator::Instance().IsManualTelemetryStream();

    CliEngine::PrintResponse(
        "================ INA236 硬件传感器实时遥测采样 ================\r\n"
        "  母线总线   (Bus)   : %5.2f V, %5.0f mA  (%5.2f W)\r\n"
        "  XDS110通道 (Port 1): %5.2f V, %5.0f mA  (%5.2f W)\r\n"
        "  DAPLink通道(Port 2): %5.2f V, %5.0f mA  (%5.2f W)\r\n"
        "----------------------------------------------------------------\r\n"
        "  上位机连接状态     : %s\r\n"
        "  二进制广播流模式   : %s\r\n"
        "================================================================\r\n",
        tel.bus_voltage_mv / 1000.0f, static_cast<float>(tel.bus_current_ma),
        (tel.bus_voltage_mv / 1000.0f) * (tel.bus_current_ma / 1000.0f),
        tel.xds_voltage_mv / 1000.0f, static_cast<float>(tel.xds_current_ma),
        (tel.xds_voltage_mv / 1000.0f) * (tel.xds_current_ma / 1000.0f),
        tel.dap_voltage_mv / 1000.0f, static_cast<float>(tel.dap_current_ma),
        (tel.dap_voltage_mv / 1000.0f) * (tel.dap_current_ma / 1000.0f),
        host_active ? (manual ? "手动开启 (CLI Override)" : "在线 (C# Heartbeat 活跃)") : "离线 (CLI 独立终端模式)",
        host_active ? "广播中 (Active)" : "已静音 (保护终端杜绝乱码)");
    return 0;
}

static int cmd_reboot(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    CliEngine::PrintResponse("[System] ESP32-S3 即将执行软复位重启...\r\n");
    vTaskDelay(pdMS_TO_TICKS(100));
    esp_restart();
    return 0;
}

void CliEngine::RegisterCommands()
{
    const esp_console_cmd_t help_cmd = {
        .command = "help",
        .help = "显示系统可用 CLI 指令列表",
        .hint = NULL,
        .func = &cmd_help,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&help_cmd));

    const esp_console_cmd_t wifi_cmd = {
        .command = "wifi",
        .help = "Wi-Fi 模式与网络控制 (ap, connect, status, scan, disconnect)",
        .hint = "<ap|connect|status|scan|disconnect>",
        .func = &cmd_wifi,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&wifi_cmd));

    const esp_console_cmd_t power_cmd = {
        .command = "power",
        .help = "控制 6 路 MOS 供电开通/隔离",
        .hint = "<ch 0-5> <0|1>",
        .func = &cmd_power,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&power_cmd));

    const esp_console_cmd_t status_cmd = {
        .command = "status",
        .help = "查询 ESP32-S3 双核系统资源与运行状态",
        .hint = NULL,
        .func = &cmd_status,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&status_cmd));

    const esp_console_cmd_t telemetry_cmd = {
        .command = "telemetry",
        .help = "查询 INA236 文本遥测采样或控制二进制广播流 (on/off)",
        .hint = "[on|off]",
        .func = &cmd_telemetry,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&telemetry_cmd));

    const esp_console_cmd_t reboot_cmd = {
        .command = "reboot",
        .help = "软复位重启 ESP32-S3",
        .hint = NULL,
        .func = &cmd_reboot,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&reboot_cmd));
}

void CliEngine::Init()
{
    LockGuard lock(m_mutex);
    if (m_initialized) return;

    esp_console_config_t console_config = ESP_CONSOLE_CONFIG_DEFAULT();
    console_config.max_cmdline_length = 256;
    console_config.max_cmdline_args = 16;
    esp_err_t err = esp_console_init(&console_config);
    if (err == ESP_OK) {
        RegisterCommands();
        m_initialized = true;
        ESP_LOGI(TAG, "esp_console component initialized with LinkNexus system commands.");
    } else {
        ESP_LOGE(TAG, "Failed to initialize esp_console: %s", esp_err_to_name(err));
    }
}

size_t CliEngine::Execute(const char *cmd_line, char *out_response, size_t max_out_len)
{
    if (!cmd_line || !out_response || max_out_len == 0) return 0;
    if (!m_initialized) Init();

    // 跳过前导空白符
    while (*cmd_line == ' ' || *cmd_line == '\t' || *cmd_line == '\r' || *cmd_line == '\n') {
        cmd_line++;
    }
    if (*cmd_line == '\0') {
        out_response[0] = '\0';
        return 0;
    }

    char clean_cmd[256];
    strncpy(clean_cmd, cmd_line, sizeof(clean_cmd) - 1);
    clean_cmd[sizeof(clean_cmd) - 1] = '\0';

    // 剔除末尾换行与空格
    size_t len = strlen(clean_cmd);
    while (len > 0 && (clean_cmd[len - 1] == '\r' || clean_cmd[len - 1] == '\n' || clean_cmd[len - 1] == ' ')) {
        clean_cmd[--len] = '\0';
    }

    LockGuard lock(m_mutex);
    s_response_buf = out_response;
    s_response_max = max_out_len;
    s_response_len = 0;
    out_response[0] = '\0';

    int cmd_ret = 0;
    esp_err_t err = esp_console_run(clean_cmd, &cmd_ret);
    if (err == ESP_ERR_NOT_FOUND) {
        PrintResponse("未识别指令 '%s'。输入 'help' 获取可用命令列表。\r\n", clean_cmd);
    } else if (err != ESP_OK && s_response_len == 0) {
        PrintResponse("指令执行错误 (0x%X)\r\n", err);
    }

    s_response_buf = nullptr;
    s_response_max = 0;
    return s_response_len;
}

} // namespace LinkNexus
