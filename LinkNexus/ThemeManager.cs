using System;
using System.Windows;
using System.Windows.Media;

namespace LinkNexus
{
    /// <summary>
    /// LinkNexus 全局主题与美学管理中枢 (Theme Manager)
    /// 遵循 UI-UX-PRO-MAX 准则构建：
    /// 1. 简约风格带有机能/二次元风味 (Minimalist Tech-Anime / 战术二次元美学)
    /// 2. 深度支持【深色模式 (Cyber Dark)】与【浅色模式 (Porcelain Light)】热切换
    /// 3. 提供满足 WCAG 4.5:1+ 高对比度、语义化色彩代币 (Color Tokens)
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDarkMode { get; private set; } = true;

        public static event Action<bool>? ThemeChanged;

        /// <summary>
        /// 热重载并应用全局主题调色板
        /// </summary>
        /// <param name="isDark">true: 赛博暗黑战术模式; false: 极简白瓷实验室模式</param>
        public static void ApplyTheme(bool isDark)
        {
            IsDarkMode = isDark;
            var res = Application.Current?.Resources;
            if (res == null) return;

            // 1. 基础容器与画板表面 (Surfaces & Backgrounds - 浅色模式采用低眩光白瓷磨砂质感)
            SetBrush(res, "AppBgBrush", isDark ? "#0B0C10" : "#E8EDF5");
            SetBrush(res, "AppSurfaceBrush", isDark ? "#13151F" : "#F8FAFC");
            SetBrush(res, "AppSurfaceSubtleBrush", isDark ? "#1A1D2B" : "#EDF2F7");
            SetBrush(res, "AppSurfaceHoverBrush", isDark ? "#23273A" : "#E2E8F0");

            // 2. 边框与微结构线条 (Borders & Tactical Lines)
            SetBrush(res, "AppBorderBrush", isDark ? "#262A3D" : "#CBD5E1");
            SetBrush(res, "AppBorderSubtleBrush", isDark ? "#1C2030" : "#E2E8F0");
            SetBrush(res, "AppBorderActiveBrush", isDark ? "#38BDF8" : "#2563EB");
            SetBrush(res, "BorderDarkBrush", isDark ? "#262A3D" : "#CBD5E1");

            // 3. 字体层级 (Typography & High Contrast Text)
            SetBrush(res, "AppTextPrimaryBrush", isDark ? "#F8FAFC" : "#1E293B");
            SetBrush(res, "AppTextSecondaryBrush", isDark ? "#94A3B8" : "#475569");
            SetBrush(res, "AppTextMutedBrush", isDark ? "#64748B" : "#64748B");
            SetBrush(res, "TextBrightBrush", isDark ? "#FAFAFA" : "#0F172A");
            SetBrush(res, "TextNormalBrush", isDark ? "#D4D4D8" : "#334155");
            SetBrush(res, "TextDimBrush", isDark ? "#71717A" : "#64748B");

            // 4. 二次元 / 赛博机能专属强调色 (深浅色点缀色适量区分，浅色偏战术深海蓝与品红)
            SetBrush(res, "AccentCyanBrush", isDark ? "#38BDF8" : "#2563EB");          // 深色苍穹青 / 浅色皇家战术蓝
            SetBrush(res, "AccentAnimePurpleBrush", isDark ? "#818CF8" : "#6366F1");   // 幻灵紫 / 靛蓝
            SetBrush(res, "AccentAnimePinkBrush", isDark ? "#F43F5E" : "#E11D48");     // 绯樱粉 / 战术赤红
            SetBrush(res, "AccentAmberBrush", isDark ? "#F59E0B" : "#D97706");         // 战术橙 / 深琥珀

            // 5. 状态反馈色 (Functional Status)
            SetBrush(res, "SuccessGreenBrush", isDark ? "#10B981" : "#059669");
            SetBrush(res, "WarningAmberBrush", isDark ? "#F59E0B" : "#D97706");
            SetBrush(res, "ErrorRedBrush", isDark ? "#EF4444" : "#DC2626");

            // 6. 设备卡片专属 (Tactical & Frosted Glass Card Tokens)
            SetBrush(res, "CardBgBrush", isDark ? "#141724" : "#FFFFFF");
            SetBrush(res, "CardBorderBrush", isDark ? "#262A3D" : "#CBD5E1");
            SetBrush(res, "CardSelectedBgBrush", isDark ? "#162036" : "#E0F2FE");
            SetBrush(res, "CardSelectedBorderBrush", isDark ? "#38BDF8" : "#2563EB");
            // 毛玻璃质感色彩令牌 (Frosted Glass Acrylic Tokens)
            SetBrush(res, "CardGlassBgBrush", isDark ? "#D9141826" : "#EBF8FAFC");
            SetBrush(res, "CardGlassBorderBrush", isDark ? "#3338BDF8" : "#80CBD5E1");
            SetBrush(res, "CardGlassHighlightBrush", isDark ? "#1AFFFFFF" : "#99FFFFFF");
            SetBrush(res, "CardGlassHoverBgBrush", isDark ? "#E61A2033" : "#F8FFFFFF");
            SetBrush(res, "CardGlassHoverBorderBrush", isDark ? "#8038BDF8" : "#B32563EB");

            // 7. 按钮与交互控件 (Buttons & Inputs)
            SetBrush(res, "IndustrialBtnBgBrush", isDark ? "#1E2235" : "#F1F5F9");
            SetBrush(res, "IndustrialBtnBorderBrush", isDark ? "#2E344E" : "#CBD5E1");
            SetBrush(res, "IndustrialBtnHoverBgBrush", isDark ? "#2B314B" : "#E2E8F0");
            SetBrush(res, "IndustrialBtnPressedBgBrush", isDark ? "#161927" : "#CBD5E1");
            SetBrush(res, "InputBgBrush", isDark ? "#0F111A" : "#FFFFFF");

            // 8. 终端控制台与日志视窗 (修复：浅色模式下为柔和浅灰磨砂编辑箱体，彻底解决全黑和白底黑块问题)
            SetBrush(res, "AppTerminalBgBrush", isDark ? "#07080D" : "#EDF2F7");
            SetBrush(res, "AppTerminalBorderBrush", isDark ? "#1F2438" : "#CBD5E1");
            SetBrush(res, "TerminalTextPrimaryBrush", isDark ? "#F8FAFC" : "#0F172A");
            SetBrush(res, "TerminalTextCyanBrush", isDark ? "#38BDF8" : "#0284C7");
            SetBrush(res, "TerminalTextGreenBrush", isDark ? "#34D399" : "#059669");
            SetBrush(res, "TerminalTextAmberBrush", isDark ? "#FBBF24" : "#D97706");
            SetBrush(res, "TerminalTextMutedBrush", isDark ? "#64748B" : "#64748B");

            // 9. 滚动条 (Industrial & Anime Slim ScrollBar)
            SetBrush(res, "ScrollBarTrackBrush", isDark ? "#10121A" : "#E2E8F0");
            SetBrush(res, "ScrollBarThumbBrush", isDark ? "#2E344E" : "#CBD5E1");
            SetBrush(res, "ScrollBarThumbHoverBrush", isDark ? "#3F476B" : "#94A3B8");

            // 10. 手绘二次元机能线条背景画刷 (Hand-Drawn Anime Tech Line-Art Brushes)
            SetBrush(res, "AppAnimeLineBrush", isDark ? "#2A364F" : "#94A3B8");
            SetBrush(res, "AppAnimeGlowBrush", isDark ? "#38BDF8" : "#2563EB");
            SetBrush(res, "AppAnimeFineBrush", isDark ? "#1A2234" : "#CBD5E1");

            ThemeChanged?.Invoke(isDark);
        }

        /// <summary>
        /// 根据当前深浅色模式获取对应的卡片机能状态指示条色值
        /// </summary>
        public static string GetStripeColor(string status)
        {
            return status switch
            {
                "Ready" => IsDarkMode ? "#38BDF8" : "#2563EB",
                "Warning" => IsDarkMode ? "#F59E0B" : "#D97706",
                _ => IsDarkMode ? "#64748B" : "#94A3B8"
            };
        }

        private static void SetBrush(ResourceDictionary res, string key, string hexColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                var brush = new SolidColorBrush(color);
                brush.Freeze(); // 提升渲染性能并支持跨线程安全
                res[key] = brush;
            }
            catch
            {
                // 保底防崩
            }
        }
    }
}

