using System.Text.Json;

namespace ShiroBot.Plugin.GithubView.Subscription;

/// <summary>
/// 订阅项持久化:PluginDirectory/data/subscriptions.json,原子写(tmp + File.Replace)。
/// 所有公开方法线程安全(轮询线程与命令处理并发访问)。
/// </summary>
internal sealed class SubscriptionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private SubscriptionData _data = new();

    public SubscriptionStore(string pluginDirectory)
    {
        var dataDir = Path.Combine(pluginDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "subscriptions.json");
        Load();
    }

    public bool Subscribe(string groupId, string owner, string repository, SubscribedEvents events)
    {
        lock (_lock)
        {
            var list = GetOrAddGroup(groupId);
            var entry = Find(list, owner, repository);
            if (entry is null)
            {
                list.Add(new SubscriptionEntry
                {
                    Owner = owner,
                    Repository = repository,
                    Events = events
                });
                Save();
                return true;
            }

            // 已订阅:合并事件
            var merged = entry.Events | events;
            if (merged == entry.Events)
            {
                return false;
            }

            entry.Events = merged;
            Save();
            return true;
        }
    }

    public bool Unsubscribe(string groupId, string owner, string repository)
    {
        lock (_lock)
        {
            if (!_data.Groups.TryGetValue(groupId, out var list))
            {
                return false;
            }

            var entry = Find(list, owner, repository);
            if (entry is null)
            {
                return false;
            }

            list.Remove(entry);
            if (list.Count == 0)
            {
                _data.Groups.Remove(groupId);
            }

            Save();
            return true;
        }
    }

    public IReadOnlyList<SubscriptionEntry> GetGroupSubscriptions(string groupId)
    {
        lock (_lock)
        {
            return _data.Groups.TryGetValue(groupId, out var list)
                ? list.Select(Clone).ToArray()
                : [];
        }
    }

    /// <summary>
    /// 快照全部订阅:groupId → entries(深拷贝,轮询遍历时不持锁)。
    /// </summary>
    public IReadOnlyList<(string GroupId, SubscriptionEntry Entry)> Snapshot()
    {
        lock (_lock)
        {
            return _data.Groups
                .SelectMany(pair => pair.Value.Select(entry => (pair.Key, Clone(entry))))
                .ToArray();
        }
    }

    /// <summary>
    /// 轮询后回写游标(按 group+repo 定位;订阅可能在轮询期间被退订,此时静默忽略)。
    /// </summary>
    public void UpdateCursors(string groupId, SubscriptionEntry updated)
    {
        lock (_lock)
        {
            if (!_data.Groups.TryGetValue(groupId, out var list))
            {
                return;
            }

            var entry = Find(list, updated.Owner, updated.Repository);
            if (entry is null)
            {
                return;
            }

            entry.LastReleaseId = updated.LastReleaseId;
            entry.IssueCursor = updated.IssueCursor;
            entry.PullRequestCursor = updated.PullRequestCursor;
            entry.CommitCursor = updated.CommitCursor;
            entry.LastFailedRunId = updated.LastFailedRunId;
            entry.LastStarMilestone = updated.LastStarMilestone;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                _data = JsonSerializer.Deserialize<SubscriptionData>(File.ReadAllText(_filePath), JsonOptions) ?? new SubscriptionData();
            }
        }
        catch
        {
            // 损坏文件不阻塞插件加载;保留原文件便于排查,写入时会被覆盖
            _data = new SubscriptionData();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_data, JsonOptions);
        var tmpPath = _filePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        if (File.Exists(_filePath))
        {
            File.Replace(tmpPath, _filePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmpPath, _filePath);
        }
    }

    private List<SubscriptionEntry> GetOrAddGroup(string groupId)
    {
        var key = groupId;
        if (!_data.Groups.TryGetValue(key, out var list))
        {
            list = [];
            _data.Groups[key] = list;
        }

        return list;
    }

    private static SubscriptionEntry? Find(List<SubscriptionEntry> list, string owner, string repository)
        => list.FirstOrDefault(entry =>
            string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Repository, repository, StringComparison.OrdinalIgnoreCase));

    private static SubscriptionEntry Clone(SubscriptionEntry entry) => new()
    {
        Owner = entry.Owner,
        Repository = entry.Repository,
        Events = entry.Events,
        LastReleaseId = entry.LastReleaseId,
        IssueCursor = entry.IssueCursor,
        PullRequestCursor = entry.PullRequestCursor,
        CommitCursor = entry.CommitCursor,
        LastFailedRunId = entry.LastFailedRunId,
        LastStarMilestone = entry.LastStarMilestone
    };
}
