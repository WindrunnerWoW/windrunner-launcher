using System.Reflection;
using System.Text;

namespace WindrunnerLauncher.Core;

public static class EmbeddedResources
{
    public static Stream Open(string relativeName)
    {
        var asm = typeof(EmbeddedResources).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(relativeName.Replace('/', '.').Replace('\\', '.'), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded resource not found: {relativeName}");
        return asm.GetManifestResourceStream(name)
               ?? throw new FileNotFoundException($"Embedded resource stream missing: {name}");
    }

    public static string ReadText(string relativeName)
    {
        using var s = Open(relativeName);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static byte[] ReadBytes(string relativeName)
    {
        using var s = Open(relativeName);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static void ExtractTo(string relativeName, string destPath, bool overwrite = false)
    {
        if (File.Exists(destPath) && !overwrite)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllBytes(destPath, ReadBytes(relativeName));
    }
}
