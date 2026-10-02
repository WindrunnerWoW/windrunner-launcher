using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Downloads;

/// <summary>
/// Owns GitHub Device Flow sign-in end to end: kicks off the flow, polls for approval, persists
/// the token (DPAPI-protected for the current Windows user) and populates <see cref="GitHubSession"/>
/// so every <see cref="GitHubReleases"/> call in the app immediately benefits from the
/// authenticated rate limit (5,000/hour) instead of the shared anonymous 60/hour-per-IP limit.
/// </summary>
public sealed class GitHubAuthService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindrunnerLauncher.GitHubToken.v1");

    private readonly StateStore _state;
    private readonly GitHubDeviceAuth _deviceAuth;

    public event Action? Changed;

    public GitHubAuthService(StateStore state)
    {
        _state = state;
        _deviceAuth = new GitHubDeviceAuth(SharedHttp);
        LoadFromSettings();
    }

    public bool IsConfigured => GitHubOAuthConfig.IsConfigured;
    public bool IsSignedIn => GitHubSession.IsSignedIn;
    public string? Login => GitHubSession.Login;

    public Task<GitHubDeviceCode> BeginSignInAsync(CancellationToken ct = default) =>
        _deviceAuth.StartAsync(ct);

    /// <summary>Polls until the user approves the code on github.com, denies it, or it expires.</summary>
    public async Task<GitHubDevicePollResult> CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var result = await _deviceAuth.PollAsync(code, ct).ConfigureAwait(false);
        if (result.Status != GitHubDevicePollStatus.Success || string.IsNullOrWhiteSpace(result.AccessToken))
            return result;

        var login = await _deviceAuth.FetchLoginAsync(result.AccessToken, ct).ConfigureAwait(false);

        GitHubSession.AccessToken = result.AccessToken;
        GitHubSession.Login = login;
        _state.Settings.GitHubLogin = login;

        if (OperatingSystem.IsWindows())
            _state.Settings.GitHubTokenProtected = Protect(result.AccessToken);
        else
            SaveTokenFile(result.AccessToken);

        _state.SaveSettings();
        Changed?.Invoke();
        return result;
    }

    public void SignOut()
    {
        GitHubSession.AccessToken = null;
        GitHubSession.Login = null;
        _state.Settings.GitHubTokenProtected = null;
        _state.Settings.GitHubLogin = null;
        _state.SaveSettings();
        try
        {
            if (File.Exists(_state.GitHubTokenFile))
                File.Delete(_state.GitHubTokenFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory session is already cleared. A leftover file is signed out on the next failed read.
        }

        Changed?.Invoke();
    }

    private void LoadFromSettings()
    {
        var token = OperatingSystem.IsWindows() ? Unprotect(_state.Settings.GitHubTokenProtected) : ReadTokenFile();
        if (string.IsNullOrWhiteSpace(token))
            return;
        GitHubSession.AccessToken = token;
        GitHubSession.Login = _state.Settings.GitHubLogin;
    }

    /// <summary>Mode 0600 file under the data directory. The token is never written to settings.json.</summary>
    private void SaveTokenFile(string token)
    {
        var path = _state.GitHubTokenFile;
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

    private string? ReadTokenFile()
    {
        var path = _state.GitHubTokenFile;
        if (!File.Exists(path))
            return null;
        var token = File.ReadAllText(path).Trim();
        return token.Length == 0 ? null : token;
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
}
