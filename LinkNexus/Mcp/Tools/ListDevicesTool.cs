using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 枚举 LinkNexus 当前已连接的物理/虚拟硬件外设
    /// </summary>
    public class ListDevicesTool : IMcpTool
    {
        private readonly Func<IEnumerable<PortDeviceModel>>? _devicesProvider;

        public ListDevicesTool(Func<IEnumerable<PortDeviceModel>>? devicesProvider = null)
        {
            _devicesProvider = devicesProvider;
        }

        public string Name => "list_devices";

        public string Description => "枚举 Windows 系统当前已识别连接的全部硬件调试器、转串口器及 USB 外设列表 (包括 VID:PID, COM 端口, 序列号与工作状态)";

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
                        includeOffline = new
                        {
                            type = "boolean",
                            description = "是否包含离线/断开连接的历史设备 (默认为 false)"
                        }
                    }
                }
            };
        }

        public Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                bool includeOffline = false;
                if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("includeOffline", out var prop))
                {
                    includeOffline = prop.GetBoolean();
                }

                var rawDevices = _devicesProvider?.Invoke() ?? Enumerable.Empty<PortDeviceModel>();
                var filtered = rawDevices.Where(d => includeOffline || d.State != DeviceState.Unplugged).ToList();

                var payload = filtered.Select(d => new
                {
                    name = d.DeviceName,
                    functionBadge = d.FunctionBadgeText,
                    comPort = d.ComPort,
                    vidPid = d.VidPid,
                    serialNumber = d.SerialNumber,
                    functionType = d.FunctionType.ToString(),
                    state = d.State.ToString(),
                    isVirtual = d.IsVirtual
                });

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                });
                return Task.FromResult(McpToolCallResult.Text(json));
            }
            catch (Exception ex)
            {
                return Task.FromResult(McpToolCallResult.Text($"枚举硬件设备失败: {ex.Message}", isError: true));
            }
        }
    }
}

