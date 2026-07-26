using Avalonia.Media.Imaging;

namespace ShiroBot.Plugin.GithubView.Views;

public sealed class CommitCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string MessageTitle { get; init; } = "Commit message";
    public string MessageBody { get; init; } = "";
    public bool HasMessageBody => !string.IsNullOrWhiteSpace(MessageBody);

    public string ShaText { get; init; } = "0000000";

    public string AuthorLogin { get; init; } = "user";
    public byte[]? AuthorAvatarBytes { get; init; }
    public Bitmap? AuthorAvatar => AvatarHelper.ToBitmap(AuthorAvatarBytes);
    public bool HasAuthorAvatar => AuthorAvatarBytes is not null;
    public string MetaText { get; init; } = "committed just now";

    public string FilesText { get; init; } = "0 files changed";
    public string AdditionsText { get; init; } = "+0";
    public string DeletionsText { get; init; } = "−0";
}
