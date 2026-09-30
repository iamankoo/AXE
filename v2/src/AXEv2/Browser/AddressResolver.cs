using System.Net;
using System.Text.RegularExpressions;

namespace AxeV2.Browser;

/// <summary>Turns address-bar input into either a direct URL or a web search.</summary>
public static partial class AddressResolver
{
    private static readonly HashSet<string> DirectSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "file", "about", "data",
    };

    public readonly record struct Resolution(Uri Uri, bool IsSearch);

    /// <summary>
    /// Resolves user input. Returns null for empty input.
    /// A leading '?' forces a search. <c>javascript:</c> input is never executed; it is searched.
    /// </summary>
    public static Resolution? Resolve(string? input, string searchTemplate)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith('?'))
        {
            return Search(text[1..].Trim(), searchTemplate);
        }

        if (TryDirect(text, out var direct))
        {
            return new Resolution(direct, false);
        }

        if (!ContainsWhitespace(text) && TryHostLike(text, out var implied))
        {
            return new Resolution(implied, false);
        }

        return Search(text, searchTemplate);
    }

    /// <summary>Builds a search URL from a template containing <c>{query}</c>.</summary>
    public static Resolution Search(string query, string searchTemplate)
    {
        var template = IsValidSearchTemplate(searchTemplate) ? searchTemplate : BrowserSettings.DefaultSearchTemplate;
        var url = template.Replace("{query}", Uri.EscapeDataString(query), StringComparison.OrdinalIgnoreCase);
        return new Resolution(new Uri(url), true);
    }

    public static bool IsValidSearchTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)
            || template.IndexOf("{query}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        var probe = template.Replace("{query}", "test", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(probe, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>True if the string is an acceptable start page (absolute http/https/file/about URL).</summary>
    public static bool IsValidStartPage(string? value) =>
        value is not null && TryDirect(value.Trim(), out var uri) && uri.Scheme != "data";

    private static bool TryDirect(string text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || !DirectSchemes.Contains(parsed.Scheme))
        {
            return false;
        }

        // "localhost:3000" parses with scheme "localhost" and is handled by TryHostLike.
        if ((parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        if (parsed.Scheme == "about" && !text.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool TryHostLike(string text, out Uri uri)
    {
        uri = null!;
        var hostEnd = text.IndexOfAny(new[] { '/', '?', '#' });
        var authority = hostEnd >= 0 ? text[..hostEnd] : text;
        if (authority.Length == 0 || authority.Contains('@'))
        {
            return false;
        }

        var host = authority;
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && !authority.EndsWith(']'))
        {
            var port = authority[(colon + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit))
            {
                return false;
            }

            host = authority[..colon];
        }

        var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                      || IPAddress.TryParse(host.Trim('[', ']'), out _);
        if (!isLocal && !DomainPattern().IsMatch(host))
        {
            return false;
        }

        var scheme = isLocal ? "http://" : "https://";
        return Uri.TryCreate(scheme + text, UriKind.Absolute, out uri!);
    }

    private static bool ContainsWhitespace(string text) => text.Any(char.IsWhiteSpace);

    // labels of letters/digits/hyphens separated by dots, ending in an alphabetic TLD (2-24 chars)
    [GeneratedRegex(@"^(?!-)([a-z0-9-]{1,63}\.)+[a-z]{2,24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
