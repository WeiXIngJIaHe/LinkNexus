using System;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 基于 MathNet.Numerics.LinearAlgebra 的矩阵与空间变换卡 (MatrixTransformNode)
    /// 接收 Vector3Stream 或 FloatStream 组装为列向量 X
    /// 内部提供紧凑 3x3/4x4 矩阵网格与偏置向量 B，实时解算 Y = M * X + B
    /// 内置磁力计椭球校准、麦克纳姆轮运动学分配矩阵等硬件调试预设
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

        private double _x0;
        private double _x1;
        private double _x2;

        public ConnectorViewModel VectorIn { get; }
        public ConnectorViewModel VectorOut { get; }
        public ConnectorViewModel Y0Out { get; }
        public ConnectorViewModel Y1Out { get; }
        public ConnectorViewModel Y2Out { get; }

        public MatrixTransformNode()
            : base(
                title: "矩阵空间变换算子",
                categoryName: "Math & Calculus",
                role: NodeRole.Transform,
                headerAccentHex: "#D97706",
                iconGeometry: IconSvg,
                customHeaderGradient: AmberGoldGradient)
        {
            VectorIn = AddInputConnector("向量输入 (Vec3 In)", PinDataType.Vector3Stream);
            VectorIn.HasInlineWidget = true;
            VectorIn.InlineValue = "1.0, 0.0, 0.0";

            VectorOut = AddOutputConnector("变换向量 (Vec3 Out)", PinDataType.Vector3Stream);
            Y0Out = AddOutputConnector("分量 Y0", PinDataType.FloatStream);
            Y1Out = AddOutputConnector("分量 Y1", PinDataType.FloatStream);
            Y2Out = AddOutputConnector("分量 Y2", PinDataType.FloatStream);

            _presetParam = AddComboParameter("变换预设", new[]
            {
                "Identity (3x3 单位矩阵)",
                "YawRotation_45Deg (绕Z轴旋转45度)",
                "Mag_Ellipsoid_Calib (磁力计椭球逆校准)",
                "Mecanum_Kinematics (麦轮底盘分配矩阵 J)"
            }, "Identity (3x3 单位矩阵)");

            _presetParam.ValueChangedCallback = preset => ApplyPreset(preset);

            _matrixTextParam = AddTextParameter("矩阵 M (3x3 行分号隔开)", "1, 0, 0; 0, 1, 0; 0, 0, 1");
            _matrixTextParam.ValueChangedCallback = txt => ParseMatrix(txt);

            _biasTextParam = AddTextParameter("偏置向量 B [b0, b1, b2]", "0.0, 0.0, 0.0");
            _biasTextParam.ValueChangedCallback = txt => ParseBias(txt);

            ApplyPreset(_presetParam.Value);
            StatusBadge = "矩阵空间变换就绪";
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
                // 磁力计软铁畸变逆矩阵
                _matrixTextParam.Value = "1.045, -0.018, 0.009; -0.018, 0.982, 0.015; 0.009, 0.015, 1.012";
                _biasTextParam.Value = "-15.2, 42.1, 8.5"; // 硬铁偏移
            }
            else if (preset.Contains("Mecanum"))
            {
                // 麦轮运动学解算矩阵
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

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            // Vector3Stream 携带 Value(X), Aux1(Y), Aux2(Z)
            _x0 = sample.Value;
            _x1 = sample.Aux1;
            _x2 = sample.Aux2;

            Vector<double> yVec;
            lock (_matrixLock)
            {
                var xVec = DenseVector.OfArray(new[] { _x0, _x1, _x2 });
                yVec = _matrixM.Multiply(xVec).Add(_vectorB);
            }

            double y0 = yVec[0];
            double y1 = yVec[1];
            double y2 = yVec[2];

            PushMicroSparklineSample(y0);
            StatusBadge = $"Y=[{y0:F1}, {y1:F1}, {y2:F1}]";

            // 输出 Vector3Stream 与 3 个独立分量
            var vOutSample = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "VEC3", y0, y1, y2);
            EmitSample(0, in vOutSample);

            var s0 = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "Y0", y0);
            var s1 = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "Y1", y1);
            var s2 = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "Y2", y2);
            EmitSample(1, in s0);
            EmitSample(2, in s1);
            EmitSample(3, in s2);

            return Task.CompletedTask;
        }
    }
}
