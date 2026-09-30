using System.Text.Json.Serialization;

namespace AxeV2.Access;

/// <summary>A purchasable access duration. Prices are shown here but always decided by the server.</summary>
public sealed record AccessPlan(string Id, string Label, int Hours, int PriceInr)
{
    public static IReadOnlyList<AccessPlan> All { get; } = new[]
    {
        new AccessPlan("1h", "1 Hour", 1, 149),
        new AccessPlan("5h", "5 Hours", 5, 199),
        new AccessPlan("10h", "10 Hours", 10, 299),
    };

    public static AccessPlan? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public string PriceText => $"₹{PriceInr}";
}

/// <summary>
/// Server-issued authorization, carried inside a signed token. Contains no name, payment
/// or browsing data: only what is needed to know how long access lasts and for which PC.
/// </summary>
public sealed class GrantClaims
{
    [JsonPropertyName("typ")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("jti")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("rid")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("plan")] public string Plan { get; set; } = string.Empty;
    /// <summary>Issued at (server clock, Unix ms).</summary>
    [JsonPropertyName("iat")] public long IssuedAt { get; set; }
    /// <summary>Expires at (server clock, Unix ms).</summary>
    [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
    /// <summary>SHA-256 of this PC's AXE device secret: a copied token does not work on another PC.</summary>
    [JsonPropertyName("did")] public string DeviceHash { get; set; } = string.Empty;
}

/// <summary>Signed answer to a status poll.</summary>
public sealed class StatusClaims
{
    [JsonPropertyName("typ")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("rid")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    /// <summary>pending | approved | rejected | expired</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("now")] public long ServerNow { get; set; }
    [JsonPropertyName("grant")] public string? Grant { get; set; }
}

/// <summary>Signed answer to a session check: the trusted server time for the timer.</summary>
public sealed class SessionClaims
{
    [JsonPropertyName("typ")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("jti")] public string GrantId { get; set; } = string.Empty;
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("now")] public long ServerNow { get; set; }
    [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
}

/// <summary>Result of submitting a request.</summary>
public sealed record SubmittedRequest(string RequestId, string PollToken);

/// <summary>What a failed server call means for the UI.</summary>
public enum AccessErrorKind
{
    /// <summary>No connection / DNS / timeout / 5xx: retry later.</summary>
    Network,
    /// <summary>The server refused the input (e.g. invalid invitation code); the message is shown.</summary>
    Rejected,
    /// <summary>A response failed signature/format checks: never trusted.</summary>
    Untrusted,
    /// <summary>Too many requests.</summary>
    RateLimited,
}

public sealed class AccessException : Exception
{
    public AccessException(AccessErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public AccessErrorKind Kind { get; }
}
