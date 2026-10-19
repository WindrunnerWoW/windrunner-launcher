using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.Core.Mods;

public sealed class VanillaTweaksResult
{
    public bool Applied { get; init; }
    public bool ToolMissing { get; init; }
    public string Message { get; init; } = "";
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>
/// Executable patching via <c>vanilla-tweaks.exe</c>.
///
/// The patched binary is always regenerated from the preserved clean executable, never from the
/// current (possibly already patched) <c>WoW.exe</c>. Patching a patched binary corrupts offsets,
/// so the clean copy is the only accepted input.
/// </summary>
public static class VanillaTweaks
{
    public const string ToolName = "vanilla-tweaks.exe";
    public const string AppliedStateFileName = ".windrunner-launcher-vanillatweaks.state";

    /// <summary>
    /// Command line flags of brndd/vanilla-tweaks 1.6.0. Most patches are on by default and are
    /// switched off with a <c>--no-</c> flag, so the builder only emits flags that change a default.
    /// Collected here so a future CLI change is a one-line edit rather than a hunt through the builder.
    /// </summary>
    public static class Flags
    {
        public const string Output = "-o";
        public const string NoLargeAddressAware = "--no-largeaddressaware";
        public const string FieldOfView = "--fov";
        public const string NoFieldOfView = "--no-fov";
        public const string MaxCameraDistance = "--maxcameradistance";
        public const string FarClip = "--farclip";
        public const string NoFarClip = "--no-farclip";
        public const string NameplateDistance = "--nameplatedistance";
        public const string NoNameplateDistance = "--no-nameplatedistance";
        public const string NoQuickloot = "--no-quickloot";
        public const string NoSoundInBackground = "--no-sound-in-background";
        public const string SoundChannels = "--soundchannels";
        public const string NoSoundChannels = "--no-soundchannels";
    }

    /// <summary>TBC-era nameplate range in yards; vanilla ships with 20.</summary>
    public const int TbcNameplateDistance = 41;

    /// <summary>Vanilla allocates 32 sound channels, which the tweak raises.</summary>
    public const int ExtendedSoundChannels = 64;

    public static VanillaTweaksSettings RecommendedPreset() => new()
    {
        UseRecommendedPreset = true,
        LargeAddressAware = true,
        FieldOfViewRadians = 1.925,
        CameraDistanceMax = 100,
        FarClip = 777,
        AlwaysAutoLoot = true,
        SoundInBackground = false,
        NameplateRangeTbc = true,
        MoreSoundChannels = true
    };

    /// <summary>Locates the optional patch tool in the client or in any managed mod store folder.</summary>
    public static string? FindTool(string clientDir, params string[] extraSearchDirectories)
    {
        foreach (var dir in new[] { clientDir }.Concat(extraSearchDirectories))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            var direct = Path.Combine(dir, ToolName);
            if (File.Exists(direct))
                return direct;

            foreach (var nested in Directory.EnumerateFiles(dir, ToolName, SearchOption.AllDirectories))
                return nested;
        }

        return null;
    }

    public static List<string> BuildArguments(VanillaTweaksSettings settings, string inputPath, string outputPath)
    {
        var effective = settings.UseRecommendedPreset ? RecommendedPreset() : settings;
        var args = new List<string> { Flags.Output, outputPath };

        if (!effective.LargeAddressAware)
            args.Add(Flags.NoLargeAddressAware);

        if (effective.FieldOfViewRadians > 0)
        {
            args.Add(Flags.FieldOfView);
            args.Add(effective.FieldOfViewRadians.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add(Flags.NoFieldOfView);
        }

        // The camera distance limit is opt-in upstream, so it is only passed when set.
        if (effective.CameraDistanceMax > 0)
        {
            args.Add(Flags.MaxCameraDistance);
            args.Add(effective.CameraDistanceMax.ToString(CultureInfo.InvariantCulture));
        }

        if (effective.FarClip > 0)
        {
            args.Add(Flags.FarClip);
            args.Add(effective.FarClip.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add(Flags.NoFarClip);
        }

        if (effective.NameplateRangeTbc)
        {
            args.Add(Flags.NameplateDistance);
            args.Add(TbcNameplateDistance.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add(Flags.NoNameplateDistance);
        }

        // Quickloot (automatic looting, hold Shift for manual) is on by default upstream.
        if (!effective.AlwaysAutoLoot)
            args.Add(Flags.NoQuickloot);

        if (!effective.SoundInBackground)
            args.Add(Flags.NoSoundInBackground);

        if (effective.MoreSoundChannels)
        {
            args.Add(Flags.SoundChannels);
            args.Add(ExtendedSoundChannels.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add(Flags.NoSoundChannels);
        }

        args.Add(inputPath);
        return args;
    }

    /// <summary>Stable identity of the effective patch settings, excluding input/output paths.</summary>
    public static string SettingsFingerprint(VanillaTweaksSettings settings, string cleanBackupPath, string livePath)
    {
        var effective = settings.UseRecommendedPreset ? RecommendedPreset() : settings;
        var cleanHash = File.Exists(cleanBackupPath) ? Security.Checksums.Sha256File(cleanBackupPath) : "missing";
        var liveHash = File.Exists(livePath) ? Security.Checksums.Sha256File(livePath) : "missing";
        var value = string.Join("|", cleanHash, liveHash, effective.LargeAddressAware, effective.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture),
            effective.CameraDistanceMax, effective.FarClip, effective.AlwaysAutoLoot, effective.SoundInBackground,
            effective.NameplateRangeTbc, effective.MoreSoundChannels);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static bool IsCurrent(string clientDir, string cleanBackupPath, VanillaTweaksSettings settings)
    {
        var target = ClientManager.FindExecutable(clientDir);
        if (target is null || !File.Exists(cleanBackupPath)) return false;
        var state = Path.Combine(clientDir, AppliedStateFileName);
        if (!File.Exists(state)) return false;
        var lines = File.ReadAllLines(state);
        return lines.Length > 0 && string.Equals(lines[0], SettingsFingerprint(settings, cleanBackupPath, target), StringComparison.Ordinal);
    }

    public static void RecordAppliedState(string clientDir, string cleanBackupPath, VanillaTweaksSettings settings)
    {
        var target = ClientManager.FindExecutable(clientDir);
        if (target is null) return;
        File.WriteAllText(Path.Combine(clientDir, AppliedStateFileName), SettingsFingerprint(settings, cleanBackupPath, target));
    }

    private static void ClearAppliedState(string clientDir)
    {
        try { File.Delete(Path.Combine(clientDir, AppliedStateFileName)); } catch { }
    }

    /// <summary>
    /// Regenerates the patched executable from the clean backup. When the optional tool is absent
    /// this is a no-op that still guarantees the clean backup exists.
    /// </summary>
    public static async Task<VanillaTweaksResult> ApplyAsync(
        string clientDir,
        string cleanBackupPath,
        VanillaTweaksSettings settings,
        string? toolPath = null,
        CancellationToken ct = default,
        string? wineRunner = null,
        string? winePrefix = null)
    {
        if (!File.Exists(cleanBackupPath))
        {
            return new VanillaTweaksResult
            {
                Message = $"No clean executable backup at {cleanBackupPath}; refusing to patch a modified binary."
            };
        }

        var target = ClientManager.FindExecutable(clientDir) ?? Path.Combine(clientDir, "WoW.exe");
        var tool = toolPath ?? FindTool(clientDir);
        if (tool is null || !File.Exists(tool))
        {
            return new VanillaTweaksResult
            {
                ToolMissing = true,
                Message = $"{ToolName} is not installed. The clean executable backup is preserved and " +
                          "WoW.exe was left untouched; install the VanillaTweaks asset to apply tweaks."
            };
        }

        var work = Path.Combine(Path.GetTempPath(), "twl-vanilla-tweaks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "WoW-clean.exe");
        var output = Path.Combine(work, "WoW-patched.exe");
        File.Copy(cleanBackupPath, input, overwrite: true);

        var arguments = BuildArguments(settings, input, output);
        var launchFile = tool;
        IEnumerable<string> launchArgs = arguments;
        IReadOnlyDictionary<string, string>? environment = null;
        if (!OperatingSystem.IsWindows() && WineHost.IsWindowsImage(tool))
        {
            var runner = WineHost.ResolveRunner(wineRunner);
            if (string.IsNullOrWhiteSpace(runner))
            {
                return new VanillaTweaksResult
                {
                    ToolMissing = true,
                    Message = $"{ToolName} is a Windows program. Install Wine, or set a Proton or Lutris runner in Settings."
                };
            }

            if (!string.IsNullOrWhiteSpace(winePrefix))
                await WineHost.EnsurePrefixAsync(runner, winePrefix, ct).ConfigureAwait(false);
            var launch = WineHost.CreateLaunch(
                runner, winePrefix ?? "", tool, arguments.Select(WineHost.RewriteArgument), dxvk: false);
            launchFile = launch.FileName;
            launchArgs = launch.Arguments;
            environment = launch.Environment;
        }

        var log = new StringBuilder();
        try
        {
            using var proc = ProcessHost.StartHidden(
                launchFile,
                work,
                launchArgs,
                captureOutput: true,
                onOutput: (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); },
                holdStdin: false,
                environment: environment);

            await proc.Process.WaitForExitAsync(ct).ConfigureAwait(false);
            var exit = proc.Process.ExitCode;

            if (exit != 0 || !File.Exists(output))
            {
                return new VanillaTweaksResult
                {
                    ExitCode = exit,
                    Output = log.ToString(),
                    Arguments = arguments,
                    Message = $"{ToolName} failed (exit code {exit}). WoW.exe was not modified."
                };
            }

            File.Copy(output, target, overwrite: true);
            RecordAppliedState(clientDir, cleanBackupPath, settings);
            return new VanillaTweaksResult
            {
                Applied = true,
                ExitCode = exit,
                Output = log.ToString(),
                Arguments = arguments,
                Message = "VanillaTweaks applied from the clean executable backup."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VanillaTweaksResult
            {
                Output = log.ToString(),
                Arguments = arguments,
                Message = $"Could not run {ToolName}: {ex.Message}"
            };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Creates the clean backup from the live executable when none exists. An existing backup is
    /// never overwritten, so it stays the untouched original for every later patch.
    /// </summary>
    public static string? EnsureCleanBackup(string clientDir)
    {
        var target = ClientManager.FindExecutable(clientDir);
        if (target is null)
            return null;

        var backup = ClientPaths.Child(clientDir, ClientManager.CleanBackupFileName);
        if (!File.Exists(backup))
            File.Copy(target, backup, overwrite: false);
        return backup;
    }

    /// <summary>Puts the clean executable back in place, undoing any executable patch.</summary>
    public static bool Revert(string clientDir, string cleanBackupPath)
    {
        if (!File.Exists(cleanBackupPath))
            return false;

        var target = ClientManager.FindExecutable(clientDir) ?? Path.Combine(clientDir, "WoW.exe");
        File.Copy(cleanBackupPath, target, overwrite: true);
        ClearAppliedState(clientDir);
        return true;
    }

    /// <summary>True when the live executable differs from the preserved clean copy.</summary>
    public static bool IsPatched(string clientDir, string cleanBackupPath)
    {
        var target = ClientManager.FindExecutable(clientDir);
        if (target is null || !File.Exists(cleanBackupPath))
            return false;
        return !Security.Checksums.Sha256File(target)
            .Equals(Security.Checksums.Sha256File(cleanBackupPath), StringComparison.OrdinalIgnoreCase);
    }
}
