using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxeV2.Access;

/// <summary>
/// HTTPS client for the AXE access backend (Supabase Edge Function <c>access</c>).
/// <para>
/// Sends only what a request needs — name, plan, amount, UTR/reference, screenshot or
/// invitation code, and the device hash — never browsing data. Every answer that affects
/// access (status, grant, server time) is ECDSA-signed by the server and verified here with a
/// fresh nonce, so responses cannot be forged, altered or replayed.
/// </para>
/// </summary>
public sealed class AccessClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SignedToken _verifier;
    private readonly Uri _base;

    public AccessClient(AccessConfig config, HttpMessageHandler? handler = null)
    {
        _verifier = SignedToken.FromSpki(config.SigningPublicKey);
        _base = new Uri(config.FunctionsUri.AbsoluteUri.TrimEnd('/') + "/access/");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeouts via cancellation below
        _http.DefaultRequestHeaders.Add("apikey", config.AnonKey);
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AnonKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AXE-v2/{typeof(AccessClient).Assembly.GetName().Version?.ToString(3)}");
    }

    internal SignedToken Verifier => _verifier;

    public Task<SubmittedRequest> SubmitPaymentAsync(string name, AccessPlan plan, decimal amountPaid, string reference,
        byte[] screenshot, string mediaType, string deviceHash, string devicePublicKey, CancellationToken ct) =>
        SubmitAsync(new JsonObject
        {
            ["kind"] = "payment",
            ["name"] = name,
            ["plan"] = plan.Id,
            ["amountPaid"] = amountPaid,
            ["utr"] = reference,
            ["screenshot"] = new JsonObject { ["type"] = mediaType, ["data"] = Convert.ToBase64String(screenshot) },
            ["device"] = deviceHash,
            ["devicePublicKey"] = devicePublicKey,
        }, TimeSpan.FromSeconds(120), ct);

    public Task<SubmittedRequest> SubmitInvitationAsync(string name, AccessPlan plan, string code, string deviceHash, string devicePublicKey, CancellationToken ct) =>
        SubmitAsync(new JsonObject
        {
            ["kind"] = "invite",
            ["name"] = name,
            ["plan"] = plan.Id,
            ["code"] = code,
            ["device"] = deviceHash,
            ["devicePublicKey"] = devicePublicKey,
        }, TimeSpan.FromSeconds(30), ct);

    private async Task<SubmittedRequest> SubmitAsync(JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "requests"))
        {
            Content = JsonContent.Create(body),
        }, timeout, ct);
        var json = await ReadJsonAsync(response, ct);
        var id = json?["requestId"]?.GetValue<string>();
        var poll = json?["pollToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(poll))
        {
            throw new AccessException(AccessErrorKind.Untrusted, "The AXE server sent an unexpected response.");
        }

        return new SubmittedRequest(id, poll);
    }

    /// <summary>Asks for the decision on a pending request. The answer is signed and bound to a fresh nonce.</summary>
    public async Task<StatusClaims> GetStatusAsync(string requestId, string pollToken, CancellationToken ct)
    {
        var nonce = NewNonce();
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_base, $"requests/{Uri.EscapeDataString(requestId)}?nonce={nonce}"));
            request.Headers.Add("x-poll-token", pollToken);
            return request;
        }, TimeSpan.FromSeconds(20), ct);
        var json = await ReadJsonAsync(response, ct);
        var status = _verifier.Verify<StatusClaims>(json?["token"]?.GetValue<string>(), "axe-status", c => c.Type);
        if (status.Nonce != nonce || status.RequestId != requestId)
        {
            throw new AccessException(AccessErrorKind.Untrusted, "The AXE server's response could not be verified (replay).");
        }

        return status;
    }

    /// <summary>Withdraws a pending request; the server deletes it (and its screenshot) at once.</summary>
    public async Task CancelAsync(string requestId, string pollToken, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_base, $"requests/{Uri.EscapeDataString(requestId)}"));
            request.Headers.Add("x-poll-token", pollToken);
            return request;
        }, TimeSpan.FromSeconds(15), ct);
        await ReadJsonAsync(response, ct);
    }

    /// <summary>
    /// Tells the server the decided result (approval or rejection) has been stored, so it deletes its result stub
    /// at once. The server keeps the stub readable until this arrives (or a short grace period ends), which is what
    /// lets a lost response be retried. Same endpoint and idempotent semantics as <see cref="CancelAsync"/>.
    /// </summary>
    public Task AcknowledgeAsync(string requestId, string pollToken, CancellationToken ct) =>
        CancelAsync(requestId, pollToken, ct);

    /// <summary>
    /// Validates a grant with the server and returns the signed, current server time. <paramref name="proveDevice"/> turns the
    /// request's fresh nonce into this installation's signature (see <see cref="AccessStore.SignDeviceProof"/>): the server
    /// accepts the grant only for the installation that holds the matching private key.
    /// </summary>
    public async Task<SessionClaims> CheckSessionAsync(string grant, Func<string, string> proveDevice, CancellationToken ct)
    {
        var nonce = NewNonce();
        var proof = proveDevice(nonce);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "session"))
        {
            Content = JsonContent.Create(new JsonObject { ["grant"] = grant, ["nonce"] = nonce, ["proof"] = proof }),
        }, TimeSpan.FromSeconds(20), ct);
        var json = await ReadJsonAsync(response, ct);
        var session = _verifier.Verify<SessionClaims>(json?["token"]?.GetValue<string>(), "axe-session", c => c.Type);
        if (session.Nonce != nonce)
        {
            throw new AccessException(AccessErrorKind.Untrusted, "The AXE server's response could not be verified (replay).");
        }

        return session;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = create();
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AccessException(AccessErrorKind.Network, "The AXE server didn't respond in time. Check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            // Includes TLS/certificate validation failures: the connection is refused, never trusted.
            throw new AccessException(AccessErrorKind.Network, "Can't reach the AXE server. Check your internet connection and try again.", ex);
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        JsonNode? json = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            json = text.Length is > 0 and < 256 * 1024 ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
            // handled by the status checks below
        }

        if (response.IsSuccessStatusCode)
        {
            return json;
        }

        var message = json?["error"]?.GetValue<string>();
        throw response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => new AccessException(AccessErrorKind.RateLimited,
                message ?? "Too many attempts. Please wait a few minutes and try again."),
            HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict
                or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge
                => new AccessException(AccessErrorKind.Rejected, message ?? "The request was not accepted."),
            _ => new AccessException(AccessErrorKind.Network, "The AXE server is unavailable right now. Please try again shortly."),
        };
    }

    private static string NewNonce() => Base64Url.Encode(RandomNumberGenerator.GetBytes(18));

    public void Dispose() => _http.Dispose();
}
