using System.Globalization;
using System.Text.RegularExpressions;

namespace ShiroBot.Plugin.GithubView.Service;

internal static partial class Formatting
{
    public static string Count(int count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#M", CultureInfo.InvariantCulture),
        >= 1_000 => (count / 1_000d).ToString("0.#k", CultureInfo.InvariantCulture),
        _ => count.ToString(CultureInfo.InvariantCulture)
    };

    public static string Relative(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time.ToUniversalTime();
        return span switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)} ago",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)} ago",
            { TotalDays: < 31 } => $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)} ago",
            { TotalDays: < 365 } => "on " + time.ToString("MMM d", CultureInfo.InvariantCulture),
            _ => "on " + time.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
        };
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span switch
        {
            { TotalSeconds: < 60 } => $"{(int)span.TotalSeconds}s",
            { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes}m {span.Seconds}s",
            _ => $"{(int)span.TotalHours}h {span.Minutes}m"
        };
    }

    /// <summary>
    /// 清洗 Markdown 用于 headless 渲染:
    /// 1. 图片替换为「[图片: alt]」占位(避免网络加载阻塞截图);
    /// 2. HTML img 同样替换,其余 HTML 标签移除;
    /// 3. GitHub issue/PR/commit 长链接缩写为 #123 / commit sha 形式;
    /// 4. 截断超长内容。
    /// </summary>
    public static string SanitizeMarkdown(string? markdown, int maxLength, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n");
        text = MarkdownImageRegex().Replace(text, static match =>
        {
            var alt = match.Groups[1].Value.Trim();
            return alt.Length > 0 ? $"`[图片: {alt}]`" : "`[图片]`";
        });
        text = HtmlImageRegex().Replace(text, "`[图片]`");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = ShortenGitHubUrls(text);
        text = text.Trim();

        if (text.Length > maxLength)
        {
            truncated = true;
            text = text[..maxLength];
            var lastBreak = text.LastIndexOf('\n');
            if (lastBreak > maxLength / 2)
            {
                text = text[..lastBreak];
            }
        }

        return text;
    }

    /// <summary>
    /// 缩写裸 GitHub 链接:
    /// https://github.com/o/r/pull/123 → o/r#123(同仓库时 → #123 由调用方无从判断,统一保留仓库名);
    /// https://github.com/o/r/issues/123 → o/r#123;
    /// https://github.com/o/r/commit/abcdef1234 → o/r@abcdef1;
    /// https://github.com/o/r/compare/v1...v2 → v1...v2。
    /// 已在 Markdown 链接语法 [text](url) 内的不动,只处理裸链接。
    /// </summary>
    public static string ShortenGitHubUrls(string text)
    {
        var input = text;
        text = GitHubIssueUrlRegex().Replace(text, match =>
            IsInsideMarkdownLink(input, match) ? match.Value : $"{match.Groups[1].Value}/{match.Groups[2].Value}#{match.Groups[3].Value}");

        input = text;
        text = GitHubCommitUrlRegex().Replace(text, match =>
        {
            if (IsInsideMarkdownLink(input, match))
            {
                return match.Value;
            }

            var sha = match.Groups[3].Value;
            return $"{match.Groups[1].Value}/{match.Groups[2].Value}@{(sha.Length > 7 ? sha[..7] : sha)}";
        });

        input = text;
        text = GitHubCompareUrlRegex().Replace(text, match =>
            IsInsideMarkdownLink(input, match) ? match.Value : match.Groups[3].Value);

        return text;
    }

    private static bool IsInsideMarkdownLink(string input, Match match)
        => match.Index > 0 && input[match.Index - 1] == '(';

    private static string Plural(int value) => value == 1 ? string.Empty : "s";

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImageRegex();

    [GeneratedRegex(@"<[^>\n]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"https?://github\.com/([\w.-]+)/([\w.-]+)/(?:pull|issues)/(\d+)(?:#[\w-]*)?")]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex(@"https?://github\.com/([\w.-]+)/([\w.-]+)/commit/([0-9a-f]{7,40})")]
    private static partial Regex GitHubCommitUrlRegex();

    [GeneratedRegex(@"https?://github\.com/([\w.-]+)/([\w.-]+)/compare/([\w.@/-]+\.\.\.[\w.@/-]+)")]
    private static partial Regex GitHubCompareUrlRegex();
}
