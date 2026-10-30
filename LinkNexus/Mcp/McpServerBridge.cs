using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Mcp.Tools;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// Model Context Protocol (MCP) 服务器中枢与分发桥接器
    /// 预留给 LinkNexus 后续 AI Agent (Claude, Gemini, Cursor 等) 挂接与调度
    /// </summary>
    public class McpServerBridge
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string SerializeResponse(McpRpcResponse response) => JsonSerializer.Serialize(response, JsonOptions);

        private readonly ConcurrentDictionary<string, IMcpTool> _tools = new(StringComparer.OrdinalIgnoreCase);

        public McpServerBridge()
        {
            RegisterDefaultTools();
        }

        /// <summary>
        /// 注册单个 MCP 工具
        /// </summary>
        public void RegisterTool(IMcpTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
        }

        /// <summary>
        /// 注册 LinkNexus 预设的一组硬件调度核心工具
        /// </summary>
        private void RegisterDefaultTools()
        {
            RegisterTool(new ListDevicesTool());
            RegisterTool(new ReadTopologyStatusTool());
            RegisterTool(new SendSerialCommandTool());
            RegisterTool(new ExecuteEsp32CliTool());
            RegisterTool(new FlashKernelTool());
            RegisterTool(new ControlMosPowerTool());
            RegisterTool(new ReadLogsTool());
            RegisterTool(new PushBlueprintDataTool());
        }

        /// <summary>
        /// 绑定动态硬件数据源 (将 MainViewModel 中的实时状态与 MCP 联通)
        /// </summary>
        public void BindHardwareContext(
            Func<IEnumerable<PortDeviceModel>>? devicesProvider,
            Func<bool>? isEsp32OnlineProvider,
            Func<string>? daemonStatusTextProvider,
            Action<int, bool>? mosPowerSwitchHandler = null,
            Func<IEnumerable<LogMessageItem>>? logsProvider = null,
            Func<IEnumerable<string>>? serialLogsProvider = null,
            Func<string, int, Task<string>>? cliExecutor = null)
        {
            if (devicesProvider != null)
            {
                RegisterTool(new ListDevicesTool(devicesProvider));
            }
            if (isEsp32OnlineProvider != null || daemonStatusTextProvider != null)
            {
                RegisterTool(new ReadTopologyStatusTool(isEsp32OnlineProvider, daemonStatusTextProvider));
            }
            if (mosPowerSwitchHandler != null)
            {
                RegisterTool(new ControlMosPowerTool(mosPowerSwitchHandler));
            }
            if (logsProvider != null || serialLogsProvider != null)
            {
                RegisterTool(new ReadLogsTool(logsProvider, serialLogsProvider));
            }
            if (cliExecutor != null)
            {
                RegisterTool(new ExecuteEsp32CliTool(cliExecutor));
            }
        }

        /// <summary>
        /// 获取当前注册的所有 MCP 工具定义列表
        /// </summary>
        public IReadOnlyList<McpToolDefinition> GetRegisteredToolDefinitions()
        {
            return _tools.Values.Select(t => t.GetDefinition()).ToList();
        }

        /// <summary>
        /// 处理来自 AI Agent 的标准 JSON-RPC 2.0 请求并生成标准响应
        /// </summary>
        /// <param name="jsonRequest">标准 JSON-RPC 2.0 字符串</param>
        /// <param name="cancellationToken">取消标记</param>
        /// <returns>符合 MCP 标准的 JSON-RPC 响应字符串</returns>
        public async Task<string> HandleJsonRpcMessageAsync(string jsonRequest, CancellationToken cancellationToken = default)
        {
            McpRpcRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<McpRpcRequest>(jsonRequest);
            }
            catch (Exception ex)
            {
                var parseErr = McpRpcResponse.Fail(null, McpConstants.ErrorParseError, $"JSON 反序列化错误: {ex.Message}");
                return SerializeResponse(parseErr);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Method))
            {
                var invalidReq = McpRpcResponse.Fail(null, McpConstants.ErrorInvalidRequest, "请求无效，缺少 method 字段");
                return SerializeResponse(invalidReq);
            }

            try
            {
                switch (request.Method)
                {
                    case McpConstants.MethodInitialize:
                    {
                        var initResult = new McpInitializeResult();
                        return SerializeResponse(McpRpcResponse.Success(request.Id, initResult));
                    }

                    case McpConstants.MethodPing:
                    {
                        return SerializeResponse(McpRpcResponse.Success(request.Id, new { }));
                    }

                    case McpConstants.MethodToolsList:
                    {
                        var toolListResult = new McpToolListResult
                        {
                            Tools = GetRegisteredToolDefinitions().ToList()
                        };
                        return SerializeResponse(McpRpcResponse.Success(request.Id, toolListResult));
                    }

                    case McpConstants.MethodToolsCall:
                    {
                        if (request.Params == null || request.Params.Value.ValueKind != JsonValueKind.Object)
                        {
                            var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorInvalidParams, "tools/call 缺少合法的 params 参数");
                            return SerializeResponse(err);
                        }

                        var paramsElem = request.Params.Value;
                        if (!paramsElem.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
                        {
                            var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorInvalidParams, "tools/call 缺少工具名称 'name'");
                            return SerializeResponse(err);
                        }

                        string toolName = nameProp.GetString()!;
                        if (!_tools.TryGetValue(toolName, out var tool))
                        {
                            var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorMethodNotFound, $"未找到注册的 MCP 工具: '{toolName}'");
                            return SerializeResponse(err);
                        }

                        JsonElement argsElem = default;
                        if (paramsElem.TryGetProperty("arguments", out var aProp) && aProp.ValueKind == JsonValueKind.Object)
                        {
                            argsElem = aProp;
                        }

                        var callResult = await tool.ExecuteAsync(argsElem, cancellationToken);
                        return SerializeResponse(McpRpcResponse.Success(request.Id, callResult));
                    }

                    default:
                    {
                        var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorMethodNotFound, $"不支持的 MCP 方法: {request.Method}");
                        return SerializeResponse(err);
                    }
                }
            }
            catch (Exception ex)
            {
                var internalErr = McpRpcResponse.Fail(request.Id, McpConstants.ErrorInternalError, $"MCP 内部执行错误: {ex.Message}");
                return SerializeResponse(internalErr);
            }
        }
    }
}
