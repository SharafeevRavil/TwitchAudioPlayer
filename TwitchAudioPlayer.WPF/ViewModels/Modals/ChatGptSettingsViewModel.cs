using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.ChatGpt;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class ChatGptSettingsViewModel : ModalViewModelBase, IDisposable
{
    private readonly IUserSettingsManager _settingsManager;
    private readonly ChatGptResolverService _resolver;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _projectName;
    [ObservableProperty] private string _newAccountName = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloginCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshStatusCommand))]
    private ChatGptAccountSettings? _selectedAccount;
    [ObservableProperty] private string _status;

    public ChatGptSettingsViewModel(
        IUserSettingsManager settingsManager,
        ChatGptResolverService resolver)
    {
        _settingsManager = settingsManager;
        _resolver = resolver;
        var settings = settingsManager.Settings.ChatGptResolver;
        Enabled = settings.Enabled;
        ProjectName = settings.ProjectName;
        Accounts = new ObservableCollection<ChatGptAccountSettings>(settings.Accounts);
        SelectedAccount = Accounts.FirstOrDefault(account => account.Id == settings.ActiveAccountId) ??
                          Accounts.FirstOrDefault();
        Status = resolver.Status;
        resolver.StatusChanged += ResolverOnStatusChanged;
    }

    public ObservableCollection<ChatGptAccountSettings> Accounts { get; }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewAccountName)
            ? $"ChatGPT account {Accounts.Count + 1}"
            : NewAccountName.Trim();
        var account = new ChatGptAccountSettings { Name = name };
        Accounts.Add(account);
        SelectedAccount = account;
        NewAccountName = string.Empty;
        await PersistAccountsAsync();
        Status = $"Added {name}. Open Login and sign in manually.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task RemoveAccountAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        _resolver.CloseAccount(account.Id);
        Accounts.Remove(account);
        SelectedAccount = Accounts.FirstOrDefault();
        await PersistAccountsAsync();
        Status = $"Removed {account.Name} from the app. Its local browser profile was left intact.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task LoginAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ShowAccountAsync(account.Id);
            }))
            return;
        Status = "ChatGPT window opened. Sign in there, then press Refresh status.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task LogoutAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.LogoutAsync(account.Id);
            }))
            return;
        Status = $"Logout page opened for {account.Name}.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task ReloginAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ReloginAsync(account.Id);
            }))
            return;
        Status = "Previous session was logged out. Sign in with the required account.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task OpenProjectAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.OpenProjectAsync(account.Id, ProjectName.Trim());
            }))
            return;
        Status = string.IsNullOrWhiteSpace(ProjectName)
            ? "ChatGPT opened. The resolver will use a regular chat."
            : $"Project “{ProjectName.Trim()}” opened. If this is the wrong page, check the exact project name.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task ResetChatAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ResetConversationAsync(account.Id);
            }))
            return;
        Status = "Saved resolver chat was reset. A new one will be created on the next track.";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task RefreshStatusAsync()
    {
        if (SelectedAccount is not { } account)
            return;
        var loggedIn = false;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                loggedIn = await _resolver.IsLoggedInAsync(account.Id);
            }))
            return;
        Status = loggedIn
            ? $"{account.Name} is logged in."
            : $"{account.Name} is not logged in, or ChatGPT has not finished loading.";
    }

    protected override async Task SaveAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Enabled = Enabled;
        settings.ProjectName = ProjectName.Trim();
        await PersistAccountsAsync();
        await _settingsManager.SaveSettingsAsync();
        _resolver.StatusChanged -= ResolverOnStatusChanged;
        await base.SaveAsync();
    }

    protected override void Cancel()
    {
        _resolver.StatusChanged -= ResolverOnStatusChanged;
        base.Cancel();
    }

    private bool HasSelectedAccount() => SelectedAccount is not null;

    private async Task ActivateSelectedAccountAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.ProjectName = ProjectName.Trim();
        settings.ActiveAccountId = SelectedAccount?.Id;
        await PersistAccountsAsync();
        await _settingsManager.SaveSettingsSilentlyAsync();
    }

    private async Task PersistAccountsAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Accounts = Accounts.ToList();
        settings.ActiveAccountId = SelectedAccount?.Id;
        await _settingsManager.SaveSettingsSilentlyAsync();
    }

    private async Task<bool> TryOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (Exception exception)
        {
            Status = $"ChatGPT operation failed: {exception.Message}";
            return false;
        }
    }

    private void ResolverOnStatusChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            Status = _resolver.Status;
        else
            dispatcher.InvokeAsync(() => Status = _resolver.Status);
    }

    public void Dispose() => _resolver.StatusChanged -= ResolverOnStatusChanged;
}
