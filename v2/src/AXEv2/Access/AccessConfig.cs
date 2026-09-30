using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxeV2.Access;

/// <summary>
/// Client-safe deployment configuration, embedded at build time from <c>config/server.json</c>
/// (never committed). Everything here is public by design:
/// <list type="bullet">
/// <item>the Supabase Edge Functions URL,</item>
/// <item>the Supabase anon (publishable) key — it grants nothing by itself; all tables are
/// locked by row-level security and only the server-side functions touch them,</item>
/// <item>the public half of the server's authorization signing key,</item>
/// <item>the update manifest URL and the public release-signing key.</item>
/// </list>
/// Secrets (service-role key, private signing keys, FCM credentials) never ship in AXE.
/// </summary>
public sealed class AccessConfig
{
    public const string ResourceName = "AxeV2.server.json";

    [JsonPropertyName("functionsUrl")] public string FunctionsUrl { get; set; } = string.Empty;
    [JsonPropertyName("anonKey")] public string AnonKey { get; set; } = string.Empty;
    [JsonPropertyName("signingPublicKey")] public string SigningPublicKey { get; set; } = string.Empty;
    [JsonPropertyName("updateManifestUrl")] public string? UpdateManifestUrl { get; set; }
    [JsonPropertyName("releasePublicKey")] public string? ReleasePublicKey { get; set; }

    [JsonIgnore] public Uri FunctionsUri { get; private set; } = null!;

    /// <summary>Loads the embedded configuration, or null when this build was made without one.</summary>
    public static AccessConfig? LoadEmbedded()
    {
        using var stream = typeof(AccessConfig).Assembly.GetManifestResourceStream(ResourceName);
        return stream is null ? null : Parse(new StreamReader(stream).ReadToEnd());
    }

    public static AccessConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<AccessConfig>(json)
            ?? throw new InvalidDataException("Empty server configuration.");
        config.FunctionsUri = RequireSecureUri(config.FunctionsUrl, "functionsUrl");
        if (string.IsNullOrWhiteSpace(config.AnonKey) || string.IsNullOrWhiteSpace(config.SigningPublicKey))
        {
            throw new InvalidDataException("Server configuration is incomplete.");
        }

        if (config.UpdateManifestUrl is { Length: > 0 } manifest)
        {
            RequireSecureUri(manifest, "updateManifestUrl");
        }

        return config;
    }

    /// <summary>
    /// HTTPS only. Plain HTTP is accepted solely for this PC's loopback address, which is
    /// how the backend runs during local development; traffic to it never leaves the machine.
    /// </summary>
    public static Uri RequireSecureUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException($"{name} is not a valid URL.");
        }

        var secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri));
        if (!secure)
        {
            throw new InvalidDataException($"{name} must use HTTPS.");
        }

        return uri;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
