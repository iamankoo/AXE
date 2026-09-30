# Screen-capture exclusion

AXE's defining feature: the AXE window is visible and usable on the physical display but excluded from supported Windows screen-capture paths.

## Product statement

> AXE is designed to exclude its window from supported Windows screen-capture mechanisms. Capture applications that intentionally bypass Windows display-affinity protections, external cameras, hardware capture devices, or privileged system inspection may still observe AXE.

## Mechanism

AXE calls the documented Win32 API:

```c
SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);   // 0x11, Windows 10 2004+
```

When a window has this affinity, the Desktop Window Manager (DWM) composes it on the physical display but omits it from the images it hands to capture APIs. The capture shows whatever is behind the window, as if it weren't there.

`src/AXE/Window/CaptureExclusionService.cs`:

1. **Apply once the HWND exists.** Protection is applied in `SourceInitialized`, when the native window exists but before it is first shown, so no frame is ever composed unprotected.
2. **Verify by read-back.** `GetWindowDisplayAffinity` must return `0x11`, or the attempt counts as a failure.
3. **Fallback.** On Windows versions before 2004, `WDA_EXCLUDEFROMCAPTURE` is unavailable. AXE then applies `WDA_MONITOR` (the window appears **black** in captures) and reports this honestly: state `MonitorOnly`, plus a warning icon.
4. **Failure is never hidden.** If both calls fail, the state is `Failed`, the header shows an amber ⚠ icon, and the log records it. AXE shows **no** indicator while it is protected, as the product design requires. The icon appears only when protection is *missing*.
5. **Re-apply on change.** Protection is re-applied:
   - synchronously on every extended-style change (`WM_STYLECHANGED`). **Windows clears display affinity when `WS_EX_LAYERED` is removed**, which AXE does when opacity reaches 100%. This platform behaviour is covered by a unit test.
   - on window state changes (maximize / restore / minimize-restore)
   - on display, DPI and settings changes (`WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_SETTINGCHANGE`)
   - by a 2-second verification timer that re-applies if the affinity ever drifts
6. **Auxiliary windows.** AXE's own dialogs, settings window and context/download menus are protected before they are first shown. A process-wide `EVENT_OBJECT_CREATE`/`SHOW` WinEvent hook protects any other top-level window the process creates. Chromium's own popup windows (e.g. `<select>` dropdowns) inherit the affinity from the AXE window; this was verified (they report `0x11` and are absent from captures).

## Independence from opacity and topmost

Three separate concerns, implemented separately:

| Concern | Mechanism | Code |
|---|---|---|
| Opacity (local look) | Layered-window alpha, `SetLayeredWindowAttributes(LWA_ALPHA)` | `WindowManager.SetOpacity` |
| Stacking order | WPF `Topmost` (`WS_EX_TOPMOST`) | `MainWindow`, `WindowStateManager.Restore` |
| Capture exclusion | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | `CaptureExclusionService` |

The one place where Windows couples them (removing `WS_EX_LAYERED` resets affinity) is handled by the synchronous `WM_STYLECHANGED` re-apply described above. It was tested by switching to 100% opacity and capturing **60 ms** later.

WPF also forces `WS_EX_LAYERED` to match `AllowsTransparency` on every style change. AXE keeps the layered bit through a native window subclass (`SetWindowSubclass`) that adjusts the style after WPF has processed `WM_STYLECHANGING`.

## What is and isn't covered

| Capture path | Expected result |
|---|---|
| GDI `BitBlt` screen capture, ffmpeg `gdigrab` | Excluded — **tested** |
| **Win+PrintScreen** (Windows' built-in screenshot to Pictures\Screenshots) | Excluded — **tested** |
| Windows.Graphics.Capture (API behind the Snipping Tool, Game Bar and most modern sharing/recording apps) | Excluded — **tested** (monitor capture) |
| **DXGI Desktop Duplication** (ffmpeg `ddagrab`; the path OBS display capture uses) | Excluded — **tested** |
| **Browser screen sharing** — `getDisplayMedia` "Entire screen" in **Chrome** and **Edge** (the path Google Meet, Teams web, Zoom web and Discord web use) | Excluded — **tested** |
| Desktop sharing apps (Teams/Zoom/Discord desktop clients, the OBS app itself) | Depends on each app's capture method. They generally use the APIs above, but were **not driven directly** here; test before relying on them (see TESTING.md) |
| Sharing a single *other* window | AXE isn't part of that window, so it isn't shared regardless |
| External cameras, HDMI capture cards | **Not excluded** — physical display is visible |
| Apps/drivers that bypass display affinity, privileged/kernel inspection, remote-desktop hosts | **Not guaranteed** |
| Task Manager / process lists | AXE is visible, by design. AXE never hides its process |

## Known limitations

- The first frame of a WPF popup (e.g. a context menu) is protected via `PresentationSource` source-changed hooks before it is shown. The WinEvent hook is a backstop for anything else and is asynchronous by nature.
- The Chromium `<select>` dropdown result was verified on WebView2 Runtime 154 and relies on WebView2 propagating the host's affinity to its popups.
- Windows itself can draw above AXE (Start menu, Task View, UAC secure desktop, exclusive-fullscreen apps). AXE does not try to override this.
