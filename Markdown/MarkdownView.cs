using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace ShiroBot.Plugin.GithubView.Markdown;

/// <summary>把 <see cref="MarkdownContent"/> 渲染成 GitHub 风格的 Avalonia 控件树。</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<MarkdownContent?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownContent?>(nameof(Markdown));

    private MarkdownContent? _builtContent;
    private ThemeVariant? _builtTheme;

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView()
    {
        // 配色取自卡片的主题资源：挂到树上或主题切换后，用正确的配色重建一次。
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Rebuild();
    }

    public MarkdownContent? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        var content = Markdown;
        if (ReferenceEquals(content, _builtContent) && _builtTheme == ActualThemeVariant)
        {
            return;
        }

        _builtContent = content;
        _builtTheme = ActualThemeVariant;
        Children.Clear();
        if (content is null || string.IsNullOrWhiteSpace(content.Markdown))
        {
            return;
        }

        var renderer = new MarkdownRenderer(content, this);
        Spacing = renderer.BlockSpacing;
        SetValue(TextElement.ForegroundProperty, renderer.TextBrush);
        SetValue(TextElement.FontSizeProperty, renderer.BaseSize);

        foreach (var control in renderer.Render())
        {
            Children.Add(control);
        }
    }
}

internal sealed partial class MarkdownRenderer(MarkdownContent content, Control host)
{
    // 配色从宿主控件所在主题的 GitHubTheme 资源中解析；找不到时回退到浅色。
    public IBrush TextBrush { get; } = Resolve(host, "Gh.Fg", "#1F2328");
    private IBrush MutedBrush { get; } = Resolve(host, "Gh.FgMuted", "#59636E");
    private IBrush LinkBrush { get; } = Resolve(host, "Gh.Link", "#0969DA");
    private IBrush BorderBrush { get; } = Resolve(host, "Gh.Border", "#D1D9E0");
    private IBrush CanvasBrush { get; } = Resolve(host, "Gh.Canvas", "#FFFFFF");
    private IBrush CodeBackground { get; } = Resolve(host, "Gh.CodeBackground", "#F6F8FA");
    private IBrush InlineCodeBackground { get; } = Resolve(host, "Gh.InlineCodeBackground", "#EFF1F3");
    private IBrush CheckboxBorder { get; } = Resolve(host, "Gh.CheckboxBorder", "#818B98");

    private static IBrush Resolve(Control host, string key, string fallback)
    {
        return host.TryFindResource(key, host.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brush(fallback);
    }

    private Color ResolveColor(string key, string fallback)
    {
        return host.TryFindResource(key, host.ActualThemeVariant, out var value) && value is Color color
            ? color
            : Color.Parse(fallback);
    }
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, monospace");

    private readonly Dictionary<string, Bitmap?> _bitmaps = new();
    private int _listDepth;

    /// <summary>
    /// 跨 HTML 块的 div 对齐栈：Markdig 会在空行处把 &lt;div align="center"&gt; 拆成多个 HtmlBlock，
    /// 中间还可能夹着 Markdown 段落，所以对齐状态要在渲染器层面保存。
    /// </summary>
    private readonly List<bool> _divAlignStack = [];

    private bool InCenteredDiv => _divAlignStack.Contains(true);

    private bool Compact => content.Compact;
    public double BaseSize => Compact ? 14.5 : 16;
    public double BlockSpacing => Compact ? 10 : 16;
    private double MaxImageHeight => Compact ? 300 : 1600;

    /// <summary>小于这个高度的图片当作行内图标 / 徽章。</summary>
    private const double InlineImageMaxHeight = 48;

    public IEnumerable<Control> Render()
    {
        var document = MarkdownSupport.Parse(content.Markdown);
        var controls = RenderBlocks(document).ToList();

        // 只有 HTML 注释（Issue 模板）或全是无法显示的图片时，给出占位文字。
        if (controls.Count == 0)
        {
            controls.Add(new TextBlock
            {
                Text = "No description provided.",
                Foreground = MutedBrush,
                FontStyle = FontStyle.Italic
            });
        }
        if (content.Truncated)
        {
            controls.Add(new TextBlock
            {
                Text = Compact ? "… 内容过长，已截断" : "… README 过长，后续内容已省略",
                Foreground = MutedBrush,
                FontStyle = FontStyle.Italic,
                FontSize = BaseSize - 1
            });
        }

        return controls;
    }

    #region Blocks

    private IEnumerable<Control> RenderBlocks(ContainerBlock container)
    {
        foreach (var block in container)
        {
            foreach (var control in RenderBlock(block))
            {
                if (block is not HtmlBlock && InCenteredDiv)
                {
                    Center(control);
                }

                yield return control;
            }
        }
    }

    private static void Center(Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        if (control is TextBlock text)
        {
            text.TextAlignment = TextAlignment.Center;
        }
        else if (control is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                Center(child);
            }
        }
    }

    private IEnumerable<Control> RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                yield return RenderHeading(heading);
                break;

            case ParagraphBlock paragraph:
                foreach (var control in RenderParagraph(paragraph.Inline))
                {
                    yield return control;
                }

                break;

            case ListBlock list:
                yield return RenderList(list);
                break;

            case AlertBlock alert:
                yield return RenderAlert(alert);
                break;

            case QuoteBlock quote:
                yield return new Border
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(14, 0, 0, 0),
                    Child = Stack(RenderBlocks(quote), BlockSpacing)
                }.With(TextElement.ForegroundProperty, MutedBrush);
                break;

            case CodeBlock code:
                yield return RenderCode(code);
                break;

            case ThematicBreakBlock:
                yield return new Border
                {
                    Height = Compact ? 2 : 4,
                    Background = BorderBrush,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 4)
                };
                break;

            case Table table:
                yield return RenderTable(table);
                break;

            case HtmlBlock html:
                var htmlControl = RenderHtml(html.Lines.ToString());
                if (htmlControl is not null)
                {
                    yield return htmlControl;
                }

                break;

            case LinkReferenceDefinitionGroup:
                break;

            case LeafBlock { Inline: not null } leaf:
                foreach (var control in RenderParagraph(leaf.Inline))
                {
                    yield return control;
                }

                break;

            case ContainerBlock nested:
                foreach (var control in RenderBlocks(nested))
                {
                    yield return control;
                }

                break;
        }
    }

    private Control RenderHeading(HeadingBlock heading)
    {
        var scale = heading.Level switch
        {
            1 => 2.0,
            2 => 1.5,
            3 => 1.25,
            4 => 1.0,
            5 => 0.875,
            _ => 0.85
        };

        if (Compact)
        {
            scale = 1 + (scale - 1) * 0.5;
        }

        var builder = new ParagraphBuilder(this, BaseSize * scale, FontWeight.SemiBold);
        if (heading.Inline is not null)
        {
            builder.AddInlines(heading.Inline, default);
        }

        Control result = Stack(builder.Build(), 6);
        if (heading.Level == 6)
        {
            result.SetValue(TextElement.ForegroundProperty, MutedBrush);
        }

        if (heading.Level <= 2 && !Compact)
        {
            result = new Border
            {
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 8),
                Child = result
            };
        }

        result.Margin = new Thickness(0, Compact ? 2 : 8, 0, 0);
        return result;
    }

    private IEnumerable<Control> RenderParagraph(ContainerInline? inline)
    {
        if (inline is null)
        {
            return [];
        }

        // 只有图片（徽章行 / 截图）的段落横向排布。
        if (IsImageOnly(inline))
        {
            var images = inline.Descendants<LinkInline>()
                .Where(link => link.IsImage)
                .Select(link => CreateImage(link.Url, null, null))
                .OfType<Control>()
                .ToList();

            foreach (var html in inline.Descendants<HtmlInline>())
            {
                foreach (var src in MarkdownSupport.FindImageSources(html.Tag))
                {
                    if (CreateImage(src, MarkdownSupport.GetAttribute(html.Tag, "width"),
                            MarkdownSupport.GetAttribute(html.Tag, "height")) is { } image)
                    {
                        images.Add(image);
                    }
                }
            }

            return images.Count == 0 ? [] : [ImageRow(images, HorizontalAlignment.Left)];
        }

        var builder = new ParagraphBuilder(this, BaseSize, FontWeight.Normal);
        builder.AddInlines(inline, default);
        return builder.Build();
    }

    private static bool IsImageOnly(ContainerInline inline)
    {
        var hasImage = false;
        foreach (var child in inline.Descendants<MdInline>())
        {
            switch (child)
            {
                case LinkInline { IsImage: true }:
                    hasImage = true;
                    break;
                case LinkInline:
                case LineBreakInline:
                case EmphasisInline:
                    break;
                case HtmlInline html when html.Tag.StartsWith("<img", StringComparison.OrdinalIgnoreCase):
                    hasImage = true;
                    break;
                case HtmlInline html when html.Tag.StartsWith("</", StringComparison.Ordinal)
                                          || html.Tag.StartsWith("<a", StringComparison.OrdinalIgnoreCase)
                                          || html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)
                                          || html.Tag.StartsWith("<p", StringComparison.OrdinalIgnoreCase):
                    break;
                case LiteralInline literal when literal.Parent is LinkInline { IsImage: true }:
                    break;
                case LiteralInline literal when literal.Content.IsEmptyOrWhitespace():
                    break;
                default:
                    return false;
            }
        }

        return hasImage;
    }

    private Control RenderList(ListBlock list)
    {
        _listDepth++;
        var panel = new StackPanel { Spacing = Compact ? 4 : 6 };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList t } ? t : null;
            Control marker = task is not null
                ? CheckBox(task.Checked)
                : new TextBlock
                {
                    Text = list.IsOrdered ? $"{number}." : (_listDepth % 3) switch { 1 => "•", 2 => "◦", _ => "▪" },
                    HorizontalAlignment = HorizontalAlignment.Right,
                    // 与正文段落同一行高，项目符号才会和第一行文字对齐。
                    LineHeight = BaseSize * 1.5,
                    Margin = new Thickness(0, 0, 8, 0)
                };

            marker.VerticalAlignment = VerticalAlignment.Top;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var markerHost = new Border { MinWidth = list.IsOrdered ? 28 : 20, Child = marker };
            var body = Stack(RenderBlocks(item), Compact ? 4 : 8);
            Grid.SetColumn(body, 1);
            grid.Children.Add(markerHost);
            grid.Children.Add(body);
            panel.Children.Add(grid);
            number++;
        }

        _listDepth--;
        return panel;
    }

    private Control CheckBox(bool isChecked)
    {
        var box = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = isChecked ? LinkBrush : CheckboxBorder,
            Background = isChecked ? LinkBrush : CanvasBrush,
            Margin = new Thickness(0, (BaseSize * 1.5 - 14) / 2, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        if (isChecked)
        {
            box.Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"),
                Fill = Brushes.White,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(1.5)
            };
        }

        return box;
    }

    private Control RenderAlert(AlertBlock alert)
    {
        var kind = alert.Kind.ToString().ToUpperInvariant();
        var (title, key, fallback) = kind switch
        {
            "TIP" => ("Tip", "Gh.Tip", "#1A7F37"),
            "IMPORTANT" => ("Important", "Gh.Important", "#8250DF"),
            "WARNING" => ("Warning", "Gh.Warning", "#9A6700"),
            "CAUTION" => ("Caution", "Gh.Caution", "#D1242F"),
            _ => ("Note", "Gh.Note", "#0969DA")
        };
        IBrush color = new ImmutableSolidColorBrush(ResolveColor(key, fallback));

        var children = new List<Control>
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = color }
        };
        children.AddRange(RenderBlocks(alert));

        return new Border
        {
            BorderBrush = color,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Child = Stack(children, 8)
        };
    }

    private Control RenderCode(CodeBlock code)
    {
        var lines = code.Lines.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var maxLines = Compact ? 14 : 80;
        var text = string.Join('\n', lines.Take(maxLines));
        if (lines.Length > maxLines)
        {
            text += $"\n… ({lines.Length - maxLines} more lines)";
        }

        return new Border
        {
            Background = CodeBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(Compact ? 12 : 16, Compact ? 10 : 14),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = MonoFont,
                FontSize = BaseSize * 0.85,
                LineHeight = BaseSize * 0.85 * 1.45,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private Control RenderTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = Math.Max(table.ColumnDefinitions.Count, rows.Count == 0 ? 1 : rows.Max(row => row.Count));
        var grid = new Grid();
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = rows[r];
            for (var c = 0; c < columns; c++)
            {
                var cell = c < row.Count ? row[c] as TableCell : null;
                var alignment = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                var cellContent = Stack(cell is null ? [] : RenderBlocks(cell), 4);
                cellContent.HorizontalAlignment = alignment switch
                {
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left
                };
                if (row.IsHeader)
                {
                    cellContent.SetValue(TextElement.FontWeightProperty, FontWeight.SemiBold);
                }

                var border = new Border
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(10, 6),
                    Background = r % 2 == 0 && !row.IsHeader ? CodeBackground : CanvasBrush,
                    Child = cellContent
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Child = grid
        };
    }

    #endregion

    #region HTML

    /// <summary>
    /// README 中常见的 HTML 块（居中 logo、徽章行、h1/p）。只做结构性降级：
    /// 块级标签换行，img 渲染为图片，h1-h6 / b / a 改变文字样式，其余标签忽略。
    /// </summary>
    private Control? RenderHtml(string html)
    {
        var blocks = new List<Control>();
        var images = new List<Control>();
        var runs = new List<HtmlRun>();
        int heading = 0, bold = 0, italic = 0, link = 0, code = 0;
        var localCenter = new List<bool>();   // 本块内 p / h1-h6 / center 等标签的对齐
        var scales = new List<double>();      // <span style="font-size: 2em">

        bool IsCentered() => InCenteredDiv || localCenter.Contains(true);

        void FlushImages()
        {
            if (images.Count > 0)
            {
                blocks.Add(ImageRow(images.ToList(), IsCentered() ? HorizontalAlignment.Center : HorizontalAlignment.Left));
                images.Clear();
            }
        }

        void FlushText()
        {
            var text = string.Concat(runs.Select(r => r.Text)).Trim();
            if (text.Length > 0)
            {
                var center = IsCentered();
                var block = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                    HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left
                };
                var first = true;
                foreach (var run in runs)
                {
                    var value = first ? run.Text.TrimStart() : run.Text;
                    first = false;
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    var headingScale = run.Heading switch
                    {
                        1 => 2.0,
                        2 => 1.5,
                        3 => 1.25,
                        _ => 1.0
                    };
                    var inline = new Run(value)
                    {
                        FontSize = BaseSize * Math.Max(headingScale, run.Scale),
                        FontWeight = run.Heading > 0 || run.Bold ? FontWeight.SemiBold : FontWeight.Normal
                    };
                    if (run.Italic)
                    {
                        inline.FontStyle = FontStyle.Italic;
                    }

                    if (run.Link)
                    {
                        inline.Foreground = LinkBrush;
                    }

                    if (run.Code)
                    {
                        inline.FontFamily = MonoFont;
                        inline.Background = InlineCodeBackground;
                    }

                    block.Inlines!.Add(inline);
                }

                blocks.Add(block);
            }

            runs.Clear();
        }

        void Flush()
        {
            FlushImages();
            FlushText();
        }

        void AddText(string raw)
        {
            var text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(raw), " ");
            if (!string.IsNullOrWhiteSpace(text))
            {
                FlushImages();
                runs.Add(new HtmlRun(text, heading, bold > 0, italic > 0, link > 0, code > 0,
                    scales.Count > 0 ? scales[^1] : 1));
            }
        }

        static void Pop(List<bool> stack)
        {
            if (stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        var position = 0;
        foreach (Match match in HtmlTokenRegex().Matches(html))
        {
            if (match.Index > position)
            {
                AddText(html[position..match.Index]);
            }

            position = match.Index + match.Length;
            if (match.Groups["comment"].Success)
            {
                continue;
            }

            var closing = match.Groups["close"].Value == "/";
            var selfClosing = match.Value.EndsWith("/>", StringComparison.Ordinal);
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var centered = !closing && CenterAlignRegex().IsMatch(match.Value);
            switch (tag)
            {
                case "img":
                    var image = CreateImage(MarkdownSupport.GetAttribute(match.Value, "src"),
                        MarkdownSupport.GetAttribute(match.Value, "width"),
                        MarkdownSupport.GetAttribute(match.Value, "height"));
                    if (image is not null)
                    {
                        FlushText();
                        images.Add(image);
                    }

                    break;
                case "br":
                    FlushText();
                    break;
                case "div":
                    Flush();
                    if (closing)
                    {
                        Pop(_divAlignStack);
                    }
                    else if (!selfClosing)
                    {
                        _divAlignStack.Add(centered);
                    }

                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    heading = closing ? 0 : tag[1] - '0';
                    if (closing)
                    {
                        Pop(localCenter);
                    }
                    else
                    {
                        localCenter.Add(centered);
                    }

                    break;
                case "p" or "center":
                    Flush();
                    if (closing)
                    {
                        Pop(localCenter);
                    }
                    else
                    {
                        localCenter.Add(centered || tag == "center");
                    }

                    break;
                case "li" or "ul" or "ol" or "table" or "tr" or "details" or "summary"
                    or "blockquote" or "pre" or "hr" or "section":
                    Flush();
                    break;
                case "b" or "strong":
                    bold = Math.Max(0, bold + (closing ? -1 : 1));
                    break;
                case "i" or "em":
                    italic = Math.Max(0, italic + (closing ? -1 : 1));
                    break;
                case "a":
                    link = Math.Max(0, link + (closing ? -1 : 1));
                    break;
                case "code" or "kbd":
                    code = Math.Max(0, code + (closing ? -1 : 1));
                    break;
                case "span" or "font":
                    if (closing)
                    {
                        if (scales.Count > 0)
                        {
                            scales.RemoveAt(scales.Count - 1);
                        }
                    }
                    else
                    {
                        scales.Add(ReadFontScale(match.Value) ?? (scales.Count > 0 ? scales[^1] : 1));
                    }

                    break;
                case "td" or "th":
                    runs.Add(new HtmlRun("  ", heading, false, false, false, false, 1));
                    break;
            }
        }

        if (position < html.Length)
        {
            AddText(html[position..]);
        }

        Flush();
        return blocks.Count switch
        {
            0 => null,
            1 => blocks[0],
            _ => Stack(blocks, Compact ? 6 : 10)
        };
    }

    /// <summary>读取 style="font-size: 2.2em" 或 font-size: 150%。</summary>
    private static double? ReadFontScale(string tag)
    {
        var match = FontSizeRegex().Match(tag);
        if (!match.Success
            || !double.TryParse(match.Groups["value"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var scale = match.Groups["unit"].Value switch
        {
            "%" => value / 100,
            "px" => value / 16,
            _ => value
        };
        return Math.Clamp(scale, 0.5, 3);
    }

    private readonly record struct HtmlRun(
        string Text, int Heading, bool Bold, bool Italic, bool Link, bool Code, double Scale);

    [GeneratedRegex(@"font-size\s*:\s*(?<value>[0-9.]+)\s*(?<unit>em|rem|%|px)", RegexOptions.IgnoreCase)]
    private static partial Regex FontSizeRegex();

    [GeneratedRegex(@"align\s*=\s*[""']?center", RegexOptions.IgnoreCase)]
    private static partial Regex CenterAlignRegex();

    [GeneratedRegex(@"(?<comment><!--.*?-->)|<(?<close>/?)(?<tag>[a-zA-Z][a-zA-Z0-9]*)\b[^>]*?/?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTokenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    #endregion

    #region Images

    internal Bitmap? GetBitmap(string? rawUrl)
    {
        var url = MarkdownSupport.ResolveUrl(rawUrl, content.BaseUrl, content.RootUrl);
        if (url is null)
        {
            return null;
        }

        if (_bitmaps.TryGetValue(url, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        if (content.Images.TryGetValue(url, out var bytes))
        {
            try
            {
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch
            {
                bitmap = null;
            }
        }

        _bitmaps[url] = bitmap;
        return bitmap;
    }

    /// <summary>按 HTML width/height 属性或图片原始尺寸计算显示尺寸。</summary>
    internal Size? GetImageSize(Bitmap bitmap, string? widthAttribute, string? heightAttribute)
    {
        var natural = bitmap.Size;
        if (natural.Width <= 0 || natural.Height <= 0)
        {
            return null;
        }

        var ratio = natural.Width / natural.Height;
        double? width = ParseLength(widthAttribute);
        double? height = ParseLength(heightAttribute);

        var size = (width, height) switch
        {
            ({ } w, { } h) => new Size(w, h),
            ({ } w, null) => new Size(w, w / ratio),
            (null, { } h) => new Size(h * ratio, h),
            _ => natural
        };

        if (size.Height > MaxImageHeight)
        {
            size = new Size(MaxImageHeight * ratio, MaxImageHeight);
        }

        return size;
    }

    private static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('%'))
        {
            return null;
        }

        return double.TryParse(value.Replace("px", string.Empty), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : null;
    }

    private Control? CreateImage(string? url, string? widthAttribute, string? heightAttribute)
    {
        var bitmap = GetBitmap(url);
        if (bitmap is null || GetImageSize(bitmap, widthAttribute, heightAttribute) is not { } size)
        {
            return null;
        }

        // MaxWidth/MaxHeight + Uniform：不放大，超出可用宽度时等比缩小。
        return new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = size.Width,
            MaxHeight = size.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Control ImageRow(List<Control> images, HorizontalAlignment alignment)
    {
        if (images.Count == 1)
        {
            images[0].HorizontalAlignment = alignment;
            return images[0];
        }

        var panel = new WrapPanel
        {
            ItemSpacing = 4,
            LineSpacing = 4,
            HorizontalAlignment = alignment
        };
        foreach (var image in images)
        {
            panel.Children.Add(image);
        }

        return panel;
    }

    #endregion

    #region Helpers

    private static StackPanel Stack(IEnumerable<Control> children, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    #endregion

    /// <summary>
    /// 把一段行内元素写进 TextBlock。遇到大图时结束当前 TextBlock、单独输出图片，
    /// 因此样式以参数形式下传，不依赖 Span 嵌套。
    /// </summary>
    private sealed class ParagraphBuilder(MarkdownRenderer renderer, double fontSize, FontWeight weight)
    {
        private readonly List<Control> _blocks = [];
        private TextBlock? _current;
        private bool _hasInlineUi;

        private InlineCollection Inlines
        {
            get
            {
                if (_current is null)
                {
                    _current = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = fontSize,
                        FontWeight = weight
                    };
                    _hasInlineUi = false;
                    _blocks.Add(_current);
                }

                return _current.Inlines!;
            }
        }

        public List<Control> Build()
        {
            Close();
            return _blocks.Where(block => block is not TextBlock text || text.Inlines!.Count > 0).ToList();
        }

        private void Close()
        {
            if (_current is not null && !_hasInlineUi)
            {
                _current.LineHeight = fontSize * 1.5;
            }

            // 去掉段首段尾的换行。
            if (_current?.Inlines is { Count: > 0 } inlines)
            {
                while (inlines.Count > 0 && inlines[^1] is LineBreak)
                {
                    inlines.RemoveAt(inlines.Count - 1);
                }

                while (inlines.Count > 0 && inlines[0] is LineBreak)
                {
                    inlines.RemoveAt(0);
                }
            }

            _current = null;
        }

        public void AddInlines(ContainerInline container, InlineStyle style)
        {
            foreach (var inline in container)
            {
                AddInline(inline, style);
            }
        }

        private void AddInline(MdInline inline, InlineStyle style)
        {
            switch (inline)
            {
                case TaskList:
                    break;

                case LiteralInline literal:
                    AddText(literal.Content.ToString(), style);
                    break;

                case CodeInline code:
                    AddText(code.Content, style with { Code = true });
                    break;

                case EmphasisInline emphasis:
                    var next = emphasis.DelimiterChar == '~'
                        ? style with { Strike = true }
                        : emphasis.DelimiterCount >= 2
                            ? style with { Bold = true }
                            : style with { Italic = true };
                    AddInlines(emphasis, next);
                    break;

                case LinkInline { IsImage: true } image:
                    AddImage(image.Url, null, null, image.FirstChild is LiteralInline alt ? alt.Content.ToString() : null, style);
                    break;

                case LinkInline link:
                    if (link.FirstChild is null)
                    {
                        AddText(link.Url ?? string.Empty, style with { Link = true });
                    }
                    else
                    {
                        AddInlines(link, style with { Link = true });
                    }

                    break;

                case AutolinkInline autolink:
                    AddText(autolink.Url, style with { Link = true });
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard || renderer.Compact)
                    {
                        Inlines.Add(new LineBreak());
                    }
                    else
                    {
                        AddText(" ", style);
                    }

                    break;

                case HtmlEntityInline entity:
                    AddText(entity.Transcoded.ToString(), style);
                    break;

                case HtmlInline html:
                    var tag = html.Tag;
                    if (tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                    {
                        Inlines.Add(new LineBreak());
                    }
                    else if (tag.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                    {
                        AddImage(MarkdownSupport.GetAttribute(tag, "src"), MarkdownSupport.GetAttribute(tag, "width"),
                            MarkdownSupport.GetAttribute(tag, "height"), MarkdownSupport.GetAttribute(tag, "alt"), style);
                    }

                    break;

                case ContainerInline nested:
                    AddInlines(nested, style);
                    break;
            }
        }

        private void AddText(string text, InlineStyle style)
        {
            if (text.Length == 0)
            {
                return;
            }

            var run = new Run(text);
            if (style.Bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (style.Italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (style.Strike)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
            }

            if (style.Link)
            {
                run.Foreground = renderer.LinkBrush;
            }

            if (style.Code)
            {
                run.FontFamily = MonoFont;
                run.FontSize = fontSize * 0.88;
                run.Background = renderer.InlineCodeBackground;
            }

            Inlines.Add(run);
        }

        private void AddImage(string? url, string? width, string? height, string? alt, InlineStyle style)
        {
            var bitmap = renderer.GetBitmap(url);
            if (bitmap is null || renderer.GetImageSize(bitmap, width, height) is not { } size)
            {
                // 图片不可用（SVG、下载失败）时，在正文中保留替代文字。
                if (!string.IsNullOrWhiteSpace(alt) && renderer.Compact)
                {
                    AddText($"[{alt}]", style with { Link = true });
                }

                return;
            }

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = size.Width,
                MaxHeight = size.Height,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (size.Height <= InlineImageMaxHeight)
            {
                image.Width = size.Width;
                image.Height = size.Height;
                _hasInlineUi = true;
                Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
                return;
            }

            Close();
            _blocks.Add(image);
        }
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike, bool Link, bool Code);
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, AvaloniaProperty property, object? value) where T : AvaloniaObject
    {
        control.SetValue(property, value);
        return control;
    }
}
