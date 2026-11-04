using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 基于 MathNet.Numerics.LinearAlgebra 的矩阵与空间变换卡 (MatrixTransformNode)
    /// 接收 Vector3Stream 或 FloatStream 组装为列向量 X
    /// 内部提供紧凑 3x3 矩阵网格与偏置向量 B，实时解算 Y = M * X + B
    /// </summary>
    public sealed class MatrixTransformNode : NodeBase
    {
        private const string IconSvg = "M4,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4M4,6V18H20V6H4M6,8H8V10H6V8M10,8H14V10H10V8M16,8H18V10H16V8M6,12H8V14H6V12M10,12H14V14H10V12M16,12H18V14H16V12M6,16H8V18H6V16M10,16H14V18H10V16M16,16H18V18H16V16Z";

        private readonly NodeParameter _presetParam;
        private readonly NodeParameter _matrixTextParam;
        private readonly NodeParameter _biasTextParam;

        private Matrix<double> _matrixM = DenseMatrix.CreateIdentity(3);
        private Vector<double> _vectorB = DenseVector.Create(3, 0.0);
        private readonly object _matrixLock = new();

        private double _x0 = 1.0;
        private double _x1;
        private double _x2;

        public override string NodeTypeKey => "matrix_transform";
        public override double CardWidth => 320.0;

        public MatrixTransformNode(double x = 700, double y = 200)
            : base("矩阵空间变换算子", "Math & Calculus", "#D97706", IconSvg, x, y)
        {
            var vIn = AddInputPin("向量输入 (Vec3 In)", PinDataType.Vector3Stream);
            vIn.HasInlineWidget = true;
            vIn.InlineValue = "1.0, 0.0, 0.0";

            AddOutputPin("变换向量 (Vec3 Out)", PinDataType.Vector3Stream);
            AddOutputPin("分量 Y0", PinDataType.FloatStream);
            AddOutputPin("分量 Y1", PinDataType.FloatStream);
            AddOutputPin("分量 Y2", PinDataType.FloatStream);

            _presetParam = new NodeParameter("preset", "变换预设", NodeParamKind.Combo, "Identity (3x3 单位矩阵)");
            _presetParam.Options.Add("Identity (3x3 单位矩阵)");
            _presetParam.Options.Add("YawRotation_45Deg (绕Z轴旋转45度)");
            _presetParam.Options.Add("Mag_Ellipsoid_Calib (磁力计椭球逆校准)");
            _presetParam.Options.Add("Mecanum_Kinematics (麦轮底盘分配矩阵 J)");
            _presetParam.ValueChangedCallback = preset => ApplyPreset(preset);

            _matrixTextParam = new NodeParameter("mat_txt", "矩阵 M (3x3 行分号)", NodeParamKind.Text, "1, 0, 0; 0, 1, 0; 0, 0, 1")
            {
                ValueChangedCallback = txt => ParseMatrix(txt)
            };

            _biasTextParam = new NodeParameter("bias_txt", "偏置向量 B [b0,b1,b2]", NodeParamKind.Text, "0.0, 0.0, 0.0")
            {
                ValueChangedCallback = txt => ParseBias(txt)
            };

            Parameters.Add(_presetParam);
            Parameters.Add(_matrixTextParam);
            Parameters.Add(_biasTextParam);

            ApplyPreset(_presetParam.Value);
            UpdateStatusOnUi("矩阵变换就绪", true);
        }

        private void ApplyPreset(string preset)
        {
            if (preset.Contains("YawRotation"))
            {
                double rad = 45.0 * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                _matrixTextParam.Value = $"{cos:F3}, {-sin:F3}, 0; {sin:F3}, {cos:F3}, 0; 0, 0, 1";
                _biasTextParam.Value = "0.0, 0.0, 0.0";
            }
            else if (preset.Contains("Mag_Ellipsoid"))
            {
                _matrixTextParam.Value = "1.045, -0.018, 0.009; -0.018, 0.982, 0.015; 0.009, 0.015, 1.012";
                _biasTextParam.Value = "-15.2, 42.1, 8.5";
            }
            else if (preset.Contains("Mecanum"))
            {
                _matrixTextParam.Value = "1.0, -1.0, -0.25; 1.0, 1.0, 0.25; 1.0, 1.0, -0.25";
                _biasTextParam.Value = "0.0, 0.0, 0.0";
            }
            else
            {
                _matrixTextParam.Value = "1.0, 0.0, 0.0; 0.0, 1.0, 0.0; 0.0, 0.0, 1.0";
                _biasTextParam.Value = "0.0, 0.0, 0.0";
            }
        }

        private void ParseMatrix(string txt)
        {
            try
            {
                var rows = txt.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (rows.Length >= 3)
                {
                    double[,] data = new double[3, 3];
                    for (int r = 0; r < 3; r++)
                    {
                        var cols = rows[r].Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int c = 0; c < Math.Min(3, cols.Length); c++)
                        {
                            if (double.TryParse(cols[c], out double d)) data[r, c] = d;
                        }
                    }
                    lock (_matrixLock)
                    {
                        _matrixM = DenseMatrix.OfArray(data);
                    }
                }
            }
            catch { }
        }

        private void ParseBias(string txt)
        {
            try
            {
                var parts = txt.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                double[] b = new double[3];
                for (int i = 0; i < Math.Min(3, parts.Length); i++)
                {
                    if (double.TryParse(parts[i], out double d)) b[i] = d;
                }
                lock (_matrixLock)
                {
                    _vectorB = DenseVector.OfArray(b);
                }
            }
            catch { }
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length >= 3)
            {
                _x0 = packet.Channels[0];
                _x1 = packet.Channels[1];
                _x2 = packet.Channels[2];
            }
            else if (packet.Channels.Length > 0)
            {
                _x0 = packet.Channels[0];
            }

            Vector<double> yVec;
            lock (_matrixLock)
            {
                var xVec = DenseVector.OfArray(new[] { _x0, _x1, _x2 });
                yVec = _matrixM.Multiply(xVec).Add(_vectorB);
            }

            double y0 = yVec[0];
            double y1 = yVec[1];
            double y2 = yVec[2];

            var vPkt = new BlueprintPacket(new[] { y0, y1, y2 }, $"VEC3:[{y0:F1},{y1:F1},{y2:F1}]", "MAT_VEC3", packet.Timestamp);
            var p0 = new BlueprintPacket(new[] { y0 }, $"Y0:{y0:F2}", "MAT_Y0", packet.Timestamp);
            var p1 = new BlueprintPacket(new[] { y1 }, $"Y1:{y1:F2}", "MAT_Y1", packet.Timestamp);
            var p2 = new BlueprintPacket(new[] { y2 }, $"Y2:{y2:F2}", "MAT_Y2", packet.Timestamp);

            EmitPacket(0, vPkt);
            EmitPacket(1, p0);
            EmitPacket(2, p1);
            EmitPacket(3, p2);

            UpdateStatusOnUi($"Y=[{y0:F1}, {y1:F1}, {y2:F1}]", true);
            return Task.CompletedTask;
        }
    }
}
