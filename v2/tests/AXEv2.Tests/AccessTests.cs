using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AxeV2.Access;

namespace AxeV2.Tests;

/// <summary>
/// The real AccessController, AccessClient, SignedToken and AccessStore against a fake backend that signs with a real
/// ECDSA key. Covers request → decision → signed grant → validation → trusted time → expiry, including everything a
/// hostile or broken server response could do.
/// </summary>
public class AccessApprovalTests
{
    [Fact]
    public void Approval_activates_access_and_is_acknowledged_only_after_the_grant_is_saved() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        h.Server.OnAck = () => h.Store.Load().Grant is not null;

        await h.SubmitPendingAsync("5h");
        Assert.Equal(AccessPhase.Pending, h.Controller.Phase);
        Assert.False(h.Controller.Phase.AllowsBrowsing());

        h.Server.Approve("5h");
        await h.WaitFor(AccessPhase.Active);

        Assert.True(h.Controller.Phase.AllowsBrowsing());
        Assert.Equal("5h", h.Controller.ActivePlan!.Id);
        Assert.Equal(TimeSpan.FromHours(5), h.Controller.Remaining);

        var saved = h.Store.Load();
        Assert.False(string.IsNullOrEmpty(saved.Grant));
        Assert.Null(saved.PendingRequestId);
        Assert.Null(saved.PendingPollToken);

        await AccessHarness.WaitUntil(() => h.Server.AckCalls == 1, what: "the acknowledgement");
        Assert.True(h.Server.GrantWasStoredWhenAcknowledged, "the result is acknowledged only once it is safely stored");
        Assert.Equal(
            new[] { AccessPhase.Checking, AccessPhase.NeedsAccess, AccessPhase.Pending, AccessPhase.Active },
            h.Phases.Distinct().ToArray());
    });

    [Fact]
    public void A_failing_acknowledgement_never_affects_access() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        h.Server.AckStatusCode = 500;
        await h.SubmitPendingAsync();
        h.Server.Approve();
        await h.WaitFor(AccessPhase.Active);
        await AccessHarness.WaitUntil(() => h.Server.AckCalls >= 1, what: "the acknowledgement attempt");
        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
        Assert.NotNull(h.Store.Load().Grant);
    });

    [Fact]
    public void A_temporary_failure_while_pending_is_never_treated_as_a_decision() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();

        foreach (var mode in new[] { StatusMode.ServerError, StatusMode.Unreachable })
        {
            h.Server.Mode = mode;
            var before = h.Server.StatusCalls;
            await AccessHarness.WaitUntil(() => h.Server.StatusCalls >= before + 2, 20000, $"polls during {mode}");
            Assert.Equal(AccessPhase.Pending, h.Controller.Phase);
            Assert.NotNull(h.Store.Load().PendingRequestId);
            Assert.Null(h.Store.Load().Grant);
        }

        // The connection comes back and the (earlier) approval is delivered: the lost answer is simply retried.
        h.Server.Approve();
        await h.WaitFor(AccessPhase.Active, 20000);
    });

    [Fact]
    public void Polling_a_pending_request_keeps_waiting() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        await AccessHarness.WaitUntil(() => h.Server.StatusCalls >= 3, what: "several polls");
        Assert.Equal(AccessPhase.Pending, h.Controller.Phase);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
    });

    [Fact]
    public void Pending_request_survives_a_restart_and_the_decision_still_arrives() => Sta.Run(async () =>
    {
        using var first = new AccessHarness();
        await first.SubmitPendingAsync("1h");
        Assert.Equal(AccessPhase.Pending, first.Controller.Phase);
        first.Controller.Dispose(); // the app is closed while the request waits

        // The app starts again: same PC (same store), same backend request.
        using var second = new AccessHarness(directory: first.StoreDirectory, server: first.Server);
        await second.Controller.StartAsync();
        Assert.Equal(AccessPhase.Pending, second.Controller.Phase);
        Assert.False(second.Controller.Phase.AllowsBrowsing());

        first.Server.Approve("1h");
        await second.WaitFor(AccessPhase.Active);
        Assert.Equal(TimeSpan.FromHours(1), second.Controller.Remaining);
    });
}

public class AccessRejectionTests
{
    [Fact]
    public void Rejection_returns_to_the_access_screen_and_never_grants() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();

        h.Server.Mode = StatusMode.Rejected;
        await h.WaitFor(AccessPhase.NeedsAccess);

        Assert.Contains("not approved", h.Controller.Message);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
        Assert.DoesNotContain(AccessPhase.Active, h.Phases);
        Assert.Null(h.Controller.ActivePlan);

        var saved = h.Store.Load();
        Assert.Null(saved.Grant);
        Assert.Null(saved.PendingRequestId);
        Assert.Null(saved.PendingPollToken);

        await AccessHarness.WaitUntil(() => h.Server.AckCalls == 1, what: "the rejection acknowledgement");
    });

    [Fact]
    public void After_a_rejection_a_new_request_is_required_and_polling_has_stopped() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        h.Server.Mode = StatusMode.Rejected;
        await h.WaitFor(AccessPhase.NeedsAccess);

        var polls = h.Server.StatusCalls;
        h.Server.Approve(); // a late approval for the old request must not revive it
        await Task.Delay(300);
        Assert.Equal(polls, h.Server.StatusCalls);
        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);

        await h.Controller.SubmitInvitationAsync("Test User", AccessPlan.Find("1h")!, "TESTCODE", CancellationToken.None);
        Assert.Equal(AccessPhase.Pending, h.Controller.Phase);
    });

    [Fact]
    public void A_request_the_server_no_longer_knows_returns_to_the_access_screen() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        h.Server.Mode = StatusMode.NotFound;
        await h.WaitFor(AccessPhase.NeedsAccess);
        Assert.Contains("no longer active", h.Controller.Message);
        Assert.Null(h.Store.Load().PendingRequestId);
    });

    [Fact]
    public void An_unreviewed_request_that_expired_returns_to_the_access_screen() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        h.Server.Mode = StatusMode.Expired;
        await h.WaitFor(AccessPhase.NeedsAccess);
        Assert.Contains("in time", h.Controller.Message);
    });

    [Fact]
    public void Cancelling_a_pending_request_clears_it() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        await h.Controller.CancelPendingAsync();
        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.Null(h.Store.Load().PendingRequestId);
        Assert.Equal(1, h.Server.AckCalls);
    });
}

public class AccessGrantValidationTests
{
    /// <summary>Every way a grant can be wrong. None of them may start a session.</summary>
    [Theory]
    [InlineData("wrong-signature")]
    [InlineData("tampered-expiry")]
    [InlineData("wrong-device")]
    [InlineData("wrong-request")]
    [InlineData("wrong-type")]
    [InlineData("expired")]
    [InlineData("duration-does-not-match-plan")]
    [InlineData("expiry-before-issue")]
    [InlineData("unknown-plan")]
    [InlineData("missing-grant-id")]
    [InlineData("malformed")]
    [InlineData("empty")]
    public void An_invalid_grant_is_refused_and_never_activates_access(string flaw) => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        var s = h.Server;
        await h.SubmitPendingAsync("5h");

        var grant = flaw switch
        {
            "wrong-signature" => s.Grant(signWith: s.OtherKey),
            "tampered-expiry" => Tamper(s.Grant("1h"), 10 * 3_600_000L),
            "wrong-device" => s.Grant(device: new string('b', 64)),
            "wrong-request" => s.Grant(requestId: Guid.NewGuid().ToString()),
            "wrong-type" => s.Grant(type: "axe-session"),
            "expired" => s.Grant("1h", issuedAt: s.Now - 2 * 3_600_000L),
            "duration-does-not-match-plan" => s.Grant("1h", expiresAt: s.Now + 10 * 3_600_000L),
            "expiry-before-issue" => s.Grant("1h", expiresAt: s.Now - 1),
            "unknown-plan" => s.Sign(new { typ = "axe-grant", jti = Guid.NewGuid().ToString(), rid = s.RequestId, plan = "99h", iat = s.Now, exp = s.Now + 3_600_000L, did = s.DeviceHash }),
            "missing-grant-id" => s.Grant(jti: ""),
            "malformed" => "not.a.valid.token",
            _ => string.Empty,
        };
        s.Approve(grant: grant);

        await h.WaitFor(AccessPhase.NeedsAccess);

        Assert.DoesNotContain(AccessPhase.Active, h.Phases);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
        Assert.NotNull(h.Controller.Message);
        var saved = h.Store.Load();
        Assert.Null(saved.Grant);
        Assert.Null(saved.PendingRequestId);
    });

    [Fact]
    public void A_valid_grant_for_each_plan_is_accepted_with_the_server_duration() => Sta.Run(async () =>
    {
        foreach (var (plan, hours) in new[] { ("1h", 1), ("5h", 5), ("10h", 10) })
        {
            using var h = new AccessHarness();
            await h.SubmitPendingAsync(plan);
            h.Server.Approve(plan);
            await h.WaitFor(AccessPhase.Active);
            Assert.Equal(plan, h.Controller.ActivePlan!.Id);
            Assert.Equal(TimeSpan.FromHours(hours), h.Controller.Remaining);
        }
    });

    [Fact]
    public void A_status_answer_replayed_for_another_nonce_or_request_is_never_trusted() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        // A signed answer for some other request id must not be accepted as this request's decision.
        h.Server.Approve();
        var foreign = h.Server.Sign(new { typ = "axe-status", rid = Guid.NewGuid().ToString(), nonce = "AAAAAAAAAAAAAAAAAAAA", status = "approved", now = h.Server.Now, grant = h.Server.Grant() });
        var client = new AccessClient(AccessConfig.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            functionsUrl = "https://axe.test/functions/v1", anonKey = "k", signingPublicKey = h.Server.PublicKeySpki,
        })), new StaticTokenHandler(foreign));
        var ex = await Assert.ThrowsAsync<AccessException>(() => client.GetStatusAsync(h.Server.RequestId, h.Server.PollToken, CancellationToken.None));
        Assert.Equal(AccessErrorKind.Untrusted, ex.Kind);
    });

    private static string Tamper(string grant, long addToExpiry)
    {
        var dot = grant.IndexOf('.');
        var claims = System.Text.Json.Nodes.JsonNode.Parse(Base64Url.Decode(grant[..dot]))!;
        claims["exp"] = claims["exp"]!.GetValue<long>() + addToExpiry;
        return Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString())) + grant[dot..];
    }

    private sealed class StaticTokenHandler : HttpMessageHandler
    {
        private readonly string _token;
        public StaticTokenHandler(string token) => _token = token;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { token = _token }), Encoding.UTF8, "application/json"),
            });
    }
}

public class AccessSessionResumeTests
{
    private static string SaveGrant(AccessHarness h, string grant)
    {
        h.Store.Save(new AccessState { Grant = grant });
        return grant;
    }

    [Fact]
    public void A_saved_valid_grant_resumes_with_the_remaining_server_time_not_a_fresh_timer() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        // Issued an hour ago for 5 hours: 4 hours must remain after a restart, never 5.
        SaveGrant(h, h.Server.Grant("5h", issuedAt: h.Server.Now - 3_600_000L));

        await h.Controller.StartAsync();

        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
        Assert.Equal(TimeSpan.FromHours(4), h.Controller.Remaining);
        Assert.Equal(1, h.Server.SessionCalls);
    });

    [Fact]
    public void A_saved_grant_the_server_says_expired_is_cleared_and_the_access_screen_shown() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        SaveGrant(h, h.Server.Grant("1h"));
        h.Server.SessionValid = false;
        h.Server.SessionReason = "expired";

        await h.Controller.StartAsync();

        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.Contains("time has ended", h.Controller.Message);
        Assert.Null(h.Store.Load().Grant);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
    });

    [Fact]
    public void A_saved_grant_the_admin_revoked_is_cleared() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        SaveGrant(h, h.Server.Grant("1h"));
        h.Server.SessionValid = false;
        h.Server.SessionReason = "revoked";

        await h.Controller.StartAsync();

        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.Contains("ended by the AXE admin", h.Controller.Message);
        Assert.Null(h.Store.Load().Grant);
    });

    [Fact]
    public void Offline_with_a_saved_grant_stays_locked_until_the_server_can_confirm_it() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        SaveGrant(h, h.Server.Grant("5h"));
        h.Server.SessionUnreachable = true;

        await h.Controller.StartAsync();

        // Without the server's time the expiry cannot be trusted, so a stale grant never opens the browser.
        Assert.Equal(AccessPhase.Offline, h.Controller.Phase);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
        Assert.NotNull(h.Store.Load().Grant);

        h.Server.SessionUnreachable = false;
        await h.Controller.RetryAsync();
        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
    });

    [Theory]
    [InlineData("tampered")]
    [InlineData("wrong-device")]
    [InlineData("wrong-signature")]
    [InlineData("garbage")]
    public void A_saved_grant_that_fails_verification_is_discarded_without_asking_the_server(string flaw) => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        var s = h.Server;
        SaveGrant(h, flaw switch
        {
            "tampered" => TamperExpiry(s.Grant("1h")),
            "wrong-device" => s.Grant(device: new string('c', 64)),
            "wrong-signature" => s.Grant(signWith: s.OtherKey),
            _ => "garbage",
        });

        await h.Controller.StartAsync();

        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.Null(h.Store.Load().Grant);
        Assert.Equal(0, s.SessionCalls);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
    });

    [Fact]
    public void A_session_answer_for_a_different_grant_does_not_activate_access() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        SaveGrant(h, h.Server.Grant("1h"));
        h.Server.SessionGrantIdOverride = Guid.NewGuid().ToString();

        await h.Controller.StartAsync();

        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.Null(h.Store.Load().Grant);
    });

    [Fact]
    public void No_saved_state_shows_the_access_screen() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.Controller.StartAsync();
        Assert.Equal(AccessPhase.NeedsAccess, h.Controller.Phase);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
    });

    [Fact]
    public void A_build_without_server_configuration_never_allows_browsing() => Sta.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "axe-access-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var controller = new AccessController((AccessClient?)null, new AccessStore(dir));
            await controller.StartAsync();
            Assert.Equal(AccessPhase.NotConfigured, controller.Phase);
            Assert.False(controller.Phase.AllowsBrowsing());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    private static string TamperExpiry(string grant)
    {
        var dot = grant.IndexOf('.');
        var claims = System.Text.Json.Nodes.JsonNode.Parse(Base64Url.Decode(grant[..dot]))!;
        claims["exp"] = claims["exp"]!.GetValue<long>() + 10 * 3_600_000L;
        return Base64Url.Encode(Encoding.UTF8.GetBytes(claims.ToJsonString())) + grant[dot..];
    }
}

public class AccessExpiryTests
{
    [Theory]
    [InlineData("1h", 1)]
    [InlineData("5h", 5)]
    [InlineData("10h", 10)]
    public void Access_runs_for_exactly_the_plan_duration_then_locks(string plan, int hours) => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync(plan);
        h.Server.Approve(plan);
        await h.WaitFor(AccessPhase.Active);
        Assert.Equal(TimeSpan.FromHours(hours), h.Controller.Remaining);

        h.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        h.Controller.OnTick();
        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
        Assert.Equal(TimeSpan.FromSeconds(1), h.Controller.Remaining);

        h.Advance(TimeSpan.FromSeconds(2));
        h.Controller.OnTick();
        await h.WaitFor(AccessPhase.NeedsAccess);

        Assert.Equal(TimeSpan.Zero, h.Controller.Remaining);
        Assert.False(h.Controller.Phase.AllowsBrowsing());
        Assert.Contains("time has ended", h.Controller.Message);
        Assert.Null(h.Store.Load().Grant);
        Assert.Null(h.Controller.ActivePlan);
    });

    [Fact]
    public void After_expiry_a_new_request_is_required() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync("1h");
        h.Server.Approve("1h");
        await h.WaitFor(AccessPhase.Active);
        h.Advance(TimeSpan.FromHours(2));
        h.Controller.OnTick();
        await h.WaitFor(AccessPhase.NeedsAccess);

        // Restarting does not bring it back: the saved grant is gone and there is no pending request.
        using var restarted = new AccessHarness(directory: h.StoreDirectory, server: h.Server);
        await restarted.Controller.StartAsync();
        Assert.Equal(AccessPhase.NeedsAccess, restarted.Controller.Phase);
        Assert.False(restarted.Controller.Phase.AllowsBrowsing());
        Assert.Null(restarted.Store.Load().Grant);
    });

    [Fact]
    public void The_countdown_follows_the_monotonic_clock_only() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync("5h");
        h.Server.Approve("5h");
        await h.WaitFor(AccessPhase.Active);

        h.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromHours(4.5), h.Controller.Remaining);
        h.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromHours(4), h.Controller.Remaining);
    });

    [Fact]
    public void A_periodic_check_that_finds_the_grant_revoked_locks_access() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        h.Server.Approve();
        await h.WaitFor(AccessPhase.Active);

        h.Server.SessionValid = false;
        h.Server.SessionReason = "revoked";
        await h.Controller.ResyncAsync();
        await h.WaitFor(AccessPhase.NeedsAccess);

        Assert.Contains("ended by the AXE admin", h.Controller.Message);
        Assert.Null(h.Store.Load().Grant);
    });

    [Fact]
    public void Losing_the_connection_mid_session_keeps_counting_down_and_never_extends_time() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync("1h");
        h.Server.Approve("1h");
        await h.WaitFor(AccessPhase.Active);

        h.Server.SessionUnreachable = true;
        h.Advance(TimeSpan.FromMinutes(20));
        await h.Controller.ResyncAsync();

        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
        Assert.Equal(TimeSpan.FromMinutes(40), h.Controller.Remaining);

        h.Advance(TimeSpan.FromMinutes(41)); // offline the whole time: it still ends on schedule
        h.Controller.OnTick();
        await h.WaitFor(AccessPhase.NeedsAccess);
    });

    [Fact]
    public void A_server_resync_re_anchors_the_clock_to_server_time() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync("1h");
        h.Server.Approve("1h");
        await h.WaitFor(AccessPhase.Active);

        // Server time moved 10 minutes while the local counter moved only 1 (e.g. after a long sleep).
        h.Server.Now += 10 * 60_000L;
        h.Advance(TimeSpan.FromMinutes(1));
        await h.Controller.ResyncAsync();

        Assert.Equal(TimeSpan.FromMinutes(50), h.Controller.Remaining);
    });
}

public class TrustedClockTests
{
    [Fact]
    public void Remaining_is_zero_until_synced()
    {
        var clock = new TrustedClock(() => 0);
        Assert.False(clock.IsSynced);
        Assert.Equal(TimeSpan.Zero, clock.Remaining(long.MaxValue));
    }

    [Fact]
    public void Time_passes_only_with_the_monotonic_counter()
    {
        long tick = 5_000;
        var clock = new TrustedClock(() => tick);
        clock.Sync(1_000_000);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), clock.Remaining(1_010_000));

        tick += 4_000;
        Assert.Equal(TimeSpan.FromMilliseconds(6_000), clock.Remaining(1_010_000));
        Assert.Equal(1_004_000, clock.NowMs);
    }

    [Fact]
    public void A_counter_that_goes_backwards_never_adds_time()
    {
        long tick = 10_000;
        var clock = new TrustedClock(() => tick);
        clock.Sync(1_000_000);
        tick = 2_000; // counter reset / tampered
        Assert.Equal(1_000_000, clock.NowMs);
        Assert.Equal(TimeSpan.FromMilliseconds(5_000), clock.Remaining(1_005_000));
    }

    [Fact]
    public void The_wall_clock_is_not_consulted()
    {
        // The clock is built from a synced server time and a counter only; nothing here can read DateTime.Now, so
        // changing the Windows clock cannot change the result.
        long tick = 0;
        var clock = new TrustedClock(() => tick);
        clock.Sync(FakeAccessServer.StartNow);
        var before = clock.Remaining(FakeAccessServer.StartNow + 3_600_000);
        Thread.Sleep(50); // real time passes, the counter does not
        Assert.Equal(before, clock.Remaining(FakeAccessServer.StartNow + 3_600_000));
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(1, "0:00:01")]
    [InlineData(3_600, "1:00:00")]
    [InlineData(36_000, "10:00:00")]
    public void Format_is_h_mm_ss(int seconds, string expected) =>
        Assert.Equal(expected, TrustedClock.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Format_rounds_up_so_zero_only_shows_when_access_has_ended() =>
        Assert.Equal("0:00:01", TrustedClock.Format(TimeSpan.FromMilliseconds(200)));
}

public class AccessBrowserGateTests
{
    [Theory]
    [InlineData(AccessPhase.NotConfigured, false)]
    [InlineData(AccessPhase.Checking, false)]
    [InlineData(AccessPhase.NeedsAccess, false)]
    [InlineData(AccessPhase.Pending, false)]
    [InlineData(AccessPhase.Offline, false)]
    [InlineData(AccessPhase.Active, true)]
    public void Only_an_active_authorization_allows_the_browser(AccessPhase phase, bool allowed) =>
        Assert.Equal(allowed, phase.AllowsBrowsing());

    [Fact]
    public void Every_phase_is_covered_so_a_new_phase_cannot_silently_open_the_browser()
    {
        var allowed = Enum.GetValues<AccessPhase>().Where(p => p.AllowsBrowsing()).ToArray();
        Assert.Equal(new[] { AccessPhase.Active }, allowed);
    }
}

public class SignedTokenTests
{
    private static (SignedToken verifier, FakeAccessServer server) Create()
    {
        var server = new FakeAccessServer();
        return (SignedToken.FromSpki(server.PublicKeySpki), server);
    }

    private static string TypeOf(StatusClaims c) => c.Type;

    [Fact]
    public void A_genuine_token_verifies()
    {
        var (v, s) = Create();
        var token = s.Sign(new { typ = "axe-status", rid = "r", nonce = "n", status = "pending", now = 1L, grant = (string?)null });
        Assert.Equal("pending", v.Verify<StatusClaims>(token, "axe-status", TypeOf).Status);
    }

    [Fact]
    public void A_token_signed_by_another_key_is_untrusted()
    {
        var (v, s) = Create();
        var token = s.Sign(new { typ = "axe-status", status = "approved" }, s.OtherKey);
        Assert.Equal(AccessErrorKind.Untrusted, Assert.Throws<AccessException>(() => v.Verify<StatusClaims>(token, "axe-status", TypeOf)).Kind);
    }

    [Fact]
    public void A_tampered_payload_is_untrusted()
    {
        var (v, s) = Create();
        var token = s.Sign(new { typ = "axe-status", status = "rejected" });
        var dot = token.IndexOf('.');
        var forged = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"axe-status\",\"status\":\"approved\"}")) + token[dot..];
        Assert.Throws<AccessException>(() => v.Verify<StatusClaims>(forged, "axe-status", TypeOf));
    }

    [Fact]
    public void A_token_of_the_wrong_type_is_untrusted()
    {
        var (v, s) = Create();
        var token = s.Sign(new { typ = "axe-session" });
        Assert.Throws<AccessException>(() => v.Verify<StatusClaims>(token, "axe-status", TypeOf));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("a.b.c")]
    [InlineData(".sig")]
    [InlineData("payload.")]
    [InlineData("!!!.???")]
    public void Malformed_tokens_are_untrusted(string? token)
    {
        var (v, _) = Create();
        Assert.Throws<AccessException>(() => v.Verify<StatusClaims>(token, "axe-status", TypeOf));
    }

    [Fact]
    public void An_oversized_token_is_untrusted()
    {
        var (v, _) = Create();
        Assert.Throws<AccessException>(() => v.Verify<StatusClaims>(new string('A', 20_000) + "." + new string('B', 86), "axe-status", TypeOf));
    }
}

public class AccessStoreTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "axe-access-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_device_identity_is_stable_per_install_and_different_between_installs()
    {
        var a = NewDir();
        var b = NewDir();
        try
        {
            var first = new AccessStore(a).DeviceHash();
            Assert.Matches("^[0-9a-f]{64}$", first);
            Assert.Equal(first, new AccessStore(a).DeviceHash());
            Assert.NotEqual(first, new AccessStore(b).DeviceHash());
        }
        finally
        {
            foreach (var d in new[] { a, b })
            {
                try { Directory.Delete(d, true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void State_round_trips_and_is_not_stored_as_plain_text()
    {
        var dir = NewDir();
        try
        {
            var store = new AccessStore(dir);
            store.Save(new AccessState { Grant = "GRANT-SECRET-VALUE", PendingRequestId = "REQ", PendingPollToken = "POLL-SECRET" });
            var loaded = store.Load();
            Assert.Equal("GRANT-SECRET-VALUE", loaded.Grant);
            Assert.Equal("POLL-SECRET", loaded.PendingPollToken);

            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "access.dat")));
            Assert.DoesNotContain("GRANT-SECRET-VALUE", raw);
            Assert.DoesNotContain("POLL-SECRET", raw);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_corrupted_state_file_can_only_lose_access_never_extend_it()
    {
        var dir = NewDir();
        try
        {
            var store = new AccessStore(dir);
            store.Save(new AccessState { Grant = "g" });
            File.WriteAllBytes(Path.Combine(dir, "access.dat"), RandomNumberGenerator.GetBytes(64));
            var loaded = store.Load();
            Assert.Null(loaded.Grant);
            Assert.Null(loaded.PendingRequestId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Deleting_the_state_files_loses_access_but_keeps_the_device_identity()
    {
        var dir = NewDir();
        try
        {
            var store = new AccessStore(dir);
            var device = store.DeviceHash();
            store.Save(new AccessState { Grant = "g" });
            File.Delete(Path.Combine(dir, "access.dat"));
            Assert.Null(store.Load().Grant);
            Assert.Equal(device, store.DeviceHash());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
