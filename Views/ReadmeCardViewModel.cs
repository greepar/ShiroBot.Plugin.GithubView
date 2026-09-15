namespace ShiroBot.Plugin.GithubView.Views;

public sealed class ReadmeCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string FileName { get; init; } = "README.md";
    public string Markdown { get; init; } = "";
    public bool Truncated { get; init; }

    // headless 渲染窗口高度上限 1080,header+边距约 130,正文限制在 950 内保证脚注可见
    public double BodyMaxHeight => 950;
    public bool ShowTruncatedNote => Truncated || MarkdownHeightEstimator.MayOverflow(Markdown, BodyMaxHeight);
}
