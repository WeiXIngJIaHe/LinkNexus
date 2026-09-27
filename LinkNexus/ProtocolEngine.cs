using System;
using System.Collections.Generic;
using System.Text;

namespace LinkNexus
{
    /// <summary>
    /// 下位机通信协议命令字定义
    /// </summary>
    public static class ProtocolCommand
    {
        /// <summary>
        /// 0x01: 握手与心跳包
        /// </summary>
        public const byte Heartbeat = 0x01;

        /// <summary>
        /// 0x10: I2C 采样数据上报 (3路 INA236)
        /// </summary>
        public const byte TelemetryReport = 0x10;

        /// <summary>
        /// 0x20: 板载 1.9 寸 TFT 屏幕状态同步
        /// </summary>
        public const byte ScreenSync = 0x20;

        /// <summary>
        /// 0x30: 通道供电控制开关
        /// </summary>
        public const byte PowerControl = 0x30;
    }

    /// <summary>
    /// INA236 采样数据包解析结果
    /// </summary>
    public class TelemetryPacketData
    {
        public double BusVoltage { get; set; }     // 总线电压 (V)
        public double BusCurrent { get; set; }     // 总线电流 (mA)
        public double BusPower => BusVoltage * (BusCurrent / 1000.0); // 总功耗 (W)

        public double XdsVoltage { get; set; }     // XDS110 电压 (V)
        public double XdsCurrent { get; set; }     // XDS110 电流 (mA)
        public double XdsPower => XdsVoltage * (XdsCurrent / 1000.0);

        public double DapVoltage { get; set; }     // DAPLink 电压 (V)
        public double DapCurrent { get; set; }     // DAPLink 电流 (mA)
        public double DapPower => DapVoltage * (DapCurrent / 1000.0);

        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 二进制帧数据包解包与封包核心引擎
    /// 帧结构：[0xAA 0x55] + [命令字: 1B] + [长度: 1B] + [有效载荷 Payload: N 字节] + [校验和: 1B]
    /// 校验和：前序所有字节累加和 (0xAA + 0x55 + Cmd + Len + Sum(Payload)) & 0xFF
    /// </summary>
    public class ProtocolEngine
    {
        public const byte Header1 = 0xAA;
        public const byte Header2 = 0x55;

        // 解包状态机枚举
        private enum ParseState
        {
            WaitForHeader1,
            WaitForHeader2,
            WaitForCmd,
            WaitForLength,
            WaitForPayload,
            WaitForChecksum
        }

        private ParseState _state = ParseState.WaitForHeader1;
        private byte _currentCmd = 0;
        private byte _expectedLength = 0;
        private readonly List<byte> _payloadBuffer = new(256);
        private byte _calculatedChecksum = 0;

        /// <summary>
        /// 成功解析出一帧完整数据时的事件分发
        /// </summary>
        public event Action<byte, byte[]>? FrameReceived;

        /// <summary>
        /// 打包一帧通用协议数据
        /// </summary>
        public static byte[] PackFrame(byte cmd, byte[]? payload = null)
        {
            payload ??= Array.Empty<byte>();
            int payloadLen = Math.Min(payload.Length, 255);
            byte[] frame = new byte[4 + payloadLen + 1];

            frame[0] = Header1;
            frame[1] = Header2;
            frame[2] = cmd;
            frame[3] = (byte)payloadLen;

            byte checksum = (byte)(Header1 + Header2 + cmd + payloadLen);

            if (payloadLen > 0)
            {
                Buffer.BlockCopy(payload, 0, frame, 4, payloadLen);
                for (int i = 0; i < payloadLen; i++)
                {
                    checksum += payload[i];
                }
            }

            frame[frame.Length - 1] = checksum;
            return frame;
        }

        /// <summary>
        /// 构建 0x01 心跳与探测包
        /// </summary>
        public static byte[] BuildHeartbeatPacket(byte sequence = 0)
        {
            return PackFrame(ProtocolCommand.Heartbeat, new byte[] { sequence });
        }

        /// <summary>
        /// 构建 0x30 通道供电控制数据包
        /// </summary>
        /// <param name="channelIndex">通道索引 (0: XDS110, 1: DAPLink, 2: FT2232, 3: UART, 4: EXT1, 5: EXT2)</param>
        /// <param name="enable">true: 导通供电, false: 关断隔离</param>
        public static byte[] BuildPowerControlPacket(byte channelIndex, bool enable)
        {
            return PackFrame(ProtocolCommand.PowerControl, new byte[] { channelIndex, (byte)(enable ? 0x01 : 0x00) });
        }

        /// <summary>
        /// 构建 0x20 屏幕同步包（反向推送给 ESP32-S3 的 1.9 寸 ST7789V3 液晶屏）
        /// </summary>
        /// <param name="activeChannelIndex">当前上位机激活选中的卡片通道索引</param>
        /// <param name="flashProgress">固件烧录进度 (0 ~ 100)</param>
        /// <param name="deviceName">当前端口插入的外设友好名称</param>
        /// <param name="isFlashing">是否正在烧录</param>
        public static byte[] BuildScreenSyncPacket(byte activeChannelIndex, byte flashProgress, string deviceName, bool isFlashing = false)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(deviceName ?? string.Empty);
            int nameLen = Math.Min(nameBytes.Length, 64); // 限制设备名长度

            // 载荷定义：[激活通道 1B] + [烧录进度 1B] + [状态标记 1B] + [名称长度 1B] + [设备名称字符串 N 字节]
            byte statusFlags = (byte)(isFlashing ? 0x01 : 0x00);
            byte[] payload = new byte[4 + nameLen];

            payload[0] = activeChannelIndex;
            payload[1] = Math.Clamp(flashProgress, (byte)0, (byte)100);
            payload[2] = statusFlags;
            payload[3] = (byte)nameLen;

            if (nameLen > 0)
            {
                Buffer.BlockCopy(nameBytes, 0, payload, 4, nameLen);
            }

            return PackFrame(ProtocolCommand.ScreenSync, payload);
        }

        /// <summary>
        /// 解析 0x10 I2C 采样上报数据
        /// 协议定义：3 组定长 INA236 数据 (Bus, XDS110, DAPLink)
        /// 每组 4 字节：电压 (uint16_t mV) + 电流 (int16_t mA) -> 共 12 字节
        /// </summary>
        public static TelemetryPacketData? ParseTelemetryPayload(byte[] payload)
        {
            if (payload == null || payload.Length < 12)
            {
                return null;
            }

            try
            {
                ushort busMv = BitConverter.ToUInt16(payload, 0);
                short busMa = BitConverter.ToInt16(payload, 2);

                ushort xdsMv = BitConverter.ToUInt16(payload, 4);
                short xdsMa = BitConverter.ToInt16(payload, 6);

                ushort dapMv = BitConverter.ToUInt16(payload, 8);
                short dapMa = BitConverter.ToInt16(payload, 10);

                return new TelemetryPacketData
                {
                    BusVoltage = busMv / 1000.0,
                    BusCurrent = busMa,
                    XdsVoltage = xdsMv / 1000.0,
                    XdsCurrent = xdsMa,
                    DapVoltage = dapMv / 1000.0,
                    DapCurrent = dapMa,
                    Timestamp = DateTime.Now
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 流式状态机逐字节接收与解包，防粘包、抗断包
        /// </summary>
        public void ParseBytes(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[offset + i];

                switch (_state)
                {
                    case ParseState.WaitForHeader1:
                        if (b == Header1)
                        {
                            _state = ParseState.WaitForHeader2;
                            _calculatedChecksum = Header1;
                        }
                        break;

                    case ParseState.WaitForHeader2:
                        if (b == Header2)
                        {
                            _state = ParseState.WaitForCmd;
                            _calculatedChecksum += Header2;
                        }
                        else if (b == Header1)
                        {
                            // 连续两个 0xAA，继续保持等待 0x55
                            _state = ParseState.WaitForHeader2;
                            _calculatedChecksum = Header1;
                        }
                        else
                        {
                            _state = ParseState.WaitForHeader1;
                        }
                        break;

                    case ParseState.WaitForCmd:
                        _currentCmd = b;
                        _calculatedChecksum += b;
                        _state = ParseState.WaitForLength;
                        break;

                    case ParseState.WaitForLength:
                        _expectedLength = b;
                        _calculatedChecksum += b;
                        _payloadBuffer.Clear();

                        if (_expectedLength == 0)
                        {
                            _state = ParseState.WaitForChecksum;
                        }
                        else
                        {
                            _state = ParseState.WaitForPayload;
                        }
                        break;

                    case ParseState.WaitForPayload:
                        _payloadBuffer.Add(b);
                        _calculatedChecksum += b;

                        if (_payloadBuffer.Count >= _expectedLength)
                        {
                            _state = ParseState.WaitForChecksum;
                        }
                        break;

                    case ParseState.WaitForChecksum:
                        if (b == _calculatedChecksum)
                        {
                            // 校验通过，派发合法数据帧
                            FrameReceived?.Invoke(_currentCmd, _payloadBuffer.ToArray());
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[ProtocolEngine] 校验和错误: 期望 0x{_calculatedChecksum:X2}, 收到 0x{b:X2}");
                        }
                        // 复位状态机等待下一帧
                        _state = ParseState.WaitForHeader1;
                        break;
                }
            }
        }

        /// <summary>
        /// 复位状态机
        /// </summary>
        public void Reset()
        {
            _state = ParseState.WaitForHeader1;
            _payloadBuffer.Clear();
            _calculatedChecksum = 0;
        }
    }
}

