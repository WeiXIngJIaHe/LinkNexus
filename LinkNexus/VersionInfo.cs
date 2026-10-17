using System;
using System.Globalization;

namespace LinkNexus
{
    /// <summary>
    /// LinkNexus 软件与固件工程版本管理中心
    /// 遵循系统规范版本号命名格式：vX.xx-AAA.YYWW
    /// - X:   大版本号 (不补零，如 0, 1)
    /// - xx:  小版本修改/修订号 (两位补零，如 10)
    /// - AAA: 发布修订阶段标识 (小写，如 preview, beta, release)
    /// - YY:  年份后两位 (如 2026年 -> 26)
    /// - WW:  所在工作周 (01~53，基于 ISO 8601 第一周规则计算，如 41)
    /// 当前工程基准版本：v0.19-preview.2641
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>
        /// 大版本号 X (当前为 0)
        /// </summary>
        public const int MajorVersion = 0;

        /// <summary>
        /// 小版本号 xx (当前为 19)
        /// </summary>
        public const int MinorRevision = 19;

        /// <summary>
        /// 修订阶段标识 AAA (当前为 "preview")
        /// </summary>
        public const string Stage = "preview";

        /// <summary>
        /// 基准年周数字符串 YYWW (2026年第41周 -> "2641")
        /// </summary>
        public const string TargetYearWeek = "2641";

        /// <summary>
        /// 根据规范格式 vX.xx-AAA.YYWW 格式化生成版本号字符串
        /// </summary>
        /// <param name="major">大版本号 X</param>
        /// <param name="minor">小版本修改号 xx</param>
        /// <param name="stage">修订阶段 AAA (preview / beta / release)</param>
        /// <param name="yearWeek">年周数 YYWW，若为 null 则基于 date 计算</param>
        /// <param name="date">参考时间</param>
        /// <returns>符合 vX.xx-AAA.YYWW 格式的版本号</returns>
        public static string Format(int major, int minor, string stage, string? yearWeek = null, DateTime? date = null)
        {
            string yw = yearWeek ?? CalculateYearWeek(date ?? DateTime.Now);
            return $"v{major}.{minor:D2}-{stage.ToLowerInvariant()}.{yw}";
        }

        /// <summary>
        /// 计算指定日期的 ISO 8601 年周数 (YYWW)
        /// </summary>
        public static string CalculateYearWeek(DateTime dt)
        {
            string yy = (dt.Year % 100).ToString("D2");
            var cal = CultureInfo.InvariantCulture.Calendar;
            int ww = cal.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            return $"{yy}{ww:D2}";
        }

        /// <summary>
        /// 根据动态或基准规则生成规范版本号
        /// </summary>
        public static string GenerateVersion(int major = MajorVersion, int minor = MinorRevision, string stage = Stage, string? yearWeek = TargetYearWeek, DateTime? date = null)
        {
            return Format(major, minor, stage, yearWeek, date);
        }

        /// <summary>
        /// 当前工程静态基准版本号字符串 (v0.10-preview.2641)
        /// </summary>
        public static string CurrentVersion { get; set; } = GenerateVersion();

        /// <summary>
        /// 完整版本号字符串 (同 CurrentVersion，带 'v' 前缀)
        /// </summary>
        public static string FullVersionString => CurrentVersion;
    }
}

