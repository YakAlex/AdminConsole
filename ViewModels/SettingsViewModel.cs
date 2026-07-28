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
    private readonly RdpCredentialValidator _rdpValidator;
    private readonly IMessenger       _messenger;
    private readonly UserSettingsService _userSettings;
    private readonly string           _zabbixUrl;

    // ── Загальні ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _closeToTray;

    /// <summary>
    /// Перемикач RDP-моніторингу. На відміну від CloseToTray —
    /// застосовується МІТТЄВО (через OnRdpMonitoringEnabledChanged нижче),
    /// а не лише при закритті Settings overlay.
    /// </summary>
    [ObservableProperty] private bool _rdpMonitoringEnabled;

    /// <summary>
    /// Перемикач Zabbix-моніторингу. Поведінка аналогічна RdpMonitoringEnabled.
    /// </summary>
    [ObservableProperty] private bool _zabbixMonitoringEnabled;

    // ── RDP ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _rdpUsername        = string.Empty;
    [ObservableProperty] private bool   _hasRdpCredentials;
    [ObservableProperty] private bool   _isRdpEditMode;
    [ObservableProperty] private string _rdpNewUsername     = string.Empty;
    [ObservableProperty] private bool   _isValidatingRdp;
    [ObservableProperty] private string _rdpValidationResult  = string.Empty;
    [ObservableProperty] private bool   _rdpValidationSuccess;

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

    private CancellationTokenSource? _rdpValidationCts;
    private CancellationTokenSource? _zabbixTestCts;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(
        CredentialStore  credentials,
        ZabbixApiClient  zabbixClient,
        IMessenger       messenger,
        RdpCredentialValidator rdpValidator,
        UserSettingsService userSettings,
        Microsoft.Extensions.Options.IOptions<Configuration.MonitoringSettings> settings)
    {
        _credentials  = credentials;
        _zabbixClient = zabbixClient;
        _rdpValidator = rdpValidator;
        _messenger    = messenger;
        _userSettings = userSettings;
        _zabbixUrl    = settings.Value.ZabbixUrl;

        RefreshCredentialState();

        // Важливо: присвоюємо бекінг-поля НАПРЯМУ, а НЕ через публічні
        // властивості — щоб НЕ спрацювати OnXxxChanged (і зайвий Save())
        // під час ініціалізації відповідно даними з минулої сесії.
        _rdpMonitoringEnabled    = _userSettings.Current.RdpMonitoringEnabled;
        _zabbixMonitoringEnabled = _userSettings.Current.ZabbixMonitoringEnabled;

        // EDGE-CASE #3 (холодний старт): публікуємо початковий стан безумовно (і для
        // true, і для false) для RdpSessionViewModel/ZabbixViewModel. Це важливо
        // саме для випадку "моніторинг був вимкнений у минулій сесії":
        // без цього явного Send() ViewModels залишились б у стані "enabled" (дефолт)
        // аж до першого poll, показуючи порожній/завислий UI замість заглушки "вимкнено".
        // На цей момент RdpSessionViewModel/ZabbixViewModel вже зареєстровані
        // (створюються раніше за параметром в конструкторі MainViewModel), а самі
        // RdpMonitorService/ZabbixPollerService — ще ні (стартують пізніше, в host.StartAsync()),
        // тому цей Send до них не дойде — і не потрібно: вони самі прочитають
        // UserSettingsService.Current напряму на своєму першому циклі (Pull).
        _messenger.Send(new MonitoringToggledMessage(MonitoredService.Rdp,    _rdpMonitoringEnabled));
        _messenger.Send(new MonitoringToggledMessage(MonitoredService.Zabbix, _zabbixMonitoringEnabled));
    }

    // ── Перемикачі моніторингу (RDP / Zabbix) ───────────────────

    /// <summary>
    /// Викликається автоматично (CommunityToolkit.Mvvm) при зміні RdpMonitoringEnabled
    /// ЧЕРЕЗ БІНДІНГ з UI (тобто перемикнув користувач), а НЕ при ініціалізації
    /// в конструкторі (там поле встановлюється напряму, без цього хука).
    ///
    /// НЕ логує подію самостійно — єдиним джерелом логу є
    /// RdpMonitorService.EvaluateMonitoringToggle(), який сам виявить транзицію після
    /// пробудження (едге-сасе #2). Два окремі логи на одну дію були б дублюванням.
    /// </summary>
    partial void OnRdpMonitoringEnabledChanged(bool value)
    {
        _userSettings.Current.RdpMonitoringEnabled = value;
        _userSettings.Save();
        _messenger.Send(new MonitoringToggledMessage(MonitoredService.Rdp, value));
    }

    /// <summary>
    /// Аналогічно OnRdpMonitoringEnabledChanged — див. коментар вище.
    /// </summary>
    partial void OnZabbixMonitoringEnabledChanged(bool value)
    {
        _userSettings.Current.ZabbixMonitoringEnabled = value;
        _userSettings.Save();
        _messenger.Send(new MonitoringToggledMessage(MonitoredService.Zabbix, value));
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
        RdpValidationResult  = string.Empty;
        RdpValidationSuccess = false;
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
    private async Task SaveRdpCredentialsAsync()
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

        var usernameToSave = RdpNewUsername.Trim();
        var passwordToSave = _rdpNewPassword;

        _rdpValidationCts?.Cancel();
        _rdpValidationCts?.Dispose();
        _rdpValidationCts = new CancellationTokenSource();

        IsValidatingRdp      = true;
        RdpValidationResult  = string.Empty;
        RdpValidationSuccess = false;

        try
        {
            var (success, error) = await _rdpValidator
                .ValidateAsync(usernameToSave, passwordToSave, _rdpValidationCts.Token);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                RdpValidationSuccess = success;
                RdpValidationResult  = success
                    ? "✅ Credentials підтверджено"
                    : $"❌ {error}";
            });

            if (!success)
            {
                // Не зберігаємо невалідні credentials
                _messenger.Send(AppLogEntryMessage.Warning("Settings",
                    $"RDP credentials не збережено: {error}"));
                return;
            }

            // Валідація пройшла — зберігаємо і запускаємо poll
            _credentials.StoreRdp(usernameToSave, passwordToSave);
            _credentials.ResetRdpCancelledFlag();
            _rdpNewPassword = string.Empty;

            _messenger.Send(new CredentialsChangedMessage
            {
                Target = CredentialTarget.Rdp,
                Action = CredentialAction.Saved
            });

            _messenger.Send(AppLogEntryMessage.Success("Settings",
                $"RDP credentials збережено для: {usernameToSave} — запускаємо опитування серверів…"));

            RefreshCredentialState();
        }
        catch (OperationCanceledException)
        {
            RdpValidationResult  = string.Empty;
            RdpValidationSuccess = false;
        }
        finally
        {
            IsValidatingRdp  = false;
            passwordToSave   = string.Empty;
        }
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

        _messenger.Send(new CredentialsChangedMessage
        {
            Target = CredentialTarget.Zabbix,
            Action = CredentialAction.Saved
        });

        _messenger.Send(AppLogEntryMessage.Info("Settings",
            "Zabbix API токен збережено — перевіряємо з'єднання…"));

        HasZabbixCredentials = _credentials.HasZabbixCredentials;
        ZabbixTokenMasked    = _credentials.GetZabbixTokenMasked();
        if (!string.IsNullOrWhiteSpace(_zabbixUrl))
        {
            _zabbixTestCts?.Cancel();
            _zabbixTestCts?.Dispose();
            _zabbixTestCts = new CancellationTokenSource();

            IsTestingZabbix   = true;
            ZabbixTestResult  = string.Empty;
            ZabbixTestSuccess = false;

            try
            {
                var (success, version, error) = await _zabbixClient
                    .TestConnectionAsync(_zabbixUrl, tokenToSave, _zabbixTestCts.Token)
                    .ConfigureAwait(false);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ZabbixTestSuccess = success;
                    ZabbixTestResult  = success
                        ? $"✅ Zabbix {version} — токен дійсний"
                        : $"❌ {error}";
                });

                if (success)
                {
                    _messenger.Send(AppLogEntryMessage.Success("Settings",
                        $"Zabbix {version} — з'єднання успішне, поллер оновлено."));
                    try
                    {
                        await Task.Delay(1500, _zabbixTestCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    RefreshCredentialState();
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
            RefreshCredentialState();
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
        _zabbixTestCts?.Cancel();
        _zabbixTestCts?.Dispose();
        _zabbixTestCts = new CancellationTokenSource();

        IsTestingZabbix  = true;
        ZabbixTestResult = string.Empty;
        ZabbixTestSuccess = false;

        try
        {
            var (success, version, error) = await _zabbixClient
                .TestConnectionAsync(_zabbixUrl, token, _zabbixTestCts.Token)
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

    /// <summary>
    /// Скасовує активний HTTP тест при закритті Settings overlay.
    /// Не скидає стан VM — Singleton живе весь час роботи програми.
    /// </summary>
    public void CancelPendingTest()
    {
        _rdpValidationCts?.Cancel();
        _rdpValidationCts?.Dispose();
        _rdpValidationCts = null;
        IsValidatingRdp      = false;
        RdpValidationResult  = string.Empty;

        _zabbixTestCts?.Cancel();
        _zabbixTestCts?.Dispose();
        _zabbixTestCts = null;
        IsTestingZabbix  = false;
        ZabbixTestResult = string.Empty;
    }

    /// <summary>
    /// Фінальне очищення при виході з програми.
    /// Викликається з App.xaml.cs OnExit.
    /// </summary>
    public void Dispose()
    {
        CancelPendingTest();
        _rdpNewPassword = string.Empty;
        ZabbixNewToken  = string.Empty;
    }
}