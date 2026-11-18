using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodingSeb.ExpressionEvaluator;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// UE5 动态表达式与 Python 热重载沙盒卡 (InlineMathAndPythonNode)
    /// 1. 轻量模式：CodingSeb.ExpressionEvaluator 编译单行公式 (如 a * b, (a - prev_a) / dt)；
    /// 2. 重载模式：绑定外部 .py 算法文件，基于 FileSystemWatcher 热重载算法内核并直连 PythonNetBridge.TryExecuteInProcess；
    /// 3. 提供 [M] Bypass 一键直通对比按钮与微秒级计算耗时指示，输入引脚满足 Blender/UE5 内联编辑规范。
    /// </summary>
    public sealed class InlineMathAndPythonNode : NodeBase
    {
        private const string IconSvg = "M19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M19,19H5V5H19V19M13.5,13.5L10,17L8.5,15.5L11,13L8.5,10.5L10,9L13.5,12.5L15,11V16H13.5V13.5Z";

        private readonly NodeParameter _modeParam;
        private readonly NodeParameter _expressionParam;
        private readonly NodeParameter _pyScriptPathParam;
        private readonly NodeParameter _bypassToggleParam;

        private readonly ExpressionEvaluator _evaluator = new();
        private readonly object _execLock = new();
        private FileSystemWatcher? _fileWatcher;
        private string _resolvedPyPath = string.Empty;

        private double _valA = 24.0;
        private double _valB = 1.5;
        private double _prevA = 24.0;
        private double _kalmanX = 24.0;
        private double _kalmanP = 1.0;
        private long _lastTick = 0;

        public override string NodeTypeKey => "inline_math_python";
        public override double CardWidth => 320.0;

        public InlineMathAndPythonNode(double x = 360, double y = 350)
            : base("表达式与 Python 沙盒", "Converter & Math", "#F59E0B", IconSvg, x, y)
        {
            var pinA = AddInputPin("自变量 A", PinDataType.FloatStream);
            pinA.HasInlineWidget = true;
            pinA.InlineValue = "24.0";
            pinA.InlineDoubleChangedCallback = v => _valA = v;

            var pinB = AddInputPin("自变量 B", PinDataType.FloatStream);
            pinB.HasInlineWidget = true;
            pinB.InlineValue = "1.5";
            pinB.InlineDoubleChangedCallback = v => _valB = v;

            AddOutputPin("计算结果 (Result)", PinDataType.FloatStream);

            _modeParam = new NodeParameter("exec_mode", "运算内核模式", NodeParamKind.Combo, "C# 动态数学公式 (Expression)");
            _modeParam.Options.Add("C# 动态数学公式 (Expression)");
            _modeParam.Options.Add("Python 热重载脚本 (.py)");

            _expressionParam = new NodeParameter("expr", "公式表达式 f(a, b, dt)", NodeParamKind.Text, "a * b");

            _pyScriptPathParam = new NodeParameter("script_path", "Python 脚本路径", NodeParamKind.Text, PythonAlgoRepoService.DefaultKalmanFile)
            {
                ValueChangedCallback = path => SetupFileWatcher(path)
            };

            _bypassToggleParam = new NodeParameter("bypass", "[M] Bypass 直通对比", NodeParamKind.Toggle)
            {
                BoolValue = false
            };

            Parameters.Add(_modeParam);
            Parameters.Add(_expressionParam);
            Parameters.Add(_pyScriptPathParam);
            Parameters.Add(_bypassToggleParam);

            SetupFileWatcher(_pyScriptPathParam.Value);
            UpdateStatusOnUi("沙盒计算就绪 (<1μs)", true);
        }

        private string ResolveScriptFilePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
            string trimmed = rawPath.Trim();

            if (PythonAlgoRepoService.Instance.TryResolveWhitelistedFile(trimmed, out string whitelistedPath, out _))
            {
                return whitelistedPath;
            }

            if (File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string repoCandidate = Path.Combine(PythonAlgoRepoService.Instance.RepoDirectory, Path.GetFileName(trimmed));
            if (File.Exists(repoCandidate))
            {
                return repoCandidate;
            }

            return string.Empty;
        }

        private void SetupFileWatcher(string path)
        {
            try
            {
                _fileWatcher?.Dispose();
                _fileWatcher = null;

                _resolvedPyPath = ResolveScriptFilePath(path);
                if (string.IsNullOrEmpty(_resolvedPyPath) || !File.Exists(_resolvedPyPath)) return;

                PythonNetBridge.NotifyScriptReloaded(_resolvedPyPath, NodeId);

                string? dir = Path.GetDirectoryName(_resolvedPyPath);
                string file = Path.GetFileName(_resolvedPyPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _fileWatcher = new FileSystemWatcher(dir, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    _fileWatcher.Changed += (_, _) =>
                    {
                        PythonNetBridge.NotifyScriptReloaded(_resolvedPyPath, NodeId);
                        UpdateStatusOnUi($"Python 热重载: {DateTime.Now:HH:mm:ss}", true);
                    };
                    _fileWatcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
            }
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length > 0)
            {
                if (pinIndex == 0) _valA = packet.Channels[0];
                else if (pinIndex == 1) _valB = packet.Channels[0];
            }

            long nowTick = Stopwatch.GetTimestamp();
            double dt = _lastTick > 0
                ? Math.Clamp((nowTick - _lastTick) / (double)Stopwatch.Frequency, 1e-6, 1.0)
                : 0.001;
            _lastTick = nowTick;

            long swStart = nowTick;
            double outVal = _valA;

            if (_bypassToggleParam.BoolValue)
            {
                outVal = _valA;
            }
            else if (_modeParam.Value.Contains("Expression", StringComparison.OrdinalIgnoreCase))
            {
                lock (_execLock)
                {
                    try
                    {
                        _evaluator.Variables["a"] = _valA;
                        _evaluator.Variables["b"] = _valB;
                        _evaluator.Variables["prev_a"] = _prevA;
                        _evaluator.Variables["dt"] = dt;
                        object res = _evaluator.Evaluate(_expressionParam.Value);
                        if (res is IConvertible conv)
                        {
                            outVal = conv.ToDouble(CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        outVal = _valA * _valB;
                    }
                }
            }
            else
            {
                outVal = ExecutePythonSandbox(_valA, _valB);
            }

            _prevA = _valA;

            long swEnd = Stopwatch.GetTimestamp();
            double costUs = (swEnd - swStart) * (1000000.0 / Stopwatch.Frequency);

            var outPkt = new BlueprintPacket(new[] { outVal }, $"RES:{outVal:F3}", "MATH_OUT", DateTime.Now);
            EmitPacket(0, outPkt);

            UpdateStatusOnUi($"输出: {outVal:F3} ({costUs:F1}μs)", true);
            return Task.CompletedTask;
        }

        private double ExecutePythonSandbox(double a, double b)
        {
            if (string.IsNullOrEmpty(_resolvedPyPath) || !File.Exists(_resolvedPyPath))
            {
                _resolvedPyPath = ResolveScriptFilePath(_pyScriptPathParam.Value);
            }

            if (!string.IsNullOrEmpty(_resolvedPyPath) && File.Exists(_resolvedPyPath))
            {
                var defaults = PythonAlgoRepoService.Instance.GetDefaultParamsFor(Path.GetFileName(_resolvedPyPath));
                defaults["a"] = a;
                defaults["b"] = b;
                string paramsJson = PythonAlgoRepoService.SerializeParams(defaults);

                if (PythonNetBridge.TryExecuteInProcess(
                        _resolvedPyPath,
                        new[] { a, b },
                        paramsJson,
                        out double[] pyResult,
                        out _,
                        stateKey: NodeId) &&
                    pyResult.Length > 0)
                {
                    return pyResult[0];
                }

                // 若本机未加载 python3*.dll，根据脚本算子类型执行一致的参数化数学核
                string fileName = Path.GetFileName(_resolvedPyPath).ToLowerInvariant();
                if (fileName.Contains("ema"))
                {
                    double alpha = defaults.TryGetValue("alpha", out double al) ? Math.Clamp(al, 0.005, 1.0) : 0.25;
                    _kalmanX = alpha * a + (1.0 - alpha) * _kalmanX;
                    return _kalmanX;
                }
                if (fileName.Contains("scale"))
                {
                    double scale = defaults.TryGetValue("scale", out double sc) ? sc : b;
                    double offset = defaults.TryGetValue("offset", out double of) ? of : 0.0;
                    return a * scale + offset;
                }
            }

            // 1D 卡尔曼递归估计 (以 b 映射动态测量噪声比例)
            double q = 0.022;
            double r = Math.Max(1e-5, Math.Abs(b) * 0.12);
            double pPred = _kalmanP + q;
            double k = pPred / (pPred + r);
            _kalmanX = _kalmanX + k * (a - _kalmanX);
            _kalmanP = (1.0 - k) * pPred;
            return _kalmanX;
        }

        public override void Dispose()
        {
            _fileWatcher?.Dispose();
            base.Dispose();
        }
    }
}
