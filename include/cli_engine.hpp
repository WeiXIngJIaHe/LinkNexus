#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief LinkNexus ESP32-S3 原生交互式命令行 (CLI) 引擎
 * 基于乐鑫官方 esp_console 组件实现，提供标准化的指令注册、分词解析与执行。
 * 负责接收上位机 C# CLI 页面下发指令或无线 TCP 调试命令，
 * 支持 Wi-Fi 主动 AP/从 AP 配置、MOS 电源控制、系统状态遥测与软复位。
 */
class CliEngine {
public:
    static CliEngine& Instance();

    /**
     * @brief 初始化 esp_console 组件并注册系统指令
     */
    void Init();

    /**
     * @brief 执行一条 CLI 命令行并生成应答报文
     * @param cmd_line 输入的指令字符串 (如 "wifi ap MySSID 12345678", "power 1 1", "status")
     * @param out_response 输出应答字符串缓冲区
     * @param max_out_len 缓冲区最大长度
     * @return 实际写入应答缓冲区的字节数
     */
    size_t Execute(const char *cmd_line, char *out_response, size_t max_out_len);

    /**
     * @brief 写入当前 CLI 执行上下文的应答内容
     */
    static void PrintResponse(const char *fmt, ...);

private:
    CliEngine();
    ~CliEngine() = default;

    CliEngine(const CliEngine&) = delete;
    CliEngine& operator=(const CliEngine&) = delete;

    void RegisterCommands();

    bool m_initialized{false};
    Mutex m_mutex;
};

} // namespace LinkNexus

