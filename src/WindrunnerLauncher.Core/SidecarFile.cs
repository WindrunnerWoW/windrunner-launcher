namespace WindrunnerLauncher.Core;

/// <summary>
/// Writes a file beside the launcher unless an equal-length copy is already there.
/// NativeAOT cannot <c>LoadLibrary</c> Skia/ANGLE from a resource; they have to exist as real DLLs.
/// </summary>
public static class SidecarFile
{
    /// <summary>
    /// Returns <see langword="true"/> when the file was written. Equal length is treated as
    /// already up to date so a second start (and a self-update that did not change Skia) is a
    /// no-op. A sharing violation from the outgoing process during self-update is ignored when
    /// the existing file is already the right size.
    /// </summary>
    public static bool Ensure(string destPath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("Destination path is required.", nameof(destPath));

        var full = Path.GetFullPath(destPath);
        if (IsPresentWithLength(full, content.Length))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllBytes(tmp, content);
        try
        {
            File.Move(tmp, full, overwrite: true);
            return true;
        }
        catch (IOException) when (IsPresentWithLength(full, content.Length))
        {
            TryDelete(tmp);
            return false;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static bool IsPresentWithLength(string path, long length)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == length;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
