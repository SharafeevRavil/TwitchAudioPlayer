using System.Windows.Controls;
using TwitchAudioPlayerWPF.ViewModels;

namespace TwitchAudioPlayerWPF.Views;

public partial class VkAudioView : UserControl
{
    public VkAudioView(VkAudioViewModel vkAudioViewModel)
    {
        InitializeComponent();
        DataContext = vkAudioViewModel;
    }
}