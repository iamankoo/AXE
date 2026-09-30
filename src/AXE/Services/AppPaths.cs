using System.IO;

namespace AXE.Services;

/// <summary>Local folders used by AXE. Everything lives under %LOCALAPPDATA%\AXE.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AXE");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string Logs => Path.Combine(Root, "logs");

    public static string DefaultWebViewData => Path.Combine(Root, "WebView2");
}
