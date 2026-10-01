using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AxeV2.Access;

namespace AxeV2.Tests;

/// <summary>What the fake backend answers to a status poll.</summary>
internal enum StatusMode { Pending, Approved, Rejected, Expired, NotFound, ServerError, Unreachable }

/// <summary>
/// The AXE backend at the HTTP level, with REAL ECDSA P-256 signing using a throw-away test key, so the Windows client's
/// real verification code runs against correctly and incorrectly signed tokens. It mirrors the contract of
/// <c>v2/backend/supabase/functions/access</c>: signed status / session tokens bound to the request nonce, and the
/// same DELETE endpoint used for withdrawing and for acknowledging a result.
/// </summary>
internal sealed class FakeAccessServer : HttpMessageHandler
{
    public const long StartNow = 1_800_000_000_000;

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ECDsa OtherKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKeySpki => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public string RequestId { get; } = Guid.NewGuid().ToString();
    public string PollToken { get; } = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The PC's device hash, as the real server would have received it with the request.</summary>
    public string DeviceHash { get; set; } = new string('a', 64);

    /// <summary>The installation's registered public key (base64 SPKI) the server checks session proofs against.</summary>
    public string? DevicePublicKey { get; set; }

    /// <summary>Like the real server: a grant is honoured only with a valid proof by the installation's key.</summary>
    public bool VerifyDeviceProofs { get; set; } = true;

    public long Now { get; set; } = StartNow;

    // ---- scripted behaviour
    public StatusMode Mode { get; set; } = StatusMode.Pending;
    public string? GrantForStatus { get; set; }
    public bool SessionUnreachable { get; set; }
    public bool SessionValid { get; set; } = true;
    public string? SessionReason { get; set; }
    public string? SessionGrantIdOverride { get; set; }
    public int AckStatusCode { get; set; } = 200;
    public Func<bool>? OnAck { get; set; }

    // ---- observations
    public int SubmitCalls;
    public int StatusCalls;
    public int SessionCalls;
    public int AckCalls;
    public bool? GrantWasStoredWhenAcknowledged;
    public string? LastSubmittedDevice;
    public string? LastSubmittedPublicKey;
    public string? LastProof;
    public string? LastNonce;

    /// <summary>Builds a signed grant like <c>admin/index.ts</c> does at approval.</summary>
    public string Grant(string plan = "5h", string? requestId = null, string? device = null, long? issuedAt = null,
        long? expiresAt = null, string type = "axe-grant", string? jti = null, ECDsa? signWith = null)
    {
        var iat = issuedAt ?? Now;
        var hours = plan switch { "1h" => 1, "5h" => 5, "10h" => 10, _ => 1 };
        return Sign(new
        {
            typ = type,
            jti = jti ?? Guid.NewGuid().ToString(),
            rid = requestId ?? RequestId,
            plan,
            iat,
            exp = expiresAt ?? iat + hours * 3_600_000L,
            did = device ?? DeviceHash,
        }, signWith);
    }

    public string Sign(object claims, ECDsa? key = null)
    {
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = (key ?? Key).SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256);
        return payload + "." + Base64Url.Encode(signature);
    }

    /// <summary>The admin approved: the next poll returns <paramref name="grant"/> (a valid one by default).</summary>
    public void Approve(string plan = "5h", string? grant = null)
    {
        GrantForStatus = grant ?? Grant(plan);
        Mode = StatusMode.Approved;
    }

    public static string GrantId(string grant)
    {
        var payload = Base64Url.Decode(grant[..grant.IndexOf('.')]);
        return JsonNode.Parse(payload)!["jti"]!.GetValue<string>();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method;

        if (method == HttpMethod.Post && path.EndsWith("/access/requests", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SubmitCalls);
            var submitted = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
            LastSubmittedDevice = submitted["device"]?.GetValue<string>();
            LastSubmittedPublicKey = submitted["devicePublicKey"]?.GetValue<string>();
            return Json(HttpStatusCode.Created, new { requestId = RequestId, pollToken = PollToken });
        }

        if (method == HttpMethod.Get && path.Contains("/access/requests/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref StatusCalls);
            return Status(request);
        }

        if (method == HttpMethod.Delete && path.Contains("/access/requests/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref AckCalls);
            GrantWasStoredWhenAcknowledged = OnAck?.Invoke();
            return Json((HttpStatusCode)AckStatusCode, new { ok = true });
        }

        if (method == HttpMethod.Post && path.EndsWith("/access/session", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SessionCalls);
            return Session(request);
        }

        return Json(HttpStatusCode.NotFound, new { error = "Not found." });
    }

    private Task<HttpResponseMessage> Status(HttpRequestMessage request)
    {
        var nonce = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["nonce"] ?? "";
        switch (Mode)
        {
            case StatusMode.Unreachable:
                throw new HttpRequestException("connection lost");
            case StatusMode.ServerError:
                return Json(HttpStatusCode.InternalServerError, new { error = "The AXE server hit a problem." });
            case StatusMode.NotFound:
                return Json(HttpStatusCode.NotFound, new { error = "This request is no longer active." });
        }

        var status = Mode switch
        {
            StatusMode.Approved => "approved",
            StatusMode.Rejected => "rejected",
            StatusMode.Expired => "expired",
            _ => "pending",
        };
        var token = Sign(new
        {
            typ = "axe-status",
            rid = RequestId,
            nonce,
            status,
            now = Now,
            grant = status == "approved" ? GrantForStatus : null,
        });
        return Json(HttpStatusCode.OK, new { token });
    }

    private Task<HttpResponseMessage> Session(HttpRequestMessage request)
    {
        if (SessionUnreachable)
        {
            throw new HttpRequestException("server unreachable");
        }

        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
        var grant = body["grant"]!.GetValue<string>();
        var nonce = body["nonce"]!.GetValue<string>();
        string jti;
        long exp;
        try
        {
            var claims = JsonNode.Parse(Base64Url.Decode(grant[..grant.IndexOf('.')]))!;
            jti = claims["jti"]!.GetValue<string>();
            exp = claims["exp"]!.GetValue<long>();
        }
        catch (Exception)
        {
            jti = string.Empty;
            exp = 0;
        }

        var proof = body["proof"]?.GetValue<string>();
        LastProof = proof;
        LastNonce = nonce;
        var deviceOk = !VerifyDeviceProofs || ProofIsValid(proof, nonce, jti);
        var valid = SessionValid && deviceOk;
        var token = Sign(new
        {
            typ = "axe-session",
            nonce,
            jti = SessionGrantIdOverride ?? jti,
            valid,
            reason = valid ? null : !deviceOk ? "device" : SessionReason ?? "expired",
            now = Now,
            exp,
        });
        return Json(HttpStatusCode.OK, new { token });
    }

    private bool ProofIsValid(string? proof, string nonce, string jti)
    {
        if (proof is null || DevicePublicKey is null)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(DevicePublicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes($"axe-device-proof|{nonce}|{jti}"), Base64Url.Decode(proof), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode code, object body) =>
        Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
}

/// <summary>A real <see cref="AccessController"/> wired to the fake backend, a temp store, and a controllable monotonic clock.</summary>
internal sealed class AccessHarness : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "axe-access-tests-" + Guid.NewGuid().ToString("N"));
    private long _monotonic = 1_000;

    /// <param name="directory">Reuse another harness's store directory to simulate restarting the app.</param>
    /// <param name="server">Reuse the same backend (same request, same signing key) across a simulated restart.</param>
    public AccessHarness(int pollMs = 20, string? directory = null, FakeAccessServer? server = null)
    {
        StoreDirectory = directory ?? _dir;
        Server = server ?? new FakeAccessServer();
        Store = new AccessStore(StoreDirectory);
        Server.DeviceHash = Store.DeviceHash();
        Server.DevicePublicKey = Store.DevicePublicKey();
        var config = AccessConfig.Parse(JsonSerializer.Serialize(new
        {
            functionsUrl = "https://axe.test/functions/v1",
            anonKey = "anon-test-key",
            signingPublicKey = Server.PublicKeySpki,
        }));
        Controller = new AccessController(new AccessClient(config, Server), Store, () => Interlocked.Read(ref _monotonic), TimeSpan.FromMilliseconds(pollMs));
        Controller.PhaseChanged += (_, _) => Phases.Add(Controller.Phase);
    }

    public string StoreDirectory { get; }
    public FakeAccessServer Server { get; }
    public AccessStore Store { get; }
    public AccessController Controller { get; }
    public List<AccessPhase> Phases { get; } = new();
    public string Device => Server.DeviceHash;

    /// <summary>Advances the monotonic tick counter (the only thing the trusted timer counts with).</summary>
    public void Advance(TimeSpan time) => Interlocked.Add(ref _monotonic, (long)time.TotalMilliseconds);

    public async Task WaitFor(AccessPhase phase, int timeoutMs = 8000) =>
        await WaitUntil(() => Controller.Phase == phase, timeoutMs, $"phase {phase} (was {Controller.Phase})");

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 8000, string what = "condition")
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Sends the PC through "submit an invitation request" so the controller is Pending.</summary>
    public async Task SubmitPendingAsync(string plan = "5h")
    {
        await Controller.StartAsync();
        await Controller.SubmitInvitationAsync("Test User", AccessPlan.Find(plan)!, "TESTCODE", CancellationToken.None);
    }

    public void Dispose()
    {
        Controller.Dispose();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup of a temp directory
        }
    }
}

/// <summary>Runs an async test on an STA thread with a WPF dispatcher (the controller uses a DispatcherTimer).</summary>
internal static class Sta
{
    public static void Run(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            async void Start()
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }

            dispatcher.BeginInvoke(new Action(Start));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("The access test did not finish in 60 seconds.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
