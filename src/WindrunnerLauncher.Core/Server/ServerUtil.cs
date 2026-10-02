using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Shared filesystem, SQL, and process helpers for the portable server tree.
/// </summary>
internal static class ServerUtil
{
    internal static readonly string[] MapDirs = ["dbc", "maps", "vmaps", "mmaps"];

    internal static IEnumerable<string> BinaryCandidates(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return name;
            yield return name[..^4];
            yield break;
        }

        yield return name;
        yield return name + ".exe";
    }

    internal static string? FindExisting(string directory, params string[] names)
    {
        if (!Directory.Exists(directory))
            return null;
        foreach (var name in names)
        {
            foreach (var candidate in BinaryCandidates(name))
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    internal static bool FileExistsInsensitive(string directory, string name)
    {
        if (!Directory.Exists(directory))
            return false;
        foreach (var candidate in BinaryCandidates(name))
        {
            if (File.Exists(Path.Combine(directory, candidate)))
                return true;
        }

        return false;
    }

    internal static string IniPath(string path) => path.Replace('\\', '/');

    internal static string SqlLiteral(string value)
    {
        if (value.Contains('\0'))
            throw new InvalidOperationException("SQL values may not contain NUL bytes.");
        return "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    }

    internal static string QuoteCnf(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static bool DirHasFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        return Directory.EnumerateFileSystemEntries(directory).Any();
    }

    internal static bool MapsPresent(string mapsDir)
    {
        foreach (var name in MapDirs)
        {
            var dir = Path.Combine(mapsDir, name);
            if (!Directory.Exists(dir) || !DirHasFiles(dir))
                return false;
        }

        return true;
    }

    internal static void AssertMapsPresent(string mapsDir)
    {
        foreach (var name in MapDirs)
        {
            var dir = Path.Combine(mapsDir, name);
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"Missing maps/{name}. Run setup or Fetch maps, and provide the four client-data directories.");
            if (!DirHasFiles(dir))
                throw new InvalidOperationException($"maps/{name} is empty. Setup requires dbc, maps, vmaps, and mmaps to contain data.");
        }
    }

    internal static bool TcpPortInUse(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    internal static bool ProcessMatchesImage(int pid, string expectedPath)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(expectedPath))
            return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
                return false;
        }
        catch
        {
            return false;
        }

        var expectedFull = Path.GetFullPath(expectedPath);
        var procExe = $"/proc/{pid}/exe";
        if (File.Exists(procExe))
        {
            try
            {
                var target = File.ResolveLinkTarget(procExe, returnFinalTarget: true)?.FullName
                             ?? new FileInfo(procExe).LinkTarget;
                if (!string.IsNullOrWhiteSpace(target)
                    && string.Equals(Path.GetFullPath(target), expectedFull, PathComparison))
                    return true;
            }
            catch
            {
                // fall through to MainModule
            }
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var actual = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(actual))
                return string.Equals(Path.GetFullPath(actual), expectedFull, PathComparison);
        }
        catch
        {
            // Do not fall back to process-name matching: that can target a foreign mysqld.
        }

        return false;
    }

    internal static IReadOnlyList<int> ProcessesByImage(string imagePath)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath);
        if (string.IsNullOrWhiteSpace(name))
            return [];
        var ids = new List<int>();
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited && ProcessMatchesImage(process.Id, imagePath))
                        ids.Add(process.Id);
                }
                catch
                {
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

    internal static bool LooksLikeZip(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64)
                return false;
            using var fs = File.OpenRead(path);
            Span<byte> mag = stackalloc byte[2];
            if (fs.Read(mag) < 2)
                return false;
            return mag[0] == 0x50 && mag[1] == 0x4B;
        }
        catch
        {
            return false;
        }
    }

    internal static void WriteMarker(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value + "\n");
    }

    internal static string ReadTrimmed(string path)
    {
        if (!File.Exists(path))
            return "";
        return File.ReadAllText(path).Trim();
    }

    internal static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    internal static void MoveReplace(string source, string dest)
    {
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        if (File.Exists(dest))
            File.Delete(dest);
        try
        {
            if (Directory.Exists(source))
                Directory.Move(source, dest);
            else
                File.Move(source, dest);
        }
        catch
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, dest);
                Directory.Delete(source, recursive: true);
            }
            else
            {
                File.Copy(source, dest, overwrite: true);
                File.Delete(source);
            }
        }
    }

    internal static void MoveFileReplace(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
            File.Delete(dest);
        try
        {
            File.Move(source, dest);
        }
        catch
        {
            File.Copy(source, dest, overwrite: true);
            File.Delete(source);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void MakeUserExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A missing execute bit shows up as a failed start.
        }
    }

    internal static void SetOwnerOnlyFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static string Sha256Hex(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static readonly Regex AccountName = new(@"^[A-Za-z0-9]{2,16}$", RegexOptions.Compiled);
}
