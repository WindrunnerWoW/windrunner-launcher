using System.Net;
using System.Reflection;
using System.Text.Json;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

/// <summary>A unique temporary directory that is deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string? prefix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"launcher-tests-{prefix ?? "t"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string File(string relative, string? contents = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, contents ?? "");
        return full;
    }

    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    public LauncherPaths Paths() => new(Path);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal static class ManifestTestData
{
    private const string CatalogResourceName = "WindrunnerLauncher.Core.Tests.Fixtures.client-catalog.json";

    public static void SaveDefaultClientCatalog(LauncherPaths paths) => SaveClient(paths, LoadDefaultClientCatalog());

    public static void SaveClient(LauncherPaths paths, ClientManifest manifest)
    {
        Directory.CreateDirectory(paths.ManifestCache);
        var payload = JsonSerializer.Serialize(manifest, JsonStore.Options);
        var envelope = DevSigningKeys.SignEnvelope(TrustDomain.Client, payload);
        JsonStore.Save(Path.Combine(paths.ManifestCache, ModManager.CachedEnvelopeFileName), envelope);
    }

    private static ClientManifest LoadDefaultClientCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CatalogResourceName)
                           ?? throw new InvalidOperationException($"Missing test catalog resource {CatalogResourceName}.");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<ClientManifest>(reader.ReadToEnd(), JsonStore.Options)
               ?? throw new InvalidOperationException("The test client catalog is invalid.");
    }
}

/// <summary>
/// Minimal loopback HTTP server built on <see cref="HttpListener"/>. Every request is routed to a
/// single asynchronous handler so tests can script failures, slow bodies, and redirects without
/// touching the network.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<HttpListenerContext, CancellationToken, Task> _handler;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public int RequestCount => Volatile.Read(ref _requests);
    private int _requests;

    public LoopbackHttpServer(Func<HttpListenerContext, CancellationToken, Task> handler)
    {
        _handler = handler;
        (_listener, Port) = Bind();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Url(string path = "file.bin") => BaseUrl + path.TrimStart('/');

    private static (HttpListener, int) Bind()
    {
        var rng = new Random();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var port = rng.Next(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Could not bind a loopback HttpListener.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _requests);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _handler(ctx, _cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Handlers that throw simply drop the connection, which is a valid failure mode to test.
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { /* already closed */ }
                }
            });
        }
    }

    public static async Task WriteAsync(HttpListenerContext ctx, byte[] body, int status = 200, bool sendLength = true)
    {
        ctx.Response.StatusCode = status;
        if (sendLength)
            ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        await ctx.Response.OutputStream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

public static class TestData
{
    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
