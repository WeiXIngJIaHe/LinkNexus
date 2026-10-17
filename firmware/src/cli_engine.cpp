#include "cli_engine.hpp"
#include "wifi_manager.hpp"
#include "power_manager.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>
#include <stdio.h>
#include <stdlib.h>

namespace LinkNexus {

static const char *TAG = "CLI_ENGINE";

CliEngine& CliEngine::Instance()
{
    static CliEngine instance;
    return instance;
}

size_t CliEngine::Execute(const char *cmd_line, char *out_response, size_t max_out_len)
{
    if (!cmd_line || !out_response || max_out_len == 0) return 0;

    // 跳过前导空白符
    while (*cmd_line == ' ' || *cmd_line == '\t' || *cmd_line == '\r' || *cmd_line == '\n') {
        cmd_line++;
    }
    if (*cmd_line == '\0') {
        out_response[0] = '\0';
        return 0;
    }

    char buf[256];
    strncpy(buf, cmd_line, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';

    // 剔除末尾换行与空格
    size_t len = strlen(buf);
    while (len > 0 && (buf[len - 1] == '\r' || buf[len - 1] == '\n' || buf[len - 1] == ' ')) {
        buf[--len] = '\0';
    }

    char *token = strtok(buf, " ");
    if (!token) return 0;

    if (strcasecmp(token, "help") == 0 || strcmp(token, "?") == 0) {
        return snprintf(out_response, max_out_len,
            "================ LinkNexus ESP32-S3 CLI 指令手册 ================\r\n"
            "  help                         - 显示 CLI 指令帮助列表\r\n"
            "  wifi ap <ssid> [pwd]         - 启动 ESP32 为 SoftAP 独立热点 (主动 AP 模式)\r\n"
            "  wifi connect <ssid> <pwd>    - 控制 ESP32 链接指定局域网 AP (从 AP / STA 模式)\r\n"
            "  wifi status                  - 查询当前 Wi-Fi 工作模式、IP 与无线调试状态\r\n"
            "  wifi scan                    - 扫描并列出周围环境可用 Wi-Fi 热点\r\n"
            "  wifi disconnect              - 断开并停止 Wi-Fi 与无线调试监听\r\n"
            "  power <ch 0-5> <0|1>         - 控制指定通道 MOS 供电开通/隔离\r\n"
            "  status                       - 查询 ESP32-S3 双核系统资源与运行状态\r\n"
            "  reboot                       - 软复位重启 ESP32-S3\r\n"
            "==================================================================\r\n");
    }
    else if (strcasecmp(token, "wifi") == 0) {
        char *sub_cmd = strtok(nullptr, " ");
        if (!sub_cmd) {
            return snprintf(out_response, max_out_len, "错误: 请输入子指令 (ap, connect, status, scan, disconnect)。输入 'help' 获取指南。\r\n");
        }

        if (strcasecmp(sub_cmd, "ap") == 0) {
            char *ssid = strtok(nullptr, " ");
            char *pwd = strtok(nullptr, " ");
            char *ch_str = strtok(nullptr, " ");
            if (!ssid) ssid = (char*)"LinkNexus-ESP32-AP";
            if (!pwd) pwd = (char*)"12345678";
            uint8_t ch = (ch_str && atoi(ch_str) >= 1 && atoi(ch_str) <= 13) ? static_cast<uint8_t>(atoi(ch_str)) : 1;

            bool ok = WifiManager::Instance().StartAp(ssid, pwd, ch);
            WifiStatusInfo st = WifiManager::Instance().GetStatus();
            return snprintf(out_response, max_out_len,
                "[%s] ESP32-S3 主动 AP (SoftAP) 已启动:\r\n"
                "  SSID      : %s\r\n"
                "  本机 IP   : %s\r\n"
                "  信道      : %u\r\n"
                "  无线调试  : TCP %s:%u [监听中]\r\n",
                ok ? "SUCCESS" : "WARN", st.ssid, st.ip_str, st.channel, st.ip_str, st.wireless_debug_port);
        }
        else if (strcasecmp(sub_cmd, "connect") == 0 || strcasecmp(sub_cmd, "sta") == 0) {
            char *ssid = strtok(nullptr, " ");
            char *pwd = strtok(nullptr, " ");
            if (!ssid) {
                return snprintf(out_response, max_out_len, "语法错误: wifi connect <SSID> [密码]\r\n");
            }
            if (!pwd) pwd = (char*)"";

            bool ok = WifiManager::Instance().ConnectSta(ssid, pwd);
            return snprintf(out_response, max_out_len,
                "[%s] ESP32-S3 从 AP (Station) 正在连接目标网络:\r\n"
                "  目标 SSID : %s\r\n"
                "  连接状态  : 握手协商中 (DHCP 获取 IP 中...)\r\n"
                "  提示      : 输入 'wifi status' 查看最新连接与真实 IP 获取结果\r\n",
                ok ? "SUCCESS" : "FAILED", ssid);
        }
        else if (strcasecmp(sub_cmd, "status") == 0) {
            WifiStatusInfo st = WifiManager::Instance().GetStatus();
            const char *mode_str = (st.mode == WifiWorkMode::SoftAP) ? "SoftAP (主动独立热点)" :
                                   (st.mode == WifiWorkMode::Station) ? "Station (从 AP / 已接入外部网络)" : "OFF (关闭)";
            return snprintf(out_response, max_out_len,
                "ESP32-S3 Wi-Fi & 无线调试状态:\r\n"
                "  工作模式     : %s\r\n"
                "  当前 SSID    : %s\r\n"
                "  IP 地址      : %s\r\n"
                "  信道 / RSSI  : CH%u / %d dBm\r\n"
                "  已连客户端   : %u\r\n"
                "  无线调试服务 : %s (TCP :%u)\r\n",
                mode_str, st.ssid, st.ip_str, st.channel, st.rssi, st.connected_clients,
                st.is_wireless_debug_active ? "已启动 [LISTENING]" : "未运行",
                st.wireless_debug_port);
        }
        else if (strcasecmp(sub_cmd, "scan") == 0 || strcasecmp(sub_cmd, "rescan") == 0) {
            char *opt = strtok(nullptr, " ");
            bool force = (opt && (strcasecmp(opt, "force") == 0 || strcasecmp(opt, "-f") == 0)) || (strcasecmp(sub_cmd, "rescan") == 0);
            return WifiManager::Instance().ScanNetworks(out_response, max_out_len, force);
        }
        else if (strcasecmp(sub_cmd, "disconnect") == 0 || strcasecmp(sub_cmd, "stop") == 0 || strcasecmp(sub_cmd, "off") == 0) {
            WifiManager::Instance().Disconnect();
            return snprintf(out_response, max_out_len, "[WiFi] 已断开 Wi-Fi 连接并关闭无线调试端口。\r\n");
        }
        else {
            return snprintf(out_response, max_out_len, "未知 wifi 子指令 '%s'。输入 'help' 获取命令帮助。\r\n", sub_cmd);
        }
    }
    else if (strcasecmp(token, "status") == 0) {
        WifiStatusInfo st = WifiManager::Instance().GetStatus();
        return snprintf(out_response, max_out_len,
            "ESP32-S3 双核系统遥测:\r\n"
            "  CPU 架构     : Xtensa LX7 双核 @ 240MHz (Core 0 + Core 1)\r\n"
            "  FreeRTOS     : SMP 抢占式多任务内核 (Tick: 1000Hz)\r\n"
            "  系统剩余堆栈 : %u Bytes (SRAM)\r\n"
            "  固件版本号   : %s\r\n"
            "  Wi-Fi IP     : %s (模式: %s)\r\n"
            "  无线调试服务 : %s\r\n",
            static_cast<unsigned int>(esp_get_free_heap_size()),
            LINKNEXUS_FIRMWARE_VERSION,
            st.ip_str,
            (st.mode == WifiWorkMode::SoftAP) ? "主动 AP" : (st.mode == WifiWorkMode::Station) ? "从 AP (STA)" : "OFF",
            st.is_wireless_debug_active ? "TCP :8266 监听中" : "未启动");
    }
    else if (strcasecmp(token, "power") == 0) {
        char *ch_str = strtok(nullptr, " ");
        char *state_str = strtok(nullptr, " ");
        if (!ch_str || !state_str) {
            return snprintf(out_response, max_out_len, "语法: power <通道 0-5> <0|1> (例: power 0 1)\r\n");
        }
        uint8_t ch = static_cast<uint8_t>(atoi(ch_str));
        bool en = (atoi(state_str) != 0);
        bool ok = PowerManager::Instance().SetPower(ch, en);
        return snprintf(out_response, max_out_len, "[MOS] 通道 %u 供电状态已更新为: %s (%s)\r\n",
                        ch, en ? "导通 (ON)" : "隔离 (OFF)", ok ? "OK" : "FAILED");
    }
    else if (strcasecmp(token, "reboot") == 0) {
        snprintf(out_response, max_out_len, "[System] ESP32-S3 即将执行软复位重启...\r\n");
        // 延迟触发重启
        vTaskDelay(pdMS_TO_TICKS(100));
        esp_restart();
        return strlen(out_response);
    }
    else {
        return snprintf(out_response, max_out_len, "未识别指令 '%s'。输入 'help' 获取可用命令列表。\r\n", token);
    }
}

} // namespace LinkNexus

