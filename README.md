# AdminConsole v2

> Десктопний WPF-інструмент для моніторингу та адміністрування серверної інфраструктури домену, з віддаленим керуванням через Telegram-бота.
> Написаний на **C# / .NET 8**, архітектура — **MVVM + Microsoft Generic Host**.

---

## Зміст

- [Можливості](#можливості)
- [Архітектура](#архітектура)
- [Структура проєкту](#структура-проєкту)
- [Конфігурація](#конфігурація)
- [Maintenance Windows (Планове обслуговування)](#maintenance-windows-планове-обслуговування)
- [Безпека та Credentials](#безпека-та-credentials)
- [Telegram Bot](#telegram-bot)
- [Фонові сервіси](#фонові-сервіси)
- [Messenger та повідомлення](#messenger-та-повідомлення)
- [Паралельність та потокобезпека](#паралельність-та-потокобезпека)
- [Logging](#logging)
- [Залежності](#залежності)
- [Збірка та запуск](#збірка-та-запуск)
- [Вимоги до середовища](#вимоги-до-середовища)
- [Відомі обмеження](#відомі-обмеження)
- [Changelog](#changelog-останні-зміни)

---

## Можливості

| Вкладка | Опис |
|---------|------|
| **Ping Dashboard** | Безперервний ICMP-моніторинг усіх серверів з групуванням, індикацією затримки та кольоровим статусом. Логує лише реальні зміни стану. Паралелізм обмежений `SemaphoreSlim`. Результати всього циклу публікуються одним batch-повідомленням. Пошук рядка сервера — через `Dictionary<string, PingResultViewModel>` (O(1)) замість лінійного пошуку. Кожен рядок має кнопку **Maintenance Mode** (🔧). Стовпець "SINCE" (час з моменту зміни статусу) прибрано з UI та з бекенду — визнано зайвим. |
| **Uptime & Incidents** | Автоматичне фіксування інцидентів недоступності: час падіння, відновлення, тривалість простою. Повна фільтрація по сервері, групі, даті та статусу. Зберігається на диск у `logs/uptime-YYYY-MM.json` з атомарним записом. Короткі мережеві "миготіння" (нижче порогу `MinIncidentDurationSeconds`) не фіксуються взагалі — ані на диску, ані в UI. Інциденти, перервані через Maintenance Mode, позначаються окремо (`ClosedByMaintenance`). |
| **Resource Monitor** | CPU та RAM у реальному часі — для `localhost` через `PerformanceCounter` і `GlobalMemoryStatusEx`, для віддалених машин через WMI. Відображає останні помилки Event Log обраного вузла. Помилки опитування конкретного вузла агрегуються у `HasError` (`ServerPollStatusViewModel`) для наочного індикатора в UI. |
| **RDP Sessions** | Опитування Terminal Servers через `quser /server:HOSTNAME`. Показує активні та відключені сесії, ім'я користувача, час входу та idle. Diff-оновлення зі збереженням виділеного рядка (`SelectedSession`) між циклами опитування. Перемикається окремим тумблером у Settings (`RdpMonitoringEnabled`) — при вимкненні опитування зупиняється, credentials не запитуються, а UI одразу показує плейсхолдер-заглушку (іконка BellOff) замість застарілих даних. Перехід сесії `Active → Disconnected` (звичайне від'єднання користувача) логується як **Info**, а не Warning — це очікувана, а не проблемна подія. |
| **Zabbix Alerts** | Інтеграція із Zabbix API (JSON-RPC 2.0). Активні проблеми severity High та Disaster. Підтримка API-токена та сесійного логіна. Помилки авторизації (включно з точним кодом і причиною від Zabbix, наприклад `API token expired`) прокидаються і в консольний лог, і в UI-вкладку Logs. Перемикається окремим тумблером у Settings (`ZabbixMonitoringEnabled`) — та сама миттєва заглушка в UI при вимкненні. |
| **Logs** | Агрегований потік подій усіх сервісів у реальному часі з рівнями severity. Записується у rolling-файл. Консольний вивід коректно відображає кирилицю (UTF-8 output encoding). |
| **Remote Management** | Перезавантаження та вимкнення через WMI (`Win32Shutdown` з Force-флагами), RDP (`mstsc.exe`), безперервний ping у новому вікні, SSH через PuTTY або вбудований Windows SSH. |
| **Maintenance Windows** | Планове вікно обслуговування для сервера (з можливістю розширення на групу). На час вікна: Ping-моніторинг та Uptime-трекер не генерують тривог/інцидентів для цього сервера, а UI показує окремий 🔧-бейдж замість тривожного статусу. Реальний ping/статус при цьому не підмінюється — сервер, що впав, і далі показується як Offline, просто без шуму в логах і без SLA-наслідків. |
| **Telegram Bot** | Віддалене керування та сповіщення через Telegram: перегляд статусу серверів, RDP-сесій, Zabbix-проблем, ping "в один клік", approve/deny запитів на доступ через inline-кнопки, розсилка алертів про падіння/відновлення серверів. Rate-limiting та cooldown проти зловживання. |
| **Tray Icon** | Згортання застосунку у системний трей (`CloseToTray` в Settings) замість повного завершення процесу. |

---

## Архітектура

```
┌─────────────────────────────────────────────────────────────┐
│                        WPF UI (Views)                       │
│  MainWindow (+ TrayIcon) · PingDashboard · UptimeView        │
│  RdpSessions · ResourceMonitor · ZabbixAlerts · Logs         │
│  CredentialDialog · ZabbixTokenDialog                       │
└───────────────────────────┬─────────────────────────────────┘
                            │ DataBinding (MVVM)
┌───────────────────────────▼─────────────────────────────────┐
│                    ViewModels (MVVM)                        │
│  MainViewModel · SettingsViewModel · PingDashboardViewModel │
│  UptimeViewModel (IDisposable) · RdpSessionViewModel        │
│  ZabbixViewModel · ResourceMonitorViewModel (IDisposable)   │
│  ServerPollStatusViewModel (HasError) · LogsViewModel       │
│  PingResultViewModel (IRecipient<Maintenance>)              │
└───────────────────────────┬─────────────────────────────────┘
                            │ WeakReferenceMessenger (Pull + Push)
┌───────────────────────────▼─────────────────────────────────┐
│           Background Services (IHostedService)              │
│  PingMonitorService     — ICMP, Main + Recovery loop        │
│  UptimeTrackerService   — фіксація інцидентів, JSON         │
│  RdpMonitorService      — quser polling, monitoring toggle  │
│  ZabbixPollerService    — Zabbix JSON-RPC, monitoring toggle│
│  ResourceMonitorService — CPU/RAM localhost                 │
│  EventLogService        — Event Log localhost               │
│  FileLoggerService      — rolling file sink                 │
│  MaintenanceService     — вікна обслуговування (Pull+Push)  │
│  TelegramBotService     — long polling, команди, approve/deny│
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│          On-demand сервіси (Singleton, не Hosted)           │
│  RemoteResourceService     — WMI CPU/RAM remote              │
│  RemoteEventLogService     — WMI Event Log remote            │
│  RemoteManagementService   — WMI shutdown/restart, SSH, RDP  │
│  EventLogReader (static)   — спільна логіка + IsReachableAsync│
│  TelegramAccessControlService — allow-list, rate-limit, TTL │
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│                    Infrastructure / Core                    │
│  CredentialStore             — Win32 Credential Manager     │
│  CredentialPromptCoordinator — SemaphoreSlim(1,1)           │
│  RdpCredentialValidator      — LogonUser-перевірка          │
│  ZabbixApiClient             — HttpClient / JSON-RPC 2.0    │
│  OverlayDialogService        — модальні overlay без сторонніх│
│  UserSettingsService         — %LocalAppData% JSON (atomic) │
│  TelegramCallbackRegistry    — короткі id для callback_data │
│  TelegramTextChunker         — пагінація довгих повідомлень │
└─────────────────────────────────────────────────────────────┘
```

### Ключові архітектурні рішення

**Generic Host** — управляє lifecycle усіх `BackgroundService`-ів, DI-контейнером та конфігурацією. Забезпечує коректне graceful-завершення при закритті вікна.

**WeakReferenceMessenger** — шина повідомлень між сервісами та ViewModel-ами. Сервіси публікують (`Send`), ViewModel-и підписуються (`RegisterAll`/`Register`). Пряма залежність між шарами відсутня. Підписка відбувається у конструкторі, щоб не пропустити перші повідомлення при старті.

**Dual-loop Ping** — `PingMonitorService` запускає два незалежних Task'и через `Task.WhenAll` + `RunLoopGuardedAsync`: основний цикл (всі сервери кожні N секунд) та recovery loop (тільки Offline сервери кожні M секунд). Падіння одного циклу автоматично зупиняє інший через `LinkedCancellationTokenSource`. Синхронізація між циклами — CAS через `ConcurrentDictionary.TryUpdate` без блокувань. Окремі `perServerLocks` запобігають TOCTOU-гонці між фоновим циклом опитування і позачерговим on-demand `/ping` з Telegram-бота для того самого сервера.

**CAS-патерн замість lock** — у `PingMonitorService` відсутній будь-який `lock` чи `Mutex` для статусів. Потокобезпека досягається через `ConcurrentDictionary<string, PingStatus>.TryUpdate` (Compare-And-Swap): тільки перший потік що виграє гонку оновлює статус і логує перехід. Другий потік отримує `false` і мовчить.

**UptimeTrackerService — двоетапна фіксація інцидентів (Pending → Confirmed)** — падіння сервера одразу **не** створює запис в `_records`. Спочатку інцидент потрапляє у внутрішній `_pendingOffline` (лише пам'ять, без диска й без UI). Тільки якщо сервер лишається Offline довше за `MinIncidentDurationSeconds`, запис "визріває" в офіційний `DowntimeRecord`, зберігається на диск і показується в UI. Це усуває зайве I/O та UI-мерехтіння при короткочасних мережевих "миготіннях", які раніше спершу створювались, а потім видалялись — подвоюючи дискові операції. Окремо: реалізована реконсиляція через `HashSet` `reconciledIps` — при перезапуску застосунку сервер, що відновився поки програма не працювала, коректно закриває відкритий з минулої сесії `DowntimeRecord` замість того, щоб лишити його "висіти" відкритим назавжди. Дані `DowntimeRecord` при збереженні групуються по місяцю фактичного падіння (`FellAt`), а при завантаженні читаються з усіх `uptime-*.json` файлів одразу — інциденти, що перетинають межу місяця, більше не губляться.

**Гібридна модель Pull + Push для Maintenance** — `MaintenanceService` є одночасно `Singleton` (для синхронного `Pull`-запиту `IsUnderMaintenance(ip, group)` з фонових поллерів на кожному циклі) і `IHostedService` (для фонового автозавершення прострочених вікон). Зміни стану (`Started`/`Ended`) додатково `Push`-яться через `MaintenanceChangedMessage`, щоб `UptimeTrackerService` міг закрити вже відкриті інциденти при старті вікна, `PingMonitorService` — перегенерувати алерт, якщо сервер не піднявся вчасно після завершення вікна, а `PingResultViewModel` — миттєво оновити бейдж в UI без затримки на наступний цикл опитування.

**Атомарний запис на диск (write-then-replace)** — `UptimeTrackerService`, `MaintenanceService` і `UserSettingsService` серіалізують у `*.tmp`, потім `File.Move(overwrite: true)` — атомарна операція NTFS. Якщо процес впаде під час запису, основний файл лишається цілим. Файлові I/O-операції додатково захищені окремим `lock`, який серіалізує доступ між UI-потоком (ручні дії користувача) і фоновими циклами автозавершення/збереження.

**On-demand WMI** — `RemoteResourceService` та `RemoteEventLogService` не є `IHostedService`. Вони викликаються напряму з `ResourceMonitorViewModel` коли користувач обирає вузол. Навантаження WMI/DCOM виникає лише для того одного вузла що реально переглядається.

**Спільний `EventLogReader.IsReachableAsync`** — ICMP ping-перевірка перед кожним WMI-запитом до remote машини винесена у спільний статичний метод.

**Thread-safe CredentialStore** — всі поля credentials (`_rdpUsername`, `_rdpPassword`, `_zabbixToken`) та прапорці скасування промптів захищені спільним `_lock`, включно з самими Vault I/O-операціями (`CredWrite`/`CredRead`/`CredDelete`) — вони виконуються **всередині** lock-блоку для атомарної синхронізації між пам'яттю процесу і Windows Credential Manager. Фонові сервіси (`RdpMonitorService`, `ZabbixPollerService`) **не мають права видаляти credentials самостійно** при помилках авторизації — лише призупиняють опитування і чекають на явну дію користувача через Settings, або на `CredentialsChangedMessage`.

**IDisposable на ViewModel-ах** — `ResourceMonitorViewModel`, `UptimeViewModel` та `SettingsViewModel` явно викликаються через `Dispose()` у `App.xaml.cs OnExit` до зупинки хосту. Це зупиняє self-rescheduling WMI-опитування та таймер оновлення тривалості інцидентів.

**Toggle-контрольований моніторинг (Pull, не Push)** — `RdpMonitorService` і `ZabbixPollerService` перевіряють `UserSettingsService.Current.{Rdp,Zabbix}MonitoringEnabled` **щоразу перед** будь-яким зверненням до credentials, а не покладаються лише на подію перемикання. Це гарантує, що вимкнений моніторинг ніколи не спровокує запит логіна/пароля навіть якщо подія перемикання була пропущена. `MonitoringToggledMessage` використовується лише як сигнал "прокинься і перевір" для миттєвого відновлення опитування, а не як джерело істини про стан. Кеш попереднього стану тумблера (`_monitoringWasEnabled`) гарантує, що лог і `MonitoringToggledMessage` шлються лише на **реальній** зміні стану, а не на кожному циклі опитування — інакше кожен цикл поллера писав би у Logs однакове "RDP моніторинг вимкнено", хоча стан не змінювався. З боку UI за вимкнений сервіс відповідає `IsMonitoringDisabled` — властивість ViewModel, яка одразу (без очікування наступного циклу опитування) показує плейсхолдер з іконкою BellOff замість застарілих даних попереднього опитування.

**`DirectBooleanToVisibilityConverter` — навмисно нестандартна назва** — у WPF вже є вбудований `BooleanToVisibilityConverter` у просторі імен `System.Windows.Controls`. Власний конвертер з таким самим ім'ям у `AdminConsole.Converters` спричинив би `CS0104: ambiguous reference` при одночасному `using` обох просторів імен. Назва `Direct...` — свідомий, хоч і нестандартний, спосіб уникнути колізії без псевдонімів (`using X = ...`) у кожному XAML-файлі.

**Явне звільнення TrayIcon** — іконка у системному треї звільняється (`Dispose()`) при будь-якому шляху завершення застосунку через `App.OnExit()`, а не лише при виборі "Вийти" в контекстному меню трею — це запобігає "осиротілим" іконкам, що лишаються видимими до перезапуску `explorer.exe`.

---

## Структура проєкту

```
AdminConsole/
├── App.xaml / App.xaml.cs              # Точка входу, DI, Host lifecycle, DispatcherUnhandledException,
│                                        # UTF-8 Console.OutputEncoding, звільнення TrayIcon при виході
├── appsettings.json                    # Сервери, інтервали, Zabbix URL, Telegram
├── appsettings_example.json            # Шаблон конфігурації без реальних даних інфраструктури
│
├── Configuration/
│   └── AppSettings.cs                  # MonitoringSettings (+ OfflinePingIntervalSeconds,
│                                        # MinIncidentDurationSeconds)
│
├── Core/
│   ├── Models/
│   │   ├── PingResult.cs               # record: Name, IP, Group, Status, LatencyMs, LastChecked
│   │   ├── DowntimeRecord.cs           # sealed class: FellAt, RecoveredAt, Duration,
│   │   │                               # DurationDisplay, ClosedByMaintenance
│   │   ├── MaintenanceWindow.cs        # ServerIp / TargetGroup, From, To, Reason, Key ([JsonIgnore])
│   │   ├── ServerEntry.cs
│   │   ├── ServerDashboardEntry.cs
│   │   ├── ResourceSnapshot.cs
│   │   ├── RdpSessionInfo.cs
│   │   ├── ZabbixProblem.cs
│   │   ├── AppLogEntry.cs              # sealed record, Formatted кешується, Sanitize() (anti log-injection)
│   │   ├── EventLogEntry.cs
│   │   └── TelegramPendingRequest.cs   # ChatId, Username, RequestedAt — запит на доступ до бота
│   └── Messages/
│       ├── PingBatchResultMessage.cs
│       ├── UptimeMessages.cs           # UptimeUpdatedMessage
│       ├── MaintenanceChangedMessage.cs # Started / Ended (Push для Uptime/Ping/UI)
│       ├── AppLogEntryMessage.cs
│       ├── EventLogUpdatedMessage.cs
│       ├── ResourceSnapshotUpdatedMessage.cs
│       ├── RdpSessionsUpdatedMessage.cs
│       ├── ZabbixProblemsUpdatedMessage.cs
│       ├── CredentialsChangedMessage.cs      # Saved/Cleared для Rdp/Zabbix — будить поллери
│       ├── MonitoringToggledMessage.cs       # Сигнал "прокинься і перевір" (не джерело істини)
│       ├── RdpCredentialsClearedMessage.cs
│       ├── TelegramAccessChangedMessage.cs   # Approved/Revoked
│       └── TelegramAccessRequestMessage.cs   # Новий запит доступу від невідомого chat_id
│
├── Services/
│   ├── PingMonitorService.cs           # Dual-loop ICMP, CAS TryUpdate, RunLoopGuardedAsync,
│   │                                    # Maintenance Pull + Ended-Push (скидання previousStatus),
│   │                                    # perServerLocks, PingAllNowAsync (on-demand /ping),
│   │                                    # TryConsumePingCooldown (20с throttle), GetSnapshot()
│   ├── UptimeTrackerService.cs         # Pending→Confirmed інциденти, JSON persistence, atomic write,
│   │                                    # Maintenance Pull + Started-Push (закриття інцидентів),
│   │                                    # реконсиляція (reconciledIps) + cross-month persistence
│   ├── MaintenanceService.cs           # Вікна обслуговування, ConcurrentDictionary, atomic write
│   ├── RdpMonitorService.cs            # quser, Regex з matchTimeout, credentials НЕ видаляються,
│   │                                    # Pull-перевірка toggle перед credential-логікою,
│   │                                    # Active→Disconnected логується як Info, GetSnapshot()
│   ├── ZabbixPollerService.cs          # JSON-RPC 2.0, token auth, детальні причини auth-помилок,
│   │                                    # Pull-перевірка toggle
│   ├── ZabbixApiClient.cs              # HttpClient, TryParse для clock, токен і в Bearer, і в auth-полі
│   ├── ResourceMonitorService.cs       # PerformanceCounter + GlobalMemoryStatusEx
│   ├── EventLogService.cs              # Incremental reader, LastSnapshot (thread-safe lock)
│   ├── EventLogReader.cs               # public static TrimMessage, IsReachableAsync
│   ├── RemoteResourceService.cs        # On-demand WMI CPU/RAM
│   ├── RemoteEventLogService.cs        # On-demand WMI Event Log (Win32_NTLogEvent)
│   ├── FileLoggerService.cs            # ConcurrentQueue (обмежена, max 5000, з drop tracking),
│   │                                    # SemaphoreSlim (Dispose), без TOCTOU-перевірки CurrentCount
│   ├── RemoteManagementService.cs      # WMI Win32Shutdown (Force), SSH, RDP, ping -t,
│   │                                    # IsValidHostOrIp — regex-валідація перед cmd.exe
│   ├── CredentialStore.cs              # advapi32 P/Invoke, lock на всі поля й на Vault I/O, RtlZeroMemory
│   ├── CredentialPromptCoordinator.cs  # SemaphoreSlim(1,1), дедуплікація діалогів
│   ├── RdpCredentialValidator.cs       # LogonUser — перевірка домену/логіна/пароля перед збереженням
│   ├── OverlayDialogService.cs         # Модальні overlay-діалоги підтвердження, лог при виклику до Attach()
│   ├── TelegramBotService.cs           # Long polling, команди, approve/deny, broadcast алертів,
│   │                                    # пагінація (pagedScreens + TelegramTextChunker),
│   │                                    # IsSingleTerminalServer bypass для RDP picker
│   └── TelegramAccessControlService.cs # Allow-list, rate-limit, cooldown 15хв на Deny/Revoke,
│                                        # TTL + межа 50 pending-запитів, PurgeExpiredThrottleState()
│
├── Utils/
│   ├── TelegramCallbackRegistry.cs     # Короткі числові id ↔ значення для callback_data (ліміт 64 байти),
│   │                                    # двобічний ConcurrentDictionary з дедуплікацією
│   └── TelegramTextChunker.cs          # Розбиття довгого тексту на сторінки під ліміт Telegram (4096)
│
├── ViewModels/
│   ├── MainViewModel.cs                # Навігація (6 вкладок), Settings overlay
│   ├── SettingsViewModel.cs            # RDP/Zabbix/Telegram credentials, monitoring toggles,
│   │                                    # Telegram allow-list з username, IDisposable
│   ├── PingDashboardViewModel.cs       # Dictionary-lookup замість FirstOrDefault
│   ├── PingResultViewModel.cs          # ToggleMaintenanceCommand, IsUnderMaintenance-бейдж,
│   │                                    # IRecipient<MaintenanceChangedMessage>
│   ├── UptimeViewModel.cs              # CollectionViewSource, фільтри, Timer, IDisposable
│   ├── RdpSessionViewModel.cs          # Diff-оновлення зі збереженням SelectedSession, HasError,
│   │                                    # IsMonitoringDisabled (плейсхолдер BellOff)
│   ├── RdpSessionRowViewModel.cs       # Рядок сесії для RdpSessionView
│   ├── ZabbixViewModel.cs              # Diff-оновлення колекції, IsMonitoringDisabled
│   ├── ZabbixProblemViewModel.cs       # Рядок проблеми для ZabbixView
│   ├── ResourceMonitorViewModel.cs     # Interlocked.Exchange для CTS, IDisposable
│   ├── ServerPollStatusViewModel.cs    # HasError — агрегований стан помилки опитування вузла
│   ├── LogEntryViewModel.cs            # Рядок логу з кольоровим тегом severity
│   └── LogsViewModel.cs                # CleanupThreshold = MaxEntries + 50
│
├── Views/
│   ├── MainWindow.xaml(.cs)            # Sidebar, Settings overlay, Dialog overlay, TrayIcon
│   ├── UptimeView.xaml(.cs)            # DataGrid, фільтр-бар, Delete кнопка per-row
│   ├── PingDashboardView.xaml(.cs)     # Кнопка Maintenance + бейдж 🔧 у колонці STATUS
│   ├── RdpSessionView.xaml(.cs)
│   ├── ZabbixView.xaml(.cs)
│   ├── ResourceMonitorView.xaml(.cs)
│   ├── LogsView.xaml(.cs)
│   ├── CredentialDialog.xaml(.cs)      # Логін/пароль для RDP
│   └── ZabbixTokenDialog.xaml(.cs)     # Введення API-токена Zabbix
│
├── Converters/
│   ├── DirectBooleanToVisibilityConverter.cs   # Нестандартна назва — уникнення CS0104 з вбудованим
│   │                                            # System.Windows.Controls.BooleanToVisibilityConverter
│   ├── InverseBooleanToVisibilityConverter.cs
│   ├── InverseBoolConverter.cs
│   ├── DoubleToProgressConverter.cs
│   ├── PingStatusToColorConverter.cs
│   ├── PingStatusToTextConverter.cs
│   ├── SeverityToColorConverter.cs
│   ├── StringToColorConverter.cs
│   └── ZabbixSeverityToColorConverter.cs
│
└── Resources/
    └── icon.ico
```

---

## Конфігурація

Файл `appsettings.json` зчитується з `AppContext.BaseDirectory` — коректно незалежно від робочої директорії запуску. `appsettings_example.json` — шаблон без реальних даних інфраструктури, безпечний для передачі/публікації; робочий `appsettings.json` не входить у git (`.gitignore`) і має лишатись лише локально.

### Секція `Monitoring`

```jsonc
"Monitoring": {
  "PingIntervalSeconds": 30,              // Основний цикл ICMP — всі сервери
  "OfflinePingIntervalSeconds": 10,       // Recovery loop — тільки Offline сервери
  "ZabbixUrl": "http://<host>/zabbix/api_jsonrpc.php",
  "ZabbixPollIntervalSeconds": 60,
  "RdpPollIntervalSeconds": 120,
  "LocalResourcePollIntervalSeconds": 3,
  "MinIncidentDurationSeconds": 10        // Поріг фільтрації коротких "миготінь" в Uptime
}
```

> `OfflinePingIntervalSeconds` — мінімальне значення 5с (захист у коді через `Math.Max`). Recovery loop пінгує тільки сервери зі статусом Offline, тому не збільшує навантаження при здоровій інфраструктурі.

> `MinIncidentDurationSeconds` — мінімальна тривалість (у секундах) даунтайму, щоб він потрапив у `DowntimeRecord` і зберігся на диск/у UI. Коротші "миготіння" (наприклад, один втрачений ping-пакет) не фіксуються взагалі. `0` вимикає фільтр (записувати все, як було раніше).

### Секція `Servers`

```jsonc
"Servers": [
  { "Name": "Server1",   "IP": "192.168.244.1",  "Group": "Group1", "Type": "Windows" },
  { "Name": "Server2",   "IP": "192.168.244.2",  "Group": "Group2", "Type": "Linux"   },
  { "Name": "Router",    "IP": "192.168.244.3",  "Group": "Group3", "Type": "Network" }
]
```

| Поле | Опис |
|------|------|
| `Name` | Доменне ім'я (для `quser /server:Name` та відображення) |
| `IP` | IP-адреса (для ICMP, RDP, WMI) |
| `Group` | Група для візуального групування у Ping Dashboard, Uptime та Maintenance Windows |
| `Type` | `Windows` / `Linux` / `Network`. Визначає кнопки керування та видимість у Server Dashboard |

**Важливі правила:**
- `quser` опитує тільки сервери з групою `Terminal Servers` (порівняння `OrdinalIgnoreCase`)
- Server Dashboard показує лише `Type = "Windows"` сервери (WMI/Event Log недоступні на Linux/Network)
- У `Name` має бути **доменне ім'я**, не IP — `quser` через Named Pipes вимагає NetBIOS-резолву; IP замість імені призводить до `RPC server is unavailable`

### Секція `Telegram`


Параметри Telegram access control НЕ виносяться в appsettings.json — це хардкод-константи
в TelegramAccessControlService:
```
| Константа              | Значення | Призначення                                            |
|-------------------------|----------|--------------------------------------------------------|
| PendingRequestTtl        | 24 години | Скільки живе pending-запит на доступ до автоочищення   |
| MaxPendingRequests       | 50        | Верхня межа одночасних pending-запитів (anti-flood)    |
| RequestCooldown          | 15 хвилин | Кулдаун на повторний /start після Deny/Revoke          |
| RateLimitMaxActions      | 10 дій/хв | Загальний rate limit на chat_id                        |
| PingCooldown             | 20 секунд | Окремий, суворіший кулдаун саме для /ping               |
| Claim-код (SettingsViewModel-генерований) | 10 хвилин | TTL одноразового коду прив'язки Primary Admin |
```

Токен бота і API-токен Zabbix у конфігурацію **не входять** — зберігаються виключно через Windows Credential Manager (див. [Безпека та Credentials](#безпека-та-credentials)).

### Користувацькі налаштування

Зберігаються у `%LocalAppData%\AdminConsole\user_settings.json` (атомарний запис через temp+rename), редагуються через Settings overlay (шестерня в sidebar):

| Поле | Опис |
|------|------|
| `CloseToTray` | `true` — згортання у трей при закритті; `false` — завершення процесу |
| `RdpMonitoringEnabled` | `true`/`false` — вмикає/вимикає опитування `quser` та запити RDP-credentials. Змінюється миттєво, без перезапуску, з Pull-перевіркою на кожному циклі поллера |
| `ZabbixMonitoringEnabled` | `true`/`false` — вмикає/вимикає опитування Zabbix API та запити токена. Той самий Pull-механізм |
| `TelegramUsernames` | Словник `chat_id → останній відомий username`, автоматично оновлюється при кожній взаємодії користувача з ботом (наприклад, `/start`) |
| Telegram allow-list | Список підтверджених `chat_id`/username, primary admin, стан claim-коду |

---

## Maintenance Windows (Планове обслуговування)

Дозволяє позначити сервер (з підтримкою розширення на цілу групу) як такий, що свідомо перебуває на плановому обслуговуванні протягом заданого періоду — без хибних тривог у логах і без псування SLA-статистики.

### Принцип роботи

- **Опитування не зупиняється.** Ping/RDP/WMI продовжують працювати як завжди — сервер, що фізично впав, і далі показує реальний статус `Offline`. Зупинка опитування створила б небезпечну "сліпу зону": якщо сервер підніметься раніше запланованого, ви маєте це побачити негайно.
- **Фільтрується лише шум.** Замовчуються Warning/Error-логи (`PingMonitorService`) і не створюються нові `DowntimeRecord` (`UptimeTrackerService`), поки вікно активне.
- **UI показує окремий бейдж 🔧**, а не підміняє статус — адміністратор завжди бачить і реальний стан, і факт того, що це очікувано.

### Життєвий цикл

| Подія | Що відбувається |
|---|---|
| Старт вікна (кнопка 🔧 на рядку сервера) | `MaintenanceService.StartMaintenance` зберігає вікно, шле `MaintenanceChangedMessage(Started)` |
| Сервер падає **до** старту вікна, потім вмикається Maintenance | `UptimeTrackerService` примусово закриває вже відкритий інцидент (`RecoveredAt = Now`, `ClosedByMaintenance = true`) — інакше він продовжив би псувати SLA-статистику весь час дії вікна |
| Сервер падає **під час** активного вікна | Ping/Uptime мовчать (Pull-перевірка `IsUnderMaintenance`); при відновленні короткі падіння додатково фільтруються порогом `MinIncidentDurationSeconds` |
| Вікно закінчується (автоматично, `MaintenanceService` перевіряє кожні 30с) | `MaintenanceChangedMessage(Ended)`; якщо сервер все ще `Offline` — `PingMonitorService` скидає `previousStatus` на `Unknown`, щоб наступний цикл згенерував свіжий алерт (сервер не "завис" непоміченим після закінчення вікна) |
| Дострокове завершення вручну | Повторний клік на 🔧 — `EndMaintenanceEarly` резолвить вікно по фактичному ключу (`IP` або `group:X`), а не завжди по IP |

### Ключові технічні деталі

- **Один ключ — одне активне вікно.** Ключ — `ServerIp`, або `"group:{TargetGroup}"` для групових вікон. Логічне **АБО**: сервер вважається під maintenance, якщо активне індивідуальне АБО групове вікно.
- **`ConcurrentDictionary<string, MaintenanceWindow>`** — сховище читається одночасно з кількох фонових потоків (Pull з поллерів) і пишеться з UI-потоку та фонового циклу автозавершення.
- **Персистентність** — `logs/maintenance.json`, атомарний запис (temp+rename) під окремим `lock`, щоб UI-потік і фоновий цикл автозавершення не намагались одночасно писати той самий файл. `Key` — обчислювана властивість, позначена `[JsonIgnore]`, щоб не потрапляла у сам JSON-файл.
- **MVP-обмеження (свідомо, за межами поточної реалізації):**
  - RDP/Zabbix-поллери поки не інтегровані з Maintenance — під час вікна вони продовжують логувати власні помилки з'єднання як реальні.
  - Тривалість вікна поки фіксована (2 години) через простий діалог підтвердження — повноцінний вибір довільного часу і причини планується окремим `MaintenanceDialog`.
  - Кнопка запуску **групового** вікна в UI ще не реалізована (модель і `MaintenanceService` це вже підтримують).

---

## Безпека та Credentials

### Windows Credential Manager

Паролі **не зберігаються у файлах або реєстрі**. Використовується Win32 Credential Manager (`advapi32.dll`):

| Target | Вміст | Persist |
|--------|-------|---------|
| `AdminConsole/RDP` | Логін + пароль для `quser` | `CRED_PERSIST_ENTERPRISE` |
| `AdminConsole/Zabbix` | API-токен або пароль | `CRED_PERSIST_ENTERPRISE` |
| `AdminConsole/Telegram` | Bot Token | `CRED_PERSIST_ENTERPRISE` |
| `<hostname>` | Тимчасові credentials для конкретного quser-запиту | `CRED_PERSIST_SESSION` |

Тимчасові session-credentials видаляються у `finally` після кожного запиту — незалежно від успіху.

### Захист пам'яті

- `byte[] blob` (UTF-16 пароль) затирається через `Array.Clear()` одразу після `Marshal.Copy`
- Некерований `blobPtr` перезаписується нулями через `RtlZeroMemory` (`kernel32.dll`) у `finally` — без виділення нового `byte[]` в `finally`, що було б небезпечним при `OutOfMemoryException`
- Присвоєння `string = string.Empty` (наприклад, локальній змінній з паролем у ViewModel) **не** є еквівалентом затирання пам'яті — рядки в .NET immutable, і такий підхід свідомо не використовується як security-захід ніде, крім рівня `CredentialStore`, де застосований справжній `byte[]`/unmanaged-підхід вище.

### Thread-safety та правило недоторканності credentials

`CredentialStore` — Singleton. Всі поля credentials та прапорці скасування промптів захищені `private readonly object _lock`, причому самі Vault I/O-виклики (`CredWrite`/`CredRead`/`CredDelete`) виконуються **всередині** цього lock-блоку — атомарна синхронізація між станом у пам'яті процесу і Windows Credential Manager, а не окремі, потенційно розсинхронізовані кроки. Скидання прапорців `UserCancelledXxxPrompt` при очищенні відбувається в тому ж lock-блоці, що й видалення самих credentials. Читання/запис з `RdpMonitorService` (thread pool), `ZabbixPollerService` (thread pool) та UI-потоку — безпечні.

**Критичне архітектурне правило:** фонові сервіси (`RdpMonitorService`, `ZabbixPollerService`) **не мають права викликати `ClearRdp()`/`ClearZabbix()`** при помилках авторизації. При невдалій автентифікації поллер лише призупиняється і чекає на `CredentialsChangedMessage` від UI — видалити credentials може тільки сам користувач через Settings. `ClearRdp()`/`ClearZabbix()` додатково скидають власний прапорець `UserCancelledXxxPrompt`, щоб після очищення поллер знову міг сам запросити нові дані, а не "застрягти", думаючи що користувач раніше відмовився.

### RdpCredentialValidator

Перед збереженням нових RDP-credentials у Settings виконується справжня перевірка домену/логіна/пароля через **Win32 `LogonUser`**, з асинхронною валідацією та індикатором завантаження в UI — користувач одразу дізнається, чи коректні дані, замість того щоб побачити помилку лише в наступному фоновому циклі опитування `quser`.

### WMI-доступ

`RemoteResourceService` та `RemoteEventLogService` використовують `ImpersonationLevel.Impersonate` — поточні Windows-credentials користувача AdminConsole. Окремих WMI-credentials не зберігається. `UnauthorizedAccessException` обробляється — показується `"Access denied"` замість краша.

### Захист від command injection

`RemoteManagementService` перед формуванням аргументів `cmd.exe` (`ping -t`, PuTTY `-ssh`) перевіряє IP/hostname через `IsValidHostOrIp` (regex-валідація) — захист на випадок, якщо джерело адреси колись стане динамічним (наразі це завжди довірений `appsettings.json`).

### Захист від log injection

`AppLogEntry.Sanitize()` прибирає CRLF та керівні символи, обмежує довжину повідомлення 2000 символами — застосовується до будь-якого тексту, що потрапляє в лог ззовні (включно з повідомленнями від Telegram-користувачів), запобігаючи підробці рядків логу через спеціально сформований вхідний текст.

---

## Telegram Bot

Дозволяє довіреним користувачам віддалено переглядати стан інфраструктури та отримувати сповіщення без відкритого доступу до самого AdminConsole. Бот працює як пасивний споживач наявних Push-повідомлень — жодного впливу на основні сервіси моніторингу.

### Можливості бота

| Команда/дія | Опис                                                                                                                                                                                                                                                                                           |
|---|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Перегляд статусу серверів | Список Online/Offline з групуванням, пагінація довгих списків                                                                                                                                                                                                                                  |
| RDP-сесії | Перегляд активних сесій по кожному Terminal Server; якщо налаштований лише один Terminal Server — бот одразу показує сесії, пропускаючи зайвий крок вибору сервера (`IsSingleTerminalServer`)                                                                                                  |
| Zabbix-проблеми | Список активних алертів High/Disaster                                                                                                                                                                                                                                                          |
| `🏓 Пінг` / `/ping` | Реальний паралельний ICMP-опитування всіх серверів на вимогу (`PingAllNowAsync`), без обходу основного throttle; повідомлення-плейсхолдер "🏓 Пінгую…" редагується власним текстом одразу після завершення. Обмежено окремим, суворішим глобальним cooldown (20с) через ресурсоємність команди |
| Broadcast-алерти | Автоматична розсилка при падінні сервера всім підтвердженим користувачам                                                                                                                                                                                                                       |
| Approve/Deny запитів | Підтвердження нового користувача можливе **лише через Telegram inline-кнопку** Primary Admin (`approve:<id>`) — навмисне архітектурне рішення, щоб верифікація завжди відбувалась безпосередньо в месенджері. З UI Settings можна лише **відхилити** (❌) запит як запасний канал              |
| `/claim_admin <код>` | Прив'язка Primary Admin через одноразовий 10-хвилинний код, згенерований у Settings                                                                                                                                                                                                            |
| `/users` | Список дозволених користувачів у форматі `@username (chat_id)` — узгоджено з відображенням у WPF Settings                                                                                                                                                                                      |

### Access control

`TelegramAccessControlService` — allow-list підтверджених `chat_id`, кожен новий незнайомий chat_id створює `TelegramPendingRequest` з TTL. Захист від зловживання:

- **Rate limiting перевіряється першим у пайплайні** — ще до будь-якої бізнес-логіки, щоб спам від неавторизованих chat_id блокувався максимально рано. До 10 дій/хв на chat_id (ковзне вікно).
- **Ping cooldown** — окремий, суворіший глобальний ліміт саме для `/ping` (20с), оскільки команда ресурсоємніша за звичайний перегляд статусу.
- **15-хвилинний cooldown після Deny/Revoke** — запобігає спаму повторними `/start`-запитами одразу після відмови; результат подається в UI через `PendingRequestResult`.
- **Верхня межа pending-запитів** (`MaxPendingRequests = 50`) з 24-годинним TTL і автоматичним видаленням прострочених — захист від флуду фейковими запитами доступу.
- **`PurgeExpiredThrottleState()`** — регулярна чистка rate-limit і cooldown словників за TTL, щоб вони не росли необмежено для кожного нового chat_id, що коли-небудь написав боту.
- Approve можливий **лише через Telegram** (inline-кнопка Primary Admin) — з UI-панелі Settings можна тільки **відхилити** запит, що є свідомим дизайн-рішенням проти випадкового підтвердження невідомого користувача з UI.

### Технічні деталі

- **Long polling** через `TelegramBotService : BackgroundService`, з `RestartPollingAsync` під `_restartLock` — захист від подвійного запуску циклу при швидкому збереженні токена в Settings.
- **`TelegramCallbackRegistry`** — Telegram обмежує `callback_data` 64 байтами, тому довгі значення (наприклад, повний IP чи id запиту) реєструються під коротким числовим ключем у двобічному `ConcurrentDictionary` з дедуплікацією (щоб не росла необмежено) і резолвяться назад при натисканні кнопки.
- **`TelegramTextChunker`** — розбиває довгі списки (сесії, сервери, інциденти, вікна обслуговування) на сторінки з навігаційними кнопками "◀ Назад"/"Далі ▶" через per-chat кеш `pagedScreens`, щоб не впертись у ліміт 4096 символів на повідомлення Telegram.
- **`GetSnapshot()`** у `PingMonitorService`/`RdpMonitorService` — потокобезпечний метод, що гарантує коректні дані для бота одразу при холодному старті, ще до завершення першого циклу опитування.
- **Кешування username** — `RefreshUsername` автоматично оновлює збережений `@username` для вже підтвердженого користувача при кожній його взаємодії з ботом (наприклад, повторний `/start`); для історичних записів без збереженого username передбачений безпечний fallback-плейсхолдер.

---

## Фонові сервіси

### PingMonitorService

Два паралельних Task у `ExecuteAsync`:

```
ExecuteAsync
├── RunMainLoopAsync      — всі сервери, кожні PingIntervalSeconds
│   └── PingServersAsync  — локальний ConcurrentBag, один PingBatchResultMessage
└── RunRecoveryLoopAsync  — тільки Offline сервери, кожні OfflinePingIntervalSeconds
    └── PingServersAsync  — окремий SemaphoreSlim(5), окремий ConcurrentBag
```

**CAS-синхронізація без lock:**
```csharp
var prev = _previousStatus.GetOrAdd(server.IP, PingStatus.Unknown);
if (prev != status)
    if (_previousStatus.TryUpdate(server.IP, status, prev))
        // тільки перший хто виграв гонку — логує перехід
```

**`RunLoopGuardedAsync`** — якщо один цикл падає з критичним винятком, автоматично скасовує другий через `LinkedCancellationTokenSource` і перекидає виняток у `Task.WhenAll`. `OperationCanceledException` у `RunRecoveryLoopAsync` коректно перехоплюється при graceful shutdown, не спливаючи як необроблений виняток.

**SemaphoreSlim з `acquired` прапорцем:**
```csharp
var acquired = false;
try {
    await throttle.WaitAsync(ct);
    acquired = true;
    // ... ping ...
} finally {
    if (acquired) throttle.Release(); // Release тільки якщо WaitAsync успішний
}
```

**Maintenance-інтеграція:** перед відправкою Warning/Error про Offline перевіряється `MaintenanceService.IsUnderMaintenance(ip, group)` (Pull). При `MaintenanceChangedMessage(Ended)` — `previousStatus` для зачеплених серверів скидається на `Unknown`, щоб наступний цикл коректно згенерував алерт, якщо сервер не встиг піднятись до кінця вікна.

**On-demand `/ping` з Telegram:** `PingAllNowAsync` виконує паралельний ICMP-запит по всіх серверах поза звичайним циклом, використовуючи ту саму throttle-логіку. `perServerLocks` (по одному на сервер) запобігають TOCTOU-гонці, коли фоновий цикл і позачерговий `/ping` намагаються опитати той самий сервер одночасно. Додатково — глобальний `TryConsumePingCooldown` (20с) обмежує частоту викликів цієї команди з Telegram.

### UptimeTrackerService

Підписується на `PingBatchResultMessage` та `MaintenanceChangedMessage`. Логіка переходів (двоетапна, Pending → Confirmed):

| Перехід | Дія |
|---------|-----|
| `Online → Offline` (поза maintenance) | Кладеться у внутрішній `_pendingOffline` (лише пам'ять) — **не** в `DowntimeRecord` одразу |
| Сервер лишається `Offline` довше `MinIncidentDurationSeconds` | Промоція з `_pendingOffline` у `DowntimeRecord`, `FellAt` = справжній час падіння; тільки тепер — запис на диск і в UI |
| `Offline → Online` до того як інцидент "визрів" | Видалення з `_pendingOffline` без жодного звернення до диска чи `PublishSnapshot` |
| `Offline → Online` після підтвердження інциденту | Заповнює `RecoveredAt` для відкритого `DowntimeRecord` |
| `Unknown/Checking → Offline` | Не створює запис (стартовий шум) |
| `Unknown/Checking → Online` (перший real-status у сесії, `reconciledIps`) | Реконсиляція: якщо на диску лишився відкритий `DowntimeRecord` з минулої сесії — коректно закриває його, а не лишає висіти назавжди |
| `MaintenanceChangedMessage(Started)` | Примусово закриває відкриті інциденти для зачепленого сервера/групи (`ClosedByMaintenance = true`), прибирає відповідні pending-записи |

**Атомарний запис на диск, з групуванням по місяцю фактичного падіння:**
```
1. Групуємо DowntimeRecord за місяцем FellAt (не поточним місяцем!)
2. Для кожного місяця: серіалізуємо JSON → uptime-YYYY-MM.json.tmp
3. File.Move(.tmp → .json, overwrite: true)   ← атомарна операція NTFS
```

При завантаженні читаються й агрегуються **всі** файли `uptime-*.json` у директорії логів одразу — інциденти, що почались в одному місяці, а тривають в іншому, більше не губляться при перезапуску застосунку. Якщо процес впаде під час запису — `.tmp` пошкоджений, але основний `.json` цілий.

**Debounce збереження:** `ScheduleSave()` відкладає `SaveToDisk()` на 500мс і об'єднує кілька змін підряд (наприклад, кілька серверів впали в одному batch) в один запис на диск. Прапорець `_saveScheduled` скидається **до** старту самого запису — так будь-яка подія, що прийде під час `SaveToDisk`, гарантовано запланує наступний цикл, а не загубиться.

**Три lock-и:**
- `_lock` — захищає `_records`, `_lastStatus` і `_pendingOffline` (швидкі in-memory операції)
- `_saveLock` — захищає I/O (повільні операції з диском), серіалізація JSON відбувається поза локом
- `_messenger.Send(AppLogEntryMessage)` — завжди **поза** `_lock`, щоб уникнути потенційного дедлоку при синхронному виклику підписників

### MaintenanceService

- `Singleton` (Pull API для поллерів) + `IHostedService` (фоновий цикл автозавершення прострочених вікон, кожні 30с)
- `ConcurrentDictionary<string, MaintenanceWindow>` — lock-free читання з фонових потоків
- Push через `MaintenanceChangedMessage(Started/Ended)` — підписники: `UptimeTrackerService`, `PingMonitorService`, `PingResultViewModel`
- Атомарний запис `logs/maintenance.json`, серіалізація JSON поза `lock`, файлові операції під окремим `_saveLock`
- Одне активне вікно на ключ (`ServerIp` або `"group:{TargetGroup}"`), логічне АБО між індивідуальним і груповим вікном

### RdpMonitorService

- Опитує тільки `Group = "Terminal Servers"`
- **Перед будь-яким зверненням до credentials** перевіряє `RdpMonitoringEnabled` (Pull) — вимкнений моніторинг ніколи не спровокує запит логіна/пароля; лог і `MonitoringToggledMessage` шлються лише на реальній зміні стану тумблера
- `quser.exe` з `WaitForExitAsync` і таймаутом 30с
- `Regex` з `matchTimeout: 500ms` — захист від catastrophic backtracking
- Credentials реєструються у Credential Manager перед запитом і видаляються у `finally`
- При помилках авторизації (logon failure, access denied) **credentials не видаляються** — лише генерується подія відключення для кешованих сесій, знімок для сервера обнуляється
- Diff-оновлення станів сесій: перехід `Active → Disconnected` логується як **Info** ("session went idle" — це звичайне від'єднання, а не проблема), `Disconnected → Active` — теж Info ("session resumed")
- Diff-оновлення рядків у `RdpSessionViewModel` зі збереженням `SelectedSession` між циклами опитування (за ключем `ServerIp + SessionId`)
- `GetSnapshot()` — потокобезпечний метод для миттєвого доступу до поточних даних з `TelegramBotService`, коректний навіть до завершення першого циклу опитування

### ZabbixPollerService

- Два режими: API Token (Zabbix 5.4+) або сесійний логін для старіших версій
- **Перед будь-яким зверненням до credentials** перевіряє `ZabbixMonitoringEnabled` (Pull)
- Severity `4` (High) і `5` (Disaster)
- Токен передається одночасно і в заголовку `Authorization: Bearer`, і в полі `auth` тіла JSON-RPC — обхід проблеми з проксі (Apache 6.2+), що стрипає нестандартні заголовки
- Optimistic Concurrency — перед позначенням токена невалідним перевіряється, чи не оновився він у сховищі поки летів запит
- `_consecutiveAuthFailures` — лічильник послідовних невдалих циклів опитування (поле класу, переживає окремі виклики `PollAsync`)
- Причина відхилення токена (код помилки Zabbix, наприклад `code=-32500: API token expired`) прокидається повністю і в `_logger`, і в `AppLogEntryMessage` для UI
- `OperationCanceledException` з `Task.Delay` перехоплюється коректно
- HTTP-таймаут 30с через `IHttpClientFactory`

### EventLogService

- Інкрементальний reader: наступні цикли читають від часу останнього читання
- `LastSnapshot` (до 20 записів) захищений `lock(_snapshotLock)` — читається з UI-потоку, пишеться з thread pool
- Зареєстрований як `AddSingleton<EventLogService>()` + `AddHostedService(sp => sp.GetRequiredService<EventLogService>())` — щоб DI повертав **той самий** екземпляр при запиті конкретного типу

### FileLoggerService

- `SemaphoreSlim _signal` явно `Dispose()`-ується через перевизначення `Dispose()` у `BackgroundService`
- `Release()` викликається без TOCTOU-перевірки `CurrentCount == 0` — семафор без обмеженого maxCount, зайвий виклик безпечний
- Черга обмежена (`max 5000` записів) з відстеженням кількості відкинутих (`drop tracking`) — захист від необмеженого росту пам'яті при екстремальному навантаженні
- Flush-loop: батчі по 50 записів, коалесценс-вікно 200 мс
- Фінальний flush при зупинці хосту

### TelegramBotService

- `BackgroundService` з long polling; `RestartPollingAsync`/`StopPollingAsync` під `_restartLock` захищають від подвійного запуску циклу
- Обробляє команди, callback-запити (approve/deny, пагінація), broadcast алертів
- Rate limiting і cooldown делеговані `TelegramAccessControlService`
- Довгі списки (сесії, сервери, інциденти) розбиваються на сторінки через `TelegramTextChunker` з навігаційними кнопками
- `IsSingleTerminalServer` — пропускає зайвий крок вибору сервера в UX, якщо Terminal Server лише один

---

## Messenger та повідомлення

| Повідомлення | Видавець | Підписники | Коли |
|---|---|---|---|
| `PingBatchResultMessage` | `PingMonitorService` | `PingDashboardViewModel`, `UptimeTrackerService`, `ResourceMonitorViewModel` | Один раз за цикл (batch всіх результатів) |
| `UptimeUpdatedMessage` | `UptimeTrackerService` | `UptimeViewModel`, `TelegramBotService` (broadcast алертів) | При кожній зміні інцидентів |
| `MaintenanceChangedMessage` | `MaintenanceService` | `UptimeTrackerService`, `PingMonitorService`, `PingResultViewModel` | Старт/завершення вікна обслуговування (вручну або автоматично) |
| `ResourceSnapshotUpdatedMessage` | `ResourceMonitorService` | `ResourceMonitorViewModel` | Кожні `LocalResourcePollIntervalSeconds` |
| `EventLogUpdatedMessage` | `EventLogService` | `ResourceMonitorViewModel` | При появі нових записів |
| `RdpSessionsUpdatedMessage` | `RdpMonitorService` | `RdpSessionViewModel`, `TelegramBotService` (on-demand запит) | Кожні `RdpPollIntervalSeconds` |
| `ZabbixProblemsUpdatedMessage` | `ZabbixPollerService` | `ZabbixViewModel`, `TelegramBotService` (on-demand запит) | Кожні `ZabbixPollIntervalSeconds` |
| `AppLogEntryMessage` | Будь-який сервіс / ViewModel | `LogsViewModel`, `FileLoggerService` | За подією |
| `CredentialsChangedMessage` | `SettingsViewModel` | `RdpMonitorService`, `ZabbixPollerService` | Збереження/очищення credentials у Settings — будить фоновий поллер для позачергового опитування |
| `MonitoringToggledMessage` | `SettingsViewModel` | `RdpMonitorService`, `ZabbixPollerService` | Перемикання тумблера в Settings — сигнал "прокинься і перевір", не джерело істини про стан |
| `TelegramAccessChangedMessage` | `TelegramAccessControlService` | `SettingsViewModel` | Approve/Revoke користувача Telegram |
| `TelegramAccessRequestMessage` | `TelegramAccessControlService` | `SettingsViewModel` | Новий pending-запит доступу |

**Чому batch для Ping важливий:** до переходу на `PingBatchResultMessage` кожен сервер публікував окреме повідомлення — при 15 серверах 15 окремих `Dispatcher.InvokeAsync` і перемальовувань за цикл. Тепер один batch → один прохід у підписниках.

**Гібридна модель Maintenance (Pull + Push):** поллери (`PingMonitorService`, `UptimeTrackerService`) синхронно опитують `MaintenanceService.IsUnderMaintenance(...)` перед кожним рішенням про логування/фіксацію інциденту (Pull — без затримки на messenger). Водночас зміни стану вікна публікуються через `MaintenanceChangedMessage` (Push), щоб UI і залежні сервіси реагували миттєво, а не чекали наступного циклу опитування.

**Toggle — той самий Pull + Push принцип:** `RdpMonitoringEnabled`/`ZabbixMonitoringEnabled` перевіряються Pull-ом на кожному циклі поллера (джерело істини — `UserSettingsService.Current`), а `MonitoringToggledMessage` лише пришвидшує реакцію (Push-сигнал пробудження), не замінюючи Pull-перевірку. З боку UI відповідний ViewModel виставляє `IsMonitoringDisabled = true` одразу по кліку на тумблер (не чекаючи на цикл поллера), показуючи плейсхолдер BellOff замість застарілих даних.

**CPU/RAM та Event Log для remote через direct call, не Messenger** — `RemoteResourceService`/`RemoteEventLogService` повертають результат як значення з `await`, а не публікують подію. Навмисна асиметрія: remote-дані потрібні одному конкретному ViewModel в конкретний момент.

---

## Паралельність та потокобезпека

| Компонент | Механізм | Причина |
|-----------|----------|---------|
| `PingMonitorService._previousStatus` | `ConcurrentDictionary` + `TryUpdate` (CAS) | Main і recovery loop пишуть паралельно |
| `PingMonitorService.perServerLocks` | `Dictionary<string, object>` + `lock` на сервер | TOCTOU-захист між фоновим циклом і on-demand `/ping` для того самого сервера |
| `UptimeTrackerService._records` / `_pendingOffline` | `lock(_lock)` | `Receive` (thread pool) + `DeleteRecord` (UI) |
| `UptimeTrackerService` — I/O | `lock(_saveLock)`, debounce 500мс | `Receive` + `DeleteRecord` можуть одночасно викликати `SaveToDisk`; прапорець скидається до старту запису, щоб не загубити паралельні зміни |
| `MaintenanceService._windows` | `ConcurrentDictionary` | Pull-читання з фонових поллерів + запис з UI-потоку та фонового автозавершення |
| `MaintenanceService` — I/O | `lock(_saveLock)` | UI-потік (Start/End вручну) і фоновий цикл автозавершення можуть одночасно писати `maintenance.json` |
| `EventLogService._lastSnapshot` | `lock(_snapshotLock)` | Thread pool пише, UI-потік читає |
| `CredentialStore` — всі поля, прапорці скасування, і сам Vault I/O | `lock(_lock)` | `RdpMonitorService`, `ZabbixPollerService`, UI — усі зміни (включно з `CredWrite`/`CredRead`/`CredDelete`) в одному lock-блоці для атомарної синхронізації пам'ять↔Vault |
| `ResourceMonitorViewModel._remoteResourceCts` | `Interlocked.Exchange` | `Dispose()` і `LoadRemoteResourceAsync` з різних потоків |
| `ResourceMonitorViewModel._disposed` | `volatile bool` | Видима між потоками без lock |
| `PingMonitorService._mainThrottle` | `SemaphoreSlim(10)` | Обмеження паралельних ICMP з основного циклу |
| `PingMonitorService._recoveryThrottle` | `SemaphoreSlim(5)` | Окремо від основного — recovery не блокується |
| `CredentialPromptCoordinator` | `SemaphoreSlim(1,1)` | Один модальний діалог одночасно |
| `PingResultViewModel.ToggleMaintenanceCommand` | `IsActionBusy` flag | Блокує повторний клік поки чекає на діалог підтвердження |
| `TelegramBotService` polling | `_restartLock` | Захист від подвійного запуску long-polling циклу при швидкому збереженні токена |
| `TelegramAccessControlService` rate-limit / cooldown | `ConcurrentDictionary<long, ...>` за `chat_id`, з `PurgeExpiredThrottleState()` | Читання/запис з polling-циклу бота і UI-потоку (Settings); періодична чистка застарілих ключів |
| `TelegramCallbackRegistry` | Двобічний `ConcurrentDictionary` з дедуплікацією | Уникнення необмеженого росту при повторюваних значеннях callback_data |
| `UserSettingsService` Telegram-стан | `MutateTelegramState`/`ReadTelegramState` під `lock` | UI-потік (Settings) і потік Telegram bot читають/пишуть той самий стан паралельно |

---

## Logging

Rolling-файли у `logs/` поруч із `.exe` (`AppContext.BaseDirectory`):

```
logs/
├── app-2026-06-18.log
├── app-2026-06-19.log
├── uptime-2026-06.json
├── uptime-2026-07.json
└── maintenance.json
```

Консольний вивід (`Console.OutputEncoding = Encoding.UTF8`, виставляється при старті `App()`) коректно відображає кирилицю у Debug Output/консолі IDE — раніше нестандартна кодова сторінка Windows-термінала ламала кириличні повідомлення на `�`.

**Формат App Log:**
```
[2026-06-19 10:24:01] [SUCCESS] [PingMonitor]  Server1 (192.168.244.86) is back ONLINE. Latency: 1 ms.
[2026-06-19 10:24:03] [ERROR]   [PingMonitor]  Server3 (192.168.244.144) went OFFLINE.
[2026-06-19 10:24:15] [WARNING] [UptimeTracker] Server3 (192.168.244.144) перейшов у стан OFFLINE.
[2026-06-19 10:31:42] [INFO]    [UptimeTracker] Server3 (192.168.244.144) відновлено. Простій: 7хв 27с.
[2026-06-19 11:02:10] [WARNING] [Zabbix]        Zabbix: токен відхилено (цикл 1/3). Причина: Zabbix відхилив токен (code=-32500): API token expired. Оновіть токен у Settings.
[2026-06-19 12:00:00] [INFO]    [Maintenance]   Maintenance розпочато для Server3: Планове обслуговування (до 14:00).
[2026-06-19 13:15:02] [INFO]    [RdpMonitor]    ivanenko → session went idle on TSVR3 (Active → Disconnected, logon: 19.06.2026 09:00).
```

**Формат Uptime JSON** (`logs/uptime-2026-06.json`):
```json
[
  {
    "ServerName": "Server3",
    "ServerIp": "192.168.244.1",
    "ServerGroup": "DataBase Servers",
    "FellAt": "2026-06-19T10:24:15+03:00",
    "RecoveredAt": "2026-06-19T10:31:42+03:00",
    "ClosedByMaintenance": false
  }
]
```

**Формат Maintenance JSON** (`logs/maintenance.json`):
```json
[
  {
    "ServerIp": "192.168.244.1",
    "TargetGroup": null,
    "DisplayName": "Server3",
    "From": "2026-06-19T12:00:00+03:00",
    "To": "2026-06-19T14:00:00+03:00",
    "Reason": "Планове обслуговування",
    "CreatedAt": "2026-06-19T12:00:00+03:00"
  }
]
```

> `Key` (обчислювана властивість `ServerIp`/`"group:{TargetGroup}"`) у цей файл не потрапляє — позначена `[JsonIgnore]`.

> **App Logs** — журнал подій самого AdminConsole. Не плутати з **Event Log** вузлів у вкладці Resource Monitor (там — системні помилки Windows цільових машин).

---

## Залежності

| Пакет | Версія | Призначення |
|-------|--------|-------------|
| `CommunityToolkit.Mvvm` | 8.3.2 | `ObservableObject`, `RelayCommand`, `WeakReferenceMessenger` |
| `MaterialDesignThemes` | 5.1.0 | Material Design 3 UI компоненти |
| `MaterialDesignColors` | 3.1.0 | Палітра кольорів |
| `Microsoft.Extensions.Hosting` | 8.0.1 | Generic Host, DI, `BackgroundService` |
| `Microsoft.Extensions.Http` | 8.0.1 | `IHttpClientFactory` |
| `Microsoft.Extensions.Configuration.Json` | 8.0.1 | `appsettings.json` |
| `System.Management` | 8.0.0 | WMI — локальний і remote доступ |
| `Telegram.Bot` | — | Long polling, Bot API клієнт |
| `Hardcodet.NotifyIcon.Wpf` | — | Іконка та контекстне меню в системному треї |

**Вбудовані (Windows Desktop SDK):**
- `System.Net.NetworkInformation.Ping` — ICMP
- `System.Diagnostics.PerformanceCounter` — CPU localhost
- `System.Diagnostics.EventLog` — Event Log localhost
- `advapi32.dll` P/Invoke — `CredWrite`, `CredRead`, `CredFree`, `CredDelete`, `LogonUser`
- `kernel32.dll` P/Invoke — `GlobalMemoryStatusEx`, `RtlZeroMemory`

---

## Збірка та запуск

### Debug (Visual Studio 2022+)

Відкрити `AdminConsole.sln` і натиснути `F5`.

### Build

```bash
dotnet build AdminConsole.csproj -c Release
```

### Publish (self-contained)

```bash
dotnet publish AdminConsole.csproj -c Release -r win-x64 --self-contained true -o ./publish
```

### Запуск

```bash
./publish/AdminConsole.exe
```

`appsettings.json` має знаходитись поруч із `.exe`. Використовуйте `appsettings_example.json` як шаблон — не публікуйте і не пересилайте робочий `appsettings.json` разом з кодом, оскільки він містить реальні внутрішні IP-адреси інфраструктури.

---

## Вимоги до середовища

| Вимога | Деталі |
|--------|--------|
| **ОС** | Windows 10 / Windows Server 2016 або новіше |
| **.NET Runtime** | .NET 8 (або self-contained публікація) |
| **Права** | Член домену; локальний адміністратор для WMI-команд (restart/shutdown, remote CPU/RAM/Event Log) |
| **Мережа** | ICMP до всіх серверів; HTTP/HTTPS до Zabbix API та Telegram Bot API; TCP 445 до Terminal Servers; DCOM/WMI (TCP 135 + динамічні порти) до Windows-вузлів |
| **quser.exe** | Вбудований у Windows Server та Windows 10 Pro/Enterprise |
| **Zabbix** | 5.0+ (API token з 5.4+) |
| **Telegram** | Bot Token від @BotFather; вихідний HTTPS-доступ до `api.telegram.org` |
| **PuTTY** (опціонально) | Для SSH через PuTTY; fallback — вбудований `ssh.exe` (OpenSSH) |

### Firewall для RDP-моніторингу (`quser`)

- TCP **445** (SMB / Named Pipes) до Terminal Servers
- `Name` у конфігурації — **доменне ім'я**, не IP

### Firewall для Server Dashboard (WMI/DCOM)

На цільових машинах:
- Служба `Windows Management Instrumentation` запущена
- Правило `Windows Management Instrumentation (DCOM-In)` у `wf.msc` дозволено
- Поточний користувач AdminConsole має права WMI (членство в `Administrators` або явний WMI namespace security)

> Якщо WMI недоступний — показується `"Access denied"` або `"Server is offline"` замість краша. Ping pre-check перед запитом запобігає довгому очікуванню DCOM-таймауту.

---

## Відомі обмеження

Свідомий технічний борг, зафіксований під час внутрішнього аудиту — некритичний за поточного масштабу (внутрішня мережа, ~15 серверів, довірений bot token), але вартий уваги при подальшому розвитку:

- RDP/Zabbix-поллери поки не інтегровані з Maintenance Windows.
- Кнопка запуску **групового** Maintenance-вікна в UI ще не реалізована.
- Аргументи зовнішніх процесів (`ping -t`, PuTTY) валідуються через `IsValidHostOrIp`, але джерело IP наразі завжди статичне (`appsettings.json`) — варто повторно перевірити цю точку, якщо джерело колись стане динамічним.
- Тривалість Maintenance-вікна поки фіксована (2 години) — вибір довільного часу і причини планується окремим діалогом.

---

## Changelog (останні зміни)

### Нові функції
- **Telegram Bot** — віддалений перегляд статусу серверів/RDP-сесій/Zabbix-проблем, `/ping`, broadcast алертів, approve/deny доступу через inline-кнопки, rate-limiting та TTL pending-запитів, `/users` зі збереженими username
- **Tray Icon** — згортання у системний трей (`CloseToTray`) з коректним звільненням іконки при будь-якому шляху завершення застосунку
- **Toggle-и моніторингу** — окремі вимикачі `RdpMonitoringEnabled`/`ZabbixMonitoringEnabled` у Settings з Pull-перевіркою на кожному циклі поллера, миттєвим `IsMonitoringDisabled`-плейсхолдером (BellOff) в UI та анти-спам логуванням лише реальних змін стану
- **RdpCredentialValidator** — перевірка нових RDP-credentials через Win32 `LogonUser` перед збереженням, замість очікування наступного фонового циклу
- **On-demand `/ping`** — `PingAllNowAsync` з окремим глобальним cooldown і `perServerLocks` проти TOCTOU-гонки з фоновим циклом
- **Maintenance Windows** — вікна планового обслуговування для сервера чи групи, з гібридною моделлю Pull+Push, автоматичним закриттям відкритих інцидентів і придушенням шуму в логах на час вікна
- **Uptime & Incidents** — вкладка з повним журналом інцидентів, фільтрацією, збереженням на диск та видаленням записів; cross-month persistence і реконсиляція відкритих інцидентів після перезапуску
- **Recovery Loop** — прискорений пінг (кожні `OfflinePingIntervalSeconds`) тільки для Offline серверів
- **WMI Force shutdown/restart** — `Win32Shutdown` з Force-флагами (5/6) замість звичайних (1/2)
- **Фільтрація коротких "миготінь"** — `MinIncidentDurationSeconds` з двоетапною Pending→Confirmed логікою, без зайвого I/O та UI-мерехтіння

### Архітектурні покращення
- CAS-патерн (`TryUpdate`) замість `_transitionLock + Clear()` у `PingMonitorService`
- `RunLoopGuardedAsync` — взаємне скасування циклів через `LinkedCancellationTokenSource`
- `Interlocked.Exchange` для CTS у `ResourceMonitorViewModel` замість non-atomic replace
- `EventLogReader.IsReachableAsync` — спільний метод замість дублювання у двох сервісах
- `AppLogEntry.Formatted` — кешується при ініціалізації (immutable record); `Sanitize()` — захист від log injection
- `CollectionViewSource` diff-оновлення у `ZabbixViewModel`, `UptimeViewModel` і `RdpSessionViewModel` замість `Clear()` (зі збереженням виділеного рядка)
- `PingDashboardViewModel` — `Dictionary`-lookup замість `FirstOrDefault` для рядків серверів
- `MaintenanceService` — гібридна Pull (Singleton, синхронний `IsUnderMaintenance`) + Push (`IHostedService`, `MaintenanceChangedMessage`) архітектура
- `RdpMonitorService`/`ZabbixPollerService` — той самий Pull+Push принцип застосований до `RdpMonitoringEnabled`/`ZabbixMonitoringEnabled`: `MonitoringToggledMessage` лише пришвидшує реакцію, Pull лишається джерелом істини; credential gating перевіряється ДО будь-якого запиту логіна/токена
- `ServerPollStatusViewModel`/`RdpSessionViewModel` — введено `HasError` для чистого агрегування стану помилки в UI
- `DirectBooleanToVisibilityConverter` — нестандартна назва навмисно, щоб уникнути `CS0104` ambiguous reference з вбудованим `System.Windows.Controls.BooleanToVisibilityConverter`
- `MaintenanceWindow.Key` позначено `[JsonIgnore]` — зайве обчислюване поле більше не потрапляє у `maintenance.json`
- Ping Dashboard — прибрано стовпець "SINCE" (час з моменту зміни статусу) з UI (`PingDashboardView.xaml`) і з `PingResultViewModel` (властивість `StatusSince`, логіка розрахунку в `ApplyResult`) як визнаний зайвим; ширина колонок `GROUP`/`ACTIONS` перебалансована після видалення

### Виправлені баги
- `RdpMonitorService` — перехід `Active → Disconnected` (звичайне від'єднання користувача) тепер логується як **Info**, а не Warning — це очікувана подія, а не проблема
- `MainWindow`/`App.OnExit` — іконка в системному треї тепер явно звільняється (`DisposeTrayIcon()`) при будь-якому шляху завершення застосунку, а не лише через пункт меню "Вийти" — усуває "осиротілі" іконки в треї
- `TelegramBotService` — реалізовано пагінацію для прямого виводу RDP-сесій (`pagedScreens`) — раніше великий список сесій при єдиному Terminal Server обрізався без можливості перегорнути сторінку
- `TelegramAccessControlService` — `PurgeExpiredThrottleState()` регулярно чистить rate-limit/cooldown словники, запобігаючи необмеженому росту пам'яті
- `CredentialStore` — Vault I/O (`CredWrite`/`CredRead`/`CredDelete`) перенесено всередину `lock`-блоку для атомарної синхронізації з пам'яттю процесу; скидання прапорця `UserCancelledXxxPrompt` перенесено в той самий `lock`, що й видалення credentials
- `OverlayDialogService` — тепер логує попередження, якщо `ShowConfirmationAsync` викликано до `Attach()`, замість мовчазного повернення `false`
- `SettingsViewModel.SaveRdpCredentialsAsync` — прибрано оманливе "затирання" пароля через `passwordToSave = string.Empty` (рядки immutable, реального security-ефекту не було)
- `RemoteManagementService` — додано `IsValidHostOrIp` regex-валідацію перед підстановкою IP/hostname в аргументи `cmd.exe`
- `PingMonitorService` — необроблений `OperationCanceledException` у `RunRecoveryLoopAsync` під час graceful shutdown тепер коректно перехоплюється
- `UptimeTrackerService` — виправлено дві помилки: реконсиляція відкритих інцидентів при відновленні сервера, поки застосунок не працював (раніше запис лишався відкритим назавжди); дані тепер групуються і читаються по місяцю фактичного падіння, а не лише поточного місяця — інциденти на межі місяців більше не губляться
- `TelegramBotService`/DI — виправлено дублювання реєстрації `RdpMonitorService` в контейнері, через яке Telegram і WPF бачили різні екземпляри та різні (застарілі) дані
- `ZabbixPollerService` — лічильник послідовних auth-невдач перенесено в поле класу (раніше мертвий код через локальну змінну й зайвий `return`)
- `RdpMonitorService` — прибрано виклик `ClearRdp()` при logon failure/access denied; фонові сервіси більше не мають права видаляти credentials
- `MaintenanceService.SaveToDisk` — усунено race condition між UI-потоком і фоновим циклом автозавершення (`lock` навколо файлових операцій, серіалізація JSON поза локом)
- `MaintenanceService.EndMaintenanceEarly` — тепер резолвиться по фактичному ключу вікна (`IP` або `group:X`), а не завжди по `IP`
- `UptimeTrackerService.ScheduleSave` — прапорець debounce скидається **до** старту `SaveToDisk`, а не після — інакше зміни, що приходять під час запису на диск, губились
- `PingResultViewModel.ToggleMaintenance` — заблоковано подвійний клік через `IsActionBusy` на час показу діалогу підтвердження
- Кодування консольного виводу — `Console.OutputEncoding = Encoding.UTF8` виправляє нечитабельну кирилицю в консолі/Debug Output IDE
- `ZabbixPollerService` — точна причина відхилення токена (код помилки Zabbix, текст) тепер прокидається в UI-логи, а не губиться в консольному логері; токен передається і в Bearer, і в JSON-RPC `auth` — обхід стрипання заголовків проксі
- `SemaphoreSlim` (`_pingThrottle`, `_recoveryThrottle`, `_signal`) тепер явно `Dispose()`-ується
- `FileLoggerService.Receive` — прибрано оманливий TOCTOU-патерн навколо перевірки `_signal.CurrentCount`; черга обмежена (max 5000) з відстеженням відкинутих записів
- `EventLogService._lastSnapshot` захищений `lock` від race між thread pool і UI-потоком
- `RdpMonitorService` — `Regex` з `matchTimeout: 500ms`
- `ZabbixApiClient` — `long.TryParse` замість `long.Parse` для `clock`
- `UserSettingsService.Save()` — атомарний запис (temp+rename) замість прямого `File.WriteAllText`; окремі методи `MutateTelegramState`/`ReadTelegramState` для потокобезпечного доступу до Telegram-стану
- `RemoteManagementService`, `RemoteResourceService`, `RemoteEventLogService` — усі `ManagementObjectSearcher`/`ManagementObjectCollection`/`ManagementObject` підтверджено в `using`
- Стартовий спам `[SUCCESS] is back ONLINE` прибраний — тихий перехід `Unknown/Checking → Online`
- `UptimeTrackerService` реєстрація у конструкторі — не пропускає перший `PingBatchResultMessage`
</file_text>
