using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 拉取系统运行流水日志或从指定物理 COM 串口 (如当前接入的 MCU) 实时读取数据日志
    /// </summary>
    public class ReadLogsTool : IMcpTool
    {
        private readonly Func<IEnumerable<LogMessageItem>>? _systemLogsProvider;
        private readonly Func<IEnumerable<string>>? _serialLogsProvider;

        public ReadLogsTool(
            Func<IEnumerable<LogMessageItem>>? systemLogsProvider = null,
            Func<IEnumerable<string>>? serialLogsProvider = null)
        {
            _systemLogsProvider = systemLogsProvider;
            _serialLogsProvider = serialLogsProvider;
        }

        public string Name => "read_logs";

        public string Description => "拉取 LinkNexus 系统运行日志、PnP 硬件变动审计日志，或从指定 COM 物理串口 (如接入的 MCU 设备 COM8) 实时拉取最新数据与日志行";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new
                        {
                            type = "string",
                            description = "日志类别: 'system' (仅系统日志), 'serial' (仅串口/MCU输出日志), 'all' (合并拉取全部日志，默认)",
                            @enum = new[] { "system", "serial", "all" }
                        },
                        portName = new
                        {
                            type = "string",
                            description = "目标物理串口端口号 (如 COM8，留空则自动识别当前已连接的 MCU / 串口设备)"
                        },
                        limit = new
                        {
                            type = "integer",
                            description = "获取的最大日志条数 (默认 50，范围 1~500)"
                        },
                        baudRate = new
                        {
                            type = "integer",
                            description = "读取物理串口时的波特率 (默认 115200)"
                        },
                        readTimeoutMs = new
                        {
                            type = "integer",
                            description = "物理串口采样读取的最大超时毫秒数 (默认 800ms，最高限制 2000ms，确保即时响应不卡顿)"
                        }
                    }
                }
            };
        }

        public async Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                string category = "all";
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("category", out var catProp) && !string.IsNullOrWhiteSpace(catProp.GetString()))
                {
                    category = catProp.GetString()!.Trim().ToLowerInvariant();
                }

                string? portName = null;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("portName", out var portProp) && !string.IsNullOrWhiteSpace(portProp.GetString()))
                {
                    portName = portProp.GetString()!.Trim().ToUpperInvariant();
                }

                int limit = 50;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var limitProp) && limitProp.GetInt32() > 0)
                {
                    limit = Math.Clamp(limitProp.GetInt32(), 1, 500);
                }

                int baudRate = 115200;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("baudRate", out var baudProp) && baudProp.GetInt32() > 0)
                {
                    baudRate = baudProp.GetInt32();
                }

                int readTimeoutMs = 800;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("readTimeoutMs", out var timeoutProp) && timeoutProp.GetInt32() > 0)
                {
                    readTimeoutMs = Math.Clamp(timeoutProp.GetInt32(), 100, 2000); // 严格限制在 2 秒以内，杜绝超过 30 秒卡顿
                }

                bool wantSystem = category == "system" || category == "all";
                bool wantSerial = category == "serial" || category == "all";

                var resultObj = new Dictionary<string, object>();
                resultObj["status"] = "success";
                resultObj["category"] = category;
                resultObj["queryTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                // 1. 获取系统运行流水日志
                if (wantSystem)
                {
                    var sysLogs = _systemLogsProvider?.Invoke()?.ToList() ?? new List<LogMessageItem>();
                    var recentSys = sysLogs.TakeLast(limit).Select(item => new
                    {
                        time = item.TimeString,
                        level = item.Level,
                        message = item.Message
                    }).ToList();

                    resultObj["systemLogsCount"] = recentSys.Count;
                    resultObj["systemLogs"] = recentSys;
                }

                // 2. 获取串口 / MCU 输出日志
                if (wantSerial)
                {
                    var serialLines = new List<string>();

                    // 优先从内存缓存中拉取
                    var memoryLines = _serialLogsProvider?.Invoke()?.ToList();
                    if (memoryLines != null && memoryLines.Count > 0)
                    {
                        serialLines.AddRange(memoryLines.TakeLast(limit));
                    }

                    // 如果未指定端口且内存中无数据，自动探测当前物理系统上的 COM 端口
                    if (string.IsNullOrWhiteSpace(portName))
                    {
                        var availablePorts = SerialPort.GetPortNames();
                        if (availablePorts.Length > 0)
                        {
                            // 优先选 COM8 或第一个可用端口
                            portName = availablePorts.FirstOrDefault(p => p.Equals("COM8", StringComparison.OrdinalIgnoreCase)) ?? availablePorts[0];
                        }
                    }

                    string serialSamplingStatus = "not_requested";

                    // 若目标端口有效，尝试安全采样物理串口最新实时数据
                    if (!string.IsNullOrWhiteSpace(portName))
                    {
                        resultObj["targetPort"] = portName;
                        resultObj["baudRate"] = baudRate;

                        var sampledLines = await SamplePhysicalSerialPortAsync(portName, baudRate, readTimeoutMs, limit, cancellationToken);
                        if (sampledLines.Count > 0)
                        {
                            serialSamplingStatus = "active_sampled";
                            // 合并并去重末尾数据
                            foreach (var line in sampledLines)
                            {
                                if (!serialLines.Contains(line))
                                {
                                    serialLines.Add(line);
                                }
                            }
                        }
                        else
                        {
                            serialSamplingStatus = "port_idle_or_in_use";
                        }
                    }

                    resultObj["serialSamplingStatus"] = serialSamplingStatus;
                    resultObj["serialLogsCount"] = serialLines.TakeLast(limit).Count();
                    resultObj["serialLogs"] = serialLines.TakeLast(limit).ToList();
                }

                string json = JsonSerializer.Serialize(resultObj, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                });

                return McpToolCallResult.Text(json);
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Text($"拉取日志失败: {ex.Message}", isError: true);
            }
        }

        /// <summary>
        /// 从物理 COM 端口安全采样一段实时日志，严格受控于超时限制
        /// </summary>
        private static async Task<List<string>> SamplePhysicalSerialPortAsync(
            string portName, int baudRate, int timeoutMs, int maxLines, CancellationToken cancellationToken)
        {
            var lines = new List<string>();

            await Task.Run(() =>
            {
                SerialPort? port = null;
                try
                {
                    port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = Math.Min(timeoutMs, 500),
                        WriteTimeout = 500
                    };
                    port.Open();

                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    var sb = new StringBuilder();
                    var buffer = new byte[1024];

                    while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                    {
                        if (port.BytesToRead > 0)
                        {
                            int count = port.Read(buffer, 0, Math.Min(buffer.Length, port.BytesToRead));
                            if (count > 0)
                            {
                                string text = Encoding.UTF8.GetString(buffer, 0, count);
                                // 过滤掉裸 ASCII 空字符
                                text = text.Replace("\0", "");
                                sb.Append(text);
                            }
                        }
                        else
                        {
                            Thread.Sleep(25);
                        }

                        // 如果已有换行且行数达到上限则提前结束
                        if (sb.ToString().Count(c => c == '\n') >= maxLines)
                        {
                            break;
                        }
                    }

                    string fullRaw = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(fullRaw))
                    {
                        var rawSplits = fullRaw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var rawLine in rawSplits)
                        {
                            string cleaned = rawLine.Trim();
                            if (!string.IsNullOrEmpty(cleaned))
                            {
                                lines.Add(cleaned);
                            }
                        }
                    }
                }
                catch
                {
                    // 若串口已被占用 (如 GUI 正在打开) 或端口不可用，平稳吞并异常并返回空列表
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

            return lines;
        }
    }
}

