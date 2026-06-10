using ShiroBot.AvaloniaDemoPlugin.Service;
using ShiroBot.AvaloniaDemoPlugin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.Github;

/// <summary>
/// 演示如何用独立 .axaml + UserControl 渲染图片。
/// 适合需要 IDE 智能提示、AXAML 调试预览、复杂控件树场景。
///
/// 命令：
/// - #render：好友/群聊里发起一次截图
/// - #gh https://github.com/owner/repo：渲染指定 GitHub 仓库卡片
/// </summary>
public sealed class Main : PluginBase
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
        GroupCommands.MapWhen(message => TryReadGitHubRepository(message.GetPlainText(), out _, out _), HandleGroupGitHubRenderAsync);
        BotLog.Info("Github 插件已加载，发送 GitHub 仓库链接触发截图。");
        return Task.CompletedTask;
    }

    private async Task HandleGroupGitHubRenderAsync(GroupIncomingMessage message)
    {
        BotLog.Info($"检测到 GitHub 链接，尝试解析: {message.GetPlainText()}");
        if (!TryReadGitHubRepository(message.GetPlainText(), out var owner, out var repository))
        {
            BotLog.Error("Github链接解析失败，无法提取 repository。");
            return;
        }

        try
        {
            var segment = await RenderAsync(owner, repository).ConfigureAwait(false);
            if (segment is null)
            {
                await Context.Message.ReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
                return;
            }

            await Context.Message.ReplyAsync(message, segment);
        }
        catch (Exception ex)
        {
            await Context.Message.ReplyAsync(message, $"渲染 GitHub 仓库卡片失败: {ex.Message}");
            BotLog.Warning($"获取 GitHub 数据失败， {ex.Message}");
        }
    }

    private async Task<ImageOutgoingSegment?> RenderAsync(
        string owner = "ShirokaProject",
        string repository = "ShiroBot")
    {
        if (Context.Render is null)
        {
            return null;
        }

        var avalonia = Context.Render.AsAvalonia();
        var vm = await _github.GetRepositoryCardAsync(
            owner,
            repository).ConfigureAwait(false);
        var png = await avalonia.RenderControlPngAsync(
            () => new DescriptionCard { DataContext = vm }).ConfigureAwait(false);

        return new ImageOutgoingSegment("base64://" + Convert.ToBase64String(png));
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
