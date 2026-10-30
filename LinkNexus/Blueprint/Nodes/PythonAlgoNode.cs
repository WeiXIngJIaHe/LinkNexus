using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;
using Microsoft.Win32;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 外部隔离 Python 算法卡片 (PythonAlgoNode)
    /// 1. 物理隔离：算法文件统一存储在软件及 Git 仓库之外的 %LOCALAPPDATA%\LinkNexus\PythonAlgoRepo；
    /// 2. 安全防乱调：严格仅允许选择外部算法仓白名单内的顶层 *.py 文件，禁止任意路径穿越或内联脚本 exec；
    /// 3. Agent 协同调参：支持 AI Agent 根据 MCP 节点采样数据通过 blueprint_tune_python_params 动态热更新标量算法参数 (如 q/r/alpha/window/scale/offset)。
    /// </summary>
    public sealed class PythonAlgoNode : NodeBase
    {
        private const string IconSvg = "M8,4 H15 A3,3 0 0,1 18,7 V11 H9 A3,3 0 0,0 6,14 V17 A3,3 0 0,0 9,20 H16 M12,4 V2 M12,22 V20";

        public const string AlgoKalman = PythonAlgoRepoService.DefaultKalmanFile;
        public const string AlgoEma = PythonAlgoRepoService.DefaultEmaFile;
        public const string AlgoMovingAvg = PythonAlgoRepoService.DefaultMovingAvgFile;
        public const string AlgoImuSolve = PythonAlgoRepoService.DefaultImuSmoothFile;
        public const string AlgoLinearScale = PythonAlgoRepoService.DefaultLinearScaleFile;

        private readonly NodeParameter _algoSelectParam;
        private readonly NodeParameter _paramsJsonParam;
        private readonly NodeParameter _openRepoBtn;
        private readonly NodeParameter _engineBadgeParam;

        private readonly object _paramLock = new();
        private Dictionary<string, double> _activeParams = new(StringComparer.Ordinal);

        // 内置同构高速回退状态 (当目标机器无 python.exe 时依然使 Agent 调优参数 100% 实时生效)
        private double[] _emaState = Array.Empty<double>();
        private double[] _kalmanX = Array.Empty<double>();
        private double[] _kalmanP = Array.Empty<double>();
        private readonly List<Queue<double>> _windowBuffers = new();

        // 持久化受限 python.exe 进程管道
        private readonly SemaphoreSlim _pyLock = new(1, 1);
        private Process? _pyProcess;
        private string _activeLoadedFilePath = string.Empty;

        public override string NodeTypeKey => "python_algo";
        public override double CardWidth => 306.0;

        public string SelectedAlgoFile => _algoSelectParam.Value;

        public PythonAlgoNode(string initialAlgoFile = AlgoKalman, double x = 368, double y = 60)
            : base("外部 Python 算法", "独立算法仓 · Agent 可调参", "#8B5CF6", IconSvg, x, y)
        {
            AddInputPin("输入通道 (CH)", PinDataType.ChannelStream);
            AddOutputPin("算法输出 (CH)", PinDataType.ChannelStream);

            var catalog = PythonAlgoRepoService.Instance.ListWhitelistedAlgorithms();
            string resolvedInitial = catalog.Any(c => string.Equals(c.FileName, initialAlgoFile, StringComparison.OrdinalIgnoreCase))
                ? initialAlgoFile
                : (catalog.Count > 0 ? catalog[0].FileName : AlgoKalman);

            _algoSelectParam = new NodeParameter("algo_file", "外部算子", NodeParamKind.Combo, resolvedInitial, "外部算法仓为空");
            _algoSelectParam.ReplaceOptions(catalog.Select(c => c.FileName), resolvedInitial);

            _activeParams = PythonAlgoRepoService.Instance.GetDefaultParamsFor(resolvedInitial);
            string initParamsJson = PythonAlgoRepoService.SerializeParams(_activeParams);

            _paramsJsonParam = new NodeParameter("params", "算法参数", NodeParamKind.Text, initParamsJson);

            _algoSelectParam.ValueChangedCallback = selectedFile =>
            {
                ResetFilterStates();
                var defaults = PythonAlgoRepoService.Instance.GetDefaultParamsFor(selectedFile);
                lock (_paramLock)
                {
                    _activeParams = defaults;
                }
                _paramsJsonParam.SetValueSilently(PythonAlgoRepoService.SerializeParams(defaults));
                _ = RestartPythonWorkerAsync(selectedFile);
            };

            _paramsJsonParam.ValueChangedCallback = rawJson =>
            {
                if (PythonAlgoRepoService.TryValidateScalarParamsJson(rawJson, out var parsed, out string err))
                {
                    lock (_paramLock)
                    {
                        _activeParams = parsed;
                    }
                    UpdateEngineBadge($"参数已生效: {PythonAlgoRepoService.SerializeParams(parsed)}");
                }
                else
                {
                    UpdateEngineBadge(err);
                }
            };

            _openRepoBtn = new NodeParameter("open_repo", "外部仓库", NodeParamKind.ActionButton, "打开外部独立 Python 算法目录")
            {
                ActionCommand = new RelayCommand(OpenExternalRepoInExplorer)
            };

            _engineBadgeParam = new NodeParameter(
                "status",
                "引擎状态",
                NodeParamKind.ReadOnlyBadge,
                $"外部仓隔离就绪 · {resolvedInitial}");

            Parameters.Add(_algoSelectParam);
            Parameters.Add(_paramsJsonParam);
            Parameters.Add(_openRepoBtn);
            Parameters.Add(_engineBadgeParam);

            PythonAlgoRepoService.Instance.RepositoryChanged += OnExternalRepoChanged;
            UpdateStatusOnUi("算子就绪", true);
            _ = RestartPythonWorkerAsync(resolvedInitial);
        }

        private void OnExternalRepoChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var catalog = PythonAlgoRepoService.Instance.ListWhitelistedAlgorithms();
                _algoSelectParam.ReplaceOptions(catalog.Select(c => c.FileName), _algoSelectParam.Value);
                _ = RestartPythonWorkerAsync(_algoSelectParam.Value);
            });
        }

        public override IReadOnlyList<NodeContextAction> GetContextMenuActions()
        {
            return new List<NodeContextAction>
            {
                new NodeContextAction
                {
                    Header = "在资源管理器中打开外部 Python 算法仓",
                    Execute = OpenExternalRepoInExplorer
                },
                new NodeContextAction
                {
                    Header = "选择其他外部独立 Python 算法文件夹...",
                    Execute = BrowseSwitchExternalRepoFromUi
                },
                new NodeContextAction
                {
                    Header = "重新扫描外部算法白名单 (.py)",
                    Execute = () => OnExternalRepoChanged()
                },
                NodeContextAction.Separator(),
                new NodeContextAction
                {
                    Header = "恢复当前算子默认参数",
                    Execute = () =>
                    {
                        var defaults = PythonAlgoRepoService.Instance.GetDefaultParamsFor(_algoSelectParam.Value);
                        lock (_paramLock)
                        {
                            _activeParams = defaults;
                        }
                        string json = PythonAlgoRepoService.SerializeParams(defaults);
                        Application.Current?.Dispatcher.Invoke(() => _paramsJsonParam.SetValueSilently(json));
                        ResetFilterStates();
                        UpdateEngineBadge($"已恢复默认参数: {json}");
                    }
                }
            };
        }

        private void OpenExternalRepoInExplorer()
        {
            try
            {
                string dir = PythonAlgoRepoService.Instance.RepoDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                UpdateEngineBadge($"打开目录失败: {ex.Message}");
            }
        }

        private void BrowseSwitchExternalRepoFromUi()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择与软件及 Git 隔离的外部 Python 算法仓库目录",
                InitialDirectory = PythonAlgoRepoService.Instance.RepoDirectory
            };

            if (dlg.ShowDialog() == true)
            {
                if (PythonAlgoRepoService.Instance.TrySetExternalRepoDirectoryFromUi(dlg.FolderName, out string err))
                {
                    UpdateEngineBadge($"已切换外部仓: {Path.GetFileName(dlg.FolderName)}");
                }
                else
                {
                    UpdateEngineBadge(err);
                }
            }
        }

        /// <summary>
        /// 供 MCP Agent (blueprint_tune_python_params) 安全调优白名单算子标量参数
        /// 严格拒绝非白名单 .py 文件名、路径穿越以及非数值/布尔的参数载荷
        /// </summary>
        public bool TryApplyAgentTuning(
            string? requestedAlgoFile,
            JsonElement? newParamsElement,
            bool replaceAllParams,
            out string summary,
            out string error)
        {
            summary = string.Empty;
            error = string.Empty;

            string targetFile = _algoSelectParam.Value;
            bool algoSwitched = false;

            if (!string.IsNullOrWhiteSpace(requestedAlgoFile))
            {
                if (!PythonAlgoRepoService.Instance.TryResolveWhitelistedFile(requestedAlgoFile, out _, out error))
                {
                    return false;
                }
                targetFile = requestedAlgoFile.Trim();
                algoSwitched = !string.Equals(targetFile, _algoSelectParam.Value, StringComparison.OrdinalIgnoreCase);
            }

            Dictionary<string, double> mergedParams;
            lock (_paramLock)
            {
                mergedParams = algoSwitched || replaceAllParams
                    ? PythonAlgoRepoService.Instance.GetDefaultParamsFor(targetFile)
                    : new Dictionary<string, double>(_activeParams, StringComparer.Ordinal);
            }

            if (newParamsElement.HasValue && newParamsElement.Value.ValueKind != JsonValueKind.Undefined && newParamsElement.Value.ValueKind != JsonValueKind.Null)
            {
                if (!PythonAlgoRepoService.TryValidateScalarParamsElement(newParamsElement.Value, out var incoming, out error))
                {
                    return false;
                }

                foreach (var kv in incoming)
                {
                    mergedParams[kv.Key] = kv.Value;
                }
            }

            string serialized = PythonAlgoRepoService.SerializeParams(mergedParams);
            lock (_paramLock)
            {
                _activeParams = mergedParams;
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (algoSwitched)
                {
                    var catalog = PythonAlgoRepoService.Instance.ListWhitelistedAlgorithms();
                    _algoSelectParam.ReplaceOptions(catalog.Select(c => c.FileName), targetFile);
                }
                _paramsJsonParam.SetValueSilently(serialized);
            });

            if (algoSwitched)
            {
                ResetFilterStates();
                _ = RestartPythonWorkerAsync(targetFile);
            }

            UpdateStatusOnUi("Agent 已调参", true);
            UpdateEngineBadge($"Agent 调优: {serialized}");
            summary = $"节点 [{NodeId}] 算子={targetFile}, 当前生效参数={serialized}";
            return true;
        }

        private void ResetFilterStates()
        {
            _emaState = Array.Empty<double>();
            _kalmanX = Array.Empty<double>();
            _kalmanP = Array.Empty<double>();
            _windowBuffers.Clear();
        }

        protected override async Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            double[] input = packet.Channels;
            if (input.Length == 0)
            {
                EmitPacket(0, packet);
                return;
            }

            Dictionary<string, double> snapshotParams;
            lock (_paramLock)
            {
                snapshotParams = new Dictionary<string, double>(_activeParams, StringComparer.Ordinal);
            }

            double[] output = await ExecuteExternalWhitelistedPythonAsync(input, snapshotParams, ct).ConfigureAwait(false);
            var outPkt = new BlueprintPacket(output, string.Empty, "PY_ALGO", packet.Timestamp, packet.StreamPrefix);
            EmitPacket(0, outPkt);
        }

        private async Task RestartPythonWorkerAsync(string algoFileName)
        {
            await _pyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                StopPythonProcessCore();

                if (!PythonAlgoRepoService.Instance.TryResolveWhitelistedFile(algoFileName, out string fullPyPath, out string err))
                {
                    UpdateEngineBadge(err);
                    UpdateStatusOnUi("拦截非法脚本", false);
                    return;
                }

                string? pyExe = PythonAlgoRepoService.ResolvePythonExecutable();
                if (string.IsNullOrEmpty(pyExe))
                {
                    UpdateEngineBadge("无 python.exe · 参数化内置核运行");
                    return;
                }

                string bootstrap = BuildIsolatedModuleWorkerBootstrap(fullPyPath);
                string b64Bootstrap = Convert.ToBase64String(Encoding.UTF8.GetBytes(bootstrap));
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    WorkingDirectory = PythonAlgoRepoService.Instance.RepoDirectory,
                    Arguments = $"-I -u -c \"import base64; exec(base64.b64decode('{b64Bootstrap}').decode('utf-8'))\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false)
                };

                var proc = Process.Start(psi);
                if (proc == null || proc.HasExited)
                {
                    UpdateEngineBadge("Python 未就绪 · 参数化内置核运行");
                    return;
                }

                _pyProcess = proc;
                _activeLoadedFilePath = fullPyPath;
                UpdateEngineBadge($"{Path.GetFileName(fullPyPath)} · PID {proc.Id}");
                UpdateStatusOnUi("外部算子在线", true);
            }
            catch
            {
                UpdateEngineBadge($"{algoFileName} · 参数化高速核运行");
            }
            finally
            {
                try { _pyLock.Release(); } catch { }
            }
        }

        private static string BuildIsolatedModuleWorkerBootstrap(string whitelistedFullPath)
        {
            string b64Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(whitelistedFullPath));
            return
                "import sys, json, base64, importlib.util\n" +
                $"mod_path = base64.b64decode('{b64Path}').decode('utf-8')\n" +
                "spec = importlib.util.spec_from_file_location('lnx_ext_algo', mod_path)\n" +
                "mod = importlib.util.module_from_spec(spec)\n" +
                "spec.loader.exec_module(mod)\n" +
                "fn = getattr(mod, 'process')\n" +
                "state = {}\n" +
                "for line in sys.stdin:\n" +
                "    s = line.strip()\n" +
                "    if not s: continue\n" +
                "    try:\n" +
                "        req = json.loads(s)\n" +
                "        res = fn(req['ch'], req['params'], state)\n" +
                "        sys.stdout.write(json.dumps([float(x) for x in res]) + '\\n')\n" +
                "        sys.stdout.flush()\n" +
                "    except Exception as e:\n" +
                "        sys.stdout.write('ERR:' + str(e) + '\\n')\n" +
                "        sys.stdout.flush()\n";
        }

        private async Task<double[]> ExecuteExternalWhitelistedPythonAsync(
            double[] input,
            Dictionary<string, double> currentParams,
            CancellationToken ct)
        {
            await _pyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_pyProcess == null || _pyProcess.HasExited)
                {
                    return ExecuteParameterizedFallback(_algoSelectParam.Value, input, currentParams);
                }

                string reqJson = JsonSerializer.Serialize(new { ch = input, @params = currentParams });
                await _pyProcess.StandardInput.WriteLineAsync(reqJson).ConfigureAwait(false);
                await _pyProcess.StandardInput.FlushAsync().ConfigureAwait(false);

                string? resp = await _pyProcess.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(resp))
                {
                    return ExecuteParameterizedFallback(_algoSelectParam.Value, input, currentParams);
                }

                if (resp.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    UpdateEngineBadge(resp);
                    return input;
                }

                var parsed = JsonSerializer.Deserialize<double[]>(resp);
                return parsed ?? input;
            }
            catch
            {
                return ExecuteParameterizedFallback(_algoSelectParam.Value, input, currentParams);
            }
            finally
            {
                _pyLock.Release();
            }
        }

        private double[] ExecuteParameterizedFallback(string fileName, double[] src, Dictionary<string, double> p)
        {
            int n = src.Length;
            var dst = new double[n];
            string key = (fileName ?? string.Empty).ToLowerInvariant();

            if (key.Contains("ema"))
            {
                double alpha = Math.Clamp(p.TryGetValue("alpha", out double a) ? a : 0.25, 0.005, 1.0);
                if (_emaState.Length != n) _emaState = (double[])src.Clone();
                for (int i = 0; i < n; i++)
                {
                    _emaState[i] = alpha * src[i] + (1.0 - alpha) * _emaState[i];
                    dst[i] = Math.Round(_emaState[i], 4);
                }
            }
            else if (key.Contains("moving"))
            {
                int win = Math.Clamp((int)Math.Round(p.TryGetValue("window", out double w) ? w : 8.0), 1, 64);
                while (_windowBuffers.Count < n) _windowBuffers.Add(new Queue<double>(win + 1));
                for (int i = 0; i < n; i++)
                {
                    var qBuf = _windowBuffers[i];
                    qBuf.Enqueue(src[i]);
                    while (qBuf.Count > win) qBuf.Dequeue();
                    dst[i] = Math.Round(qBuf.Average(), 4);
                }
            }
            else if (key.Contains("imu"))
            {
                double gain = Math.Clamp(p.TryGetValue("gain", out double g) ? g : 0.35, 0.01, 1.0);
                double lim = Math.Max(10.0, p.TryGetValue("limit_deg", out double l) ? l : 180.0);
                if (_emaState.Length != n) _emaState = (double[])src.Clone();
                for (int i = 0; i < n; i++)
                {
                    double delta = src[i] - _emaState[i];
                    if (delta > 180.0) delta -= 360.0;
                    else if (delta < -180.0) delta += 360.0;
                    double smoothed = _emaState[i] + gain * delta;
                    while (smoothed > lim) smoothed -= 2.0 * lim;
                    while (smoothed < -lim) smoothed += 2.0 * lim;
                    _emaState[i] = smoothed;
                    dst[i] = Math.Round(smoothed, 3);
                }
            }
            else if (key.Contains("scale") || key.Contains("linear"))
            {
                double scale = p.TryGetValue("scale", out double s) ? s : 1.0;
                double offset = p.TryGetValue("offset", out double o) ? o : 0.0;
                for (int i = 0; i < n; i++)
                {
                    dst[i] = Math.Round(src[i] * scale + offset, 4);
                }
            }
            else
            {
                double q = Math.Max(1e-6, p.TryGetValue("q", out double qv) ? qv : 0.022);
                double r = Math.Max(1e-6, p.TryGetValue("r", out double rv) ? rv : 0.180);
                if (_kalmanX.Length != n)
                {
                    _kalmanX = (double[])src.Clone();
                    _kalmanP = Enumerable.Repeat(1.0, n).ToArray();
                }
                for (int i = 0; i < n; i++)
                {
                    double pPred = _kalmanP[i] + q;
                    double k = pPred / (pPred + r);
                    _kalmanX[i] = _kalmanX[i] + k * (src[i] - _kalmanX[i]);
                    _kalmanP[i] = (1.0 - k) * pPred;
                    dst[i] = Math.Round(_kalmanX[i], 4);
                }
            }

            return dst;
        }

        public override Dictionary<string, object?> ExportMcpMetadata()
        {
            Dictionary<string, double> snapshot;
            lock (_paramLock)
            {
                snapshot = new Dictionary<string, double>(_activeParams, StringComparer.Ordinal);
            }

            return new Dictionary<string, object?>
            {
                ["selectedAlgoFile"] = _algoSelectParam.Value,
                ["externalRepoIsolated"] = true,
                ["activeLoadedFile"] = Path.GetFileName(_activeLoadedFilePath),
                ["tunableScalarParams"] = snapshot
            };
        }

        private void StopPythonProcess()
        {
            if (_pyLock.Wait(150))
            {
                try
                {
                    StopPythonProcessCore();
                }
                finally
                {
                    try { _pyLock.Release(); } catch { }
                }
            }
            else
            {
                StopPythonProcessCore();
            }
        }

        private void StopPythonProcessCore()
        {
            if (_pyProcess != null)
            {
                try
                {
                    if (!_pyProcess.HasExited) _pyProcess.Kill();
                }
                catch { }
                _pyProcess.Dispose();
                _pyProcess = null;
            }
        }

        private void UpdateEngineBadge(string text)
        {
            string clipped = text.Length > 36 ? text[..36] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _engineBadgeParam.SetValueSilently(clipped));
        }

        public override void Dispose()
        {
            PythonAlgoRepoService.Instance.RepositoryChanged -= OnExternalRepoChanged;
            StopPythonProcess();
            _pyLock.Dispose();
            base.Dispose();
        }
    }
}
