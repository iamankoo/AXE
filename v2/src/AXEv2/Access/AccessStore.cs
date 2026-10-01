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
    private readonly string _deviceKeyFile;
    private readonly object _gate = new();
    private ECDsa? _deviceKey;

    public AccessStore(string directory)
    {
        _stateFile = Path.Combine(directory, "access.dat");
        _deviceKeyFile = Path.Combine(directory, "device.key");
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
    /// This installation's device id: the lowercase hex SHA-256 of its own public key (SubjectPublicKeyInfo). Every
    /// installation generates its own ECDSA P-256 key pair on first use; the private key is stored only encrypted with
    /// Windows DPAPI (this user, this PC) and never leaves the machine. Because the id is derived from the key, it can
    /// neither be chosen nor shared, and the backend can require a signature by the matching private key on every
    /// authorization check: a grant copied to another installation is useless there.
    /// </summary>
    public string DeviceHash()
    {
        lock (_gate)
        {
            return Convert.ToHexString(SHA256.HashData(DeviceKeyLocked().ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        }
    }

    /// <summary>This installation's public key (base64 SubjectPublicKeyInfo), registered with the backend with each request.</summary>
    public string DevicePublicKey()
    {
        lock (_gate)
        {
            return Convert.ToBase64String(DeviceKeyLocked().ExportSubjectPublicKeyInfo());
        }
    }

    /// <summary>
    /// Proof of possession of this installation's private key for ONE authorization check: an ECDSA P-256 / SHA-256
    /// (IEEE P1363, base64url) signature over <c>axe-device-proof|nonce|grantId</c>. Bound to the server's fresh nonce and the
    /// grant, so it cannot be replayed or reused for another grant.
    /// </summary>
    public string SignDeviceProof(string nonce, string grantId)
    {
        lock (_gate)
        {
            var signature = DeviceKeyLocked().SignData(Encoding.UTF8.GetBytes($"axe-device-proof|{nonce}|{grantId}"), HashAlgorithmName.SHA256);
            return Base64Url.Encode(signature);
        }
    }

    private ECDsa DeviceKeyLocked()
    {
        if (_deviceKey is not null)
        {
            return _deviceKey;
        }

        try
        {
            if (File.Exists(_deviceKeyFile))
            {
                var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(_deviceKeyFile), Entropy, DataProtectionScope.CurrentUser), out _);
                if (key.KeySize == 256)
                {
                    return _deviceKey = key;
                }

                key.Dispose();
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Device identity unreadable; creating a new one ({Log.Describe(ex)}).");
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteProtected(_deviceKeyFile, created.ExportPkcs8PrivateKey());
        return _deviceKey = created;
    }

    private static void WriteProtected(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
    }
}
