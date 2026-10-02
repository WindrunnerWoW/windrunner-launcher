using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace WindrunnerLauncher.Tools.SignManifest;

/// <summary>
/// Matches the Core envelope format without linking launcher code into the signing tool.
/// </summary>
public sealed class SignedManifestEnvelope
{
    public string KeyId { get; set; } = "";
    public string Algorithm { get; set; } = "Ed25519";
    public string SignatureB64 { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SignedManifestEnvelope))]
internal partial class ToolJsonContext : JsonSerializerContext;

public static class Program
{
    private const string Usage = """
        sign-manifest - Ed25519 key generation and manifest signing for Windrunner Launcher

        Usage:
          sign-manifest keygen  --out-dir <dir> --key-id <id>
              Generates <id>.private.b64 and <id>.public.b64 (raw 32-byte keys, base64).

          sign-manifest sign    --key <private.b64> --key-id <id> --in <payload.json> [--out <envelope.json>]
              Wraps the payload verbatim in a SignedManifestEnvelope. Prints to stdout when --out is omitted.

          sign-manifest verify  --public <public.b64 file or base64> --in <envelope.json>
              Exit code 0 when the envelope verifies against the given public key, 1 otherwise.

          sign-manifest pubkey  --key <private.b64>
              Prints the base64 public key derived from a private key.

        The payload is signed byte-for-byte (UTF-8 of payloadJson); do not reformat it after signing.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            var opts = ParseOptions(args.Skip(1));
            return args[0].ToLowerInvariant() switch
            {
                "keygen" => KeyGen(opts),
                "sign" => Sign(opts),
                "verify" => Verify(opts),
                "pubkey" => PubKey(opts),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}", 2)
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, 1);
        }
    }

    private static int KeyGen(Dictionary<string, string> o)
    {
        var dir = Require(o, "out-dir");
        var keyId = Require(o, "key-id");
        Directory.CreateDirectory(dir);

        var priv = new byte[32];
        RandomNumberGenerator.Fill(priv);
        var pub = new Ed25519PrivateKeyParameters(priv).GeneratePublicKey().GetEncoded();

        var privPath = Path.Combine(dir, $"{keyId}.private.b64");
        var pubPath = Path.Combine(dir, $"{keyId}.public.b64");
        if (File.Exists(privPath) && !o.ContainsKey("force"))
            return Fail($"{privPath} already exists; pass --force to overwrite.", 1);

        File.WriteAllText(privPath, Convert.ToBase64String(priv) + "\n");
        File.WriteAllText(pubPath, Convert.ToBase64String(pub) + "\n");
        Console.WriteLine($"keyId:   {keyId}");
        Console.WriteLine($"public:  {Convert.ToBase64String(pub)}");
        Console.WriteLine($"private: {privPath}  (keep this secret for production keys)");
        return 0;
    }

    private static int Sign(Dictionary<string, string> o)
    {
        var priv = ReadKey(Require(o, "key"), 32);
        var keyId = Require(o, "key-id");
        var payload = File.ReadAllText(Require(o, "in"));

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(priv));
        var msg = Encoding.UTF8.GetBytes(payload);
        signer.BlockUpdate(msg, 0, msg.Length);
        var sig = signer.GenerateSignature();

        var envelope = new SignedManifestEnvelope
        {
            KeyId = keyId,
            Algorithm = "Ed25519",
            SignatureB64 = Convert.ToBase64String(sig),
            PayloadJson = payload
        };
        var json = JsonSerializer.Serialize(envelope, ToolJsonContext.Default.SignedManifestEnvelope);
        if (o.TryGetValue("out", out var outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json);
            Console.Error.WriteLine($"Wrote {outPath}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }

    private static int Verify(Dictionary<string, string> o)
    {
        var pub = ReadKey(Require(o, "public"), 32);
        var envelope = JsonSerializer.Deserialize(File.ReadAllText(Require(o, "in")), ToolJsonContext.Default.SignedManifestEnvelope)
                       ?? throw new InvalidDataException("Envelope is empty.");
        if (!string.Equals(envelope.Algorithm, "Ed25519", StringComparison.OrdinalIgnoreCase))
            return Fail($"Unsupported algorithm '{envelope.Algorithm}'.", 1);

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(pub));
        var msg = Encoding.UTF8.GetBytes(envelope.PayloadJson);
        verifier.BlockUpdate(msg, 0, msg.Length);
        var ok = verifier.VerifySignature(Convert.FromBase64String(envelope.SignatureB64));
        Console.WriteLine(ok ? $"OK  keyId={envelope.KeyId}" : $"FAIL keyId={envelope.KeyId}");
        return ok ? 0 : 1;
    }

    private static int PubKey(Dictionary<string, string> o)
    {
        var priv = ReadKey(Require(o, "key"), 32);
        Console.WriteLine(Convert.ToBase64String(new Ed25519PrivateKeyParameters(priv).GeneratePublicKey().GetEncoded()));
        return 0;
    }

    /// <summary>Accepts either a path to a file containing base64 or a literal base64 string.</summary>
    private static byte[] ReadKey(string fileOrB64, int expectedLength)
    {
        var text = File.Exists(fileOrB64) ? File.ReadAllText(fileOrB64) : fileOrB64;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"'{fileOrB64}' is neither an existing file nor valid base64.");
        }

        if (bytes.Length != expectedLength)
            throw new InvalidDataException($"Key must be {expectedLength} bytes, got {bytes.Length}.");
        return bytes;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{a}'.");
            var name = a[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                result[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[name] = list[++i];
            }
            else
            {
                result[name] = "true";
            }
        }

        return result;
    }

    private static string Require(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"Missing required option --{name}.");

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}
