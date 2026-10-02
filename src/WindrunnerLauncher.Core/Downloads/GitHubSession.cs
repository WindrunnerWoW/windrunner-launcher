namespace WindrunnerLauncher.Core.Downloads;

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

    public static bool IsSignedIn => !string.IsNullOrWhiteSpace(AccessToken);
}
