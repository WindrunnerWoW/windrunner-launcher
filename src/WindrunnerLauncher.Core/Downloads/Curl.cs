using System.ComponentModel;
using System.Diagnostics;

namespace WindrunnerLauncher.Core.Downloads;

/// <summary>
/// Runs the system curl. Only used where a host rejects .NET's TLS fingerprint; see
/// <see cref="FuckingFast"/>. Windows 10 1803+ ships curl.exe, and Linux distributions and the
/// AppImage base image carry it too.
/// </summary>
internal static class Curl
{
    /// <summary>Cloudflare serves the page only to a request that looks like a top-level navigation.</summary>
    private static readonly string[] BrowserHeaders =
    [
        "-H", "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        "-H", "Accept-Language: en-US,en;q=0.9",
        "-H", "Sec-Fetch-Dest: document",
        "-H", "Sec-Fetch-Mode: navigate",
        "-H", "Sec-Fetch-Site: none",
        "-H", "Sec-Fetch-User: ?1",
        "-H", "Upgrade-Insecure-Requests: 1"
    ];

    private static string Executable => OperatingSystem.IsWindows() ? "curl.exe" : "curl";

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>Body of <paramref name="url"/>.</summary>
    public static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(
            ["-sS", "--max-time", "30", "-A", FuckingFast.UserAgent, .. BrowserHeaders, url], ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"curl failed with exit code {exit}: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>The <c>Location</c> header of <paramref name="url"/>, without following it.</summary>
    public static async Task<string> RedirectUrlAsync(string url, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunAsync(
            ["-sS", "--max-time", "30", "-o", NullDevice, "-w", "%{redirect_url}", "-A", FuckingFast.UserAgent, .. BrowserHeaders, url],
            ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"curl failed with exit code {exit}: {stderr.Trim()}");
        return stdout.Trim();
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("curl could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"curl is required to resolve this download link but was not found ({Executable}).", ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }
}
