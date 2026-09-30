using System.Windows.Threading;
using AxeV2.Services;
using Microsoft.Win32;

namespace AxeV2.Access;

public enum AccessPhase
{
    /// <summary>This build has no server configuration.</summary>
    NotConfigured,
    /// <summary>Verifying a saved authorization with the server.</summary>
    Checking,
    /// <summary>No authorization: the access screen is shown.</summary>
    NeedsAccess,
    /// <summary>A request is waiting for the admin's decision.</summary>
    Pending,
    /// <summary>Authorized: browsing is available until the server-set expiry.</summary>
    Active,
    /// <summary>The server can't be reached to verify a saved authorization.</summary>
    Offline,
}

/// <summary>
/// Drives timed access: request → admin decision → signed grant → trusted timer → expiry.
/// <para>
/// All decisions come from the server: AXE only consumes ECDSA-signed grants and server
/// time. Nothing here can create or extend access. Network work is asynchronous and never
/// blocks the window; the countdown runs on a 1-second dispatcher timer.
/// </para>
/// </summary>
public sealed class AccessController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxPollBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(5);

    private readonly AccessClient? _client;
    private readonly AccessStore _store;
    private readonly TrustedClock _clock = new();
    private readonly DispatcherTimer _tick;
    private AccessState _state = new();
    private GrantClaims? _grant;
    private CancellationTokenSource? _pollCts;
    private DateTime _lastResync;
    private bool _resyncing;
    private bool _disposed;

    public AccessController(AccessConfig? config, AccessStore store)
    {
        _store = store;
        _client = config is null ? null : new AccessClient(config);
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public AccessPhase Phase { get; private set; } = AccessPhase.Checking;

    /// <summary>User-facing explanation for the current phase (e.g. why access ended), if any.</summary>
    public string? Message { get; private set; }

    public AccessPlan? ActivePlan => _grant is null ? null : AccessPlan.Find(_grant.Plan);

    public TimeSpan Remaining => _grant is null ? TimeSpan.Zero : _clock.Remaining(_grant.ExpiresAt);

    /// <summary>Raised on the UI thread whenever <see cref="Phase"/> or <see cref="Message"/> changes.</summary>
    public event EventHandler? PhaseChanged;

    /// <summary>Raised every second while access is active.</summary>
    public event EventHandler<TimeSpan>? Tick;

    // ------------------------------------------------------------------ startup

    public async Task StartAsync()
    {
        if (_client is null)
        {
            SetPhase(AccessPhase.NotConfigured, "This copy of AXE v2 isn't connected to an AXE server.");
            return;
        }

        SetPhase(AccessPhase.Checking, null);
        var device = await Task.Run(_store.DeviceHash);
        _state = await Task.Run(_store.Load);

        if (!string.IsNullOrEmpty(_state.Grant))
        {
            await ResumeGrantAsync(_state.Grant!, device);
            return;
        }

        if (!string.IsNullOrEmpty(_state.PendingRequestId) && !string.IsNullOrEmpty(_state.PendingPollToken))
        {
            SetPhase(AccessPhase.Pending, null);
            StartPolling();
            return;
        }

        SetPhase(AccessPhase.NeedsAccess, null);
    }

    /// <summary>Retry after <see cref="AccessPhase.Offline"/>.</summary>
    public Task RetryAsync() => StartAsync();

    private async Task ResumeGrantAsync(string grant, string device)
    {
        GrantClaims claims;
        try
        {
            claims = VerifyGrant(grant, device, expectedRequestId: null);
        }
        catch (AccessException)
        {
            Log.Warn("Saved authorization failed verification; discarded.");
            await EndAccessAsync("Your access could not be verified. Please request access again.");
            return;
        }

        try
        {
            var session = await _client!.CheckSessionAsync(grant, CancellationToken.None);
            if (!session.Valid || session.GrantId != claims.Id)
            {
                await EndAccessAsync(session.Reason == "revoked"
                    ? "Your access was ended by the AXE admin."
                    : "Your access time has ended. Choose a plan to continue.");
                return;
            }

            _clock.Sync(session.ServerNow);
            _lastResync = DateTime.UtcNow;
            ActivateGrant(claims);
        }
        catch (AccessException ex)
        {
            // The expiry can only be trusted with the server's time; without it AXE stays locked.
            Log.Warn($"Authorization check failed: {ex.Kind}.");
            SetPhase(AccessPhase.Offline, ex.Kind == AccessErrorKind.Untrusted
                ? "The AXE server's response could not be verified. Check your connection and try again."
                : ex.Message);
        }
    }

    // ------------------------------------------------------------------ requests

    public async Task SubmitPaymentAsync(string name, AccessPlan plan, decimal amountPaid, string reference,
        byte[] screenshot, string mediaType, CancellationToken ct)
    {
        var device = await Task.Run(_store.DeviceHash, ct);
        var submitted = await Client.SubmitPaymentAsync(name, plan, amountPaid, reference, screenshot, mediaType, device, ct);
        await BeginPendingAsync(submitted);
        Log.Info("Payment verification request submitted.");
    }

    public async Task SubmitInvitationAsync(string name, AccessPlan plan, string code, CancellationToken ct)
    {
        var device = await Task.Run(_store.DeviceHash, ct);
        var submitted = await Client.SubmitInvitationAsync(name, plan, code, device, ct);
        await BeginPendingAsync(submitted);
        Log.Info("Invitation request submitted.");
    }

    /// <summary>Withdraws the pending request (the server deletes it and any screenshot).</summary>
    public async Task CancelPendingAsync()
    {
        StopPolling();
        var id = _state.PendingRequestId;
        var poll = _state.PendingPollToken;
        _state.PendingRequestId = null;
        _state.PendingPollToken = null;
        await SaveAsync();
        SetPhase(AccessPhase.NeedsAccess, null);

        if (id is not null && poll is not null)
        {
            try
            {
                await Client.CancelAsync(id, poll, CancellationToken.None);
            }
            catch (AccessException ex)
            {
                Log.Warn($"Cancel request failed ({ex.Kind}); the server removes unanswered requests automatically.");
            }
        }
    }

    private AccessClient Client => _client ?? throw new AccessException(AccessErrorKind.Network, "AXE isn't connected to a server.");

    private async Task BeginPendingAsync(SubmittedRequest submitted)
    {
        _state.PendingRequestId = submitted.RequestId;
        _state.PendingPollToken = submitted.PollToken;
        await SaveAsync();
        SetPhase(AccessPhase.Pending, null);
        StartPolling();
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.Zero;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var id = _state.PendingRequestId;
            var poll = _state.PendingPollToken;
            if (id is null || poll is null)
            {
                return;
            }

            StatusClaims status;
            try
            {
                status = await Client.GetStatusAsync(id, poll, ct);
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AccessException ex)
            {
                if (ex.Kind == AccessErrorKind.Rejected)
                {
                    // Unknown/removed request (e.g. it timed out on the server).
                    await ClearPendingAsync("Your request is no longer active. Please submit it again.");
                    return;
                }

                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(MaxPollBackoff.TotalSeconds, PollInterval.TotalSeconds * Math.Pow(2, Math.Min(failures, 3))));
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            switch (status.Status)
            {
                case "approved":
                    await ApproveAsync(status, id);
                    return;
                case "rejected":
                    Log.Info("Access request rejected by the admin.");
                    await ClearPendingAsync("Your request was not approved. Check the details and try again.");
                    return;
                case "expired":
                    await ClearPendingAsync("Your request wasn't reviewed in time. Please submit it again.");
                    return;
                default:
                    delay = PollInterval;
                    break;
            }
        }
    }

    private async Task ApproveAsync(StatusClaims status, string requestId)
    {
        var device = await Task.Run(_store.DeviceHash);
        GrantClaims claims;
        try
        {
            claims = VerifyGrant(status.Grant, device, requestId);
        }
        catch (AccessException)
        {
            Log.Error("Approved grant failed verification.");
            await ClearPendingAsync("The authorization from the AXE server could not be verified. Please try again.");
            return;
        }

        _clock.Sync(status.ServerNow);
        _lastResync = DateTime.UtcNow;
        _state = new AccessState { Grant = status.Grant };
        await SaveAsync();
        Log.Info("Access approved.");
        ActivateGrant(claims);
    }

    private async Task ClearPendingAsync(string message)
    {
        StopPolling();
        _state.PendingRequestId = null;
        _state.PendingPollToken = null;
        await SaveAsync();
        SetPhase(AccessPhase.NeedsAccess, message);
    }

    private GrantClaims VerifyGrant(string? grant, string device, string? expectedRequestId)
    {
        var claims = _client!.Verifier.Verify<GrantClaims>(grant, "axe-grant", c => c.Type);
        if (!string.Equals(claims.DeviceHash, device, StringComparison.Ordinal)
            || (expectedRequestId is not null && claims.RequestId != expectedRequestId)
            || claims.ExpiresAt <= claims.IssuedAt
            || AccessPlan.Find(claims.Plan) is null)
        {
            throw new AccessException(AccessErrorKind.Untrusted, "Authorization does not belong to this PC.");
        }

        return claims;
    }

    // ------------------------------------------------------------------ active session

    private void ActivateGrant(GrantClaims claims)
    {
        _grant = claims;
        if (Remaining <= TimeSpan.Zero)
        {
            _ = EndAccessAsync("Your access time has ended. Choose a plan to continue.");
            return;
        }

        SetPhase(AccessPhase.Active, null);
        _tick.Start();
        Tick?.Invoke(this, Remaining);
    }

    private void OnTick()
    {
        if (_grant is null)
        {
            _tick.Stop();
            return;
        }

        var remaining = Remaining;
        Tick?.Invoke(this, remaining);
        if (remaining <= TimeSpan.Zero)
        {
            Log.Info("Access expired.");
            _ = EndAccessAsync("Your access time has ended. Choose a plan to continue.");
            return;
        }

        if (DateTime.UtcNow - _lastResync > ResyncInterval)
        {
            _ = ResyncAsync();
        }
    }

    /// <summary>Re-reads the server clock; also ends access that the admin revoked.</summary>
    private async Task ResyncAsync()
    {
        if (_resyncing || _grant is null || _state.Grant is null)
        {
            return;
        }

        _resyncing = true;
        _lastResync = DateTime.UtcNow;
        try
        {
            var session = await Client.CheckSessionAsync(_state.Grant, CancellationToken.None);
            if (_grant is null)
            {
                return;
            }

            if (!session.Valid)
            {
                await EndAccessAsync(session.Reason == "revoked"
                    ? "Your access was ended by the AXE admin."
                    : "Your access time has ended. Choose a plan to continue.");
                return;
            }

            _clock.Sync(session.ServerNow);
        }
        catch (AccessException ex)
        {
            // Offline mid-session: keep counting down on the monotonic clock (never extends time).
            Log.Warn($"Periodic access check failed ({ex.Kind}); continuing on the trusted clock.");
        }
        finally
        {
            _resyncing = false;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _lastResync = DateTime.MinValue; // re-sync on the next tick after the PC wakes
        }
    }

    /// <summary>Ends access: clears the saved grant and returns to the access screen.</summary>
    private async Task EndAccessAsync(string message)
    {
        _tick.Stop();
        _grant = null;
        _state.Grant = null;
        await SaveAsync();
        SetPhase(AccessPhase.NeedsAccess, message);
    }

    private Task SaveAsync()
    {
        var snapshot = new AccessState
        {
            Grant = _state.Grant,
            PendingRequestId = _state.PendingRequestId,
            PendingPollToken = _state.PendingPollToken,
        };
        return Task.Run(() => _store.Save(snapshot));
    }

    private void SetPhase(AccessPhase phase, string? message)
    {
        if (_disposed)
        {
            return;
        }

        Phase = phase;
        Message = message;
        PhaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tick.Stop();
        StopPolling();
        _client?.Dispose();
    }
}
