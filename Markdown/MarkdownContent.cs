using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ShiroBot.Plugin.GithubView.Markdown;

/// <summary>
/// 交给 <see cref="MarkdownView"/> 渲染的 Markdown 文档：源码 + 已在工作线程下载好的图片。
/// 控件只在 UI 线程上解码图片，不做任何网络请求。
/// </summary>
public sealed class MarkdownContent
{
    public string Markdown { get; init; } = string.Empty;

    /// <summary>相对路径图片的基准 URL（README 所在目录）。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>以 / 开头的路径的基准 URL（仓库根目录）。</summary>
    public string? RootUrl { get; init; }

    /// <summary>已下载的图片，键为 <see cref="MarkdownSupport.ResolveUrl"/> 解析后的地址。</summary>
    public IReadOnlyDictionary<string, byte[]> Images { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>紧凑模式：Issue / 评论使用，字号更小，软换行按 GitHub 评论规则视为换行。</summary>
    public bool Compact { get; init; }

    /// <summary>源码是否被截断过。</summary>
    public bool Truncated { get; init; }
}

public static partial class MarkdownSupport
{
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseAlertBlocks()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseEmojiAndSmiley(false)
        .Build();

    public static MarkdownDocument Parse(string markdown)
    {
        return Markdig.Markdown.Parse(markdown, Pipeline);
    }

    /// <summary>按顶层块截断 Markdown，尽量不切断段落、列表或代码块。</summary>
    public static (string Markdown, bool Truncated) Truncate(string markdown, int maxChars)
    {
        markdown = markdown.Replace("\r\n", "\n");
        if (markdown.Length <= maxChars)
        {
            return (markdown, false);
        }

        var document = Parse(markdown);
        var end = 0;
        foreach (var block in document)
        {
            if (block.Span.End + 1 > maxChars)
            {
                break;
            }

            end = block.Span.End + 1;
        }

        // 第一个块就超长时按字符硬截断。
        if (end == 0)
        {
            end = maxChars;
        }

        return (markdown[..Math.Min(end, markdown.Length)].TrimEnd(), true);
    }

    /// <summary>按出现顺序收集文档中的图片地址（Markdown 图片与 HTML img）。</summary>
    public static IReadOnlyList<string> CollectImageUrls(string markdown, string? baseUrl, string? rootUrl, int max)
    {
        var result = new List<string>();
        if (max <= 0 || string.IsNullOrWhiteSpace(markdown))
        {
            return result;
        }

        void Add(string? raw)
        {
            if (result.Count >= max)
            {
                return;
            }

            var url = ResolveUrl(raw, baseUrl, rootUrl);
            if (url is not null && !result.Contains(url))
            {
                result.Add(url);
            }
        }

        foreach (var node in Parse(markdown).Descendants())
        {
            switch (node)
            {
                case LinkInline { IsImage: true } image:
                    Add(image.Url);
                    break;
                case HtmlInline html:
                    foreach (var src in FindImageSources(html.Tag))
                    {
                        Add(src);
                    }

                    break;
                case HtmlBlock html:
                    foreach (var src in FindImageSources(html.Lines.ToString()))
                    {
                        Add(src);
                    }

                    break;
            }

            if (result.Count >= max)
            {
                break;
            }
        }

        return result;
    }

    public static IEnumerable<string> FindImageSources(string html)
    {
        foreach (Match img in ImgTagRegex().Matches(html))
        {
            var src = GetAttribute(img.Value, "src");
            if (!string.IsNullOrWhiteSpace(src))
            {
                yield return src;
            }
        }
    }

    public static string? GetAttribute(string tag, string name)
    {
        foreach (Match attribute in AttributeRegex().Matches(tag))
        {
            if (string.Equals(attribute.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return System.Net.WebUtility.HtmlDecode(attribute.Groups["value"].Value.Trim('"', '\''));
            }
        }

        return null;
    }

    /// <summary>把 Markdown 中的图片地址解析成可下载的绝对地址。</summary>
    public static string? ResolveUrl(string? raw, string? baseUrl, string? rootUrl)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim().Trim('<', '>');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith('#'))
        {
            return null;
        }

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        Uri? uri;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            uri = absolute;
        }
        else if (raw.StartsWith('/') && rootUrl is not null)
        {
            uri = new Uri(new Uri(rootUrl), raw.TrimStart('/'));
        }
        else if (baseUrl is not null && Uri.TryCreate(new Uri(baseUrl), raw, out var relative))
        {
            uri = relative;
        }
        else
        {
            return null;
        }

        var builder = new UriBuilder(uri);

        // shields.io 默认是 SVG，换成同路径的 PNG 服务。
        if (string.Equals(builder.Host, "img.shields.io", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = "raster.shields.io";
        }

        // github.com/owner/repo/blob/... 是网页，换成 raw 下载地址。
        if (string.Equals(builder.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = builder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 3 && parts[2] == "blob")
            {
                parts[2] = "raw";
                builder.Path = "/" + string.Join('/', parts);
            }
        }

        return builder.Uri.AbsoluteUri;
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTagRegex();

    [GeneratedRegex(@"(?<name>[a-zA-Z-]+)\s*=\s*(?<value>""[^""]*""|'[^']*'|[^\s>]+)")]
    private static partial Regex AttributeRegex();
}
