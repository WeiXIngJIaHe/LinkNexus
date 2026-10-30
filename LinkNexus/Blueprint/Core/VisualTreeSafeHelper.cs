using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// 安全的 WPF 视觉树/逻辑树回溯辅助器
    /// 彻底避免在 System.Windows.Documents.Run / Span / Hyperlink 等 FrameworkContentElement 上直接调用
    /// VisualTreeHelper.GetParent 导致的 InvalidOperationException (“Run 不是 Visual 或 Visual3D”)
    /// </summary>
    public static class VisualTreeSafeHelper
    {
        public static DependencyObject? GetSafeParent(DependencyObject? current)
        {
            if (current == null)
            {
                return null;
            }

            if (current is Visual || current is Visual3D)
            {
                return VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            if (current is FrameworkContentElement fce)
            {
                return fce.Parent ?? ContentOperations.GetParent(fce) ?? LogicalTreeHelper.GetParent(fce);
            }

            if (current is ContentElement ce)
            {
                return ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(ce);
            }

            return LogicalTreeHelper.GetParent(current);
        }

        public static T? FindAncestor<T>(DependencyObject? source, bool includeSelf = true)
            where T : DependencyObject
        {
            DependencyObject? current = includeSelf ? source : GetSafeParent(source);
            while (current != null)
            {
                if (current is T matched)
                {
                    return matched;
                }
                current = GetSafeParent(current);
            }
            return null;
        }

        public static bool IsInsideButtonOrInteractive(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is ButtonBase || current is System.Windows.Controls.ComboBox || current is System.Windows.Controls.TextBox)
                {
                    return true;
                }
                current = GetSafeParent(current);
            }
            return false;
        }
    }
}

