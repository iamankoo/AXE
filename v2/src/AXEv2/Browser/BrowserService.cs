using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxeV2.Dialogs;
using AxeV2.Services;
using AxeV2.Window;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfWindow = System.Windows.Window;

namespace AxeV2.Browser;

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
/// Hosts the WebView2 (Chromium) browser tabs.
/// <para>
/// Every tab is a WebView2 child of the main AXE window, so capture exclusion covers all of
/// them. Anything Chromium would show in a separate browser-process window (context menu,
/// JavaScript dialogs, permission prompts, download bubble, new windows, DevTools, autofill)
/// is either replaced with an AXE window or kept inside AXE as a tab, so it inherits the
/// same protection.
/// </para>
/// </summary>
public sealed class BrowserService : IDisposable
{
    /// <summary>Upper bound on open tabs (each tab runs its own renderer process).</summary>
    public const int MaxTabs = 20;

    private const int MaxRecentlyClosed = 10;

    private readonly Grid _host;
    private readonly WpfWindow _owner;
    private readonly CaptureExclusionService _capture;
    private readonly List<DownloadEntry> _downloads = new();
    private readonly List<string> _recentlyClosed = new();
    private BrowserSettings _settings;
    private CoreWebView2Environment? _environment;
    private bool _browserVisible = true;
    private bool _disposed;

    public BrowserService(Grid host, WpfWindow owner, CaptureExclusionService capture, BrowserSettings settings)
    {
        _host = host;
        _owner = owner;
        _capture = capture;
        _settings = settings;
    }

    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    public BrowserTab? ActiveTab { get; private set; }

    public bool IsReady => ActiveTab?.Core is not null;
    public bool CanGoBack => ActiveTab?.CanGoBack == true;
    public bool CanGoForward => ActiveTab?.CanGoForward == true;
    public bool IsLoading => ActiveTab?.IsLoading == true;
    public string CurrentUrl => ActiveTab?.Url ?? string.Empty;
    public IReadOnlyList<DownloadEntry> Downloads => _downloads;
    public CoreWebView2Profile? Profile => Tabs.FirstOrDefault(t => t.Core is not null)?.Core?.Profile;

    /// <summary>Navigation state of the active tab changed (or a different tab became active).</summary>
    public event EventHandler? NavigationStateChanged;
    public event EventHandler<ImageSource?>? FaviconChanged;
    public event EventHandler<bool>? FullscreenChanged;
    public event EventHandler? DownloadsChanged;
    /// <summary>A tab was opened, closed or activated.</summary>
    public event EventHandler? ActiveTabChanged;
    /// <summary>Raised when the browser engine is gone and the UI should offer a restart.</summary>
    public event EventHandler<string>? EngineFailed;

    public string UserDataFolder =>
        string.IsNullOrWhiteSpace(_settings.UserDataFolder) ? AppPaths.DefaultWebViewData : _settings.UserDataFolder!;

    public void UpdateSettings(BrowserSettings settings)
    {
        _settings = settings;
        foreach (var tab in Tabs)
        {
            ApplyCoreSettings(tab.Core);
        }
    }

    /// <summary>
    /// Creates the Chromium environment and the first tab(s).
    /// <paramref name="restoreUrls"/> reopens tabs after an engine restart.
    /// </summary>
    public async Task<BrowserInitResult> InitializeAsync(string? initialAddress, IReadOnlyList<string>? restoreUrls = null)
    {
        CloseAllTabs();
        _disposed = false;

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

            var addresses = restoreUrls is { Count: > 0 }
                ? restoreUrls.Take(MaxTabs).ToList()
                : new List<string> { initialAddress ?? _settings.StartPage };
            foreach (var address in addresses)
            {
                var tab = await CreateTabAsync();
                if (tab is null)
                {
                    throw new InvalidOperationException("Tab creation failed.");
                }

                NavigateTab(tab, address, isStartup: true);
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 runtime not found during environment creation.");
            CloseAllTabs();
            return BrowserInitResult.RuntimeMissing;
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 initialization failed.", ex);
            CloseAllTabs();
            return BrowserInitResult.Failed;
        }

        Activate(Tabs[0]);
        Log.Info($"WebView2 initialized with {Tabs.Count} tab(s).");
        return BrowserInitResult.Ready;
    }

    /// <summary>
    /// Creates a tab with an initialized (but not yet navigated) CoreWebView2.
    /// With <paramref name="activate"/> the tab is shown and selected at once, before
    /// Chromium finishes creating it, so the click that opened it gets an immediate response.
    /// </summary>
    private async Task<BrowserTab?> CreateTabAsync(int? insertAt = null, bool activate = false)
    {
        if (_environment is null || Tabs.Count >= MaxTabs)
        {
            return null;
        }

        var view = new WebView2
        {
            // Same as Chromium: pages that don't set a background expect white.
            DefaultBackgroundColor = System.Drawing.Color.White,
            Visibility = Visibility.Hidden,
        };
        var tab = new BrowserTab(view);
        _host.Children.Insert(0, view); // below the message panel
        Tabs.Insert(Math.Clamp(insertAt ?? Tabs.Count, 0, Tabs.Count), tab);
        if (activate)
        {
            Activate(tab);
        }

        try
        {
            await view.EnsureCoreWebView2Async(_environment);
            if (_disposed || !Tabs.Contains(tab))
            {
                DiscardTab(tab); // closed (or AXE locked) while Chromium was still creating it
                return null;
            }

            WireEvents(tab);
            ApplyCoreSettings(tab.Core);
            await InstallDocumentScriptsAsync(tab.Core!);
        }
        catch when (_disposed || !Tabs.Contains(tab))
        {
            DiscardTab(tab);
            return null;
        }
        catch
        {
            DiscardTab(tab);
            throw;
        }

        return tab;
    }

    /// <summary>Scripts added once per document (never per interaction). Must run before the tab's first navigation.</summary>
    private static async Task InstallDocumentScriptsAsync(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CursorLock.DocumentScript);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cursor lock script could not be installed ({Log.Describe(ex)}).");
        }
    }

    /// <summary>Removes a tab that never finished opening, without replacing it or recording it as closed.</summary>
    private void DiscardTab(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        RemoveTab(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = null;
            if (Tabs.Count > 0)
            {
                Activate(Tabs[TabOrder.IndexAfterClose(Math.Max(index, 0), Tabs.Count)]);
                return;
            }
        }

        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCoreSettings(CoreWebView2? core)
    {
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

    // ------------------------------------------------------------------ tabs

    /// <summary>Opens a new tab (start page when <paramref name="address"/> is null).</summary>
    public async Task<BrowserTab?> NewTabAsync(string? address = null, bool activate = true, BrowserTab? after = null)
    {
        if (Tabs.Count >= MaxTabs)
        {
            AxeDialog.ShowMessage(_owner, "Too many tabs", $"AXE can keep up to {MaxTabs} tabs open. Close a tab to open another.");
            return null;
        }

        BrowserTab? tab;
        try
        {
            tab = await CreateTabAsync(after is null ? null : Tabs.IndexOf(after) + 1, activate);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a new tab.", ex);
            return null;
        }

        if (tab is null)
        {
            return null;
        }

        NavigateTab(tab, address ?? _settings.StartPage, isStartup: true);
        if (!activate)
        {
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (tab == ActiveTab)
        {
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        }

        Log.Info($"Tab opened ({Tabs.Count} open).");
        return tab;
    }

    public void Activate(BrowserTab tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        var previous = ActiveTab;
        if (previous is not null && previous != tab)
        {
            previous.IsActive = false;
            previous.View.Visibility = Visibility.Hidden;
            if (previous.IsFullscreen)
            {
                FullscreenChanged?.Invoke(this, false);
            }
        }

        ActiveTab = tab;
        tab.IsActive = true;
        tab.View.Visibility = _browserVisible ? Visibility.Visible : Visibility.Hidden;

        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        FaviconChanged?.Invoke(this, tab.Favicon);
    }

    /// <summary>Closes a tab. Closing the last tab leaves a fresh start-page tab, so AXE never ends up empty.</summary>
    public void CloseTab(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var url = tab.Url;
        if (!string.IsNullOrEmpty(MainWindowUrl.Display(url)))
        {
            // Kept in memory only, for Ctrl+Shift+T; never written to disk or logged.
            _recentlyClosed.Add(url);
            if (_recentlyClosed.Count > MaxRecentlyClosed)
            {
                _recentlyClosed.RemoveAt(0);
            }
        }

        var wasActive = tab == ActiveTab;
        if (wasActive && tab.IsFullscreen)
        {
            FullscreenChanged?.Invoke(this, false);
        }

        RemoveTab(tab);
        Log.Info($"Tab closed ({Tabs.Count} open).");

        if (Tabs.Count == 0)
        {
            ActiveTab = null;
            _ = NewTabAsync();
            return;
        }

        if (wasActive)
        {
            Activate(Tabs[TabOrder.IndexAfterClose(index, Tabs.Count)]);
        }
        else
        {
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CloseActiveTab()
    {
        if (ActiveTab is not null)
        {
            CloseTab(ActiveTab);
        }
    }

    /// <summary>Reopens the most recently closed tab (Ctrl+Shift+T).</summary>
    public void ReopenClosedTab()
    {
        if (_recentlyClosed.Count > 0)
        {
            var url = _recentlyClosed[^1];
            _recentlyClosed.RemoveAt(_recentlyClosed.Count - 1);
            _ = NewTabAsync(url);
        }
    }

    /// <summary>Moves to the next (+1) or previous (-1) tab, wrapping around.</summary>
    public void SelectRelative(int delta)
    {
        if (ActiveTab is null || Tabs.Count < 2)
        {
            return;
        }

        Activate(Tabs[TabOrder.Cycle(Tabs.IndexOf(ActiveTab), delta, Tabs.Count)]);
    }

    /// <summary>Ctrl+1..8 select that tab; Ctrl+9 selects the last tab (as in Chrome).</summary>
    public void SelectByNumber(int number)
    {
        var index = TabOrder.IndexForNumber(number, Tabs.Count);
        if (index is { } i)
        {
            Activate(Tabs[i]);
        }
    }

    private void RemoveTab(BrowserTab tab)
    {
        Tabs.Remove(tab);
        _host.Children.Remove(tab.View);
        try
        {
            tab.View.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 dispose failed ({Log.Describe(ex)}).");
        }
    }

    private void CloseAllTabs()
    {
        foreach (var tab in Tabs.ToList())
        {
            RemoveTab(tab);
        }

        ActiveTab = null;
    }

    /// <summary>Addresses of the open tabs, used to reopen them after an engine restart.</summary>
    public IReadOnlyList<string> OpenTabUrls() =>
        Tabs.Select(t => t.Url).Where(u => !string.IsNullOrEmpty(MainWindowUrl.Display(u))).ToList();

    // ------------------------------------------------------------------ navigation (active tab)

    /// <summary>Navigates the active tab to a URL or runs a search. Returns false if input was empty/invalid.</summary>
    public bool Navigate(string? input) => ActiveTab is not null && NavigateTab(ActiveTab, input, isStartup: false);

    private bool NavigateTab(BrowserTab tab, string? input, bool isStartup)
    {
        var core = tab.Core;
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
            ActiveTab!.Core!.GoBack();
        }
    }

    public void GoForward()
    {
        if (CanGoForward)
        {
            ActiveTab!.Core!.GoForward();
        }
    }

    public void ReloadOrStop()
    {
        var core = ActiveTab?.Core;
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

    public void FocusPage() => ActiveTab?.View.Focus();

    /// <summary>Hides every tab (e.g. while a message or the access screen covers the browser area).</summary>
    public void SetBrowserVisible(bool visible)
    {
        _browserVisible = visible;
        foreach (var tab in Tabs)
        {
            tab.View.Visibility = visible && tab == ActiveTab ? Visibility.Visible : Visibility.Hidden;
        }
    }

    // ------------------------------------------------------------------ events

    private void WireEvents(BrowserTab tab)
    {
        var core = tab.Core!;

        void RaiseIfActive()
        {
            if (tab == ActiveTab)
            {
                NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        core.NavigationStarting += (_, _) =>
        {
            tab.IsLoading = true;
            RaiseIfActive();
        };
        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                // Only the error category is logged, never the URL.
                Log.Warn($"Navigation failed: {e.WebErrorStatus}.");
            }

            RaiseIfActive();
        };
        core.SourceChanged += (_, _) => RaiseIfActive();
        core.HistoryChanged += (_, _) => RaiseIfActive();
        core.DocumentTitleChanged += (_, _) => tab.Title = core.DocumentTitle;
        core.FaviconChanged += async (_, _) =>
        {
            tab.Favicon = await LoadFaviconAsync(core);
            if (tab == ActiveTab)
            {
                FaviconChanged?.Invoke(this, tab.Favicon);
            }
        };
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == ActiveTab)
            {
                FullscreenChanged?.Invoke(this, core.ContainsFullScreenElement);
            }
        };

        // New windows (target=_blank, window.open) open as AXE tabs so they stay inside the
        // protected window. The new tab becomes the page's real popup (window.opener works),
        // which keeps sign-in popups working.
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(tab, e);
        core.WindowCloseRequested += (_, _) => _owner.Dispatcher.InvokeAsync(() => CloseTab(tab));

        core.ContextMenuRequested += (_, e) => OnContextMenuRequested(tab, e);
        core.ScriptDialogOpening += (_, e) => OnScriptDialogOpening(tab, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(tab, e);
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
    }

    private async void OnNewWindowRequested(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!IsAllowedPopupUri(e.Uri))
        {
            return;
        }

        if (Tabs.Count >= MaxTabs)
        {
            // No room for another tab: keep the old single-view behaviour and open it in place.
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme != "about")
            {
                opener.Core?.Navigate(uri.AbsoluteUri);
            }

            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var tab = await CreateTabAsync(Tabs.IndexOf(opener) + 1, activate: true);
            if (tab?.Core is not null)
            {
                e.NewWindow = tab.Core;
                Log.Info($"Popup opened as a tab ({Tabs.Count} open).");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a popup as a tab.", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal static bool IsAllowedPopupUri(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps
            || string.Equals(parsed.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase));

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

    private void OnContextMenuRequested(BrowserTab tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;

        var menu = new ContextMenu();
        ProtectWhenCreated(menu);

        // WebView2 has no "open in new tab" item of its own; AXE adds one for links.
        var target = e.ContextMenuTarget;
        if (target.HasLinkUri && IsAllowedPopupUri(target.LinkUri) && !target.LinkUri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            var link = target.LinkUri;
            var newTab = new MenuItem { Header = "Open link in new tab" };
            ProtectWhenCreated(newTab);
            newTab.Click += (_, _) => _ = NewTabAsync(link, activate: false, after: tab);
            menu.Items.Add(newTab);
            menu.Items.Add(new Separator());
        }

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

    private void OnScriptDialogOpening(BrowserTab tab, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var deferral = e.GetDeferral();
        var host = SafeHost(e.Uri);
        _owner.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // A dialog belongs to its tab: show that tab first, as Chrome does.
                Activate(tab);
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

    private void OnPermissionRequested(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var host = SafeHost(e.Uri);
        var what = DescribePermission(e.PermissionKind);
        _owner.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                Activate(tab);
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

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"Browser process failure: {e.ProcessFailedKind}.");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // Every tab shares the browser process; raise once for the whole engine.
                if (_environment is not null)
                {
                    _environment = null; // a new environment is required after the browser process exits
                    EngineFailed?.Invoke(this, "The browser engine stopped unexpectedly.");
                }

                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                var crashes = tab.RenderCrashes;
                crashes.Enqueue(DateTime.UtcNow);
                while (crashes.Count > 0 && crashes.Peek() < DateTime.UtcNow.AddMinutes(-1))
                {
                    crashes.Dequeue();
                }

                if (crashes.Count <= 3)
                {
                    tab.Core?.Reload();
                }
                else if (tab == ActiveTab)
                {
                    AxeDialog.ShowMessage(_owner, "This page keeps crashing", "AXE stopped reloading it. Try again later or open a different page.");
                }

                break;
        }
    }

    // ------------------------------------------------------------------ cleanup

    /// <summary>Closes the Chromium session cleanly; the browser processes exit with it.</summary>
    public void Dispose()
    {
        _disposed = true;
        var count = Tabs.Count;
        CloseAllTabs();
        _environment = null;
        if (count > 0)
        {
            Log.Info("Browser session closed.");
        }
    }
}

/// <summary>Pure tab-index arithmetic (unit tested).</summary>
public static class TabOrder
{
    /// <summary>After closing the tab at <paramref name="closedIndex"/>, the tab that becomes active: the one to its right, else the new last tab.</summary>
    public static int IndexAfterClose(int closedIndex, int remaining) =>
        remaining <= 0 ? -1 : Math.Min(closedIndex, remaining - 1);

    /// <summary>Wrap-around step for Ctrl+Tab / Ctrl+Shift+Tab.</summary>
    public static int Cycle(int index, int delta, int count) =>
        count <= 0 ? -1 : ((index + delta) % count + count) % count;

    /// <summary>Ctrl+1..8 → that tab if it exists; Ctrl+9 → last tab.</summary>
    public static int? IndexForNumber(int number, int count)
    {
        if (count <= 0 || number < 1 || number > 9)
        {
            return null;
        }

        if (number == 9)
        {
            return count - 1;
        }

        return number <= count ? number - 1 : null;
    }
}

/// <summary>What the address bar shows for a URL (internal pages show as empty).</summary>
public static class MainWindowUrl
{
    public static string Display(string url) =>
        url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url == "about:blank" ? string.Empty : url;
}
