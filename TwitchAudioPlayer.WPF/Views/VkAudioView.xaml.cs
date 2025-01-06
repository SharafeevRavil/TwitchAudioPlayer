using System.Windows.Controls;
using TwitchAudioPlayer.WPF.ViewModels;

namespace TwitchAudioPlayer.WPF.Views;

public partial class VkAudioView : UserControl
{
    public VkAudioView(VkAudioViewModel vkAudioViewModel)
    {
        InitializeComponent();
        DataContext = vkAudioViewModel;
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        const double bottomOffsetToExecute = 200;
        if (e.ExtentHeight == 0) return;
        if (e.ExtentHeight - e.ViewportHeight - e.VerticalOffset > bottomOffsetToExecute) return;
        if (DataContext is not VkAudioViewModel viewModel) return;
        viewModel.ScrollNearEndCommand.Execute(null);
    }
}