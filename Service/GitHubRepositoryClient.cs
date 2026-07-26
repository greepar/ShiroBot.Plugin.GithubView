using Avalonia.Controls;
using Avalonia.Media;
using ShiroBot.Plugin.Github.Views;
using ShiroBot.Plugin.GithubView.Views;

namespace ShiroBot.Plugin.GithubView.Service;

internal sealed class GitHubRepositoryClient(GitHubApiClient api)
{
    public async Task<DescriptionCardViewModel> GetRepositoryCardAsync(
        string owner,
        string repository,
        CancellationToken ct = default)
    {
        using var document = await api.GetJsonAsync($"repos/{owner}/{repository}", ct).ConfigureAwait(false);
        var root = document.RootElement;

        var contributors = await GetContributorCountAsync(owner, repository, ct).ConfigureAwait(false);
        var avatarBytes = await api.GetImageAsync(root.GetStringOrNull("owner", "avatar_url"), ct).ConfigureAwait(false);
        var languages = await GetLanguagesOrDefaultAsync(owner, repository, ct).ConfigureAwait(false);

        return new DescriptionCardViewModel
        {
            Owner = root.GetStringOrNull("owner", "login") ?? owner,
            Repository = root.GetStringOrNull("name") ?? repository,
            Description = root.GetStringOrNull("description") ?? "No description provided.",
            Contributors = Formatting.Count(contributors),
            Issues = Formatting.Count(root.GetIntOrZero("open_issues_count")),
            Discussions = "-",
            Stars = Formatting.Count(root.GetIntOrZero("stargazers_count")),
            Forks = Formatting.Count(root.GetIntOrZero("forks_count")),
            PrimaryLanguage = languages.PrimaryLanguage,
            AvatarBytes = avatarBytes ?? (string.Equals(owner, "ShirokaProject", StringComparison.OrdinalIgnoreCase)
                && string.Equals(repository, "ShiroBot", StringComparison.OrdinalIgnoreCase)
                    ? DescriptionCardViewModel.LoadDefaultAvatarBytes()
                    : null),
            Language1Width = languages[0].Width,
            Language2Width = languages[1].Width,
            Language3Width = languages[2].Width,
            Language4Width = languages[3].Width,
            Language5Width = languages[4].Width,
            Language1Color = languages[0].Color,
            Language2Color = languages[1].Color,
            Language3Color = languages[2].Color,
            Language4Color = languages[3].Color,
            Language5Color = languages[4].Color,
        };
    }

    private async Task<LanguageBar> GetLanguagesAsync(string owner, string repository, CancellationToken ct)
    {
        using var document = await api.GetJsonAsync($"repos/{owner}/{repository}/languages", ct).ConfigureAwait(false);
        var languages = document.RootElement.EnumerateObject()
            .Select(property => new
            {
                property.Name,
                Bytes = property.Value.TryGetInt64(out var value) ? value : 0
            })
            .Where(language => language.Bytes > 0)
            .OrderByDescending(language => language.Bytes)
            .Take(5)
            .ToArray();

        var total = languages.Sum(language => language.Bytes);
        var result = new LanguageBarSegment[5];
        for (var i = 0; i < result.Length; i++)
        {
            if (i < languages.Length && total > 0)
            {
                result[i] = new LanguageBarSegment(
                    new GridLength(languages[i].Bytes, GridUnitType.Star),
                    GetLanguageColor(languages[i].Name));
            }
            else
            {
                result[i] = new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent);
            }
        }

        var primaryLanguage = languages.Length > 0 && total > 0
            ? languages[0].Name
            : "Unknown";

        return new LanguageBar(result, primaryLanguage);
    }

    private async Task<LanguageBar> GetLanguagesOrDefaultAsync(
        string owner,
        string repository,
        CancellationToken ct)
    {
        try
        {
            return await GetLanguagesAsync(owner, repository, ct).ConfigureAwait(false);
        }
        catch
        {
            return new LanguageBar([
                new LanguageBarSegment(new GridLength(1, GridUnitType.Star), Color.Parse("#8C959F")),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent)
            ], "Unknown");
        }
    }

    private async Task<int> GetContributorCountAsync(string owner, string repository, CancellationToken ct)
    {
        try
        {
            return await api.GetCollectionCountAsync(
                $"repos/{owner}/{repository}/contributors?per_page=1&anon=true", ct).ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    private static Color GetLanguageColor(string language)
    {
        return language switch
        {
            "C#" => Color.Parse("#178600"),
            "F#" => Color.Parse("#b845fc"),
            "Visual Basic .NET" => Color.Parse("#945db7"),
            "JavaScript" => Color.Parse("#f1e05a"),
            "TypeScript" => Color.Parse("#3178c6"),
            "HTML" => Color.Parse("#e34c26"),
            "CSS" => Color.Parse("#663399"),
            "Python" => Color.Parse("#3572A5"),
            "Java" => Color.Parse("#b07219"),
            "Kotlin" => Color.Parse("#A97BFF"),
            "C" => Color.Parse("#555555"),
            "C++" => Color.Parse("#f34b7d"),
            "Rust" => Color.Parse("#dea584"),
            "Go" => Color.Parse("#00ADD8"),
            "Swift" => Color.Parse("#F05138"),
            "Shell" => Color.Parse("#89e051"),
            "Dockerfile" => Color.Parse("#384d54"),
            "PowerShell" => Color.Parse("#012456"),
            "Vue" => Color.Parse("#41b883"),
            "Svelte" => Color.Parse("#ff3e00"),
            "Dart" => Color.Parse("#00B4AB"),
            "Ruby" => Color.Parse("#701516"),
            "PHP" => Color.Parse("#4F5D95"),
            "Lua" => Color.Parse("#000080"),
            "Jupyter Notebook" => Color.Parse("#DA5B0B"),
            _ => Color.Parse("#8C959F")
        };
    }

    private sealed record LanguageBar(LanguageBarSegment[] Segments, string PrimaryLanguage)
    {
        public LanguageBarSegment this[int index] => Segments[index];
    }

    private sealed record LanguageBarSegment(GridLength Width, Color Color);
}
