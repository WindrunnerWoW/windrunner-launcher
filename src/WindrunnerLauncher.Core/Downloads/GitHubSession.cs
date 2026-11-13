namespace WindrunnerLauncher.Core.Downloads;

/// <summary>Why a stored GitHub sign-in was dropped.</summary>
public enum GitHubSignOutReason
{
    /// <summary>The user pressed Sign out.</summary>
    UserRequested,

    /// <summary>GitHub answered 401 for the stored token: revoked, expired, or from another account.</summary>
    TokenRejected,

    /// <summary>The token expired and the refresh token could not renew it.</summary>
    TokenExpired
}

/// <summary>
/// Process-wide GitHub auth state. Every subsystem that talks to the GitHub API (server updates,
/// launcher self-update, mod release resolution, server binary fetch) constructs its own
/// <see cref="GitHubReleases"/> over its own <see cref="HttpClient"/>, so a single static holder is
/// the simplest way to make a signed-in token apply everywhere at once, instead of threading it
/// through every constructor.
/// </summary>
public static class GitHubSession
{
    public static string? AccessToken { get; internal set; }
    public static string? Login { get; internal set; }

    /// <summary>
    /// When the access token stops working, as reported by GitHub. Null means GitHub did not say,
    /// which is the case for OAuth apps that do not expire user tokens.
    /// </summary>
    public static DateTimeOffset? ExpiresAt { get; internal set; }

    public static bool IsSignedIn => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// Set by <see cref="GitHubAuthService"/>. Called before a request uses the token, so a token
    /// we already know has expired is renewed instead of being sent and rejected.
    /// </summary>
    internal static Func<CancellationToken, Task>? RefreshHook { get; set; }

    /// <summary>
    /// Set by <see cref="GitHubAuthService"/>. Called when GitHub answers 401 for a token we sent,
    /// so the dead token is dropped and the request can be retried anonymously.
    /// </summary>
    internal static Action? TokenRejectedHook { get; set; }

    internal static void ClearToken()
    {
        AccessToken = null;
        Login = null;
        ExpiresAt = null;
    }
}
