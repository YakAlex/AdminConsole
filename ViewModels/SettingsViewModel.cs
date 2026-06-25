using AdminConsole.Core.Messages;
using AdminConsole.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AdminConsole.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CredentialStore  _credentials;
    private readonly ZabbixApiClient  _zabbixClient;
    private readonly IMessenger       _messenger;
    private readonly string           _zabbixUrl;

    // ── Загальні ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _closeToTray;

    // ── RDP ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _rdpUsername        = string.Empty;
    [ObservableProperty] private bool   _hasRdpCredentials;
    [ObservableProperty] private bool   _isRdpEditMode;
    [ObservableProperty] private string _rdpNewUsername     = string.Empty;

    // Пароль не зберігаємо у VM як ObservableProperty —
    // він приходить з PasswordBox через SetRdpPassword() з code-behind.
    // Мінімальний час перебування в пам'яті.
    private string _rdpNewPassword = string.Empty;

    // ── Zabbix ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _zabbixTokenMasked    = string.Empty;
    [ObservableProperty] private bool   _hasZabbixCredentials;
    [ObservableProperty] private bool   _isZabbixEditMode;
    [ObservableProperty] private string _zabbixNewToken       = string.Empty;
    [ObservableProperty] private bool   _isTestingZabbix;
    [ObservableProperty] private string _zabbixTestResult     = string.Empty;
    [ObservableProperty] private bool   _zabbixTestSuccess;

    private CancellationTokenSource? _testCts;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(
        CredentialStore  credentials,
        ZabbixApiClient  zabbixClient,
        IMessenger       messenger,
        Microsoft.Extensions.Options.IOptions<Configuration.MonitoringSettings> settings)
    {
        _credentials  = credentials;
        _zabbixClient = zabbixClient;
        _messenger    = messenger;
        _zabbixUrl    = settings.Value.ZabbixUrl;

        RefreshCredentialState();
    }

    // ── Ініціалізація стану ───────────────────────────────────────────────────

    /// <summary>
    /// Оновлює відображення credentials зі сховища.
    /// Викликається при відкритті Settings і після кожної зміни.
    /// </summary>
    public void RefreshCredentialState()
    {
        HasRdpCredentials    = _credentials.HasRdpCredentials;
        RdpUsername          = _credentials.GetRdpUsername();
        HasZabbixCredentials = _credentials.HasZabbixCredentials;
        ZabbixTokenMasked    = _credentials.GetZabbixTokenMasked();

        // Скидаємо edit mode і тимчасові поля при оновленні
        IsRdpEditMode    = false;
        IsZabbixEditMode = false;
        RdpNewUsername   = string.Empty;
        _rdpNewPassword  = string.Empty;
        ZabbixNewToken   = string.Empty;
        ZabbixTestResult = string.Empty;
    }

    // ── RDP команди ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void EnterRdpEditMode()
    {
        RdpNewUsername  = RdpUsername; // pre-fill поточним username
        _rdpNewPassword = string.Empty;
        IsRdpEditMode   = true;
        ZabbixTestResult = string.Empty;
    }

    [RelayCommand]
    private void CancelRdpEdit()
    {
        _rdpNewPassword = string.Empty;
        RdpNewUsername  = string.Empty;
        IsRdpEditMode   = false;
    }

    [RelayCommand]
    private void SaveRdpCredentials()
    {
        if (string.IsNullOrWhiteSpace(RdpNewUsername))
        {
            _messenger.Send(AppLogEntryMessage.Warning("Settings",
                "RDP: username не може бути порожнім."));
            return;
        }

        if (string.IsNullOrWhiteSpace(_rdpNewPassword))
        {
            _messenger.Send(AppLogEntryMessage.Warning("Settings",
                "RDP: пароль не може бути порожнім."));
            return;
        }

        // Зберігаємо у Credential Manager
        _credentials.StoreRdp(RdpNewUsername.Trim(), _rdpNewPassword);

        // Критично: скидаємо прапорець скасування —
        // без цього RdpMonitorService не відновиться якщо юзер раніше
        // натиснув Cancel при першому старті програми.
        _credentials.ResetRdpCancelledFlag();

        // Негайно затираємо пароль з пам'яті VM
        _rdpNewPassword = string.Empty;

        // Публікуємо повідомлення — RdpMonitorService прокинеться і запустить poll
        _messenger.Send(new CredentialsChangedMessage
        {
            Target = CredentialTarget.Rdp,
            Action = CredentialAction.Saved
        });

        _messenger.Send(AppLogEntryMessage.Info("Settings",
            $"RDP credentials збережено для: {RdpNewUsername.Trim()} — запускаємо опитування серверів…"));

        RefreshCredentialState();
    }
    
    [RelayCommand]
    private void ClearRdpCredentials()
    {
        _credentials.ClearRdp();

        _messenger.Send(new RdpCredentialsClearedMessage());

        _messenger.Send(new CredentialsChangedMessage
        {
            Target = CredentialTarget.Rdp,
            Action = CredentialAction.Cleared
        });

        _messenger.Send(AppLogEntryMessage.Warning("Settings",
            "RDP credentials видалено. Моніторинг сесій призупинено."));

        RefreshCredentialState();
    }

    /// <summary>
    /// Викликається з code-behind MainWindow при PasswordChanged.
    /// Пароль не проходить через Binding — приходить напряму з PasswordBox.Password.
    /// </summary>
    public void SetRdpPassword(string password)
    {
        _rdpNewPassword = password;
    }

    // ── Zabbix команди ────────────────────────────────────────────────────────

    [RelayCommand]
    private void EnterZabbixEditMode()
    {
        ZabbixNewToken   = string.Empty;
        ZabbixTestResult = string.Empty;
        IsZabbixEditMode = true;
    }

    [RelayCommand]
    private void CancelZabbixEdit()
    {
        ZabbixNewToken   = string.Empty;
        ZabbixTestResult = string.Empty;
        IsZabbixEditMode = false;
    }

    [RelayCommand]
private async Task SaveZabbixTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(ZabbixNewToken))
        {
            _messenger.Send(AppLogEntryMessage.Warning("Settings",
                "Zabbix: токен не може бути порожнім."));
            return;
        }

        var tokenToSave = ZabbixNewToken.Trim();

        _credentials.StoreZabbixToken(tokenToSave);
        _credentials.ResetZabbixCancelledFlag();

        _messenger.Send(AppLogEntryMessage.Info("Settings",
            "Zabbix API токен збережено — перевіряємо з'єднання…"));

        RefreshCredentialState(); // скидає ZabbixNewToken, IsZabbixEditMode = false

        // Автоматично тестуємо збережений токен — юзер одразу бачить результат
        if (!string.IsNullOrWhiteSpace(_zabbixUrl))
        {
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource();

            IsTestingZabbix  = true;
            ZabbixTestResult = string.Empty;
            ZabbixTestSuccess = false;

            try
            {
                var (success, version, error) = await _zabbixClient
                    .TestConnectionAsync(_zabbixUrl, tokenToSave, _testCts.Token)
                    .ConfigureAwait(false);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ZabbixTestSuccess = success;
                    ZabbixTestResult  = success
                        ? $"✅ Zabbix {version} — токен дійсний"
                        : $"❌ {error}";
                });

                _messenger.Send(new CredentialsChangedMessage
                {
                    Target = CredentialTarget.Zabbix,
                    Action = CredentialAction.Saved
                });

                if (success)
                {
                    _messenger.Send(AppLogEntryMessage.Success("Settings",
                        $"Zabbix {version} — з'єднання успішне, поллер оновлено."));
                }
                else
                {
                    _messenger.Send(AppLogEntryMessage.Warning("Settings",
                        $"Токен збережено, тест показав помилку: {error}. " +
                        $"Поллер спробує самостійно."));
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                IsTestingZabbix = false;
            }
        }
        else
        {
            // ZabbixUrl не налаштований — просто повідомляємо поллер без тесту
            _messenger.Send(new CredentialsChangedMessage
            {
                Target = CredentialTarget.Zabbix,
                Action = CredentialAction.Saved
            });
        }
    }

    [RelayCommand]
    private void ClearZabbixCredentials()
    {
        _credentials.ClearZabbix();

        _messenger.Send(new ZabbixProblemsUpdatedMessage(new ZabbixProblemsPayload(
            Problems: [],
            ErrorMessage: "Zabbix credentials видалено — підключення відсутнє.",
            FetchedAt: DateTimeOffset.Now)));

        _messenger.Send(new CredentialsChangedMessage
        {
            Target = CredentialTarget.Zabbix,
            Action = CredentialAction.Cleared
        });

        _messenger.Send(AppLogEntryMessage.Warning("Settings",
            "Zabbix credentials видалено. Підключення до Zabbix призупинено."));

        RefreshCredentialState();
    }

    [RelayCommand]
    private async Task TestZabbixConnectionAsync()
    {
        var token = !string.IsNullOrWhiteSpace(ZabbixNewToken)
            ? ZabbixNewToken.Trim()
            : _credentials.GetZabbix().Token;

        if (string.IsNullOrWhiteSpace(token))
        {
            ZabbixTestResult = "❌ Введіть токен перед перевіркою";
            ZabbixTestSuccess = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(_zabbixUrl))
        {
            ZabbixTestResult = "❌ ZabbixUrl не налаштований в appsettings.json";
            ZabbixTestSuccess = false;
            return;
        }

        // Скасовуємо попередній тест якщо ще йде
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();

        IsTestingZabbix  = true;
        ZabbixTestResult = string.Empty;
        ZabbixTestSuccess = false;

        try
        {
            var (success, version, error) = await _zabbixClient
                .TestConnectionAsync(_zabbixUrl, token, _testCts.Token)
                .ConfigureAwait(false);

            // Повертаємось у UI-потік
            await System.Windows.Application.Current.Dispatcher
                .InvokeAsync(() =>
                {
                    ZabbixTestSuccess = success;
                    ZabbixTestResult  = success
                        ? $"✅ Zabbix {version} — з'єднання успішне"
                        : $"❌ {error}";
                });
        }
        catch (OperationCanceledException)
        {
            // Юзер закрив Settings поки тест йшов — ігноруємо
        }
        finally
        {
            IsTestingZabbix = false;
        }
    }

    // ── CloseToTray ───────────────────────────────────────────────────────────

    /// <summary>
    /// Завантажує поточне значення CloseToTray зі сховища.
    /// Викликається з MainViewModel при відкритті Settings overlay.
    /// </summary>
    public void LoadCloseToTray(bool value) => CloseToTray = value;

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;

        // Затираємо тимчасовий пароль з пам'яті
        _rdpNewPassword = string.Empty;
        ZabbixNewToken  = string.Empty;
    }
}