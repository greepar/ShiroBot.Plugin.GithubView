using Avalonia.Controls;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.Plugin.Github.Views;
using ShiroBot.Plugin.GithubView.Service;
using ShiroBot.Plugin.GithubView.Views;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.GithubView;

[BotPlugin(id: "GithubView",
    Name = "Github 预览插件",
    Version = "1.1.0",
    Author = "greepar",
    Description = "解析 GitHub 链接并渲染仓库 / README / Issue / PR / Actions / Release / Commit 卡片。",
    GithubRepo = "greepar/ShiroBot.Plugin.GithubView",
    IsPluginSingleFile = true)
]
public sealed class GithubViewPlugin : PluginBase
{
    private readonly GitHubApiClient _api = new();
    private readonly GitHubRepositoryClient _repoClient;
    private readonly GitHubPageService _pages;
    private IDisposable? _configWatch;

    public GithubViewPlugin()
    {
        _repoClient = new GitHubRepositoryClient(_api);
        _pages = new GitHubPageService(_api);
    }

    public override string Name => "GithubPlugin";

    protected override Task LoadAsync()
    {
        ApplyConfig(Context.Config.Load<PluginConfig>());
        _configWatch = Context.Config.Watch<PluginConfig>(ApplyConfig);

        // readme 命令:「gh readme owner/repo」或「ghreadme owner/repo」
        GroupCommands.MapPrefix("gh readme", message => HandleReadmeCommandAsync(message, "gh readme"));
        GroupCommands.MapPrefix("ghreadme", message => HandleReadmeCommandAsync(message, "ghreadme"));

        // 链接触发
        GroupCommands.MapWhen(
            message => GitHubLinkParser.TryParse(message.GetPlainText(), out _),
            HandleGroupGitHubRenderAsync);

        BotLog.Info("Github 插件已加载:支持仓库/README/Issue/PR/Actions/Release/Commit 链接渲染。");
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        _configWatch?.Dispose();
        _configWatch = null;
        _api.Dispose();
        return Task.CompletedTask;
    }

    private void ApplyConfig(PluginConfig config)
    {
        _api.SetToken(config.GithubToken);
        _pages.MaxBodyLength = Math.Clamp(config.MaxBodyLength, 500, 20000);
        _pages.ListItemCount = Math.Clamp(config.ListItemCount, 3, 15);
    }

    private async Task HandleReadmeCommandAsync(MessageEvent message, string prefix)
    {
        var arg = StripPrefix(message.GetPlainText(), prefix);
        var parts = arg.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            await Context.Message.QuoteReplyAsync(message, "用法: gh readme owner/repo");
            return;
        }

        await RenderAndReplyAsync(message, new GitHubLink(GitHubPageKind.Readme, parts[0], parts[1]));
    }

    private async Task HandleGroupGitHubRenderAsync(MessageEvent message)
    {
        if (!GitHubLinkParser.TryParse(message.GetPlainText(), out var link))
        {
            return;
        }

        BotLog.Info($"检测到 GitHub 链接 [{link.Kind}] {link.Owner}/{link.Repository}");
        await RenderAndReplyAsync(message, link);
    }

    private async Task RenderAndReplyAsync(MessageEvent message, GitHubLink link)
    {
        if (Context.Render is null)
        {
            await Context.Message.QuoteReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
            return;
        }

        try
        {
            var png = await RenderPageAsync(link).ConfigureAwait(false);
            BotLog.Success($"Github {link.Kind} 渲染完成。");
            await Context.Message.ReplyAsync(message,
                new ImageSegment("base64://" + Convert.ToBase64String(png)));
        }
        catch (GitHubNotFoundException)
        {
            await Context.Message.QuoteReplyAsync(message,
                link.Kind == GitHubPageKind.Release
                    ? $"{link.Owner}/{link.Repository} 没有找到 Release。"
                    : $"未找到该 GitHub 资源（{link.Kind}），可能不存在或为私有仓库。");
        }
        catch (GitHubRateLimitException ex)
        {
            await Context.Message.QuoteReplyAsync(message, ex.Message);
        }
        catch (Exception ex)
        {
            await Context.Message.QuoteReplyAsync(message, $"渲染 GitHub 卡片失败: {ex.Message}");
            BotLog.Warning($"获取 GitHub 数据失败: {ex}");
        }
    }

    private async Task<byte[]> RenderPageAsync(GitHubLink link)
    {
        var (owner, repo) = (link.Owner, link.Repository);
        return link.Kind switch
        {
            GitHubPageKind.Repository => await RenderAsync<DescriptionCard>(
                await _repoClient.GetRepositoryCardAsync(owner, repo).ConfigureAwait(false)),
            GitHubPageKind.Readme => await RenderAsync<ReadmeCard>(
                await _pages.GetReadmeCardAsync(owner, repo).ConfigureAwait(false)),
            GitHubPageKind.Issue => await RenderAsync<IssueDetailCard>(
                await _pages.GetIssueCardAsync(owner, repo, link.Number).ConfigureAwait(false)),
            GitHubPageKind.PullRequest => await RenderAsync<IssueDetailCard>(
                await _pages.GetPullRequestCardAsync(owner, repo, link.Number).ConfigureAwait(false)),
            GitHubPageKind.IssueList => await RenderAsync<ListCard>(
                await _pages.GetIssueListCardAsync(owner, repo).ConfigureAwait(false)),
            GitHubPageKind.PullRequestList => await RenderAsync<ListCard>(
                await _pages.GetPullRequestListCardAsync(owner, repo).ConfigureAwait(false)),
            GitHubPageKind.ActionsList => await RenderAsync<ListCard>(
                await _pages.GetActionsListCardAsync(owner, repo).ConfigureAwait(false)),
            GitHubPageKind.ActionRun => await RenderAsync<RunDetailCard>(
                await _pages.GetRunCardAsync(owner, repo, link.Number).ConfigureAwait(false)),
            GitHubPageKind.Release => await RenderAsync<ReleaseCard>(
                await _pages.GetReleaseCardAsync(owner, repo, link.Reference).ConfigureAwait(false)),
            GitHubPageKind.Commit => await RenderAsync<CommitCard>(
                await _pages.GetCommitCardAsync(owner, repo, link.Reference!).ConfigureAwait(false)),
            _ => throw new InvalidOperationException($"未支持的页面类型: {link.Kind}")
        };
    }

    private Task<byte[]> RenderAsync<TControl>(object viewModel) where TControl : Control, new()
        => Context.RenderControlPngAsync<TControl>(viewModel, new ControlRenderOptions(RenderTheme.Auto));

    private static string StripPrefix(string text, string prefix)
    {
        text = text.TrimStart();
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[prefix.Length..];
        }

        return text.Trim();
    }
}
