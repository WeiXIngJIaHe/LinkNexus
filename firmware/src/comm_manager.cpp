#include "comm_manager.hpp"
#include "linknexus_config.hpp"
#include "esp_log.h"

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

    m_initialized = true;
    ESP_LOGI(TAG, "UART0 communication driver initialized successfully.");
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

    int bytes_sent = uart_write_bytes(Pins::COMM_UART_PORT, reinterpret_cast<const char*>(frame_buf), frame_len);
    return bytes_sent > 0 ? static_cast<size_t>(bytes_sent) : 0;
}

int CommManager::ReadBytes(uint8_t *buf, size_t max_len, TickType_t wait_ticks)
{
    if (!buf || max_len == 0 || !m_initialized) return -1;
    return uart_read_bytes(Pins::COMM_UART_PORT, buf, max_len, wait_ticks);
}

} // namespace LinkNexus

