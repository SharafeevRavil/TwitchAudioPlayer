using System.Windows.Controls;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class YtAudioView : UserControl
{
    public YtAudioView(YtAudioViewModel ytAudioViewModel)
    {
        InitializeComponent();
        DataContext = ytAudioViewModel;
    }
}
