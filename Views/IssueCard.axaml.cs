using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShiroBot.Plugin.GithubView.Views;

public partial class IssueCard : UserControl
{
    public IssueCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
