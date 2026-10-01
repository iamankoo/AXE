// Manual end-to-end harness for the Windows authorization lifecycle.
//
//   dotnet run --project v2/tools/AXEv2.EndToEnd -- approve [plan] [timeoutSeconds]
//   dotnet run --project v2/tools/AXEv2.EndToEnd -- reject  [plan] [timeoutSeconds]
//
// It runs the REAL AccessController + AccessClient (real ECDSA verification with the public key from
// v2/config/server.json, real HTTP) against the LOCAL backend, submits an invitation request with synthetic data, and
// then waits for the ADMIN to decide (on the Android app, or any other admin client). It prints what the Windows client
// does. The expiry step at the end uses the controller's injectable monotonic clock (a real plan lasts >= 1 hour);
// every signature and server time in it is real.
//
// Output lines start with "E2E:" so other tooling can follow them.

using System.IO;
using System.Windows.Threading;
using AxeV2.Access;

namespace AxeV2.EndToEnd;

internal static class Program
{
    private static long _monotonic = 1_000;

    [STAThread]
    private static int Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : "approve";
        var plan = args.Length > 1 ? args[1] : "1h";
        var timeout = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 240);
        var exit = 1;

        if (scenario == "update-negative")
        {
            var config = AccessConfig.Parse(File.ReadAllText(FindServerJson()));
            return UpdateNegative.RunAsync(config.UpdateManifestUrl!, config.ReleasePublicKey!, args.Length > 1 ? args[1] : "2.0.2").GetAwaiter().GetResult();
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                exit = scenario == "reject" ? await RejectAsync(plan, timeout) : await ApproveAsync(plan, timeout);
            }
            catch (Exception ex)
            {
                Say($"FAILED with {ex.GetType().Name}: {ex.Message}");
                exit = 1;
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        }));
        Dispatcher.Run();
        return exit;
    }

    private static async Task<int> ApproveAsync(string planId, TimeSpan timeout)
    {
        var plan = AccessPlan.Find(planId) ?? throw new ArgumentException("unknown plan");
        var dir = NewStoreDirectory();
        var phases = new List<string>();
        using var controller = Create(dir, phases);

        await controller.StartAsync();
        Say($"start: phase={controller.Phase}");

        await controller.SubmitInvitationAsync("E2E Test User", plan, InviteCode(), CancellationToken.None);
        var requestId = new AccessStore(dir).Load().PendingRequestId;
        Say($"SUBMITTED request={requestId} plan={plan.Id} phase={controller.Phase} browsing={controller.Phase.AllowsBrowsing()}");

        if (!await WaitForDecisionAsync(controller, timeout))
        {
            Say("FAILED: no decision before the timeout");
            return 1;
        }

        if (controller.Phase != AccessPhase.Active)
        {
            Say($"FAILED: expected Active, got {controller.Phase} ({controller.Message})");
            return 1;
        }

        var remaining = controller.Remaining;
        Say($"APPROVED: phase=Active browsing={controller.Phase.AllowsBrowsing()} plan={controller.ActivePlan?.Id} remaining={remaining:h\\:mm\\:ss}");
        var saved = new AccessStore(dir).Load();
        Say($"stored: grant={(saved.Grant is null ? "missing" : "present")} pendingId={(saved.PendingRequestId is null ? "cleared" : "STILL SET")} pendingToken={(saved.PendingPollToken is null ? "cleared" : "STILL SET")}");
        var slack = TimeSpan.FromMinutes(2);
        Say(remaining <= plan.Hours * TimeSpan.FromHours(1) && remaining > plan.Hours * TimeSpan.FromHours(1) - slack
            ? "server duration: OK (the full plan from the server's approval time)"
            : "server duration: UNEXPECTED");

        await Task.Delay(TimeSpan.FromSeconds(4)); // let the acknowledgement reach the server
        Say("ack: sent after the grant was stored");

        // Restart the app: same PC, same stored grant. The real server validates it and supplies the time.
        controller.Dispose();
        using var restarted = Create(dir, phases);
        await restarted.StartAsync();
        Say($"RESTART: phase={restarted.Phase} browsing={restarted.Phase.AllowsBrowsing()} remaining={restarted.Remaining:h\\:mm\\:ss} (continues, is not reset)");

        // Expiry (clock advanced; signatures/server time are real).
        Interlocked.Add(ref _monotonic, (long)(plan.Hours * 3_600_000L + 5_000));
        restarted.OnTick();
        await Task.Delay(500);
        Say($"EXPIRED: phase={restarted.Phase} browsing={restarted.Phase.AllowsBrowsing()} grantStored={new AccessStore(dir).Load().Grant is not null} message=\"{restarted.Message}\"");
        if (restarted.Phase != AccessPhase.NeedsAccess)
        {
            Cleanup(dir);
            return 1;
        }

        // Optional: after expiry a NEW request is required, and it works like the first one (AXE_E2E_REREQUEST=1).
        if (Environment.GetEnvironmentVariable("AXE_E2E_REREQUEST") == "1")
        {
            await restarted.SubmitInvitationAsync("E2E Test Again", plan, InviteCode(), CancellationToken.None);
            var again = new AccessStore(dir).Load().PendingRequestId;
            Say($"RE-REQUEST submitted request={again} phase={restarted.Phase} browsing={restarted.Phase.AllowsBrowsing()}");
            if (!await WaitForDecisionAsync(restarted, timeout))
            {
                Say("FAILED: no decision on the second request before the timeout");
                return 1;
            }

            Say($"RE-APPROVED: phase={restarted.Phase} browsing={restarted.Phase.AllowsBrowsing()} remaining={restarted.Remaining:h\\:mm\\:ss}");
            Say("phases seen: " + string.Join(" > ", phases));
            Cleanup(dir);
            return restarted.Phase == AccessPhase.Active ? 0 : 1;
        }

        Say("phases seen: " + string.Join(" > ", phases));
        Cleanup(dir);
        return 0;
    }

    private static async Task<int> RejectAsync(string planId, TimeSpan timeout)
    {
        var plan = AccessPlan.Find(planId) ?? throw new ArgumentException("unknown plan");
        var dir = NewStoreDirectory();
        var phases = new List<string>();
        using var controller = Create(dir, phases);

        await controller.StartAsync();
        await controller.SubmitInvitationAsync("E2E Test Reject", plan, InviteCode(), CancellationToken.None);
        var requestId = new AccessStore(dir).Load().PendingRequestId;
        Say($"SUBMITTED request={requestId} plan={plan.Id} phase={controller.Phase} browsing={controller.Phase.AllowsBrowsing()}");

        if (!await WaitForDecisionAsync(controller, timeout))
        {
            Say("FAILED: no decision before the timeout");
            return 1;
        }

        var saved = new AccessStore(dir).Load();
        Say($"REJECTED: phase={controller.Phase} browsing={controller.Phase.AllowsBrowsing()} grant={(saved.Grant is null ? "none" : "PRESENT")} pending={(saved.PendingRequestId is null ? "cleared" : "STILL SET")} message=\"{controller.Message}\"");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Say("phases seen: " + string.Join(" > ", phases));
        Cleanup(dir);
        return controller.Phase == AccessPhase.NeedsAccess && saved.Grant is null ? 0 : 1;
    }

    private static async Task<bool> WaitForDecisionAsync(AccessController controller, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (controller.Phase == AccessPhase.Pending)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(250);
        }

        return true;
    }

    private static AccessController Create(string dir, List<string> phases)
    {
        var json = File.ReadAllText(FindServerJson());
        var config = AccessConfig.Parse(json);
        var controller = new AccessController(new AccessClient(config), new AccessStore(dir),
            () => Interlocked.Read(ref _monotonic) + Environment.TickCount64, TimeSpan.FromSeconds(1));
        controller.PhaseChanged += (_, _) => phases.Add(controller.Phase.ToString());
        return controller;
    }

    /// <summary>The LOCAL backend's invitation code: AXE_E2E_INVITE_CODE, else the first code in the dev backend's .env.</summary>
    private static string InviteCode()
    {
        var fromEnv = Environment.GetEnvironmentVariable("AXE_E2E_INVITE_CODE");
        var codeFile = Environment.GetEnvironmentVariable("AXE_E2E_INVITE_CODE_FILE");
        if (string.IsNullOrWhiteSpace(fromEnv) && !string.IsNullOrWhiteSpace(codeFile) && File.Exists(codeFile))
        {
            fromEnv = File.ReadAllText(codeFile);
        }

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        var json = FindServerJson();
        var dev = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(json))!, "backend", "supabase", "functions", ".env");
        foreach (var line in File.Exists(dev) ? File.ReadAllLines(dev) : Array.Empty<string>())
        {
            if (line.StartsWith("AXE_INVITE_CODES=", StringComparison.Ordinal))
            {
                return line["AXE_INVITE_CODES=".Length..].Split(',')[0].Trim();
            }
        }

        throw new InvalidOperationException("Set AXE_E2E_INVITE_CODE (an invitation code of the local backend).");
    }

    private static string FindServerJson()
    {
        var overridePath = Environment.GetEnvironmentVariable("AXE_E2E_SERVER_JSON");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : throw new FileNotFoundException("AXE_E2E_SERVER_JSON points to a missing file.");
        }

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "config", "server.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(d.FullName, "v2", "config", "server.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("v2/config/server.json not found (run v2/backend/scripts/setup-local.mjs).");
    }

    private static string NewStoreDirectory() => Path.Combine(Path.GetTempPath(), "axe-e2e-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // temp files only
        }
    }

    private static void Say(string message) => Console.WriteLine("E2E: " + message);
}
