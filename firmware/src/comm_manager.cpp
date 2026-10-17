#include "comm_manager.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"
#include "driver/usb_serial_jtag.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

namespace LinkNexus {

static const char *TAG = "COMM_MGR";

CommManager& CommManager::Instance()
{
    static CommManager instance;
    return instance;
}

void CommManager::Init()
{
    LockGuard lock(m_tx_mutex);
    if (m_initialized) return;

    ESP_LOGI(TAG, "Initializing UART0 communication driver (Baud: %lu)...", Pins::COMM_UART_BAUD);

    uart_config_t uart_config = {
        .baud_rate = static_cast<int>(Pins::COMM_UART_BAUD),
        .data_bits = UART_DATA_8_BITS,
        .parity    = UART_PARITY_DISABLE,
        .stop_bits = UART_STOP_BITS_1,
        .flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
        .rx_flow_ctrl_thresh = 0,
        .source_clk = UART_SCLK_DEFAULT,
        .flags = {
            .backup_before_sleep = 0
        }
    };

    ESP_ERROR_CHECK(uart_param_config(Pins::COMM_UART_PORT, &uart_config));
    ESP_ERROR_CHECK(uart_driver_install(Pins::COMM_UART_PORT,
                                        Pins::COMM_RX_BUF_SIZE,
                                        Pins::COMM_TX_BUF_SIZE,
                                        0, NULL, 0));

    // 初始化 ESP32-S3 原生 USB-Serial-JTAG (供直连 Type-C USB 端口的 ESP Decoder 与控制台终端通信)
    usb_serial_jtag_driver_config_t usj_config = USB_SERIAL_JTAG_DRIVER_CONFIG_DEFAULT();
    usj_config.rx_buffer_size = 1024;
    usj_config.tx_buffer_size = 1024;
    esp_err_t usj_ret = usb_serial_jtag_driver_install(&usj_config);
    if (usj_ret == ESP_OK) {
        ESP_LOGI(TAG, "USB-Serial-JTAG CDC driver installed successfully.");
    } else {
        ESP_LOGW(TAG, "USB-Serial-JTAG install returned: %d (may already be in use)", usj_ret);
    }

    m_initialized = true;
    ESP_LOGI(TAG, "Dual communication driver initialized successfully (UART0 & USB-Serial-JTAG).");
}

size_t CommManager::SendRawFrame(const uint8_t *frame_buf, size_t frame_len)
{
    if (!frame_buf || frame_len == 0 || !m_initialized) return 0;

    // 获取发送互斥锁，确保整帧数据原子性发送，防止双核并发写入导致字节交错粘包
    LockGuard lock(m_tx_mutex, pdMS_TO_TICKS(100));
    if (!lock.IsLocked()) {
        ESP_LOGE(TAG, "Failed to acquire UART TX mutex within 100ms!");
        return 0;
    }

    // 1. 发往 UART0 硬件引脚 (对齐 C# 上位机 LinkNexus 物理串口)
    int bytes_sent = uart_write_bytes(Pins::COMM_UART_PORT, reinterpret_cast<const char*>(frame_buf), frame_len);

    // 2. 同时镜像输出至 USB-Serial-JTAG 原生接口 (对齐直连 ESP32-S3 Type-C USB 端口的 ESP Decoder/终端)
    usb_serial_jtag_write_bytes(frame_buf, frame_len, pdMS_TO_TICKS(30));

    return bytes_sent > 0 ? static_cast<size_t>(bytes_sent) : 0;
}

int CommManager::ReadBytes(uint8_t *buf, size_t max_len, TickType_t wait_ticks)
{
    if (!buf || max_len == 0 || !m_initialized) return -1;

    // 1. 优先从 UART0 硬件端口读取
    int bytes_read = uart_read_bytes(Pins::COMM_UART_PORT, buf, max_len, wait_ticks);
    if (bytes_read > 0) {
        return bytes_read;
    }

    // 2. 若 UART0 暂无输入，从原生 USB-Serial-JTAG 端口无阻塞读取
    int usj_read = usb_serial_jtag_read_bytes(buf, max_len, 0);
    if (usj_read > 0) {
        return usj_read;
    }

    return 0;
}

size_t CommManager::Print(const char *str)
{
    if (!str || !m_initialized) return 0;
    return SendRawFrame(reinterpret_cast<const uint8_t*>(str), strlen(str));
}

size_t CommManager::Printf(const char *fmt, ...)
{
    if (!fmt || !m_initialized) return 0;
    char buf[512];
    va_list args;
    va_start(args, fmt);
    int len = vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    if (len > 0) {
        size_t actual_len = (len < static_cast<int>(sizeof(buf))) ? static_cast<size_t>(len) : sizeof(buf) - 1;
        return SendRawFrame(reinterpret_cast<const uint8_t*>(buf), actual_len);
    }
    return 0;
}

} // namespace LinkNexus

