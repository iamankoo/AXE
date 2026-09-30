using System.IO;
using System.Text.Json;
using AxeV2.Browser;
using AxeV2.Models;

namespace AxeV2.Services;

/// <summary>Loads, validates and saves settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;

    public SettingsService(string path)
    {
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Loads settings. A missing file is created with defaults; a corrupt file is kept as
    /// settings.json.bad and defaults are used, so AXE always starts.
    /// </summary>
    public AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(_path))
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings file unreadable, using defaults. [{Log.Describe(ex)}]");
            TryBackupCorrupt();
        }

        Current = Normalize(loaded ?? new AppSettings());
        if (!File.Exists(_path))
        {
            Save(Current);
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = Normalize(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings.", ex);
        }
    }

    /// <summary>Clamps and repairs values so the rest of AXE can trust them.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        settings.Browser ??= new BrowserSettings();
        var browser = settings.Browser;

        settings.DefaultOpacity = AppSettings.ClampOpacity(settings.DefaultOpacity);
        settings.ToggleHotkey = settings.ToggleHotkey?.Trim() ?? string.Empty;

        browser.StartPage = AddressResolver.IsValidStartPage(browser.StartPage)
            ? browser.StartPage.Trim()
            : BrowserSettings.DefaultStartPage;

        browser.SearchUrlTemplate = AddressResolver.IsValidSearchTemplate(browser.SearchUrlTemplate)
            ? browser.SearchUrlTemplate.Trim()
            : BrowserSettings.DefaultSearchTemplate;

        if (string.IsNullOrWhiteSpace(browser.UserDataFolder) || !Path.IsPathFullyQualified(browser.UserDataFolder))
        {
            browser.UserDataFolder = null;
        }

        return settings;
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Copy(_path, _path + ".bad", overwrite: true);
            File.Delete(_path);
        }
        catch
        {
            // ignore
        }
    }
}
