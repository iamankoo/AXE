# Architecture

AXE is a single-process WPF application (.NET 8) hosting a Microsoft Edge WebView2 (Chromium) browser. There is no backend, database, account or network service of its own.

```
┌──────────────────────── AXE.exe (WPF) ────────────────────────┐
│ App                  single instance, logging, settings, start │
│ MainWindow           chrome UI + WebView2 host                 │
│   ├─ WindowManager          HWND: opacity, maximize bounds,    │
│   │                         hotkey, display/style notifications│
│   ├─ WindowStateManager     minimize/maximize/fullscreen       │
│   ├─ CaptureExclusionService display affinity + verification   │
│   └─ BrowserService         WebView2 lifecycle & UI plumbing   │
│ Dialogs/  AxeDialog, SettingsWindow (protected before shown)   │
└───────────────┬────────────────────────────────────────────────┘
                │ WebView2 SDK
      msedgewebview2.exe processes (browser, GPU, renderers)
      profile: %LOCALAPPDATA%\AXE\WebView2
```

## Source layout

```
src/AXE/
├── App.xaml(.cs)             startup, single instance, global error logging, shared styles
├── MainWindow.xaml(.cs)      header (brand, opacity, caption buttons), nav bar, browser host
├── Browser/
│   ├── BrowserService.cs     WebView2 environment/control, events, context menu, script
│   │                         dialogs, permissions, downloads, crash recovery
│   ├── BrowserSettings.cs    start page, search template, profile folder, feature flags
│   └── AddressResolver.cs    decides "URL or search" for address-bar input
├── Window/
│   ├── CaptureExclusionService.cs  SetWindowDisplayAffinity, read-back, re-apply, WinEvent hook
│   ├── WindowManager.cs      layered alpha, native subclass (keeps WS_EX_LAYERED), WM_GETMINMAXINFO,
│   │                         RegisterHotKey, display/DPI/style change events
│   ├── WindowStateManager.cs minimize→hide, restore, maximize, drag-from-maximized, page fullscreen
│   ├── WindowPlacement.cs    initial ¼-screen geometry (pure, unit-tested)
│   └── HotkeyParser.cs       "Ctrl+Alt+X" → RegisterHotKey args
├── Dialogs/                  AxeDialog (alert/confirm/prompt), SettingsWindow
├── Services/                 SettingsService, Log, SingleInstanceService, AppPaths, CommandLineOptions
├── Models/AppSettings.cs     settings.json root
└── Native/NativeMethods.cs   Win32 declarations (user32, dwmapi, comctl32)
tests/AXE.Tests/              xUnit tests (logic + real display-affinity API)
tools/AXE.CaptureProbe/       automated pixel-level capture test + UI smoke tests
installer/AXE.iss             Inno Setup script → AXE-Setup.exe
build/build-release.ps1       test → publish → installer, one command
```

## Key design decisions

**Opacity via layered-window alpha, not `AllowsTransparency`.** WPF's `AllowsTransparency` makes a per-pixel-alpha window, and WebView2's child HWND can't render inside one (the "airspace" limitation). AXE instead keeps a normal WPF window and applies a uniform alpha with `SetLayeredWindowAttributes(LWA_ALPHA)`. DWM applies it to the whole window, WebView2 content included. At 100% the layered style is removed for best video/game performance.

**Keeping WPF from undoing opacity.** WPF resets `WS_EX_LAYERED` on every `WM_STYLECHANGING`. A `SetWindowSubclass` procedure wraps WPF's window procedure and restores the bit after WPF runs. The same subclass raises `ExtendedStyleChanged` so capture exclusion is re-applied synchronously.

**No taskbar button, still a normal app.** `ShowInTaskbar=False` (WPF parents the window to a hidden owner). The process is ordinary: visible in Task Manager, launchable from Start/Search. Minimize hides the window, since a minimized window with no taskbar button would otherwise leave a stub on the desktop. Launching AXE again (single-instance signal) or pressing the hotkey restores it.

**Single instance.** A per-session mutex guarantees one process per WebView2 profile, so the profile can't be corrupted by concurrent launches. A second launch sets a named event and exits; the running instance restores itself.

**Keep browser UI inside AXE.** Several Chromium features open their own windows. AXE replaces them with its own protected UI or keeps them in the main view:
- context menu → WPF menu built from `ContextMenuRequested`
- `alert`/`confirm`/`prompt`/`beforeunload` → `AxeDialog`
- permission prompts → `AxeDialog`
- download bubble → AXE downloads menu
- `window.open` / `target=_blank` → same view
- DevTools / autofill → off by default

**Custom chrome.** `WindowStyle=None` + `WindowChrome` (6 px resize border). The page host has a 5 px inset so edge resizing hits the AXE window rather than the WebView child. `WM_GETMINMAXINFO` keeps a maximized borderless window inside the monitor work area; for page fullscreen (video), it covers the whole monitor.

## Startup sequence

1. `App.OnStartup`: initialize logging; single-instance check (a secondary instance signals and exits).
2. Load and normalize `settings.json`.
3. Create `CaptureExclusionService` and `MainWindow` (¼-screen geometry).
4. `SourceInitialized`: attach the window subclass, apply **capture exclusion**, then **opacity**, then **topmost**, and register the hotkey.
5. `Loaded`: re-assert opacity; create the WebView2 environment (`%LOCALAPPDATA%\AXE\WebView2`) and navigate to the start page or the command-line URL.

## Shutdown

× → `OnClosing`: dispose the WebView2 control (the browser processes exit), remove hooks, and stop timers → `Application.Shutdown`. The capture suite verifies that no AXE-profile `msedgewebview2.exe` processes remain afterwards.
