using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShiroBot.Plugin.GithubView.Views;

/// <summary>GitHub 浅色 / 深色配色资源，供各卡片合并使用。</summary>
public partial class GitHubTheme : ResourceDictionary
{
    public GitHubTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
