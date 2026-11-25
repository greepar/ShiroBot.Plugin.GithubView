using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShiroBot.Plugin.GithubView.Markdown;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>Issue / Pull Request 卡片状态，对应 GitHub 页面上的状态徽章。</summary>
public enum IssueCardState
{
    IssueOpen,
    IssueCompleted,
    IssueNotPlanned,
    PullOpen,
    PullDraft,
    PullMerged,
    PullClosed
}

/// <summary>Issue / Pull Request 卡片 ViewModel：一次性渲染用 POCO。</summary>
public sealed class IssueCardViewModel
{
    public string Owner { get; init; } = "ShirokaProject";
    public string Repository { get; init; } = "ShiroBot";
    public int Number { get; init; } = 42;
    public string Title { get; init; } = "支持在群聊中渲染 GitHub Issue 与 Pull Request 卡片";
    public string Author { get; init; } = "greepar";
    public string OpenedAt { get; init; } = "2026-11-25";
    public string? ClosedText { get; init; }
    public IssueCardState State { get; init; } = IssueCardState.PullOpen;
    public IReadOnlyList<IssueLabelViewModel> Labels { get; init; } =
    [
        new("enhancement", Color.Parse("#A2EEEF")),
        new("plugin", Color.Parse("#5319E7"))
    ];

    /// <summary>时间线：第一项是正文，后面是评论。</summary>
    public IReadOnlyList<CommentViewModel> Entries { get; init; } =
    [
        new()
        {
            Author = "greepar",
            Date = "2026-11-25",
            Association = "Owner",
            IsIssueAuthor = true,
            AvatarBytes = DescriptionCardViewModel.LoadDefaultAvatarBytes(),
            Content = new MarkdownContent
            {
                Compact = true,
                Markdown = "目前只有仓库链接会渲染卡片，希望 **Issue** 和 **PR** 链接也能展示：\n\n- [x] 标题与状态\n- [ ] 标签与 `diff` 统计"
            }
        },
        new()
        {
            Author = "shiroka",
            Date = "2026-11-25",
            Association = "Member",
            Content = new MarkdownContent { Compact = true, Markdown = "LGTM 👍" }
        }
    ];

    /// <summary>评论总数（不含正文），用于提示还有多少条未展示。</summary>
    public int TotalComments { get; init; } = 3;

    public string Comments { get; init; } = "3";
    public string Reactions { get; init; } = "5";

    // Pull Request 专属
    public string Commits { get; init; } = "4";
    public string ChangedFiles { get; init; } = "6";
    public string Additions { get; init; } = "+412";
    public string Deletions { get; init; } = "−37";
    public string BaseBranch { get; init; } = "main";
    public string HeadBranch { get; init; } = "feat/issue-card";

    public bool IsPullRequest => State is IssueCardState.PullOpen or IssueCardState.PullDraft
        or IssueCardState.PullMerged or IssueCardState.PullClosed;

    public bool IsIssue => !IsPullRequest;
    public string RepositoryDisplay => $"{Owner}/{Repository}";
    public string NumberDisplay => $"#{Number}";
    public string KindDisplay => IsPullRequest ? "Pull Request" : "Issue";
    public bool HasLabels => Labels.Count > 0;
    public int HiddenComments => Math.Max(0, TotalComments - (Entries.Count - 1));
    public bool HasHiddenComments => HiddenComments > 0;
    public string HiddenCommentsText => $"还有 {HiddenComments} 条评论未展示，前往 GitHub 查看完整讨论";
    public string MetaSuffix => string.IsNullOrEmpty(ClosedText)
        ? $" opened on {OpenedAt}"
        : $" opened on {OpenedAt} · {ClosedText}";

    public string StateText => State switch
    {
        IssueCardState.IssueOpen or IssueCardState.PullOpen => "Open",
        IssueCardState.IssueCompleted or IssueCardState.PullClosed => "Closed",
        IssueCardState.IssueNotPlanned => "Not planned",
        IssueCardState.PullDraft => "Draft",
        IssueCardState.PullMerged => "Merged",
        _ => "Unknown"
    };

    public Color StateColor => State switch
    {
        IssueCardState.IssueOpen or IssueCardState.PullOpen => Color.Parse("#1F883D"),
        IssueCardState.IssueCompleted or IssueCardState.PullMerged => Color.Parse("#8250DF"),
        IssueCardState.PullClosed => Color.Parse("#CF222E"),
        _ => Color.Parse("#59636E")
    };

    /// <summary>Octicons 16px 图标路径。</summary>
    public Geometry StateIcon => Geometry.Parse(State switch
    {
        IssueCardState.IssueOpen =>
            "M8 9.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0ZM1.5 8a6.5 6.5 0 1 0 13 0 6.5 6.5 0 0 0-13 0Z",
        IssueCardState.IssueCompleted =>
            "M11.28 6.78a.75.75 0 0 0-1.06-1.06L7.25 8.69 5.78 7.22a.75.75 0 0 0-1.06 1.06l2 2a.75.75 0 0 0 1.06 0l3.5-3.5Z M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0Zm-1.5 0a6.5 6.5 0 1 0-13 0 6.5 6.5 0 0 0 13 0Z",
        IssueCardState.IssueNotPlanned =>
            "M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0ZM1.5 8a6.5 6.5 0 1 0 13 0 6.5 6.5 0 0 0-13 0Zm9.78-2.22-5.5 5.5a.749.749 0 0 1-1.275-.326.749.749 0 0 1 .215-.734l5.5-5.5a.751.751 0 0 1 1.042.018.751.751 0 0 1 .018 1.042Z",
        IssueCardState.PullMerged =>
            "M5.45 5.154A4.25 4.25 0 0 0 9.25 7.5h1.378a2.251 2.251 0 1 1 0 1.5H9.25A5.734 5.734 0 0 1 5 7.123v3.505a2.25 2.25 0 1 1-1.5 0V5.372a2.25 2.25 0 1 1 1.95-.218ZM4.25 13.5a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5Zm8.5-4.5a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5ZM5 3.25a.75.75 0 1 0 0 .005V3.25Z",
        IssueCardState.PullClosed =>
            "M3.25 1A2.25 2.25 0 0 1 4 5.372v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.251 2.251 0 0 1 3.25 1Zm9.5 5.5a.75.75 0 0 1 .75.75v3.378a2.251 2.251 0 1 1-1.5 0V7.25a.75.75 0 0 1 .75-.75Zm-2.03-5.273a.75.75 0 0 1 1.06 0l.97.97.97-.97a.748.748 0 0 1 1.265.332.75.75 0 0 1-.205.729l-.97.97.97.97a.751.751 0 0 1-.018 1.042.751.751 0 0 1-1.042.018l-.97-.97-.97.97a.749.749 0 0 1-1.275-.326.749.749 0 0 1 .215-.734l.97-.97-.97-.97a.75.75 0 0 1 0-1.06ZM2.5 3.25a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0ZM3.25 12a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm9.5 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Z",
        IssueCardState.PullDraft =>
            "M3.25 1A2.25 2.25 0 0 1 4 5.372v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.251 2.251 0 0 1 3.25 1Zm9.5 14a2.25 2.25 0 1 1 0-4.5 2.25 2.25 0 0 1 0 4.5ZM2.5 3.25a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0ZM3.25 12a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm9.5 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5ZM14 7.5a1.25 1.25 0 1 1-2.5 0 1.25 1.25 0 0 1 2.5 0Zm0-4.25a1.25 1.25 0 1 1-2.5 0 1.25 1.25 0 0 1 2.5 0Z",
        _ =>
            "M1.5 3.25a2.25 2.25 0 1 1 3 2.122v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.25 2.25 0 0 1 1.5 3.25Zm5.677-.177L9.573.677A.25.25 0 0 1 10 .854V2.5h1A2.5 2.5 0 0 1 13.5 5v5.628a2.251 2.251 0 1 1-1.5 0V5a1 1 0 0 0-1-1h-1v1.646a.25.25 0 0 1-.427.177L7.177 3.427a.25.25 0 0 1 0-.354ZM3.75 2.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm0 9.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm8.25.75a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0Z"
    });

}

/// <summary>Issue 标签，配色由 <see cref="LabelChip"/> 按主题计算。</summary>
public sealed record IssueLabelViewModel(string Name, Color Color);

/// <summary>时间线中的一条：Issue / PR 正文或一条评论。</summary>
public sealed class CommentViewModel
{
    public string Author { get; init; } = "ghost";
    public string Date { get; init; } = "-";
    public string? Association { get; init; }
    public bool IsIssueAuthor { get; init; }
    public byte[]? AvatarBytes { get; init; }
    public MarkdownContent Content { get; init; } = new();

    public string ActionText => $" commented on {Date}";
    public bool HasAssociation => !string.IsNullOrEmpty(Association);
    public string AuthorInitial => string.IsNullOrWhiteSpace(Author) ? "?" : Author[..1].ToUpperInvariant();
    public Bitmap? Avatar => AvatarBytes is null ? null : new Bitmap(new MemoryStream(AvatarBytes));
    public bool HasAvatar => AvatarBytes is not null;
    public bool HasNoAvatar => AvatarBytes is null;
}
