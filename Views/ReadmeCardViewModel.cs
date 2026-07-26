namespace ShiroBot.Plugin.GithubView.Views;

public sealed class ReadmeCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string FileName { get; init; } = "README.md";
    public string Markdown { get; init; } = "";
    public bool Truncated { get; init; }
}
