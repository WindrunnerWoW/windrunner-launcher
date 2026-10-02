using System.Diagnostics;

namespace WindrunnerLauncher.Core.Platform;

/// <summary>
/// How a Windows game is started on Linux. Wine binaries (system Wine, Lutris, Wine-GE) use
/// <c>WINEPREFIX</c>. A Proton script uses <c>proton run</c>, or <c>umu-run</c> when that is installed.
/// </summary>
public enum WineRunnerKind
{
    Wine,
    Proton,
    Umu
}

/// <summary>The process, arguments, and extra environment for one Wine or Proton launch.</summary>
public sealed class WineLaunch
{
    public required WineRunnerKind Kind { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required string PrefixMarker { get; init; }
}

/// <summary>
/// Runs Windows game binaries on Linux. The prefix lives in the launcher data directory so the
/// user's default <c>~/.wine</c> is left alone.
/// </summary>
public static class WineHost
{
    public static bool IsWindowsImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[2];
            return stream.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The Steam or GE <c>proton</c> wrapper script.</summary>
    public static bool IsProtonWrapper(string runner) =>
        Path.GetFileName(runner).Equals("proton", StringComparison.OrdinalIgnoreCase);

    /// <summary>The umu launcher, which runs Proton outside Steam.</summary>
    public static bool IsUmu(string runner) =>
        Path.GetFileName(runner).Equals("umu-run", StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings override, then <c>WINE</c>, then <c>wine</c>, then <c>wine64</c>.</summary>
    public static string? ResolveRunner(string? settingsOverride)
    {
        if (!string.IsNullOrWhiteSpace(settingsOverride))
            return ResolveCommand(settingsOverride.Trim());

        var env = Environment.GetEnvironmentVariable("WINE");
        if (!string.IsNullOrWhiteSpace(env))
            return ResolveCommand(env.Trim());

        return Which("wine") ?? Which("wine64");
    }

    public static bool PrefixIsReady(string runner, string prefix) =>
        File.Exists(CreateLaunch(runner, prefix, "wineboot", [], dxvk: false).PrefixMarker);

    public static async Task EnsurePrefixAsync(string wine, string prefix, CancellationToken ct = default)
    {
        var launch = CreateLaunch(wine, prefix, "wineboot", ["-u"], dxvk: false);
        if (File.Exists(launch.PrefixMarker))
            return;

        Directory.CreateDirectory(prefix);
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = prefix,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in launch.Arguments)
            psi.ArgumentList.Add(argument);
        foreach (var pair in launch.Environment)
            psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Failed to start {launch.FileName} to create the Wine prefix.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process already exited.
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The pipes close when the process dies.
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
        {
            // wineserver saves system.reg a few seconds after wineboot exits, so wait for it.
            if (launch.Kind == WineRunnerKind.Wine)
                await WaitForWineServerAsync(wine, launch.Environment, ct).ConfigureAwait(false);
            if (await WaitForFileAsync(launch.PrefixMarker, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
                return;
        }

        var detail = Tail(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        throw new InvalidOperationException(
            $"Wine prefix setup failed (exit {process.ExitCode}).{PrefixFailureHint(launch.Kind, detail)}"
            + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail));
    }

    /// <summary><c>wineserver -w</c> returns once the server has shut down and saved the registry.</summary>
    private static async Task WaitForWineServerAsync(
        string wine, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(wine);
        var beside = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "wineserver");
        var wineserver = beside is not null && File.Exists(beside) ? beside : Which("wineserver");
        if (wineserver is null)
            return;

        var psi = new ProcessStartInfo { FileName = wineserver, UseShellExecute = false };
        psi.ArgumentList.Add("-w");
        foreach (var pair in environment)
            psi.Environment[pair.Key] = pair.Value;

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The file poll below still gets a chance.
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // wineserver could not be started. The file poll below still gets a chance.
        }
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return true;
    }

    public static ProcessStartInfo CreateStartInfo(
        string wine,
        string prefix,
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        bool dxvk)
    {
        var launch = CreateLaunch(wine, prefix, fileName, arguments, dxvk);
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in launch.Arguments)
            psi.ArgumentList.Add(argument);
        foreach (var pair in launch.Environment)
            psi.Environment[pair.Key] = pair.Value;
        return psi;
    }

    public static WineLaunch CreateLaunch(
        string runner,
        string prefix,
        string fileName,
        IEnumerable<string> arguments,
        bool dxvk) =>
        BuildLaunch(runner, prefix, fileName, arguments, dxvk, Which("umu-run"));

    /// <summary>Whether a runner uses a plain Wine prefix, or a Proton or umu one.</summary>
    public static WineRunnerKind KindOf(string runner) =>
        Classify(runner, ResolveProtonScript(runner), Which("umu-run"));

    /// <summary>
    /// <paramref name="umuRun"/> forces the umu binary. Null means a Proton script is invoked directly.
    /// <paramref name="win32"/> overrides the check for 32-bit Wine support.
    /// </summary>
    internal static WineLaunch BuildLaunch(
        string runner,
        string prefix,
        string fileName,
        IEnumerable<string> arguments,
        bool dxvk,
        string? umuRun,
        bool? win32 = null)
    {
        var protonScript = ResolveProtonScript(runner);
        var kind = Classify(runner, protonScript, umuRun);
        var args = new List<string>();
        if (kind == WineRunnerKind.Proton)
            args.Add("run");
        args.Add(fileName);
        args.AddRange(arguments);

        var protonDirectory = protonScript is null ? null : Path.GetDirectoryName(Path.GetFullPath(protonScript));
        return new WineLaunch
        {
            Kind = kind,
            FileName = kind switch
            {
                WineRunnerKind.Umu => IsUmu(runner) ? runner : umuRun!,
                WineRunnerKind.Proton => protonScript!,
                _ => runner
            },
            Arguments = args,
            Environment = LaunchEnvironment(
                kind, prefix, dxvk, protonDirectory,
                kind == WineRunnerKind.Wine && NewWin32Prefix(runner, prefix, win32)),
            PrefixMarker = kind == WineRunnerKind.Proton
                ? Path.Combine(prefix, "pfx", "system.reg")
                : Path.Combine(prefix, "system.reg")
        };
    }

    /// <summary>
    /// Disables the Mono and Gecko installers so <c>wineboot</c> does not wait on a dialog.
    /// DXVK adds a native <c>d3d9</c> override on top of that.
    /// </summary>
    public static Dictionary<string, string> PrefixEnvironment(string? prefix, bool dxvk = false)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WINEDEBUG"] = "-all",
            ["WINEDLLOVERRIDES"] = dxvk ? "mscoree,mshtml=;d3d9=n,b" : "mscoree,mshtml="
        };
        if (!string.IsNullOrWhiteSpace(prefix))
            env["WINEPREFIX"] = prefix;
        return env;
    }

    public static string ToWindowsPath(string path) => UnixToWinePath(Path.GetFullPath(path));

    /// <summary>Turns an absolute Unix path into a Wine <c>Z:\</c> path. Drive-letter paths are kept.</summary>
    internal static string UnixToWinePath(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        if (normalized.Length >= 2 && normalized[1] == ':')
            return normalized.Replace('/', '\\');
        return "Z:\\" + normalized.TrimStart('/').Replace('/', '\\');
    }

    public static string RewriteArgument(string argument)
    {
        if (argument.StartsWith('-'))
            return argument;
        if (argument.Contains('/') || argument.Contains('\\') || Path.IsPathRooted(argument))
            return ToWindowsPath(argument);
        return argument;
    }

    private static WineRunnerKind Classify(string runner, string? protonScript, string? umuRun)
    {
        if (IsUmu(runner))
            return WineRunnerKind.Umu;
        if (protonScript is not null && !string.IsNullOrWhiteSpace(umuRun))
            return WineRunnerKind.Umu;
        if (protonScript is not null)
            return WineRunnerKind.Proton;
        return WineRunnerKind.Wine;
    }

    private static string? ResolveProtonScript(string runner)
    {
        if (IsUmu(runner))
            return null;
        if (IsProtonWrapper(runner))
            return runner;
        return ProtonScriptBeside(runner);
    }

    /// <summary>
    /// Proton ships <c>files/bin/wine</c> or <c>dist/bin/wine</c> next to the <c>proton</c> script.
    /// That wine binary is not a normal win32 Wine.
    /// </summary>
    private static string? ProtonScriptBeside(string wineBinary)
    {
        var bin = Path.GetDirectoryName(wineBinary);
        var filesOrDist = string.IsNullOrEmpty(bin) ? null : Path.GetDirectoryName(bin);
        if (string.IsNullOrEmpty(filesOrDist))
            return null;
        var leaf = Path.GetFileName(filesOrDist);
        if (!leaf.Equals("files", StringComparison.OrdinalIgnoreCase)
            && !leaf.Equals("dist", StringComparison.OrdinalIgnoreCase))
            return null;
        var root = Path.GetDirectoryName(filesOrDist);
        if (string.IsNullOrEmpty(root))
            return null;
        var proton = Path.Combine(root, "proton");
        return File.Exists(proton) ? proton : null;
    }

    /// <summary>
    /// Vanilla 1.12 is a 32-bit game, so a new prefix is win32 when the runner has a 32-bit
    /// loader. <c>wine64</c> and WoW64-only builds cannot make a win32 prefix; they get the
    /// default 64-bit prefix, which still runs 32-bit programs. An existing prefix keeps its
    /// arch, because Wine refuses a <c>WINEARCH</c> that does not match it.
    /// </summary>
    private static bool NewWin32Prefix(string runner, string prefix, bool? win32)
    {
        if (string.IsNullOrWhiteSpace(prefix) || File.Exists(Path.Combine(prefix, "system.reg")))
            return false;
        return win32 ?? HasWin32Support(runner);
    }

    internal static bool HasWin32Support(string runner)
    {
        if (Path.GetFileName(runner).Equals("wine64", StringComparison.OrdinalIgnoreCase))
            return false;

        string real;
        try
        {
            real = File.ResolveLinkTarget(runner, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(runner);
        }
        catch (IOException)
        {
            real = runner;
        }

        var roots = new List<string>();
        var bin = Path.GetDirectoryName(real);
        var root = string.IsNullOrEmpty(bin) ? null : Path.GetDirectoryName(bin);
        if (!string.IsNullOrEmpty(root))
            roots.Add(root);
        // Distro wrappers such as Debian's /usr/bin/wine are scripts, so check /usr as well.
        if (!roots.Contains("/usr"))
            roots.Add("/usr");

        foreach (var dir in roots)
        {
            foreach (var lib in new[] { "lib", "lib32", Path.Combine("lib", "i386-linux-gnu") })
            {
                if (File.Exists(Path.Combine(dir, lib, "wine", "i386-unix", "ntdll.so")))
                    return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> LaunchEnvironment(
        WineRunnerKind kind, string prefix, bool dxvk, string? protonDirectory, bool win32Prefix)
    {
        var env = kind == WineRunnerKind.Wine
            ? PrefixEnvironment(prefix, dxvk)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WINEDEBUG"] = "-all",
                ["WINEDLLOVERRIDES"] = dxvk ? "mscoree,mshtml=;d3d9=n,b" : "mscoree,mshtml="
            };

        switch (kind)
        {
            case WineRunnerKind.Wine:
                if (win32Prefix)
                    env["WINEARCH"] = "win32";
                break;
            case WineRunnerKind.Proton:
                env["STEAM_COMPAT_DATA_PATH"] = prefix;
                env["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = FindSteamClient() ?? protonDirectory ?? prefix;
                break;
            case WineRunnerKind.Umu:
                if (!string.IsNullOrWhiteSpace(prefix))
                    env["WINEPREFIX"] = prefix;
                env["GAMEID"] = "umu-0";
                if (!string.IsNullOrWhiteSpace(protonDirectory))
                    env["PROTONPATH"] = protonDirectory;
                else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROTONPATH")))
                    env["PROTONPATH"] = "GE-Proton"; // umu downloads and updates the latest GE-Proton.
                break;
        }

        return env;
    }

    internal static string? FindSteamClient()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".steam", "root"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            candidates.Add(Path.Combine(xdg, "Steam"));

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    private static string PrefixFailureHint(WineRunnerKind kind, string detail)
    {
        if (detail.Contains("wine32", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("32-bit", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("WINEARCH", StringComparison.OrdinalIgnoreCase))
            return " Vanilla 1.12 needs 32-bit Wine support. Install the 32-bit Wine libraries (wine32), or choose Proton in Settings.";
        if (kind == WineRunnerKind.Proton
            && (detail.Contains("pressure-vessel", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("STEAM_RUNTIME", StringComparison.OrdinalIgnoreCase)))
            return " Official Proton needs the Steam runtime outside Steam. Install umu-run and try again.";
        return "";
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        const int limit = 800;
        return trimmed.Length <= limit ? trimmed : trimmed[^limit..];
    }

    private static string ResolveCommand(string command) => Which(command) ?? command;

    public static string? Which(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (File.Exists(name))
            return Path.GetFullPath(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
