namespace WindrunnerLauncher.Core.Tests;

public static class TestCollections
{
    /// <summary>
    /// <see cref="WindrunnerLauncher.Core.Downloads.GitHubSession"/> is process-wide static state:
    /// every GitHub request reads its token and may invoke its refresh and token-rejected hooks.
    /// Tests that touch it therefore cannot run alongside any other test that talks to the GitHub
    /// API, or one test's hooks fire for another test's request.
    /// </summary>
    public const string GitHubApi = "GitHub API";
}

[CollectionDefinition(TestCollections.GitHubApi, DisableParallelization = true)]
public sealed class GitHubApiCollection
{
}
