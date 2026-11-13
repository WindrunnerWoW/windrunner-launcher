using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Downloads;

public sealed class GitHubDeviceCode
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

public sealed class GitHubAccessTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }

    /// <summary>
    /// Only present when the OAuth App expires user tokens. Without it an expired token can only be
    /// replaced by signing in again.
    /// </summary>
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    /// <summary>Lifetime of <see cref="AccessToken"/> in seconds, when the app expires tokens.</summary>
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
}

public sealed class GitHubUserResponse
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}

public enum GitHubDevicePollStatus { Success, Pending, Denied, Expired, Error }

public sealed class GitHubDevicePollResult
{
    public GitHubDevicePollStatus Status { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }

    /// <summary>When <see cref="AccessToken"/> stops working, or null when it does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// GitHub OAuth Device Flow sign-in. No client secret is involved — only a public Client ID — so
/// this can run entirely from a distributed desktop app: the user visits a short GitHub URL,
/// enters an 8-character code, and this class polls until they approve it.
/// See https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow
/// </summary>
public sealed class GitHubDeviceAuth
{
    private readonly HttpClient _http;

    public GitHubDeviceAuth(HttpClient http) => _http = http;

    public async Task<GitHubDeviceCode> StartAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubOAuthConfig.ClientId
        });

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.GitHubDeviceCode)
               ?? throw new InvalidDataException("GitHub did not return a device code.");
    }

    /// <summary>Polls until the user finishes authorizing on github.com, the code expires, or they deny it.</summary>
    public async Task<GitHubDevicePollResult> PollAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(code.Interval, 5));
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubOAuthConfig.ClientId,
                ["device_code"] = code.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.GitHubAccessTokenResponse);

            if (!string.IsNullOrWhiteSpace(payload?.AccessToken))
                return FromTokenResponse(payload, fallbackRefreshToken: null);

            switch (payload?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = TimeSpan.FromSeconds(Math.Max(payload.Interval ?? 5, (int)interval.TotalSeconds + 5));
                    continue;
                case "expired_token":
                    return new GitHubDevicePollResult
                    {
                        Status = GitHubDevicePollStatus.Expired,
                        Message = "The code expired before it was entered on GitHub."
                    };
                case "access_denied":
                    return new GitHubDevicePollResult { Status = GitHubDevicePollStatus.Denied, Message = "Sign-in was cancelled." };
                default:
                    return new GitHubDevicePollResult
                    {
                        Status = GitHubDevicePollStatus.Error,
                        Message = payload?.ErrorDescription ?? "GitHub sign-in failed."
                    };
            }
        }

        return new GitHubDevicePollResult
        {
            Status = GitHubDevicePollStatus.Expired,
            Message = "The code expired before it was entered on GitHub."
        };
    }

    /// <summary>
    /// Exchanges a refresh token for a new access token. Null when GitHub refuses, which means the
    /// user has to sign in again. Only OAuth apps that expire user tokens issue refresh tokens.
    /// </summary>
    public async Task<GitHubDevicePollResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = GitHubOAuthConfig.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.GitHubAccessTokenResponse);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            return null;

        // GitHub may rotate the refresh token; keep the old one when it does not send a new one.
        return FromTokenResponse(payload, fallbackRefreshToken: refreshToken);
    }

    private static GitHubDevicePollResult FromTokenResponse(
        GitHubAccessTokenResponse payload, string? fallbackRefreshToken) =>
        new()
        {
            Status = GitHubDevicePollStatus.Success,
            AccessToken = payload.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(payload.RefreshToken) ? fallbackRefreshToken : payload.RefreshToken,
            ExpiresAt = payload.ExpiresIn is > 0
                ? DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn.Value)
                : null
        };

    public async Task<string?> FetchLoginAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("WindrunnerLauncher/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.GitHubUserResponse)?.Login;
    }
}
