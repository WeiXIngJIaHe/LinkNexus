using System;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 向指定串口发送文本或 HEX 字节指令并接收返回数据
    /// </summary>
    public class SendSerialCommandTool : IMcpTool
    {
        public string Name => "send_serial_command";

        public string Description => "向指定的 Windows COM 物理串口发送文本指令或 HEX 字节帧，并可选等待接收设备回显响应";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    required = new[] { "portName", "payload" },
                    properties = new
                    {
                        portName = new
                        {
                            type = "string",
                            description = "目标串口端口名称 (如 COM3, COM4)"
                        },
                        payload = new
                        {
                            type = "string",
                            description = "发送数据载荷 (如 'help\\n' 或 HEX 序列 'AA BB 01 02')"
                        },
                        isHex = new
                        {
                            type = "boolean",
                            description = "是否以十六进制 HEX 格式解析并发送字节流 (默认为 false)"
                        },
                        baudRate = new
                        {
                            type = "integer",
                            description = "串口波特率 (默认为 115200)"
                        },
                        timeoutMs = new
                        {
                            type = "integer",
                            description = "等待接收回显的最大超时毫秒数 (默认为 500ms，设为 0 表示不等待)"
                        }
                    }
                }
            };
        }

        public async Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (!arguments.TryGetProperty("portName", out var portProp) || string.IsNullOrWhiteSpace(portProp.GetString()))
            {
                return McpToolCallResult.Text("缺少必要参数 'portName'", isError: true);
            }

            if (!arguments.TryGetProperty("payload", out var payloadProp))
            {
                return McpToolCallResult.Text("缺少必要参数 'payload'", isError: true);
            }

            string portName = portProp.GetString()!.Trim().ToUpperInvariant();
            string payload = payloadProp.GetString() ?? string.Empty;

            bool isHex = arguments.TryGetProperty("isHex", out var hexProp) && hexProp.GetBoolean();
            int baudRate = arguments.TryGetProperty("baudRate", out var baudProp) ? baudProp.GetInt32() : 115200;
            int timeoutMs = arguments.TryGetProperty("timeoutMs", out var timeoutProp) ? timeoutProp.GetInt32() : 500;

            return await Task.Run(() =>
            {
                SerialPort? port = null;
                try
                {
                    port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = Math.Max(50, timeoutMs),
                        WriteTimeout = 1000
                    };
                    port.Open();

                    byte[] dataBytes;
                    if (isHex)
                    {
                        dataBytes = ParseHexBytes(payload);
                    }
                    else
                    {
                        // 统一换行符转换
                        string textToSend = payload.Replace("\\r", "\r").Replace("\\n", "\n");
                        dataBytes = Encoding.UTF8.GetBytes(textToSend);
                    }

                    port.Write(dataBytes, 0, dataBytes.Length);

                    if (timeoutMs <= 0)
                    {
                        return McpToolCallResult.Text($"已向 {portName} 发送 {dataBytes.Length} 字节 (无需等待回显)");
                    }

                    // 等待响应流
                    var sb = new StringBuilder();
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    var buf = new byte[1024];

                    while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                    {
                        if (port.BytesToRead > 0)
                        {
                            int count = port.Read(buf, 0, Math.Min(buf.Length, port.BytesToRead));
                            if (count > 0)
                            {
                                sb.Append(Encoding.UTF8.GetString(buf, 0, count));
                            }
                        }
                        else
                        {
                            Thread.Sleep(20);
                        }
                    }

                    string rxResult = sb.ToString();
                    return McpToolCallResult.Text(string.IsNullOrEmpty(rxResult)
                        ? $"已向 {portName} 发送 {dataBytes.Length} 字节，但在 {timeoutMs}ms 内未收到回显"
                        : $"成功向 {portName} 发送并接收到数据:\n{rxResult}");
                }
                catch (Exception ex)
                {
                    return McpToolCallResult.Text($"串口操作失败 ({portName}): {ex.Message}", isError: true);
                }
                finally
                {
                    if (port != null && port.IsOpen)
                    {
                        try { port.Close(); } catch { }
                    }
                    port?.Dispose();
                }
            }, cancellationToken);
        }

        private static byte[] ParseHexBytes(string hexString)
        {
            string clean = hexString.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            if (clean.Length % 2 != 0)
            {
                clean = "0" + clean;
            }

            var bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}

