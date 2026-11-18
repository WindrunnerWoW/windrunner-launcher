using System.Globalization;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Selectable Lua memory ceilings for the "Raise Lua script memory" configuration asset. The
/// manifest values (16 MB of script memory and a 128 MB add-on ceiling) stay the default, so an
/// install that never touches the dropdown keeps exactly the pair it had.
/// </summary>
public static class ScriptMemoryOptions
{
    public const int DefaultMegabytes = 16;

    /// <summary>The add-on ceiling never drops below the value the manifest has always shipped.</summary>
    public const int MinimumAddOnMegabytes = 128;

    /// <summary>Choices offered by the launcher, in megabytes.</summary>
    public static readonly IReadOnlyList<int> Megabytes = [16, 128, 256, 512];

    /// <summary>Falls back to <see cref="DefaultMegabytes"/> for a missing or stale stored value.</summary>
    public static int Normalize(int megabytes) => Megabytes.Contains(megabytes) ? megabytes : DefaultMegabytes;

    /// <summary>CVar values for a ceiling, both expressed in kilobytes like the client expects.</summary>
    public static IReadOnlyDictionary<string, string> Values(int megabytes)
    {
        var chosen = Normalize(megabytes);
        var addOn = Math.Max(chosen, MinimumAddOnMegabytes);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scriptMemory"] = Kilobytes(chosen),
            ["maxAddOnMemory"] = Kilobytes(addOn)
        };
    }

    private static string Kilobytes(int megabytes) =>
        (megabytes * 1024).ToString(CultureInfo.InvariantCulture);
}
