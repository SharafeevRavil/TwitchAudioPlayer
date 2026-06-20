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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNamedAccountMode))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAccountCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloginCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshStatusCommand))]
    private bool _useAnonymous;
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
        UseAnonymous = settings.UseAnonymous;
        ProjectName = settings.ProjectName;
        Accounts = new ObservableCollection<ChatGptAccountSettings>(settings.Accounts);
        SelectedAccount = Accounts.FirstOrDefault(account => account.Id == settings.ActiveAccountId) ??
                          Accounts.FirstOrDefault();
        Status = resolver.Status;
        resolver.StatusChanged += ResolverOnStatusChanged;
    }

    public ObservableCollection<ChatGptAccountSettings> Accounts { get; }
    public bool IsNamedAccountMode => !UseAnonymous;

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

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task LoginAsync()
    {
        var accountId = GetTargetAccountId();
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ShowAccountAsync(accountId);
            }))
            return;
        Status = UseAnonymous
            ? "Anonymous ChatGPT window opened. If ChatGPT asks for login, this region/session does not allow guest chat."
            : "ChatGPT window opened. Sign in there, then press Refresh status.";
    }

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task LogoutAsync()
    {
        var accountId = GetTargetAccountId();
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.LogoutAsync(accountId);
            }))
            return;
        Status = UseAnonymous
            ? "Anonymous profile logout page opened."
            : $"Logout page opened for {SelectedAccount?.Name}.";
    }

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task ReloginAsync()
    {
        var accountId = GetTargetAccountId();
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ReloginAsync(accountId);
            }))
            return;
        Status = UseAnonymous
            ? "Anonymous ChatGPT profile reopened."
            : "Previous session was logged out. Sign in with the required account.";
    }

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task OpenProjectAsync()
    {
        var accountId = GetTargetAccountId();
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.OpenProjectAsync(accountId, ProjectName.Trim());
            }))
            return;
        Status = UseAnonymous
            ? "Anonymous ChatGPT opened. Project setting is ignored without an account."
            : string.IsNullOrWhiteSpace(ProjectName)
            ? "ChatGPT opened. The resolver will use a regular chat."
            : $"Project “{ProjectName.Trim()}” opened. If this is the wrong page, check the exact project name.";
    }

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task ResetChatAsync()
    {
        var accountId = GetTargetAccountId();
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                await _resolver.ResetConversationAsync(accountId);
            }))
            return;
        Status = "Saved resolver chat was reset. A new one will be created on the next track.";
    }

    [RelayCommand(CanExecute = nameof(HasResolverSession))]
    private async Task RefreshStatusAsync()
    {
        var accountId = GetTargetAccountId();
        var loggedIn = false;
        if (!await TryOperationAsync(async () =>
            {
                await ActivateSelectedAccountAsync();
                loggedIn = await _resolver.IsLoggedInAsync(accountId);
            }))
            return;
        Status = UseAnonymous
            ? loggedIn
                ? "Anonymous ChatGPT profile has a working composer."
                : "Anonymous ChatGPT has no composer yet. It may require login or still be loading."
            : loggedIn
                ? $"{SelectedAccount?.Name} is logged in."
                : $"{SelectedAccount?.Name} is not logged in, or ChatGPT has not finished loading.";
    }

    protected override async Task SaveAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Enabled = Enabled;
        settings.UseAnonymous = UseAnonymous;
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

    private bool HasSelectedAccount() => !UseAnonymous && SelectedAccount is not null;
    private bool HasResolverSession() => UseAnonymous || SelectedAccount is not null;
    private Guid GetTargetAccountId() => UseAnonymous ? Guid.Empty : SelectedAccount?.Id ??
        throw new InvalidOperationException("Select a ChatGPT account first");

    private async Task ActivateSelectedAccountAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.ProjectName = ProjectName.Trim();
        settings.UseAnonymous = UseAnonymous;
        settings.ActiveAccountId = UseAnonymous ? null : SelectedAccount?.Id;
        await PersistAccountsAsync();
        await _settingsManager.SaveSettingsSilentlyAsync();
    }

    private async Task PersistAccountsAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Accounts = Accounts.ToList();
        settings.ActiveAccountId = UseAnonymous ? null : SelectedAccount?.Id;
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
