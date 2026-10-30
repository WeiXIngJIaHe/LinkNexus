using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace LinkNexus.Blueprint.Controls
{
    public partial class Helix3DViewportControl : UserControl
    {
        private const int MaxPhasePoints = 2000;

        private double _tareRoll = 0.0;
        private double _tarePitch = 0.0;
        private double _tareYaw = 0.0;

        private double _lastRawRoll = 0.0;
        private double _lastRawPitch = 0.0;
        private double _lastRawYaw = 0.0;

        private readonly List<Point3D> _trajectoryPoints = new(MaxPhasePoints + 10);
        private Point3D? _lastPhasePoint;

        public Helix3DViewportControl()
        {
            InitializeComponent();
            Viewport3D.Camera.Position = new Point3D(24, 24, 18);
            Viewport3D.Camera.LookDirection = new Vector3D(-24, -24, -18);
            Viewport3D.Camera.UpDirection = new Vector3D(0, 0, 1);
        }

        public void UpdateAttitude(double rawRollDeg, double rawPitchDeg, double rawYawDeg)
        {
            _lastRawRoll = rawRollDeg;
            _lastRawPitch = rawPitchDeg;
            _lastRawYaw = rawYawDeg;

            double calRoll = NormalizeDegrees(rawRollDeg - _tareRoll);
            double calPitch = Math.Clamp(rawPitchDeg - _tarePitch, -89.5, 89.5);
            double calYaw = NormalizeDegrees(rawYawDeg - _tareYaw);

            Dispatcher.BeginInvoke(() =>
            {
                // Helix/WPF 3D: Z 向上, X 向右, Y 向前
                var qYaw = new Quaternion(new Vector3D(0, 0, 1), -calYaw);
                var qPitch = new Quaternion(new Vector3D(0, 1, 0), calPitch);
                var qRoll = new Quaternion(new Vector3D(1, 0, 0), calRoll);

                Quaternion combined = qYaw * qPitch * qRoll;
                combined.Normalize();
                AttitudeRotation.Quaternion = combined;

                TxtAttitude.Text = string.Create(CultureInfo.InvariantCulture,
                    $"Roll: {calRoll:+0.0;-0.0;0.0}° | Pitch: {calPitch:+0.0;-0.0;0.0}° | Yaw: {calYaw:+0.0;-0.0;0.0}°");
            });
        }

        public void PushPhasePoint(double x, double y, double z)
        {
            var p = new Point3D(x, y, z);
            Dispatcher.BeginInvoke(() =>
            {
                if (_lastPhasePoint.HasValue)
                {
                    PhaseSpaceLines.Points.Add(_lastPhasePoint.Value);
                    PhaseSpaceLines.Points.Add(p);

                    // 限制最大历史点数
                    while (PhaseSpaceLines.Points.Count > MaxPhasePoints * 2)
                    {
                        PhaseSpaceLines.Points.RemoveAt(0);
                        PhaseSpaceLines.Points.RemoveAt(0);
                    }

                    TxtPhasePoints.Text = $"相空间轨迹段: {PhaseSpaceLines.Points.Count / 2} / {MaxPhasePoints}";
                }
                _lastPhasePoint = p;
            });
        }

        private static double NormalizeDegrees(double angle)
        {
            while (angle > 180.0) angle -= 360.0;
            while (angle < -180.0) angle += 360.0;
            return angle;
        }

        private void BtnTare_Click(object sender, RoutedEventArgs e)
        {
            _tareRoll = _lastRawRoll;
            _tarePitch = _lastRawPitch;
            _tareYaw = _lastRawYaw;
            UpdateAttitude(_lastRawRoll, _lastRawPitch, _lastRawYaw);
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e)
        {
            Viewport3D.ResetCamera();
            Viewport3D.Camera.Position = new Point3D(24, 24, 18);
            Viewport3D.Camera.LookDirection = new Vector3D(-24, -24, -18);
            Viewport3D.Camera.UpDirection = new Vector3D(0, 0, 1);
        }

        private void BtnClearTrajectory_Click(object sender, RoutedEventArgs e)
        {
            PhaseSpaceLines.Points.Clear();
            _lastPhasePoint = null;
            TxtPhasePoints.Text = "相空间点数: 0 / 2000";
        }
    }
}
