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
        private static readonly SolidColorBrush PromptBrush = new(Color.FromRgb(0x34, 0xD3, 0x99)); // #34D399 Bright Mint Green (Prompt)
        private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));  // #F87171 Soft Red (Error)
        private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));   // #FBBF24 Amber (Warning & NVS)
        private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));// #10B981 Emerald Green (Success & OK)
        private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x38, 0xBD, 0xF8));   // #38BDF8 Cyan Blue (Tag & Info)
        private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));  // #64748B Slate Muted (Dividers)
        private static readonly SolidColorBrush TableTextBrush = new(Color.FromRgb(0xCB, 0xD5, 0xE1)); // #CBD5E1 Light Slate (Indented Rows)
        private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0xE2, 0xE8, 0xF0)); // #E2E8F0 Off-White (Standard Output)

        static CliLineBrushConverter()
        {
            PromptBrush.Freeze();
            ErrorBrush.Freeze();
            WarnBrush.Freeze();
            SuccessBrush.Freeze();
            InfoBrush.Freeze();
            MutedBrush.Freeze();
            TableTextBrush.Freeze();
            DefaultBrush.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string line || string.IsNullOrWhiteSpace(line))
            {
                return DefaultBrush;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("root@") || trimmed.StartsWith("esp32-s3:~#") || trimmed.StartsWith("$ ") || trimmed.StartsWith("# "))
            {
                return PromptBrush;
            }
            if (trimmed.StartsWith("[ERR]") || trimmed.Contains("未知命令") || trimmed.Contains("失败") || trimmed.Contains("超时") || trimmed.Contains("Error"))
            {
                return ErrorBrush;
            }
            if (trimmed.StartsWith("[WARN]") || trimmed.StartsWith("[NVS]"))
            {
                return WarnBrush;
            }
            if (trimmed.StartsWith("[OK]") || trimmed.StartsWith("[SUCCESS]") || trimmed.Contains("[OK]"))
            {
                return SuccessBrush;
            }
            if (trimmed.StartsWith("[WDEBUG]") || trimmed.StartsWith("[WIFI]") || trimmed.StartsWith("[POWER]") || trimmed.StartsWith("[SYSTEM]") || trimmed.StartsWith("[INFO]") || trimmed.StartsWith("LinkNexus") || trimmed.StartsWith("LINKNEXUS"))
            {
                return InfoBrush;
            }
            if (trimmed.StartsWith("=") || trimmed.StartsWith("-") || trimmed.StartsWith("#"))
            {
                return MutedBrush;
            }
            if (line.StartsWith("  "))
            {
                return TableTextBrush;
            }

            return DefaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

