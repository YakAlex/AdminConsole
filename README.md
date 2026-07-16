# AdminConsole v2

> Десктопний WPF-інструмент для моніторингу та адміністрування серверної інфраструктури домену.
> Написаний на **C# / .NET 8**, архітектура — **MVVM + Microsoft Generic Host**.

---

## Зміст

- [Можливості](#можливості)
- [Архітектура](#архітектура)
- [Структура проєкту](#структура-проєкту)
- [Конфігурація](#конфігурація)
- [Maintenance Windows (Планове обслуговування)](#maintenance-windows-планове-обслуговування)
- [Безпека та Credentials](#безпека-та-credentials)
- [Фонові сервіси](#фонові-сервіси)
- [Messenger та повідомлення](#messenger-та-повідомлення)
- [Паралельність та потокобезпека](#паралельність-та-потокобезпека)
- [Logging](#logging)
- [Залежності](#залежності)
- [Збірка та запуск](#збірка-та-запуск)
- [Вимоги до середовища](#вимоги-до-середовища)
- [Changelog](#changelog-останні-зміни)

---

## Можливості

| Вкладка | Опис |
|---------|------|
| **Ping Dashboard** | Безперервний ICMP-моніторинг усіх серверів з групуванням, індикацією затримки та кольоровим статусом. Логує лише реальні зміни стану. Паралелізм обмежений `SemaphoreSlim`. Результати всього циклу публікуються одним batch-повідомленням. Пошук рядка сервера — через `Dictionary<string, PingResultViewModel>` (O(1)) замість лінійного пошуку. Кожен рядок має кнопку **Maintenance Mode** (🔧). |
| **Uptime & Incidents** | Автоматичне фіксування інцидентів недоступності: час падіння, відновлення, тривалість простою. Повна фільтрація по сервері, групі, даті та статусу. Зберігається на диск у `logs/uptime-YYYY-MM.json` з атомарним записом. Короткі мережеві "миготіння" (нижче порогу `MinIncidentDurationSeconds`) не фіксуються взагалі — ані на диску, ані в UI. Інциденти, перервані через Maintenance Mode, позначаються окремо (`ClosedByMaintenance`). |
| **Resource Monitor** | CPU та RAM у реальному часі — для `localhost` через `PerformanceCounter` і `GlobalMemoryStatusEx`, для віддалених машин через WMI. Відображає останні помилки Event Log обраного вузла. |
| **RDP Sessions** | Опитування Terminal Servers через `quser /server:HOSTNAME`. Показує активні та відключені сесії, ім'я користувача, час входу та idle. Diff-оновлення зі збереженням виділеного рядка (`SelectedSession`) між циклами опитування. |
| **Zabbix Alerts** | Інтеграція із Zabbix API (JSON-RPC 2.0). Активні проблеми severity High та Disaster. Підтримка API-токена та сесійного логіна. Помилки авторизації (включно з точним кодом і причиною від Zabbix, наприклад `API token expired`) прокидаються і в консольний лог, і в UI-вкладку Logs. |
| **Logs** | Агрегований потік подій усіх сервісів у реальному часі з рівнями severity. Записується у rolling-файл. Консольний вивід коректно відображає кирилицю (UTF-8 output encoding). |
| **Remote Management** | Перезавантаження та вимкнення через WMI (`Win32Shutdown` з Force-флагами), RDP (`mstsc.exe`), безперервний ping у новому вікні, SSH через PuTTY або вбудований Windows SSH. |
| **Maintenance Windows** | Планове вікно обслуговування для сервера (з можливістю розширення на групу). На час вікна: Ping-моніторинг та Uptime-трекер не генерують тривог/інцидентів для цього сервера, а UI показує окремий 🔧-бейдж замість тривожного статусу. Реальний ping/статус при цьому не підмінюється — сервер, що впав, і далі показується як Offline, просто без шуму в логах і без SLA-наслідків. |

---

## Архітектура

```
┌─────────────────────────────────────────────────────────────┐
│                        WPF UI (Views)                       │
│  MainWindow · PingDashboard · UptimeView · RdpSessions      │
│  ResourceMonitor · ZabbixAlerts · Logs                      │
└───────────────────────────┬─────────────────────────────────┘
                            │ DataBinding (MVVM)
┌───────────────────────────▼─────────────────────────────────┐
│                    ViewModels (MVVM)                        │
│  MainViewModel · PingDashboardViewModel                     │
│  UptimeViewModel (IDisposable) · RdpSessionViewModel        │
│  ZabbixViewModel · ResourceMonitorViewModel (IDisposable)   │
│  LogsViewModel · PingResultViewModel (IRecipient<Maintenance>)│
└───────────────────────────┬─────────────────────────────────┘
                            │ WeakReferenceMessenger (Pull + Push)
┌───────────────────────────▼─────────────────────────────────┐
│           Background Services (IHostedService)              │
│  PingMonitorService    — ICMP, Main + Recovery loop         │
│  UptimeTrackerService  — фіксація інцидентів, JSON          │
│  RdpMonitorService     — quser polling                      │
│  ZabbixPollerService   — Zabbix JSON-RPC                    │
│  ResourceMonitorService — CPU/RAM localhost                  │
│  EventLogService       — Event Log localhost                 │
│  FileLoggerService     — rolling file sink                  │
│  MaintenanceService    — вікна обслуговування (Pull+Push)   │
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│          On-demand сервіси (Singleton, не Hosted)           │
│  RemoteResourceService   — WMI CPU/RAM remote               │
│  RemoteEventLogService   — WMI Event Log remote             │
│  RemoteManagementService — WMI shutdown/restart, SSH, RDP   │
│  EventLogReader (static) — спільна логіка + IsReachableAsync│
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│                    Infrastructure / Core                    │
│  CredentialStore         — Win32 Credential Manager + lock  │
│  CredentialPromptCoordinator — SemaphoreSlim(1,1)           │
│  ZabbixApiClient         — HttpClient / JSON-RPC 2.0        │
│  OverlayDialogService    — модальні overlay без сторонніх   │
│  UserSettingsService     — %LocalAppData% JSON (atomic)     │
└─────────────────────────────────────────────────────────────┘
```

### Ключові архітектурні рішення

**Generic Host** — управляє lifecycle усіх `BackgroundService`-ів, DI-контейнером та конфігурацією. Забезпечує коректне graceful-завершення при закритті вікна.

**WeakReferenceMessenger** — шина повідомлень між сервісами та ViewModel-ами. Сервіси публікують (`Send`), ViewModel-и підписуються (`RegisterAll`/`Register`). Пряма залежність між шарами відсутня. Підписка відбувається у конструкторі, щоб не пропустити перші повідомлення при старті.

**Dual-loop Ping** — `PingMonitorService` запускає два незалежних Task'и через `Task.WhenAll` + `RunLoopGuardedAsync`: основний цикл (всі сервери кожні N секунд) та recovery loop (тільки Offline сервери кожні M секунд). Падіння одного циклу автоматично зупиняє інший через `LinkedCancellationTokenSource`. Синхронізація між циклами — CAS через `ConcurrentDictionary.TryUpdate` без блокувань.

**CAS-патерн замість lock** — у `PingMonitorService` відсутній будь-який `lock` чи `Mutex` для статусів. Потокобезпека досягається через `ConcurrentDictionary<string, PingStatus>.TryUpdate` (Compare-And-Swap): тільки перший потік що виграє гонку оновлює статус і логує перехід. Другий потік отримує `false` і мовчить.

**UptimeTrackerService — двоетапна фіксація інцидентів (Pending → Confirmed)** — падіння сервера одразу **не** створює запис в `_records`. Спочатку інцидент потрапляє у внутрішній `_pendingOffline` (лише пам'ять, без диска й без UI). Тільки якщо сервер лишається Offline довше за `MinIncidentDurationSeconds`, запис "визріває" в офіційний `DowntimeRecord`, зберігається на диск і показується в UI. Це усуває зайве I/O та UI-мерехтіння при короткочасних мережевих "миготіннях", які раніше спершу створювались, а потім видалялись — подвоюючи дискові операції.

**Гібридна модель Pull + Push для Maintenance** — `MaintenanceService` є одночасно `Singleton` (для синхронного `Pull`-запиту `IsUnderMaintenance(ip, group)` з фонових поллерів на кожному циклі) і `IHostedService` (для фонового автозавершення прострочених вікон). Зміни стану (`Started`/`Ended`) додатково `Push`-яться через `MaintenanceChangedMessage`, щоб `UptimeTrackerService` міг закрити вже відкриті інциденти при старті вікна, `PingMonitorService` — перегенерувати алерт, якщо сервер не піднявся вчасно після завершення вікна, а `PingResultViewModel` — миттєво оновити бейдж в UI без затримки на наступний цикл опитування.

**Атомарний запис на диск (write-then-replace)** — `UptimeTrackerService`, `MaintenanceService` і `UserSettingsService` серіалізують у `*.tmp`, потім `File.Move(overwrite: true)` — атомарна операція NTFS. Якщо процес впаде під час запису, основний файл лишається цілим. Файлові I/O-операції додатково захищені окремим `lock`, який серіалізує доступ між UI-потоком (ручні дії користувача) і фоновими циклами автозавершення/збереження.

**On-demand WMI** — `RemoteResourceService` та `RemoteEventLogService` не є `IHostedService`. Вони викликаються напряму з `ResourceMonitorViewModel` коли користувач обирає вузол. Навантаження WMI/DCOM виникає лише для того одного вузла що реально переглядається.

**Спільний `EventLogReader.IsReachableAsync`** — ICMP ping-перевірка перед кожним WMI-запитом до remote машини винесена у спільний статичний метод.

**Thread-safe CredentialStore** — всі поля credentials (`_rdpUsername`, `_rdpPassword`, `_zabbixToken`) захищені спільним `_lock`. Фонові сервіси (`RdpMonitorService`, `ZabbixPollerService`) **не мають права видаляти credentials самостійно** при помилках авторизації — лише призупиняють опитування і чекають на явну дію користувача через Settings, або на `CredentialsChangedMessage`.

**IDisposable на ViewModel-ах** — `ResourceMonitorViewModel` та `UptimeViewModel` явно викликаються через `Dispose()` у `App.xaml.cs OnExit` до зупинки хосту. Це зупиняє self-rescheduling WMI-опитування та таймер оновлення тривалості інцидентів.

---

## Структура проєкту

```
AdminConsole/
├── App.xaml / App.xaml.cs              # Точка входу, DI, Host lifecycle, DispatcherUnhandledException,
│                                        # UTF-8 Console.OutputEncoding
├── appsettings.json                    # Сервери, інтервали, Zabbix URL
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
│   │   ├── MaintenanceWindow.cs        # ServerIp / TargetGroup, From, To, Reason, Key
│   │   ├── ServerEntry.cs
│   │   ├── ServerDashboardEntry.cs
│   │   ├── ResourceSnapshot.cs
│   │   ├── RdpSessionInfo.cs
│   │   ├── ZabbixProblem.cs
│   │   ├── AppLogEntry.cs              # sealed record, Formatted кешується при ініціалізації
│   │   └── EventLogEntry.cs
│   └── Messages/
│       ├── PingBatchResultMessage.cs
│       ├── UptimeMessages.cs           # UptimeUpdatedMessage
│       ├── MaintenanceChangedMessage.cs # Started / Ended (Push для Uptime/Ping/UI)
│       ├── AppLogEntryMessage.cs
│       ├── EventLogUpdatedMessage.cs
│       ├── ResourceSnapshotUpdatedMessage.cs
│       ├── RdpSessionsUpdatedMessage.cs
│       └── ZabbixProblemsUpdatedMessage.cs
│
├── Services/
│   ├── PingMonitorService.cs           # Dual-loop ICMP, CAS TryUpdate, RunLoopGuardedAsync,
│   │                                    # Maintenance Pull + Ended-Push (скидання previousStatus)
│   ├── UptimeTrackerService.cs         # Pending→Confirmed інциденти, JSON persistence, atomic write,
│   │                                    # Maintenance Pull + Started-Push (закриття інцидентів)
│   ├── MaintenanceService.cs           # Вікна обслуговування, ConcurrentDictionary, atomic write
│   ├── RdpMonitorService.cs            # quser, Regex з matchTimeout, credentials НЕ видаляються
│   ├── ZabbixPollerService.cs          # JSON-RPC 2.0, token auth, детальні причини auth-помилок
│   ├── ZabbixApiClient.cs              # HttpClient, TryParse для clock
│   ├── ResourceMonitorService.cs       # PerformanceCounter + GlobalMemoryStatusEx
│   ├── EventLogService.cs              # Incremental reader, LastSnapshot (thread-safe lock)
│   ├── EventLogReader.cs               # public static TrimMessage, IsReachableAsync
│   ├── RemoteResourceService.cs        # On-demand WMI CPU/RAM
│   ├── RemoteEventLogService.cs        # On-demand WMI Event Log (Win32_NTLogEvent)
│   ├── FileLoggerService.cs            # ConcurrentQueue, SemaphoreSlim (Dispose)
│   ├── RemoteManagementService.cs      # WMI Win32Shutdown (Force), SSH, RDP
│   ├── CredentialStore.cs              # advapi32 P/Invoke, lock на всі поля, RtlZeroMemory
│   ├── CredentialPromptCoordinator.cs  # SemaphoreSlim(1,1), дедуплікація діалогів
│   └── OverlayDialogService.cs
│
├── ViewModels/
│   ├── MainViewModel.cs                # Навігація (6 вкладок), Settings overlay
│   ├── PingDashboardViewModel.cs       # Dictionary-lookup замість FirstOrDefault
│   ├── PingResultViewModel.cs          # ToggleMaintenanceCommand, IsUnderMaintenance-бейдж,
│   │                                    # IRecipient<MaintenanceChangedMessage>
│   ├── UptimeViewModel.cs              # CollectionViewSource, фільтри, Timer, IDisposable
│   ├── RdpSessionViewModel.cs          # Diff-оновлення зі збереженням SelectedSession
│   ├── ZabbixViewModel.cs              # Diff-оновлення колекції
│   ├── ResourceMonitorViewModel.cs     # Interlocked.Exchange для CTS, IDisposable
│   └── LogsViewModel.cs                # CleanupThreshold = MaxEntries + 50
│
├── Views/
│   ├── MainWindow.xaml(.cs)
│   ├── UptimeView.xaml(.cs)            # DataGrid, фільтр-бар, Delete кнопка per-row
│   ├── PingDashboardView.xaml(.cs)     # Кнопка Maintenance + бейдж 🔧 у колонці STATUS
│   ├── RdpSessionView.xaml(.cs)
│   ├── ZabbixView.xaml(.cs)
│   ├── ResourceMonitorView.xaml(.cs)
│   └── LogsView.xaml(.cs)
│
└── Resources/
    └── icon.ico
```

---

## Конфігурація

Файл `appsettings.json` зчитується з `AppContext.BaseDirectory` — коректно незалежно від робочої директорії запуску.

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
- У `Name` має бути **доменне ім'я**, не IP — `quser` через Named Pipes вимагає NetBIOS-резолву

### Користувацькі налаштування

Зберігаються у `%LocalAppData%\AdminConsole\user_settings.json` (атомарний запис через temp+rename), редагуються через Settings overlay (шестерня в sidebar):

| Поле | Опис |
|------|------|
| `CloseToTray` | `true` — згортання у трей при закритті; `false` — завершення процесу |

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
- **Персистентність** — `logs/maintenance.json`, атомарний запис (temp+rename) під окремим `lock`, щоб UI-потік і фоновий цикл автозавершення не намагались одночасно писати той самий файл.
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
| `<hostname>` | Тимчасові credentials для конкретного quser-запиту | `CRED_PERSIST_SESSION` |

Тимчасові session-credentials видаляються у `finally` після кожного запиту — незалежно від успіху.

### Захист пам'яті

- `byte[] blob` (UTF-16 пароль) затирається через `Array.Clear()` одразу після `Marshal.Copy`
- Некерований `blobPtr` перезаписується нулями через `RtlZeroMemory` (`kernel32.dll`) у `finally` — без виділення нового `byte[]` в `finally`, що було б небезпечним при `OutOfMemoryException`

### Thread-safety та правило недоторканності credentials

`CredentialStore` — Singleton. Всі поля credentials захищені `private readonly object _lock`. Читання/запис з `RdpMonitorService` (thread pool), `ZabbixPollerService` (thread pool) та UI-потоку — безпечні.

**Критичне архітектурне правило:** фонові сервіси (`RdpMonitorService`, `ZabbixPollerService`) **не мають права викликати `ClearRdp()`/`ClearZabbix()`** при помилках авторизації. При невдалій автентифікації поллер лише призупиняється і чекає на `CredentialsChangedMessage` від UI — видалити credentials може тільки сам користувач через Settings. `ClearRdp()`/`ClearZabbix()` додатково скидають власний прапорець `UserCancelledXxxPrompt`, щоб після очищення поллер знову міг сам запросити нові дані, а не "застрягти", думаючи що користувач раніше відмовився.

### WMI-доступ

`RemoteResourceService` та `RemoteEventLogService` використовують `ImpersonationLevel.Impersonate` — поточні Windows-credentials користувача AdminConsole. Окремих WMI-credentials не зберігається. `UnauthorizedAccessException` обробляється — показується `"Access denied"` замість краша.

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

**`RunLoopGuardedAsync`** — якщо один цикл падає з критичним винятком, автоматично скасовує другий через `LinkedCancellationTokenSource` і перекидає виняток у `Task.WhenAll`.

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

### UptimeTrackerService

Підписується на `PingBatchResultMessage` та `MaintenanceChangedMessage`. Логіка переходів (двоетапна, Pending → Confirmed):

| Перехід | Дія |
|---------|-----|
| `Online → Offline` (поза maintenance) | Кладеться у внутрішній `_pendingOffline` (лише пам'ять) — **не** в `DowntimeRecord` одразу |
| Сервер лишається `Offline` довше `MinIncidentDurationSeconds` | Промоція з `_pendingOffline` у `DowntimeRecord`, `FellAt` = справжній час падіння; тільки тепер — запис на диск і в UI |
| `Offline → Online` до того як інцидент "визрів" | Видалення з `_pendingOffline` без жодного звернення до диска чи `PublishSnapshot` |
| `Offline → Online` після підтвердження інциденту | Заповнює `RecoveredAt` для відкритого `DowntimeRecord` |
| `Unknown/Checking → Offline` | Не створює запис (стартовий шум) |
| `Unknown/Checking → Online` | Тихо, без логу |
| `MaintenanceChangedMessage(Started)` | Примусово закриває відкриті інциденти для зачепленого сервера/групи (`ClosedByMaintenance = true`), прибирає відповідні pending-записи |

**Атомарний запис на диск:**
```
1. Серіалізуємо JSON → uptime-YYYY-MM.json.tmp
2. File.Move(.tmp → .json, overwrite: true)   ← атомарна операція NTFS
```

Якщо процес впаде під час запису — `.tmp` пошкоджений, але основний `.json` цілий.

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
- `quser.exe` з `WaitForExitAsync` і таймаутом 30с
- `Regex` з `matchTimeout: 500ms` — захист від catastrophic backtracking
- Credentials реєструються у Credential Manager перед запитом і видаляються у `finally`
- При помилках авторизації (logon failure, access denied) **credentials не видаляються** — лише генерується подія відключення для кешованих сесій, знімок для сервера обнуляється
- Diff-оновлення рядків у `RdpSessionViewModel` зі збереженням `SelectedSession` між циклами опитування (за ключем `ServerIp + SessionId`)

### ZabbixPollerService

- Два режими: API Token (Zabbix 5.4+) або сесійний логін для старіших версій
- Severity `4` (High) і `5` (Disaster)
- Патерн "ремінь і підтяжки" для Apache-проксі: токен передається і в заголовку `Authorization: Bearer`, і в полі `auth` тіла JSON-RPC
- Optimistic Concurrency — перед позначенням токена невалідним перевіряється, чи не оновився він у сховищі поки летів запит
- `_consecutiveAuthFailures` — лічильник послідовних невдалих циклів опитування (поле класу, переживає окремі виклики `PollAsync`, на відміну від попередньої реалізації з локальною змінною)
- Причина відхилення токена (код помилки Zabbix, наприклад `code=-32500: API token expired`) прокидається повністю і в `_logger`, і в `AppLogEntryMessage` для UI — раніше в UI йшов лише узагальнений текст
- `OperationCanceledException` з `Task.Delay` перехоплюється коректно
- HTTP-таймаут 30с через `IHttpClientFactory`

### EventLogService

- Інкрементальний reader: наступні цикли читають від часу останнього читання
- `LastSnapshot` (до 20 записів) захищений `lock(_snapshotLock)` — читається з UI-потоку, пишеться з thread pool
- Зареєстрований як `AddSingleton<EventLogService>()` + `AddHostedService(sp => sp.GetRequiredService<EventLogService>())` — щоб DI повертав **той самий** екземпляр при запиті конкретного типу

### FileLoggerService

- `SemaphoreSlim _signal` явно `Dispose()`-ується через перевизначення `Dispose()` у `BackgroundService`
- `Release()` викликається без перевірки `CurrentCount == 0` — семафор без обмеженого maxCount, зайвий виклик безпечний і не потребує TOCTOU-перевірки
- Flush-loop: батчі по 50 записів, коалесценс-вікно 200 мс
- Фінальний flush при зупинці хосту

---

## Messenger та повідомлення

| Повідомлення | Видавець | Підписники | Коли |
|---|---|---|---|
| `PingBatchResultMessage` | `PingMonitorService` | `PingDashboardViewModel`, `UptimeTrackerService`, `ResourceMonitorViewModel` | Один раз за цикл (batch всіх результатів) |
| `UptimeUpdatedMessage` | `UptimeTrackerService` | `UptimeViewModel` | При кожній зміні інцидентів |
| `MaintenanceChangedMessage` | `MaintenanceService` | `UptimeTrackerService`, `PingMonitorService`, `PingResultViewModel` | Старт/завершення вікна обслуговування (вручну або автоматично) |
| `ResourceSnapshotUpdatedMessage` | `ResourceMonitorService` | `ResourceMonitorViewModel` | Кожні `LocalResourcePollIntervalSeconds` |
| `EventLogUpdatedMessage` | `EventLogService` | `ResourceMonitorViewModel` | При появі нових записів |
| `RdpSessionsUpdatedMessage` | `RdpMonitorService` | `RdpSessionViewModel` | Кожні `RdpPollIntervalSeconds` |
| `ZabbixProblemsUpdatedMessage` | `ZabbixPollerService` | `ZabbixViewModel` | Кожні `ZabbixPollIntervalSeconds` |
| `AppLogEntryMessage` | Будь-який сервіс / ViewModel | `LogsViewModel`, `FileLoggerService` | За подією |

**Чому batch для Ping важливий:** до переходу на `PingBatchResultMessage` кожен сервер публікував окреме повідомлення — при 15 серверах 15 окремих `Dispatcher.InvokeAsync` і перемальовувань за цикл. Тепер один batch → один прохід у підписниках.

**Гібридна модель Maintenance (Pull + Push):** поллери (`PingMonitorService`, `UptimeTrackerService`) синхронно опитують `MaintenanceService.IsUnderMaintenance(...)` перед кожним рішенням про логування/фіксацію інциденту (Pull — без затримки на messenger). Водночас зміни стану вікна публікуються через `MaintenanceChangedMessage` (Push), щоб UI і залежні сервіси реагували миттєво, а не чекали наступного циклу опитування.

**CPU/RAM та Event Log для remote через direct call, не Messenger** — `RemoteResourceService`/`RemoteEventLogService` повертають результат як значення з `await`, а не публікують подію. Навмисна асиметрія: remote-дані потрібні одному конкретному ViewModel в конкретний момент.

---

## Паралельність та потокобезпека

| Компонент | Механізм | Причина |
|-----------|----------|---------|
| `PingMonitorService._previousStatus` | `ConcurrentDictionary` + `TryUpdate` (CAS) | Main і recovery loop пишуть паралельно |
| `UptimeTrackerService._records` / `_pendingOffline` | `lock(_lock)` | `Receive` (thread pool) + `DeleteRecord` (UI) |
| `UptimeTrackerService` — I/O | `lock(_saveLock)`, debounce 500мс | `Receive` + `DeleteRecord` можуть одночасно викликати `SaveToDisk`; прапорець скидається до старту запису, щоб не загубити паралельні зміни |
| `MaintenanceService._windows` | `ConcurrentDictionary` | Pull-читання з фонових поллерів + запис з UI-потоку та фонового автозавершення |
| `MaintenanceService` — I/O | `lock(_saveLock)` | UI-потік (Start/End вручну) і фоновий цикл автозавершення можуть одночасно писати `maintenance.json` |
| `EventLogService._lastSnapshot` | `lock(_snapshotLock)` | Thread pool пише, UI-потік читає |
| `CredentialStore` — всі поля | `lock(_lock)` | `RdpMonitorService`, `ZabbixPollerService`, UI |
| `ResourceMonitorViewModel._remoteResourceCts` | `Interlocked.Exchange` | `Dispose()` і `LoadRemoteResourceAsync` з різних потоків |
| `ResourceMonitorViewModel._disposed` | `volatile bool` | Видима між потоками без lock |
| `PingMonitorService._mainThrottle` | `SemaphoreSlim(10)` | Обмеження паралельних ICMP з основного циклу |
| `PingMonitorService._recoveryThrottle` | `SemaphoreSlim(5)` | Окремо від основного — recovery не блокується |
| `CredentialPromptCoordinator` | `SemaphoreSlim(1,1)` | Один модальний діалог одночасно |
| `PingResultViewModel.ToggleMaintenanceCommand` | `IsActionBusy` flag | Блокує повторний клік поки чекає на діалог підтвердження |

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
[2026-06-19 10:24:01] [SUCCESS] [PingMonitor]  Kiev-dc1 (192.168.244.86) is back ONLINE. Latency: 1 ms.
[2026-06-19 10:24:03] [ERROR]   [PingMonitor]  Websvr3 (192.168.244.144) went OFFLINE.
[2026-06-19 10:24:15] [WARNING] [UptimeTracker] Websvr3 (192.168.244.144) перейшов у стан OFFLINE.
[2026-06-19 10:31:42] [INFO]    [UptimeTracker] Websvr3 (192.168.244.144) відновлено. Простій: 7хв 27с.
[2026-06-19 11:02:10] [WARNING] [Zabbix]        Zabbix: токен відхилено (цикл 1/3). Причина: Zabbix відхилив токен (code=-32500): API token expired. Оновіть токен у Settings.
[2026-06-19 12:00:00] [INFO]    [Maintenance]   Maintenance розпочато для Websvr3: Планове обслуговування (до 14:00).
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
    "ServerIp": "192.168.244.144",
    "TargetGroup": null,
    "DisplayName": "Websvr3",
    "From": "2026-06-19T12:00:00+03:00",
    "To": "2026-06-19T14:00:00+03:00",
    "Reason": "Планове обслуговування",
    "CreatedAt": "2026-06-19T12:00:00+03:00"
  }
]
```

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

**Вбудовані (Windows Desktop SDK):**
- `System.Net.NetworkInformation.Ping` — ICMP
- `System.Diagnostics.PerformanceCounter` — CPU localhost
- `System.Diagnostics.EventLog` — Event Log localhost
- `advapi32.dll` P/Invoke — `CredWrite`, `CredRead`, `CredFree`, `CredDelete`
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

`appsettings.json` має знаходитись поруч із `.exe`.

---

## Вимоги до середовища

| Вимога | Деталі |
|--------|--------|
| **ОС** | Windows 10 / Windows Server 2016 або новіше |
| **.NET Runtime** | .NET 8 (або self-contained публікація) |
| **Права** | Член домену; локальний адміністратор для WMI-команд (restart/shutdown, remote CPU/RAM/Event Log) |
| **Мережа** | ICMP до всіх серверів; HTTP/HTTPS до Zabbix API; TCP 445 до Terminal Servers; DCOM/WMI (TCP 135 + динамічні порти) до Windows-вузлів |
| **quser.exe** | Вбудований у Windows Server та Windows 10 Pro/Enterprise |
| **Zabbix** | 5.0+ (API token з 5.4+) |
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

## Changelog (останні зміни)

### Нові функції
- **Maintenance Windows** — вікна планового обслуговування для сервера чи групи, з гібридною моделлю Pull+Push, автоматичним закриттям відкритих інцидентів і придушенням шуму в логах на час вікна
- **Uptime & Incidents** — вкладка з повним журналом інцидентів, фільтрацією, збереженням на диск та видаленням записів
- **Recovery Loop** — прискорений пінг (кожні `OfflinePingIntervalSeconds`) тільки для Offline серверів
- **WMI Force shutdown/restart** — `Win32Shutdown` з Force-флагами (5/6) замість звичайних (1/2)
- **Фільтрація коротких "миготінь"** — `MinIncidentDurationSeconds` з двоетапною Pending→Confirmed логікою, без зайвого I/O та UI-мерехтіння

### Архітектурні покращення
- CAS-патерн (`TryUpdate`) замість `_transitionLock + Clear()` у `PingMonitorService`
- `RunLoopGuardedAsync` — взаємне скасування циклів через `LinkedCancellationTokenSource`
- `Interlocked.Exchange` для CTS у `ResourceMonitorViewModel` замість non-atomic replace
- `EventLogReader.IsReachableAsync` — спільний метод замість дублювання у двох сервісах
- `AppLogEntry.Formatted` — кешується при ініціалізації (immutable record)
- `CollectionViewSource` diff-оновлення у `ZabbixViewModel`, `UptimeViewModel` і `RdpSessionViewModel` замість `Clear()` (зі збереженням виділеного рядка)
- `PingDashboardViewModel` — `Dictionary`-lookup замість `FirstOrDefault` для рядків серверів
- `MaintenanceService` — гібридна Pull (Singleton, синхронний `IsUnderMaintenance`) + Push (`IHostedService`, `MaintenanceChangedMessage`) архітектура

### Виправлені баги
- `ZabbixPollerService` — лічильник послідовних auth-невдач перенесено в поле класу (раніше мертвий код через локальну змінну й зайвий `return`)
- `RdpMonitorService` — прибрано виклик `ClearRdp()` при logon failure/access denied; фонові сервіси більше не мають права видаляти credentials
- `CredentialStore.ClearRdp()`/`ClearZabbix()` — тепер коректно скидають **свій** прапорець `UserCancelledXxxPrompt` (раніше `ClearZabbix()` помилково чіпав RDP-прапорець)
- `MaintenanceService.SaveToDisk` — усунено race condition між UI-потоком і фоновим циклом автозавершення (`lock` навколо файлових операцій, серіалізація JSON поза локом)
- `MaintenanceService.EndMaintenanceEarly` — тепер резолвиться по фактичному ключу вікна (`IP` або `group:X`), а не завжди по `IP`
- `UptimeTrackerService.ScheduleSave` — прапорець debounce скидається **до** старту `SaveToDisk`, а не після — інакше зміни, що приходять під час запису на диск, губились
- `UptimeTrackerService` — двоетапна Pending→Confirmed фіксація інцидентів замість "створити одразу, видалити якщо коротке" (усуває подвійне I/O та UI-мерехтіння)
- `PingResultViewModel.ToggleMaintenance` — заблоковано подвійний клік через `IsActionBusy` на час показу діалогу підтвердження
- Кодування консольного виводу — `Console.OutputEncoding = Encoding.UTF8` виправляє нечитабельну кирилицю в консолі/Debug Output IDE
- `ZabbixPollerService` — точна причина відхилення токена (код помилки Zabbix, текст) тепер прокидається в UI-логи, а не губиться в консольному логері
- `SemaphoreSlim` (`_pingThrottle`, `_recoveryThrottle`, `_signal`) тепер явно `Dispose()`-ується
- `FileLoggerService.Receive` — прибрано оманливий TOCTOU-патерн навколо перевірки `_signal.CurrentCount`
- `EventLogService._lastSnapshot` захищений `lock` від race між thread pool і UI-потоком
- `CredentialStore` — всі поля credentials під `lock`, `RtlZeroMemory` замість `new byte[]` у `finally`
- `RdpMonitorService` — `Regex` з `matchTimeout: 500ms`
- `ZabbixApiClient` — `long.TryParse` замість `long.Parse` для `clock`
- `UserSettingsService.Save()` — атомарний запис (temp+rename) замість прямого `File.WriteAllText`
- `RemoteManagementService`, `RemoteResourceService`, `RemoteEventLogService` — усі `ManagementObjectSearcher`/`ManagementObjectCollection`/`ManagementObject` підтверджено в `using`
- Стартовий спам `[SUCCESS] is back ONLINE` прибраний — тихий перехід `Unknown/Checking → Online`
- `UptimeTrackerService` реєстрація у конструкторі — не пропускає перший `PingBatchResultMessage`
