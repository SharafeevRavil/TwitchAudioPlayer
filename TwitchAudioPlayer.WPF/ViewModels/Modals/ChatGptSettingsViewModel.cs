using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.ChatGpt;
using TwitchAudioPlayer.WPF.Services.MusicOrder;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class ChatGptSettingsViewModel : ModalViewModelBase, IDisposable
{
    private readonly IUserSettingsManager _settingsManager;
    private readonly ChatGptResolverService _resolver;
    private readonly VkYouTubePlaybackService _vkYouTube;

    [ObservableProperty] private bool _enabled;
    [NotifyPropertyChangedFor(nameof(IsDeepSeekProvider))]
    [ObservableProperty] private AiResolverProvider _provider;
    [ObservableProperty] private bool _deepSeekUseSearch;
    [ObservableProperty] private bool _deepSeekUseDeepThink;
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
        ChatGptResolverService resolver,
        VkYouTubePlaybackService vkYouTube)
    {
        _settingsManager = settingsManager;
        _resolver = resolver;
        _vkYouTube = vkYouTube;
        var settings = settingsManager.Settings.ChatGptResolver;
        Enabled = settings.Enabled;
        Provider = settings.Provider;
        DeepSeekUseSearch = settings.DeepSeekUseSearch;
        DeepSeekUseDeepThink = settings.DeepSeekUseDeepThink;
        UseAnonymous = settings.UseAnonymous;
        ProjectName = settings.ProjectName;
        Accounts = new ObservableCollection<ChatGptAccountSettings>(settings.Accounts);
        SelectedAccount = Accounts.FirstOrDefault(account => account.Id == settings.ActiveAccountId) ??
                          Accounts.FirstOrDefault();
        Status = resolver.Status;
        resolver.StatusChanged += ResolverOnStatusChanged;
    }

    public ObservableCollection<ChatGptAccountSettings> Accounts { get; }
    public IReadOnlyList<AiResolverProvider> ProviderOptions { get; } =
        Enum.GetValues<AiResolverProvider>();
    public bool IsDeepSeekProvider => Provider == AiResolverProvider.DeepSeekWeb;
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
            ? "Anonymous provider window opened. If it asks for login, this region/session does not allow guest chat."
            : $"{ProviderName} window opened. Sign in there, then press Refresh status.";
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
            : $"Logout page opened for {SelectedAccount?.Name} in {ProviderName}.";
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
            ? "Anonymous provider profile reopened."
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
            ? $"Anonymous {ProviderName} opened. Project setting is ignored without an account."
            : $"{ProviderName} opened. Project selection is disabled for now; the resolver will use a regular chat.";
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

    [RelayCommand]
    private async Task ClearYouTubeResolveCacheAsync()
    {
        if (!await TryOperationAsync(async () =>
            {
                await _vkYouTube.ClearCacheAsync();
                await _resolver.ClearDecisionCacheAsync();
            }))
            return;

        Status = "YouTube resolve cache cleared: local search/ranking, manual choices, and AI decisions were deleted.";
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
                ? $"Anonymous {ProviderName} profile has a working composer."
                : $"Anonymous {ProviderName} has no composer yet. It may require login or still be loading."
            : loggedIn
                ? $"{SelectedAccount?.Name} is logged in to {ProviderName}."
                : $"{SelectedAccount?.Name} is not logged in, or {ProviderName} has not finished loading.";
    }

    protected override async Task SaveAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Enabled = Enabled;
        settings.Provider = Provider;
        settings.DeepSeekUseSearch = DeepSeekUseSearch;
        settings.DeepSeekUseDeepThink = DeepSeekUseDeepThink;
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
        throw new InvalidOperationException($"Select a {ProviderName} account first");

    private async Task ActivateSelectedAccountAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Provider = Provider;
        settings.DeepSeekUseSearch = DeepSeekUseSearch;
        settings.DeepSeekUseDeepThink = DeepSeekUseDeepThink;
        settings.ProjectName = ProjectName.Trim();
        settings.UseAnonymous = UseAnonymous;
        settings.ActiveAccountId = UseAnonymous ? null : SelectedAccount?.Id;
        await PersistAccountsAsync();
        await _settingsManager.SaveSettingsSilentlyAsync();
    }

    private async Task PersistAccountsAsync()
    {
        var settings = _settingsManager.Settings.ChatGptResolver;
        settings.Provider = Provider;
        settings.DeepSeekUseSearch = DeepSeekUseSearch;
        settings.DeepSeekUseDeepThink = DeepSeekUseDeepThink;
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
            Status = $"{ProviderName} operation failed: {exception.Message}";
            return false;
        }
    }

    private string ProviderName => Provider switch
    {
        AiResolverProvider.DeepSeekWeb => "DeepSeek",
        _ => "ChatGPT"
    };

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
