using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// HelixToolkit 3D 空间姿态动捕与非线性相空间轨迹视口 (Helix3DViewportControl)
    /// 采用姿态与轨迹点合帧刷新机制 (VSync Coalescing)，彻底杜绝高频串口推流时产生堆积的 Dispatcher.BeginInvoke
    /// </summary>
    public partial class Helix3DViewportControl : UserControl
    {
        private const int MaxPhasePoints = 2000;

        private readonly object _syncLock = new();
        private readonly Queue<Point3D> _pendingPhaseQueue = new(256);

        private double _tareRoll = 0.0;
        private double _tarePitch = 0.0;
        private double _tareYaw = 0.0;

        private double _lastRawRoll = 0.0;
        private double _lastRawPitch = 0.0;
        private double _lastRawYaw = 0.0;

        private bool _hasPendingAttitude = false;
        private int _flushScheduled = 0;
        private long _lastFlushTick = 0;

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
            lock (_syncLock)
            {
                _lastRawRoll = rawRollDeg;
                _lastRawPitch = rawPitchDeg;
                _lastRawYaw = rawYawDeg;
                _hasPendingAttitude = true;
            }

            ScheduleCoalescedFlush();
        }

        public void PushPhasePoint(double x, double y, double z)
        {
            var p = new Point3D(x, y, z);
            lock (_syncLock)
            {
                if (_pendingPhaseQueue.Count >= MaxPhasePoints)
                {
                    _pendingPhaseQueue.Dequeue();
                }
                _pendingPhaseQueue.Enqueue(p);
            }

            ScheduleCoalescedFlush();
        }

        private void ScheduleCoalescedFlush()
        {
            long nowTick = Stopwatch.GetTimestamp();
            double elapsedMs = (nowTick - Interlocked.Read(ref _lastFlushTick)) * (1000.0 / Stopwatch.Frequency);
            double minIntervalMs = DisplayRefreshService.TargetFrameIntervalMs * 0.85;

            if (elapsedMs < minIntervalMs && Volatile.Read(ref _flushScheduled) == 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(FlushPendingVisuals, DispatcherPriority.Render);
            }
        }

        private void FlushPendingVisuals()
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            Interlocked.Exchange(ref _lastFlushTick, Stopwatch.GetTimestamp());

            if (!IsVisible) return;

            bool updateAttitude;
            double rawRoll, rawPitch, rawYaw;
            List<Point3D>? batchPoints = null;

            lock (_syncLock)
            {
                updateAttitude = _hasPendingAttitude;
                _hasPendingAttitude = false;
                rawRoll = _lastRawRoll;
                rawPitch = _lastRawPitch;
                rawYaw = _lastRawYaw;

                if (_pendingPhaseQueue.Count > 0)
                {
                    batchPoints = new List<Point3D>(_pendingPhaseQueue.Count);
                    while (_pendingPhaseQueue.Count > 0)
                    {
                        batchPoints.Add(_pendingPhaseQueue.Dequeue());
                    }
                }
            }

            if (updateAttitude)
            {
                double calRoll = NormalizeDegrees(rawRoll - _tareRoll);
                double calPitch = Math.Clamp(rawPitch - _tarePitch, -89.5, 89.5);
                double calYaw = NormalizeDegrees(rawYaw - _tareYaw);

                var qYaw = new Quaternion(new Vector3D(0, 0, 1), -calYaw);
                var qPitch = new Quaternion(new Vector3D(0, 1, 0), calPitch);
                var qRoll = new Quaternion(new Vector3D(1, 0, 0), calRoll);

                Quaternion combined = qYaw * qPitch * qRoll;
                combined.Normalize();
                AttitudeRotation.Quaternion = combined;

                TxtAttitude.Text = string.Create(CultureInfo.InvariantCulture,
                    $"Roll: {calRoll:+0.0;-0.0;0.0}° | Pitch: {calPitch:+0.0;-0.0;0.0}° | Yaw: {calYaw:+0.0;-0.0;0.0}°");
            }

            if (batchPoints != null && batchPoints.Count > 0)
            {
                for (int i = 0; i < batchPoints.Count; i++)
                {
                    Point3D p = batchPoints[i];
                    if (_lastPhasePoint.HasValue)
                    {
                        PhaseSpaceLines.Points.Add(_lastPhasePoint.Value);
                        PhaseSpaceLines.Points.Add(p);
                    }
                    _lastPhasePoint = p;
                }

                while (PhaseSpaceLines.Points.Count > MaxPhasePoints * 2)
                {
                    PhaseSpaceLines.Points.RemoveAt(0);
                    PhaseSpaceLines.Points.RemoveAt(0);
                }

                TxtPhasePoints.Text = $"相空间轨迹段: {PhaseSpaceLines.Points.Count / 2} / {MaxPhasePoints}";
            }
        }

        private static double NormalizeDegrees(double angle)
        {
            while (angle > 180.0) angle -= 360.0;
            while (angle < -180.0) angle += 360.0;
            return angle;
        }

        private void BtnTare_Click(object sender, RoutedEventArgs e)
        {
            lock (_syncLock)
            {
                _tareRoll = _lastRawRoll;
                _tarePitch = _lastRawPitch;
                _tareYaw = _lastRawYaw;
            }
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
            lock (_syncLock)
            {
                _pendingPhaseQueue.Clear();
            }
            PhaseSpaceLines.Points.Clear();
            _lastPhasePoint = null;
            TxtPhasePoints.Text = "相空间点数: 0 / 2000";
        }
    }
}
