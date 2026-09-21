using System.Windows;
using System.Windows.Media;

namespace Support_Tool.Views;

// Shared by DebloaterPage/ProgramsPage/InstallPage's own code-behind, which previously each
// carried an identical MinGridItemWidth const + column-count calculation + visual-tree search
// (external review - responsive grid columns should use the same logic everywhere rather than
// three copies that could silently drift apart, per this pass's own "responsive Apps/Programs
// grid" requirement).
internal static class GridColumnHelper
{
    // Same minimum card width for every grid on every page - so all three choose the same number
    // of columns from the same available width rather than one page fitting more/larger cards
    // than another from a different calculation.
    public const double MinItemWidth = 300;

    // ScrollViewer padding (12px each side) and item margins (3px each side) - subtracted before
    // dividing, so the column count reflects the space actually available to cards, not the raw
    // container width.
    public static int CalculateColumns(double availableWidth, double edgePadding = 24)
    {
        var effectiveWidth = Math.Max(0, availableWidth - edgePadding);
        return Math.Max(1, (int)(effectiveWidth / MinItemWidth));
    }

    // The per-item width a WrapPanel's own ItemWidth should be set to so `columns` of them fill
    // the available width evenly - used by DebloaterPage/ProgramsPage (external review: their
    // grid view previously used a UniformGrid, which reserves cell HEIGHT based on the full
    // available viewport rather than packing rows from the top when there are few items, so a
    // sparse Programs list rendered a couple of oversized rows filling most of the page instead
    // of compact cards. A WrapPanel with ItemWidth set never has this problem - by construction
    // it always sizes to exactly "however many rows its children actually wrap into," never
    // stretching to fill leftover space). Never below MinItemWidth, so a very narrow window still
    // gets a usable card rather than a degenerate near-zero width.
    public static double CalculateItemWidth(double availableWidth, int columns, double edgePadding = 24)
    {
        var effectiveWidth = Math.Max(0, availableWidth - edgePadding);
        return Math.Max(MinItemWidth, effectiveWidth / Math.Max(1, columns));
    }

    public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) return target;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) yield return target;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }
}
