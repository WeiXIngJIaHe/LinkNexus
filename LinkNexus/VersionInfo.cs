using System;
using System.Globalization;

namespace LinkNexus
{
    /// <summary>
    /// LinkNexus 软件与固件工程版本管理中心
    /// 遵循规范版本号命名格式：YYDWW.XXxx
    /// - YY: 年份后两位 (例如 2026年 -> 26)
    /// - D:  星期几 (1~7，ISO 8601 标准，周一为 1，周日为 7) 或字母 D
    /// - WW: 所在工作周 (01~53，基于 ISO 8601 第一周规则计算)
    /// - . : 分隔点
    /// - XX: 大版本号 (两位数字，如 01, 02)
    /// - xx: 小版本修改/修订号 (两位数字，如 00, 01)
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>
        /// 大版本号 (XX: 两位数字)
        /// </summary>
        public const int MajorVersion = 1;

        /// <summary>
        /// 小版本修改号 (xx: 两位数字)
        /// </summary>
        public const int MinorRevision = 7;

        /// <summary>
        /// 是否采用字面量字母 'D' (如 26D39.0100) 替代数字星期 (如 26739.0100)
        /// 默认 false (采用工业标准数字星期几 1~7)
        /// </summary>
        public static bool UseLiteralD { get; set; } = false;

        /// <summary>
        /// 根据 YYDWW.XXxx 规则生成版本号字符串
        /// </summary>
        /// <param name="date">指定基准时间，为 null 时采用当前本地时间</param>
        /// <param name="major">大版本号 XX (0~99)</param>
        /// <param name="minor">小版本修改 xx (0~99)</param>
        /// <param name="literalD">是否将 D 作为字面字符 'D'</param>
        /// <returns>符合 YYDWW.XXxx 格式的版本字符串</returns>
        public static string GenerateVersion(DateTime? date = null, int major = MajorVersion, int minor = MinorRevision, bool? literalD = null)
        {
            var dt = date ?? DateTime.Now;
            string yy = (dt.Year % 100).ToString("D2");

            bool isLiteralD = literalD ?? UseLiteralD;
            string dStr = isLiteralD
                ? "D"
                : (dt.DayOfWeek == DayOfWeek.Sunday ? "7" : ((int)dt.DayOfWeek).ToString());

            var cal = CultureInfo.InvariantCulture.Calendar;
            int ww = cal.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);

            string xxMajor = (major % 100).ToString("D2");
            string xxMinor = (minor % 100).ToString("D2");

            return $"{yy}{dStr}{ww:D2}.{xxMajor}{xxMinor}";
        }

        /// <summary>
        /// 当前静态生成的标准版本号字符串 (如 26739.0100)
        /// </summary>
        public static string CurrentVersion { get; set; } = GenerateVersion();

        /// <summary>
        /// 带有 'v' 前缀的友好版本展示字符串 (如 v26739.0100)
        /// </summary>
        public static string FullVersionString => $"v{CurrentVersion}";
    }
}

