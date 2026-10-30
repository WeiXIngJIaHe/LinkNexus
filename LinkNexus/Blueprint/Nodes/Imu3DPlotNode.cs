using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// IMU 三维姿态实时可视化卡片 (Imu3DPlotNode)
    /// 驱动 WPF Viewport3D 硬件加速三维机体棱柱与三轴坐标架，支持 R/P/Y 欧拉角与四元数流自动解算及一键姿态归零校准
    /// </summary>
    public sealed class Imu3DPlotNode : NodeBase
    {
        private const string IconSvg = "M12,2 L21,7 V17 L12,22 L3,17 V7 Z M12,2 V12 M21,7 L12,12 L3,7";

        private const string FormatEuler = "欧拉角 Roll,Pitch,Yaw (°)";
        private const string FormatQuat = "四元数 q0,q1,q2,q3 (w,x,y,z)";

        private readonly NodeParameter _formatParam;
        private readonly NodeParameter _tareBtnParam;

        private double _rawRoll;
        private double _rawPitch;
        private double _rawYaw;

        private double _tareRoll;
        private double _tarePitch;
        private double _tareYaw;

        private Quaternion _attitudeQuaternion = Quaternion.Identity;
        private string _rollDisplay = "0.0°";
        private string _pitchDisplay = "0.0°";
        private string _yawDisplay = "0.0°";
        private long _lastUiTickMs;

        public override string NodeTypeKey => "imu_3d";
        public override double CardWidth => 342.0;
        public override bool HasImu3DPreview => true;

        public Action<double, double, double>? AttitudeUpdatedCallback { get; set; }

        public Quaternion AttitudeQuaternion
        {
            get => _attitudeQuaternion;
            private set
            {
                if (_attitudeQuaternion != value)
                {
                    _attitudeQuaternion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RollDisplay
        {
            get => _rollDisplay;
            private set { if (_rollDisplay != value) { _rollDisplay = value; OnPropertyChanged(); } }
        }

        public string PitchDisplay
        {
            get => _pitchDisplay;
            private set { if (_pitchDisplay != value) { _pitchDisplay = value; OnPropertyChanged(); } }
        }

        public string YawDisplay
        {
            get => _yawDisplay;
            private set { if (_yawDisplay != value) { _yawDisplay = value; OnPropertyChanged(); } }
        }

        public Imu3DPlotNode(double x = 710, double y = 310)
            : base("IMU 三维姿态视窗", "空间三维解算", "#A855F7", IconSvg, x, y)
        {
            AddInputPin("姿态向量 (IMU)", PinDataType.ImuAttitude);
            AddOutputPin("欧拉角透传", PinDataType.ChannelStream);

            _formatParam = new NodeParameter("fmt", "解算协议", NodeParamKind.Combo, FormatEuler);
            _formatParam.Options.Add(FormatEuler);
            _formatParam.Options.Add(FormatQuat);

            _tareBtnParam = new NodeParameter("tare", "基准校准", NodeParamKind.ActionButton, "当前姿态一键归零 (Tare)")
            {
                ActionCommand = new RelayCommand(ApplyZeroTare)
            };

            Parameters.Add(_formatParam);
            Parameters.Add(_tareBtnParam);

            UpdateStatusOnUi("三维视窗就绪", true);
        }

        private void ApplyZeroTare()
        {
            _tareRoll = _rawRoll;
            _tarePitch = _rawPitch;
            _tareYaw = _rawYaw;
            UpdateAttitudeOnUi(0, 0, 0);
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            double[] ch = packet.Channels;
            if (ch.Length == 0) return Task.CompletedTask;

            double rollDeg;
            double pitchDeg;
            double yawDeg;

            if (_formatParam.Value == FormatQuat && ch.Length >= 4)
            {
                QuatToEulerDegrees(ch[0], ch[1], ch[2], ch[3], out rollDeg, out pitchDeg, out yawDeg);
            }
            else
            {
                rollDeg = ch.Length > 0 ? ch[0] : 0.0;
                pitchDeg = ch.Length > 1 ? ch[1] : 0.0;
                yawDeg = ch.Length > 2 ? ch[2] : 0.0;
            }

            _rawRoll = rollDeg;
            _rawPitch = pitchDeg;
            _rawYaw = yawDeg;

            double calRoll = NormalizeDegrees(rollDeg - _tareRoll);
            double calPitch = Math.Clamp(pitchDeg - _tarePitch, -89.5, 89.5);
            double calYaw = NormalizeDegrees(yawDeg - _tareYaw);

            EmitPacket(0, new BlueprintPacket(new[] { calRoll, calPitch, calYaw }, string.Empty, "IMU_3D", packet.Timestamp));

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastUiTickMs) >= 30)
            {
                Interlocked.Exchange(ref _lastUiTickMs, now);
                Application.Current?.Dispatcher.BeginInvoke(() => UpdateAttitudeOnUi(calRoll, calPitch, calYaw));
            }

            return Task.CompletedTask;
        }

        private void UpdateAttitudeOnUi(double rollDeg, double pitchDeg, double yawDeg)
        {
            // WPF Viewport3D 右手坐标系: +Y 朝上 (Yaw), +X 朝右 (Pitch), +Z 朝前机头 (Roll)
            var qYaw = new Quaternion(new Vector3D(0, 1, 0), -yawDeg);
            var qPitch = new Quaternion(new Vector3D(1, 0, 0), pitchDeg);
            var qRoll = new Quaternion(new Vector3D(0, 0, 1), -rollDeg);

            Quaternion combined = qYaw * qPitch * qRoll;
            combined.Normalize();
            AttitudeQuaternion = combined;

            RollDisplay = string.Create(CultureInfo.InvariantCulture, $"{rollDeg:+0.0;-0.0;0.0}°");
            PitchDisplay = string.Create(CultureInfo.InvariantCulture, $"{pitchDeg:+0.0;-0.0;0.0}°");
            YawDisplay = string.Create(CultureInfo.InvariantCulture, $"{yawDeg:+0.0;-0.0;0.0}°");
            AttitudeUpdatedCallback?.Invoke(rollDeg, pitchDeg, yawDeg);
        }

        private static double NormalizeDegrees(double angle)
        {
            while (angle > 180.0) angle -= 360.0;
            while (angle < -180.0) angle += 360.0;
            return angle;
        }

        private static void QuatToEulerDegrees(double w, double x, double y, double z, out double roll, out double pitch, out double yaw)
        {
            double norm = Math.Sqrt(w * w + x * x + y * y + z * z);
            if (norm > 1e-6)
            {
                w /= norm;
                x /= norm;
                y /= norm;
                z /= norm;
            }

            double sinrCosp = 2.0 * (w * x + y * z);
            double cosrCosp = 1.0 - 2.0 * (x * x + y * y);
            roll = Math.Atan2(sinrCosp, cosrCosp) * (180.0 / Math.PI);

            double sinp = 2.0 * (w * y - z * x);
            pitch = (Math.Abs(sinp) >= 1.0 ? Math.CopySign(Math.PI / 2.0, sinp) : Math.Asin(sinp)) * (180.0 / Math.PI);

            double sinyCosp = 2.0 * (w * z + x * y);
            double cosyCosp = 1.0 - 2.0 * (y * y + z * z);
            yaw = Math.Atan2(sinyCosp, cosyCosp) * (180.0 / Math.PI);
        }
    }
}
