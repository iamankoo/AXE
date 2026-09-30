using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AXE.Dialogs;
using AXE.Services;
using AXE.Window;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfWindow = System.Windows.Window;

namespace AXE.Browser;

public enum BrowserInitResult
{
    Ready,
    RuntimeMissing,
    Failed,
}

/// <summary>A download started from AXE (shown in AXE's own downloads menu).</summary>
public sealed class DownloadEntry
{
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public CoreWebView2DownloadState State { get; set; }
    public int? Percent { get; set; }
    internal CoreWebView2DownloadOperation? Operation { get; set; }
}

/// <summary>
/// Hosts the WebView2 (Chromium) browser.
/// <para>
/// Capture exclusion covers AXE's own top-level windows. Anything Chromium would show in a
/// separate browser-process window (context menu, JavaScript dialogs, permission prompts,
/// download bubble, new windows, DevTools, autofill) is either replaced with an AXE window or
/// kept inside AXE, so it inherits the same protection.
/// </para>
/// </summary>
public sealed class BrowserService : IDisposable
{
    private readonly Grid _host;
    private readonly WpfWindow _owner;
    private readonly CaptureExclusionService _capture;
    private readonly List<DownloadEntry> _downloads = new();
    private readonly Queue<DateTime> _renderCrashes = new();
    private BrowserSettings _settings;
    private WebView2? _webView;
    private CoreWebView2Environment? _environment;

    public BrowserService(Grid host, WpfWindow owner, CaptureExclusionService capture, BrowserSettings settings)
    {
        _host = host;
        _owner = owner;
        _capture = capture;
        _settings = settings;
    }

    public bool IsReady => _webView?.CoreWebView2 is not null;
    public bool CanGoBack => _webView?.CoreWebView2?.CanGoBack == true;
    public bool CanGoForward => _webView?.CoreWebView2?.CanGoForward == true;
    public bool IsLoading { get; private set; }
    public string CurrentUrl => _webView?.CoreWebView2?.Source ?? string.Empty;
    public IReadOnlyList<DownloadEntry> Downloads => _downloads;
    public CoreWebView2Profile? Profile => _webView?.CoreWebView2?.Profile;

    public event EventHandler? NavigationStateChanged;
    public event EventHandler<ImageSource?>? FaviconChanged;
    public event EventHandler<bool>? FullscreenChanged;
    public event EventHandler? DownloadsChanged;
    /// <summary>Raised when the browser engine is gone and the UI should offer a restart.</summary>
    public event EventHandler<string>? EngineFailed;

    public string UserDataFolder =>
        string.IsNullOrWhiteSpace(_settings.UserDataFolder) ? AppPaths.DefaultWebViewData : _settings.UserDataFolder!;

    public void UpdateSettings(BrowserSettings settings)
    {
        _settings = settings;
        ApplyCoreSettings();
    }

    /// <summary>Creates the WebView2 control and the Chromium environment.</summary>
    public async Task<BrowserInitResult> InitializeAsync(string? initialAddress)
    {
        DisposeWebView();

        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log.Info($"WebView2 runtime {version} found.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 runtime not found.");
            return BrowserInitResult.RuntimeMissing;
        }

        try
        {
            Directory.CreateDirectory(UserDataFolder);
            _environment ??= await CoreWebView2Environment.CreateAsync(null, UserDataFolder, new CoreWebView2EnvironmentOptions());

            _webView = new WebView2
            {
                // Same as Chromium: pages that don't set a background expect white.
                DefaultBackgroundColor = System.Drawing.Color.White,
            };
            _host.Children.Insert(0, _webView);
            await _webView.EnsureCoreWebView2Async(_environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 runtime not found during environment creation.");
            DisposeWebView();
            return BrowserInitResult.RuntimeMissing;
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 initialization failed.", ex);
            DisposeWebView();
            return BrowserInitResult.Failed;
        }

        WireEvents(_webView.CoreWebView2);
        ApplyCoreSettings();
        Log.Info("WebView2 initialized.");

        Navigate(initialAddress ?? _settings.StartPage, isStartup: true);
        return BrowserInitResult.Ready;
    }

    private void ApplyCoreSettings()
    {
        var core = _webView?.CoreWebView2;
        if (core is null)
        {
            return;
        }

        var s = core.Settings;
        s.IsScriptEnabled = true;
        s.IsWebMessageEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;   // replaced by AxeDialog
        s.AreDefaultContextMenusEnabled = true;     // rendered by AXE (ContextMenuRequested)
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = _settings.EnableDevTools;
        s.IsGeneralAutofillEnabled = _settings.EnableAutofill;
        s.IsPasswordAutosaveEnabled = false;
        s.IsZoomControlEnabled = true;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.IsBuiltInErrorPageEnabled = true;
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Navigates to a URL or runs a search. Returns false if input was empty/invalid.</summary>
    public bool Navigate(string? input, bool isStartup = false)
    {
        var core = _webView?.CoreWebView2;
        var resolution = AddressResolver.Resolve(input, _settings.SearchUrlTemplate);
        if (core is null || resolution is null)
        {
            return false;
        }

        try
        {
            core.Navigate(resolution.Value.Uri.AbsoluteUri);
            return true;
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Navigation rejected as invalid ({Log.Describe(ex)}).");
            if (!isStartup)
            {
                AxeDialog.ShowMessage(_owner, "Can't open that address", "The address isn't valid. Check it and try again.");
            }

            return false;
        }
    }

    public void GoHome() => Navigate(_settings.StartPage);

    public void GoBack()
    {
        if (CanGoBack)
        {
            _webView!.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            _webView!.CoreWebView2.GoForward();
        }
    }

    public void ReloadOrStop()
    {
        var core = _webView?.CoreWebView2;
        if (core is null)
        {
            return;
        }

        if (IsLoading)
        {
            core.Stop();
        }
        else
        {
            core.Reload();
        }
    }

    public void FocusPage() => _webView?.Focus();

    public void SetBrowserVisible(bool visible)
    {
        if (_webView is not null)
        {
            _webView.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        }
    }

    // ------------------------------------------------------------------ events

    private void WireEvents(CoreWebView2 core)
    {
        core.NavigationStarting += (_, _) =>
        {
            IsLoading = true;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        };
        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                // Only the error category is logged, never the URL.
                Log.Warn($"Navigation failed: {e.WebErrorStatus}.");
            }

            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        };
        core.SourceChanged += (_, _) => NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        core.HistoryChanged += (_, _) => NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        core.FaviconChanged += async (_, _) => FaviconChanged?.Invoke(this, await LoadFaviconAsync(core));
        core.ContainsFullScreenElementChanged += (_, _) => FullscreenChanged?.Invoke(this, core.ContainsFullScreenElement);

        // New windows (target=_blank, window.open) stay inside AXE so they remain excluded from capture.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                core.Navigate(uri.AbsoluteUri);
            }
        };

        core.ContextMenuRequested += OnContextMenuRequested;
        core.ScriptDialogOpening += OnScriptDialogOpening;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
    }

    private static async Task<ImageSource?> LoadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            if (string.IsNullOrEmpty(core.FaviconUri))
            {
                return null;
            }

            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null || stream.Length == 0)
            {
                return null;
            }

            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- context menu: rendered by AXE (WPF), protected before it is shown

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;

        var menu = new ContextMenu();
        ProtectWhenCreated(menu);
        Populate(menu, e.MenuItems, e);

        var completed = false;
        menu.Closed += (_, _) =>
        {
            if (!completed)
            {
                completed = true;
                deferral.Complete();
            }
        };

        if (menu.Items.Count == 0)
        {
            completed = true;
            deferral.Complete();
            return;
        }

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void Populate(ItemsControl target, IList<CoreWebView2ContextMenuItem> items, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        foreach (var item in items)
        {
            // Items that open Chromium-owned windows are dropped so nothing escapes AXE's window.
            if (item.Name is "inspectElement" or "openLinkInNewWindow" or "emoji" or "share" or "webCapture"
                && !(item.Name == "inspectElement" && _settings.EnableDevTools))
            {
                continue;
            }

            if (item.Kind == CoreWebView2ContextMenuItemKind.Separator)
            {
                if (target.Items.Count > 0 && target.Items[^1] is not Separator)
                {
                    target.Items.Add(new Separator());
                }

                continue;
            }

            var menuItem = new MenuItem
            {
                Header = item.Label.Replace("&&", "\u0001").Replace('&', '_').Replace("\u0001", "&"),
                InputGestureText = item.ShortcutKeyDescription,
                IsEnabled = item.IsEnabled,
                IsCheckable = item.Kind is CoreWebView2ContextMenuItemKind.CheckBox or CoreWebView2ContextMenuItemKind.Radio,
                IsChecked = item.IsChecked,
            };
            ProtectWhenCreated(menuItem);

            if (item.Kind == CoreWebView2ContextMenuItemKind.Submenu)
            {
                Populate(menuItem, item.Children, e);
                if (menuItem.Items.Count == 0)
                {
                    continue;
                }
            }
            else
            {
                var commandId = item.CommandId;
                menuItem.Click += (_, _) => e.SelectedCommandId = commandId;
            }

            target.Items.Add(menuItem);
        }

        if (target.Items.Count > 0 && target.Items[^1] is Separator trailing)
        {
            target.Items.Remove(trailing);
        }
    }

    /// <summary>Applies capture exclusion to a WPF popup's window as soon as it exists (before first show).</summary>
    internal void ProtectWhenCreated(FrameworkElement element)
    {
        PresentationSource.AddSourceChangedHandler(element, (_, args) =>
        {
            if (args.NewSource is HwndSource source)
            {
                _capture.ProtectAuxiliary(source.Handle);
            }
        });
    }

    // --- JavaScript dialogs (alert/confirm/prompt/beforeunload)

    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var deferral = e.GetDeferral();
        var host = SafeHost(e.Uri);
        _owner.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                switch (e.Kind)
                {
                    case CoreWebView2ScriptDialogKind.Alert:
                        AxeDialog.ShowMessage(_owner, host, e.Message);
                        e.Accept();
                        break;
                    case CoreWebView2ScriptDialogKind.Confirm:
                        if (AxeDialog.Confirm(_owner, host, e.Message))
                        {
                            e.Accept();
                        }

                        break;
                    case CoreWebView2ScriptDialogKind.Prompt:
                        var text = AxeDialog.Prompt(_owner, host, e.Message, e.DefaultText);
                        if (text is not null)
                        {
                            e.ResultText = text;
                            e.Accept();
                        }

                        break;
                    case CoreWebView2ScriptDialogKind.Beforeunload:
                        if (AxeDialog.Confirm(_owner, "Leave site?", "Changes you made may not be saved.", "Leave", "Stay"))
                        {
                            e.Accept();
                        }

                        break;
                }
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    // --- permission prompts (camera, microphone, location, notifications, ...)

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var host = SafeHost(e.Uri);
        var what = DescribePermission(e.PermissionKind);
        _owner.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var allow = AxeDialog.Confirm(_owner, "Permission request", $"{host} wants to {what}.", "Allow", "Block");
                e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    internal static string DescribePermission(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Notifications => "show notifications",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "download multiple files",
        CoreWebView2PermissionKind.FileReadWrite => "access files on your device",
        CoreWebView2PermissionKind.Autoplay => "autoplay media",
        CoreWebView2PermissionKind.LocalFonts => "use fonts installed on your device",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "control your MIDI devices",
        CoreWebView2PermissionKind.WindowManagement => "manage windows on your displays",
        _ => "use an additional browser permission",
    };

    private static string SafeHost(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host) ? parsed.Host : "This page";

    // --- downloads: Chromium's download bubble is replaced by AXE's downloads menu

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // suppress the default download dialog window
        var op = e.DownloadOperation;
        var entry = new DownloadEntry
        {
            FileName = Path.GetFileName(e.ResultFilePath),
            FilePath = e.ResultFilePath,
            State = op.State,
            Operation = op,
        };
        _downloads.Insert(0, entry);
        if (_downloads.Count > 10)
        {
            _downloads.RemoveAt(_downloads.Count - 1);
        }

        op.BytesReceivedChanged += (_, _) =>
        {
            var total = op.TotalBytesToReceive;
            entry.Percent = total is > 0 ? (int)((ulong)op.BytesReceived * 100 / total.Value) : null;
            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        };
        op.StateChanged += (_, _) =>
        {
            entry.State = op.State;
            if (op.State == CoreWebView2DownloadState.Completed)
            {
                entry.Percent = 100;
                Log.Info("Download completed.");
            }
            else if (op.State == CoreWebView2DownloadState.Interrupted)
            {
                Log.Warn($"Download interrupted: {op.InterruptReason}.");
            }

            DownloadsChanged?.Invoke(this, EventArgs.Empty);
        };
        Log.Info("Download started.");
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static void OpenDownload(DownloadEntry entry)
    {
        if (entry.State == CoreWebView2DownloadState.Completed && File.Exists(entry.FilePath))
        {
            Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true });
        }
    }

    public static void ShowDownloadInFolder(DownloadEntry entry)
    {
        if (File.Exists(entry.FilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FilePath}\"") { UseShellExecute = true });
        }
    }

    public static void CancelDownload(DownloadEntry entry)
    {
        if (entry.State == CoreWebView2DownloadState.InProgress)
        {
            entry.Operation?.Cancel();
        }
    }

    // --- crashes

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"Browser process failure: {e.ProcessFailedKind}.");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _environment = null; // a new environment is required after the browser process exits
                EngineFailed?.Invoke(this, "The browser engine stopped unexpectedly.");
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                _renderCrashes.Enqueue(DateTime.UtcNow);
                while (_renderCrashes.Count > 0 && _renderCrashes.Peek() < DateTime.UtcNow.AddMinutes(-1))
                {
                    _renderCrashes.Dequeue();
                }

                if (_renderCrashes.Count <= 3)
                {
                    _webView?.CoreWebView2?.Reload();
                }
                else
                {
                    EngineFailed?.Invoke(this, "This page keeps crashing.");
                }

                break;
        }
    }

    // ------------------------------------------------------------------ cleanup

    private void DisposeWebView()
    {
        if (_webView is null)
        {
            return;
        }

        _host.Children.Remove(_webView);
        try
        {
            _webView.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 dispose failed ({Log.Describe(ex)}).");
        }

        _webView = null;
    }

    /// <summary>Closes the Chromium session cleanly; the browser processes exit with it.</summary>
    public void Dispose()
    {
        DisposeWebView();
        _environment = null;
        Log.Info("Browser session closed.");
    }
}
