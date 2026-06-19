using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class YtAudioView : UserControl
{
    private readonly YtAudioViewModel _viewModel;

    public YtAudioView(YtAudioViewModel ytAudioViewModel)
    {
        InitializeComponent();
        _viewModel = ytAudioViewModel;
        DataContext = ytAudioViewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Unloaded += (_, _) => _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(YtAudioViewModel.PlayedSelectedTrack) ||
            _viewModel.PlayedSelectedTrack == null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => PlayedTracksListBox.ScrollIntoView(_viewModel.PlayedSelectedTrack),
            DispatcherPriority.Background);
    }
}
