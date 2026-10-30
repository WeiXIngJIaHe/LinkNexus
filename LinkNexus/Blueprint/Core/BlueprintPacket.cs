using System;
using System.Collections.Generic;
using System.Globalization;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// 流式蓝图底层数据包 (在各 NodeBase 的 Channel 管道间零锁高速传递)
    /// 兼容 VOFA+ FireWater 单流/多数据流前缀帧 (如 BUS:v,ma,w / XDS:v,ma,w)、IMU 姿态向量以及原始串口文本帧
    /// </summary>
    public sealed class BlueprintPacket
    {
        public double[] Channels { get; }
        public string RawText { get; }
        public string SourceTag { get; }
        public string StreamPrefix { get; }
        public DateTime Timestamp { get; }

        public BlueprintPacket(
            double[] channels,
            string rawText = "",
            string sourceTag = "STREAM",
            DateTime? timestamp = null,
            string streamPrefix = "")
        {
            Channels = channels ?? Array.Empty<double>();
            RawText = rawText ?? string.Empty;
            SourceTag = sourceTag ?? "STREAM";
            StreamPrefix = streamPrefix ?? string.Empty;
            Timestamp = timestamp ?? DateTime.Now;
        }

        /// <summary>
        /// 格式化为标准 VOFA+ FireWater 逗号分隔行 (不含末尾换行)
        /// </summary>
        public string ToFireWaterLine()
        {
            if (Channels.Length == 0) return RawText;
            var parts = new string[Channels.Length];
            for (int i = 0; i < Channels.Length; i++)
            {
                parts[i] = Channels[i].ToString("F3", CultureInfo.InvariantCulture);
            }
            return string.IsNullOrEmpty(StreamPrefix)
                ? string.Join(",", parts)
                : $"{StreamPrefix}:{string.Join(",", parts)}";
        }

        /// <summary>
        /// 解析 VOFA+ FireWater 协议行 ("v0,v1,v2" 或多数据流标签行 "BUS: v0, v1, v2") 及键值遥测行 ("roll=12.3,pitch=-4.1,yaw=85.0")
        /// </summary>
        public static bool TryParseLine(string? line, string sourceTag, out BlueprintPacket packet)
        {
            packet = new BlueprintPacket(Array.Empty<double>(), line ?? string.Empty, sourceTag);
            if (string.IsNullOrWhiteSpace(line)) return false;

            string trimmed = line.Trim();
            string detectedPrefix = string.Empty;
            string body = trimmed;

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0 && colonIdx < 24)
            {
                string candidatePrefix = trimmed[..colonIdx].Trim().Trim('[', ']', '<', '>', '#');
                if (IsValidStreamPrefix(candidatePrefix))
                {
                    detectedPrefix = candidatePrefix.ToUpperInvariant();
                    body = trimmed[(colonIdx + 1)..].Trim();
                }
            }
            else if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                int closeBracket = trimmed.IndexOf(']');
                if (closeBracket > 1 && closeBracket < 20)
                {
                    string candidatePrefix = trimmed[1..closeBracket].Trim();
                    if (IsValidStreamPrefix(candidatePrefix))
                    {
                        detectedPrefix = candidatePrefix.ToUpperInvariant();
                        body = trimmed[(closeBracket + 1)..].Trim();
                    }
                }
            }

            var values = new List<double>(12);
            var tokens = body.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawToken in tokens)
            {
                string candidate = rawToken;
                int eqIdx = candidate.IndexOf('=');
                if (eqIdx >= 0 && eqIdx < candidate.Length - 1)
                {
                    candidate = candidate[(eqIdx + 1)..];
                }

                // 剥离常见物理单位后缀 (V, mA, W, deg, dBm)
                candidate = candidate.TrimEnd('V', 'v', 'A', 'a', 'W', 'w', 'm', 'd', 'B', 'M', '°', '%');
                if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) &&
                    !double.IsNaN(val) && !double.IsInfinity(val))
                {
                    values.Add(val);
                }
            }

            if (values.Count == 0)
            {
                return false;
            }

            packet = new BlueprintPacket(values.ToArray(), trimmed, sourceTag, DateTime.Now, detectedPrefix);
            return true;
        }

        private static bool IsValidStreamPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 18) return false;
            bool hasLetter = false;
            foreach (char c in prefix)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                }
                else if (!char.IsDigit(c) && c != '_' && c != '-')
                {
                    return false;
                }
            }
            return hasLetter;
        }
    }
}
