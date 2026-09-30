namespace AxeV2.Browser;

/// <summary>User-configurable browser options (persisted in settings.json).</summary>
public sealed class BrowserSettings
{
    public const string DefaultStartPage = "https://www.google.com";
    public const string DefaultSearchTemplate = "https://www.google.com/search?q={query}";

    /// <summary>Well-known search engines offered in the settings dialog.</summary>
    public static IReadOnlyList<(string Name, string Template)> KnownSearchEngines { get; } = new[]
    {
        ("Google", DefaultSearchTemplate),
        ("Bing", "https://www.bing.com/search?q={query}"),
        ("DuckDuckGo", "https://duckduckgo.com/?q={query}"),
    };

    /// <summary>Page loaded when AXE starts and when Home is used.</summary>
    public string StartPage { get; set; } = DefaultStartPage;

    /// <summary>Search URL; <c>{query}</c> is replaced by the URL-encoded search text.</summary>
    public string SearchUrlTemplate { get; set; } = DefaultSearchTemplate;

    /// <summary>Optional custom WebView2 profile folder. Empty = %LOCALAPPDATA%\AXE\WebView2.</summary>
    public string? UserDataFolder { get; set; }

    /// <summary>Chromium DevTools open in a separate window, so they are off by default.</summary>
    public bool EnableDevTools { get; set; }

    /// <summary>Autofill suggestions render in separate browser popups, so they are off by default.</summary>
    public bool EnableAutofill { get; set; }

    /// <summary>Friendly name of the current search engine, used for the address-bar placeholder.</summary>
    public string SearchEngineName
    {
        get
        {
            foreach (var (name, template) in KnownSearchEngines)
            {
                if (string.Equals(template, SearchUrlTemplate, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return "the web";
        }
    }
}
