using System.Collections.Concurrent;
using Avalonia.Controls;
using ShiroBot.AvaloniaSdk;
using ShiroBot.Plugin.Github.Views;
using ShiroBot.Plugin.GithubView.Service;
using ShiroBot.Plugin.GithubView.Views;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.GithubView;

[BotPlugin(id: "GithubView",
    Name = "Github 预览插件",
    Version = "1.2.0",
    Author = "greepar",
    Category = PluginCategory.Render,
    Description = "识别 GitHub 仓库、Issue 与 Pull Request 链接并渲染信息卡片，回复仓库卡片可查看完整 README。",
    GithubRepo = "greepar/ShiroBot.Plugin.GithubView",
    IsPluginSingleFile = true)
]
public sealed class GithubViewPlugin : PluginBase<GithubViewConfig>
{
    private const string ReadmeHintText = "回复 readme 查看完整 README";

    private readonly GitHubClient _github = new();
    private readonly ConcurrentDictionary<IReplySubscription, byte> _readmeSubscriptions = new();

    public override string Name => "Github 预览插件";

    protected override void ConfigureRoutes()
    {
        GroupCommands.MapWhen(
            message => TryMatch(message, out _),
            "GitHub 仓库 / Issue / Pull Request 链接",
            HandleGroupGitHubRenderAsync);
    }

    protected override Task LoadAsync()
    {
        _github.Token = Settings.Token;
        BotLog.Info("Github 插件已加载，发送 GitHub 仓库 / Issue / PR 链接即可生成卡片。");
        return Task.CompletedTask;
    }

    protected override Task OnConfigChangedAsync(
        GithubViewConfig previous,
        GithubViewConfig current,
        CancellationToken cancellationToken)
    {
        _github.Token = current.Token;
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        foreach (var subscription in _readmeSubscriptions.Keys)
        {
            subscription.Dispose();
        }

        _readmeSubscriptions.Clear();
        return Task.CompletedTask;
    }

    private bool TryMatch(MessageEvent message, out GitHubLink link)
    {
        if (!GitHubLink.TryFind(message.GetPlainText(), out link))
        {
            return false;
        }

        return link.Kind switch
        {
            GitHubLinkKind.Repository => Settings.EnableRepositoryCard,
            GitHubLinkKind.Issue => Settings.EnableIssueCard,
            GitHubLinkKind.PullRequest => Settings.EnablePullRequestCard,
            _ => false
        };
    }

    private async Task HandleGroupGitHubRenderAsync(MessageEvent message)
    {
        if (!TryMatch(message, out var link))
        {
            return;
        }

        BotLog.Info($"检测到 GitHub 链接: {link}");
        if (Context.Render is null)
        {
            await Context.Message.QuoteReplyAsync(message, "宿主未启用 Avalonia 渲染（EnableAvalonia=false），无法渲染图片。");
            return;
        }

        await RunReportingErrorsAsync(message, link.ToString(), async ct =>
        {
            if (link.Kind == GitHubLinkKind.Repository)
            {
                await SendRepositoryCardAsync(message, link, ct);
                return;
            }

            var commentCount = Math.Clamp(Settings.CommentCount, 0, 10);
            var vm = await _github.GetIssueCardAsync(link, commentCount, ct).ConfigureAwait(false);
            await SendPngAsync(message, await RenderAsync<IssueCard>(vm));
        });
    }

    private async Task SendRepositoryCardAsync(MessageEvent message, GitHubLink link, CancellationToken ct)
    {
        var readmeTask = Settings.EnableReadmeReply
            ? _github.GetReadmeOrDefaultAsync(link.Owner, link.Repository, ct)
            : Task.FromResult<GitHubClient.ReadmeSource?>(null);

        var vm = await _github.GetRepositoryCardAsync(link.Owner, link.Repository, ct).ConfigureAwait(false);
        var readme = await readmeTask.ConfigureAwait(false);
        vm.ReadmeHint = readme is null ? null : ReadmeHintText;

        var sent = await SendPngAsync(message, await RenderAsync<DescriptionCard>(vm));
        if (readme is not null && sent.IsSuccess && sent.Reference is { } reference)
        {
            SubscribeReadmeReply(reference, vm.Owner, vm.Repository, readme, vm.AvatarBytes);
        }
    }

    /// <summary>仓库卡片发出后，在一段时间内监听对它的回复，收到 “readme” 时渲染完整 README。</summary>
    private void SubscribeReadmeReply(
        MessageReference reference,
        string owner,
        string repository,
        GitHubClient.ReadmeSource readme,
        byte[]? ownerAvatar)
    {
        var minutes = Math.Clamp(Settings.ReadmeReplyMinutes, 1, 1440);
        IReplySubscription? subscription = null;
        var handled = 0;

        subscription = Context.Message.SubscribeReply(reference, TimeSpan.FromMinutes(minutes), async reply =>
        {
            if (!IsReadmeRequest(reply) || Interlocked.Exchange(ref handled, 1) == 1)
            {
                return;
            }

            if (subscription is not null && _readmeSubscriptions.TryRemove(subscription, out _))
            {
                subscription.Dispose();
            }

            BotLog.Info($"渲染 README: {owner}/{repository}");
            await RunReportingErrorsAsync(reply, $"{owner}/{repository} README", async ct =>
            {
                var vm = await _github.GetReadmeCardAsync(owner, repository, readme, ownerAvatar, ct)
                    .ConfigureAwait(false);
                await SendPngAsync(reply, await RenderAsync<ReadmeCard>(vm));
            });
        }, disposeOnReply: false);

        _readmeSubscriptions[subscription] = 0;

        // 到期后宿主会结束订阅，这里同步清理引用。
        _ = Task.Delay(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(5)).ContinueWith(_task =>
        {
            if (_readmeSubscriptions.TryRemove(subscription, out _))
            {
                subscription.Dispose();
            }
        }, TaskScheduler.Default);
    }

    private static bool IsReadmeRequest(MessageEvent reply)
    {
        var text = string.Concat(reply.Segments.OfType<TextSegment>().Select(segment => segment.Text))
            .Trim()
            .TrimStart('/', '#', '!', '！')
            .Trim();

        return text.Equals("readme", StringComparison.OrdinalIgnoreCase)
               || text.Equals("readme.md", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunReportingErrorsAsync(MessageEvent message, string target, Func<CancellationToken, Task> action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await action(timeout.Token);
            BotLog.Success($"GitHub 卡片渲染完成: {target}");
        }
        catch (GitHubApiException ex)
        {
            BotLog.Warning($"获取 GitHub 数据失败 ({target}): {ex.Message}");
            await Context.Message.QuoteReplyAsync(message, ex.Message);
        }
        catch (OperationCanceledException)
        {
            BotLog.Warning($"获取 GitHub 数据超时: {target}");
            await Context.Message.QuoteReplyAsync(message, "请求 GitHub 超时，请稍后再试。");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"渲染 GitHub 卡片失败 ({target}): {ex}");
            await Context.Message.QuoteReplyAsync(message, $"渲染 GitHub 卡片失败: {ex.Message}");
        }
    }

    private Task<SentMessage> SendPngAsync(MessageEvent message, byte[] png)
    {
        return Context.Message.ReplyAsync(message, new ImageSegment("base64://" + Convert.ToBase64String(png)));
    }

    private Task<byte[]> RenderAsync<TView>(object viewModel) where TView : Control, new()
    {
        var theme = Settings.Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => RenderTheme.Light,
            "dark" => RenderTheme.Dark,
            _ => RenderTheme.Auto
        };
        return Context.RenderControlPngAsync<TView>(viewModel, new ControlRenderOptions(theme));
    }
}

[ConfigModel]
public sealed class GithubViewConfig
{
    [ConfigField("GitHub Personal Access Token（无需任何权限）。匿名访问每小时仅 60 次请求，填写后提升至 5000 次；留空则匿名访问。",
        Label = "GitHub Token", Type = "password", Placeholder = "github_pat_...",
        Group = "api", GroupLabel = "GitHub API", GroupOrder = 10, Order = 10)]
    public string Token { get; set; } = string.Empty;

    [ConfigField("卡片配色：auto 跟随宿主 avalonia_theme 设置，light 固定浅色，dark 固定深色。",
        Label = "卡片主题", Options = ["auto", "light", "dark"], Group = "cards", GroupLabel = "卡片类型",
        GroupOrder = 20, Order = 5)]
    public string Theme { get; set; } = "auto";

    [ConfigField("发送仓库首页链接（github.com/owner/repo）时渲染仓库卡片。",
        Label = "仓库卡片", Group = "cards", GroupLabel = "卡片类型", GroupOrder = 20, Order = 10)]
    public bool EnableRepositoryCard { get; set; } = true;

    [ConfigField("发送 Issue 链接（github.com/owner/repo/issues/123）时渲染 Issue 卡片。",
        Label = "Issue 卡片", Group = "cards", GroupLabel = "卡片类型", GroupOrder = 20, Order = 20)]
    public bool EnableIssueCard { get; set; } = true;

    [ConfigField("发送 Pull Request 链接（github.com/owner/repo/pull/123）时渲染 PR 卡片。",
        Label = "PR 卡片", Group = "cards", GroupLabel = "卡片类型", GroupOrder = 20, Order = 30)]
    public bool EnablePullRequestCard { get; set; } = true;

    [ConfigField("Issue / PR 卡片在正文下方展示的评论条数，0 表示不展示评论。",
        Label = "展示评论数", Min = 0, Max = 10, Group = "cards", GroupLabel = "卡片类型", GroupOrder = 20, Order = 40)]
    public int CommentCount { get; set; } = 3;

    [ConfigField("仓库卡片显示“回复 readme”提示，回复后渲染完整 README。",
        Label = "README 回复", Group = "readme", GroupLabel = "README", GroupOrder = 30, Order = 10)]
    public bool EnableReadmeReply { get; set; } = true;

    [ConfigField("仓库卡片发出后，多少分钟内回复 readme 有效。",
        Label = "回复有效期（分钟）", Min = 1, Max = 1440, Group = "readme", GroupLabel = "README", GroupOrder = 30, Order = 20)]
    public int ReadmeReplyMinutes { get; set; } = 10;
}
