using ShiroBot.AvaloniaDemoPlugin.ViewModels;
using ShiroBot.AvaloniaDemoPlugin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.AvaloniaDemoPlugin;

/// <summary>
/// 演示如何用独立 .axaml + UserControl 渲染图片。
/// 适合需要 IDE 智能提示、AXAML 调试预览、复杂控件树场景。
///
/// 命令：
/// - #render：好友/群聊里发起一次截图
/// - #gh https://github.com/owner/repo：渲染指定 GitHub 仓库卡片
/// </summary>
public sealed class AvaloniaDemoPlugin : PluginBase
{
    private readonly GitHubRepositoryClient _github = new();

    public override string Name => "GithubPlugin";

    public override BotComponentMetadata Metadata { get; } = new()
    {
        Name = "Github 解析插件",
        Version = "1.0.0",
        Description = "解析Github地址",
        IsPluginSingleFile = true
    };

    protected override Task LoadAsync()
    {
        FriendCommands.MapWhen(message => TryReadGitHubRepository(message.GetPlainText(), out _, out _), HandleFriendGitHubRenderAsync);
        GroupCommands.MapWhen(message => TryReadGitHubRepository(message.GetPlainText(), out _, out _), HandleGroupGitHubRenderAsync);

        BotLog.Info("[AvaloniaDemoPlugin] 已加载，使用 #render、#gh <GitHub URL> 或直接发送 GitHub 仓库链接触发截图。");
        return Task.CompletedTask;
    }
    

    private async Task HandleFriendGitHubRenderAsync(FriendIncomingMessage message)
    {
        if (!TryReadGitHubRepository(message.GetPlainText(), out var owner, out var repository))
        {
            await Context.Message.ReplyAsync(message, "用法：#gh https://github.com/owner/repo");
            return;
        }

        var segment = await RenderAsync(message.SenderId.ToString(), owner, repository).ConfigureAwait(false);
        if (segment is null)
        {
            await Context.Message.ReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
            return;
        }

        await Context.Message.ReplyAsync(message, segment);
    }

    private async Task HandleGroupGitHubRenderAsync(GroupIncomingMessage message)
    {
        if (!TryReadGitHubRepository(message.GetPlainText(), out var owner, out var repository))
        {
            await Context.Message.ReplyAsync(message, "用法：#gh https://github.com/owner/repo");
            return;
        }

        var segment = await RenderAsync(message.SenderId.ToString(), owner, repository).ConfigureAwait(false);
        if (segment is null)
        {
            await Context.Message.ReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
            return;
        }

        await Context.Message.ReplyAsync(message, segment);
    }

    private async Task<ImageOutgoingSegment?> RenderAsync(
        string requestedBy,
        string owner = "ShirokaProject",
        string repository = "ShiroBot")
    {
        if (Context.Render is null)
        {
            return null;
        }

        var avalonia = Context.Render.AsAvalonia();
        var vm = await CreateViewModelAsync(requestedBy, owner, repository).ConfigureAwait(false);

        var png = await avalonia.RenderControlPngAsync(
            () => new DescriptionCard { DataContext = vm }).ConfigureAwait(false);

        return new ImageOutgoingSegment("base64://" + Convert.ToBase64String(png));
    }

    private async Task<DescriptionCardViewModel> CreateViewModelAsync(string requestedBy, string owner, string repository)
    {
        try
        {
            return await _github.GetRepositoryCardAsync(
                owner,
                repository,
                requestedBy,
                $"AvaloniaDemoPlugin v{Metadata.Version}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"[AvaloniaDemoPlugin] 获取 GitHub 数据失败，使用示例数据: {ex.Message}");
            return new DescriptionCardViewModel
            {
                Owner = owner,
                Repository = repository,
                Description = "GitHub API request failed. Showing fallback card data.",
                RequestedBy = requestedBy,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Footer = $"AvaloniaDemoPlugin v{Metadata.Version} · GitHub API fallback"
            };
        }
    }

    private static bool TryReadGitHubRepository(string text, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;

        var start = text.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var urlStart = start;
        while (urlStart > 0 && !char.IsWhiteSpace(text[urlStart - 1]))
        {
            urlStart--;
        }

        var urlEnd = start;
        while (urlEnd < text.Length && !char.IsWhiteSpace(text[urlEnd]))
        {
            urlEnd++;
        }

        var candidate = text[urlStart..urlEnd].Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        owner = parts[0];
        repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^".git".Length]
            : parts[1];

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository);
    }
}
