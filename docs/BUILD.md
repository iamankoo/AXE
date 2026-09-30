# Building AXE

## Requirements

- Windows 10 (2004+) or Windows 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (only needed to produce the installer)
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11) to run AXE locally

## Build and run

```powershell
dotnet build AXE.sln
dotnet run --project src\AXE
```

Useful command-line arguments:

| Argument | Effect |
|---|---|
| `AXE.exe <url-or-search>` | open that page instead of the start page |
| `--opacity=NN` | start at NN % opacity (10–100) |
| `--diag-allow-capture` | **diagnostic only** — disables capture exclusion so the test suite can prove its detector sees AXE. Never needed for normal use. |

## Tests

```powershell
dotnet test tests\AXE.Tests
```

For the pixel-level capture test and UI smoke tests (`tools\AXE.CaptureProbe`), see [TESTING.md](TESTING.md).

## Release: one command

```powershell
powershell -ExecutionPolicy Bypass -File build\build-release.ps1
```

This will:

1. run the unit tests;
2. `dotnet publish` AXE **self-contained** for `win-x64` (ReadyToRun, no symbols) into `artifacts\publish` — the .NET runtime is bundled, so target PCs need no .NET install;
3. download Microsoft's **WebView2 Evergreen Bootstrapper** into `installer\redist` (first run only) and verify its Microsoft Authenticode signature;
4. compile `installer\AXE.iss` with Inno Setup;
5. check that `release\` contains exactly one file: **`release\AXE-Setup.exe`**.

`artifacts\`, `release\` and `installer\redist\` are build outputs and are git-ignored.

## Versioning

Update the version in two places:

- `src\AXE\AXE.csproj` → `<Version>`
- `installer\AXE.iss` → `#define AppVersion`

## Code signing (recommended before public distribution)

`AXE-Setup.exe` and `AXE.exe` are **not Authenticode-signed**, so Windows SmartScreen and UAC show "Unknown publisher" even though the file metadata says *Aniket Raj*. Signing with a code-signing certificate issued to Aniket Raj makes Windows display that name. Example:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a artifacts\publish\AXE.exe
# rebuild the installer, then:
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a release\AXE-Setup.exe
```
