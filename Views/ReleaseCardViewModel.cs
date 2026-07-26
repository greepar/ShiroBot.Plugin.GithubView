using Avalonia.Media.Imaging;

namespace ShiroBot.Plugin.GithubView.Views;

public sealed class ReleaseCardViewModel
{
    public string RepoSlug { get; init; } = "owner/repo";
    public string ReleaseName { get; init; } = "v1.0.0";
    public string TagText { get; init; } = "v1.0.0";
    public bool IsPreRelease { get; init; }
    public bool IsLatest { get; init; }

    public string AuthorLogin { get; init; } = "user";
    public byte[]? AuthorAvatarBytes { get; init; }
    public Bitmap? AuthorAvatar => AvatarHelper.ToBitmap(AuthorAvatarBytes);
    public bool HasAuthorAvatar => AuthorAvatarBytes is not null;

    public string MetaText { get; init; } = "released just now";
    public string AssetsText { get; init; } = "";
    public bool HasAssets => !string.IsNullOrEmpty(AssetsText);

    public string Body { get; init; } = "";
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool BodyTruncated { get; init; }
}
