using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>
/// Issue / PR 详情卡 ViewModel(PR 复用:IsPullRequest=true 时显示分支与增删行数)。
/// </summary>
public sealed class IssueDetailCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string Title { get; init; } = "Issue title";
    public string NumberText { get; init; } = "#0";

    // 状态胶囊
    public string StateText { get; init; } = "Open";
    public string StateIconData { get; init; } = Service.Octicons.IssueOpened;
    public Color StateColor { get; init; } = GitHubColors.OpenGreen;
    public IBrush StateBrush => new SolidColorBrush(StateColor);

    public string AuthorLogin { get; init; } = "user";
    public byte[]? AuthorAvatarBytes { get; init; }
    public Bitmap? AuthorAvatar => AvatarHelper.ToBitmap(AuthorAvatarBytes);
    public bool HasAuthorAvatar => AuthorAvatarBytes is not null;

    public string MetaText { get; init; } = "opened just now";
    public string CommentsText { get; init; } = "0 comments";

    public string Body { get; init; } = "";
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool BodyTruncated { get; init; }

    public IReadOnlyList<LabelChip> Labels { get; init; } = [];
    public bool HasLabels => Labels.Count > 0;

    // PR 专属
    public bool IsPullRequest { get; init; }
    public string BranchText { get; init; } = "";
    public string DiffStatText { get; init; } = "";
    public bool HasDiffStat => IsPullRequest && !string.IsNullOrEmpty(DiffStatText);
}
