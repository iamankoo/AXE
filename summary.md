# AXE — Work Summary (2026-09-30)

## Status: complete and verified

AXE, the private floating Chromium browser (C# / .NET 8 / WPF / WebView2), is built, tested and packaged.

**Shareable installer (the only file to send):**
`C:\Users\iaman\Desktop\Anni-Personal\x-Zone\Project\axe\release\AXE-Setup.exe`
48.2 MB (50,544,546 bytes), SHA-256 `18618b762c907609df455583d4547ecd53a08001acb810886ed40c3bfe8de1f5`

AXE is also installed on this PC (per-user, `%LOCALAPPDATA%\Programs\AXE`, with a desktop shortcut) from that exact installer.

## What was built
- A floating browser matching the reference design:
  - header row: `AXE | Engineered by Aniket Raj`, then the opacity `%`, blue slider, and minimize/maximize/close;
  - navigation row: back/forward/refresh and a wide address pill ("Search Google or type a URL") with the site's favicon.
- Starts at 25% opacity (slider 10–100%), always on top, no taskbar button, about ¼ of the screen on the right.
- Capture exclusion via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, independent of opacity. No protection indicator is shown; a warning appears only if Windows refuses protection.
- Browser popups are drawn or kept inside AXE, so they are protected too: right-click menu, alert/confirm/prompt, permission prompts, downloads, new windows.
- Minimize hides AXE. It comes back via Start/Search relaunch (single instance) or **Ctrl+Alt+X**.
- Settings: start page, search engine (Google/Bing/DuckDuckGo/custom), starting opacity, clear browsing data, open the log folder.
- Local-only: no server, account or telemetry. Logs never contain URLs or typed text.
- Installer (Inno Setup):
  - publisher Aniket Raj, branded wizard;
  - per-user install by default;
  - Start Menu entry, optional desktop shortcut, "Launch AXE" checkbox;
  - installs WebView2 if it's missing; uninstall offers to delete user data.

## Key fixes found during testing
1. WPF silently removed the transparency style, so the window stayed at 100% opacity. Fixed with a native window subclass that keeps the style in place.
2. Windows clears capture exclusion when the transparency style is removed (AXE does this at 100% opacity). Fixed by re-applying exclusion immediately on every style change. Verified 60 ms after switching to 100%.
3. Pages without their own background rendered on AXE's dark default. Changed the default to white, as in Chromium.

## Verified (on the installed build)
- Unit tests: 62/62 pass.
- Capture suite: 77/77 checks pass (GDI + Windows.Graphics.Capture) at 10/25/50/75/100% opacity, after slider changes, move, resize, maximize, minimize/restore, with another window focused, with typed text, and with the context menu and `<select>` dropdown open.
- Real tools: AXE was absent from Win+PrintScreen, ffmpeg `ddagrab` (DXGI Desktop Duplication, the OBS path), ffmpeg `gdigrab`, and Chrome and Edge `getDisplayMedia` "Entire screen" sharing. The unprotected controls captured AXE in every tool.
- Browser: Google, search, video, keyboard, copy/paste, back/forward/refresh. Close leaves no leftover processes.
- Installer:
  - the GUI wizard was clicked through end to end;
  - Start Menu and desktop shortcuts were created; `Get-StartApps` lists AXE;
  - upgrade while AXE is running works;
  - uninstall works;
  - Publisher = Aniket Raj.

## Not yet done / open items
- [ ] Clean-machine install test (needs another PC or a VM; Windows Sandbox isn't available on Windows 11 Home).
- [ ] The WebView2-missing install path (this PC already has WebView2; the bootstrapper needs internet).
- [ ] Manual checks: Teams/Zoom/Discord desktop apps, the OBS app, a live Google Meet call, the Snipping Tool app UI, multiple monitors, display scaling above 100%.
- [ ] Web game keyboard play (2048 loaded, but the scripted arrow-key test was interrupted by a Windows flyout).
- [ ] Code signing: without a certificate, SmartScreen shows "Unknown publisher" (the recipient clicks **More info → Run anyway**).
- [ ] **Git: nothing committed yet.** The repo is initialized locally (identity `iamankoo`) with **no remote**. Review the diff, then make local commits.

## Rules for this project
- **Local only.** Never push, never add a git remote, no external uploads.
- Commits only after the owner reviews `git status` and the diff. Use the owner's identity; no AI attribution anywhere.
- Never commit env files or assistant instruction files.

## Useful commands
```powershell
# Full release build (tests -> self-contained publish -> installer) -> release\AXE-Setup.exe
powershell -ExecutionPolicy Bypass -File build\build-release.ps1

# Unit tests
dotnet test tests\AXE.Tests

# Capture validation (close AXE first; uses screen/mouse for ~3 min)
dotnet build tools\AXE.CaptureProbe -c Release
$probe = "tools\AXE.CaptureProbe\bin\Release\net8.0-windows10.0.19041.0\AXE.CaptureProbe.exe"
& $probe suite    "$env:LOCALAPPDATA\Programs\AXE\AXE.exe" <out-folder>
& $probe external "$env:LOCALAPPDATA\Programs\AXE\AXE.exe" <out-folder>
& $probe extras   "$env:LOCALAPPDATA\Programs\AXE\AXE.exe"
```

## Where things are
- App source: `src\AXE\`. Capture exclusion is in `Window\CaptureExclusionService.cs`; opacity in `Window\WindowManager.cs`.
- Tests: `tests\AXE.Tests\`. Capture/installer test tool: `tools\AXE.CaptureProbe\`.
- Installer: `installer\AXE.iss`. Build script: `build\build-release.ps1`.
- Docs: `README.md`, `docs\` (ARCHITECTURE, BUILD, INSTALLATION, PRIVACY, CAPTURE_EXCLUSION, TESTING).
- User data on this PC: `%LOCALAPPDATA%\AXE` (settings, logs, browser profile).
