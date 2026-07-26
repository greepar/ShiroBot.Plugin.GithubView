using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShiroBot.Plugin.GithubView.Views;

public partial class CommitCard : UserControl
{
    public CommitCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
