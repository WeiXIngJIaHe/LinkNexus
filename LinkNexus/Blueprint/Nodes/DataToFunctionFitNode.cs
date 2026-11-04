using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;
using MathNet.Numerics;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 数据直接变函数：实时最小二乘拟合与代码生成算子卡 (DataToFunctionFitNode)
    /// 调用 MathNet.Numerics.Fit.Polynomial 实时拟合多项式函数 f(x) 并计算决定系数 R²
    /// 具备 [生成 C 宏/函数] 一键将霍纳法则优化的 C 代码复制到剪贴板，同时将拟合函数作用于 Eval_X_In
    /// </summary>
    public sealed class DataToFunctionFitNode : NodeBase
    {
        private const string IconSvg = "M16,6L18.29,8.29L13.41,13.17L9.41,9.17L2,16.59L3.41,18L9.41,12L13.41,16L19.71,9.71L22,12V6H16Z";

        private readonly NodeParameter _polyOrderParam;
        private readonly NodeParameter _windowSizeParam;
        private readonly NodeParameter _formulaDisplayParam;
        private readonly NodeParameter _r2DisplayParam;
        private readonly NodeParameter _generateCCodeBtn;

        private readonly Queue<(double X, double Y)> _sampleWindow = new(512);
        private double[] _polyCoeffs = new[] { 0.0, 1.0 };
        private double _rSquared = 1.0;
        private readonly object _fitLock = new();

        private double _currX = 1.0;
        private double _currY = 2.0;
        private double _evalX = 5.0;

        public override string NodeTypeKey => "function_fit";
        public override double CardWidth => 330.0;

        public DataToFunctionFitNode(double x = 700, double y = 360)
            : base("多项式拟合与代码生成", "Math & Calculus", "#D97706", IconSvg, x, y)
        {
            var xIn = AddInputPin("自变量样本 (X)", PinDataType.FloatStream);
            xIn.HasInlineWidget = true;
            xIn.InlineValue = "1.0";

            var yIn = AddInputPin("因变量样本 (Y)", PinDataType.FloatStream);
            yIn.HasInlineWidget = true;
            yIn.InlineValue = "2.0";

            var evalXIn = AddInputPin("求值自变量 (Eval_X)", PinDataType.FloatStream);
            evalXIn.HasInlineWidget = true;
            evalXIn.InlineValue = "5.0";

            AddOutputPin("函数输出 f(x)", PinDataType.FloatStream);
            AddOutputPin("决定系数 (R²)", PinDataType.FloatStream);

            _polyOrderParam = new NodeParameter("poly_order", "多项式阶数", NodeParamKind.Combo, "2阶抛物线 (Quadratic)");
            _polyOrderParam.Options.Add("1阶线性拟合 (Linear)");
            _polyOrderParam.Options.Add("2阶抛物线 (Quadratic)");
            _polyOrderParam.Options.Add("3阶多项式 (Cubic)");

            _windowSizeParam = new NodeParameter("win_size", "拟合滑动窗口点数", NodeParamKind.Combo, "64 点");
            _windowSizeParam.Options.Add("32 点");
            _windowSizeParam.Options.Add("64 点");
            _windowSizeParam.Options.Add("128 点");
            _windowSizeParam.Options.Add("256 点");

            _formulaDisplayParam = new NodeParameter("formula_txt", "拟合多项式", NodeParamKind.ReadOnlyBadge, "f(x) = 等待样本中...");
            _r2DisplayParam = new NodeParameter("r2_txt", "拟合优度 R²", NodeParamKind.ReadOnlyBadge, "R² = --");

            _generateCCodeBtn = new NodeParameter("gen_c_btn", "生成 C 宏/霍纳函数", NodeParamKind.ActionButton)
            {
                ButtonText = "生成霍纳 C 函数并复制",
                ActionCommand = new RelayCommand(_ => CopyHornerFunctionToClipboard())
            };

            Parameters.Add(_polyOrderParam);
            Parameters.Add(_windowSizeParam);
            Parameters.Add(_formulaDisplayParam);
            Parameters.Add(_r2DisplayParam);
            Parameters.Add(_generateCCodeBtn);

            UpdateStatusOnUi("拟合引擎就绪", true);
        }

        private void CopyHornerFunctionToClipboard()
        {
            try
            {
                string cCode;
                lock (_fitLock)
                {
                    cCode = BuildHornerCCode(_polyCoeffs, _rSquared);
                }

                var thread = new Thread(() =>
                {
                    try
                    {
                        Clipboard.SetText(cCode);
                    }
                    catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(500);

                UpdateStatusOnUi("已复制霍纳 C 函数至剪贴板!", true);
            }
            catch (Exception ex)
            {
                UpdateStatusOnUi($"复制失败: {ex.Message}", false);
            }
        }

        private string BuildHornerCCode(double[] coeffs, double r2)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/* ========================================================================= */");
            sb.AppendLine($"/* LinkNexus Auto-Generated Horner Calibration Function (R² = {r2:F4}) */");
            sb.AppendLine($"/* Generated At: {DateTime.Now:yyyy-MM-dd HH:mm:ss} */");
            sb.AppendLine("/* ========================================================================= */");
            sb.AppendLine("#include <math.h>\n");
            sb.AppendLine("static inline double Calibrate_f(double x)");
            sb.AppendLine("{");

            if (coeffs.Length == 2)
            {
                sb.AppendLine($"    return {coeffs[0]:G6} + x * ({coeffs[1]:G6});");
            }
            else if (coeffs.Length == 3)
            {
                sb.AppendLine($"    return {coeffs[0]:G6} + x * ({coeffs[1]:G6} + x * ({coeffs[2]:G6}));");
            }
            else if (coeffs.Length == 4)
            {
                sb.AppendLine($"    return {coeffs[0]:G6} + x * ({coeffs[1]:G6} + x * ({coeffs[2]:G6} + x * ({coeffs[3]:G6})));");
            }
            else
            {
                sb.AppendLine($"    return {coeffs[0]:G6};");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length > 0)
            {
                if (inputPinIndex == 0) _currX = packet.Channels[0];
                else if (inputPinIndex == 1) _currY = packet.Channels[0];
                else if (inputPinIndex == 2) _evalX = packet.Channels[0];
            }

            double xVal = InputPins[0].IsConnected ? _currX : InputPins[0].InlineDoubleValue;
            double yVal = InputPins[1].IsConnected ? _currY : InputPins[1].InlineDoubleValue;
            double evalX = InputPins[2].IsConnected ? _evalX : InputPins[2].InlineDoubleValue;

            int maxCap = 64;
            if (_windowSizeParam.Value.Contains("32")) maxCap = 32;
            else if (_windowSizeParam.Value.Contains("128")) maxCap = 128;
            else if (_windowSizeParam.Value.Contains("256")) maxCap = 256;

            lock (_fitLock)
            {
                if (_sampleWindow.Count >= maxCap) _sampleWindow.Dequeue();
                _sampleWindow.Enqueue((xVal, yVal));

                if (_sampleWindow.Count >= 8)
                {
                    PerformFit(maxCap);
                }
            }

            double evalY;
            double r2;
            lock (_fitLock)
            {
                evalY = EvaluatePolynomial(_polyCoeffs, evalX);
                r2 = _rSquared;
            }

            var evalPkt = new BlueprintPacket(new[] { evalY }, $"EVAL:{evalY:F3}", "FIT_EVAL", packet.Timestamp);
            var r2Pkt = new BlueprintPacket(new[] { r2 }, $"R2:{r2:F4}", "FIT_R2", packet.Timestamp);

            EmitPacket(0, evalPkt);
            EmitPacket(1, r2Pkt);
            return Task.CompletedTask;
        }

        private void PerformFit(int windowCap)
        {
            int n = _sampleWindow.Count;
            double[] xArr = new double[n];
            double[] yArr = new double[n];
            int idx = 0;
            double ySum = 0.0;
            foreach (var pt in _sampleWindow)
            {
                xArr[idx] = pt.X;
                yArr[idx] = pt.Y;
                ySum += pt.Y;
                idx++;
            }

            int order = 2;
            if (_polyOrderParam.Value.Contains("1阶")) order = 1;
            else if (_polyOrderParam.Value.Contains("3阶")) order = 3;

            try
            {
                _polyCoeffs = Fit.Polynomial(xArr, yArr, order);

                double yMean = ySum / n;
                double ssTot = 0.0;
                double ssRes = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double pred = EvaluatePolynomial(_polyCoeffs, xArr[i]);
                    double res = yArr[i] - pred;
                    ssRes += res * res;
                    double tot = yArr[i] - yMean;
                    ssTot += tot * tot;
                }

                _rSquared = ssTot > 1e-12 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 1.0;
                double mse = ssRes / n;

                if (_polyCoeffs.Length == 2)
                    _formulaDisplayParam.Value = $"f(x) = {_polyCoeffs[0]:F3} + {_polyCoeffs[1]:F3}*x";
                else if (_polyCoeffs.Length == 3)
                    _formulaDisplayParam.Value = $"f(x) = {_polyCoeffs[0]:F3} + {_polyCoeffs[1]:F3}*x + {_polyCoeffs[2]:F3}*x²";
                else if (_polyCoeffs.Length >= 4)
                    _formulaDisplayParam.Value = $"f(x) = {_polyCoeffs[0]:F3} + {_polyCoeffs[1]:F3}*x + {_polyCoeffs[2]:F3}*x² + {_polyCoeffs[3]:F3}*x³";

                _r2DisplayParam.Value = $"R² = {_rSquared:F4} (MSE: {mse:F4})";
                UpdateStatusOnUi($"拟合 R²={_rSquared:F3}", true);
            }
            catch
            {
                _rSquared = 0.0;
            }
        }

        private static double EvaluatePolynomial(double[] coeffs, double x)
        {
            if (coeffs == null || coeffs.Length == 0) return 0.0;
            double result = 0.0;
            for (int i = coeffs.Length - 1; i >= 0; i--)
            {
                result = result * x + coeffs[i];
            }
            return result;
        }
    }
}
