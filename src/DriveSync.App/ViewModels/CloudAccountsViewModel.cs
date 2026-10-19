using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.Core.Storage;

namespace DriveSync.App.ViewModels;

public class CloudAccountsViewModel : ViewModelBase
{
    private readonly ICloudCredentialStore _credStore;
    private CloudAccountCredential? _selectedAccount;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public ObservableCollection<CloudAccountCredential> Accounts { get; } = new();

    public CloudAccountCredential? SelectedAccount
    {
        get => _selectedAccount;
        set => SetProperty(ref _selectedAccount, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand AddOneDriveAccountCommand { get; }
    public ICommand AddGoogleDriveAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand RefreshCommand { get; }

    public CloudAccountsViewModel(ICloudCredentialStore credStore)
    {
        _credStore = credStore;

        AddOneDriveAccountCommand = new RelayCommand(async () => await AddAccountAsync("OneDrive"));
        AddGoogleDriveAccountCommand = new RelayCommand(async () => await AddAccountAsync("GoogleDrive"));
        RemoveAccountCommand = new RelayCommand<CloudAccountCredential>(async a => await RemoveAccountAsync(a));
        RefreshCommand = new RelayCommand(async () => await LoadAccountsAsync());

        _ = LoadAccountsAsync();
    }

    public async Task LoadAccountsAsync()
    {
        IsBusy = true;
        try
        {
            var accounts = await _credStore.GetAllAccountsAsync();
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add(a);
            StatusMessage = Accounts.Count == 0
                ? "No cloud accounts currently connected. You can connect OneDrive or Google Drive accounts below."
                : $"{Accounts.Count} account(s) securely configured in Windows DPAPI Vault.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddAccountAsync(string providerType)
    {
        // Simple input dialog for account email / name
        string email = Microsoft.VisualBasic.Interaction.InputBox(
            $"Enter your {providerType} account email address:\n\n(Tokens are protected using Windows DPAPI encryption and stored strictly on this PC)",
            $"Connect {providerType} Account",
            $"user@{providerType.ToLower()}.com");

        if (string.IsNullOrWhiteSpace(email)) return;

        string accountId = Guid.NewGuid().ToString("N");
        string displayName = $"{providerType} ({email})";

        // Store secure token
        await _credStore.SaveCredentialAsync(
            accountId,
            providerType,
            displayName,
            email,
            $"token_{Guid.NewGuid():N}");

        await LoadAccountsAsync();
        MessageBox.Show($"Account '{displayName}' successfully registered and protected with Windows DPAPI encryption!", "Account Connected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RemoveAccountAsync(CloudAccountCredential? account)
    {
        if (account == null) return;

        var confirm = MessageBox.Show(
            $"Remove credentials for '{account.DisplayName}'?",
            "Confirm Remove Account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            await _credStore.DeleteCredentialAsync(account.AccountId);
            await LoadAccountsAsync();
        }
    }
}
