using System.Windows;

namespace AxeV2.Window;

/// <summary>Pure geometry for AXE's initial placement (unit tested).</summary>
public static class WindowPlacement
{
    public const double MinWidth = 600; // room for brand, tabs, opacity and caption buttons
    public const double MinHeight = 360;
    public const double EdgeMargin = 24;

    /// <summary>
    /// Initial bounds: half the work-area width and half its height (≈ ¼ of the screen),
    /// placed on the right side, slightly above vertical centre.
    /// </summary>
    public static Rect Initial(Rect workArea)
    {
        var width = Math.Max(MinWidth, Math.Round(workArea.Width / 2));
        var height = Math.Max(MinHeight, Math.Round(workArea.Height / 2));
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);

        var left = workArea.Right - width - EdgeMargin;
        var top = workArea.Top + (workArea.Height - height) * 0.3;

        left = Math.Max(workArea.Left, left);
        top = Math.Max(workArea.Top, top);
        return new Rect(Math.Round(left), Math.Round(top), width, height);
    }

    /// <summary>Opacity (0.10-1.00) as the whole percent shown in the header.</summary>
    public static int ToPercent(double opacity) => (int)Math.Round(opacity * 100, MidpointRounding.AwayFromZero);
}
