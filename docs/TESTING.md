# Testing AXE

AXE is tested at three levels:

1. **Unit tests** (`tests/AXE.Tests`): logic, plus the real Windows display-affinity API on a real window.
2. **Automated capture suite** (`tools/AXE.CaptureProbe suite`): real screen captures, pixel-level.
3. **Manual checklist** (below): things that need a human, e.g. third-party sharing apps.

---

## 1. Unit tests

```powershell
dotnet test tests\AXE.Tests
```

62 tests covering: URL-vs-search resolution (including `javascript:` never being executed), search templates, settings defaults, clamping, corruption recovery and round-trip, ¼-screen initial placement, hotkey parsing, command-line parsing, and log redaction. They also exercise `SetWindowDisplayAffinity` on a real window:
- exclusion is applied and read back at 10/25/50/75/100% layered alpha;
- removing `WS_EX_LAYERED` resets affinity (a Windows platform behaviour), and re-applying restores it;
- diagnostic mode clears it;
- an invalid window reports **Failed**, never success.

## 2. Automated capture suite

```powershell
dotnet build tools\AXE.CaptureProbe -c Release
tools\AXE.CaptureProbe\bin\Release\net8.0-windows10.0.19041.0\AXE.CaptureProbe.exe suite <path\to\AXE.exe> <output-folder>
```

Close AXE first. The suite takes about 3 minutes and uses the screen, mouse and keyboard, so don't use the PC while it runs.

**How it works.** A topmost, solid-green (`#00A000`) backdrop window fills the primary screen. AXE is launched on a magenta test page containing a `<select>`, a text input and text. The screen is then captured with **two independent APIs**:

- **GDI** `BitBlt` with `CAPTUREBLT` (the classic screenshot path)
- **Windows.Graphics.Capture** monitor capture (the API used by the Snipping Tool, Xbox Game Bar and most modern screen-sharing and recording apps)

Inside AXE's window rectangle, a protected AXE must be **invisible**: ≥ 97% of pixels must be the green backdrop, which proves the desktop behind AXE is what gets captured. For popup scenarios, a 260 px margin around AXE is measured too.

**Positive controls.** The same scenarios run with `--diag-allow-capture` (exclusion off) and must show ≤ 10% backdrop, which proves the detector really sees AXE when it isn't protected. A control also opens the `<select>` dropdown and context menu, proving those popups actually open during the test.

Every capture is saved as PNG next to `report.md` for visual review.

### Scenarios

| Scenario | What it proves |
|---|---|
| `excluded-10/25/50/75/100` | Excluded at every opacity level (launch-time opacity) |
| `control-unprotected-25/100` | Detector sees AXE when unprotected; 25% really is translucent |
| `control-unprotected-popups`, `popups` | `<select>` dropdown (a Chromium-owned window) and AXE's context menu: visible when unprotected, absent when protected |
| `slider-100/10/100/60/100/25` | Runtime opacity changes via the real slider (UI Automation), captured 60 ms after each change, including repeated switches to 100% |
| `moved`, `resized` | Excluded after move and resize |
| `maximized`, `restored-from-maximize` | Excluded after maximize/restore (maximize button) |
| minimize + second launch | Minimize hides AXE and keeps it running; a second launch exits and restores the first instance (single instance); still excluded |
| `other-window-focused` | Still `WS_EX_TOPMOST` and excluded while another window is active |
| `typed-text` | Text typed into a page field is not captured |
| close checks (every scenario) | × exits the process and no AXE-profile `msedgewebview2.exe` remains |

### Recorded results

Windows 11 Home 10.0.26200, WebView2 Runtime 154.0.4258.37, single 1920×1080 monitor at 100% scaling.

| Build under test | Result |
|---|---|
| Development build (`src\AXE\bin\Debug`) | **ALL CHECKS PASSED** (77 PASS lines) |
| **Installed build** (`%LOCALAPPDATA%\Programs\AXE\AXE.exe`, installed by `AXE-Setup.exe`) | **ALL CHECKS PASSED** (77 PASS lines) |

Chromium's `<select>` popup window reported affinity `0x11` (inherited from AXE) and was absent from both capture APIs.

### Real-world capture tools (`external`)

```powershell
AXE.CaptureProbe.exe external <AXE.exe> <output-folder>
```

This runs actual tools against AXE, first with protection off (a control: each tool must **see** AXE), then with protection on (AXE must be **absent**):

| Tool | Capture path | Control (unprotected) | Protected |
|---|---|---|---|
| Win+PrintScreen | Windows' built-in screenshot | AXE visible ✅ | AXE absent, backdrop 100% ✅ |
| ffmpeg `ddagrab` | DXGI Desktop Duplication (OBS display capture) | AXE visible ✅ | AXE absent, backdrop 100% ✅ |
| ffmpeg `gdigrab` | GDI | AXE visible ✅ | AXE absent, backdrop 100% ✅ |
| Chrome `getDisplayMedia` "Entire screen" | Browser screen sharing (Meet, Teams web, …) | AXE visible ✅ | AXE absent, backdrop 100% ✅ |
| Edge `getDisplayMedia` "Entire screen" | Browser screen sharing | AXE visible ✅ | AXE absent, backdrop 100% ✅ |

Result on the **installed** build: **ALL CHECKS PASSED** (ffmpeg 8.1; Chrome and Edge current as of 2026-09-30).

### Smoke commands

```powershell
AXE.CaptureProbe.exe smoke <AXE.exe> <out>     # Google, address-bar search, video, web game (exclusion off, for screenshots)
AXE.CaptureProbe.exe keys  <AXE.exe> <out>     # keyboard, Ctrl+A/C/V, Tab inside a page
AXE.CaptureProbe.exe installer-gui <AXE-Setup.exe> <out> [/TASKS=desktopicon] [/LOG=file]
AXE.CaptureProbe.exe external <AXE.exe> <out>  # Win+PrtScn, ffmpeg DXGI/GDI, Chrome/Edge screen share
AXE.CaptureProbe.exe extras <AXE.exe>          # back/forward/refresh, taskbar-button and hotkey checks
AXE.CaptureProbe.exe inspect                   # live exstyle / layered alpha / topmost / affinity of AXE
```

---

## 3. Manual checklist

Legend: ✅ verified in this environment (how) · ⬜ to be verified by a person

### Browser
- ✅ Google opens (smoke screenshot)
- ✅ Search works: `weather in Delhi` → `google.com/search?q=weather%20in%20Delhi` (smoke run; Google answered its automated-traffic check page)
- ✅ URL navigation works (smoke: typed URLs)
- ✅ HTTPS works
- ✅ JavaScript works (keydown counter page, 2048 game rendered)
- ✅ Video works (Big Buck Bunny MP4: frames change after pressing play)
- ⬜ Browser game playable with keyboard (2048 loaded and rendered; the scripted arrow-key test was interrupted by a Windows Quick Settings flyout)
- ✅ Keyboard input works (20 keydown events registered in the page)
- ✅ Copy/paste works (Ctrl+A, Ctrl+C, Tab, Ctrl+V pasted into a page field)
- ✅ Back · ✅ Forward · ✅ Refresh (`extras`: example.com → example.org, then back/forward/refresh via the toolbar buttons)

### Window
- ✅ Drag / move (suite: moved) · ⬜ drag by mouse on the header
- ✅ Resize (suite: resized) · ⬜ resize by mouse on the edges
- ✅ Minimize works (hidden, process alive)
- ✅ Restore works (second launch)
- ✅ Maximize / restore works
- ✅ Close works (process exits, no WebView2 leftovers)
- ✅ Always on top (`WS_EX_TOPMOST` while another window is active)
- ✅ No taskbar button (`extras`: owned window without `WS_EX_APPWINDOW`, which Windows excludes from the taskbar)
- ✅ Start/Search launch works (`Get-StartApps` lists AXE; Start Menu shortcut launches the installed exe; a second launch doesn't duplicate)
- ✅ Ctrl+Alt+X hides and shows AXE (`extras`; affinity still `0x11` after restore)

### Transparency
- ✅ Starts at 25% (layered alpha 64/255 read from the live window)
- ✅ Slider decreases / increases opacity (UI Automation, 10 → 100)
- ✅ 100% opacity works
- ⬜ Low opacity remains usable (subjective)

### Capture exclusion
- ✅ 10 / 25 / 50 / 75 / 100% — AXE absent (GDI + WGC)
- ✅ Browser content absent · ✅ typed text absent · ✅ websites absent
- ✅ Desktop behind AXE remains visible (backdrop measured ≥ 97%, observed 100%)
- ✅ Moving · resizing · minimize/restore · maximize · other apps focused
- ⬜ **Changing displays / multi-monitor / DPI scaling other than 100%** (single-monitor machine)
- ✅ Win+PrintScreen · ✅ DXGI Desktop Duplication (ffmpeg `ddagrab`) · ✅ Chrome and Edge screen sharing (`getDisplayMedia`, entire screen)
- ⬜ **Snipping Tool app** (its Windows.Graphics.Capture API was tested; the app UI wasn't driven)
- ⬜ **Teams / Zoom / Discord desktop apps, the OBS app, a live Google Meet call**: start sharing the entire screen, confirm AXE is absent, then stop sharing

### Distribution
- ✅ `AXE-Setup.exe` runs, and the wizard was driven like a user: Welcome → Destination → Tasks → Install → Finish, exit code 0
- ✅ Publisher **Aniket Raj** in the installer file properties and in the Apps/uninstall entry
- ✅ Start Menu entry and optional desktop shortcut created
- ✅ AXE launches from the Finish page
- ✅ Upgrade while AXE is running (Setup closes it)
- ✅ Uninstall removes the program folder, shortcuts and uninstall entry, and keeps user data unless asked
- ⬜ **Clean-machine install** (a PC/VM without the .NET SDK or WebView2). Not performed: no VM or Windows Sandbox was available (Windows 11 Home). AXE is published self-contained, so it doesn't use the installed .NET SDK
- ⬜ WebView2-missing path (the bootstrapper install) — the test PC already has WebView2
