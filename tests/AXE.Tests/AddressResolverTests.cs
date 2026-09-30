using AXE.Browser;

namespace AXE.Tests;

public class AddressResolverTests
{
    private const string Google = BrowserSettings.DefaultSearchTemplate;

    [Theory]
    [InlineData("https://google.com", "https://google.com/")]
    [InlineData("http://example.org/path?q=1", "http://example.org/path?q=1")]
    [InlineData("google.com", "https://google.com/")]
    [InlineData("www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc")]
    [InlineData("sub.domain.co.uk", "https://sub.domain.co.uk/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("127.0.0.1:8080/api", "http://127.0.0.1:8080/api")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("  https://google.com  ", "https://google.com/")]
    public void Urls_navigate_directly(string input, string expected)
    {
        var result = AddressResolver.Resolve(input, Google);
        Assert.NotNull(result);
        Assert.False(result!.Value.IsSearch);
        Assert.Equal(expected, result.Value.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("weather in Delhi", "weather%20in%20Delhi")]
    [InlineData("hello", "hello")]
    [InlineData("c# tutorial", "c%23%20tutorial")]
    [InlineData("what is 2+2?", "what%20is%202%2B2%3F")]
    [InlineData("?google.com", "google.com")]
    public void Text_becomes_a_search(string input, string expectedQuery)
    {
        var result = AddressResolver.Resolve(input, Google);
        Assert.NotNull(result);
        Assert.True(result!.Value.IsSearch);
        Assert.Equal("https://www.google.com/search?q=" + expectedQuery, result.Value.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:settings")]
    [InlineData("ftp://example.com")]
    public void Unsafe_or_unsupported_schemes_are_searched_not_opened(string input)
    {
        var result = AddressResolver.Resolve(input, Google);
        Assert.True(result!.Value.IsSearch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_resolves_to_nothing(string? input) =>
        Assert.Null(AddressResolver.Resolve(input, Google));

    [Fact]
    public void Custom_search_engine_is_used()
    {
        var result = AddressResolver.Resolve("cats", "https://duckduckgo.com/?q={query}");
        Assert.Equal("https://duckduckgo.com/?q=cats", result!.Value.Uri.AbsoluteUri);
    }

    [Fact]
    public void Invalid_search_template_falls_back_to_default()
    {
        var result = AddressResolver.Resolve("cats", "not a template");
        Assert.Equal("https://www.google.com/search?q=cats", result!.Value.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.bing.com/search?q={query}", true)]
    [InlineData("https://example.com/search", false)]
    [InlineData("javascript:{query}", false)]
    [InlineData("", false)]
    public void Search_template_validation(string template, bool valid) =>
        Assert.Equal(valid, AddressResolver.IsValidSearchTemplate(template));

    [Theory]
    [InlineData("https://www.google.com", true)]
    [InlineData("about:blank", true)]
    [InlineData("google.com", false)]
    [InlineData("data:text/html,hi", false)]
    public void Start_page_validation(string value, bool valid) =>
        Assert.Equal(valid, AddressResolver.IsValidStartPage(value));
}
