using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// Python 算法仓库卡片 (PythonAlgoNode)
    /// 内置高频工业信号处理算子 (EMA 低通、一维卡尔曼、滑动均值、IMU 姿态解算、增益偏置)，并支持持久管道调用本机 python.exe 执行自定义脚本
    /// </summary>
    public sealed class PythonAlgoNode : NodeBase
    {
        private const string IconSvg = "M8,4 H15 A3,3 0 0,1 18,7 V11 H9 A3,3 0 0,0 6,14 V17 A3,3 0 0,0 9,20 H16 M12,4 V2 M12,22 V20";

        public const string AlgoEma = "一阶低通滤波 (EMA α=0.25)";
        public const string AlgoKalman = "一维卡尔曼滤波 (Kalman)";
        public const string AlgoMovingAvg = "滑动均值滤波 (Window=8)";
        public const string AlgoImuSolve = "IMU 姿态平滑与归一化";
        public const string AlgoLinearScale = "线性增益与零偏 (×1.25)";
        public const string AlgoCustomPython = "自定义 Python 脚本 (python.exe)";

        private readonly NodeParameter _algoSelectParam;
        private readonly NodeParameter _scriptParam;
        private readonly NodeParameter _applyScriptBtn;
        private readonly NodeParameter _engineBadgeParam;

        // 内置算子状态缓存
        private double[] _emaState = Array.Empty<double>();
        private double[] _kalmanX = Array.Empty<double>();
        private double[] _kalmanP = Array.Empty<double>();
        private readonly List<Queue<double>> _windowBuffers = new();

        // 持久化 python.exe 进程管道
        private readonly SemaphoreSlim _pyLock = new(1, 1);
        private Process? _pyProcess;
        private string _activeScriptSnapshot = string.Empty;

        public override double CardWidth => 304.0;

        public PythonAlgoNode(string initialAlgo = AlgoKalman, double x = 368, double y = 60)
            : base("Python 算法仓库", "信号处理算子", "#8B5CF6", IconSvg, x, y)
        {
            AddInputPin("输入通道 (CH)", PinDataType.ChannelStream);
            AddOutputPin("算法输出 (CH)", PinDataType.ChannelStream);

            _algoSelectParam = new NodeParameter("algo", "算法模型", NodeParamKind.Combo, initialAlgo);
            _algoSelectParam.Options.Add(AlgoEma);
            _algoSelectParam.Options.Add(AlgoKalman);
            _algoSelectParam.Options.Add(AlgoMovingAvg);
            _algoSelectParam.Options.Add(AlgoImuSolve);
            _algoSelectParam.Options.Add(AlgoLinearScale);
            _algoSelectParam.Options.Add(AlgoCustomPython);

            _scriptParam = new NodeParameter("script", "算子脚本 (def process(ch):)", NodeParamKind.CodeScript, GetTemplateForAlgo(initialAlgo));

            _algoSelectParam.ValueChangedCallback = selected =>
            {
                ResetFilterStates();
                if (selected != AlgoCustomPython)
                {
                    StopPythonProcess();
                    _scriptParam.SetValueSilently(GetTemplateForAlgo(selected));
                    UpdateEngineBadge($"内置高速算子就绪");
                    UpdateStatusOnUi("算子就绪", true);
                }
                else
                {
                    _ = RestartPythonWorkerAsync(_scriptParam.Value);
                }
            };

            _applyScriptBtn = new NodeParameter("reload", "脚本热重载", NodeParamKind.ActionButton, "编译并应用 Python 脚本")
            {
                ActionCommand = new RelayCommand(() =>
                {
                    if (_algoSelectParam.Value != AlgoCustomPython)
                    {
                        _algoSelectParam.Value = AlgoCustomPython;
                    }
                    else
                    {
                        _ = RestartPythonWorkerAsync(_scriptParam.Value);
                    }
                })
            };

            _engineBadgeParam = new NodeParameter("status", "引擎状态", NodeParamKind.ReadOnlyBadge, "内置零延迟算子已激活");

            Parameters.Add(_algoSelectParam);
            Parameters.Add(_scriptParam);
            Parameters.Add(_applyScriptBtn);
            Parameters.Add(_engineBadgeParam);

            UpdateStatusOnUi("算子就绪", true);
        }

        private static string GetTemplateForAlgo(string algo)
        {
            return algo switch
            {
                AlgoEma => "def process(ch):\n    # α = 0.25 指数滑动低通\n    return [round(v * 0.25, 3) for v in ch]",
                AlgoKalman => "def process(ch):\n    # 1D Kalman: Q=0.02, R=0.18\n    return [round(v, 3) for v in ch]",
                AlgoMovingAvg => "def process(ch):\n    # N = 8 滑动窗口均值去噪\n    return [round(v, 3) for v in ch]",
                AlgoImuSolve => "def process(ch):\n    # 欧拉角限幅与低通抗抖动\n    return [max(-180.0, min(180.0, v)) for v in ch]",
                AlgoLinearScale => "def process(ch):\n    # 增益 ×1.25 + 零偏校准\n    return [round(v * 1.25, 3) for v in ch]",
                _ => "import math\ndef process(ch):\n    return [round(v * 0.9, 3) for v in ch]"
            };
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

            string mode = _algoSelectParam.Value;
            double[] output;

            if (mode == AlgoCustomPython)
            {
                output = await ExecuteViaPythonProcessAsync(input, ct).ConfigureAwait(false);
            }
            else
            {
                output = ExecuteBuiltinFastOperator(mode, input);
            }

            var outPkt = new BlueprintPacket(output, string.Empty, "PY_ALGO", packet.Timestamp);
            EmitPacket(0, outPkt);
        }

        private double[] ExecuteBuiltinFastOperator(string mode, double[] src)
        {
            int n = src.Length;
            var dst = new double[n];

            switch (mode)
            {
                case AlgoEma:
                {
                    const double alpha = 0.25;
                    if (_emaState.Length != n)
                    {
                        _emaState = (double[])src.Clone();
                    }
                    for (int i = 0; i < n; i++)
                    {
                        _emaState[i] = alpha * src[i] + (1.0 - alpha) * _emaState[i];
                        dst[i] = Math.Round(_emaState[i], 4);
                    }
                    break;
                }

                case AlgoKalman:
                {
                    const double q = 0.022;
                    const double r = 0.18;
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
                    break;
                }

                case AlgoMovingAvg:
                {
                    const int winSize = 8;
                    while (_windowBuffers.Count < n) _windowBuffers.Add(new Queue<double>(winSize + 1));
                    for (int i = 0; i < n; i++)
                    {
                        var qBuf = _windowBuffers[i];
                        qBuf.Enqueue(src[i]);
                        while (qBuf.Count > winSize) qBuf.Dequeue();
                        dst[i] = Math.Round(qBuf.Average(), 4);
                    }
                    break;
                }

                case AlgoImuSolve:
                {
                    if (_emaState.Length != n)
                    {
                        _emaState = (double[])src.Clone();
                    }
                    for (int i = 0; i < n; i++)
                    {
                        // 处理欧拉角跨 ±180° 跳变平滑
                        double delta = src[i] - _emaState[i];
                        if (delta > 180.0) delta -= 360.0;
                        else if (delta < -180.0) delta += 360.0;

                        double smoothed = _emaState[i] + 0.35 * delta;
                        while (smoothed > 180.0) smoothed -= 360.0;
                        while (smoothed < -180.0) smoothed += 360.0;

                        _emaState[i] = smoothed;
                        dst[i] = Math.Round(smoothed, 3);
                    }
                    break;
                }

                case AlgoLinearScale:
                default:
                {
                    for (int i = 0; i < n; i++)
                    {
                        dst[i] = Math.Round(src[i] * 1.25, 4);
                    }
                    break;
                }
            }

            return dst;
        }

        private static string? ResolvePythonExecutable()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pioPython = Path.Combine(userProfile, ".platformio", "penv", "Scripts", "python.exe");
            if (File.Exists(pioPython)) return pioPython;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programsPyDir = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(programsPyDir))
            {
                var found = Directory.GetFiles(programsPyDir, "python.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }

            return "python";
        }

        private async Task RestartPythonWorkerAsync(string userScript)
        {
            await _pyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                StopPythonProcessCore();
                string? pyExe = ResolvePythonExecutable();
                if (string.IsNullOrEmpty(pyExe))
                {
                    UpdateEngineBadge("未找到 python.exe，回退内置算子");
                    return;
                }

                string bootstrap = BuildPythonWorkerBootstrap(userScript);
                var psi = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = "-u -c \"" + bootstrap.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
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
                    UpdateEngineBadge("Python 进程启动失败");
                    UpdateStatusOnUi("脚本错误", false);
                    return;
                }

                _pyProcess = proc;
                _activeScriptSnapshot = userScript;
                UpdateEngineBadge($"python.exe 在线 (PID {proc.Id})");
                UpdateStatusOnUi("Python 运行中", true);
            }
            catch (Exception ex)
            {
                UpdateEngineBadge($"Python 不可用: {ex.Message}");
                UpdateStatusOnUi("回退内置", false);
            }
            finally
            {
                _pyLock.Release();
            }
        }

        private static string BuildPythonWorkerBootstrap(string userScript)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userScript));
            return
                "import sys, json, base64, math\n" +
                $"code = base64.b64decode('{b64}').decode('utf-8')\n" +
                "ns = {'math': math}\n" +
                "exec(code, ns)\n" +
                "fn = ns.get('process', lambda ch: ch)\n" +
                "for line in sys.stdin:\n" +
                "    s = line.strip()\n" +
                "    if not s: continue\n" +
                "    try:\n" +
                "        arr = json.loads(s)\n" +
                "        res = fn(arr)\n" +
                "        sys.stdout.write(json.dumps([float(x) for x in res]) + '\\n')\n" +
                "        sys.stdout.flush()\n" +
                "    except Exception as e:\n" +
                "        sys.stdout.write('ERR:' + str(e) + '\\n')\n" +
                "        sys.stdout.flush()\n";
        }

        private async Task<double[]> ExecuteViaPythonProcessAsync(double[] input, CancellationToken ct)
        {
            if (_pyProcess == null || _pyProcess.HasExited || _activeScriptSnapshot != _scriptParam.Value)
            {
                await RestartPythonWorkerAsync(_scriptParam.Value).ConfigureAwait(false);
            }

            await _pyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_pyProcess == null || _pyProcess.HasExited)
                {
                    return ExecuteBuiltinFastOperator(AlgoKalman, input);
                }

                string reqJson = JsonSerializer.Serialize(input);
                await _pyProcess.StandardInput.WriteLineAsync(reqJson).ConfigureAwait(false);
                await _pyProcess.StandardInput.FlushAsync().ConfigureAwait(false);

                string? resp = await _pyProcess.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(resp))
                {
                    return input;
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
                return ExecuteBuiltinFastOperator(AlgoKalman, input);
            }
            finally
            {
                _pyLock.Release();
            }
        }

        private void StopPythonProcess()
        {
            _pyLock.Wait();
            try
            {
                StopPythonProcessCore();
            }
            finally
            {
                _pyLock.Release();
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
            string clipped = text.Length > 34 ? text[..34] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _engineBadgeParam.SetValueSilently(clipped));
        }

        public override void Dispose()
        {
            StopPythonProcess();
            _pyLock.Dispose();
            base.Dispose();
        }
    }
}
