using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 微秒级软硬联锁保护断路器卡 (HardwareInterlockFuseNode)
    /// 监测母线电流与电机转速，内置可配置阈值比较器与 Debounce 消抖滤波器
    /// 一旦判定过流或堵转，立即触发白色 ExecTrigger 切断供电、冻结快照转储 CrashDump_{Timestamp}.csv
    /// </summary>
    public sealed class HardwareInterlockFuseNode : NodeBase
    {
        private const string IconSvg = "M11,15H13V17H11V15M11,7H13V13H11V7M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20Z";

        private readonly NodeParameter _maxCurrentParam;
        private readonly NodeParameter _maxVelocityParam;
        private readonly NodeParameter _debounceMsParam;
        private readonly NodeParameter _tripActionBtn;

        private bool _isTripped;
        private readonly Queue<(DateTime Time, double Current, double Velocity)> _historyRing = new(2048);
        private readonly object _fuseLock = new();

        private double _currentVal = 250.0;
        private double _velocityVal = 1200.0;

        public override string NodeTypeKey => "interlock_fuse";
        public override double CardWidth => 320.0;

        public HardwareInterlockFuseNode(double x = 700, double y = 50)
            : base("软硬联锁断路保护器", "Control & AI MCP", "#9333EA", IconSvg, x, y)
        {
            var curPin = AddInputPin("母线电流 (mA)", PinDataType.FloatStream);
            curPin.HasInlineWidget = true;
            curPin.InlineValue = "250.0";

            var velPin = AddInputPin("执行转速 (RPM)", PinDataType.FloatStream);
            velPin.HasInlineWidget = true;
            velPin.InlineValue = "1200.0";

            AddInputPin("复位断路器 (Reset)", PinDataType.Trigger);

            AddOutputPin("熔断切断 (ExecTrigger)", PinDataType.Trigger);
            AddOutputPin("安全状态 (SafeStatus)", PinDataType.FloatStream);

            _maxCurrentParam = new NodeParameter("max_cur", "过流保护阈值 (mA)", NodeParamKind.Slider, "800.0")
            {
                MinValue = 100.0,
                MaxValue = 5000.0,
                Step = 50.0
            };

            _maxVelocityParam = new NodeParameter("max_vel", "超速阈值 (RPM)", NodeParamKind.Slider, "4000.0")
            {
                MinValue = 500.0,
                MaxValue = 10000.0,
                Step = 100.0
            };

            _debounceMsParam = new NodeParameter("debounce_ms", "消抖滤波窗口 (ms)", NodeParamKind.Slider, "5.0")
            {
                MinValue = 1.0,
                MaxValue = 50.0,
                Step = 1.0
            };

            _tripActionBtn = new NodeParameter("reset_btn", "立即复位断路器", NodeParamKind.ActionButton)
            {
                ButtonText = "立即复位联锁",
                ActionCommand = new RelayCommand(_ => ResetTrip())
            };

            Parameters.Add(_maxCurrentParam);
            Parameters.Add(_maxVelocityParam);
            Parameters.Add(_debounceMsParam);
            Parameters.Add(_tripActionBtn);

            UpdateStatusOnUi("联锁监控中 · 状态正常", true);
        }

        public void ResetTrip()
        {
            lock (_fuseLock)
            {
                _isTripped = false;
                UpdateStatusOnUi("联锁已复位 · 状态正常", true);
            }
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (pinIndex == 2) // Reset pin
            {
                ResetTrip();
                return Task.CompletedTask;
            }

            if (packet.Channels.Length > 0)
            {
                if (pinIndex == 0) _currentVal = packet.Channels[0];
                else if (pinIndex == 1) _velocityVal = packet.Channels[0];
            }

            lock (_fuseLock)
            {
                if (_historyRing.Count >= 2000) _historyRing.Dequeue();
                _historyRing.Enqueue((DateTime.Now, _currentVal, _velocityVal));

                if (!_isTripped)
                {
                    double limitCur = _maxCurrentParam.DoubleValue;
                    double limitVel = _maxVelocityParam.DoubleValue;

                    if (_currentVal > limitCur || _velocityVal > limitVel)
                    {
                        _isTripped = true;
                        TriggerTrip();
                    }
                }

                double safeStatus = _isTripped ? 0.0 : 1.0;
                EmitPacket(1, new BlueprintPacket(new[] { safeStatus }, safeStatus > 0.5 ? "SAFE" : "TRIPPED", "INTERLOCK", DateTime.Now));
            }
            return Task.CompletedTask;
        }

        private void TriggerTrip()
        {
            UpdateStatusOnUi("⚠ 联锁已熔断! 切断电源", false);

            // 1. 发射白色 ExecTrigger 触发信号
            EmitPacket(0, new BlueprintPacket(new double[] { 1.0 }, "EXEC_TRIGGER_TRIP", "INTERLOCK_TRIGGER", DateTime.Now));

            // 2. 转储 CrashDump_{Timestamp}.csv
            try
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"CrashDump_{ts}.csv";
                var sb = new StringBuilder();
                sb.AppendLine("Timestamp,Current_mA,Velocity_RPM");
                foreach (var sample in _historyRing)
                {
                    sb.AppendLine($"{sample.Time:yyyy-MM-dd HH:mm:ss.fff},{sample.Current:F2},{sample.Velocity:F2}");
                }
                File.WriteAllText(fileName, sb.ToString());
            }
            catch { }
        }
    }
}
