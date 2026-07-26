namespace ShiroBot.Plugin.GithubView.Service;

internal enum GitHubPageKind
{
    Repository,
    Readme,
    Issue,
    IssueList,
    PullRequest,
    PullRequestList,
    ActionsList,
    ActionRun,
    Release,
    Commit
}

/// <summary>
/// 解析结果:owner/repo 必填,Number 用于 issue/PR/run 编号,Reference 用于 tag 名或 commit sha。
/// </summary>
internal sealed record GitHubLink(
    GitHubPageKind Kind,
    string Owner,
    string Repository,
    long Number = 0,
    string? Reference = null);

internal static class GitHubLinkParser
{
    public static bool TryParse(string text, out GitHubLink link)
    {
        link = null!;

        var start = text.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var urlStart = start;
        while (urlStart > 0 && !char.IsWhiteSpace(text[urlStart - 1]))
        {
            urlStart--;
        }

        var urlEnd = start;
        while (urlEnd < text.Length && !char.IsWhiteSpace(text[urlEnd]))
        {
            urlEnd++;
        }

        var candidate = text[urlStart..urlEnd].Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var owner = parts[0];
        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^".git".Length]
            : parts[1];

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        link = Classify(owner, repository, parts, uri.Fragment);
        return true;
    }

    private static GitHubLink Classify(string owner, string repository, string[] parts, string fragment)
    {
        if (parts.Length == 2)
        {
            return string.Equals(fragment, "#readme", StringComparison.OrdinalIgnoreCase)
                ? new GitHubLink(GitHubPageKind.Readme, owner, repository)
                : new GitHubLink(GitHubPageKind.Repository, owner, repository);
        }

        var section = parts[2].ToLowerInvariant();
        var rest = parts.AsSpan(3);

        switch (section)
        {
            case "issues" when rest.Length >= 1 && long.TryParse(rest[0], out var issueNumber):
                return new GitHubLink(GitHubPageKind.Issue, owner, repository, issueNumber);
            case "issues":
                return new GitHubLink(GitHubPageKind.IssueList, owner, repository);

            case "pull" when rest.Length >= 1 && long.TryParse(rest[0], out var prNumber):
                return new GitHubLink(GitHubPageKind.PullRequest, owner, repository, prNumber);
            case "pull" or "pulls":
                return new GitHubLink(GitHubPageKind.PullRequestList, owner, repository);

            case "actions" when rest.Length >= 2
                                && string.Equals(rest[0], "runs", StringComparison.OrdinalIgnoreCase)
                                && long.TryParse(rest[1], out var runId):
                return new GitHubLink(GitHubPageKind.ActionRun, owner, repository, runId);
            case "actions":
                return new GitHubLink(GitHubPageKind.ActionsList, owner, repository);

            case "releases" when rest.Length >= 2
                                 && string.Equals(rest[0], "tag", StringComparison.OrdinalIgnoreCase):
                return new GitHubLink(GitHubPageKind.Release, owner, repository, Reference: Uri.UnescapeDataString(rest[1]));
            case "releases":
                return new GitHubLink(GitHubPageKind.Release, owner, repository);

            case "commit" when rest.Length >= 1:
                return new GitHubLink(GitHubPageKind.Commit, owner, repository, Reference: rest[0]);

            case "blob" when rest.Length >= 1
                             && rest[^1].StartsWith("README", StringComparison.OrdinalIgnoreCase):
                return new GitHubLink(GitHubPageKind.Readme, owner, repository);

            default:
                return new GitHubLink(GitHubPageKind.Repository, owner, repository);
        }
    }
}
