using System.Windows;
using AxeV2.Native;
using AxeV2.Services;
using AxeV2.Window;

namespace AxeV2.Tests;

public class WindowPlacementTests
{
    [Theory]
    [InlineData(1920, 1040)]
    [InlineData(2560, 1400)]
    [InlineData(1366, 728)]
    public void Initial_size_is_about_a_quarter_of_the_work_area(double w, double h)
    {
        var work = new Rect(0, 0, w, h);
        var r = WindowPlacement.Initial(work);

        var fraction = r.Width * r.Height / (w * h);
        Assert.InRange(fraction, 0.24, 0.30);
        Assert.True(work.Contains(r), "window must start fully on screen");
        Assert.True(r.Left > w / 3, "window should float on the right side");
    }

    [Fact]
    public void Initial_size_respects_minimum_on_small_screens()
    {
        var r = WindowPlacement.Initial(new Rect(0, 0, 800, 600));
        Assert.True(r.Width >= WindowPlacement.MinWidth);
        Assert.True(r.Height >= WindowPlacement.MinHeight);
    }

    [Fact]
    public void Offset_work_area_is_honoured()
    {
        var work = new Rect(-1920, 40, 1920, 1040); // secondary monitor on the left, taskbar on top
        Assert.True(work.Contains(WindowPlacement.Initial(work)));
    }

    [Theory]
    [InlineData(0.25, 25)]
    [InlineData(0.10, 10)]
    [InlineData(1.0, 100)]
    [InlineData(0.745, 75)]
    public void Opacity_percent(double opacity, int percent) =>
        Assert.Equal(percent, WindowPlacement.ToPercent(opacity));
}

public class HotkeyParserTests
{
    [Fact]
    public void Parses_default_hotkey()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+X", out var hk));
        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, hk.Modifiers);
        Assert.Equal(0x58u, hk.VirtualKey); // 'X'
    }

    [Theory]
    [InlineData("ctrl + shift + 1", 0x31u)]
    [InlineData("Win+Alt+F9", 0x78u)]
    public void Parses_variants(string text, uint vk)
    {
        Assert.True(HotkeyParser.TryParse(text, out var hk));
        Assert.Equal(vk, hk.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("X")]            // no modifier: never grab a plain key system-wide
    [InlineData("Ctrl+Alt")]     // no key
    [InlineData("Ctrl+X+Y")]     // two keys
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid(string text) => Assert.False(HotkeyParser.TryParse(text, out _));
}

public class CommandLineOptionsTests
{
    [Fact]
    public void Parses_url_opacity_and_diagnostic_switch()
    {
        var o = CommandLineOptions.Parse(new[] { "https://example.com", "--opacity=60", "--diag-allow-capture" });
        Assert.Equal("https://example.com", o.InitialAddress);
        Assert.Equal(0.6, o.Opacity!.Value, 3);
        Assert.True(o.DiagnosticAllowCapture);
    }

    [Fact]
    public void Capture_exclusion_is_on_by_default()
    {
        var o = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.False(o.DiagnosticAllowCapture);
        Assert.Null(o.Opacity);
        Assert.Null(o.InitialAddress);
    }
}

public class LogTests
{
    [Fact]
    public void Exception_description_contains_no_message_text()
    {
        var ex = new InvalidOperationException("https://secret.example/?token=abc typed text");
        var text = Log.Describe(ex);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("typed", text);
        Assert.StartsWith("InvalidOperationException", text);
    }
}
