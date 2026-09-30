using AxeV2.Browser;
using AxeV2.Controls;

namespace AxeV2.Tests;

public class TabOrderTests
{
    [Theory]
    [InlineData(0, 2, 0)] // closed first of three: the next tab (now index 0) becomes active
    [InlineData(1, 2, 1)] // closed middle: the tab to its right
    [InlineData(2, 2, 1)] // closed last: the new last tab
    [InlineData(0, 0, -1)]
    public void IndexAfterClose_PrefersRightNeighbour(int closed, int remaining, int expected) =>
        Assert.Equal(expected, TabOrder.IndexAfterClose(closed, remaining));

    [Theory]
    [InlineData(0, 1, 3, 1)]
    [InlineData(2, 1, 3, 0)]  // Ctrl+Tab wraps to the first tab
    [InlineData(0, -1, 3, 2)] // Ctrl+Shift+Tab wraps to the last tab
    [InlineData(0, 1, 1, 0)]
    public void Cycle_Wraps(int index, int delta, int count, int expected) =>
        Assert.Equal(expected, TabOrder.Cycle(index, delta, count));

    [Theory]
    [InlineData(1, 5, 0)]
    [InlineData(5, 5, 4)]
    [InlineData(6, 5, null)] // Ctrl+6 with five tabs does nothing
    [InlineData(9, 5, 4)]    // Ctrl+9 is always the last tab
    [InlineData(9, 12, 11)]
    [InlineData(0, 5, null)]
    public void IndexForNumber_MatchesChrome(int number, int count, int? expected) =>
        Assert.Equal(expected, TabOrder.IndexForNumber(number, count));

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("", false)]
    public void PopupUris_OnlyWebAndBlank(string uri, bool allowed) =>
        Assert.Equal(allowed, BrowserService.IsAllowedPopupUri(uri));
}

public class TabStripPanelTests
{
    [Fact]
    public void TabWidth_ClampsBetweenMinAndMax()
    {
        Assert.Equal(TabStripPanel.MaxTabWidth, TabStripPanel.TabWidth(1000, 1));
        Assert.Equal(200, TabStripPanel.TabWidth(1000, 5));
        Assert.Equal(TabStripPanel.MinTabWidth, TabStripPanel.TabWidth(300, 20));
        Assert.Equal(0, TabStripPanel.TabWidth(300, 0));
    }

    [Fact]
    public void ScrollOffset_IsZeroWhenTabsFit() =>
        Assert.Equal(0, TabStripPanel.ScrollOffset(1000, 4, 3));

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(19)]
    public void ScrollOffset_KeepsActiveTabVisible(int active)
    {
        const double available = 300;
        const int count = 20;
        var width = TabStripPanel.TabWidth(available, count);
        var offset = TabStripPanel.ScrollOffset(available, count, active);
        var left = active * width - offset;
        Assert.InRange(left, 0, available - width);
        Assert.InRange(offset, 0, width * count - available);
    }
}
