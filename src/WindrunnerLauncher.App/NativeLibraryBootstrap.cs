using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindrunnerLauncher.Core;

namespace WindrunnerLauncher.App;

/// <summary>
/// NativeAOT still loads Skia and HarfBuzz from disk. The publish output embeds the natives for
/// the target OS and this unpacks them before Avalonia starts. A writable directory beside the
/// executable is preferred. On Linux, a read-only install falls back to the data directory.
/// </summary>
internal static class NativeLibraryBootstrap
{
    internal static readonly string[] WindowsFileNames =
    [
        "libSkiaSharp.dll",
        "libHarfBuzzSharp.dll",
        "av_libglesv2.dll",
    ];

    internal static readonly string[] LinuxFileNames =
    [
        "libSkiaSharp.so",
        "libHarfBuzzSharp.so",
    ];

    public static void EnsureBesideExecutable()
    {
        var exeDir = ExecutableDirectory();
        var dir = NativeDirectory(exeDir);
        var asm = typeof(NativeLibraryBootstrap).Assembly;
        foreach (var name in OperatingSystem.IsLinux() ? LinuxFileNames : WindowsFileNames)
        {
            var path = Path.Combine(dir, name);
            SidecarFile.Ensure(path, ReadEmbedded(asm, name));
            if (OperatingSystem.IsLinux())
                MarkExecutable(path);
        }

        if (OperatingSystem.IsLinux())
            RegisterLinuxResolvers(dir);
    }

    private static string ExecutableDirectory()
    {
        var exe = Environment.ProcessPath;
        var dir = !string.IsNullOrWhiteSpace(exe)
            ? Path.GetDirectoryName(exe)
            : AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("Cannot resolve the launcher directory to unpack native libraries.");
        return dir;
    }

    private static string NativeDirectory(string exeDir)
    {
        if (!OperatingSystem.IsLinux() || CanWrite(exeDir))
            return exeDir;

        var fallback = Path.Combine(LauncherPaths.FromExecutable().Root, "native");
        Directory.CreateDirectory(fallback);
        if (!CanWrite(fallback))
            throw new InvalidOperationException(
                $"Cannot unpack Skia next to the launcher ({exeDir}) or under {fallback}.");
        return fallback;
    }

    private static bool CanWrite(string dir)
    {
        var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(probe); } catch { /* the probe may not exist */ }
            return false;
        }
    }

    private static void RegisterLinuxResolvers(string dir)
    {
        // typeof loads the assembly without running type initializers, so the resolver is in place
        // before the first P/Invoke. Returning zero keeps the runtime's default search.
        Register(typeof(SkiaSharp.SKBitmap).Assembly, dir);
        Register(typeof(HarfBuzzSharp.Buffer).Assembly, dir);
    }

    private static void Register(Assembly assembly, string dir)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
            {
                var path = ResolveLibrary(dir, libraryName);
                return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
            });
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered. Default probing still sees libraries beside the exe.
        }
    }

    private static string? ResolveLibrary(string dir, string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
            return null;

        var names = new List<string> { libraryName };
        if (!libraryName.EndsWith(".so", StringComparison.Ordinal))
            names.Add(libraryName + ".so");
        if (!libraryName.StartsWith("lib", StringComparison.Ordinal))
            names.Add("lib" + libraryName + ".so");

        foreach (var name in names)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static void MarkExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // dlopen does not require the execute bit. A failure here is not fatal.
        }
    }

    private static byte[] ReadEmbedded(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(
                $"The launcher is missing the embedded native library '{name}'. Rebuild for this operating system so the matching Skia natives are packed in.");
        using var ms = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
