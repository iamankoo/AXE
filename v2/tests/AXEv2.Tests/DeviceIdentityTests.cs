using System.IO;
using System.Security.Cryptography;
using System.Text;
using AxeV2.Access;
using Xunit;

namespace AxeV2.Tests;

/// <summary>Per-installation device identity: each installation has its own key, and an authorization is honoured only for it.</summary>
public class DeviceIdentityTests
{
    [Fact]
    public void The_device_id_is_the_sha256_of_the_installations_own_public_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "axe-device-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccessStore(dir);
            var spki = Convert.FromBase64String(store.DevicePublicKey());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(), store.DeviceHash());
            Assert.Equal(store.DevicePublicKey(), new AccessStore(dir).DevicePublicKey()); // persisted, stable across restarts
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            Assert.Equal(256, key.KeySize);
            Assert.DoesNotContain(Convert.ToBase64String(spki), File.ReadAllText(Path.Combine(dir, "device.key"), Encoding.Latin1)); // key file is DPAPI protected
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Proofs_verify_only_with_their_own_key_nonce_and_grant()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "axe-device-" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "axe-device-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new AccessStore(dirA);
            var b = new AccessStore(dirB);
            Assert.NotEqual(a.DeviceHash(), b.DeviceHash());

            bool Verify(AccessStore owner, string proof, string nonce, string jti)
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(owner.DevicePublicKey()), out _);
                var sig = Base64Url.Decode(proof);
                return sig.Length == 64 && key.VerifyData(Encoding.UTF8.GetBytes($"axe-device-proof|{nonce}|{jti}"), sig, HashAlgorithmName.SHA256);
            }

            var proof = a.SignDeviceProof("n1", "g1");
            Assert.True(Verify(a, proof, "n1", "g1"));
            Assert.False(Verify(b, proof, "n1", "g1"));
            Assert.False(Verify(a, proof, "n2", "g1"));
            Assert.False(Verify(a, proof, "n1", "g2"));
        }
        finally
        {
            foreach (var d in new[] { dirA, dirB })
            {
                try { Directory.Delete(d, true); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public void The_request_carries_the_installations_public_key_and_the_session_check_a_valid_proof() => Sta.Run(async () =>
    {
        using var h = new AccessHarness();
        await h.SubmitPendingAsync();
        Assert.Equal(h.Store.DeviceHash(), h.Server.LastSubmittedDevice);
        Assert.Equal(h.Store.DevicePublicKey(), h.Server.LastSubmittedPublicKey);

        h.Store.Save(new AccessState { Grant = h.Server.Grant("5h") });
        await h.Controller.StartAsync(); // resume: the server verifies the proof against the registered key
        Assert.Equal(AccessPhase.Active, h.Controller.Phase);
        Assert.False(string.IsNullOrEmpty(h.Server.LastProof));
    });

    [Fact]
    public void An_authorization_copied_to_another_installation_is_refused_and_cleared() => Sta.Run(async () =>
    {
        using var owner = new AccessHarness();
        var grant = owner.Server.Grant("5h"); // registered to the OWNER's key
        using var thief = new AccessHarness(server: owner.Server); // another installation, own key, same backend
        owner.Server.DevicePublicKey = owner.Store.DevicePublicKey();
        Assert.NotEqual(owner.Store.DeviceHash(), thief.Store.DeviceHash());

        thief.Store.Save(new AccessState { Grant = grant });
        await thief.Controller.StartAsync();

        // The grant names the owner's device id, so the client already refuses it locally.
        Assert.Equal(AccessPhase.NeedsAccess, thief.Controller.Phase);
        Assert.Contains("could not be verified", thief.Controller.Message);
        Assert.Null(thief.Store.Load().Grant);
        Assert.Equal(0, owner.Server.SessionCalls);

        // A grant that names the thief's id (the id is public) but is registered to the owner's key: the SERVER refuses the proof.
        var forged = owner.Server.Grant("5h", device: thief.Store.DeviceHash());
        thief.Store.Save(new AccessState { Grant = forged });
        await thief.Controller.StartAsync();
        Assert.Equal(AccessPhase.NeedsAccess, thief.Controller.Phase);
        Assert.Contains("doesn't belong to this PC", thief.Controller.Message);
        Assert.Null(thief.Store.Load().Grant);

        owner.Store.Save(new AccessState { Grant = grant });
        await owner.Controller.StartAsync();
        Assert.Equal(AccessPhase.Active, owner.Controller.Phase); // and the real owner is unaffected
    });
}
