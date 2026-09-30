using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxeV2.Access;

/// <summary>
/// Verifies server-signed messages of the form <c>base64url(json).base64url(signature)</c>,
/// signed with ECDSA P-256 / SHA-256 (IEEE P1363 r||s). AXE holds only the public key; the
/// private key exists only in the server's secret store, so neither a modified AXE nor a
/// network attacker can mint authorizations or fake server time.
/// </summary>
public sealed class SignedToken
{
    private readonly ECDsa _key;

    public SignedToken(ECDsa publicKey)
    {
        _key = publicKey;
    }

    /// <summary>Loads a P-256 public key from base64 SubjectPublicKeyInfo (DER).</summary>
    public static SignedToken FromSpki(string base64Spki)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64Spki), out _);
        if (key.KeySize != 256)
        {
            throw new CryptographicException("Signing key must be P-256.");
        }

        return new SignedToken(key);
    }

    /// <summary>
    /// Verifies the signature and the <c>typ</c> field, then deserializes.
    /// Throws <see cref="AccessException"/> (Untrusted) for anything malformed, tampered or of the wrong type.
    /// </summary>
    public T Verify<T>(string? token, string expectedType, Func<T, string> typeOf)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16 * 1024)
        {
            throw Untrusted("missing or oversized");
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.') || dot == token.Length - 1)
        {
            throw Untrusted("format");
        }

        byte[] payload, signature;
        try
        {
            payload = Base64Url.Decode(token[..dot]);
            signature = Base64Url.Decode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            throw Untrusted("encoding");
        }

        if (signature.Length != 64
            || !_key.VerifyData(Encoding.ASCII.GetBytes(token[..dot]), signature, HashAlgorithmName.SHA256))
        {
            throw Untrusted("signature");
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload);
        }
        catch (JsonException)
        {
            throw Untrusted("payload");
        }

        if (value is null || typeOf(value) != expectedType)
        {
            throw Untrusted("type");
        }

        return value;
    }

    private static AccessException Untrusted(string what) =>
        new(AccessErrorKind.Untrusted, $"The AXE server's response could not be verified ({what}).");
}

public static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            0 => s,
            _ => throw new FormatException(),
        };
        return Convert.FromBase64String(s);
    }

    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
