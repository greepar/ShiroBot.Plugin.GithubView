using Avalonia.Media.Imaging;
using ShiroBot.Plugin.GithubView.Markdown;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>完整 README 卡片。</summary>
public sealed class ReadmeCardViewModel
{
    public string Owner { get; init; } = "ShirokaProject";
    public string Repository { get; init; } = "ShiroBot";
    public string FileName { get; init; } = "README.md";
    public string Branch { get; init; } = "main";
    public byte[]? AvatarBytes { get; init; } = DescriptionCardViewModel.LoadDefaultAvatarBytes();

    public MarkdownContent Content { get; init; } = new()
    {
        Markdown = "# ShiroBot\n\n一个轻量的插件化机器人框架。\n\n## 特性\n\n- 多平台适配\n- 插件热更新\n\n```csharp\nawait Context.Message.ReplyAsync(message, \"pong\");\n```"
    };

    public string RepositoryDisplay => $"{Owner}/{Repository}";
    public string OwnerInitial => string.IsNullOrWhiteSpace(Owner) ? "?" : Owner[..1].ToUpperInvariant();
    public Bitmap? Avatar => AvatarBytes is null ? null : new Bitmap(new MemoryStream(AvatarBytes));
    public bool HasAvatar => AvatarBytes is not null;
    public bool HasNoAvatar => AvatarBytes is null;
}
