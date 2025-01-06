using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class HotkeySettingsViewModel : ModalViewModelBase
{
    private readonly IUserSettingsManager _settingsManager;

    public HotkeySettingsViewModel(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        
        PrevKey = _settingsManager.Settings.PrevKey;
        PrevModifiers = _settingsManager.Settings.PrevModifiers;
        UpdateText("PrevKeyTextBox", PrevKey, PrevModifiers);
        PauseKey = _settingsManager.Settings.PauseKey;
        PauseModifiers = _settingsManager.Settings.PauseModifiers;
        UpdateText("PauseKeyTextBox", PauseKey, PauseModifiers);
        NextKey = _settingsManager.Settings.NextKey;
        NextModifiers = _settingsManager.Settings.NextModifiers;
        UpdateText("NextKeyTextBox", NextKey, NextModifiers);
        
        VolMuteKey = _settingsManager.Settings.VolMuteKey;
        VolMuteModifiers = _settingsManager.Settings.VolMuteModifiers;
        UpdateText("VolMuteKeyTextBox", VolMuteKey, VolMuteModifiers);
        VolDownKey = _settingsManager.Settings.VolDownKey;
        VolDownModifiers = _settingsManager.Settings.VolDownModifiers;
        UpdateText("VolDownKeyTextBox", VolDownKey, VolDownModifiers);
        VolUpKey = _settingsManager.Settings.VolUpKey;
        VolUpModifiers = _settingsManager.Settings.VolUpModifiers;
        UpdateText("VolUpKeyTextBox", VolUpKey, VolUpModifiers);
    }

    [ObservableProperty] private Key _prevKey;
    [ObservableProperty] private ModifierKeys _prevModifiers;
    [ObservableProperty] private string _prevText;
    [ObservableProperty] private Key _pauseKey;
    [ObservableProperty] private ModifierKeys _pauseModifiers;
    [ObservableProperty] private string _pauseText;
    [ObservableProperty] private Key _nextKey;
    [ObservableProperty] private ModifierKeys _nextModifiers;
    [ObservableProperty] private string _nextText;
    
    [ObservableProperty] private Key _volMuteKey;
    [ObservableProperty] private ModifierKeys _volMuteModifiers;
    [ObservableProperty] private string _volMuteText;
    [ObservableProperty] private Key _volDownKey;
    [ObservableProperty] private ModifierKeys _volDownModifiers;
    [ObservableProperty] private string _volDownText;
    [ObservableProperty] private Key _volUpKey;
    [ObservableProperty] private ModifierKeys _volUpModifiers;
    [ObservableProperty] private string _volUpText;
    
    protected override async Task SaveAsync()
    {
        _settingsManager.Settings.PrevKey = PrevKey;
        _settingsManager.Settings.PrevModifiers = PrevModifiers;
        _settingsManager.Settings.PauseKey = PauseKey;
        _settingsManager.Settings.PauseModifiers = PauseModifiers;
        _settingsManager.Settings.NextKey = NextKey;
        _settingsManager.Settings.NextModifiers = NextModifiers;
        
        _settingsManager.Settings.VolMuteKey = VolMuteKey;
        _settingsManager.Settings.VolMuteModifiers = VolMuteModifiers;
        _settingsManager.Settings.VolDownKey = VolDownKey;
        _settingsManager.Settings.VolDownModifiers = VolDownModifiers;
        _settingsManager.Settings.VolUpKey = VolUpKey;
        _settingsManager.Settings.VolUpModifiers = VolUpModifiers;
        
        await _settingsManager.SaveSettingsAsync();
        await base.SaveAsync();
    }

    public void UpdateText(string textBoxName, Key key, ModifierKeys modifiers)
    {
        switch (textBoxName)
        {
            case "PrevKeyTextBox":
                PrevKey = key;
                PrevModifiers = modifiers;
                PrevText = TextBoxText(key, modifiers);
                break;
            case "PauseKeyTextBox":
                PauseKey = key;
                PauseModifiers = modifiers;
                PauseText = TextBoxText(key, modifiers);
                break;
            case "NextKeyTextBox":
                NextKey = key;
                NextModifiers = modifiers;
                NextText = TextBoxText(key, modifiers);
                break;
            case "VolMuteKeyTextBox":
                VolMuteKey = key;
                VolMuteModifiers = modifiers;
                VolMuteText = TextBoxText(key, modifiers);
                break;
            case "VolDownKeyTextBox":
                VolDownKey = key;
                VolDownModifiers = modifiers;
                VolDownText = TextBoxText(key, modifiers);
                break;
            case "VolUpKeyTextBox":
                VolUpKey = key;
                VolUpModifiers = modifiers;
                VolUpText = TextBoxText(key, modifiers);
                break;
        }
    }
    
    public static string TextBoxText(Key key, ModifierKeys modifiers) =>
        modifiers == ModifierKeys.None ? $"{key}" : $"{modifiers} + {key}";
}