namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>
/// 通用列表卡(Issue 列表 / PR 列表 / Actions runs 列表)。
/// </summary>
public sealed class ListCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string TitleText { get; init; } = "Issues";
    public string SubTitleText { get; init; } = "";
    public bool HasSubTitle => !string.IsNullOrEmpty(SubTitleText);

    public IReadOnlyList<ListItem> Items { get; init; } = [];
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText { get; init; } = "Nothing to show";
}
