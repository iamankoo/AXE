using System.IO;
using AxeV2.Browser;
using AxeV2.Models;
using AxeV2.Services;

namespace AxeV2.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "axe-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Missing_file_creates_defaults()
    {
        var settings = new SettingsService(SettingsPath).Load();

        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(0.25, settings.DefaultOpacity);
        Assert.Equal(BrowserSettings.DefaultStartPage, settings.Browser.StartPage);
        Assert.Equal(BrowserSettings.DefaultSearchTemplate, settings.Browser.SearchUrlTemplate);
        Assert.False(settings.Browser.EnableDevTools);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_backed_up()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = new SettingsService(SettingsPath).Load();

        Assert.Equal(0.25, settings.DefaultOpacity);
        Assert.True(File.Exists(SettingsPath + ".bad"));
    }

    [Theory]
    [InlineData(0.01, 0.10)]
    [InlineData(5.0, 1.00)]
    [InlineData(0.5, 0.5)]
    [InlineData(double.NaN, 0.25)]
    public void Opacity_is_clamped_to_10_to_100_percent(double stored, double expected)
    {
        var settings = SettingsService.Normalize(new AppSettings { DefaultOpacity = stored });
        Assert.Equal(expected, settings.DefaultOpacity, 3);
    }

    [Fact]
    public void Invalid_values_are_repaired()
    {
        var settings = SettingsService.Normalize(new AppSettings
        {
            Browser = new BrowserSettings
            {
                StartPage = "not a url",
                SearchUrlTemplate = "https://example.com/no-placeholder",
                UserDataFolder = "relative\\path",
            },
        });

        Assert.Equal(BrowserSettings.DefaultStartPage, settings.Browser.StartPage);
        Assert.Equal(BrowserSettings.DefaultSearchTemplate, settings.Browser.SearchUrlTemplate);
        Assert.Null(settings.Browser.UserDataFolder);
    }

    [Fact]
    public void Save_and_reload_round_trips()
    {
        var service = new SettingsService(SettingsPath);
        service.Load();
        service.Save(new AppSettings
        {
            DefaultOpacity = 0.6,
            Browser = new BrowserSettings { StartPage = "https://duckduckgo.com", SearchUrlTemplate = "https://duckduckgo.com/?q={query}" },
        });

        var reloaded = new SettingsService(SettingsPath).Load();
        Assert.Equal(0.6, reloaded.DefaultOpacity, 3);
        Assert.Equal("https://duckduckgo.com", reloaded.Browser.StartPage);
        Assert.Equal("DuckDuckGo", reloaded.Browser.SearchEngineName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
