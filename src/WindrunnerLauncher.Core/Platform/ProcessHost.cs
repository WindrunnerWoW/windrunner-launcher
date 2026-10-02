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
