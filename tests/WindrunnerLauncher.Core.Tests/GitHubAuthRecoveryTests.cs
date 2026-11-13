using System.Net;
using System.Text;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Tests;

/// <summary>
/// A stored GitHub token that GitHub no longer accepts used to fail every API call, which the
/// launcher reported as "GitHub unreachable". These cover the recovery path.
/// </summary>
public class GitHubAuthRecoveryTests : IDisposable
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.3",
          "name": "Release 1.2.3",
          "assets": []
        }
        """;

    public GitHubAuthRecoveryTests() => ResetSession();

    public void Dispose() => ResetSession();

    private static void ResetSession()
    {
        GitHubSession.ClearToken();
        GitHubSession.RefreshHook = null;
        GitHubSession.TokenRejectedHook = null;
    }

    [Fact]
    public async Task RejectedToken_IsDropped_AndTheRequestRetriedAnonymously()
    {
        var rejected = 0;
        GitHubSession.AccessToken = "gho_dead_token";
        GitHubSession.TokenRejectedHook = () => rejected++;

        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            // A dead token must not be able to fail the request: the retry goes out without it.
            if (ctx.Request.Headers["Authorization"] is not null)
                return LoopbackHttpServer.WriteAsync(ctx, [], status: 401);
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(ReleaseJson));
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        var release = await releases.LatestAsync("owner/repo");

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release!.TagName);
        Assert.Equal(1, rejected);
        Assert.False(GitHubSession.IsSignedIn);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task RateLimited_ReportsTheLimit_InsteadOfLookingUnreachable()
    {
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            ctx.Response.Headers["X-RateLimit-Reset"] = "1794484043";
            return LoopbackHttpServer.WriteAsync(ctx, [], status: 403);
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        var (release, failure) = await releases.LatestWithFailureAsync("owner/repo");

        Assert.Null(release);
        Assert.NotNull(failure);
        Assert.Equal(HttpStatusCode.Forbidden, failure!.StatusCode);
        Assert.Contains("rate limit", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotFound_SaysTheRepositoryIsMissing()
    {
        using var server = new LoopbackHttpServer((ctx, _) =>
            LoopbackHttpServer.WriteAsync(ctx, [], status: 404));

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        var (release, failure) = await releases.LatestWithFailureAsync("owner/repo");

        Assert.Null(release);
        Assert.Equal(HttpStatusCode.NotFound, failure!.StatusCode);
        Assert.Contains("404", failure.Message);
    }

    [Fact]
    public async Task ExpiredToken_IsRenewedBeforeTheRequestGoesOut()
    {
        var refreshed = 0;
        GitHubSession.AccessToken = "gho_old";
        GitHubSession.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        GitHubSession.RefreshHook = _ =>
        {
            refreshed++;
            GitHubSession.AccessToken = "gho_new";
            GitHubSession.ExpiresAt = DateTimeOffset.UtcNow.AddHours(8);
            return Task.CompletedTask;
        };

        string? sentAuthorization = null;
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            sentAuthorization = ctx.Request.Headers["Authorization"];
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(ReleaseJson));
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        await releases.LatestAsync("owner/repo");

        Assert.Equal(1, refreshed);
        Assert.Equal("Bearer gho_new", sentAuthorization);
    }

    [Fact]
    public async Task FreshToken_IsSentUnchanged()
    {
        GitHubSession.AccessToken = "gho_fresh";
        GitHubSession.ExpiresAt = DateTimeOffset.UtcNow.AddHours(7);
        GitHubSession.RefreshHook = _ =>
        {
            // Mirrors GitHubAuthService: only a token past its expiry is renewed.
            if (GitHubSession.ExpiresAt is { } at && at <= DateTimeOffset.UtcNow)
                GitHubSession.AccessToken = "gho_renewed";
            return Task.CompletedTask;
        };

        string? sentAuthorization = null;
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            sentAuthorization = ctx.Request.Headers["Authorization"];
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(ReleaseJson));
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        await releases.LatestAsync("owner/repo");

        Assert.Equal("Bearer gho_fresh", sentAuthorization);
    }

    [Fact]
    public async Task AnonymousRequest_DoesNotTouchTheRefreshHook()
    {
        var refreshed = 0;
        GitHubSession.RefreshHook = _ =>
        {
            refreshed++;
            return Task.CompletedTask;
        };

        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(ReleaseJson));
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        await releases.LatestAsync("owner/repo");

        Assert.Equal(0, refreshed);
    }

    [Fact]
    public async Task ExpiredTokenWithoutRefreshToken_SignsOut_InsteadOfSendingIt()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        using var auth = new GitHubAuthService(state);

        // A token GitHub says has expired, with nothing stored to renew it.
        GitHubSession.AccessToken = "gho_expired";
        GitHubSession.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var sentAuthorization = false;
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            sentAuthorization = ctx.Request.Headers["Authorization"] is not null;
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(ReleaseJson));
        });

        using var http = new HttpClient(new RewriteToLoopbackHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        await releases.LatestAsync("owner/repo");

        Assert.False(sentAuthorization);
        Assert.True(auth.NeedsReauth);
        Assert.Equal(GitHubSignOutReason.TokenExpired, auth.LastSignOutReason);
    }

    [Fact]
    public void RejectedSignOut_AsksForANewSignIn_UserSignOutDoesNot()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();
        var state = new StateStore(paths);
        using var auth = new GitHubAuthService(state);

        auth.SignOut(GitHubSignOutReason.TokenRejected);
        Assert.True(auth.NeedsReauth);
        Assert.Equal(GitHubSignOutReason.TokenRejected, auth.LastSignOutReason);

        auth.SignOut();
        Assert.False(auth.NeedsReauth);
        Assert.Equal(GitHubSignOutReason.UserRequested, auth.LastSignOutReason);
    }

    private sealed class RewriteToLoopbackHandler(string baseUrl) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var original = request.RequestUri!;
            request.RequestUri = new Uri(new Uri(baseUrl), original.PathAndQuery.TrimStart('/'));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
