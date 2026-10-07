using ShiroBot.AvaloniaSdk;
using ShiroBot.Plugin.Github.Views;
using ShiroBot.Plugin.GithubView.Service;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;
using ShiroBot.SDK.Models;

[assembly: ShiroBotApiCompatibility("0.9.2", "0.9.2")]

namespace ShiroBot.Plugin.GithubView;

[BotPlugin(id: "GithubView",
    Name = "Github 预览插件",
    Version = "1.0.1",
    Author = "greepar",
    Description = "解析 GitHub 仓库链接并渲染相关信息卡片。",
    GithubRepo = "greepar/ShiroBot.Plugin.GithubView",
    IsPluginSingleFile = true)
]
public sealed class GithubViewPlugin : PluginBase
{
    private readonly GitHubRepositoryClient _github = new();

    public override string Name => "GithubPlugin";

    protected override Task LoadAsync()
    {
        GroupCommands.MapWhen(message => TryReadGitHubRepository(message.GetPlainText(), out _, out _), HandleGroupGitHubRenderAsync);
        BotLog.Info("Github 插件已加载，发送 GitHub 仓库链接触发截图。");
        return Task.CompletedTask;
    }

    private async Task HandleGroupGitHubRenderAsync(MessageEvent message)
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
                await Context.Message.QuoteReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
                return;
            }

            BotLog.Success("Github页面渲染完成。");
            await Context.Message.ReplyAsync(message, segment);
        }
        catch (Exception ex)
        {
            await Context.Message.QuoteReplyAsync(message, $"渲染 GitHub 仓库卡片失败: {ex.Message}");
            BotLog.Warning($"获取 GitHub 数据失败， {ex.Message}");
        }
    }

    private async Task<ImageSegment?> RenderAsync(
        string owner = "ShirokaProject",
        string repository = "ShiroBot")
    {
        if (Context.Render is null)
        {
            return null;
        }
        
        var vm = await _github.GetRepositoryCardAsync(
            owner,
            repository).ConfigureAwait(false);
        var png = await Context.RenderControlPngAsync<DescriptionCard>(
            vm, 
            new ControlRenderOptions(RenderTheme.Auto));

        return new ImageSegment("base64://" + Convert.ToBase64String(png));
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
