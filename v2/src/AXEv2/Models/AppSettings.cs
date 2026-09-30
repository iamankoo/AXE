using AxeV2.Browser;

namespace AxeV2.Models;

/// <summary>Root of settings.json.</summary>
public sealed class AppSettings
{
    public const double MinOpacity = 0.10;
    public const double MaxOpacity = 1.00;
    public const double DefaultOpacityValue = 0.25;

    public BrowserSettings Browser { get; set; } = new();

    /// <summary>Opacity AXE starts with (0.10 - 1.00). 0.25 = 25% visible / 75% transparent.</summary>
    public double DefaultOpacity { get; set; } = DefaultOpacityValue;

    /// <summary>Global shortcut that shows/hides AXE. Empty disables it.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+X";

    public static double ClampOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinOpacity, MaxOpacity) : DefaultOpacityValue;
}
