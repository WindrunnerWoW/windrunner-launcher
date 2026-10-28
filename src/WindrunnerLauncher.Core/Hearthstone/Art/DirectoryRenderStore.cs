using System.Collections.Concurrent;
using WindrunnerLauncher.Core.Hearthstone.Imaging;

namespace WindrunnerLauncher.Core.Hearthstone.Art;

/// <summary>
/// Renders cached on disk the way the module's pipeline caches them: &lt;dir&gt;/&lt;id&gt;.png for the 256x renders and
/// &lt;dir&gt;/512/&lt;id&gt;.png for the 512x ones. With <paramref name="keepDecoded"/> decoded images are kept;
/// every call returns a copy.
/// </summary>
public sealed class DirectoryRenderStore(string directory, bool keepDecoded = true) : IRenderStore
{
    private readonly ConcurrentDictionary<string, PilImage> _decoded = new();

    public string Directory { get; } = directory;

    public string LoPath(string id) => Path.Combine(Directory, id + ".png");
    public string HiPath(string id) => Path.Combine(Directory, "512", id + ".png");

    public bool HasLo(string id) => File.Exists(LoPath(id)) && new FileInfo(LoPath(id)).Length > 0;

    public PilImage LoadLo(string id) => Load(LoPath(id));

    public PilImage LoadHi(string id) => Load(HiPath(id));

    private PilImage Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Card render missing from the download cache.", path);
        return keepDecoded ? _decoded.GetOrAdd(path, ImageCodec.LoadRgba).Copy() : ImageCodec.LoadRgba(path);
    }

    /// <summary>Drops the decoded images (they are about 1.5 MB each at 512x).</summary>
    public void Forget() => _decoded.Clear();
}
