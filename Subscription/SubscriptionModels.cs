using System.Text.Json.Serialization;

namespace ShiroBot.Plugin.GithubView.Subscription;

[Flags]
internal enum SubscribedEvents
{
    None = 0,
    Release = 1,
    Issue = 2,
    PullRequest = 4,
    Commit = 8,
    ActionFailure = 16,
    StarMilestone = 32,
    All = Release | Issue | PullRequest | Commit | ActionFailure | StarMilestone
}

internal static class SubscribedEventsExtensions
{
    private static readonly (SubscribedEvents Flag, string Name)[] Names =
    [
        (SubscribedEvents.Release, "release"),
        (SubscribedEvents.Issue, "issue"),
        (SubscribedEvents.PullRequest, "pr"),
        (SubscribedEvents.Commit, "commit"),
        (SubscribedEvents.ActionFailure, "action"),
        (SubscribedEvents.StarMilestone, "star")
    ];

    public static bool TryParseEvent(string text, out SubscribedEvents parsed)
    {
        parsed = SubscribedEvents.None;
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "全部", StringComparison.OrdinalIgnoreCase))
        {
            parsed = SubscribedEvents.All;
            return true;
        }

        foreach (var (flag, name) in Names)
        {
            if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
            {
                parsed = flag;
                return true;
            }
        }

        return false;
    }

    public static string Describe(this SubscribedEvents events)
    {
        if (events == SubscribedEvents.All)
        {
            return "all";
        }

        var parts = Names.Where(pair => events.HasFlag(pair.Flag)).Select(pair => pair.Name);
        return string.Join("+", parts);
    }

    public static string ValidNames() => string.Join("/", Names.Select(pair => pair.Name)) + "/all";
}

/// <summary>
/// 单个「群 × 仓库」订阅项,含各事件类型的轮询游标。
/// 游标为 null 表示尚未初始化(首次轮询只记录、不推送,避免刷屏)。
/// </summary>
internal sealed class SubscriptionEntry
{
    public required string Owner { get; set; }
    public required string Repository { get; set; }
    public SubscribedEvents Events { get; set; } = SubscribedEvents.Release;

    // 游标
    public long? LastReleaseId { get; set; }
    public DateTimeOffset? IssueCursor { get; set; }
    public DateTimeOffset? PullRequestCursor { get; set; }
    public DateTimeOffset? CommitCursor { get; set; }
    public long? LastFailedRunId { get; set; }
    public int? LastStarMilestone { get; set; }

    [JsonIgnore]
    public string Slug => $"{Owner}/{Repository}";
}

internal sealed class SubscriptionData
{
    // key: 群/频道 Id(SDK 中为 string)
    public Dictionary<string, List<SubscriptionEntry>> Groups { get; set; } = [];
}
