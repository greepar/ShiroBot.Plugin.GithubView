using Avalonia.Controls;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.Plugin.Github.Views;
using ShiroBot.Plugin.GithubView.Service;
using ShiroBot.Plugin.GithubView.Subscription;
using ShiroBot.Plugin.GithubView.Views;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.GithubView;

[BotPlugin(id: "GithubView",
    Name = "Github 预览插件",
    Version = "1.2.0",
    Author = "greepar",
    Description = "解析 GitHub 链接渲染仓库 / README / Issue / PR / Actions / Release / Commit 卡片,支持仓库事件订阅推送。",
    GithubRepo = "greepar/ShiroBot.Plugin.GithubView",
    IsPluginSingleFile = true)
]
public sealed class GithubViewPlugin : PluginBase
{
    private readonly GitHubApiClient _api = new();
    private readonly GitHubRepositoryClient _repoClient;
    private readonly GitHubPageService _pages;
    private IDisposable? _configWatch;
    private SubscriptionStore? _subscriptions;
    private SubscriptionPoller? _poller;

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

        _subscriptions = new SubscriptionStore(Context.PluginDirectory);
        _poller = new SubscriptionPoller(
            _api,
            _subscriptions,
            HandleNotificationAsync,
            warning => BotLog.Warning(warning));
        ApplyPollerConfig(Context.Config.Load<PluginConfig>());
        _poller.Start();

        // readme 命令:「gh readme owner/repo」或「ghreadme owner/repo」
        GroupCommands.MapPrefix("gh readme", message => HandleReadmeCommandAsync(message, "gh readme"));
        GroupCommands.MapPrefix("ghreadme", message => HandleReadmeCommandAsync(message, "ghreadme"));

        // 订阅命令
        GroupCommands.MapPrefix("gh订阅列表", HandleListSubscriptionsAsync);
        GroupCommands.MapPrefix("gh订阅", message => HandleSubscribeAsync(message, "gh订阅"));
        GroupCommands.MapPrefix("gh退订", message => HandleUnsubscribeAsync(message, "gh退订"));

        // 链接触发
        GroupCommands.MapWhen(
            message => GitHubLinkParser.TryParse(message.GetPlainText(), out _),
            HandleGroupGitHubRenderAsync);

        BotLog.Info("Github 插件已加载:链接渲染 + 订阅推送。");
        return Task.CompletedTask;
    }

    protected override async Task OnUnloadAsync()
    {
        if (_poller is not null)
        {
            await _poller.StopAsync().ConfigureAwait(false);
            _poller = null;
        }

        _configWatch?.Dispose();
        _configWatch = null;
        _api.Dispose();
    }

    private void ApplyConfig(PluginConfig config)
    {
        _api.SetToken(config.GithubToken);
        _pages.MaxBodyLength = Math.Clamp(config.MaxBodyLength, 500, 20000);
        _pages.ListItemCount = Math.Clamp(config.ListItemCount, 3, 15);
        ApplyPollerConfig(config);
    }

    private void ApplyPollerConfig(PluginConfig config)
    {
        if (_poller is not null)
        {
            _poller.Interval = TimeSpan.FromMinutes(Math.Clamp(config.PollIntervalMinutes, 1, 120));
        }
    }

    // ---------- 订阅命令 ----------

    private async Task HandleSubscribeAsync(MessageEvent message, string prefix)
    {
        if (!EnsureAdmin(message, out var groupId))
        {
            await Context.Message.QuoteReplyAsync(message, "只有 Bot 管理员可以管理订阅。");
            return;
        }

        var args = StripPrefix(message.GetPlainText(), prefix)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length == 0 || !TryParseSlug(args[0], out var owner, out var repo))
        {
            await Context.Message.QuoteReplyAsync(message,
                $"用法: gh订阅 owner/repo [事件...]\n事件可选: {SubscribedEventsExtensions.ValidNames()},缺省为 release。");
            return;
        }

        var events = SubscribedEvents.None;
        foreach (var arg in args.Skip(1))
        {
            if (!SubscribedEventsExtensions.TryParseEvent(arg, out var parsed))
            {
                await Context.Message.QuoteReplyAsync(message,
                    $"未知事件类型「{arg}」,可选: {SubscribedEventsExtensions.ValidNames()}");
                return;
            }

            events |= parsed;
        }

        if (events == SubscribedEvents.None)
        {
            events = SubscribedEvents.Release;
        }

        // 校验仓库存在(顺带暖游标)
        try
        {
            using var _ = await _api.GetJsonAsync($"repos/{owner}/{repo}").ConfigureAwait(false);
        }
        catch (GitHubNotFoundException)
        {
            await Context.Message.QuoteReplyAsync(message, $"仓库 {owner}/{repo} 不存在或不可访问。");
            return;
        }
        catch (Exception ex)
        {
            await Context.Message.QuoteReplyAsync(message, $"校验仓库失败: {ex.Message}");
            return;
        }

        var changed = _subscriptions!.Subscribe(groupId, owner, repo, events);
        await Context.Message.QuoteReplyAsync(message, changed
            ? $"已订阅 {owner}/{repo} [{events.Describe()}],新事件将推送到本群。"
            : $"本群已订阅 {owner}/{repo} 的这些事件,无需重复订阅。");
    }

    private async Task HandleUnsubscribeAsync(MessageEvent message, string prefix)
    {
        if (!EnsureAdmin(message, out var groupId))
        {
            await Context.Message.QuoteReplyAsync(message, "只有 Bot 管理员可以管理订阅。");
            return;
        }

        var arg = StripPrefix(message.GetPlainText(), prefix);
        if (!TryParseSlug(arg, out var owner, out var repo))
        {
            await Context.Message.QuoteReplyAsync(message, "用法: gh退订 owner/repo");
            return;
        }

        var removed = _subscriptions!.Unsubscribe(groupId, owner, repo);
        await Context.Message.QuoteReplyAsync(message, removed
            ? $"已退订 {owner}/{repo}。"
            : $"本群未订阅 {owner}/{repo}。");
    }

    private async Task HandleListSubscriptionsAsync(MessageEvent message)
    {
        if (message.IsDirect)
        {
            return;
        }

        var entries = _subscriptions!.GetGroupSubscriptions(message.Channel.Id);
        if (entries.Count == 0)
        {
            await Context.Message.QuoteReplyAsync(message, "本群暂无 GitHub 订阅。使用「gh订阅 owner/repo [事件...]」添加。");
            return;
        }

        var lines = entries.Select(entry => $"· {entry.Slug} [{entry.Events.Describe()}]");
        await Context.Message.QuoteReplyAsync(message, "本群 GitHub 订阅:\n" + string.Join('\n', lines));
    }

    private bool EnsureAdmin(MessageEvent message, out string groupId)
    {
        groupId = message.Channel.Id;
        return !message.IsDirect && Context.IsAdmin(message.Sender.Id);
    }

    private static bool TryParseSlug(string text, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        (owner, repo) = (parts[0], parts[1]);
        return true;
    }

    // ---------- 订阅推送 ----------

    private async Task HandleNotificationAsync(SubscriptionNotification notification)
    {
        var (owner, repo) = (notification.Owner, notification.Repository);
        var slug = $"{owner}/{repo}";

        var (prefix, render) = notification.Kind switch
        {
            NotificationKind.Release => (
                $"📦 {slug} 发布了新版本 {notification.Reference}",
                RenderFor<ReleaseCard>(Fetch(() => _pages.GetReleaseCardAsync(owner, repo, notification.Reference)))),
            NotificationKind.IssueOpened => (
                $"🐛 {slug} 新 Issue #{notification.Number}",
                RenderFor<IssueDetailCard>(Fetch(() => _pages.GetIssueCardAsync(owner, repo, notification.Number)))),
            NotificationKind.IssueClosed => (
                $"✅ {slug} Issue #{notification.Number} 已关闭",
                RenderFor<IssueDetailCard>(Fetch(() => _pages.GetIssueCardAsync(owner, repo, notification.Number)))),
            NotificationKind.PrOpened => (
                $"🔀 {slug} 新 PR #{notification.Number}",
                RenderFor<IssueDetailCard>(Fetch(() => _pages.GetPullRequestCardAsync(owner, repo, notification.Number)))),
            NotificationKind.PrMerged => (
                $"🎉 {slug} PR #{notification.Number} 已合并",
                RenderFor<IssueDetailCard>(Fetch(() => _pages.GetPullRequestCardAsync(owner, repo, notification.Number)))),
            NotificationKind.PrClosed => (
                $"🚫 {slug} PR #{notification.Number} 已关闭",
                RenderFor<IssueDetailCard>(Fetch(() => _pages.GetPullRequestCardAsync(owner, repo, notification.Number)))),
            NotificationKind.Commits => (
                $"📝 {slug} {notification.Summary}",
                RenderFor<CommitCard>(Fetch(() => _pages.GetCommitCardAsync(owner, repo, notification.Reference!)))),
            NotificationKind.ActionFailure => (
                $"❌ {slug} workflow 运行失败",
                RenderFor<RunDetailCard>(Fetch(() => _pages.GetRunCardAsync(owner, repo, notification.Number)))),
            NotificationKind.StarMilestone => (
                $"⭐ {slug} Star 数突破 {notification.Summary}!",
                RenderFor<DescriptionCard>(Fetch(() => _repoClient.GetRepositoryCardAsync(owner, repo)))),
            NotificationKind.Digest => (
                $"📋 {slug} 动态: {notification.Summary}",
                null),
            _ => (null, null)
        };

        if (prefix is null)
        {
            return;
        }

        byte[]? png = null;
        if (Context.Render is not null && render is not null)
        {
            try
            {
                png = await render().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BotLog.Warning($"订阅推送渲染失败 [{notification.Kind}] {slug}: {ex.Message}");
            }
        }

        if (png is not null)
        {
            await Context.Message.SendGroupMessageAsync(
                notification.GroupId,
                new TextSegment(prefix),
                new ImageSegment("base64://" + Convert.ToBase64String(png)));
        }
        else
        {
            // 渲染不可用时降级为纯文本
            await Context.Message.SendGroupMessageAsync(notification.GroupId, prefix);
        }
    }

    private Func<Task<byte[]>> RenderFor<TControl>(Func<Task<object>> fetch) where TControl : Control, new()
        => async () => await RenderAsync<TControl>(await fetch().ConfigureAwait(false)).ConfigureAwait(false);

    // 包装:Task<T> → Task<object>(switch 表达式各分支 ViewModel 类型不同)
    private static Func<Task<object>> Fetch<T>(Func<Task<T>> fetch) where T : class
        => async () => await fetch().ConfigureAwait(false);

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
