using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindrunnerLauncher.Core.Platform;

public static class PlatformInfo
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static string ExeSuffix => IsWindows ? ".exe" : "";
}

public sealed class OwnedProcess : IDisposable
{
    public Process Process { get; }
    public StreamWriter? Stdin { get; }
    public int Id => Process.Id;
    public string ImagePath { get; }

    public OwnedProcess(Process process, StreamWriter? stdin, string imagePath)
    {
        Process = process;
        Stdin = stdin;
        ImagePath = imagePath;
    }

    public void CloseStdin()
    {
        try { Stdin?.Close(); } catch { }
    }

    public void Kill()
    {
        CloseStdin();
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Dispose()
    {
        CloseStdin();
        Process.Dispose();
    }
}

public static class ProcessHost
{
    public static OwnedProcess StartHidden(
        string fileName,
        string workingDirectory,
        IEnumerable<string>? arguments = null,
        bool captureOutput = false,
        DataReceivedEventHandler? onOutput = null,
        bool holdStdin = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = holdStdin,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };
        if (arguments is not null)
        {
            foreach (var a in arguments)
                psi.ArgumentList.Add(a);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (captureOutput && onOutput is not null)
        {
            process.OutputDataReceived += onOutput;
            process.ErrorDataReceived += onOutput;
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {fileName}");

            StreamWriter? writer = null;
            if (holdStdin)
            {
                writer = process.StandardInput;
                writer.AutoFlush = true;
            }

            if (captureOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            return new OwnedProcess(process, writer, fileName);
        }
        catch
        {
            using var failed = new OwnedProcess(process, null, fileName);
            failed.Kill();
            throw;
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Running process ids whose image is <paramref name="executablePath"/>. On Linux this also
    /// matches Windows programs running under Wine, where the image is the Wine binary and the
    /// executable name only appears in the command line.
    /// </summary>
    public static IReadOnlyList<int> ProcessesByImage(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return [];

        var full = Path.GetFullPath(executablePath);
        var name = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrWhiteSpace(name))
            return [];

        if (OperatingSystem.IsLinux())
            return LinuxProcessesByCommandLine(Path.GetFileName(full));

        var ids = new List<int>();
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (!process.HasExited
                        && !string.IsNullOrWhiteSpace(actual)
                        && string.Equals(Path.GetFullPath(actual), full, StringComparison.OrdinalIgnoreCase))
                        ids.Add(process.Id);
                }
                catch
                {
                    // Access denied or the process exited while it was inspected.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return ids;
    }

    private static IReadOnlyList<int> LinuxProcessesByCommandLine(string fileName)
    {
        var ids = new List<int>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch
        {
            return ids;
        }

        // Wine sets the Linux process name to the Windows executable, but truncates it to 15
        // characters; the command line is only used when its first argument is the image itself,
        // so a loader that merely receives the game name as an argument is not matched.
        var commLimit = Math.Min(fileName.Length, 15);
        var commPrefix = fileName[..commLimit];

        foreach (var dir in entries)
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
                continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm")).TrimEnd('\n');
                if (string.Equals(comm, commPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(pid);
                    continue;
                }

                var commandLine = File.ReadAllText(Path.Combine(dir, "cmdline"));
                var first = commandLine.Split('\0', 2)[0];
                var image = first.Replace('\\', '/');
                if (image.EndsWith('/' + fileName, StringComparison.OrdinalIgnoreCase))
                    ids.Add(pid);
            }
            catch
            {
                // The process exited while /proc was being read.
            }
        }

        return ids;
    }

    public static async Task WaitGoneAsync(int pid, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAlive(pid))
                return;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Tracks a launched process and every process it starts. On Windows a job object is used, so a
/// loader that starts the real game and exits (VanillaFixes) is still tracked until the game ends.
/// The accounting query does not need access to the game process itself, which keeps elevated
/// clients from looking like they exited immediately.
/// </summary>
public sealed class ProcessGroup : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;

    private IntPtr _job;
    private readonly int _rootPid;

    private ProcessGroup(IntPtr job, int rootPid)
    {
        _job = job;
        _rootPid = rootPid;
    }

    /// <summary>Puts <paramref name="process"/> in a job object so its whole tree can be watched.</summary>
    /// <returns><c>null</c> when job tracking is unavailable; callers fall back to PID tracking.</returns>
    public static ProcessGroup? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return null;

        try
        {
            if (AssignProcessToJobObject(job, process.Handle))
                return new ProcessGroup(job, process.Id);
        }
        catch
        {
            // The process exited before it could be assigned, or a parent job disallows nesting.
        }

        CloseHandle(job);
        return null;
    }

    /// <summary>True while the launch or anything it started is still running.</summary>
    public bool AnyAlive()
    {
        if (_job == IntPtr.Zero)
            return ProcessHost.IsAlive(_rootPid);

        // If the query fails, assume the game is alive: restoring the launcher early is worse than
        // leaving it in the tray for a moment longer.
        if (!QueryInformationJobObject(
                _job,
                JobObjectBasicAccountingInformation,
                out var info,
                Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(),
                IntPtr.Zero))
            return true;

        return info.ActiveProcesses > 0;
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // A wrapper can briefly leave the job empty between starting the game and exiting itself.
        var grace = TimeSpan.FromSeconds(2);
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed >= grace && !AnyAlive())
                return;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero)
            return;
        CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

public static class PidFiles
{
    public static void Write(string dataDir, string name, int pid)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, $"{name}.pid"), pid.ToString() + "\n");
    }

    public static int Read(string dataDir, string name)
    {
        var path = Path.Combine(dataDir, $"{name}.pid");
        if (!File.Exists(path))
            return 0;
        return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : 0;
    }

    public static void Remove(string dataDir, string name)
    {
        var path = Path.Combine(dataDir, $"{name}.pid");
        if (File.Exists(path))
            File.Delete(path);
    }
}
