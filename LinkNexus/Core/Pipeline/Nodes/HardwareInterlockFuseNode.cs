using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 微秒级软硬联锁保护断路器节点 (HardwareInterlockFuseNode)
    /// 监测母线电流 (IbusCurrent) 与电机转速/速度 (MotorVelocity)，内置可配置阈值比较器与消抖滤波器
    /// 触发过流或堵转异常瞬间：
    /// 1. 立即点火白色 ExecTrigger 联锁引脚切断母舰供电；
    /// 2. 冻结环形缓冲区故障快照；
    /// 3. 自动将前后 2000 个采样点转储为 CrashDump_{Timestamp}.csv 审计黑匣子。
    /// </summary>
    public sealed class HardwareInterlockFuseNode : NodeBase
    {
        private const string IconSvg = "M11,15H13V17H11V15M11,7H13V13H11V7M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20Z";

        private readonly NodeParameter _maxCurrentParam;
        private readonly NodeParameter _maxVelocityParam;
        private readonly NodeParameter _debounceMsParam;
        private readonly NodeParameter _tripActionBtn;

        private bool _isTripped;
        private long _overcurrentStartTicks;
        private readonly Queue<(long Seq, double T, double Current, double Velocity)> _historyRing = new(2048);
        private readonly object _fuseLock = new();

        public ConnectorViewModel CurrentIn { get; }
        public ConnectorViewModel VelocityIn { get; }
        public ConnectorViewModel ResetTripIn { get; }

        public ConnectorViewModel TripTriggerOut { get; }
        public ConnectorViewModel SafeStatusOut { get; }

        public HardwareInterlockFuseNode()
            : base(
                title: "软硬联锁断路保护器",
                categoryName: "Control & AI MCP",
                role: NodeRole.Sink,
                headerAccentHex: "#9333EA",
                iconGeometry: IconSvg,
                customHeaderGradient: PurpleGradient)
        {
            // 输入引脚：支持 Blender/UE5 风格内联预设值
            CurrentIn = AddInputConnector("母线电流 (Current mA)", PinDataType.FloatStream);
            CurrentIn.HasInlineWidget = true;
            CurrentIn.InlineValue = "250.0";

            VelocityIn = AddInputConnector("执行转速 (Velocity)", PinDataType.FloatStream);
            VelocityIn.HasInlineWidget = true;
            VelocityIn.InlineValue = "1200.0";

            ResetTripIn = AddInputConnector("复位断路器 (Reset)", PinDataType.Trigger);

            // 输出端口：白色 Unreal 风格执行触发引脚
            TripTriggerOut = AddOutputConnector("熔断切断 (ExecTrigger)", PinDataType.Trigger);
            SafeStatusOut = AddOutputConnector("安全状态 (SafeStatus)", PinDataType.FloatStream);

            // 参数定义
            _maxCurrentParam = AddSliderParameter("过流保护阈值 (mA)", min: 100.0, max: 5000.0, step: 50.0, initialValue: 800.0);
            _maxCurrentParam.GhostDoubleValue = 1200.0;
            _maxCurrentParam.GhostReason = "AI: 提高限流至 1200mA 以容忍电机启动瞬态突加负载";

            _maxVelocityParam = AddSliderParameter("超速阈值 (RPM)", min: 500.0, max: 10000.0, step: 100.0, initialValue: 4000.0);

            _debounceMsParam = AddSliderParameter("消抖滤波窗口 (ms)", min: 1.0, max: 50.0, step: 1.0, initialValue: 5.0);

            _tripActionBtn = AddActionParameter("立即复位断路器", () => ResetTrip());

            StatusBadge = "联锁监控中 · 状态正常";
        }

        public void ResetTrip()
        {
            lock (_fuseLock)
            {
                _isTripped = false;
                _overcurrentStartTicks = 0;
                StatusBadge = "联锁已复位 · 状态正常";
            }
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            if (inputPinIndex == 2 && sample.Value > 0.5) // ResetTripIn
            {
                ResetTrip();
                return Task.CompletedTask;
            }

            double cur = CurrentIn.IsConnected ? (inputPinIndex == 0 ? sample.Value : CurrentIn.InlineDoubleValue) : CurrentIn.InlineDoubleValue;
            double vel = VelocityIn.IsConnected ? (inputPinIndex == 1 ? sample.Value : VelocityIn.InlineDoubleValue) : VelocityIn.InlineDoubleValue;

            lock (_fuseLock)
            {
                if (_historyRing.Count >= 2000) _historyRing.Dequeue();
                _historyRing.Enqueue((sample.Sequence, sample.TimeSeconds, cur, vel));

                if (_isTripped)
                {
                    StatusBadge = "【断路已跳闸】请复位";
                    return Task.CompletedTask;
                }

                double maxI = _maxCurrentParam.DoubleValue;
                double maxV = _maxVelocityParam.DoubleValue;

                bool isExceeded = cur > maxI || vel > maxV;
                if (isExceeded)
                {
                    if (_overcurrentStartTicks == 0) _overcurrentStartTicks = DateTime.UtcNow.Ticks;

                    double durationMs = (DateTime.UtcNow.Ticks - _overcurrentStartTicks) / 10000.0;
                    if (durationMs >= _debounceMsParam.DoubleValue)
                    {
                        TriggerEmergencyTrip(cur, vel, sample);
                    }
                }
                else
                {
                    _overcurrentStartTicks = 0;
                }
            }

            PushMicroSparklineSample(cur);
            return Task.CompletedTask;
        }

        private void TriggerEmergencyTrip(double current, double velocity, MetricSample sample)
        {
            _isTripped = true;
            StatusBadge = $"【紧急跳闸】I={current:F1}mA V={velocity:F0}";

            // 1. 点火白色 ExecTrigger 触发引脚
            var tripSample = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "TRIP", 1.0);
            EmitSample(0, in tripSample);

            // 2. 输出不安全电平
            var safeSample = new MetricSample(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, "SAFE", 0.0);
            EmitSample(1, in safeSample);

            // 3. 转储前后 2000 点至 CrashDump_{Timestamp}.csv 黑匣子
            Task.Run(() =>
            {
                try
                {
                    string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkNexus", "CrashDumps");
                    Directory.CreateDirectory(dumpDir);

                    string filePath = Path.Combine(dumpDir, $"CrashDump_{DateTime.Now:yyyyMMdd_HHmmss_fff}.csv");
                    var sb = new StringBuilder();
                    sb.AppendLine("Sequence,TimeSeconds,Current_mA,Velocity_RPM,Status");

                    lock (_fuseLock)
                    {
                        foreach (var item in _historyRing)
                        {
                            sb.AppendLine($"{item.Seq},{item.T:F4},{item.Current:F2},{item.Velocity:F2},TRIP_SNAP");
                        }
                    }

                    File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            });
        }
    }
}
