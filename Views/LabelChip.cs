using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>
/// Issue 标签。浅色主题用标签原色做底、自动选黑白字；
/// 深色主题与 GitHub 一致：半透明原色做底，提亮原色作文字和描边。
/// </summary>
public sealed class LabelChip : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LabelChip, string?>(nameof(Text));

    public static readonly StyledProperty<Color> LabelColorProperty =
        AvaloniaProperty.Register<LabelChip, Color>(nameof(LabelColor), Color.Parse("#EDEDED"));

    private readonly TextBlock _text = new()
    {
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 240,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    static LabelChip()
    {
        TextProperty.Changed.AddClassHandler<LabelChip>((chip, _) => chip.Update());
        LabelColorProperty.Changed.AddClassHandler<LabelChip>((chip, _) => chip.Update());
    }

    public LabelChip()
    {
        Height = 24;
        Padding = new Thickness(10, 0);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Child = _text;
        ActualThemeVariantChanged += (_, _) => Update();
        Update();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Color LabelColor
    {
        get => GetValue(LabelColorProperty);
        set => SetValue(LabelColorProperty, value);
    }

    private void Update()
    {
        _text.Text = Text;
        var color = LabelColor;

        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            var text = Lighten(color);
            Background = new ImmutableSolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B));
            BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(110, text.R, text.G, text.B));
            _text.Foreground = new ImmutableSolidColorBrush(text);
            return;
        }

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255d;
        Background = new ImmutableSolidColorBrush(color);
        BorderBrush = new ImmutableSolidColorBrush(color);
        _text.Foreground = new ImmutableSolidColorBrush(luminance > 0.6 ? Color.Parse("#1F2328") : Colors.White);
    }

    /// <summary>把过暗的标签色提亮到深色背景上可读的亮度。</summary>
    private static Color Lighten(Color color)
    {
        var hsl = color.ToHsl();
        var lightness = Math.Max(hsl.L, 0.72);
        return HslColor.ToRgb(hsl.H, Math.Min(hsl.S, 0.9), lightness, 1);
    }
}
