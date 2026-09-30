using System.Globalization;

namespace AXE.Services;

/// <summary>
/// Command-line arguments:
/// <list type="bullet">
/// <item><c>AXE.exe [url]</c> — open a URL (or search text) instead of the start page.</item>
/// <item><c>--opacity=NN</c> — start at NN% opacity (10-100).</item>
/// <item><c>--diag-allow-capture</c> — DIAGNOSTIC ONLY: turns capture exclusion off so the
/// capture test suite can prove its detector sees AXE when it is not protected.</item>
/// </list>
/// </summary>
public sealed record CommandLineOptions(string? InitialAddress, double? Opacity, bool DiagnosticAllowCapture)
{
    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        string? address = null;
        double? opacity = null;
        var allowCapture = false;

        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Equals("--diag-allow-capture", StringComparison.OrdinalIgnoreCase))
            {
                allowCapture = true;
            }
            else if (arg.StartsWith("--opacity=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--opacity=".Length..].TrimEnd('%');
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    opacity = percent / 100.0;
                }
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 0)
            {
                address ??= arg;
            }
        }

        return new CommandLineOptions(address, opacity, allowCapture);
    }
}
