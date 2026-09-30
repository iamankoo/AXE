using System.Windows;
using System.Windows.Controls;
using AxeV2.Browser;

namespace AxeV2.Controls;

/// <summary>
/// Chrome-style tab layout: tabs share the available width equally between
/// <see cref="MinTabWidth"/> and <see cref="MaxTabWidth"/>. When even the minimum width
/// overflows, the strip scrolls so the active tab stays fully visible.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public const double MinTabWidth = 56;
    public const double MaxTabWidth = 220;

    /// <summary>Width of each tab for a given strip width (unit tested).</summary>
    public static double TabWidth(double available, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (double.IsInfinity(available) || double.IsNaN(available))
        {
            return MaxTabWidth;
        }

        return Math.Floor(Math.Clamp(available / count, MinTabWidth, MaxTabWidth));
    }

    /// <summary>Horizontal scroll offset that keeps the active tab visible (unit tested).</summary>
    public static double ScrollOffset(double available, int count, int activeIndex)
    {
        var width = TabWidth(available, count);
        var total = width * count;
        if (total <= available || activeIndex < 0)
        {
            return 0;
        }

        // Keep the active tab in view, preferring to show one neighbour on each side.
        var desired = (activeIndex + 1) * width - available + Math.Min(width, available - width);
        return Math.Clamp(desired, 0, total - available);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = TabWidth(availableSize.Width, InternalChildren.Count);
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var total = width * InternalChildren.Count;
        return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = InternalChildren.Count;
        var width = TabWidth(finalSize.Width, count);
        var active = -1;
        for (var i = 0; i < count; i++)
        {
            if ((InternalChildren[i] as FrameworkElement)?.DataContext is BrowserTab { IsActive: true })
            {
                active = i;
                break;
            }
        }

        var x = -ScrollOffset(finalSize.Width, count, active);
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }
}
