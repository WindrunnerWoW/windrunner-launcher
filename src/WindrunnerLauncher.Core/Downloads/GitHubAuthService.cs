using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Downloads;

/// <summary>
/// Owns GitHub Device Flow sign-in end to end: kicks off the flow, polls for approval, persists
/// the token (DPAPI-protected for the current Windows user) and populates <see cref="GitHubSession"/>
/// so every <see cref="GitHubReleases"/> call in the app immediately benefits from the
/// authenticated rate limit (5,000/hour) instead of the shared anonymous 60/hour-per-IP limit.
///
/// A stored token is renewed through its refresh token when GitHub expires it, and dropped the
/// moment GitHub rejects it. Leaving a dead token in place is what made every update check look
/// like a network outage: the token is attached to every request, so GitHub answers 401 instead of
/// serving the anonymous response.
/// </summary>
public sealed class GitHubAuthService : IDisposable
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindrunnerLauncher.GitHubToken.v1");

    /// <summary>Renew this long before GitHub's stated expiry, so a request never races the deadline.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly StateStore _state;
    private readonly GitHubDeviceAuth _deviceAuth;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<CancellationToken, Task> _refreshHook;
    private readonly Action _tokenRejectedHook;
    private bool _disposed;

    public event Action? Changed;

    public GitHubAuthService(StateStore state)
    {
        _state = state;
        _deviceAuth = new GitHubDeviceAuth(SharedHttp);
        LoadFromSettings();

        _refreshHook = EnsureFreshAsync;
        _tokenRejectedHook = OnTokenRejected;
        GitHubSession.RefreshHook = _refreshHook;
        GitHubSession.TokenRejectedHook = _tokenRejectedHook;
    }

    public bool IsConfigured => GitHubOAuthConfig.IsConfigured;
    public bool IsSignedIn => GitHubSession.IsSignedIn;
    public string? Login => GitHubSession.Login;

    /// <summary>
    /// True when a stored sign-in was dropped because GitHub rejected or expired it, rather than
    /// because the user signed out. The UI offers a fresh sign-in while this is set.
    /// </summary>
    public bool NeedsReauth { get; private set; }

    public GitHubSignOutReason? LastSignOutReason { get; private set; }

    public Task<GitHubDeviceCode> BeginSignInAsync(CancellationToken ct = default) =>
        _deviceAuth.StartAsync(ct);

    /// <summary>Polls until the user approves the code on github.com, denies it, or it expires.</summary>
    public async Task<GitHubDevicePollResult> CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var result = await _deviceAuth.PollAsync(code, ct).ConfigureAwait(false);
        if (result.Status != GitHubDevicePollStatus.Success || string.IsNullOrWhiteSpace(result.AccessToken))
            return result;

        var login = await _deviceAuth.FetchLoginAsync(result.AccessToken, ct).ConfigureAwait(false);

        GitHubSession.Login = login;
        _state.Settings.GitHubLogin = login;
        ApplyToken(result.AccessToken, result.RefreshToken, result.ExpiresAt);

        NeedsReauth = false;
        LastSignOutReason = null;
        Changed?.Invoke();
        return result;
    }

    public void SignOut() => SignOut(GitHubSignOutReason.UserRequested);

    public void SignOut(GitHubSignOutReason reason)
    {
        GitHubSession.ClearToken();
        _state.Settings.GitHubTokenProtected = null;
        _state.Settings.GitHubRefreshTokenProtected = null;
        _state.Settings.GitHubTokenExpiresAt = null;
        _state.Settings.GitHubLogin = null;
        _state.SaveSettings();

        DeleteTokenFile(_state.GitHubTokenFile);
        DeleteTokenFile(_state.GitHubRefreshTokenFile);

        NeedsReauth = reason != GitHubSignOutReason.UserRequested;
        LastSignOutReason = reason;
        Changed?.Invoke();
    }

    /// <summary>
    /// Renews the access token when GitHub told us it expires, so a request never carries a token
    /// we already know is dead. A refresh that GitHub refuses signs the user out instead of leaving
    /// a broken token in place.
    /// </summary>
    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (_disposed || !GitHubSession.IsSignedIn || !NeedsRefresh())
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have renewed the token while this one waited for the gate.
            if (_disposed || !GitHubSession.IsSignedIn || !NeedsRefresh())
                return;

            var refreshToken = OperatingSystem.IsWindows()
                ? Unprotect(_state.Settings.GitHubRefreshTokenProtected)
                : ReadTokenFile(_state.GitHubRefreshTokenFile);

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                SignOut(GitHubSignOutReason.TokenExpired);
                return;
            }

            GitHubDevicePollResult? renewed;
            try
            {
                renewed = await _deviceAuth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Offline. Keep the token and try again on the next request rather than signing out.
                return;
            }

            if (renewed is null || string.IsNullOrWhiteSpace(renewed.AccessToken))
            {
                SignOut(GitHubSignOutReason.TokenExpired);
                return;
            }

            ApplyToken(renewed.AccessToken, renewed.RefreshToken, renewed.ExpiresAt);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool NeedsRefresh() =>
        GitHubSession.ExpiresAt is { } expiresAt && expiresAt - RefreshSkew <= DateTimeOffset.UtcNow;

    /// <summary>GitHub answered 401 for a token we sent: drop it so the retry goes out anonymously.</summary>
    private void OnTokenRejected()
    {
        if (_disposed)
            return;
        SignOut(GitHubSignOutReason.TokenRejected);
    }

    private void LoadFromSettings()
    {
        var token = OperatingSystem.IsWindows()
            ? Unprotect(_state.Settings.GitHubTokenProtected)
            : ReadTokenFile(_state.GitHubTokenFile);
        if (string.IsNullOrWhiteSpace(token))
            return;

        GitHubSession.AccessToken = token;
        GitHubSession.Login = _state.Settings.GitHubLogin;
        GitHubSession.ExpiresAt = _state.Settings.GitHubTokenExpiresAt;
    }

    private void ApplyToken(string accessToken, string? refreshToken, DateTimeOffset? expiresAt)
    {
        GitHubSession.AccessToken = accessToken;
        GitHubSession.ExpiresAt = expiresAt;
        _state.Settings.GitHubTokenExpiresAt = expiresAt;

        if (OperatingSystem.IsWindows())
        {
            _state.Settings.GitHubTokenProtected = Protect(accessToken);
            _state.Settings.GitHubRefreshTokenProtected =
                string.IsNullOrWhiteSpace(refreshToken) ? null : Protect(refreshToken);
        }
        else
        {
            SaveTokenFile(_state.GitHubTokenFile, accessToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
                DeleteTokenFile(_state.GitHubRefreshTokenFile);
            else
                SaveTokenFile(_state.GitHubRefreshTokenFile, refreshToken);
        }

        _state.SaveSettings();
    }

    /// <summary>Mode 0600 file under the data directory. The token is never written to settings.json.</summary>
    private static void SaveTokenFile(string path, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(tmp, token);
        }
        else
        {
            // Created 0600. A later chmod would leave the token world-readable under a typical umask.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(token);
        }

        File.Move(tmp, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string? ReadTokenFile(string path)
    {
        if (!File.Exists(path))
            return null;
        var token = File.ReadAllText(path).Trim();
        return token.Length == 0 ? null : token;
    }

    private static void DeleteTokenFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory session is already cleared. A leftover file is signed out on the next failed read.
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Protect(string plainText)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    [SupportedOSPlatform("windows")]
    private static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Protected under a different user profile (e.g. settings.json copied between
            // machines) or corrupt; treat as signed out rather than crashing startup.
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (GitHubSession.RefreshHook == _refreshHook)
            GitHubSession.RefreshHook = null;
        if (GitHubSession.TokenRejectedHook == _tokenRejectedHook)
            GitHubSession.TokenRejectedHook = null;

        // _refreshGate is deliberately not disposed: a request already in flight may still be
        // waiting on it, and SemaphoreSlim holds no unmanaged state unless its wait handle is used.
    }
}
