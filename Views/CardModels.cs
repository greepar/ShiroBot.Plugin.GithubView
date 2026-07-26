using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>
/// 标签胶囊(issue/PR label)。
/// </summary>
public sealed class LabelChip
{
    public required string Name { get; init; }
    public required Color Background { get; init; }
    public required Color Foreground { get; init; }
    public required Color Border { get; init; }

    public static LabelChip FromHex(string name, string? hex)
    {
        var background = TryParse(hex, out var color) ? color : Color.Parse("#EAEEF2");
        // GitHub 风格:浅色底 + 深色字。按亮度决定文字用深色还是白色。
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255d;
        var foreground = luminance > 0.6 ? Color.Parse("#24292F") : Colors.White;
        var border = Color.FromArgb(64, background.R, background.G, background.B);
        return new LabelChip { Name = name, Background = background, Foreground = foreground, Border = border };
    }

    private static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        return Color.TryParse(hex.StartsWith('#') ? hex : "#" + hex, out color);
    }
}

/// <summary>
/// 列表卡片中的一行(issue / PR / workflow run 通用)。
/// </summary>
public sealed class ListItem
{
    public required string IconData { get; init; }
    public required Color IconColor { get; init; }
    public required string Title { get; init; }
    public required string SubText { get; init; }
    public string RightText { get; init; } = "";

    public IBrush IconBrush => new SolidColorBrush(IconColor);
}

/// <summary>
/// Run 详情卡中的单个 job 行。
/// </summary>
public sealed class JobItem
{
    public required string IconData { get; init; }
    public required Color IconColor { get; init; }
    public required string Name { get; init; }
    public string DurationText { get; init; } = "";

    public IBrush IconBrush => new SolidColorBrush(IconColor);
}

internal static class AvatarHelper
{
    public static Bitmap? ToBitmap(byte[]? bytes)
        => bytes is null ? null : new Bitmap(new MemoryStream(bytes));
}

/// <summary>
/// GitHub 状态配色。
/// </summary>
public static class GitHubColors
{
    public static readonly Color OpenGreen = Color.Parse("#1A7F37");
    public static readonly Color ClosedPurple = Color.Parse("#8250DF");
    public static readonly Color ClosedRed = Color.Parse("#CF222E");
    public static readonly Color NeutralGray = Color.Parse("#57606A");
    public static readonly Color WarningYellow = Color.Parse("#BF8700");
}
