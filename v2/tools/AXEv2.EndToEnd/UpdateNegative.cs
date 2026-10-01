using System.IO;
using AxeV2.Access;
using AxeV2.Updates;

namespace AxeV2.EndToEnd;

/// <summary>
/// Negative tests of the updater against the LIVE R2 endpoint: the app's real <see cref="UpdateService"/> (real HTTPS, the real
/// embedded release key from the production config) must reject every bad manifest and every bad installer. Fixtures are made
/// by <c>tools/release/make-negative-fixtures.mjs</c>. Prints one "E2E:" line per case; the exit code is 0 only if all pass.
/// </summary>
internal static class UpdateNegative
{
    private static readonly (string Name, UpdateCheckStatus Expected)[] ManifestCases =
    {
        ("bad-signature", UpdateCheckStatus.Rejected),
        ("tampered-payload", UpdateCheckStatus.Rejected),
        ("malformed", UpdateCheckStatus.Rejected),
        ("unsigned-json", UpdateCheckStatus.Rejected),
        ("http-url", UpdateCheckStatus.Rejected),
        ("wrong-product", UpdateCheckStatus.Rejected),
        ("downgrade", UpdateCheckStatus.UpToDate),
        ("missing-fields", UpdateCheckStatus.Rejected),
        ("missing-sha256", UpdateCheckStatus.Rejected),
        ("other-host", UpdateCheckStatus.Rejected),
        ("does-not-exist", UpdateCheckStatus.Unavailable),
    };

    private static readonly string[] DownloadCases = { "tampered-installer", "wrong-sha256", "truncated-installer", "oversized-installer" };

    public static async Task<int> RunAsync(string manifestUrl, string releasePublicKey, string current)
    {
        var baseUri = new Uri(manifestUrl);
        var root = $"{baseUri.Scheme}://{baseUri.Host}";
        var updates = Path.Combine(Path.GetTempPath(), "axe-e2e-update-" + Guid.NewGuid().ToString("N"));
        var launcher = new NeverLauncher();
        var failures = 0;

        UpdateService Service(string name) => new(new Uri($"{root}/test/{name}.json"), SignedToken.FromSpki(releasePublicKey),
            Version.Parse(current), UpdateService.CreateHttpClient(), updates, launcher);

        foreach (var (name, expected) in ManifestCases)
        {
            var result = await Service(name).CheckAsync(CancellationToken.None);
            var ok = result.Status == expected && result.Update is null;
            failures += ok ? 0 : 1;
            Say($"{(ok ? "PASS" : "FAIL")} manifest '{name}': {result.Status} ({result.Reason}); expected {expected}");
        }

        foreach (var name in DownloadCases)
        {
            var service = Service(name);
            var check = await service.CheckAsync(CancellationToken.None);
            if (check.Status != UpdateCheckStatus.Available)
            {
                failures++;
                Say($"FAIL download '{name}': the signed manifest should be accepted but was {check.Status} ({check.Reason})");
                continue;
            }

            string outcome;
            var rejected = false;
            try
            {
                await service.DownloadAsync(check.Update!, null, CancellationToken.None);
                outcome = "ACCEPTED a bad installer";
            }
            catch (UpdateException ex)
            {
                rejected = true;
                outcome = ex.Message;
            }

            var leftovers = Directory.Exists(updates) ? Directory.GetFiles(updates).Length : 0;
            var ok = rejected && leftovers == 0;
            failures += ok ? 0 : 1;
            Say($"{(ok ? "PASS" : "FAIL")} installer '{name}': {outcome} (files left behind: {leftovers})");
        }

        Say(launcher.Launched ? "FAIL an installer was launched during the negative tests" : "PASS no installer was ever launched");
        failures += launcher.Launched ? 1 : 0;
        try
        {
            Directory.Delete(updates, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Say(failures == 0 ? "ALL NEGATIVE TESTS PASSED" : $"{failures} NEGATIVE TEST(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Say(string message) => Console.WriteLine("E2E: " + message);

    private sealed class NeverLauncher : IInstallerLauncher
    {
        public bool Launched { get; private set; }

        public void Launch(string installerPath, string relaunchExecutable, bool perUserInstall, string logPath) => Launched = true;
    }
}
