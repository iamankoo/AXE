using System.IO;
using AxeV2.Access;

namespace AxeV2.Tests;

/// <summary>
/// The embedded server configuration fails closed: anything unusable means "not connected to a server" (access stays
/// locked), never a crash and never a fallback server. Loopback HTTP exists only in Debug builds; run
/// <c>dotnet test -c Release</c> to exercise the Release expectations.
/// </summary>
public class AccessConfigTests
{
    private const string Key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEexample";

    private static readonly string ReleaseKey = ReleaseKeyOf();

    private static string ReleaseKeyOf()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static string Json(string functionsUrl = "https://axe.example/functions/v1", string anon = "anon", string key = Key, string? manifest = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { functionsUrl, anonKey = anon, signingPublicKey = key, updateManifestUrl = manifest, releasePublicKey = manifest is null ? null : ReleaseKey });

    [Fact]
    public void A_complete_https_configuration_is_accepted()
    {
        var config = AccessConfig.Parse(Json());
        Assert.Equal("https://axe.example/functions/v1", config.FunctionsUri.AbsoluteUri.TrimEnd('/'));
        Assert.NotNull(AccessConfig.TryParse(Json()));
    }

    [Theory]
    [InlineData("http://axe.example/functions/v1")]
    [InlineData("http://192.168.1.10:54321/functions/v1")]
    [InlineData("ftp://axe.example/")]
    [InlineData("https://user:pass@axe.example/functions/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Plain_http_remote_hosts_credentials_and_garbage_are_refused(string url)
    {
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json(functionsUrl: url)));
        Assert.Null(AccessConfig.TryParse(Json(functionsUrl: url)));
    }

    [Theory]
    [InlineData("", Key)]
    [InlineData("anon", "")]
    [InlineData("  ", "  ")]
    public void A_configuration_without_its_keys_is_refused(string anon, string key)
    {
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json(anon: anon, key: key)));
        Assert.Null(AccessConfig.TryParse(Json(anon: anon, key: key)));
    }

    [Fact]
    public void An_insecure_update_manifest_url_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(Json(manifest: "http://updates.example/manifest.json")));
        Assert.NotNull(AccessConfig.TryParse(Json(manifest: "https://updates.example/manifest.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"functionsUrl\": 5}")]
    [InlineData("not json at all")]
    public void Malformed_configuration_never_throws_and_never_connects(string json) =>
        Assert.Null(AccessConfig.TryParse(json));

    [Fact]
    public void Loopback_http_is_a_Debug_only_convenience_and_a_Release_build_refuses_it()
    {
        var local = Json(functionsUrl: "http://127.0.0.1:54321/functions/v1");
#if DEBUG
        Assert.True(AccessConfig.AllowLoopbackHttp);
        Assert.NotNull(AccessConfig.TryParse(local));
#else
        Assert.False(AccessConfig.AllowLoopbackHttp);
        Assert.Throws<InvalidDataException>(() => AccessConfig.Parse(local));
        Assert.Null(AccessConfig.TryParse(local));
        // even an https URL that points at this PC is refused in a release build
        Assert.Null(AccessConfig.TryParse(Json(functionsUrl: "https://localhost/functions/v1")));
        Assert.Null(AccessConfig.TryParse(Json(functionsUrl: "https://127.0.0.1/functions/v1")));
#endif
    }

    [Fact]
    public void A_remote_host_that_merely_contains_localhost_is_not_loopback()
    {
        Assert.False(AccessConfig.IsLoopback(new Uri("https://localhost.evil.example/")));
        Assert.False(AccessConfig.IsLoopback(new Uri("https://axe.example/?next=127.0.0.1")));
        Assert.True(AccessConfig.IsLoopback(new Uri("http://127.0.0.1:54321/")));
        Assert.True(AccessConfig.IsLoopback(new Uri("http://localhost/")));
        Assert.True(AccessConfig.IsLoopback(new Uri("http://[::1]/")));
    }
}
