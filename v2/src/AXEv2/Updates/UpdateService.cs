using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AxeV2.Access;
using AxeV2.Services;

namespace AxeV2.Updates;

/// <summary>Starts the verified installer. Separate so the hand-off can be tested without running an installer.</summary>
public interface IInstallerLauncher
{
    /// <summary>Starts <paramref name="installerPath"/> detached; it replaces AXE and AXE is started again afterwards.</summary>
    void Launch(string installerPath, string relaunchExecutable, bool perUserInstall, string logPath);
}

public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    /// <summary>
    /// <c>cmd /c ""setup.exe" /SILENT ... &amp; start "" "AXE-v2.exe""</c>: the installer runs to completion (success OR failure)
    /// and AXE is then started again, so a failed installation never leaves the user without the app. The paths are produced by
    /// AXE itself (never from the manifest) and quoted.
    /// </summary>
    public static string BuildCommandLine(string installerPath, string relaunchExecutable, bool perUserInstall, string logPath)
    {
        foreach (var value in new[] { installerPath, relaunchExecutable, logPath })
        {
            if (value.Contains('"') || value.Contains('%') || value.Contains('\n') || value.Contains('\r'))
            {
                throw new UpdateException("An update path contains characters that are not allowed.");
            }
        }

        var scope = perUserInstall ? "/CURRENTUSER" : "/ALLUSERS";
        return $"/d /s /c \"\"{installerPath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART {scope} /LOG=\"{logPath}\" & start \"\" \"{relaunchExecutable}\"\"";
    }

    public void Launch(string installerPath, string relaunchExecutable, bool perUserInstall, string logPath)
    {
        var info = new ProcessStartInfo("cmd.exe", BuildCommandLine(installerPath, relaunchExecutable, perUserInstall, logPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        };
        Process.Start(info)?.Dispose();
    }
}

/// <summary>
/// Checks the signed manifest, downloads the installer and verifies it. Nothing is ever executed because a manifest says
/// so: a manifest must pass signature + schema + origin checks, and the downloaded file must match the signed SHA-256 and size.
/// </summary>
public sealed class UpdateService
{
    private const int MaxManifestBytes = 32 * 1024;
    private readonly Uri _manifestUri;
    private readonly SignedToken _verifier;
    private readonly Version _current;
    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly IInstallerLauncher _launcher;

    public UpdateService(Uri manifestUri, SignedToken verifier, Version current, HttpClient http, string updatesDirectory, IInstallerLauncher launcher)
    {
        _manifestUri = manifestUri;
        _verifier = verifier;
        _current = current;
        _http = http;
        _updatesDirectory = updatesDirectory;
        _launcher = launcher;
    }

    /// <summary>The HTTP client AXE uses: redirects are refused (a redirect could leave HTTPS or the trusted host).</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Builds the service from the embedded configuration, or null when this build has no (valid) update settings.</summary>
    public static UpdateService? Create(AccessConfig? config)
    {
        if (config?.UpdateManifestUrl is not { Length: > 0 } url || config.ReleasePublicKey is not { Length: > 0 } key)
        {
            return null;
        }

        try
        {
            var current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new UpdateService(new Uri(url), SignedToken.FromSpki(key), new Version(current.Major, current.Minor, Math.Max(current.Build, 0)),
                CreateHttpClient(), Path.Combine(AppPaths.Root, "updates"), new ProcessInstallerLauncher());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or UriFormatException)
        {
            Log.Error("The update configuration is unusable; updates are disabled.", ex);
            return null;
        }
    }

    public Version Current => _current;

    // ------------------------------------------------------------------------------------------------------ check

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        string text;
        try
        {
            using var response = await _http.GetAsync(_manifestUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null, $"the update server answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Rejected, null, "the manifest is oversized");
            }

            text = await ReadCappedAsync(response, MaxManifestBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null, "the update server could not be reached");
        }
        catch (InvalidDataException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Rejected, null, "the manifest is oversized");
        }

        UpdateManifest manifest;
        try
        {
            manifest = _verifier.Verify<UpdateManifest>(text.Trim(), UpdateManifest.Type, m => m.Typ ?? string.Empty);
        }
        catch (AccessException ex) when (ex.Kind == AccessErrorKind.Untrusted)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Rejected, null, "the manifest is unsigned, tampered or malformed");
        }

        return UpdateValidator.Validate(manifest, _current, _manifestUri);
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > limit)
        {
            throw new InvalidDataException("too large");
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    // -------------------------------------------------------------------------------------------------- download

    /// <summary>
    /// Downloads the installer into AXE's dedicated update directory and returns its path ONLY after the size and SHA-256 match
    /// the signed manifest. Any failure, interruption or cancellation removes the partial file.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_updatesDirectory);
        var finalPath = Path.Combine(_updatesDirectory, update.InstallerFileName);
        var partialPath = finalPath + ".partial";
        DeleteQuietly(partialPath);

        if (File.Exists(finalPath))
        {
            if (await HashMatchesAsync(finalPath, update, ct).ConfigureAwait(false))
            {
                progress?.Report(1);
                return finalPath;
            }

            DeleteQuietly(finalPath);
        }

        try
        {
            using var response = await _http.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException($"The update server answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is { } declared && declared != update.Size)
            {
                throw new UpdateException("The installer's size does not match the signed manifest.");
            }

            using var sha = SHA256.Create();
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > update.Size)
                    {
                        throw new UpdateException("The installer is larger than the signed manifest says.");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)received / update.Size);
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (received != update.Size)
            {
                throw new UpdateException("The download was interrupted before it finished.");
            }

            if (!string.Equals(Convert.ToHexString(sha.Hash!), update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("The installer does not match the signed checksum and was discarded.");
            }

            File.Move(partialPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(ct.IsCancellationRequested ? "The download was cancelled." : "The download failed. Check the connection and try again.", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new UpdateException(ct.IsCancellationRequested ? "The download was cancelled." : "The download timed out.", ex);
        }
        finally
        {
            DeleteQuietly(partialPath);
        }
    }

    /// <summary>Removes leftovers of earlier attempts (partial files, installers of other versions).</summary>
    public void CleanUp(Version? keep = null)
    {
        if (!Directory.Exists(_updatesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_updatesDirectory))
        {
            if (keep is not null && Path.GetFileName(file) == UpdateValidator.InstallerFileName(keep))
            {
                continue;
            }

            DeleteQuietly(file);
        }
    }

    // --------------------------------------------------------------------------------------------------- install

    /// <summary>
    /// Re-verifies the file on disk (it must still be exactly the signed installer), then hands over to the installer.
    /// The caller exits AXE right afterwards; the installer replaces the files and AXE is started again.
    /// </summary>
    public async Task InstallAsync(UpdateInfo update, string installerPath, string relaunchExecutable, bool perUserInstall, CancellationToken ct)
    {
        var expectedDirectory = Path.GetFullPath(_updatesDirectory);
        var full = Path.GetFullPath(installerPath);
        if (!string.Equals(Path.GetDirectoryName(full), expectedDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full) != update.InstallerFileName)
        {
            throw new UpdateException("The installer is not in AXE's update folder.");
        }

        if (!File.Exists(full) || !await HashMatchesAsync(full, update, ct).ConfigureAwait(false))
        {
            DeleteQuietly(full);
            throw new UpdateException("The downloaded installer changed or is missing and was discarded.");
        }

        _launcher.Launch(full, relaunchExecutable, perUserInstall, Path.Combine(_updatesDirectory, "install.log"));
    }

    private static async Task<bool> HashMatchesAsync(string path, UpdateInfo update, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != update.Size)
            {
                return false;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), update.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a stuck temp file is retried at the next start (CleanUp)
        }
    }
}
