using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LinkNexus
{
    /// <summary>
    /// Linux 风格嵌入式 CLI 终端控制台行语义色彩转换器
    /// 自动对命令行提示符、执行应答、警告、错误、配置与分割线赋予高可读性终端高亮
    /// </summary>
    public class CliLineBrushConverter : IValueConverter
    {
        // 深色模式调色板 (Cyber Dark)
        private static readonly SolidColorBrush DarkPromptBrush = new(Color.FromRgb(0x34, 0xD3, 0x99)); // #34D399 Mint Green
        private static readonly SolidColorBrush DarkErrorBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));  // #F87171 Soft Red
        private static readonly SolidColorBrush DarkWarnBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));   // #FBBF24 Amber
        private static readonly SolidColorBrush DarkSuccessBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));// #10B981 Emerald
        private static readonly SolidColorBrush DarkInfoBrush = new(Color.FromRgb(0x38, 0xBD, 0xF8));   // #38BDF8 Cyan
        private static readonly SolidColorBrush DarkMutedBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));  // #64748B Slate Muted
        private static readonly SolidColorBrush DarkTableTextBrush = new(Color.FromRgb(0xCB, 0xD5, 0xE1)); // #CBD5E1 Light Slate
        private static readonly SolidColorBrush DarkDefaultBrush = new(Color.FromRgb(0xE2, 0xE8, 0xF0)); // #E2E8F0 Off-White

        // 浅色模式调色板 (Porcelain Light - 确保在浅灰背景下满足 WCAG AA >= 4.5:1 对比度)
        private static readonly SolidColorBrush LightPromptBrush = new(Color.FromRgb(0x05, 0x96, 0x69)); // #059669 Dark Mint
        private static readonly SolidColorBrush LightErrorBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));  // #DC2626 Crimson Red
        private static readonly SolidColorBrush LightWarnBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));   // #D97706 Deep Amber
        private static readonly SolidColorBrush LightSuccessBrush = new(Color.FromRgb(0x05, 0x96, 0x69));// #059669 Dark Emerald
        private static readonly SolidColorBrush LightInfoBrush = new(Color.FromRgb(0x02, 0x84, 0xC7));   // #0284C7 Deep Sky
        private static readonly SolidColorBrush LightMutedBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));  // #64748B Slate Muted
        private static readonly SolidColorBrush LightTableTextBrush = new(Color.FromRgb(0x33, 0x41, 0x55)); // #334155 Slate 700
        private static readonly SolidColorBrush LightDefaultBrush = new(Color.FromRgb(0x0F, 0x17, 0x2A)); // #0F172A Slate 900

        static CliLineBrushConverter()
        {
            DarkPromptBrush.Freeze();
            DarkErrorBrush.Freeze();
            DarkWarnBrush.Freeze();
            DarkSuccessBrush.Freeze();
            DarkInfoBrush.Freeze();
            DarkMutedBrush.Freeze();
            DarkTableTextBrush.Freeze();
            DarkDefaultBrush.Freeze();

            LightPromptBrush.Freeze();
            LightErrorBrush.Freeze();
            LightWarnBrush.Freeze();
            LightSuccessBrush.Freeze();
            LightInfoBrush.Freeze();
            LightMutedBrush.Freeze();
            LightTableTextBrush.Freeze();
            LightDefaultBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isDark = ThemeManager.IsDarkMode;

            if (value is not string line || string.IsNullOrWhiteSpace(line))
            {
                return isDark ? DarkDefaultBrush : LightDefaultBrush;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("root@") || trimmed.StartsWith("esp32-s3:~#") || trimmed.StartsWith("$ ") || trimmed.StartsWith("# "))
            {
                return isDark ? DarkPromptBrush : LightPromptBrush;
            }
            if (trimmed.StartsWith("[ERR]") || trimmed.Contains("未知命令") || trimmed.Contains("失败") || trimmed.Contains("超时") || trimmed.Contains("Error"))
            {
                return isDark ? DarkErrorBrush : LightErrorBrush;
            }
            if (trimmed.StartsWith("[WARN]") || trimmed.StartsWith("[NVS]"))
            {
                return isDark ? DarkWarnBrush : LightWarnBrush;
            }
            if (trimmed.StartsWith("[OK]") || trimmed.StartsWith("[SUCCESS]") || trimmed.Contains("[OK]"))
            {
                return isDark ? DarkSuccessBrush : LightSuccessBrush;
            }
            if (trimmed.StartsWith("[WDEBUG]") || trimmed.StartsWith("[WIFI]") || trimmed.StartsWith("[POWER]") || trimmed.StartsWith("[SYSTEM]") || trimmed.StartsWith("[INFO]") || trimmed.StartsWith("LinkNexus") || trimmed.StartsWith("LINKNEXUS"))
            {
                return isDark ? DarkInfoBrush : LightInfoBrush;
            }
            if (trimmed.StartsWith("=") || trimmed.StartsWith("-") || trimmed.StartsWith("#"))
            {
                return isDark ? DarkMutedBrush : LightMutedBrush;
            }
            if (line.StartsWith("  "))
            {
                return isDark ? DarkTableTextBrush : LightTableTextBrush;
            }

            return isDark ? DarkDefaultBrush : LightDefaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

