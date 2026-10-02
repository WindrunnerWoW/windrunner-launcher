namespace WindrunnerLauncher.Core.Downloads;

/// <summary>
/// Client ID of the GitHub OAuth App used for Device Flow sign-in (Settings → GitHub sign-in).
/// Device Flow needs only a Client ID, never a client secret, so it is safe to compile into a
/// distributed build. See docs/auto-updates.md for how to register the app and fill this in.
/// </summary>
public static class GitHubOAuthConfig
{
    public const string ClientId = "Ov23li2eA7h0F6OLvJl4";

    public static bool IsConfigured => ClientId.Length > 0;
}
