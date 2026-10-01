using System.Diagnostics;
using System.IO;
using System.Windows;
using AxeV2.Access;
using AxeV2.Services;
using WpfWindow = System.Windows.Window;

namespace AxeV2.Updates;

/// <summary>
/// Ties the updater to the app: a silent check shortly after start (never blocking startup), the prompt, the download, and the
/// hand-over to the installer followed by a graceful exit.
/// </summary>
internal static class UpdateCoordinator
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(4);

    public static void StartBackgroundCheck(WpfWindow owner)
    {
        var service = UpdateService.Create(AccessConfig.LoadEmbedded());
        if (service is null)
        {
            Log.Info("Updates: no update configuration in this build.");
            return;
        }

        _ = RunAsync(owner, service);
    }

    private static async Task RunAsync(WpfWindow owner, UpdateService service)
    {
        try
        {
            service.CleanUp();
            await Task.Delay(StartDelay).ConfigureAwait(true);
            var result = await service.CheckAsync(CancellationToken.None).ConfigureAwait(true);
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    Log.Info($"Updates: {result.Update!.Version.ToString(3)} is available (installed {service.Current.ToString(3)}).");
                    Prompt(owner, service, result.Update);
                    break;
                case UpdateCheckStatus.Rejected:
                    Log.Warn($"Updates: the update information was rejected ({result.Reason}).");
                    break;
                case UpdateCheckStatus.Unavailable:
                    Log.Info($"Updates: could not check ({result.Reason}).");
                    break;
                default:
                    Log.Info("Updates: AXE is up to date.");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Updates: the update check failed.", ex);
        }
    }

    private static void Prompt(WpfWindow owner, UpdateService service, UpdateInfo update)
    {
        var dialog = new UpdateDialog(owner, update);
        dialog.InstallRequested += async (progress, ct) =>
        {
            try
            {
                var path = await service.DownloadAsync(update, progress, ct).ConfigureAwait(true);
                Log.Info($"Updates: {update.Version.ToString(3)} downloaded and verified.");
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? throw new UpdateException("AXE could not find its own program file.");
                var perUser = IsPerUserInstall(exe);
                await service.InstallAsync(update, path, exe, perUser, ct).ConfigureAwait(true);
                Log.Info("Updates: installer started; closing AXE.");
                return true;
            }
            catch (UpdateException ex)
            {
                Log.Warn($"Updates: {ex.Message}");
                dialog.ShowFailure(ex.Message);
                return false;
            }
        };

        var accepted = dialog.ShowDialog() == true;
        if (accepted)
        {
            Application.Current.Shutdown(); // graceful: windows close normally, the installer then replaces the files
        }
        else if (update.Enforced)
        {
            Log.Info("Updates: a required update was declined; closing AXE.");
            Application.Current.Shutdown();
        }
    }

    /// <summary>A per-user install lives under the user's local application data; anything else was installed for all users.</summary>
    internal static bool IsPerUserInstall(string exePath)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(exePath).StartsWith(Path.GetFullPath(local) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
