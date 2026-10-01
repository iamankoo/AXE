using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxeV2.Access;
using AxeV2.Updates;
using Xunit;

namespace AxeV2.Tests;

/// <summary>Scripted HTTP layer for the updater: serves the manifest and the installer from memory.</summary>
internal sealed class FakeUpdateServer : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
    public List<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Respond!(request));
    }
}

/// <summary>A response body that fails part-way, like a dropped connection.</summary>
internal sealed class DroppingStream : Stream
{
    private readonly byte[] _data;
    private int _position;
    private readonly int _failAt;

    public DroppingStream(byte[] data, int failAt)
    {
        _data = data;
        _failAt = failAt;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _failAt)
        {
            throw new IOException("connection reset");
        }

        var n = Math.Min(Math.Min(count, _data.Length - _position), _failAt - _position);
        Array.Copy(_data, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class RecordingLauncher : IInstallerLauncher
{
    public List<(string Installer, string Relaunch, bool PerUser, string Log)> Calls { get; } = new();

    public void Launch(string installerPath, string relaunchExecutable, bool perUserInstall, string logPath) =>
        Calls.Add((installerPath, relaunchExecutable, perUserInstall, logPath));
}

/// <summary>Everything the updater test needs: a release key, a published installer, and a service wired to a scripted server.</summary>
internal sealed class UpdateHarness : IDisposable
{
    public const string ManifestUrl = "https://updates.axe.test/stable/latest.json";

    public ECDsa ReleaseKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Installer { get; } = RandomNumberGenerator.GetBytes(1_300_000);
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "axe-update-tests-" + Guid.NewGuid().ToString("N"));
    public FakeUpdateServer Server { get; } = new();
    public RecordingLauncher Launcher { get; } = new();
    public string ManifestText { get; set; } = string.Empty;
    public byte[] ServedInstaller { get; set; }

    public UpdateHarness(string current = "2.0.2")
    {
        ServedInstaller = Installer;
        Current = Version.Parse(current);
        ManifestText = Sign(ManifestJson());
        Server.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/stable/latest.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestText) },
            var path when path.EndsWith(".exe", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ServedInstaller) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        Service = Create(Server);
    }

    public Version Current { get; }
    public UpdateService Service { get; private set; }

    public UpdateService Create(HttpMessageHandler handler, ECDsa? verifierKey = null) =>
        new(new Uri(ManifestUrl), new SignedToken(verifierKey ?? ReleaseKey), Current, new HttpClient(handler), Directory, Launcher);

    public Dictionary<string, object?> ManifestJson(string version = "2.0.3", Action<Dictionary<string, object?>>? change = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["typ"] = "axe-update",
            ["product"] = "axe-v2",
            ["version"] = version,
            ["minimumSupportedVersion"] = "2.0.0",
            ["downloadUrl"] = $"https://updates.axe.test/releases/{version}/AXE-v2-Setup-{version}.exe",
            ["sha256"] = Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant(),
            ["size"] = (long)Installer.Length,
            ["releaseDate"] = "2026-10-01",
            ["releaseNotes"] = "Test release.",
            ["mandatory"] = false,
        };
        change?.Invoke(manifest);
        return manifest;
    }

    public string Sign(Dictionary<string, object?> manifest, ECDsa? key = null) => SignRaw(JsonSerializer.Serialize(manifest), key);

    public string SignRaw(string payloadJson, ECDsa? key = null)
    {
        var payload = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = (key ?? ReleaseKey).SignData(Encoding.ASCII.GetBytes(payload), HashAlgorithmName.SHA256);
        return payload + "." + Base64Url.Encode(signature);
    }

    public void Publish(Action<Dictionary<string, object?>>? change = null, string version = "2.0.3") =>
        ManifestText = Sign(ManifestJson(version, change));

    public async Task<UpdateCheckResult> CheckAsync() => await Service.CheckAsync(CancellationToken.None);

    public async Task<UpdateInfo> AvailableAsync()
    {
        var result = await CheckAsync();
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        return result.Update!;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class UpdateManifestTests
{
    [Fact]
    public async Task A_newer_signed_manifest_is_offered_with_its_details()
    {
        using var h = new UpdateHarness("2.0.2");
        h.Publish(m => { m["releaseNotes"] = "Notes here."; });
        var update = await h.AvailableAsync();
        Assert.Equal(new Version(2, 0, 3), update.Version);
        Assert.Equal(new Version(2, 0, 2), update.Current);
        Assert.Equal("Notes here.", update.ReleaseNotes);
        Assert.False(update.Enforced);
        Assert.Equal("AXE-v2-Setup-2.0.3.exe", update.InstallerFileName);
    }

    [Theory]
    [InlineData("2.0.2", "2.0.2")]
    [InlineData("2.0.2", "2.0.1")]
    [InlineData("2.1.0", "2.0.9")]
    [InlineData("10.0.0", "9.9.9")]
    public async Task The_same_or_an_older_version_is_never_offered_no_downgrade_and_no_replay(string installed, string published)
    {
        using var h = new UpdateHarness(installed);
        h.Publish(version: published);
        var result = await h.CheckAsync();
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task Versions_compare_numerically_not_as_text()
    {
        using var h = new UpdateHarness("2.0.9");
        h.Publish(version: "2.0.10");
        Assert.Equal(new Version(2, 0, 10), (await h.AvailableAsync()).Version);
    }

    [Fact]
    public async Task Mandatory_is_enforced_by_the_flag_or_by_a_minimum_supported_version_above_this_build()
    {
        using var flagged = new UpdateHarness("2.0.2");
        flagged.Publish(m => m["mandatory"] = true);
        Assert.True((await flagged.AvailableAsync()).Enforced);

        using var tooOld = new UpdateHarness("2.0.2");
        tooOld.Publish(m => m["minimumSupportedVersion"] = "2.0.3");
        var update = await tooOld.AvailableAsync();
        Assert.False(update.Mandatory);
        Assert.True(update.Enforced);

        using var supported = new UpdateHarness("2.0.2");
        supported.Publish();
        Assert.False((await supported.AvailableAsync()).Enforced);
    }

    private static object[] Case(string label, Action<Dictionary<string, object?>> change) => new object[] { label, change };

    public static IEnumerable<object[]> BadManifests() => new List<object[]>
    {
        Case("wrong product", m => m["product"] = "axe-v1"),
        Case("no product", m => m.Remove("product")),
        Case("wrong typ", m => m["typ"] = "axe-grant"),
        Case("no version", m => m.Remove("version")),
        Case("bad version", m => m["version"] = "2.0"),
        Case("version with suffix", m => m["version"] = "2.0.3-beta"),
        Case("no minimumSupportedVersion", m => m.Remove("minimumSupportedVersion")),
        Case("minimum above version", m => m["minimumSupportedVersion"] = "3.0.0"),
        Case("no mandatory flag", m => m.Remove("mandatory")),
        Case("no sha256", m => m.Remove("sha256")),
        Case("short sha256", m => m["sha256"] = "abcd"),
        Case("upper-case sha256", m => m["sha256"] = new string('A', 64)),
        Case("no size", m => m.Remove("size")),
        Case("tiny size", m => m["size"] = 10L),
        Case("huge size", m => m["size"] = 9_000_000_000L),
        Case("no releaseDate", m => m.Remove("releaseDate")),
        Case("bad releaseDate", m => m["releaseDate"] = "yesterday"),
        Case("no releaseNotes", m => m.Remove("releaseNotes")),
        Case("oversized notes", m => m["releaseNotes"] = new string('x', 5000)),
        Case("no downloadUrl", m => m.Remove("downloadUrl")),
        Case("http download", m => m["downloadUrl"] = "http://updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe"),
        Case("ftp download", m => m["downloadUrl"] = "ftp://updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe"),
        Case("another host", m => m["downloadUrl"] = "https://evil.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe"),
        Case("another port", m => m["downloadUrl"] = "https://updates.axe.test:8443/releases/2.0.3/AXE-v2-Setup-2.0.3.exe"),
        Case("credentials in url", m => m["downloadUrl"] = "https://user:pw@updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe"),
        Case("query string", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe?x=1"),
        Case("fragment", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.exe#x"),
        Case("path traversal", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/../../stable/AXE-v2-Setup-2.0.3.exe"),
        Case("encoded traversal", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/%2e%2e/AXE-v2-Setup-2.0.3.exe"),
        Case("another file name", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/payload.exe"),
        Case("another version in the path", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.1/AXE-v2-Setup-2.0.1.exe"),
        Case("not an exe", m => m["downloadUrl"] = "https://updates.axe.test/releases/2.0.3/AXE-v2-Setup-2.0.3.msi"),
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public async Task A_signed_but_invalid_manifest_is_rejected_and_nothing_is_downloaded(string label, Action<Dictionary<string, object?>> change)
    {
        using var h = new UpdateHarness("2.0.2");
        h.Publish(change);
        var result = await h.CheckAsync();
        Assert.True(result.Status == UpdateCheckStatus.Rejected, $"{label}: expected Rejected, got {result.Status} ({result.Reason})");
        Assert.Null(result.Update);
        Assert.DoesNotContain(h.Server.Requests, uri => uri.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_signed_by_the_wrong_key_is_rejected()
    {
        using var h = new UpdateHarness();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        h.ManifestText = h.Sign(h.ManifestJson(), attacker);
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
    }

    [Fact]
    public async Task A_tampered_payload_or_signature_is_rejected()
    {
        using var h = new UpdateHarness();
        var good = h.Sign(h.ManifestJson());
        var dot = good.IndexOf('.');
        var tamperedPayload = Base64Url.Encode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Base64Url.Decode(good[..dot])).Replace("2.0.3", "9.9.9"))) + good[dot..];
        h.ManifestText = tamperedPayload;
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);

        var sig = Base64Url.Decode(good[(dot + 1)..]);
        sig[10] ^= 0x01;
        h.ManifestText = good[..dot] + "." + Base64Url.Encode(sig);
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a manifest")]
    [InlineData("a.b.c")]
    [InlineData("{\"version\":\"9.9.9\"}")]
    [InlineData("<html>captive portal</html>")]
    public async Task Malformed_or_unsigned_manifest_text_is_rejected(string text)
    {
        using var h = new UpdateHarness();
        h.ManifestText = text;
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
    }

    [Fact]
    public async Task A_signed_message_that_is_not_json_or_is_empty_is_rejected()
    {
        using var h = new UpdateHarness();
        h.ManifestText = h.SignRaw("this is not json");
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
        h.ManifestText = h.SignRaw("null");
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
        h.ManifestText = h.SignRaw("{}");
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
    }

    [Fact]
    public async Task An_oversized_manifest_is_rejected_without_reading_it_all()
    {
        using var h = new UpdateHarness();
        h.ManifestText = new string('A', 200_000);
        Assert.Equal(UpdateCheckStatus.Rejected, (await h.CheckAsync()).Status);
    }

    [Fact]
    public async Task Server_errors_offline_and_redirects_are_Unavailable_not_trusted()
    {
        using var h = new UpdateHarness();
        h.Server.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal(UpdateCheckStatus.Unavailable, (await h.CheckAsync()).Status);

        h.Server.Respond = _ => throw new HttpRequestException("offline");
        Assert.Equal(UpdateCheckStatus.Unavailable, (await h.CheckAsync()).Status);

        h.Server.Respond = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://evil.test/latest.json") } };
        Assert.Equal(UpdateCheckStatus.Unavailable, (await h.CheckAsync()).Status);
    }

    [Fact]
    public void The_real_http_client_refuses_to_follow_redirects()
    {
        using var client = UpdateService.CreateHttpClient();
        var handlerField = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var handler = Assert.IsType<SocketsHttpHandler>(handlerField.GetValue(client));
        Assert.False(handler.AllowAutoRedirect);
    }
}

public class UpdateDownloadTests
{
    [Fact]
    public async Task A_matching_download_is_stored_verified_and_progress_is_reported()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var reports = new List<double>();
        var path = await h.Service.DownloadAsync(update, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(Path.Combine(h.Directory, "AXE-v2-Setup-2.0.3.exe"), path);
        Assert.Equal(h.Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count > 1);
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory, "*.partial"));
    }

    [Fact]
    public async Task A_modified_installer_with_an_unchanged_manifest_is_discarded()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var tampered = (byte[])h.Installer.Clone();
        tampered[500_000] ^= 0xFF; // same size, different content
        h.ServedInstaller = tampered;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }

    [Fact]
    public async Task A_wrong_sha256_in_the_signed_manifest_discards_the_download()
    {
        using var h = new UpdateHarness();
        h.Publish(m => m["sha256"] = new string('0', 64));
        var update = await h.AvailableAsync();
        await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }

    [Fact]
    public async Task A_size_mismatch_is_rejected_whether_larger_or_smaller()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();

        h.ServedInstaller = h.Installer.Concat(new byte[1000]).ToArray();
        await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));

        h.ServedInstaller = h.Installer.Take(h.Installer.Length - 1000).ToArray();
        await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }

    [Fact]
    public async Task An_interrupted_download_leaves_nothing_behind_and_can_be_retried()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var interrupt = true;
        h.Server.Respond = request => request.RequestUri!.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(interrupt ? new DroppingStream(h.Installer, 600_000) : new MemoryStream(h.Installer)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(h.ManifestText) };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
        Assert.Contains("failed", ex.Message);
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));

        interrupt = false; // the retry succeeds
        var path = await h.Service.DownloadAsync(update, null, CancellationToken.None);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_cancelled_download_removes_the_partial_file()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(value => { if (value > 0.2) cts.Cancel(); });
        var ex = await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, progress, cts.Token));
        Assert.Contains("cancelled", ex.Message);
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_200_download_is_a_failure(HttpStatusCode status)
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        h.Server.Respond = _ => new HttpResponseMessage(status);
        await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }

    [Fact]
    public async Task A_network_failure_is_a_clear_UpdateException()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        h.Server.Respond = _ => throw new HttpRequestException("offline");
        await Assert.ThrowsAsync<UpdateException>(() => h.Service.DownloadAsync(update, null, CancellationToken.None));
    }

    [Fact]
    public async Task Leftovers_are_cleaned_up_but_the_wanted_installer_is_kept()
    {
        using var h = new UpdateHarness();
        System.IO.Directory.CreateDirectory(h.Directory);
        File.WriteAllText(Path.Combine(h.Directory, "AXE-v2-Setup-2.0.3.exe.partial"), "x");
        File.WriteAllText(Path.Combine(h.Directory, "AXE-v2-Setup-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(h.Directory, "AXE-v2-Setup-2.0.3.exe"), "x");
        h.Service.CleanUp(new Version(2, 0, 3));
        Assert.Equal(new[] { "AXE-v2-Setup-2.0.3.exe" }, System.IO.Directory.GetFiles(h.Directory).Select(Path.GetFileName).ToArray());
        h.Service.CleanUp();
        Assert.Empty(System.IO.Directory.GetFiles(h.Directory));
    }
}

public class UpdateInstallTests
{
    private const string Exe = @"C:\Users\test\AppData\Local\Programs\AXE v2\AXE-v2.exe";

    [Fact]
    public async Task A_verified_installer_is_handed_to_the_launcher_with_the_relaunch_target()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var path = await h.Service.DownloadAsync(update, null, CancellationToken.None);

        await h.Service.InstallAsync(update, path, Exe, perUserInstall: true, CancellationToken.None);

        var call = Assert.Single(h.Launcher.Calls);
        Assert.Equal(path, call.Installer);
        Assert.Equal(Exe, call.Relaunch);
        Assert.True(call.PerUser);
        Assert.Equal(Path.Combine(h.Directory, "install.log"), call.Log);
    }

    [Fact]
    public async Task An_installer_changed_after_verification_is_refused_and_deleted_not_launched()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var path = await h.Service.DownloadAsync(update, null, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<UpdateException>(() => h.Service.InstallAsync(update, path, Exe, true, CancellationToken.None));
        Assert.Empty(h.Launcher.Calls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Only_the_expected_file_in_the_update_folder_is_ever_launched()
    {
        using var h = new UpdateHarness();
        var update = await h.AvailableAsync();
        var path = await h.Service.DownloadAsync(update, null, CancellationToken.None);

        var elsewhere = Path.Combine(Path.GetTempPath(), "AXE-v2-Setup-2.0.3.exe");
        File.Copy(path, elsewhere, overwrite: true);
        var renamed = Path.Combine(h.Directory, "other.exe");
        File.Copy(path, renamed);
        try
        {
            await Assert.ThrowsAsync<UpdateException>(() => h.Service.InstallAsync(update, elsewhere, Exe, true, CancellationToken.None));
            await Assert.ThrowsAsync<UpdateException>(() => h.Service.InstallAsync(update, renamed, Exe, true, CancellationToken.None));
            await Assert.ThrowsAsync<UpdateException>(() => h.Service.InstallAsync(update, Path.Combine(h.Directory, "..", "AXE-v2-Setup-2.0.3.exe"), Exe, true, CancellationToken.None));
            Assert.Empty(h.Launcher.Calls);
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public void The_hand_off_command_runs_the_installer_silently_and_then_starts_AXE_whatever_the_outcome()
    {
        var line = ProcessInstallerLauncher.BuildCommandLine(@"C:\u\AXE v2\updates\AXE-v2-Setup-2.0.3.exe", Exe, perUserInstall: true, @"C:\u\install.log");
        Assert.Equal(
            "/d /s /c \"\"C:\\u\\AXE v2\\updates\\AXE-v2-Setup-2.0.3.exe\" /SILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /LOG=\"C:\\u\\install.log\" & start \"\" \"" + Exe + "\"\"",
            line);
        Assert.Contains("/ALLUSERS", ProcessInstallerLauncher.BuildCommandLine(@"C:\u\s.exe", Exe, perUserInstall: false, @"C:\u\l.log"));
    }

    [Theory]
    [InlineData("C:\\u\\\"evil.exe")]
    [InlineData("C:\\u\\%PATH%.exe")]
    [InlineData("C:\\u\\a\r\nb.exe")]
    public void Paths_with_shell_metacharacters_are_refused(string path)
    {
        Assert.Throws<UpdateException>(() => ProcessInstallerLauncher.BuildCommandLine(path, Exe, true, @"C:\u\l.log"));
    }
}

public class UpdateConfigTests
{
    private static string Spki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static string Json(string? url, string? key) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["functionsUrl"] = "https://axe.test/functions/v1",
        ["anonKey"] = "anon",
        ["signingPublicKey"] = Spki(),
        ["updateManifestUrl"] = url,
        ["releasePublicKey"] = key,
    });

    [Fact]
    public void Update_settings_are_optional_but_must_come_as_a_pair()
    {
        Assert.NotNull(AccessConfig.Parse(Json(null, null)));
        Assert.NotNull(AccessConfig.Parse(Json("https://updates.axe.test/stable/latest.json", Spki())));
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json("https://updates.axe.test/stable/latest.json", null)));
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json(null, Spki())));
    }

    [Fact]
    public void The_manifest_url_must_be_https_and_the_key_a_P256_public_key()
    {
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json("http://updates.axe.test/stable/latest.json", Spki())));
        Assert.ThrowsAny<Exception>(() => AccessConfig.Parse(Json("https://updates.axe.test/stable/latest.json", "bm90IGEga2V5")));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<Exception>(() => AccessConfig.Parse(Json("https://updates.axe.test/stable/latest.json", Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()))));
    }

    [Fact]
    public void A_build_without_update_settings_has_no_updater()
    {
        var config = AccessConfig.Parse(Json(null, null));
        Assert.Null(UpdateService.Create(config));
        Assert.Null(UpdateService.Create(null));
    }

    [Fact]
    public void Per_user_installs_are_recognised_by_their_location()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.True(UpdateCoordinator_IsPerUser(Path.Combine(local, "Programs", "AXE v2", "AXE-v2.exe")));
        Assert.False(UpdateCoordinator_IsPerUser(@"C:\Program Files\AXE v2\AXE-v2.exe"));
        Assert.False(UpdateCoordinator_IsPerUser(local + "-evil" + @"\AXE-v2.exe"));
    }

    private static bool UpdateCoordinator_IsPerUser(string path) => AxeV2.Updates.UpdateCoordinator.IsPerUserInstall(path);
}

internal sealed class SyncProgress : IProgress<double>
{
    private readonly Action<double> _report;

    public SyncProgress(Action<double> report) => _report = report;

    public void Report(double value) => _report(value);
}
