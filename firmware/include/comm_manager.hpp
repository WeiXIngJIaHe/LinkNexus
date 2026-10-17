#pragma once

#include <stdint.h>
#include <stddef.h>
#include "driver/uart.h"
#include "freertos_mutex.hpp"

namespace LinkNexus {

/**
 * @brief 通信硬件层与数据传输管理器
 * 负责 UART0 外设驱动、高速缓冲区管理以及线程安全的数据帧发送
 * 杜绝 Core 0 (周期遥测) 与 Core 1 (心跳/控制ACK) 并发写入串口导致的数据包字节交错
 */
class CommManager {
public:
    static CommManager& Instance();

    /**
     * @brief 初始化通信 UART 外设
     */
    void Init();

    /**
     * @brief 线程安全发送原始字节流数据帧
     * @param frame_buf 数据帧缓冲区
     * @param frame_len 数据帧字节数
     * @param force_usb 是否强制向原生 USB-Serial-JTAG 发送 (默认 false，智能拦截未握手的二进制包防终端乱码)
     * @return 实际成功写入字节数
     */
    size_t SendRawFrame(const uint8_t *frame_buf, size_t frame_len, bool force_usb = false);

    /**
     * @brief 从串口驱动环形缓冲区读取数据
     */
    int ReadBytes(uint8_t *buf, size_t max_len, TickType_t wait_ticks);

    /**
     * @brief 线程安全向串口打印格式化文本 (便于用户在串口助手或监控端实时观察)
     */
    size_t Printf(const char *fmt, ...);

    /**
     * @brief 线程安全向串口输出文本字符串
     */
    size_t Print(const char *str);

private:
    CommManager() = default;
    ~CommManager() = default;

    CommManager(const CommManager&) = delete;
    CommManager& operator=(const CommManager&) = delete;

    Mutex m_tx_mutex;
    bool m_initialized{false};
};

} // namespace LinkNexus

