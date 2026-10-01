using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AxeV2.Access;

namespace AxeV2.Updates;

/// <summary>
/// The signed update manifest, exactly as published. It is a <see cref="SignedToken"/> (<c>base64url(json).base64url(sig)</c>,
/// ECDSA P-256 / SHA-256 over the base64url payload text) with <c>typ</c> = <c>axe-update</c>, signed with the RELEASE key whose
/// public half is embedded in AXE. The private key never leaves the release machine.
/// </summary>
public sealed class UpdateManifest
{
    public const string Type = "axe-update";
    public const string ProductId = "axe-v2";

    [JsonPropertyName("typ")] public string? Typ { get; set; }
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("minimumSupportedVersion")] public string? MinimumSupportedVersion { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("releaseNotes")] public string? ReleaseNotes { get; set; }
    [JsonPropertyName("mandatory")] public bool? Mandatory { get; set; }
}

/// <summary>A manifest that passed every check, in typed form.</summary>
public sealed record UpdateInfo(
    Version Current,
    Version Version,
    Version MinimumSupported,
    Uri DownloadUri,
    string Sha256,
    long Size,
    string ReleaseDate,
    string ReleaseNotes,
    bool Mandatory)
{
    /// <summary>Enforced when the manifest says so, or when this build is older than the minimum the publisher still supports.</summary>
    public bool Enforced => Mandatory || Current < MinimumSupported;

    public string InstallerFileName => UpdateValidator.InstallerFileName(Version);
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Pure validation of a (signature-verified) manifest. Every rule fails closed with a precise reason.</summary>
public static partial class UpdateValidator
{
    public const long MinInstallerBytes = 1 * 1024 * 1024;
    public const long MaxInstallerBytes = 500L * 1024 * 1024;
    private const int MaxNotesLength = 4000;

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>The only installer name AXE will ever download for a version. Nothing else is accepted.</summary>
    public static string InstallerFileName(Version version) => $"AXE-v2-Setup-{version.ToString(3)}.exe";

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || !VersionPattern().IsMatch(text))
        {
            return false;
        }

        version = Version.Parse(text);
        return true;
    }

    /// <summary>
    /// Validates <paramref name="manifest"/> against the running build and the manifest's own origin.
    /// Returns the typed result, or a reason it must be rejected. A version that is not NEWER than the running build is
    /// reported as <see cref="UpdateCheckStatus.UpToDate"/> (never offered: no downgrades or replays of old manifests).
    /// </summary>
    public static UpdateCheckResult Validate(UpdateManifest manifest, Version current, Uri manifestUri)
    {
        if (manifest.Typ != UpdateManifest.Type)
        {
            return Reject("wrong message type");
        }

        if (manifest.Product != UpdateManifest.ProductId)
        {
            return Reject("the manifest is for a different product");
        }

        if (!TryParseVersion(manifest.Version, out var version))
        {
            return Reject("missing or malformed version");
        }

        if (!TryParseVersion(manifest.MinimumSupportedVersion, out var minimum))
        {
            return Reject("missing or malformed minimumSupportedVersion");
        }

        if (minimum > version)
        {
            return Reject("minimumSupportedVersion is newer than the version itself");
        }

        if (manifest.Mandatory is null)
        {
            return Reject("missing mandatory flag");
        }

        if (manifest.Sha256 is null || !Sha256Pattern().IsMatch(manifest.Sha256))
        {
            return Reject("missing or malformed sha256");
        }

        if (manifest.Size is not { } size || size < MinInstallerBytes || size > MaxInstallerBytes)
        {
            return Reject("missing or implausible size");
        }

        if (string.IsNullOrWhiteSpace(manifest.ReleaseDate) || !DateOnly.TryParseExact(manifest.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return Reject("missing or malformed releaseDate");
        }

        if (manifest.ReleaseNotes is null || manifest.ReleaseNotes.Length > MaxNotesLength)
        {
            return Reject("missing or oversized releaseNotes");
        }

        var uriProblem = CheckDownloadUri(manifest.DownloadUrl, version, manifestUri, out var downloadUri);
        if (uriProblem is not null)
        {
            return Reject(uriProblem);
        }

        if (version <= current)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, $"installed {current.ToString(3)} is not older than {version.ToString(3)}");
        }

        var info = new UpdateInfo(current, version, minimum, downloadUri!, manifest.Sha256, size, manifest.ReleaseDate, manifest.ReleaseNotes, manifest.Mandatory.Value);
        return new UpdateCheckResult(UpdateCheckStatus.Available, info, null);
    }

    /// <summary>HTTPS only, same origin as the manifest, exactly <c>/releases/{version}/AXE-v2-Setup-{version}.exe</c>, no credentials/query/fragment/escapes.</summary>
    private static string? CheckDownloadUri(string? value, Version version, Uri manifestUri, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            return "missing or oversized downloadUrl";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return "downloadUrl is not a valid URL";
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return "downloadUrl must use HTTPS";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return "downloadUrl must not carry credentials, a query or a fragment";
        }

        if (!parsed.IsDefaultPort || !string.Equals(parsed.Host, manifestUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return "downloadUrl must be on the same host as the manifest";
        }

        // Compare the raw text: Uri normalisation would hide '%2e%2e', '..' and backslashes.
        var expectedPath = $"/releases/{version.ToString(3)}/{InstallerFileName(version)}";
        var rawPath = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        rawPath = rawPath[rawPath.IndexOf('/')..];
        if (rawPath != expectedPath)
        {
            return $"downloadUrl must be exactly {expectedPath}";
        }

        uri = parsed;
        return null;
    }

    private static UpdateCheckResult Reject(string reason) => new(UpdateCheckStatus.Rejected, null, reason);
}

public enum UpdateCheckStatus
{
    /// <summary>Nothing newer than the running build is published.</summary>
    UpToDate,

    /// <summary>A newer, fully verified update exists.</summary>
    Available,

    /// <summary>The server answered but the manifest is untrustworthy or malformed: ignored, never acted on.</summary>
    Rejected,

    /// <summary>The update server could not be reached (offline, outage).</summary>
    Unavailable,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string? Reason);
