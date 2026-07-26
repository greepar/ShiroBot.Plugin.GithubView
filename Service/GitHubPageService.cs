using System.Text;
using System.Text.Json;
using Avalonia.Media;
using ShiroBot.Plugin.GithubView.Views;

namespace ShiroBot.Plugin.GithubView.Service;

/// <summary>
/// 拉取 GitHub API 数据并组装各卡片 ViewModel。
/// </summary>
internal sealed class GitHubPageService(GitHubApiClient api)
{
    public int MaxBodyLength { get; set; } = 4000;
    public int ListItemCount { get; set; } = 8;

    // ---------- Issue / PR 详情 ----------

    public async Task<IssueDetailCardViewModel> GetIssueCardAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/issues/{number}", ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var state = root.GetStringOrNull("state") ?? "open";
        var stateReason = root.GetStringOrNull("state_reason");
        var isOpen = state == "open";
        var avatar = await api.GetImageAsync(root.GetStringOrNull("user", "avatar_url"), ct).ConfigureAwait(false);
        var body = Formatting.SanitizeMarkdown(root.GetStringOrNull("body"), MaxBodyLength, out var truncated);
        var created = root.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow;
        var comments = root.GetIntOrZero("comments");

        return new IssueDetailCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            Title = root.GetStringOrNull("title") ?? $"Issue #{number}",
            NumberText = $"#{number}",
            StateText = isOpen ? "Open" : stateReason == "not_planned" ? "Closed" : "Closed",
            StateIconData = isOpen ? Octicons.IssueOpened : Octicons.IssueClosed,
            StateColor = isOpen ? GitHubColors.OpenGreen : GitHubColors.ClosedPurple,
            AuthorLogin = root.GetStringOrNull("user", "login") ?? "ghost",
            AuthorAvatarBytes = avatar,
            MetaText = $"opened this issue {Formatting.Relative(created)}",
            CommentsText = $"{Formatting.Count(comments)} comment{(comments == 1 ? "" : "s")}",
            Body = body,
            BodyTruncated = truncated,
            Labels = ReadLabels(root)
        };
    }

    public async Task<IssueDetailCardViewModel> GetPullRequestCardAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/pulls/{number}", ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var state = root.GetStringOrNull("state") ?? "open";
        var merged = root.GetBoolOrFalse("merged");
        var isDraft = root.GetBoolOrFalse("draft");
        var avatar = await api.GetImageAsync(root.GetStringOrNull("user", "avatar_url"), ct).ConfigureAwait(false);
        var body = Formatting.SanitizeMarkdown(root.GetStringOrNull("body"), MaxBodyLength, out var truncated);
        var created = root.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow;
        var comments = root.GetIntOrZero("comments") + root.GetIntOrZero("review_comments");

        var (stateText, icon, color) = (merged, state, isDraft) switch
        {
            (true, _, _) => ("Merged", Octicons.GitMerge, GitHubColors.ClosedPurple),
            (_, "open", true) => ("Draft", Octicons.GitPullRequest, GitHubColors.NeutralGray),
            (_, "open", _) => ("Open", Octicons.GitPullRequest, GitHubColors.OpenGreen),
            _ => ("Closed", Octicons.GitPullRequest, GitHubColors.ClosedRed)
        };

        var head = root.GetStringOrNull("head", "label") ?? root.GetStringOrNull("head", "ref") ?? "?";
        var baseRef = root.GetStringOrNull("base", "ref") ?? "?";

        return new IssueDetailCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            Title = root.GetStringOrNull("title") ?? $"PR #{number}",
            NumberText = $"#{number}",
            StateText = stateText,
            StateIconData = icon,
            StateColor = color,
            AuthorLogin = root.GetStringOrNull("user", "login") ?? "ghost",
            AuthorAvatarBytes = avatar,
            MetaText = $"opened this pull request {Formatting.Relative(created)}",
            CommentsText = $"{Formatting.Count(comments)} comment{(comments == 1 ? "" : "s")}",
            Body = body,
            BodyTruncated = truncated,
            Labels = ReadLabels(root),
            IsPullRequest = true,
            BranchText = $"{baseRef} ← {head}",
            DiffStatText = $"{root.GetIntOrZero("changed_files")} files  +{Formatting.Count(root.GetIntOrZero("additions"))} −{Formatting.Count(root.GetIntOrZero("deletions"))}"
        };
    }

    // ---------- 列表 ----------

    public async Task<ListCardViewModel> GetIssueListCardAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/issues?state=open&per_page={ListItemCount * 2}", ct).ConfigureAwait(false);
        var items = new List<ListItem>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            // /issues 端点混含 PR,过滤掉
            if (element.TryGetProperty("pull_request", out _))
            {
                continue;
            }

            items.Add(new ListItem
            {
                IconData = Octicons.IssueOpened,
                IconColor = GitHubColors.OpenGreen,
                Title = element.GetStringOrNull("title") ?? "",
                SubText = $"#{element.GetIntOrZero("number")} opened {Formatting.Relative(element.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow)} by {element.GetStringOrNull("user", "login") ?? "ghost"}",
                RightText = CommentBadge(element.GetIntOrZero("comments"))
            });

            if (items.Count >= ListItemCount)
            {
                break;
            }
        }

        return new ListCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            TitleText = "Issues",
            SubTitleText = "open, recently updated",
            Items = items,
            EmptyText = "No open issues"
        };
    }

    public async Task<ListCardViewModel> GetPullRequestListCardAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/pulls?state=open&per_page={ListItemCount}", ct).ConfigureAwait(false);
        var items = new List<ListItem>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var isDraft = element.GetBoolOrFalse("draft");
            items.Add(new ListItem
            {
                IconData = Octicons.GitPullRequest,
                IconColor = isDraft ? GitHubColors.NeutralGray : GitHubColors.OpenGreen,
                Title = element.GetStringOrNull("title") ?? "",
                SubText = $"#{element.GetIntOrZero("number")} opened {Formatting.Relative(element.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow)} by {element.GetStringOrNull("user", "login") ?? "ghost"}",
                RightText = element.GetStringOrNull("base", "ref") ?? ""
            });
        }

        return new ListCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            TitleText = "Pull requests",
            SubTitleText = "open",
            Items = items,
            EmptyText = "No open pull requests"
        };
    }

    public async Task<ListCardViewModel> GetActionsListCardAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/actions/runs?per_page={ListItemCount}", ct).ConfigureAwait(false);
        var items = new List<ListItem>();
        if (doc.RootElement.TryGetProperty("workflow_runs", out var runs))
        {
            foreach (var run in runs.EnumerateArray())
            {
                var (icon, color, _) = RunStatusVisual(run);
                items.Add(new ListItem
                {
                    IconData = icon,
                    IconColor = color,
                    Title = run.GetStringOrNull("display_title") ?? run.GetStringOrNull("name") ?? "",
                    SubText = $"{run.GetStringOrNull("name")} #{run.GetIntOrZero("run_number")} · {run.GetStringOrNull("head_branch")} · {Formatting.Relative(run.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow)}",
                    RightText = run.GetStringOrNull("event") ?? ""
                });
            }
        }

        return new ListCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            TitleText = "Actions",
            SubTitleText = "recent workflow runs",
            Items = items,
            EmptyText = "No workflow runs"
        };
    }

    // ---------- Run 详情 ----------

    public async Task<RunDetailCardViewModel> GetRunCardAsync(string owner, string repo, long runId, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/actions/runs/{runId}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var (icon, color, statusText) = RunStatusVisual(root);

        var jobs = new List<JobItem>();
        try
        {
            using var jobsDoc = await api.GetJsonAsync($"repos/{owner}/{repo}/actions/runs/{runId}/jobs?per_page=20", ct).ConfigureAwait(false);
            if (jobsDoc.RootElement.TryGetProperty("jobs", out var jobArray))
            {
                foreach (var job in jobArray.EnumerateArray())
                {
                    var (jobIcon, jobColor, _) = RunStatusVisual(job);
                    var started = job.GetDateOrNull("started_at");
                    var completed = job.GetDateOrNull("completed_at");
                    jobs.Add(new JobItem
                    {
                        IconData = jobIcon,
                        IconColor = jobColor,
                        Name = job.GetStringOrNull("name") ?? "job",
                        DurationText = started is not null && completed is not null
                            ? Formatting.Duration(completed.Value - started.Value)
                            : ""
                    });
                }
            }
        }
        catch
        {
            // jobs 拉取失败不影响主卡
        }

        var runStarted = root.GetDateOrNull("run_started_at");
        var updated = root.GetDateOrNull("updated_at");

        return new RunDetailCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            WorkflowName = root.GetStringOrNull("name") ?? "workflow",
            RunTitle = root.GetStringOrNull("display_title") ?? "",
            RunNumberText = $"#{root.GetIntOrZero("run_number")}",
            StatusText = statusText,
            StatusIconData = icon,
            StatusColor = color,
            BranchText = root.GetStringOrNull("head_branch") ?? "?",
            CommitShaText = (root.GetStringOrNull("head_sha") ?? "").Length >= 7 ? root.GetStringOrNull("head_sha")![..7] : "",
            MetaText = $"{root.GetStringOrNull("event")} · {Formatting.Relative(root.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow)}",
            DurationText = runStarted is not null && updated is not null && updated > runStarted
                ? "took " + Formatting.Duration(updated.Value - runStarted.Value)
                : "",
            Jobs = jobs
        };
    }

    // ---------- Release ----------

    public async Task<ReleaseCardViewModel> GetReleaseCardAsync(string owner, string repo, string? tag, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(tag)
            ? $"repos/{owner}/{repo}/releases/latest"
            : $"repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";

        using var doc = await api.GetJsonAsync(path, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var avatar = await api.GetImageAsync(root.GetStringOrNull("author", "avatar_url"), ct).ConfigureAwait(false);
        var body = Formatting.SanitizeMarkdown(root.GetStringOrNull("body"), MaxBodyLength, out var truncated);
        var assetCount = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.GetArrayLength()
            : 0;
        var published = root.GetDateOrNull("published_at") ?? root.GetDateOrNull("created_at") ?? DateTimeOffset.UtcNow;
        var tagName = root.GetStringOrNull("tag_name") ?? tag ?? "";

        return new ReleaseCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            ReleaseName = string.IsNullOrWhiteSpace(root.GetStringOrNull("name")) ? tagName : root.GetStringOrNull("name")!,
            TagText = tagName,
            IsPreRelease = root.GetBoolOrFalse("prerelease"),
            IsLatest = string.IsNullOrEmpty(tag),
            AuthorLogin = root.GetStringOrNull("author", "login") ?? "ghost",
            AuthorAvatarBytes = avatar,
            MetaText = $"released {Formatting.Relative(published)}",
            AssetsText = assetCount > 0 ? $"{assetCount} asset{(assetCount == 1 ? "" : "s")}" : "",
            Body = body,
            BodyTruncated = truncated
        };
    }

    // ---------- Commit ----------

    public async Task<CommitCardViewModel> GetCommitCardAsync(string owner, string repo, string sha, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/commits/{Uri.EscapeDataString(sha)}", ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var message = root.GetStringOrNull("commit", "message") ?? "";
        var newline = message.IndexOf('\n');
        var title = newline < 0 ? message : message[..newline].TrimEnd();
        var bodyText = newline < 0 ? "" : message[(newline + 1)..].Trim();

        var avatar = await api.GetImageAsync(root.GetStringOrNull("author", "avatar_url"), ct).ConfigureAwait(false);
        var login = root.GetStringOrNull("author", "login");
        var date = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("commit", out var commit))
        {
            login ??= commit.GetStringOrNull("author", "name");
            if (commit.TryGetProperty("author", out var commitAuthor))
            {
                date = commitAuthor.GetDateOrNull("date") ?? date;
            }
        }

        var files = root.TryGetProperty("files", out var filesArray) && filesArray.ValueKind == JsonValueKind.Array
            ? filesArray.GetArrayLength()
            : 0;

        var additions = 0;
        var deletions = 0;
        if (root.TryGetProperty("stats", out var stats))
        {
            additions = stats.GetIntOrZero("additions");
            deletions = stats.GetIntOrZero("deletions");
        }

        var fullSha = root.GetStringOrNull("sha") ?? sha;

        return new CommitCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            MessageTitle = title,
            MessageBody = bodyText.Length > 800 ? bodyText[..800] + "…" : bodyText,
            ShaText = fullSha.Length >= 7 ? fullSha[..7] : fullSha,
            AuthorLogin = login ?? "ghost",
            AuthorAvatarBytes = avatar,
            MetaText = $"committed {Formatting.Relative(date)}",
            FilesText = $"{files} file{(files == 1 ? "" : "s")} changed",
            AdditionsText = $"+{Formatting.Count(additions)}",
            DeletionsText = $"−{Formatting.Count(deletions)}"
        };
    }

    // ---------- README ----------

    public async Task<ReadmeCardViewModel> GetReadmeCardAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var doc = await api.GetJsonAsync($"repos/{owner}/{repo}/readme", ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var content = root.GetStringOrNull("content") ?? "";
        var markdown = Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
        markdown = Formatting.SanitizeMarkdown(markdown, MaxBodyLength * 2, out var truncated);

        return new ReadmeCardViewModel
        {
            RepoSlug = $"{owner}/{repo}",
            FileName = root.GetStringOrNull("name") ?? "README.md",
            Markdown = markdown,
            Truncated = truncated
        };
    }

    // ---------- helpers ----------

    private static IReadOnlyList<LabelChip> ReadLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labels.EnumerateArray()
            .Select(label => LabelChip.FromHex(
                label.GetStringOrNull("name") ?? "",
                label.GetStringOrNull("color")))
            .Where(chip => chip.Name.Length > 0)
            .Take(10)
            .ToArray();
    }

    private static (string Icon, Color Color, string Text) RunStatusVisual(JsonElement runOrJob)
    {
        var status = runOrJob.GetStringOrNull("status") ?? "";
        var conclusion = runOrJob.GetStringOrNull("conclusion") ?? "";

        if (status is "queued" or "waiting" or "pending")
        {
            return (Octicons.DotFill, GitHubColors.WarningYellow, "Queued");
        }

        if (status == "in_progress")
        {
            return (Octicons.Play, GitHubColors.WarningYellow, "In progress");
        }

        return conclusion switch
        {
            "success" => (Octicons.CheckCircleFill, GitHubColors.OpenGreen, "Success"),
            "failure" => (Octicons.XCircleFill, GitHubColors.ClosedRed, "Failure"),
            "cancelled" => (Octicons.StopCircle, GitHubColors.NeutralGray, "Cancelled"),
            "skipped" => (Octicons.SkipCircle, GitHubColors.NeutralGray, "Skipped"),
            "timed_out" => (Octicons.XCircleFill, GitHubColors.ClosedRed, "Timed out"),
            "action_required" => (Octicons.DotFill, GitHubColors.WarningYellow, "Action required"),
            _ => (Octicons.DotFill, GitHubColors.NeutralGray, string.IsNullOrEmpty(conclusion) ? status : conclusion)
        };
    }

    // headless 渲染下 emoji 字体不保证可用,用纯文本
    private static string CommentBadge(int comments) => comments > 0 ? $"{Formatting.Count(comments)} comments" : "";
}
