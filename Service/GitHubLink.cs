using System.Globalization;
using System.Text.RegularExpressions;

namespace ShiroBot.Plugin.GithubView.Service;

internal enum GitHubLinkKind
{
    Repository,
    Issue,
    PullRequest
}

/// <summary>消息中识别出的 GitHub 链接：仓库根地址、Issue 或 Pull Request。</summary>
internal sealed record GitHubLink(GitHubLinkKind Kind, string Owner, string Repository, int Number = 0)
{
    // 只在 github.com 链接上做候选提取，再按路径段严格校验。
    private static readonly Regex CandidatePattern = new(
        @"(?:https?://)?(?:www\.)?github\.com/[^\s<>""'`，。、；：！？）】》」』]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OwnerPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RepositoryPattern = new(
        "^[A-Za-z0-9._-]{1,100}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>github.com 下不是用户 / 组织的一级路径，形如 github.com/orgs/xxx 的链接不当作仓库。</summary>
    private static readonly HashSet<string> ReservedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "account", "apps", "blog", "codespaces", "collections", "contact", "customer-stories",
        "dashboard", "enterprise", "events", "explore", "features", "gist", "github-copilot", "issues",
        "join", "login", "logout", "marketplace", "new", "notifications", "organizations", "orgs",
        "pricing", "pulls", "readme", "search", "security", "settings", "site", "sponsors", "stars",
        "team", "topics", "trending", "users"
    };

    /// <summary>PR 链接允许带上的子页面，例如 /pull/1/files。</summary>
    private static readonly HashSet<string> PullRequestTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "files", "commits", "checks"
    };

    public static bool TryFind(string? text, out GitHubLink link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(text)
            || text.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        foreach (Match match in CandidatePattern.Matches(text))
        {
            // 排除 gist.github.com、xxx.github.com 之类的子域名。
            if (match.Index > 0 && (char.IsLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] is '.' or '-'))
            {
                continue;
            }

            if (TryParse(match.Value, out link))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParse(string candidate, out GitHubLink link)
    {
        link = null!;
        candidate = candidate.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'');
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !(string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var owner = parts[0];
        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^".git".Length]
            : parts[1];

        if (ReservedOwners.Contains(owner)
            || !OwnerPattern.IsMatch(owner)
            || !RepositoryPattern.IsMatch(repository)
            || repository is "." or "..")
        {
            return false;
        }

        switch (parts.Length)
        {
            // github.com/owner/repo
            case 2:
                link = new GitHubLink(GitHubLinkKind.Repository, owner, repository);
                return true;

            // github.com/owner/repo/issues/123
            case 4 when parts[2].Equals("issues", StringComparison.OrdinalIgnoreCase)
                        && TryReadNumber(parts[3], out var issue):
                link = new GitHubLink(GitHubLinkKind.Issue, owner, repository, issue);
                return true;

            // github.com/owner/repo/pull/123[/files|commits|checks]
            case 4 or 5 when parts[2].Equals("pull", StringComparison.OrdinalIgnoreCase)
                             && TryReadNumber(parts[3], out var pull)
                             && (parts.Length == 4 || PullRequestTabs.Contains(parts[4])):
                link = new GitHubLink(GitHubLinkKind.PullRequest, owner, repository, pull);
                return true;

            default:
                return false;
        }
    }

    private static bool TryReadNumber(string value, out int number)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    public override string ToString()
    {
        return Kind switch
        {
            GitHubLinkKind.Issue => $"{Owner}/{Repository}#{Number} (issue)",
            GitHubLinkKind.PullRequest => $"{Owner}/{Repository}#{Number} (pull request)",
            _ => $"{Owner}/{Repository}"
        };
    }
}
