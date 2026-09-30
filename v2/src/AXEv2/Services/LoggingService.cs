using System.IO;
using System.Text;

namespace AxeV2.Services;

/// <summary>
/// Lightweight local diagnostic log. Records technical events only (startup, WebView2
/// initialization, capture-exclusion status, window state, errors).
/// It must never be given URLs, page content, typed text, form data or credentials.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int RetainDays = 7;
    private static readonly object Gate = new();
    private static string? _directory;

    public static string? Directory => _directory;

    public static void Initialize(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            _directory = directory;
            PruneOldFiles();
        }
        catch
        {
            _directory = null; // logging is best-effort and must never break AXE
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} [{Describe(ex)}]");

    /// <summary>Exception summary without stack-local data such as URLs or page content.</summary>
    internal static string Describe(Exception ex) =>
        $"{ex.GetType().Name} 0x{ex.HResult:X8}";

    private static void Write(string level, string message)
    {
        if (_directory is null)
        {
            return;
        }

        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(_directory, $"axe-v2-{DateTime.UtcNow:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    return; // cap per-day size; diagnostics only
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch
            {
                // ignore: logging must never throw
            }
        }
    }

    private static void PruneOldFiles()
    {
        if (_directory is null)
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "axe-v2-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetainDays))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
