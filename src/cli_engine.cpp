#include "cli_engine.hpp"
#include "esp_console.h"
#include "wifi_manager.hpp"
#include "app_coordinator.hpp"
#include "config_manager.hpp"
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

static bool ParseBoolCli(const char *str, bool &out_val)
{
    if (!str) return false;
    if (strcasecmp(str, "true") == 0 || strcasecmp(str, "1") == 0 ||
        strcasecmp(str, "on") == 0 || strcasecmp(str, "enable") == 0 ||
        strcasecmp(str, "high") == 0) {
        out_val = true;
        return true;
    }
    if (strcasecmp(str, "false") == 0 || strcasecmp(str, "0") == 0 ||
        strcasecmp(str, "off") == 0 || strcasecmp(str, "disable") == 0 ||
        strcasecmp(str, "low") == 0) {
        out_val = false;
        return true;
    }
    return false;
}

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
        "  help                               - 显示 CLI 指令帮助列表\r\n"
        "  wifi ap <ssid> [pwd] [ch] [save]   - 启动 SoftAP 热点 (可选 [save] 固化至 NVS)\r\n"
        "  wifi connect <ssid> [pwd] [save]   - 连接局域网 STA (可选 [save] 固化至 NVS)\r\n"
        "  wifi status                        - 查询当前 Wi-Fi 工作模式、射频功率与连接状态\r\n"
        "  wifi txpower [dbm] [save]          - 查询/设置发射功率 (8~20 dBm, 可选 [save])\r\n"
        "  wifi protocol [b|bg|bgn] [save]    - 设置调制协议 (b/bg/bgn, 可选 [save])\r\n"
        "  wifi channel <1-13> [save]         - 设置射频工作信道 (1~13, 可选 [save])\r\n"
        "  wifi diag                          - 显示天线 Π 网 0Ω 射频阻抗与握手失败诊断报告\r\n"
        "  wifi scan [force]                  - 扫描周围可用 Wi-Fi 热点 (复用扫描缓存)\r\n"
        "  wifi disconnect                    - 断开 Wi-Fi 连接并关闭无线调试端口\r\n"
        "  wifi save                          - 将当前所有 Wi-Fi 运行时参数固化至 NVS\r\n"
        "  wifi reset                         - 清除 NVS 自定义配置并恢复出厂默认值\r\n"
        "  wifi config                        - 查看当前 NVS 持久化参数配置表\r\n"
        "  status                             - 查询 ESP32-S3 双核系统资源与运行状态\r\n"
        "  whoami                             - 查询设备身份标识与主控角色确认\r\n"
        "  reboot                             - 软复位重启 ESP32-S3\r\n"
        "  clear                              - 清空当前终端屏幕\r\n"
        "=================================================================================\r\n");
    return 0;
}

static int cmd_wifi(int argc, char **argv)
{
    if (argc < 2) {
        CliEngine::PrintResponse("错误: 请输入子指令 (ap, connect, status, txpower, protocol, channel, diag, scan, disconnect, save, reset, config)。输入 'help' 获取指南。\r\n");
        return 1;
    }

    const char *sub_cmd = argv[1];
    if (strcasecmp(sub_cmd, "save") == 0) {
        ConfigManager::Instance().SaveAllCurrent();
        CliEngine::PrintResponse("[NVS] 当前 Wi-Fi 运行时参数 (发射功率/调制协议/工作信道/AP配置/STA配置) 已全量固化至 NVS 存储区。\r\n");
        return 0;
    }
    else if (strcasecmp(sub_cmd, "reset") == 0) {
        ConfigManager::Instance().ResetToDefaults();
        CliEngine::PrintResponse("[NVS] 已清除 NVS 自定义配置并恢复出厂默认值。\r\n");
        return 0;
    }
    else if (strcasecmp(sub_cmd, "config") == 0 || strcasecmp(sub_cmd, "nvs") == 0) {
        static char s_cfg_buf[1024];
        ConfigManager::Instance().FormatConfigSummary(s_cfg_buf, sizeof(s_cfg_buf));
        CliEngine::PrintResponse("%s", s_cfg_buf);
        return 0;
    }
    else if (strcasecmp(sub_cmd, "ap") == 0) {
        bool do_save = false;
        const char *ssid = "LinkNexus-ESP32-AP";
        const char *pwd  = "12345678";
        uint8_t ch = 1;
        int pos = 0;
        for (int i = 2; i < argc; ++i) {
            if (strcasecmp(argv[i], "save") == 0) {
                do_save = true;
                continue;
            }
            if (pos == 0) ssid = argv[i];
            else if (pos == 1) pwd = argv[i];
            else if (pos == 2) {
                int c = atoi(argv[i]);
                if (c >= 1 && c <= 13) ch = static_cast<uint8_t>(c);
            }
            pos++;
        }

        bool ok = WifiManager::Instance().StartAp(ssid, pwd, ch);
        if (do_save && ok) {
            ConfigManager::Instance().SaveSoftAp(ssid, pwd, ch);
        }
        WifiStatusInfo st = WifiManager::Instance().GetStatus();
        uint32_t freq_mhz = WifiManager::GetChannelFreqMhz(st.channel);
        CliEngine::PrintResponse(
            "[%s] ESP32-S3 主动 AP (SoftAP) 已启动%s:\r\n"
            "  SSID      : %s\r\n"
            "  本机 IP   : %s\r\n"
            "  信道频率  : CH%u (%u MHz)\r\n"
            "  发射功率  : %.2f dBm (raw: %d)\r\n"
            "  调制协议  : %s\r\n"
            "  无线调试  : TCP %s:%u [监听中]\r\n",
            ok ? "SUCCESS" : "WARN",
            do_save ? " [已固化至 NVS]" : "",
            st.ssid, st.ip_str, st.channel, freq_mhz,
            st.tx_power_dbm, WifiManager::Instance().GetTxPowerRaw(),
            WifiManager::Instance().GetProtocolName(),
            st.ip_str, st.wireless_debug_port);
        return 0;
    }
    else if (strcasecmp(sub_cmd, "connect") == 0 || strcasecmp(sub_cmd, "sta") == 0) {
        bool do_save = false;
        const char *ssid = nullptr;
        const char *pwd  = "";
        int pos = 0;
        for (int i = 2; i < argc; ++i) {
            if (strcasecmp(argv[i], "save") == 0) {
                do_save = true;
                continue;
            }
            if (pos == 0) ssid = argv[i];
            else if (pos == 1) pwd = argv[i];
            pos++;
        }
        if (!ssid) {
            CliEngine::PrintResponse("语法错误: wifi connect <SSID> [密码] [save]\r\n");
            return 1;
        }

        // 检测是否已处于连接就绪状态 (避免重复连接并直接返回当前连接状态)
        if (WifiManager::Instance().IsStaConnected(ssid)) {
            if (do_save) {
                ConfigManager::Instance().SaveSta(ssid, pwd, true);
            }
            WifiStatusInfo st = WifiManager::Instance().GetStatus();
            uint32_t freq_mhz = WifiManager::GetChannelFreqMhz(st.channel);
            CliEngine::PrintResponse(
                "[ALREADY_CONNECTED] Wi-Fi 已连接目标网络 (无需重复发起连接)%s:\r\n"
                "  目标 SSID    : %s\r\n"
                "  IP 地址      : %s\r\n"
                "  信号强度     : %d dBm\r\n"
                "  信道频率     : CH%u (%u MHz)\r\n"
                "  发射功率     : %.2f dBm (raw: %d)\r\n"
                "  调制协议     : %s\r\n"
                "  无线调试服务 : %s (TCP :%u)\r\n",
                do_save ? " [已固化至 NVS]" : "",
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
        if (do_save && ok) {
            ConfigManager::Instance().SaveSta(ssid, pwd, true);
        }
        CliEngine::PrintResponse(
            "[%s] ESP32-S3 从 AP (Station) 正在连接目标网络%s:\r\n"
            "  目标 SSID : %s\r\n"
            "  发射功率  : %.2f dBm (raw: %d)\r\n"
            "  连接状态  : 握手协商中 (DHCP 获取 IP 中...)\r\n"
            "  提示      : 若握手超时失败，可使用 'wifi txpower 10' 降低发射功率再试\r\n",
            ok ? "SUCCESS" : "FAILED",
            do_save ? " [已固化至 NVS]" : "",
            ssid,
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
            bool do_save = (argc > 3 && strcasecmp(argv[3], "save") == 0);
            float val = atof(argv[2]);
            if (val <= 25.0f && val >= 2.0f) {
                WifiManager::Instance().SetTxPowerDbm(val);
            } else if (val >= 28.0f && val <= 84.0f) {
                WifiManager::Instance().SetTxPowerRaw(static_cast<int8_t>(val));
            } else {
                CliEngine::PrintResponse("参数错误: 请输入 dBm 功率 (8~20) 或底层寄存器值 (32~80)。\r\n");
                return 1;
            }
            if (do_save) {
                ConfigManager::Instance().SaveTxPowerRaw(WifiManager::Instance().GetTxPowerRaw());
            }
            CliEngine::PrintResponse(
                "[WiFi RF] 最大发射功率已更新%s:\r\n"
                "  设定功率 : %.2f dBm (底层寄存器: %d)\r\n"
                "  调谐提示 : 针对 0Ω 直连 IPEX 自制板，降低发射功率可显著减少天线反射与 EVM 失真。\r\n",
                do_save ? " [已固化至 NVS]" : "",
                WifiManager::Instance().GetTxPowerDbm(),
                WifiManager::Instance().GetTxPowerRaw());
            return 0;
        }

        CliEngine::PrintResponse(
            "[WiFi RF] 当前 Wi-Fi 发射功率配置:\r\n"
            "  当前功率 : %.2f dBm (底层寄存器: %d, 范围: 8.00~20.00 dBm / raw 32~80)\r\n"
            "  推荐基准 : 10.00 ~ 13.00 dBm (抑制未调谐 0Ω Π 网阻抗失配反射)\r\n"
            "  修改语法 : wifi txpower <dBm值> [save] (例如: wifi txpower 10 save)\r\n",
            WifiManager::Instance().GetTxPowerDbm(),
            WifiManager::Instance().GetTxPowerRaw());
        return 0;
    }
    else if (strcasecmp(sub_cmd, "protocol") == 0 || strcasecmp(sub_cmd, "proto") == 0 || strcasecmp(sub_cmd, "mode") == 0) {
        if (argc > 2) {
            bool do_save = (argc > 3 && strcasecmp(argv[3], "save") == 0);
            const char *mode = argv[2];
            uint8_t proto_mask = 0;
            if (strcasecmp(mode, "b") == 0 || strcasecmp(mode, "11b") == 0) {
                proto_mask = 1; // WIFI_PROTOCOL_11B
            } else if (strcasecmp(mode, "bg") == 0 || strcasecmp(mode, "11bg") == 0) {
                proto_mask = 1 | 2; // WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G
            } else if (strcasecmp(mode, "bgn") == 0 || strcasecmp(mode, "11bgn") == 0 || strcasecmp(mode, "all") == 0) {
                proto_mask = 1 | 2 | 4; // WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G | WIFI_PROTOCOL_11N
            } else {
                CliEngine::PrintResponse("参数错误: 支持 'b' (极高抗失配), 'bg' (54M兼容), 'bgn' (全速)。\r\n");
                return 1;
            }
            WifiManager::Instance().SetProtocol(proto_mask);
            if (do_save) {
                ConfigManager::Instance().SaveProtocol(proto_mask);
            }
            CliEngine::PrintResponse("[WiFi RF] 调制协议模式已切换为: %s%s\r\n",
                                     WifiManager::Instance().GetProtocolName(),
                                     do_save ? " [已固化至 NVS]" : "");
            return 0;
        }

        CliEngine::PrintResponse(
            "[WiFi RF] 当前 Wi-Fi 调制协议模式: %s\r\n"
            "  切换语法 : wifi protocol <b | bg | bgn> [save]\r\n"
            "  说明     : 模式 'b' 采用 CCK 调制 (1~11Mbps)，对天线失配和高驻波具有极强抗干扰能力\r\n",
            WifiManager::Instance().GetProtocolName());
        return 0;
    }
    else if (strcasecmp(sub_cmd, "channel") == 0 || strcasecmp(sub_cmd, "ch") == 0) {
        if (argc > 2) {
            bool do_save = (argc > 3 && strcasecmp(argv[3], "save") == 0);
            uint8_t ch = static_cast<uint8_t>(atoi(argv[2]));
            if (ch >= 1 && ch <= 13) {
                WifiManager::Instance().SetChannel(ch);
                if (do_save) {
                    ConfigManager::Instance().SaveChannel(ch);
                }
                CliEngine::PrintResponse("[WiFi RF] 工作信道频率已设置为 CH%u (%u MHz)%s\r\n",
                                         ch, WifiManager::GetChannelFreqMhz(ch),
                                         do_save ? " [已固化至 NVS]" : "");
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
        "  主控唯一 MAC : %s\r\n"
        "  Wi-Fi IP     : %s (模式: %s)\r\n"
        "  无线调试服务 : %s\r\n"
        "  上位机链路   : %s\r\n"
        "  遥测广播流   : %s\r\n",
        static_cast<unsigned int>(esp_get_free_heap_size()),
        LINKNEXUS_FIRMWARE_VERSION,
        LINKNEXUS_DEFAULT_MAC,
        st.ip_str,
        (st.mode == WifiWorkMode::SoftAP) ? "主动 AP" : (st.mode == WifiWorkMode::Station) ? "从 AP (STA)" : "OFF",
        st.is_wireless_debug_active ? "TCP :8266 监听中" : "未启动",
        host_active ? "在线 (C# Heartbeat 活跃)" : "离线 (CLI独立终端模式)",
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

static int cmd_whoami(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    CliEngine::PrintResponse(
        "LinkNexus-Pro-ESP32S3 | MAC: %s | Firmware: %s | Role: Master Daemon | Chip: %s\r\n",
        LINKNEXUS_DEFAULT_MAC,
        LINKNEXUS_FIRMWARE_VERSION,
        LINKNEXUS_CHIP_PART_NUMBER);
    return 0;
}

static int cmd_clear(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    CliEngine::PrintResponse("\033[2J\033[H[LinkNexus Shell] 屏幕已清空。\r\n");
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
        .help = "Wi-Fi 模式与网络控制 (ap, connect, status, txpower, protocol, channel, diag, scan, disconnect, save, reset, config)",
        .hint = "<ap|connect|status|txpower|protocol|channel|diag|scan|disconnect|save|reset|config>",
        .func = &cmd_wifi,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&wifi_cmd));

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

    const esp_console_cmd_t whoami_cmd = {
        .command = "whoami",
        .help = "查询设备身份标识与主控角色确认",
        .hint = NULL,
        .func = &cmd_whoami,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&whoami_cmd));

    const esp_console_cmd_t clear_cmd = {
        .command = "clear",
        .help = "清空控制台屏幕",
        .hint = NULL,
        .func = &cmd_clear,
        .argtable = NULL,
        .func_w_context = NULL,
        .context = NULL
    };
    ESP_ERROR_CHECK(esp_console_cmd_register(&clear_cmd));
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

    // 跳过前导空白字符
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
    s_response_buf[0] = '\0';

    int ret_code = 0;
    esp_err_t err = esp_console_run(clean_cmd, &ret_code);

    if (err == ESP_ERR_NOT_FOUND) {
        PrintResponse("未知指令: \"%s\"。输入 'help' 获取可用命令列表。\r\n", clean_cmd);
    } else if (err != ESP_OK) {
        PrintResponse("指令执行失败: %s (错误码: %d)\r\n", esp_err_to_name(err), err);
    }

    size_t actual_len = s_response_len;
    s_response_buf = nullptr;
    s_response_max = 0;
    s_response_len = 0;

    return actual_len;
}

} // namespace LinkNexus
