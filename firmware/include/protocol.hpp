#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <functional>

namespace LinkNexus {

/**
 * @brief 协议关键常量定义 (与 C# ProtocolEngine.cs 严格二进制对齐)
 */
constexpr uint8_t PROTOCOL_HEADER1         = 0xAA;
constexpr uint8_t PROTOCOL_HEADER2         = 0x55;
constexpr size_t  PROTOCOL_MAX_PAYLOAD_LEN = 255;

/**
 * @brief 下位机通信协议命令字
 */
enum class ProtocolCmd : uint8_t {
    Heartbeat        = 0x01,  ///< 0x01: 握手与心跳包
    TelemetryReport  = 0x10,  ///< 0x10: I2C 采样数据上报 (3路 INA236, 定长12字节)
    ScreenSync       = 0x20,  ///< 0x20: 板载 1.9 寸 TFT 屏幕状态同步
    PowerControl     = 0x30,  ///< 0x30: 通道供电控制开关
    WifiControl      = 0x40,  ///< 0x40: Wi-Fi 模式与无线调试配置
    CliTransfer      = 0x50,  ///< 0x50: ESP32-S3 原生交互式命令行 CLI 数据流
};

/**
 * @brief INA236 遥测数据包 (12字节定长，小端序，与 C# BitConverter 完全对齐)
 */
#pragma pack(push, 1)
struct TelemetryPayload {
    uint16_t bus_voltage_mv;   ///< 总线母线电压 (mV)
    int16_t  bus_current_ma;   ///< 总线母线电流 (mA)
    uint16_t xds_voltage_mv;   ///< XDS110 电压 (mV)
    int16_t  xds_current_ma;   ///< XDS110 电流 (mA)
    uint16_t dap_voltage_mv;   ///< DAPLink 电压 (mV)
    int16_t  dap_current_ma;   ///< DAPLink 电流 (mA)
};
#pragma pack(pop)

/**
 * @brief 屏幕状态同步信息结构
 */
struct ScreenSyncInfo {
    uint8_t active_channel{0};    ///< 当前上位机选中的通道 (0: XDS110, 1: DAPLink, 2: FT2232, 3: UART, 4: EXT1, 5: EXT2)
    uint8_t flash_progress{0};    ///< 固件烧录进度 0~100 %
    uint8_t status_flags{0};      ///< 状态标记 (Bit0: 正在烧录, Bit1: 设备在线)
    uint8_t name_len{0};          ///< 设备名称长度
    char    device_name[65]{0};   ///< 设备名称 UTF-8 字符串 (带 NULL 终止)
};

/**
 * @brief 数据帧分发处理回调函数类型
 */
using FrameHandler = std::function<void(uint8_t cmd, const uint8_t *payload, uint8_t len)>;

/**
 * @brief 流式防粘包协议解析状态机
 */
class ProtocolParser {
public:
    enum class State {
        WaitForHeader1,
        WaitForHeader2,
        WaitForCmd,
        WaitForLength,
        WaitForPayload,
        WaitForChecksum
    };

    ProtocolParser();
    explicit ProtocolParser(FrameHandler handler);

    void SetHandler(FrameHandler handler);
    void Reset();
    bool IsParsingFrame() const { return m_state != State::WaitForHeader1; }
    bool FeedByte(uint8_t b);
    void Feed(const uint8_t *data, size_t len);

private:
    State m_state{State::WaitForHeader1};
    uint8_t m_current_cmd{0};
    uint8_t m_expected_len{0};
    uint8_t m_payload_idx{0};
    uint8_t m_payload[PROTOCOL_MAX_PAYLOAD_LEN]{0};
    uint8_t m_calc_checksum{0};
    FrameHandler m_handler{nullptr};
};

/**
 * @brief 协议编解码通用工具类
 */
class ProtocolUtils {
public:
    /**
     * @brief 计算协议帧累加和校验码
     */
    static uint8_t CalculateChecksum(uint8_t cmd, uint8_t len, const uint8_t *payload);

    /**
     * @brief 打包通用二进制数据帧
     * @param cmd 命令字
     * @param payload 载荷指针
     * @param payload_len 载荷字节数
     * @param out_buf 输出缓冲区 (至少 5 + payload_len 字节)
     * @return 实际组帧长度
     */
    static size_t PackFrame(uint8_t cmd, const uint8_t *payload, uint8_t payload_len, uint8_t *out_buf);

    /**
     * @brief 组装心跳应答帧 (Cmd: 0x01, Payload: 0x06 ACK)
     */
    static size_t PackHeartbeatAck(uint8_t seq, uint8_t *out_buf);

    /**
     * @brief 组装 3 路 INA236 遥测上报帧 (Cmd: 0x10, Payload: 12字节)
     */
    static size_t PackTelemetry(const TelemetryPayload &telemetry, uint8_t *out_buf);

    /**
     * @brief 组装通道供电控制应答帧 (Cmd: 0x30, Payload: [Channel, State])
     */
    static size_t PackPowerAck(uint8_t channel, bool state, uint8_t *out_buf);

    /**
     * @brief 解析 0x20 屏幕同步包有效载荷
     */
    static bool ParseScreenSync(const uint8_t *payload, uint8_t len, ScreenSyncInfo &out_info);

    /**
     * @brief 组装 0x40 Wi-Fi 状态响应帧 (与 C# ParseWifiStatusPayload 完全对齐)
     */
    static size_t PackWifiStatus(uint8_t mode, uint8_t channel, int8_t rssi, uint8_t clients, uint16_t debug_port,
                                const char *ssid, const char *ip_str, uint8_t *out_buf);
};

} // namespace LinkNexus

