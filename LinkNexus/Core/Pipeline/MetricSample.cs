using System;
using System.Diagnostics;
using System.Globalization;

namespace LinkNexus.Core.Pipeline
{
    /// <summary>
    /// 零 GC 内存分配的强类型硬件指标采样单元 (MetricSample)
    /// 借鉴 Bonsai-RX 高性能响应式流设计，支持标量、三维姿态、多通道电气遥测的纳秒级极速流转
    /// </summary>
    public readonly struct MetricSample : IEquatable<MetricSample>
    {
        public long Sequence { get; }
        public long TimestampTicks { get; }
        public string Channel { get; }
        public double Value { get; }
        public double Aux1 { get; }
        public double Aux2 { get; }

        public double TimeSeconds => (double)TimestampTicks / Stopwatch.Frequency;

        public MetricSample(long sequence, long timestampTicks, double timeSeconds, int channelIndex, double value, double aux1 = 0.0, double aux2 = 0.0)
        {
            Sequence = sequence;
            TimestampTicks = timestampTicks != 0 ? timestampTicks : DateTime.UtcNow.Ticks;
            Channel = $"CH{channelIndex}";
            Value = value;
            Aux1 = aux1;
            Aux2 = aux2;
        }

        public MetricSample(long sequence, long timestampTicks, double timeSeconds, string channel, double value, double aux1 = 0.0, double aux2 = 0.0)
        {
            Sequence = sequence;
            TimestampTicks = timestampTicks != 0 ? timestampTicks : DateTime.UtcNow.Ticks;
            Channel = channel ?? string.Empty;
            Value = value;
            Aux1 = aux1;
            Aux2 = aux2;
        }

        public MetricSample(double value, string channel = "", double aux1 = 0.0, double aux2 = 0.0, long sequence = 0, long timestampTicks = 0)
        {
            Value = value;
            Channel = channel ?? string.Empty;
            Aux1 = aux1;
            Aux2 = aux2;
            Sequence = sequence;
            TimestampTicks = timestampTicks != 0 ? timestampTicks : DateTime.UtcNow.Ticks;
        }

        public static MetricSample Create(double value, string channel = "", double aux1 = 0.0, double aux2 = 0.0, long sequence = 0)
            => new(value, channel, aux1, aux2, sequence, DateTime.UtcNow.Ticks);

        public static MetricSample FromVector3(double x, double y, double z, string channel = "VEC3", long sequence = 0)
            => new(x, channel, y, z, sequence, DateTime.UtcNow.Ticks);

        public static MetricSample Empty => new(0.0, string.Empty, 0.0, 0.0, 0, 0);

        public bool Equals(MetricSample other)
            => Sequence == other.Sequence &&
               TimestampTicks == other.TimestampTicks &&
               Value.Equals(other.Value) &&
               Aux1.Equals(other.Aux1) &&
               Aux2.Equals(other.Aux2) &&
               string.Equals(Channel, other.Channel, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is MetricSample other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(Sequence, TimestampTicks, Value, Aux1, Aux2, Channel);

        public static bool operator ==(MetricSample left, MetricSample right) => left.Equals(right);
        public static bool operator !=(MetricSample left, MetricSample right) => !left.Equals(right);

        public override string ToString()
        {
            if (Aux1 == 0.0 && Aux2 == 0.0)
            {
                return string.IsNullOrEmpty(Channel)
                    ? Value.ToString("F3", CultureInfo.InvariantCulture)
                    : $"{Channel}: {Value.ToString("F3", CultureInfo.InvariantCulture)}";
            }

            return $"{Channel}[{Value.ToString("F2", CultureInfo.InvariantCulture)}, {Aux1.ToString("F2", CultureInfo.InvariantCulture)}, {Aux2.ToString("F2", CultureInfo.InvariantCulture)}]";
        }
    }
}
