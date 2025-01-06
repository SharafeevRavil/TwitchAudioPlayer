using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public abstract partial class ModalViewModelBase : ObservableObject
{
    public Action? CloseAction { get; set; }

    [RelayCommand]
    protected virtual Task SaveAsync()
    {
        CloseAction?.Invoke();
        return Task.CompletedTask;
    }

    [RelayCommand]
    protected virtual void Cancel()
    {
        CloseAction?.Invoke();
    }
}