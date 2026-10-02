using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public class Ed25519Tests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("{\"schema\":1,\"version\":\"1.2.3\"}");

    [Fact]
    public void GenerateKeyPair_Produces32ByteKeys()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        Assert.Equal(32, pub.Length);
        Assert.Equal(32, priv.Length);
        Assert.NotEqual(pub, priv);
    }

    [Fact]
    public void SignVerify_Roundtrip()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, Message);
        Assert.Equal(64, sig.Length);
        Assert.True(Ed25519Signer.Verify(pub, Message, sig));
    }

    [Fact]
    public void Sign_IsDeterministic()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        Assert.Equal(Ed25519Signer.Sign(priv, Message), Ed25519Signer.Sign(priv, Message));
    }

    [Fact]
    public void Verify_WrongKey_Fails()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        var (otherPub, _) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, Message);
        Assert.False(Ed25519Signer.Verify(otherPub, Message, sig));
    }

    [Fact]
    public void Verify_TamperedMessage_Fails()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, Message);
        var tampered = (byte[])Message.Clone();
        tampered[^2] ^= 0x01;
        Assert.False(Ed25519Signer.Verify(pub, tampered, sig));
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, Message);
        sig[10] ^= 0xFF;
        Assert.False(Ed25519Signer.Verify(pub, Message, sig));
    }

    [Fact]
    public void Verify_MalformedInputs_ReturnFalseInsteadOfThrowing()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, Message);
        Assert.False(Ed25519Signer.Verify(new byte[5], Message, sig));
        Assert.False(Ed25519Signer.Verify(pub, Message, new byte[3]));
        Assert.False(Ed25519Signer.Verify([], Message, []));
    }

    [Fact]
    public void BuiltInTrust_EveryDomainHasActiveKeys_AllValid32BytePublicKeys()
    {
        var keys = BuiltInTrust.Keys;
        Assert.NotEmpty(keys);
        foreach (var domain in Enum.GetValues<TrustDomain>())
            Assert.Contains(keys, k => k.Domain == domain && !k.Retired);

        foreach (var k in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(k.KeyId));
            Assert.Equal(32, Convert.FromBase64String(k.PublicKeyB64).Length);
        }

        Assert.Equal(keys.Count, keys.Select(k => k.KeyId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BuiltInTrust_TrustsClientProductionKey()
    {
        var prod = Assert.Single(BuiltInTrust.Keys, k => k.KeyId == BuiltInTrust.ClientProductionKeyId);
        Assert.Equal(TrustDomain.Client, prod.Domain);
        Assert.False(prod.Retired);

        // A dev-signed envelope must not pass as the production key.
        var devSigned = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{\"schema\":1}");
        devSigned.KeyId = BuiltInTrust.ClientProductionKeyId;
        Assert.False(Ed25519Signer.VerifyEnvelope(devSigned, BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Theory]
    [InlineData(TrustDomain.Launcher)]
    [InlineData(TrustDomain.Client)]
    [InlineData(TrustDomain.Server)]
    public void BuiltInTrust_PublicKeys_MatchDevPrivateKeys(TrustDomain domain)
    {
        var trusted = Assert.Single(BuiltInTrust.Keys, k => k.KeyId == DevSigningKeys.KeyIdFor(domain));
        Assert.Equal(domain, trusted.Domain);
        Assert.Equal(DevSigningKeys.PublicFor(domain), trusted.PublicKeyB64);

        var sig = Ed25519Signer.Sign(DevSigningKeys.PrivateFor(domain), Message);
        Assert.True(Ed25519Signer.Verify(Convert.FromBase64String(trusted.PublicKeyB64), Message, sig));
    }

    [Theory]
    [InlineData(TrustDomain.Launcher)]
    [InlineData(TrustDomain.Client)]
    [InlineData(TrustDomain.Server)]
    public void VerifyEnvelope_SignedWithDevKey_VerifiesAgainstBuiltInTrust(TrustDomain domain)
    {
        var envelope = DevSigningKeys.SignEnvelope(domain, "{\"schema\":1}");
        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, domain));
    }

    [Fact]
    public void VerifyEnvelope_KeyFromOtherDomain_IsRejected()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{\"schema\":1}");
        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Server));
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Launcher));
    }

    [Fact]
    public void VerifyEnvelope_RetiredKey_Fails()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        const string payload = "{\"schema\":1,\"version\":\"9.9.9\"}";
        var envelope = new SignedManifestEnvelope
        {
            KeyId = "server-2",
            Algorithm = "Ed25519",
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(priv, Encoding.UTF8.GetBytes(payload))),
            PayloadJson = payload
        };

        var active = new TrustedKey { KeyId = "server-2", Domain = TrustDomain.Server, PublicKeyB64 = Convert.ToBase64String(pub) };
        var retired = new TrustedKey { KeyId = "server-2", Domain = TrustDomain.Server, PublicKeyB64 = Convert.ToBase64String(pub), Retired = true };

        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, [active], TrustDomain.Server));
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, [retired], TrustDomain.Server));
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, [], TrustDomain.Server));
    }

    [Fact]
    public void VerifyEnvelope_KeyRotation_OldKeyRetiredNewKeyActive()
    {
        var (oldPub, oldPriv) = Ed25519Signer.GenerateKeyPair();
        var (newPub, newPriv) = Ed25519Signer.GenerateKeyPair();
        const string payload = "{\"schema\":1}";
        var trust = new List<TrustedKey>
        {
            new() { KeyId = "client-1", Domain = TrustDomain.Client, PublicKeyB64 = Convert.ToBase64String(oldPub), Retired = true },
            new() { KeyId = "client-2", Domain = TrustDomain.Client, PublicKeyB64 = Convert.ToBase64String(newPub) }
        };

        var signedOld = new SignedManifestEnvelope
        {
            KeyId = "client-1",
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(oldPriv, Encoding.UTF8.GetBytes(payload))),
            PayloadJson = payload
        };
        var signedNew = new SignedManifestEnvelope
        {
            KeyId = "client-2",
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(newPriv, Encoding.UTF8.GetBytes(payload))),
            PayloadJson = payload
        };

        Assert.False(Ed25519Signer.VerifyEnvelope(signedOld, trust, TrustDomain.Client));
        Assert.True(Ed25519Signer.VerifyEnvelope(signedNew, trust, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_WrongKey_Fails()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        var (otherPub, _) = Ed25519Signer.GenerateKeyPair();
        const string payload = "{}";
        var envelope = new SignedManifestEnvelope
        {
            KeyId = "k",
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(priv, Encoding.UTF8.GetBytes(payload))),
            PayloadJson = payload
        };
        var trust = new[] { new TrustedKey { KeyId = "k", Domain = TrustDomain.Launcher, PublicKeyB64 = Convert.ToBase64String(otherPub) } };
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, trust, TrustDomain.Launcher));
    }

    [Fact]
    public void VerifyEnvelope_UnknownKeyId_Fails()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        envelope.KeyId = "client-does-not-exist";
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_KeyIdMatchIsCaseInsensitive()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        envelope.KeyId = envelope.KeyId.ToUpperInvariant();
        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_UnsupportedAlgorithm_Fails()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        envelope.Algorithm = "RSA-PSS";
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_AlgorithmMatchIsCaseInsensitive()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        envelope.Algorithm = "ed25519";
        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_TamperedPayload_Fails()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Server, "{\"version\":\"1.0.0\"}");
        envelope.PayloadJson = "{\"version\":\"1.0.1\"}";
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Server));
    }

    [Fact]
    public void VerifyEnvelope_MalformedBase64_ReturnsFalse()
    {
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        envelope.SignatureB64 = "not base64!";
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client));

        var badKey = new[] { new TrustedKey { KeyId = "x", Domain = TrustDomain.Client, PublicKeyB64 = "%%%" } };
        var env2 = DevSigningKeys.SignEnvelope(TrustDomain.Client, "{}");
        env2.KeyId = "x";
        Assert.False(Ed25519Signer.VerifyEnvelope(env2, badKey, TrustDomain.Client));
    }

    [Fact]
    public void VerifyEnvelope_UsesUtf8PayloadBytes_Verbatim()
    {
        // Signing must cover the exact payload string including whitespace and unicode.
        const string payload = "{ \"name\": \"Tortue — Schildkröte\",\n  \"n\": 1 }";
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Launcher, payload);
        Assert.True(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Launcher));
        envelope.PayloadJson = payload.Replace("\n  ", " ");
        Assert.False(Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Launcher));
    }
}
