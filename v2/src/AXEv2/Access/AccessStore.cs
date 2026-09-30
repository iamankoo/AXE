using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxeV2.Services;

namespace AxeV2.Access;

/// <summary>What AXE remembers between launches about access (encrypted on disk).</summary>
public sealed class AccessState
{
    /// <summary>Request waiting for the admin's decision, so a restart keeps waiting for it.</summary>
    public string? PendingRequestId { get; set; }

    public string? PendingPollToken { get; set; }

    /// <summary>The signed, server-issued authorization token.</summary>
    public string? Grant { get; set; }
}

/// <summary>
/// Minimal local access state, encrypted with Windows DPAPI for the current user and PC.
/// Stores no name, payment details or screenshot — only the pending request's id/poll secret
/// and the signed grant. Deleting these files can only lose access, never extend it: the
/// expiry lives inside the server-signed grant and is checked against server time.
/// </summary>
public sealed class AccessStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AXE v2 access state 1");
    private readonly string _stateFile;
    private readonly string _deviceFile;
    private readonly object _gate = new();

    public AccessStore(string directory)
    {
        _stateFile = Path.Combine(directory, "access.dat");
        _deviceFile = Path.Combine(directory, "device.dat");
    }

    public AccessState Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_stateFile))
                {
                    return new AccessState();
                }

                var json = ProtectedData.Unprotect(File.ReadAllBytes(_stateFile), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AccessState>(json) ?? new AccessState();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn($"Access state unreadable; starting fresh ({Log.Describe(ex)}).");
                return new AccessState();
            }
        }
    }

    public void Save(AccessState state)
    {
        lock (_gate)
        {
            WriteProtected(_stateFile, JsonSerializer.SerializeToUtf8Bytes(state));
        }
    }

    public void Clear() => Save(new AccessState());

    /// <summary>
    /// SHA-256 (hex) of a random per-PC secret created on first use. Grants are bound to it,
    /// so a grant copied to another PC (where DPAPI cannot decrypt this secret) is useless.
    /// </summary>
    public string DeviceHash()
    {
        lock (_gate)
        {
            byte[]? secret = null;
            try
            {
                if (File.Exists(_deviceFile))
                {
                    secret = ProtectedData.Unprotect(File.ReadAllBytes(_deviceFile), Entropy, DataProtectionScope.CurrentUser);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Device identity unreadable; creating a new one ({Log.Describe(ex)}).");
            }

            if (secret is not { Length: 32 })
            {
                secret = RandomNumberGenerator.GetBytes(32);
                WriteProtected(_deviceFile, secret);
            }

            return Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant();
        }
    }

    private static void WriteProtected(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
    }
}
