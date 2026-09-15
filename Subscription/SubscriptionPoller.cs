using System.Text.Json;
using ShiroBot.Plugin.GithubView.Service;

namespace ShiroBot.Plugin.GithubView.Subscription;

internal enum NotificationKind
{
    Release,
    IssueOpened,
    IssueClosed,
    PrOpened,
    PrMerged,
    PrClosed,
    Commits,
    ActionFailure,
    StarMilestone,

    /// <summary>单轮通知超限后的合并摘要(纯文本,Summary 为摘要内容)。</summary>
    Digest
}

internal sealed record SubscriptionNotification(
    string GroupId,
    string Owner,
    string Repository,
    NotificationKind Kind,
    long Number = 0,
    string? Reference = null,
    string Summary = "");

/// <summary>
/// 订阅轮询:后台循环拉取各订阅仓库的最新数据,与游标 diff 后产出通知。
/// 同一轮询周期内同仓库数据只拉一次(多群订阅同仓库时共享)。
/// </summary>
internal sealed class SubscriptionPoller(
    GitHubApiClient api,
    SubscriptionStore store,
    Func<SubscriptionNotification, Task> onNotification,
    Action<string> logWarning)
{
    private const int StarMilestoneStep = 1000;

    /// <summary>单仓库单轮最多推送的图片通知数,超出合并为一条文字摘要(防刷屏)。</summary>
    public int MaxNotificationsPerRepo { get; set; } = 5;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            if (_loopTask is not null)
            {
                await _loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logWarning($"订阅轮询周期异常: {ex.Message}");
            }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var snapshot = store.Snapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        // 同仓库共享一次拉取
        var cache = new RepoDataCache(api);

        foreach (var (groupId, entry) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var notifications = await DiffEntryAsync(groupId, entry, cache, ct).ConfigureAwait(false);
                store.UpdateCursors(groupId, entry);

                if (notifications.Count > MaxNotificationsPerRepo)
                {
                    notifications = Digest(groupId, entry, notifications);
                }

                foreach (var notification in notifications)
                {
                    try
                    {
                        await onNotification(notification).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logWarning($"订阅推送失败 [{notification.Kind}] {entry.Slug} → 群 {groupId}: {ex.Message}");
                    }
                }
            }
            catch (GitHubRateLimitException)
            {
                logWarning("GitHub API 限流,本轮订阅轮询中止。");
                return;
            }
            catch (Exception ex)
            {
                logWarning($"订阅轮询失败 {entry.Slug}: {ex.Message}");
            }
        }
    }

    private static async Task<List<SubscriptionNotification>> DiffEntryAsync(
        string groupId,
        SubscriptionEntry entry,
        RepoDataCache cache,
        CancellationToken ct)
    {
        var result = new List<SubscriptionNotification>();
        var (owner, repo) = (entry.Owner, entry.Repository);

        if (entry.Events.HasFlag(SubscribedEvents.Release))
        {
            var release = await cache.GetLatestReleaseAsync(owner, repo, ct).ConfigureAwait(false);
            if (release is not null)
            {
                if (entry.LastReleaseId is { } lastId && release.Id != lastId)
                {
                    result.Add(new SubscriptionNotification(groupId, owner, repo, NotificationKind.Release, Reference: release.Tag));
                }

                entry.LastReleaseId = release.Id;
            }
        }

        if (entry.Events.HasFlag(SubscribedEvents.Issue) || entry.Events.HasFlag(SubscribedEvents.PullRequest))
        {
            var issues = await cache.GetRecentIssuesAsync(owner, repo, ct).ConfigureAwait(false);
            DiffIssues(groupId, entry, issues, result);
        }

        if (entry.Events.HasFlag(SubscribedEvents.Commit))
        {
            var commits = await cache.GetRecentCommitsAsync(owner, repo, ct).ConfigureAwait(false);
            DiffCommits(groupId, entry, commits, result);
        }

        if (entry.Events.HasFlag(SubscribedEvents.ActionFailure))
        {
            var run = await cache.GetLatestFailedRunAsync(owner, repo, ct).ConfigureAwait(false);
            if (run is not null)
            {
                if (entry.LastFailedRunId is { } lastRun && run.Value > lastRun)
                {
                    result.Add(new SubscriptionNotification(groupId, owner, repo, NotificationKind.ActionFailure, Number: run.Value));
                }

                entry.LastFailedRunId = Math.Max(run.Value, entry.LastFailedRunId ?? 0);
            }
        }

        if (entry.Events.HasFlag(SubscribedEvents.StarMilestone))
        {
            var stars = await cache.GetStarCountAsync(owner, repo, ct).ConfigureAwait(false);
            var milestone = stars / StarMilestoneStep;
            if (entry.LastStarMilestone is { } lastMilestone && milestone > lastMilestone)
            {
                result.Add(new SubscriptionNotification(
                    groupId, owner, repo, NotificationKind.StarMilestone,
                    Summary: $"{milestone * StarMilestoneStep:N0}"));
            }

            entry.LastStarMilestone = Math.Max(milestone, entry.LastStarMilestone ?? 0);
        }

        return result;
    }

    private static void DiffIssues(
        string groupId,
        SubscriptionEntry entry,
        IReadOnlyList<IssueActivity> issues,
        List<SubscriptionNotification> result)
    {
        var wantIssue = entry.Events.HasFlag(SubscribedEvents.Issue);
        var wantPr = entry.Events.HasFlag(SubscribedEvents.PullRequest);
        var issueCursor = entry.IssueCursor;
        var prCursor = entry.PullRequestCursor;

        foreach (var issue in issues)
        {
            var cursor = issue.IsPullRequest ? prCursor : issueCursor;
            var want = issue.IsPullRequest ? wantPr : wantIssue;
            if (!want || cursor is null || issue.UpdatedAt <= cursor)
            {
                continue;
            }

            if (issue.CreatedAt > cursor)
            {
                result.Add(new SubscriptionNotification(
                    groupId, entry.Owner, entry.Repository,
                    issue.IsPullRequest ? NotificationKind.PrOpened : NotificationKind.IssueOpened,
                    Number: issue.Number));
            }
            else if (issue.ClosedAt > cursor)
            {
                result.Add(new SubscriptionNotification(
                    groupId, entry.Owner, entry.Repository,
                    issue.IsPullRequest
                        ? issue.Merged ? NotificationKind.PrMerged : NotificationKind.PrClosed
                        : NotificationKind.IssueClosed,
                    Number: issue.Number));
            }
        }

        var maxUpdated = issues.Count > 0 ? issues.Max(issue => issue.UpdatedAt) : DateTimeOffset.UtcNow;
        if (wantIssue)
        {
            entry.IssueCursor = Max(entry.IssueCursor, maxUpdated);
        }

        if (wantPr)
        {
            entry.PullRequestCursor = Max(entry.PullRequestCursor, maxUpdated);
        }
    }

    private static void DiffCommits(
        string groupId,
        SubscriptionEntry entry,
        IReadOnlyList<CommitActivity> commits,
        List<SubscriptionNotification> result)
    {
        if (entry.CommitCursor is { } cursor)
        {
            var fresh = commits.Where(commit => commit.Date > cursor).ToArray();
            if (fresh.Length > 0)
            {
                // 多条 commit 合并为一条通知,渲染最新那条的详情卡
                result.Add(new SubscriptionNotification(
                    groupId, entry.Owner, entry.Repository, NotificationKind.Commits,
                    Reference: fresh[0].Sha,
                    Summary: fresh.Length == 1 ? "1 条新提交" : $"{fresh.Length} 条新提交"));
            }
        }

        if (commits.Count > 0)
        {
            entry.CommitCursor = Max(entry.CommitCursor, commits.Max(commit => commit.Date));
        }
        else
        {
            entry.CommitCursor ??= DateTimeOffset.UtcNow;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b)
        => a is { } value && value > b ? value : b;

    /// <summary>
    /// 通知超限:Release 保留单独推送(低频高价值),其余合并为一条文字摘要。
    /// </summary>
    private static List<SubscriptionNotification> Digest(
        string groupId,
        SubscriptionEntry entry,
        List<SubscriptionNotification> notifications)
    {
        var kept = notifications.Where(n => n.Kind == NotificationKind.Release).ToList();
        var rest = notifications.Where(n => n.Kind != NotificationKind.Release).ToArray();

        var counts = rest
            .GroupBy(n => n.Kind)
            .Select(g => g.Key switch
            {
                NotificationKind.IssueOpened => $"{g.Count()} 个新 Issue",
                NotificationKind.IssueClosed => $"{g.Count()} 个 Issue 关闭",
                NotificationKind.PrOpened => $"{g.Count()} 个新 PR",
                NotificationKind.PrMerged => $"{g.Count()} 个 PR 合并",
                NotificationKind.PrClosed => $"{g.Count()} 个 PR 关闭",
                NotificationKind.Commits => g.First().Summary,
                NotificationKind.ActionFailure => "workflow 运行失败",
                NotificationKind.StarMilestone => $"Star 数突破 {g.First().Summary}",
                _ => null
            })
            .Where(text => text is not null);

        kept.Add(new SubscriptionNotification(
            groupId, entry.Owner, entry.Repository, NotificationKind.Digest,
            Summary: string.Join(",", counts)));
        return kept;
    }

    // ---------- 单周期仓库数据缓存 ----------

    private sealed record ReleaseSummary(long Id, string Tag);

    private sealed record IssueActivity(
        long Number,
        bool IsPullRequest,
        bool Merged,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ClosedAt);

    private sealed record CommitActivity(string Sha, DateTimeOffset Date);

    private sealed class RepoDataCache(GitHubApiClient api)
    {
        private readonly Dictionary<string, ReleaseSummary?> _releases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<IssueActivity>> _issues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<CommitActivity>> _commits = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long?> _failedRuns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _stars = new(StringComparer.OrdinalIgnoreCase);

        public async Task<ReleaseSummary?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
        {
            var key = $"{owner}/{repo}";
            if (_releases.TryGetValue(key, out var cached))
            {
                return cached;
            }

            ReleaseSummary? release = null;
            try
            {
                using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/releases/latest", ct).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var releaseId))
                {
                    release = new ReleaseSummary(releaseId, root.GetStringOrNull("tag_name") ?? "");
                }
            }
            catch (GitHubNotFoundException)
            {
                // 无 release
            }

            _releases[key] = release;
            return release;
        }

        public async Task<IReadOnlyList<IssueActivity>> GetRecentIssuesAsync(string owner, string repo, CancellationToken ct)
        {
            var key = $"{owner}/{repo}";
            if (_issues.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var list = new List<IssueActivity>();
            using (var doc = await api.GetJsonAsync(
                       $"repos/{owner}/{repo}/issues?state=all&sort=updated&direction=desc&per_page=30", ct).ConfigureAwait(false))
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var isPr = element.TryGetProperty("pull_request", out var prObj);
                    var merged = isPr
                                 && prObj.ValueKind == JsonValueKind.Object
                                 && prObj.TryGetProperty("merged_at", out var mergedAt)
                                 && mergedAt.ValueKind == JsonValueKind.String;
                    list.Add(new IssueActivity(
                        element.GetIntOrZero("number"),
                        isPr,
                        merged,
                        element.GetDateOrNull("created_at") ?? DateTimeOffset.MinValue,
                        element.GetDateOrNull("updated_at") ?? DateTimeOffset.MinValue,
                        element.GetDateOrNull("closed_at")));
                }
            }

            _issues[key] = list;
            return list;
        }

        public async Task<IReadOnlyList<CommitActivity>> GetRecentCommitsAsync(string owner, string repo, CancellationToken ct)
        {
            var key = $"{owner}/{repo}";
            if (_commits.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var list = new List<CommitActivity>();
            try
            {
                using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/commits?per_page=30", ct).ConfigureAwait(false);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    DateTimeOffset? date = null;
                    if (element.TryGetProperty("commit", out var commit)
                        && commit.TryGetProperty("committer", out var committer))
                    {
                        date = committer.GetDateOrNull("date");
                    }

                    var sha = element.GetStringOrNull("sha");
                    if (sha is not null && date is not null)
                    {
                        list.Add(new CommitActivity(sha, date.Value));
                    }
                }
            }
            catch (GitHubNotFoundException)
            {
                // 空仓库
            }

            _commits[key] = list;
            return list;
        }

        public async Task<long?> GetLatestFailedRunAsync(string owner, string repo, CancellationToken ct)
        {
            var key = $"{owner}/{repo}";
            if (_failedRuns.TryGetValue(key, out var cached))
            {
                return cached;
            }

            long? runId = null;
            using (var doc = await api.GetJsonAsync(
                       $"repos/{owner}/{repo}/actions/runs?status=failure&per_page=1", ct).ConfigureAwait(false))
            {
                if (doc.RootElement.TryGetProperty("workflow_runs", out var runs))
                {
                    foreach (var run in runs.EnumerateArray())
                    {
                        if (run.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
                        {
                            runId = value;
                        }

                        break;
                    }
                }
            }

            _failedRuns[key] = runId;
            return runId;
        }

        public async Task<int> GetStarCountAsync(string owner, string repo, CancellationToken ct)
        {
            var key = $"{owner}/{repo}";
            if (_stars.TryGetValue(key, out var cached))
            {
                return cached;
            }

            using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}", ct).ConfigureAwait(false);
            var stars = doc.RootElement.GetIntOrZero("stargazers_count");
            _stars[key] = stars;
            return stars;
        }
    }
}
