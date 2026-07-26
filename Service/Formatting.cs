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
    /// 清洗 Markdown 用于 headless 渲染:移除图片(避免网络加载阻塞截图)与 HTML 标签,并截断超长内容。
    /// </summary>
    public static string SanitizeMarkdown(string? markdown, int maxLength, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n");
        text = MarkdownImageRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, string.Empty);
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

    private static string Plural(int value) => value == 1 ? string.Empty : "s";

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"<[^>\n]+>")]
    private static partial Regex HtmlTagRegex();
}
