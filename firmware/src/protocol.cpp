#include "protocol.hpp"
#include <string.h>
#include <algorithm>

namespace LinkNexus {

ProtocolParser::ProtocolParser()
    : m_state(State::WaitForHeader1), m_handler(nullptr)
{
    Reset();
}

ProtocolParser::ProtocolParser(FrameHandler handler)
    : m_state(State::WaitForHeader1), m_handler(handler)
{
    Reset();
}

void ProtocolParser::SetHandler(FrameHandler handler)
{
    m_handler = handler;
}

void ProtocolParser::Reset()
{
    m_state = State::WaitForHeader1;
    m_current_cmd = 0;
    m_expected_len = 0;
    m_payload_idx = 0;
    m_calc_checksum = 0;
    memset(m_payload, 0, sizeof(m_payload));
}

void ProtocolParser::Feed(const uint8_t *data, size_t len)
{
    if (!data || len == 0) return;

    for (size_t i = 0; i < len; ++i) {
        uint8_t b = data[i];

        switch (m_state) {
            case State::WaitForHeader1:
                if (b == PROTOCOL_HEADER1) {
                    m_state = State::WaitForHeader2;
                    m_calc_checksum = PROTOCOL_HEADER1;
                }
                break;

            case State::WaitForHeader2:
                if (b == PROTOCOL_HEADER2) {
                    m_state = State::WaitForCmd;
                    m_calc_checksum += PROTOCOL_HEADER2;
                } else if (b == PROTOCOL_HEADER1) {
                    // 连续 0xAA 容错维持
                    m_state = State::WaitForHeader2;
                    m_calc_checksum = PROTOCOL_HEADER1;
                } else {
                    m_state = State::WaitForHeader1;
                }
                break;

            case State::WaitForCmd:
                m_current_cmd = b;
                m_calc_checksum += b;
                m_state = State::WaitForLength;
                break;

            case State::WaitForLength:
                m_expected_len = b;
                m_calc_checksum += b;
                m_payload_idx = 0;
                if (m_expected_len == 0) {
                    m_state = State::WaitForChecksum;
                } else {
                    m_state = State::WaitForPayload;
                }
                break;

            case State::WaitForPayload:
                if (m_payload_idx < PROTOCOL_MAX_PAYLOAD_LEN) {
                    m_payload[m_payload_idx++] = b;
                }
                m_calc_checksum += b;

                if (m_payload_idx >= m_expected_len) {
                    m_state = State::WaitForChecksum;
                }
                break;

            case State::WaitForChecksum:
                if (b == m_calc_checksum) {
                    // 校验和匹配成功，触发有效帧派发
                    if (m_handler) {
                        m_handler(m_current_cmd, m_payload, m_expected_len);
                    }
                }
                // 无论是否匹配，重置状态等待下一帧
                m_state = State::WaitForHeader1;
                break;

            default:
                m_state = State::WaitForHeader1;
                break;
        }
    }
}

uint8_t ProtocolUtils::CalculateChecksum(uint8_t cmd, uint8_t len, const uint8_t *payload)
{
    uint32_t sum = PROTOCOL_HEADER1 + PROTOCOL_HEADER2 + cmd + len;
    if (payload && len > 0) {
        for (uint8_t i = 0; i < len; ++i) {
            sum += payload[i];
        }
    }
    return static_cast<uint8_t>(sum & 0xFF);
}

size_t ProtocolUtils::PackFrame(uint8_t cmd, const uint8_t *payload, uint8_t payload_len, uint8_t *out_buf)
{
    if (!out_buf) return 0;

    out_buf[0] = PROTOCOL_HEADER1;
    out_buf[1] = PROTOCOL_HEADER2;
    out_buf[2] = cmd;
    out_buf[3] = payload_len;

    uint32_t sum = PROTOCOL_HEADER1 + PROTOCOL_HEADER2 + cmd + payload_len;

    if (payload && payload_len > 0) {
        memcpy(&out_buf[4], payload, payload_len);
        for (uint8_t i = 0; i < payload_len; ++i) {
            sum += payload[i];
        }
    }

    out_buf[4 + payload_len] = static_cast<uint8_t>(sum & 0xFF);
    return static_cast<size_t>(5 + payload_len);
}

size_t ProtocolUtils::PackHeartbeatAck(uint8_t seq, uint8_t *out_buf)
{
    // 与 C# ProtocolEngine 对齐：返回 0x06 (ACK) 或序号回显
    uint8_t payload = 0x06;
    return PackFrame(static_cast<uint8_t>(ProtocolCmd::Heartbeat), &payload, 1, out_buf);
}

size_t ProtocolUtils::PackTelemetry(const TelemetryPayload &telemetry, uint8_t *out_buf)
{
    if (!out_buf) return 0;
    return PackFrame(static_cast<uint8_t>(ProtocolCmd::TelemetryReport),
                     reinterpret_cast<const uint8_t*>(&telemetry),
                     sizeof(TelemetryPayload),
                     out_buf);
}

size_t ProtocolUtils::PackPowerAck(uint8_t channel, bool state, uint8_t *out_buf)
{
    uint8_t payload[2];
    payload[0] = channel;
    payload[1] = state ? 0x01 : 0x00;
    return PackFrame(static_cast<uint8_t>(ProtocolCmd::PowerControl), payload, 2, out_buf);
}

bool ProtocolUtils::ParseScreenSync(const uint8_t *payload, uint8_t len, ScreenSyncInfo &out_info)
{
    if (!payload || len < 4) return false;

    out_info.active_channel = payload[0];
    out_info.flash_progress = payload[1];
    out_info.status_flags   = payload[2];
    out_info.name_len       = payload[3];

    size_t copy_len = out_info.name_len;
    if (copy_len > static_cast<size_t>(len - 4)) {
        copy_len = static_cast<size_t>(len - 4);
    }
    if (copy_len > 64) {
        copy_len = 64;
    }

    if (copy_len > 0) {
        memcpy(out_info.device_name, &payload[4], copy_len);
    }
    out_info.device_name[copy_len] = '\0';

    return true;
}

size_t ProtocolUtils::PackWifiStatus(uint8_t mode, uint8_t channel, int8_t rssi, uint8_t clients, uint16_t debug_port,
                                     const char *ssid, const char *ip_str, uint8_t *out_buf)
{
    if (!out_buf) return 0;

    uint8_t payload[128] = {0};
    payload[0] = mode;
    payload[1] = channel;
    payload[2] = static_cast<uint8_t>(rssi);
    payload[3] = clients;
    payload[4] = static_cast<uint8_t>(debug_port & 0xFF);
    payload[5] = static_cast<uint8_t>((debug_port >> 8) & 0xFF);

    uint8_t ssid_len = ssid ? static_cast<uint8_t>(strlen(ssid)) : 0;
    if (ssid_len > 32) ssid_len = 32;
    payload[6] = ssid_len;
    if (ssid_len > 0) {
        memcpy(&payload[7], ssid, ssid_len);
    }

    size_t ip_offset = 7 + ssid_len;
    uint8_t ip_len = ip_str ? static_cast<uint8_t>(strlen(ip_str)) : 0;
    if (ip_len > 15) ip_len = 15;
    payload[ip_offset] = ip_len;
    if (ip_len > 0) {
        memcpy(&payload[ip_offset + 1], ip_str, ip_len);
    }

    uint8_t total_payload_len = static_cast<uint8_t>(ip_offset + 1 + ip_len);
    return PackFrame(static_cast<uint8_t>(ProtocolCmd::WifiControl), payload, total_payload_len, out_buf);
}

} // namespace LinkNexus

