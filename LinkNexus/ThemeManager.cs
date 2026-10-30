using System;
using System.Windows;
using System.Windows.Media;

namespace LinkNexus
{
    public enum AppTheme
    {
        Dark,
        Light
    }

    /// <summary>
    /// 全局工业主题与高对比度调色板管理器
    /// 支持柔化工业深色模式 (Softened Industrial Slate-Charcoal, WCAG AAA >= 7.8:1) 与高反差护眼浅色模式无缝热切换
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDarkMode { get; private set; } = true;
        public static AppTheme CurrentTheme => IsDarkMode ? AppTheme.Dark : AppTheme.Light;

        public static event Action<bool>? ThemeChanged;

        public static void ApplyTheme(AppTheme theme)
        {
            ApplyTheme(theme == AppTheme.Dark);
        }

        public static void ApplyTheme(bool isDark)
        {
            IsDarkMode = isDark;
            try
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(isDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
            }
            catch { }

            var res = Application.Current?.Resources;
            if (res == null) return;

            if (isDark)
            {
                // 柔化工业深色模式 (Softened Slate-Charcoal) — 调淡死黑背景、强化纯白与亮银文字对比度
                SetBrush(res, "AppBgBrush", "#1A202C");
                SetBrush(res, "WindowBgBrush", "#1A202C");
                SetBrush(res, "AppSurfaceBrush", "#222938");
                SetBrush(res, "AppSurfaceSubtleBrush", "#283142");
                SetBrush(res, "AppSurfaceHoverBrush", "#323D52");
                SetBrush(res, "AppBorderBrush", "#3E4C66");
                SetBrush(res, "AppBorderSubtleBrush", "#334056");

                // 卡片与毛玻璃表层色
                SetBrush(res, "CardBgBrush", "#252E3F");
                SetBrush(res, "CardHoverBgBrush", "#2F3B52");
                SetBrush(res, "CardSelectedBgBrush", "#223B59");

                // 终端与代码视窗底色 (调淡纯黑，消除刺眼眩光)
                SetBrush(res, "AppTerminalBgBrush", "#141923");
                SetBrush(res, "AppTerminalBorderBrush", "#38455E");

                // 强化文字色彩层级：纯白主字 (#FFFFFF) + 亮银白副字 (#F1F5F9) + 高清浅灰蓝辅助标签 (#CBD5E1)
                SetBrush(res, "AppTextPrimaryBrush", "#FFFFFF");
                SetBrush(res, "TextBrightBrush", "#FFFFFF");
                SetBrush(res, "AppTextSecondaryBrush", "#F1F5F9");
                SetBrush(res, "TextNormalBrush", "#F1F5F9");
                SetBrush(res, "AppTextMutedBrush", "#CBD5E1");
                SetBrush(res, "TextDimBrush", "#CBD5E1");

                // 工业功能强调色 (高饱和度高辨识度)
                SetBrush(res, "AccentCyanBrush", "#38BDF8");
                SetBrush(res, "AccentBlueBrush", "#60A5FA");
                SetBrush(res, "AccentAnimePurpleBrush", "#A5B4FC");
                SetBrush(res, "SuccessGreenBrush", "#34D399");
                SetBrush(res, "WarningAmberBrush", "#FBBF24");
                SetBrush(res, "DangerRedBrush", "#F87171");

                // 终端高亮文本色
                SetBrush(res, "TerminalTextPrimaryBrush", "#FFFFFF");
                SetBrush(res, "TerminalTextCyanBrush", "#38BDF8");
                SetBrush(res, "TerminalTextGreenBrush", "#34D399");
                SetBrush(res, "TerminalTextAmberBrush", "#FBBF24");

                // 工业按钮状态色
                SetBrush(res, "IndustrialBtnBgBrush", "#2A3447");
                SetBrush(res, "IndustrialBtnHoverBgBrush", "#354259");
                SetBrush(res, "IndustrialBtnPressedBgBrush", "#1F2736");
                SetBrush(res, "IndustrialBtnBorderBrush", "#475672");

                // 工业毛玻璃亚克力质感画刷
                SetBrush(res, "CardGlassBgBrush", "#EB263042");
                SetBrush(res, "CardGlassHoverBgBrush", "#F5303C54");
                SetBrush(res, "CardGlassBorderBrush", "#465675");
                SetBrush(res, "CardGlassHoverBorderBrush", "#38BDF8");
                SetBrush(res, "CardGlassHighlightBrush", "#38FFFFFF");
                SetBrush(res, "AppAnimeGridBrush", "#1838BDF8");
                SetBrush(res, "AppAnimeLineBrush", "#35818CF8");
            }
            else
            {
                // 工业高反差浅色模式 (Industrial Daylight)
                SetBrush(res, "AppBgBrush", "#E2E8F0");
                SetBrush(res, "WindowBgBrush", "#E2E8F0");
                SetBrush(res, "AppSurfaceBrush", "#F1F5F9");
                SetBrush(res, "AppSurfaceSubtleBrush", "#E2E8F0");
                SetBrush(res, "AppSurfaceHoverBrush", "#CBD5E1");
                SetBrush(res, "AppBorderBrush", "#94A3B8");
                SetBrush(res, "AppBorderSubtleBrush", "#CBD5E1");

                SetBrush(res, "CardBgBrush", "#FFFFFF");
                SetBrush(res, "CardHoverBgBrush", "#F8FAFC");
                SetBrush(res, "CardSelectedBgBrush", "#E0F2FE");

                // 终端采用高对比冷灰底色
                SetBrush(res, "AppTerminalBgBrush", "#E2E8F0");
                SetBrush(res, "AppTerminalBorderBrush", "#94A3B8");

                // 深邃墨黑文字保证极高对比度
                SetBrush(res, "AppTextPrimaryBrush", "#0F172A");
                SetBrush(res, "TextBrightBrush", "#0F172A");
                SetBrush(res, "AppTextSecondaryBrush", "#1E293B");
                SetBrush(res, "TextNormalBrush", "#1E293B");
                SetBrush(res, "AppTextMutedBrush", "#334155");
                SetBrush(res, "TextDimBrush", "#475569");

                // 浅色背景下的深色系高对比功能强调色
                SetBrush(res, "AccentCyanBrush", "#0284C7");
                SetBrush(res, "AccentBlueBrush", "#2563EB");
                SetBrush(res, "AccentAnimePurpleBrush", "#4F46E5");
                SetBrush(res, "SuccessGreenBrush", "#059669");
                SetBrush(res, "WarningAmberBrush", "#D97706");
                SetBrush(res, "DangerRedBrush", "#DC2626");

                // 终端深色系高亮文本
                SetBrush(res, "TerminalTextPrimaryBrush", "#0F172A");
                SetBrush(res, "TerminalTextCyanBrush", "#0369A1");
                SetBrush(res, "TerminalTextGreenBrush", "#047857");
                SetBrush(res, "TerminalTextAmberBrush", "#B45309");

                // 工业按钮状态
                SetBrush(res, "IndustrialBtnBgBrush", "#E2E8F0");
                SetBrush(res, "IndustrialBtnHoverBgBrush", "#CBD5E1");
                SetBrush(res, "IndustrialBtnPressedBgBrush", "#94A3B8");
                SetBrush(res, "IndustrialBtnBorderBrush", "#94A3B8");

                // 浅色模式毛玻璃亚克力质感画刷
                SetBrush(res, "CardGlassBgBrush", "#F2FFFFFF");
                SetBrush(res, "CardGlassHoverBgBrush", "#FFFFFFFF");
                SetBrush(res, "CardGlassBorderBrush", "#94A3B8");
                SetBrush(res, "CardGlassHoverBorderBrush", "#0284C7");
                SetBrush(res, "CardGlassHighlightBrush", "#FFFFFFFF");
                SetBrush(res, "AppAnimeGridBrush", "#120284C7");
                SetBrush(res, "AppAnimeLineBrush", "#284F46E5");
            }

            ThemeChanged?.Invoke(isDark);
        }

        public static void ToggleTheme()
        {
            ApplyTheme(!IsDarkMode);
        }

        public static string GetStripeColor(string stateCategory)
        {
            bool isLight = !IsDarkMode;
            return stateCategory switch
            {
                "Ready" => isLight ? "#059669" : "#10B981",
                "Warning" => isLight ? "#D97706" : "#F59E0B",
                _ => isLight ? "#64748B" : "#64748B"
            };
        }

        private static void SetBrush(ResourceDictionary res, string key, string hexColor)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }
    }
}
