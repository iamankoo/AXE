using System.IO;

namespace AxeV2.Services;

/// <summary>Local folders used by AXE. Everything lives under %LOCALAPPDATA%\AXE v2, separate from AXE v1.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AXE v2");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string Logs => Path.Combine(Root, "logs");

    public static string DefaultWebViewData => Path.Combine(Root, "WebView2");
}
