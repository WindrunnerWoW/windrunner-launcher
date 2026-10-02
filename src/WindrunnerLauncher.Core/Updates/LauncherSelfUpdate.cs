using System.Reflection;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.Core.Updates;

/// <summary>
/// Atomic launcher replacement with exactly one retained previous executable.
///
/// Staging is <c>download → .new</c>, <c>current → .previous</c>, <c>.new → current</c>. The new
/// build calls <see cref="MarkHealthy"/> once its main window has initialized, which deletes the
/// previous executable. If initialization never gets that far, <see cref="RestorePrevious"/> puts
/// the old build back.
/// </summary>
public sealed class LauncherSelfUpdate
{
    public const string NewSuffix = ".new";
    public const string PreviousSuffix = ".previous";

    private readonly StateStore _state;

    public LauncherSelfUpdate(StateStore state)
    {
        _state = state;
    }

    /// <summary>The writable AppImage or launcher file, rather than an executable in a read-only mount.</summary>
    public static string CurrentExecutablePath() =>
        ResolveExecutablePath(
            OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null,
            Environment.ProcessPath);

    internal static string ResolveExecutablePath(string? appImagePath, string? processPath) =>
        !string.IsNullOrWhiteSpace(appImagePath) && Path.IsPathRooted(appImagePath)
            ? appImagePath
            : processPath
        ?? Path.Combine(AppContext.BaseDirectory,
            (Assembly.GetEntryAssembly()?.GetName().Name ?? "WindrunnerLauncher") + PlatformInfo.ExeSuffix);

    /// <summary>A staged update is waiting for the new build to report itself healthy.</summary>
    public bool PendingVerification =>
        !_state.Settings.HealthySelfUpdate
        && _state.Settings.PreviousLauncherExe is { Length: > 0 } previous
        && File.Exists(previous);

    /// <summary>
    /// Puts <paramref name="stagedExecutable"/> in place of the current launcher and keeps the old
    /// build beside it. Returns the path of the retained previous executable.
    /// </summary>
    public string Stage(string stagedExecutable, string? targetExecutable = null)
    {
        if (!File.Exists(stagedExecutable))
            throw new FileNotFoundException("Staged launcher executable not found.", stagedExecutable);

        var target = targetExecutable ?? CurrentExecutablePath();
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))
                        ?? throw new InvalidOperationException($"Cannot resolve a directory for {target}.");
        Directory.CreateDirectory(directory);

        var pending = target + NewSuffix;
        var previous = target + PreviousSuffix;

        // Land the payload beside the target first so the swap itself is a same-volume rename.
        if (File.Exists(pending))
            File.Delete(pending);
        File.Copy(stagedExecutable, pending, overwrite: true);
        MakeExecutable(pending);

        if (File.Exists(previous))
            File.Delete(previous);
        if (File.Exists(target))
            File.Move(target, previous);

        File.Move(pending, target);
        MakeExecutable(target);

        _state.Settings.PreviousLauncherExe = File.Exists(previous) ? previous : null;
        _state.Settings.HealthySelfUpdate = false;
        _state.SaveSettings();
        return previous;
    }

    /// <summary>Called once the new build's main window initializes. Drops the previous executable.</summary>
    public bool MarkHealthy()
    {
        var previous = _state.Settings.PreviousLauncherExe;
        var deleted = false;
        if (!string.IsNullOrWhiteSpace(previous) && File.Exists(previous))
        {
            try
            {
                File.Delete(previous);
                deleted = true;
            }
            catch (IOException)
            {
                // Still locked by the outgoing process; the next healthy start will clean it up.
                return false;
            }
        }

        _state.Settings.PreviousLauncherExe = null;
        _state.Settings.HealthySelfUpdate = true;
        _state.SaveSettings();
        return deleted;
    }

    /// <summary>Puts the retained previous executable back when the new build failed to start.</summary>
    public bool RestorePrevious(string? targetExecutable = null)
    {
        var previous = _state.Settings.PreviousLauncherExe;
        if (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous))
            return false;

        var target = targetExecutable ?? CurrentExecutablePath();
        var broken = target + ".failed";
        try
        {
            if (File.Exists(broken))
                File.Delete(broken);
            if (File.Exists(target))
                File.Move(target, broken);
            File.Move(previous, target);
            MakeExecutable(target);
        }
        catch (IOException)
        {
            return false;
        }

        _state.Settings.PreviousLauncherExe = null;
        _state.Settings.HealthySelfUpdate = true;
        _state.SaveSettings();
        return true;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Permission bits are best effort; the copy itself already succeeded.
        }
    }
}
