using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LinkNexus.Blueprint.Core;
using LinkNexus.Blueprint.Nodes;
using LinkNexus.Blueprint.ViewModels;
using LinkNexus.Mcp.Tools;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// Model Context Protocol (MCP) 服务器中枢与分发桥接器
    /// 支持将整个流式蓝图共享为 MCP Resource 与 Tool 数据，供 AI Agent 自动化摆放节点/连线、读取 MCP 节点采样分析及安全调优外部 Python 算法参数
    /// </summary>
    public class McpServerBridge
    {
        public const string ResourceBlueprintGraphUri = "linknexus://blueprint/graph";
        public const string ResourceMcpNodesUri = "linknexus://blueprint/mcp-nodes";
        public const string ResourcePythonCatalogUri = "linknexus://python-repo/catalog";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions IndentedJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static string SerializeResponse(McpRpcResponse response) => JsonSerializer.Serialize(response, JsonOptions);

        private readonly ConcurrentDictionary<string, IMcpTool> _tools = new(StringComparer.OrdinalIgnoreCase);
        private BlueprintWorkspaceViewModel? _blueprintVm;
        private Dispatcher? _uiDispatcher;

        public McpServerBridge()
        {
            RegisterDefaultTools();
        }

        public void RegisterTool(IMcpTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
        }

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
            RegisterTool(new BlueprintGetStateTool());
            RegisterTool(new BlueprintOrchestrateTool());
            RegisterTool(new BlueprintAnalyzeMcpNodeTool());
            RegisterTool(new BlueprintTunePythonParamsTool());
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
        /// 绑定流式蓝图工作台上下文，使整个蓝图画布、MCP 采样节点与外部 Python 参数调优全量接入 MCP 服务
        /// </summary>
        public void BindBlueprintContext(BlueprintWorkspaceViewModel blueprintVm, Dispatcher uiDispatcher)
        {
            _blueprintVm = blueprintVm ?? throw new ArgumentNullException(nameof(blueprintVm));
            _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

            RegisterTool(new BlueprintGetStateTool(_blueprintVm, _uiDispatcher));
            RegisterTool(new BlueprintOrchestrateTool(_blueprintVm, _uiDispatcher));
            RegisterTool(new BlueprintAnalyzeMcpNodeTool(_blueprintVm, _uiDispatcher));
            RegisterTool(new BlueprintTunePythonParamsTool(_blueprintVm, _uiDispatcher));
        }

        public IReadOnlyList<McpToolDefinition> GetRegisteredToolDefinitions()
        {
            return _tools.Values.Select(t => t.GetDefinition()).ToList();
        }

        public IReadOnlyList<McpResourceDefinition> GetRegisteredResources()
        {
            return new List<McpResourceDefinition>
            {
                new McpResourceDefinition
                {
                    Uri = ResourceBlueprintGraphUri,
                    Name = "LinkNexus 流式蓝图全量拓扑图 (Blueprint Graph)",
                    Description = "实时共享蓝图画布上的全部节点、二维坐标、输入/输出端口、连线拓扑、真实 USB 枚举端口、固件仓与外部隔离 Python 算法白名单",
                    MimeType = "application/json"
                },
                new McpResourceDefinition
                {
                    Uri = ResourceMcpNodesUri,
                    Name = "MCP 节点指定数据流采样与信号统计 (MCP Analysis Nodes)",
                    Description = "读取所有 MCP 数据分析节点上已接入的指定数据流统计量 (均值/标准差/峰峰值/抖动度) 与近期时间序列样本",
                    MimeType = "application/json"
                },
                new McpResourceDefinition
                {
                    Uri = ResourcePythonCatalogUri,
                    Name = "外部独立 Python 算法仓库白名单与可调标量参数目录",
                    Description = "列出独立于软件与 Git 之外的 Python 算法文件白名单及其默认标量参数 Schema",
                    MimeType = "application/json"
                }
            };
        }

        private string ReadResourceJson(string uri)
        {
            if (string.Equals(uri, ResourcePythonCatalogUri, StringComparison.OrdinalIgnoreCase))
            {
                var catalog = PythonAlgoRepoService.Instance.ListWhitelistedAlgorithms().Select(a => new
                {
                    fileName = a.FileName,
                    title = a.DisplayTitle,
                    description = a.Description,
                    defaultScalarParams = a.DefaultParams
                }).ToList();

                return JsonSerializer.Serialize(new
                {
                    isolatedFromSoftwareAndGit = true,
                    whitelistedAlgorithms = catalog
                }, IndentedJsonOptions);
            }

            if (_blueprintVm == null || _uiDispatcher == null)
            {
                return JsonSerializer.Serialize(new { error = "蓝图工作台尚未完成绑定" }, IndentedJsonOptions);
            }

            if (string.Equals(uri, ResourceBlueprintGraphUri, StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = _uiDispatcher.CheckAccess()
                    ? _blueprintVm.ExportMcpGraphSnapshot(includeMcpSamples: true)
                    : _uiDispatcher.Invoke(() => _blueprintVm.ExportMcpGraphSnapshot(includeMcpSamples: true));
                return JsonSerializer.Serialize(snapshot, IndentedJsonOptions);
            }

            if (string.Equals(uri, ResourceMcpNodesUri, StringComparison.OrdinalIgnoreCase))
            {
                var reports = _uiDispatcher.CheckAccess()
                    ? _blueprintVm.Nodes.OfType<McpInputNode>().Select(m => m.BuildAnalysisReport(48)).ToList()
                    : _uiDispatcher.Invoke(() => _blueprintVm.Nodes.OfType<McpInputNode>().Select(m => m.BuildAnalysisReport(48)).ToList());
                return JsonSerializer.Serialize(new
                {
                    mcpNodeCount = reports.Count,
                    nodes = reports
                }, IndentedJsonOptions);
            }

            throw new KeyNotFoundException($"未知的 MCP 资源 URI: {uri}");
        }

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

                    case McpConstants.MethodResourcesList:
                    {
                        var resList = new McpResourceListResult
                        {
                            Resources = GetRegisteredResources().ToList()
                        };
                        return SerializeResponse(McpRpcResponse.Success(request.Id, resList));
                    }

                    case McpConstants.MethodResourcesRead:
                    {
                        if (request.Params == null || request.Params.Value.ValueKind != JsonValueKind.Object ||
                            !request.Params.Value.TryGetProperty("uri", out var uriProp) ||
                            string.IsNullOrWhiteSpace(uriProp.GetString()))
                        {
                            var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorInvalidParams, "resources/read 缺少 uri 参数");
                            return SerializeResponse(err);
                        }

                        string uri = uriProp.GetString()!;
                        try
                        {
                            string jsonText = ReadResourceJson(uri);
                            var readRes = new McpResourceReadResult
                            {
                                Contents = new List<McpResourceContentItem>
                                {
                                    new McpResourceContentItem
                                    {
                                        Uri = uri,
                                        MimeType = "application/json",
                                        Text = jsonText
                                    }
                                }
                            };
                            return SerializeResponse(McpRpcResponse.Success(request.Id, readRes));
                        }
                        catch (KeyNotFoundException knf)
                        {
                            var err = McpRpcResponse.Fail(request.Id, McpConstants.ErrorInvalidParams, knf.Message);
                            return SerializeResponse(err);
                        }
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
