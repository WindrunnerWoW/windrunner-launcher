using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public sealed class ModHandlerRegressionTests
{
    [Theory]
    [InlineData(ModKind.Mpq, "Data/patch-A.mpq")]
    [InlineData(ModKind.Dll, "Example.dll")]
    public async Task ApplyRequiredAsync_ReplacesStaleLiveFileFromCachedValidPayload(ModKind kind, string destination)
    {
        using var tmp = new TempDir("handler-required");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var bytes = System.Text.Encoding.UTF8.GetBytes("cached-valid-payload");
        var asset = new ManagedAsset
        {
            Id = "required-" + kind,
            Kind = kind,
            Destination = destination,
            Required = true,
            Sha256 = Checksums.Sha256Hex(bytes)
        };
        ManifestTestData.SaveClient(paths, new ClientManifest { Assets = [asset] });
        var state = new StateStore(paths);
        var client = paths.Client;
        var live = Path.Combine(client, destination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.WriteAllText(live, "stale-live-file");
        var store = paths.ModStore(asset.Id);
        Directory.CreateDirectory(store);
        File.WriteAllBytes(Path.Combine(store, Path.GetFileName(live)), bytes);
        using var downloads = new DownloadManager();
        var manager = new ModManager(paths, state, downloads);

        await manager.ApplyRequiredAsync(state.SelectedRealm());

        Assert.Equal("cached-valid-payload", File.ReadAllText(live));
    }

    [Fact]
    public async Task MpqEnable_ReplacesStaleLiveFile_AndRejectsInvalidPayload()
    {
        using var tmp = new TempDir("handler-mpq");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var asset = new ManagedAsset
        {
            Id = "mpq",
            Kind = ModKind.Mpq,
            Destination = "Data/patch-A.mpq",
            Sha256 = Checksums.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("new"))
        };
        var client = tmp.Dir("client");
        var store = paths.ModStore(asset.Id);
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(Path.Combine(client, "Data"));
        File.WriteAllText(Path.Combine(client, "Data", "patch-A.mpq"), "old");
        File.WriteAllText(Path.Combine(store, "patch-A.mpq"), "new");
        var ctx = Context(paths, state, asset, client, store);

        var result = await new MpqHandler().EnableAsync(ctx);

        Assert.True(result.Changed);
        Assert.Equal("new", File.ReadAllText(ctx.LivePath));
        File.WriteAllText(Path.Combine(store, "patch-A.mpq"), "bad");
        File.WriteAllText(ctx.LivePath, "keep");
        result = await new MpqHandler().EnableAsync(ctx);
        Assert.False(result.Changed);
        Assert.Equal("keep", File.ReadAllText(ctx.LivePath));
    }

    [Fact]
    public async Task DllEnable_ReplacesStaleLiveFile_AndRejectsInvalidPayload()
    {
        using var tmp = new TempDir("handler-dll");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var asset = new ManagedAsset
        {
            Id = "dll",
            Kind = ModKind.Dll,
            Destination = "Example.dll",
            Sha256 = Checksums.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("new"))
        };
        var client = tmp.Dir("client");
        var store = paths.ModStore(asset.Id);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(client, "Example.dll"), "old");
        File.WriteAllText(Path.Combine(store, "Example.dll"), "new");
        var ctx = Context(paths, state, asset, client, store);

        var result = await new DllHandler().EnableAsync(ctx);

        Assert.True(result.Changed);
        Assert.Equal("new", File.ReadAllText(ctx.LivePath));
        File.WriteAllText(Path.Combine(store, "Example.dll"), "bad");
        File.WriteAllText(ctx.LivePath, "keep");
        result = await new DllHandler().EnableAsync(ctx);
        Assert.False(result.Changed);
        Assert.Equal("keep", File.ReadAllText(ctx.LivePath));
    }

    [Fact]
    public async Task ZipRoot_TracksManifestPerClientDirectory()
    {
        using var tmp = new TempDir("handler-zip");
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        var asset = new ManagedAsset { Id = "zip", Kind = ModKind.ZipRoot };
        var store = paths.ModStore(asset.Id);
        var source = tmp.Dir("source");
        File.WriteAllText(Path.Combine(source, "shared.txt"), "payload");
        ArchiveUtil.ZipDirectory(source, Path.Combine(store, "release.zip"));
        var a = tmp.Dir("client-a");
        var b = tmp.Dir("client-b");
        var handler = new ZipRootHandler();

        await handler.EnableAsync(Context(paths, state, asset, a, store));
        await handler.EnableAsync(Context(paths, state, asset, b, store));
        await handler.DisableAsync(Context(paths, state, asset, b, store));

        Assert.True(File.Exists(Path.Combine(a, "shared.txt")));
        Assert.False(File.Exists(Path.Combine(b, "shared.txt")));
        Assert.True(handler.IsApplied(Context(paths, state, asset, a, store)));
        await handler.DisableAsync(Context(paths, state, asset, a, store));
        Assert.False(File.Exists(Path.Combine(a, "shared.txt")));
    }

    private static ModApplyContext Context(
        LauncherPaths paths, StateStore state, ManagedAsset asset, string client, string store) => new()
        {
            Paths = paths,
            State = state,
            Asset = asset,
            ClientDir = client,
            StoreDir = store
        };
}
