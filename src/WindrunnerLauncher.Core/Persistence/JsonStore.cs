using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LauncherSettings))]
[JsonSerializable(typeof(ServerFriendlySettings))]
[JsonSerializable(typeof(VanillaTweaksSettings))]
[JsonSerializable(typeof(RealmListState))]
[JsonSerializable(typeof(RealmEntry))]
[JsonSerializable(typeof(SignedManifestEnvelope))]
[JsonSerializable(typeof(ClientManifest))]
[JsonSerializable(typeof(ClientBootstrap))]
[JsonSerializable(typeof(ManagedAsset))]
[JsonSerializable(typeof(AddonCatalog))]
[JsonSerializable(typeof(AddonEntry))]
[JsonSerializable(typeof(ServerManifest))]
[JsonSerializable(typeof(LauncherUpdateManifest))]
[JsonSerializable(typeof(RollbackMetadata))]
[JsonSerializable(typeof(TrustedKey))]
[JsonSerializable(typeof(List<TrustedKey>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(GitHubDeviceCode))]
[JsonSerializable(typeof(GitHubAccessTokenResponse))]
[JsonSerializable(typeof(GitHubUserResponse))]
internal partial class LauncherJsonContext : JsonSerializerContext;

public static class JsonStore
{
    private static readonly Lock SaveGate = new();

    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = LauncherJsonContext.Default,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T LoadOrNew<T>(string path, Func<T> factory) where T : class
    {
        if (!File.Exists(path))
            return factory();
        var json = File.ReadAllText(path);
        var info = (JsonTypeInfo<T>)LauncherJsonContext.Default.GetTypeInfo(typeof(T))!;
        return JsonSerializer.Deserialize(json, info) ?? factory();
    }

    public static void Save<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        var info = (JsonTypeInfo<T>)LauncherJsonContext.Default.GetTypeInfo(typeof(T))!;
        var json = JsonSerializer.Serialize(value, info);
        lock (SaveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var tmp = fullPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, fullPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
