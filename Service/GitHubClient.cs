using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using ShiroBot.Plugin.GithubView.Markdown;
using ShiroBot.Plugin.GithubView.Views;

namespace ShiroBot.Plugin.GithubView.Service;

/// <summary>GitHub API 请求失败时给用户看的提示。</summary>
internal sealed class GitHubApiException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class GitHubClient
{
    private const string UserAgent = "ShiroBot-GithubView";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly HttpClient ImageHttp = CreateImageHttpClient();

    /// <summary>可选的 GitHub Token；为空时匿名访问（60 次/小时）。</summary>
    public string? Token { get; set; }

    #region Repository

    public async Task<DescriptionCardViewModel> GetRepositoryCardAsync(
        string owner,
        string repository,
        CancellationToken ct = default)
    {
        // 子请求互不依赖，并行发出以缩短等待时间。
        var repoTask = GetJsonAsync($"repos/{owner}/{repository}", ct);
        var contributorsTask = GetContributorCountOrDefaultAsync(owner, repository, ct);
        var languagesTask = GetLanguagesOrDefaultAsync(owner, repository, ct);

        using var document = await repoTask.ConfigureAwait(false);
        var root = document.RootElement;
        var avatarBytes = await GetAvatarBytesAsync(GetString(root, "owner", "avatar_url"), ct).ConfigureAwait(false);
        var contributors = await contributorsTask.ConfigureAwait(false);
        var languages = await languagesTask.ConfigureAwait(false);

        return new DescriptionCardViewModel
        {
            Owner = GetString(root, "owner", "login") ?? owner,
            Repository = GetString(root, "name") ?? repository,
            Description = GetString(root, "description") ?? "No description provided.",
            Contributors = FormatCount(contributors),
            Issues = FormatCount(GetInt(root, "open_issues_count")),
            Watchers = FormatCount(GetInt(root, "subscribers_count")),
            Stars = FormatCount(GetInt(root, "stargazers_count")),
            Forks = FormatCount(GetInt(root, "forks_count")),
            PrimaryLanguage = languages.PrimaryLanguage,
            AvatarBytes = avatarBytes ?? (string.Equals(owner, "ShirokaProject", StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(repository, "ShiroBot", StringComparison.OrdinalIgnoreCase)
                ? DescriptionCardViewModel.LoadDefaultAvatarBytes()
                : null),
            Language1Width = languages[0].Width,
            Language2Width = languages[1].Width,
            Language3Width = languages[2].Width,
            Language4Width = languages[3].Width,
            Language5Width = languages[4].Width,
            Language1Color = languages[0].Color,
            Language2Color = languages[1].Color,
            Language3Color = languages[2].Color,
            Language4Color = languages[3].Color,
            Language5Color = languages[4].Color,
        };
    }

    private async Task<LanguageBar> GetLanguagesAsync(string owner, string repository, CancellationToken ct)
    {
        using var document = await GetJsonAsync($"repos/{owner}/{repository}/languages", ct).ConfigureAwait(false);
        var languages = document.RootElement.EnumerateObject()
            .Select(property => new
            {
                property.Name,
                Bytes = property.Value.TryGetInt64(out var value) ? value : 0
            })
            .Where(language => language.Bytes > 0)
            .OrderByDescending(language => language.Bytes)
            .Take(5)
            .ToArray();

        var total = languages.Sum(language => language.Bytes);
        var result = new LanguageBarSegment[5];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = i < languages.Length && total > 0
                ? new LanguageBarSegment(
                    new GridLength(languages[i].Bytes, GridUnitType.Star),
                    GetLanguageColor(languages[i].Name))
                : new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent);
        }

        var primaryLanguage = languages.Length > 0 && total > 0
            ? languages[0].Name
            : "Unknown";

        return new LanguageBar(result, primaryLanguage);
    }

    private async Task<LanguageBar> GetLanguagesOrDefaultAsync(string owner, string repository, CancellationToken ct)
    {
        try
        {
            return await GetLanguagesAsync(owner, repository, ct).ConfigureAwait(false);
        }
        catch
        {
            return new LanguageBar([
                new LanguageBarSegment(new GridLength(1, GridUnitType.Star), Color.Parse("#8C959F")),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent),
                new LanguageBarSegment(new GridLength(0, GridUnitType.Star), Colors.Transparent)
            ], "Unknown");
        }
    }

    private async Task<int> GetContributorCountOrDefaultAsync(string owner, string repository, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync($"repos/{owner}/{repository}/contributors?per_page=1&anon=true", ct)
                .ConfigureAwait(false);

            if (response.Headers.TryGetValues("Link", out var values) && TryReadLastPage(values) is { } count)
            {
                return count;
            }

            // 仓库过大时 GitHub 返回 403 / 204，这里统一降级为 0。
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Issue / Pull Request

    public async Task<IssueCardViewModel> GetIssueCardAsync(
        GitHubLink link,
        int commentCount,
        CancellationToken ct = default)
    {
        var basePath = $"repos/{link.Owner}/{link.Repository}";

        // issues 端点同时返回 Issue 和 PR（PR 带 pull_request 字段），并且包含 reactions / labels。
        // 已知是 PR 链接时并行请求 pulls 端点；评论与正文并行拉取。
        var issueTask = GetJsonAsync($"{basePath}/issues/{link.Number}", ct);
        var pullTask = link.Kind == GitHubLinkKind.PullRequest
            ? GetJsonAsync($"{basePath}/pulls/{link.Number}", ct)
            : null;
        var commentsTask = commentCount > 0
            ? GetJsonOrDefaultAsync($"{basePath}/issues/{link.Number}/comments?per_page={commentCount}", ct)
            : Task.FromResult<JsonDocument?>(null);

        using var issueDocument = await issueTask.ConfigureAwait(false);
        var issue = issueDocument.RootElement;

        if (pullTask is null && issue.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            // /issues/N 实际指向一个 PR，GitHub 网页也会跳转到 /pull/N。
            pullTask = GetJsonAsync($"{basePath}/pulls/{link.Number}", ct);
        }

        using var pullDocument = pullTask is null ? null : await pullTask.ConfigureAwait(false);
        var pull = pullDocument?.RootElement;
        using var commentsDocument = await commentsTask.ConfigureAwait(false);

        var author = GetString(issue, "user", "login") ?? "ghost";
        var state = pull is { } p ? ReadPullState(p) : ReadIssueState(issue);
        var issueComments = GetInt(issue, "comments");
        var comments = issueComments + (pull is { } pc ? GetInt(pc, "review_comments") : 0);

        // 时间线：正文 + 前几条评论，头像和正文图片并行下载。
        var entryTasks = new List<Task<CommentViewModel>>
        {
            BuildEntryAsync(issue, author, BodyMaxChars, BodyMaxImages,
                emptyText: "_No description provided._", ct)
        };
        if (commentsDocument?.RootElement is { ValueKind: JsonValueKind.Array } commentArray)
        {
            entryTasks.AddRange(commentArray.EnumerateArray()
                .Take(commentCount)
                .Select(comment => BuildEntryAsync(comment, author, CommentMaxChars, CommentMaxImages, null, ct)));
        }

        var entries = await Task.WhenAll(entryTasks).ConfigureAwait(false);

        return new IssueCardViewModel
        {
            Owner = link.Owner,
            Repository = link.Repository,
            Number = GetInt(issue, "number") is > 0 and var number ? number : link.Number,
            Title = GetString(issue, "title") ?? "(untitled)",
            Author = author,
            OpenedAt = FormatDate(GetString(issue, "created_at")),
            ClosedText = state switch
            {
                IssueCardState.PullMerged => FormatEvent("merged", GetString(pull!.Value, "merged_at")),
                IssueCardState.IssueCompleted or IssueCardState.IssueNotPlanned or IssueCardState.PullClosed
                    => FormatEvent("closed", GetString(issue, "closed_at")),
                _ => null
            },
            State = state,
            Labels = ReadLabels(issue),
            Entries = entries,
            TotalComments = issueComments,
            Comments = FormatCount(comments),
            Reactions = FormatCount(issue.TryGetProperty("reactions", out var reactions) ? GetInt(reactions, "total_count") : 0),
            Commits = pull is { } c ? FormatCount(GetInt(c, "commits")) : "0",
            ChangedFiles = pull is { } f ? FormatCount(GetInt(f, "changed_files")) : "0",
            Additions = pull is { } a ? "+" + FormatCount(GetInt(a, "additions")) : "+0",
            Deletions = pull is { } d ? "−" + FormatCount(GetInt(d, "deletions")) : "−0",
            BaseBranch = pull is { } b ? GetString(b, "base", "ref") ?? "-" : "-",
            HeadBranch = pull is { } h ? ReadHeadBranch(h) : "-",
        };
    }

    private const int BodyMaxChars = 1600;
    private const int BodyMaxImages = 3;
    private const int CommentMaxChars = 700;
    private const int CommentMaxImages = 2;

    /// <summary>把 Issue 正文或一条评论转换成时间线条目。</summary>
    private async Task<CommentViewModel> BuildEntryAsync(
        JsonElement element,
        string issueAuthor,
        int maxChars,
        int maxImages,
        string? emptyText,
        CancellationToken ct)
    {
        var login = GetString(element, "user", "login") ?? "ghost";
        var isBot = string.Equals(GetString(element, "user", "type"), "Bot", StringComparison.OrdinalIgnoreCase);
        var body = GetString(element, "body");
        if (string.IsNullOrWhiteSpace(body))
        {
            body = emptyText ?? "_No content._";
        }

        var (markdown, truncated) = MarkdownSupport.Truncate(body, maxChars);
        var imageUrls = MarkdownSupport.CollectImageUrls(markdown, null, null, maxImages);

        var avatarTask = GetAvatarBytesAsync(WithAvatarSize(GetString(element, "user", "avatar_url")), ct);
        var imagesTask = DownloadImagesAsync(imageUrls, ct);

        return new CommentViewModel
        {
            Author = login,
            Date = FormatDate(GetString(element, "created_at")),
            IsIssueAuthor = string.Equals(login, issueAuthor, StringComparison.OrdinalIgnoreCase),
            Association = isBot ? "bot" : FormatAssociation(GetString(element, "author_association"),
                string.Equals(login, issueAuthor, StringComparison.OrdinalIgnoreCase)),
            AvatarBytes = await avatarTask.ConfigureAwait(false),
            Content = new MarkdownContent
            {
                Markdown = markdown,
                Truncated = truncated,
                Compact = true,
                Images = await imagesTask.ConfigureAwait(false)
            }
        };
    }

    private static string? FormatAssociation(string? association, bool isIssueAuthor)
    {
        return association?.ToUpperInvariant() switch
        {
            "OWNER" => "Owner",
            "MEMBER" => "Member",
            "COLLABORATOR" => "Collaborator",
            _ when isIssueAuthor => "Author",
            "CONTRIBUTOR" => "Contributor",
            "FIRST_TIME_CONTRIBUTOR" or "FIRST_TIMER" => "First-time contributor",
            _ => null
        };
    }

    #endregion

    #region README

    /// <summary>README 原文，及解析相对路径图片所需的基准地址。</summary>
    public sealed record ReadmeSource(string Markdown, string FileName, string? Branch, string? BaseUrl, string? RootUrl);

    private const int ReadmeMaxChars = 40_000;
    private const int ReadmeMaxImages = 40;

    /// <summary>获取仓库默认分支的 README；仓库没有 README 时返回 null。</summary>
    public async Task<ReadmeSource?> GetReadmeOrDefaultAsync(string owner, string repository, CancellationToken ct = default)
    {
        try
        {
            using var document = await GetJsonAsync($"repos/{owner}/{repository}/readme", ct).ConfigureAwait(false);
            var root = document.RootElement;
            var encoded = GetString(root, "content");
            if (string.IsNullOrWhiteSpace(encoded)
                || !string.Equals(GetString(root, "encoding"), "base64", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var markdown = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\n", "").Replace("\r", "")));
            var path = GetString(root, "path") ?? "README.md";
            var downloadUrl = GetString(root, "download_url");

            // download_url = https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
            string? rootUrl = null, baseUrl = null, branch = null;
            if (downloadUrl is not null && downloadUrl.EndsWith(path, StringComparison.Ordinal))
            {
                rootUrl = downloadUrl[..^path.Length];
                baseUrl = downloadUrl[..(downloadUrl.LastIndexOf('/') + 1)];
                var prefix = $"https://raw.githubusercontent.com/{owner}/{repository}/";
                if (rootUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    branch = rootUrl[prefix.Length..].TrimEnd('/');
                }
            }

            return new ReadmeSource(markdown, Path.GetFileName(path), branch, baseUrl, rootUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<ReadmeCardViewModel> GetReadmeCardAsync(
        string owner,
        string repository,
        ReadmeSource readme,
        byte[]? ownerAvatar,
        CancellationToken ct = default)
    {
        var (markdown, truncated) = MarkdownSupport.Truncate(readme.Markdown, ReadmeMaxChars);
        var imageUrls = MarkdownSupport.CollectImageUrls(markdown, readme.BaseUrl, readme.RootUrl, ReadmeMaxImages);
        var images = await DownloadImagesAsync(imageUrls, ct).ConfigureAwait(false);

        return new ReadmeCardViewModel
        {
            Owner = owner,
            Repository = repository,
            FileName = readme.FileName,
            Branch = readme.Branch ?? "HEAD",
            AvatarBytes = ownerAvatar,
            Content = new MarkdownContent
            {
                Markdown = markdown,
                Truncated = truncated,
                BaseUrl = readme.BaseUrl,
                RootUrl = readme.RootUrl,
                Images = images
            }
        };
    }

    #endregion

    #region Images

    private const long MaxImageBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim ImageGate = new(8);

    /// <summary>并行下载 Markdown 中的图片，跳过 SVG、过大或下载失败的图片。</summary>
    private static async Task<IReadOnlyDictionary<string, byte[]>> DownloadImagesAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct)
    {
        if (urls.Count == 0)
        {
            return new Dictionary<string, byte[]>();
        }

        var results = await Task.WhenAll(urls.Select(async url =>
        {
            await ImageGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var response = await ImageHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!response.IsSuccessStatusCode
                    || mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase)
                    || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentLength > MaxImageBytes)
                {
                    return (url, bytes: (byte[]?)null);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                return (url, bytes: bytes.Length is > 0 and <= (int)MaxImageBytes && !LooksLikeSvg(bytes) ? bytes : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (url, bytes: null);
            }
            finally
            {
                ImageGate.Release();
            }
        })).ConfigureAwait(false);

        return results.Where(r => r.bytes is not null).ToDictionary(r => r.url, r => r.bytes!);
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart('\uFEFF', ' ', '\n', '\r', '\t');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Issue helpers

    private static IssueCardState ReadIssueState(JsonElement issue)
    {
        if (!string.Equals(GetString(issue, "state"), "closed", StringComparison.OrdinalIgnoreCase))
        {
            return IssueCardState.IssueOpen;
        }

        return GetString(issue, "state_reason") is "not_planned" or "duplicate"
            ? IssueCardState.IssueNotPlanned
            : IssueCardState.IssueCompleted;
    }

    private static IssueCardState ReadPullState(JsonElement pull)
    {
        if (GetBool(pull, "merged") || GetString(pull, "merged_at") is not null)
        {
            return IssueCardState.PullMerged;
        }

        if (string.Equals(GetString(pull, "state"), "closed", StringComparison.OrdinalIgnoreCase))
        {
            return IssueCardState.PullClosed;
        }

        return GetBool(pull, "draft") ? IssueCardState.PullDraft : IssueCardState.PullOpen;
    }

    private static string ReadHeadBranch(JsonElement pull)
    {
        var headRef = GetString(pull, "head", "ref") ?? "-";
        if (!pull.TryGetProperty("head", out var head) || !pull.TryGetProperty("base", out var @base))
        {
            return headRef;
        }

        // 来自 fork 的 PR 显示 user:branch，与 GitHub 网页一致。
        var headRepo = GetString(head, "repo", "full_name");
        var baseRepo = GetString(@base, "repo", "full_name");
        return string.Equals(headRepo, baseRepo, StringComparison.OrdinalIgnoreCase)
            ? headRef
            : GetString(head, "label") ?? headRef;
    }

    private static IReadOnlyList<IssueLabelViewModel> ReadLabels(JsonElement issue)
    {
        if (!issue.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<IssueLabelViewModel>();
        foreach (var label in labels.EnumerateArray())
        {
            var name = GetString(label, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var color = Color.TryParse("#" + GetString(label, "color"), out var parsed)
                ? parsed
                : Color.Parse("#EDEDED");
            result.Add(new IssueLabelViewModel(name, color));
            if (result.Count == 6)
            {
                break;
            }
        }

        return result;
    }

    #endregion

    #region HTTP

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token.Trim());
        }

        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> GetJsonOrDefaultAsync(string path, CancellationToken ct)
    {
        try
        {
            return await GetJsonAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private GitHubApiException CreateApiException(HttpResponseMessage response)
    {
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
                          || (response.StatusCode == HttpStatusCode.Forbidden
                              && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
                              && remaining.FirstOrDefault() == "0");

        return response.StatusCode switch
        {
            _ when rateLimited => new GitHubApiException(string.IsNullOrWhiteSpace(Token)
                ? "GitHub API 请求次数已达上限，请稍后再试，或在插件配置中填写 GitHub Token。"
                : "GitHub API 请求次数已达上限，请稍后再试。"),
            HttpStatusCode.NotFound => new GitHubApiException("找不到对应内容，可能不存在或为私有仓库。"),
            HttpStatusCode.Unauthorized => new GitHubApiException("GitHub Token 无效或已过期。"),
            HttpStatusCode.Gone => new GitHubApiException("该内容已被删除或转移。"),
            _ => new GitHubApiException($"GitHub API 返回 {(int)response.StatusCode} {response.ReasonPhrase}。")
        };
    }

    private static async Task<byte[]?> GetAvatarBytesAsync(string? avatarUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)
            || !Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            return await ImageHttp.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string? WithAvatarSize(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            return avatarUrl;
        }

        return avatarUrl + (avatarUrl.Contains('?') ? "&s=192" : "?s=192");
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(20)
        };

        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.2"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static HttpClient CreateImageHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.2"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        return http;
    }

    #endregion

    #region JSON / 格式化

    private static int? TryReadLastPage(IEnumerable<string> links)
    {
        foreach (var part in string.Join(',', links).Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 只取 URL 查询串里的 page=，避免误读 per_page=。
            var pageIndex = part.IndexOf("&page=", StringComparison.OrdinalIgnoreCase);
            if (pageIndex < 0)
            {
                pageIndex = part.IndexOf("?page=", StringComparison.OrdinalIgnoreCase);
            }

            if (pageIndex < 0)
            {
                continue;
            }

            pageIndex += "&page=".Length;
            var endIndex = pageIndex;
            while (endIndex < part.Length && char.IsDigit(part[endIndex]))
            {
                endIndex++;
            }

            if (int.TryParse(part[pageIndex..endIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            {
                return page;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetString(JsonElement root, string objectName, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(objectName, out var obj)
               && obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static bool GetBool(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.True;
    }

    private static string FormatDate(string? iso)
    {
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "-";
    }

    private static string? FormatEvent(string verb, string? iso)
    {
        return iso is null ? verb : $"{verb} on {FormatDate(iso)}";
    }

    private static string FormatCount(int count)
    {
        return count switch
        {
            >= 1_000_000 => (count / 1_000_000d).ToString("0.#M", CultureInfo.InvariantCulture),
            >= 1_000 => (count / 1_000d).ToString("0.#k", CultureInfo.InvariantCulture),
            _ => count.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static Color GetLanguageColor(string language)
    {
        return language switch
        {
            "C#" => Color.Parse("#178600"),
            "F#" => Color.Parse("#b845fc"),
            "Visual Basic .NET" => Color.Parse("#945db7"),
            "JavaScript" => Color.Parse("#f1e05a"),
            "TypeScript" => Color.Parse("#3178c6"),
            "HTML" => Color.Parse("#e34c26"),
            "CSS" => Color.Parse("#663399"),
            "Python" => Color.Parse("#3572A5"),
            "Java" => Color.Parse("#b07219"),
            "Kotlin" => Color.Parse("#A97BFF"),
            "C" => Color.Parse("#555555"),
            "C++" => Color.Parse("#f34b7d"),
            "Rust" => Color.Parse("#dea584"),
            "Go" => Color.Parse("#00ADD8"),
            "Swift" => Color.Parse("#F05138"),
            "Shell" => Color.Parse("#89e051"),
            "Dockerfile" => Color.Parse("#384d54"),
            "PowerShell" => Color.Parse("#012456"),
            "Vue" => Color.Parse("#41b883"),
            "Svelte" => Color.Parse("#ff3e00"),
            "Dart" => Color.Parse("#00B4AB"),
            "Ruby" => Color.Parse("#701516"),
            "PHP" => Color.Parse("#4F5D95"),
            "Lua" => Color.Parse("#000080"),
            "Jupyter Notebook" => Color.Parse("#DA5B0B"),
            _ => Color.Parse("#8C959F")
        };
    }

    private sealed record LanguageBar(LanguageBarSegment[] Segments, string PrimaryLanguage)
    {
        public LanguageBarSegment this[int index] => Segments[index];
    }

    private sealed record LanguageBarSegment(GridLength Width, Color Color);

    #endregion
}
