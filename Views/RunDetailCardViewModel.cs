using Avalonia.Media;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>
/// Actions 单次 workflow run 详情卡。
/// </summary>
public sealed class RunDetailCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string WorkflowName { get; init; } = "CI";
    public string RunTitle { get; init; } = "Run title";
    public string RunNumberText { get; init; } = "#0";

    public string StatusText { get; init; } = "Success";
    public string StatusIconData { get; init; } = Service.Octicons.CheckCircleFill;
    public Color StatusColor { get; init; } = GitHubColors.OpenGreen;
    public IBrush StatusBrush => new SolidColorBrush(StatusColor);

    public string MetaText { get; init; } = "";
    public string BranchText { get; init; } = "main";
    public string CommitShaText { get; init; } = "";
    public string DurationText { get; init; } = "";

    public IReadOnlyList<JobItem> Jobs { get; init; } = [];
    public bool HasJobs => Jobs.Count > 0;
}
