using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Security;

public static class Checksums
{
    public static string Sha256Hex(Stream data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Sha256Hex(fs);
    }

    public static bool VerifyFile(string path, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
            return false;
        var actual = Sha256File(path);
        return actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha1HexUpper(byte[] data)
    {
        var hash = SHA1.HashData(data);
        return Convert.ToHexString(hash);
    }

    public static string WowPassHash(string username, string password)
    {
        var payload = Encoding.UTF8.GetBytes($"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}");
        return Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
    }
}

public static class Ed25519Signer
{
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);
        var parameters = new Ed25519PrivateKeyParameters(privateKey);
        var publicKey = parameters.GeneratePublicKey().GetEncoded();
        return (publicKey, privateKey);
    }

    public static byte[] Sign(byte[] privateKey, byte[] message)
    {
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyEnvelope(SignedManifestEnvelope envelope, IEnumerable<TrustedKey> keys, TrustDomain domain)
    {
        var key = keys.FirstOrDefault(k =>
            !k.Retired && k.Domain == domain && string.Equals(k.KeyId, envelope.KeyId, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;
        if (!string.Equals(envelope.Algorithm, "Ed25519", StringComparison.OrdinalIgnoreCase))
            return false;
        byte[] pub, sig, msg;
        try
        {
            pub = Convert.FromBase64String(key.PublicKeyB64);
            sig = Convert.FromBase64String(envelope.SignatureB64);
            msg = Encoding.UTF8.GetBytes(envelope.PayloadJson);
        }
        catch
        {
            return false;
        }

        return Verify(pub, msg, sig);
    }
}

public static class BuiltInTrust
{
    public const string ClientProductionKeyId = "client-prod-1";
    public const string ClientProductionPublicB64 = "n/DdxiWqZkLgCBOEadHebyoE0MTyPAUMvbaG/5b7fa8=";

    public const string LauncherProductionKeyId = "launcher-prod-1";
    public const string LauncherProductionPublicB64 = "kH+/aCW5P/WltuDJSzgfvKz25p9JwQPVY9yTZNc9fcg=";

    /// <summary>
    /// ProductionTrust excludes the public development keys for client and launcher manifests.
    /// Server manifests still use the development server key until a production key is configured.
    /// </summary>
    public static IReadOnlyList<TrustedKey> Keys { get; } = BuildKeys();

    private static IReadOnlyList<TrustedKey> BuildKeys()
    {
        var keys = new List<TrustedKey>();
#if !PRODUCTION_TRUST
        keys.Add(new TrustedKey
        {
            KeyId = "launcher-dev-1",
            Domain = TrustDomain.Launcher,
            PublicKeyB64 = DevKeys.LauncherPublicB64
        });
        keys.Add(new TrustedKey
        {
            KeyId = "client-dev-1",
            Domain = TrustDomain.Client,
            PublicKeyB64 = DevKeys.ClientPublicB64
        });
#endif
        if (LauncherProductionKeyId.Length > 0)
        {
            keys.Add(new TrustedKey
            {
                KeyId = LauncherProductionKeyId,
                Domain = TrustDomain.Launcher,
                PublicKeyB64 = LauncherProductionPublicB64
            });
        }
        keys.Add(new TrustedKey
        {
            KeyId = ClientProductionKeyId,
            Domain = TrustDomain.Client,
            PublicKeyB64 = ClientProductionPublicB64
        });
        keys.Add(new TrustedKey
        {
            KeyId = "server-dev-1",
            Domain = TrustDomain.Server,
            PublicKeyB64 = DevKeys.ServerPublicB64
        });
        return keys;
    }
}

/// <summary>
/// Development trust anchors: real 32-byte Ed25519 public keys generated once for this repository.
/// The matching private keys are intentionally public and live only in
/// <c>tests/WindrunnerLauncher.Core.Tests/DevSigningKeys.cs</c> and <c>tools/sign-manifest/dev-keys/</c>
/// so that unit tests and locally signed sample manifests verify. They must never be used to sign
/// production artifacts.
/// </summary>
internal static class DevKeys
{
    public const string LauncherPublicB64 = "5fqCKP8nB5SDunHLDbpxy8ByxR43PBtLSoTVOJxuL7k=";
    public const string ClientPublicB64 = "cxqNeKVgLiO17rNXUyhMmWLYvbpiums47+t9YTAKgks=";
    public const string ServerPublicB64 = "lp3eyek4ARzSSBphdmYNlj+OtXIFNb0Ge8d6RdeTe4E=";
}
