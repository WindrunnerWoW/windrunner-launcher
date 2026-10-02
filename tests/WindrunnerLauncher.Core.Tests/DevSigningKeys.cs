namespace WindrunnerLauncher.Core.Tests;

/// <summary>
/// Private halves of the development trust anchors compiled into
/// <c>WindrunnerLauncher.Core.Security.BuiltInTrust</c>. These are intentionally public so unit
/// tests and local sample manifests can be signed. They are never used for production releases;
/// a launcher built against dev anchors must not be distributed.
/// Files with the same material live in <c>tools/sign-manifest/dev-keys/</c>.
/// </summary>
public static class DevSigningKeys
{
    public const string LauncherKeyId = "launcher-dev-1";
    public const string ClientKeyId = "client-dev-1";
    public const string ServerKeyId = "server-dev-1";

    public const string LauncherPrivateB64 = "7QJTMGDhSDWx2dRf9U6Xj2UCs4S2AxgdEEWgHLWr7uM=";
    public const string ClientPrivateB64 = "gj0Mb1gLCCISGe1jI/6kLuALylQe7aKuvi0a7C7TX6Q=";
    public const string ServerPrivateB64 = "rmjKDeyaCEU3l+9FOhd+YPbXafb5i/GX/nui2o3XqBI=";

    public const string LauncherPublicB64 = "5fqCKP8nB5SDunHLDbpxy8ByxR43PBtLSoTVOJxuL7k=";
    public const string ClientPublicB64 = "cxqNeKVgLiO17rNXUyhMmWLYvbpiums47+t9YTAKgks=";
    public const string ServerPublicB64 = "lp3eyek4ARzSSBphdmYNlj+OtXIFNb0Ge8d6RdeTe4E=";

    public static byte[] PrivateFor(TrustDomain domain) => Convert.FromBase64String(domain switch
    {
        TrustDomain.Launcher => LauncherPrivateB64,
        TrustDomain.Client => ClientPrivateB64,
        TrustDomain.Server => ServerPrivateB64,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    });

    public static string KeyIdFor(TrustDomain domain) => domain switch
    {
        TrustDomain.Launcher => LauncherKeyId,
        TrustDomain.Client => ClientKeyId,
        TrustDomain.Server => ServerKeyId,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    public static string PublicFor(TrustDomain domain) => domain switch
    {
        TrustDomain.Launcher => LauncherPublicB64,
        TrustDomain.Client => ClientPublicB64,
        TrustDomain.Server => ServerPublicB64,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    /// <summary>Signs <paramref name="payloadJson"/> with the dev key for <paramref name="domain"/>.</summary>
    public static SignedManifestEnvelope SignEnvelope(TrustDomain domain, string payloadJson)
    {
        var sig = Security.Ed25519Signer.Sign(PrivateFor(domain), Encoding.UTF8.GetBytes(payloadJson));
        return new SignedManifestEnvelope
        {
            KeyId = KeyIdFor(domain),
            Algorithm = "Ed25519",
            SignatureB64 = Convert.ToBase64String(sig),
            PayloadJson = payloadJson
        };
    }
}
