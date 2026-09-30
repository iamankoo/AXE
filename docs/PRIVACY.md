# Privacy

AXE is local-first. It has **no server, no account, no analytics and no telemetry**. The only network traffic is what the websites you choose to open generate, plus the normal background traffic of Microsoft's WebView2 runtime (e.g. its own updates), which Microsoft governs.

## What AXE stores, and where

Everything is under `%LOCALAPPDATA%\AXE` on your PC:

| Data | Path | Purpose |
|---|---|---|
| Browser profile (cookies, cache, local storage, site permissions, WebView2 history) | `WebView2\` | Normal website functionality (staying signed in, etc.), managed by Chromium/WebView2 |
| Settings | `settings.json` | Start page, search engine, starting opacity, hotkey |
| Diagnostic log | `logs\axe-YYYYMMDD.log` | Technical events for troubleshooting; kept 7 days, capped at 1 MB/day |

A different browser-profile folder can be set with `Browser.UserDataFolder` in `settings.json`.

## What AXE never collects

AXE's own code does not record, transmit or log:

- browsing history or URLs
- typed text, form data or passwords
- website content, cookies or personal information

The diagnostic log records events such as "AXE started", "WebView2 initialized", "Capture exclusion applied/failed", "Window state changed: Maximized", "Navigation failed: HostNameNotResolved" (the error type only, never the address) and "AXE closed". Exceptions are logged by type and error code only, not by message, because messages can contain URLs.

Password saving and form autofill are **off** by default.

## Clearing data

- **Settings → Clear browsing data** deletes cookies, site data, cache and history from AXE's profile.
- Uninstalling AXE offers to delete `%LOCALAPPDATA%\AXE` entirely.

## Screen-capture exclusion is not anonymity

AXE's privacy feature is limited to keeping its window out of supported screen captures. It does not hide your traffic from networks or websites, does not make you anonymous, and does not hide the AXE process from the operating system, administrators or security software.
