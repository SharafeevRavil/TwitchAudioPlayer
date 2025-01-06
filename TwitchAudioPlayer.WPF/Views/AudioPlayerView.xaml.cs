using System.Windows.Controls;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class AudioPlayerView : UserControl
{
    public AudioPlayerView(AudioPlayerViewModel playerViewModel)
    {
        InitializeComponent();
        DataContext = playerViewModel;
    }
}