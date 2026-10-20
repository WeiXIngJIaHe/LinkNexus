using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp.Tools
{
    /// <summary>
    /// MCP 工具: 在 ESP32-S3 下位机上执行原生交互式 CLI 命令并获取回显响应
    /// 支持 wifi connect, wifi status, power, telemetry, status, help 等系统控制指令
    /// </summary>
    public class ExecuteEsp32CliTool : IMcpTool
    {
        private readonly Func<string, int, Task<string>>? _cliExecutor;

        public ExecuteEsp32CliTool(Func<string, int, Task<string>>? cliExecutor = null)
        {
            _cliExecutor = cliExecutor;
        }

        public string Name => "esp32_cli";

        public string Description => "在 ESP32-S3 下位机上执行原生交互式 CLI 命令并获取回显 (如 'wifi connect <ssid> <pwd>', 'wifi status', 'power status', 'status', 'help')";

        public McpToolDefinition GetDefinition()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = new
                {
                    type = "object",
                    required = new[] { "command" },
                    properties = new
                    {
                        command = new
                        {
                            type = "string",
                            description = "要在 ESP32-S3 下位机执行的 CLI 命令文本 (例如 'wifi connect JiaHe 12345678', 'wifi status', 'power status', 'status', 'help')"
                        },
                        timeoutMs = new
                        {
                            type = "integer",
                            description = "等待下位机执行与回显的超时毫秒数 (默认为 5000ms，Wi-Fi 连接建议 4000~8000ms)"
                        }
                    }
                }
            };
        }

        public async Task<McpToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            if (!arguments.TryGetProperty("command", out var cmdProp) || string.IsNullOrWhiteSpace(cmdProp.GetString()))
            {
                return McpToolCallResult.Text("缺少必要参数 'command' (CLI 命令行文本)", isError: true);
            }

            string command = cmdProp.GetString()!.Trim();
            int timeoutMs = arguments.TryGetProperty("timeoutMs", out var timeoutProp) ? timeoutProp.GetInt32() : 5000;
            if (timeoutMs < 500) timeoutMs = 500;
            if (timeoutMs > 30000) timeoutMs = 30000;

            try
            {
                string response;
                if (_cliExecutor != null)
                {
                    response = await _cliExecutor(command, timeoutMs);
                }
                else
                {
                    response = await Esp32CommService.Instance.ExecuteCliCommandAsync(command, timeoutMs);
                }

                bool isError = response.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase) ||
                               response.StartsWith("[Timeout]", StringComparison.OrdinalIgnoreCase);

                return McpToolCallResult.Text(response, isError);
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Text($"[MCP] 执行 ESP32 CLI 命令异常: {ex.Message}", isError: true);
            }
        }
    }
}
