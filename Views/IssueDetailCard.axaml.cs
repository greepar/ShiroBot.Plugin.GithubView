using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShiroBot.Plugin.GithubView.Views;

public partial class IssueDetailCard : UserControl
{
    public IssueDetailCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
