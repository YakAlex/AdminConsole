# AdminConsole v2

> Десктопний WPF-інструмент для моніторингу та адміністрування серверної інфраструктури домену.
> Написаний на **C# / .NET 8**, архітектура — **MVVM + Microsoft Generic Host**.

---

## Зміст

- [Можливості](#можливості)
- [Архітектура](#архітектура)
- [Структура проєкту](#структура-проєкту)
- [Конфігурація](#конфігурація)
- [Безпека та Credentials](#безпека-та-credentials)
- [Фонові сервіси](#фонові-сервіси)
- [Messenger та повідомлення](#messenger-та-повідомлення)
- [Паралельність та потокобезпека](#паралельність-та-потокобезпека)
- [Logging](#logging)
- [Залежності](#залежності)
- [Збірка та запуск](#збірка-та-запуск)
- [Вимоги до середовища](#вимоги-до-середовища)

---

## Можливості

| Вкладка | Опис |
|---------|------|
| **Ping Dashboard** | Безперервний ICMP-моніторинг усіх серверів з групуванням, індикацією затримки та кольоровим статусом. Логує лише реальні зміни стану. Паралелізм обмежений `SemaphoreSlim`. Результати всього циклу публікуються одним batch-повідомленням. |
| **Uptime & Incidents** | Автоматичне фіксування інцидентів недоступності: час падіння, відновлення, тривалість простою. Повна фільтрація по сервері, групі, даті та статусу. Зберігається на диск у `logs/uptime-YYYY-MM.json` з атомарним записом. |
| **Resource Monitor** | CPU та RAM у реальному часі — для `localhost` через `PerformanceCounter` і `GlobalMemoryStatusEx`, для віддалених машин через WMI. Відображає останні помилки Event Log обраного вузла. |
| **RDP Sessions** | Опитування Terminal Servers через `quser /server:HOSTNAME`. Показує активні та відключені сесії, ім'я користувача, час входу та idle. |
| **Zabbix Alerts** | Інтеграція із Zabbix API (JSON-RPC 2.0). Активні проблеми severity High та Disaster. Підтримка API-токена. |
| **Logs** | Агрегований потік подій усіх сервісів у реальному часі з рівнями severity. Записується у rolling-файл. |
| **Remote Management** | Перезавантаження та вимкнення через WMI (`Win32Shutdown` з Force-флагами), RDP (`mstsc.exe`), безперервний ping у новому вікні, SSH через PuTTY або вбудований Windows SSH. |

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
│  LogsViewModel                                              │
└───────────────────────────┬─────────────────────────────────┘
                            │ WeakReferenceMessenger
┌───────────────────────────▼─────────────────────────────────┐
│           Background Services (IHostedService)              │
│  PingMonitorService    — ICMP, Main + Recovery loop         │
│  UptimeTrackerService  — фіксація інцидентів, JSON          │
│  RdpMonitorService     — quser polling                      │
│  ZabbixPollerService   — Zabbix JSON-RPC                    │
│  ResourceMonitorService — CPU/RAM localhost                  │
│  EventLogService       — Event Log localhost                 │
│  FileLoggerService     — rolling file sink                  │
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
│  UserSettingsService     — %LocalAppData% JSON              │
└─────────────────────────────────────────────────────────────┘
```

### Ключові архітектурні рішення

**Generic Host** — управляє lifecycle усіх `BackgroundService`-ів, DI-контейнером та конфігурацією. Забезпечує коректне graceful-завершення при закритті вікна.

**WeakReferenceMessenger** — шина повідомлень між сервісами та ViewModel-ами. Сервіси публікують (`Send`), ViewModel-и підписуються (`RegisterAll`). Пряма залежність між шарами відсутня. Підписка відбувається у конструкторі, щоб не пропустити перші повідомлення при старті.

**Dual-loop Ping** — `PingMonitorService` запускає два незалежних Task'и через `Task.WhenAll` + `RunLoopGuardedAsync`: основний цикл (всі сервери кожні N секунд) та recovery loop (тільки Offline сервери кожні M секунд). Падіння одного циклу автоматично зупиняє інший через `LinkedCancellationTokenSource`. Синхронізація між циклами — CAS через `ConcurrentDictionary.TryUpdate` без блокувань.

**CAS-патерн замість lock** — у `PingMonitorService` відсутній будь-який `lock` чи `Mutex`. Потокобезпека досягається через `ConcurrentDictionary<string, PingStatus>.TryUpdate` (Compare-And-Swap): тільки перший потік що виграє гонку оновлює статус і логує перехід. Другий потік отримує `false` і мовчить. Це усуває потребу в `_transitionLock` та будь-якому `Clear()`.

**UptimeTrackerService** — окремий Singleton-сервіс що підписується на `PingBatchResultMessage` і веде журнал інцидентів незалежно від UI. Зберігає дані на диск атомарно через write-then-replace (`*.tmp` → rename). `lock(_lock)` захищає список інцидентів, окремий `_saveLock` захищає I/O від одночасного запису з двох потоків.

**On-demand WMI** — `RemoteResourceService` та `RemoteEventLogService` не є `IHostedService`. Вони викликаються напряму з `ResourceMonitorViewModel` коли користувач обирає вузол. Навантаження WMI/DCOM виникає лише для того одного вузла що реально переглядається.

**Спільний `EventLogReader.IsReachableAsync`** — ICMP ping-перевірка перед кожним WMI-запитом до remote машини винесена у спільний статичний метод. Раніше дублювалась у `RemoteResourceService` і `RemoteEventLogService` — тепер одне місце.

**Thread-safe CredentialStore** — всі поля credentials (`_rdpUsername`, `_rdpPassword`, `_zabbixToken`) захищені спільним `_lock`. Читання та запис з різних потоків (`RdpMonitorService`, `ZabbixPollerService`, UI) безпечні.

**IDisposable на ViewModel-ах** — `ResourceMonitorViewModel` та `UptimeViewModel` явно викликаються через `Dispose()` у `App.xaml.cs OnExit` до зупинки хосту. Це зупиняє self-rescheduling WMI-опитування та таймер оновлення тривалості інцидентів.

---

## Структура проєкту

```
AdminConsole/
├── App.xaml / App.xaml.cs              # Точка входу, DI, Host lifecycle, DispatcherUnhandledException
├── appsettings.json                    # Сервери, інтервали, Zabbix URL
│
├── Configuration/
│   └── AppSettings.cs                  # MonitoringSettings (+ OfflinePingIntervalSeconds)
│
├── Core/
│   ├── Models/
│   │   ├── PingResult.cs               # record: Name, IP, Group, Status, LatencyMs, LastChecked
│   │   ├── DowntimeRecord.cs           # sealed class: FellAt, RecoveredAt, Duration, DurationDisplay
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
│       ├── AppLogEntryMessage.cs
│       ├── EventLogUpdatedMessage.cs
│       ├── ResourceSnapshotUpdatedMessage.cs
│       ├── RdpSessionsUpdatedMessage.cs
│       └── ZabbixProblemsUpdatedMessage.cs
│
├── Services/
│   ├── PingMonitorService.cs           # Dual-loop ICMP, CAS TryUpdate, RunLoopGuardedAsync
│   ├── UptimeTrackerService.cs         # Інциденти, JSON persistence, atomic write
│   ├── RdpMonitorService.cs            # quser, Regex з matchTimeout
│   ├── ZabbixPollerService.cs          # JSON-RPC 2.0, token auth
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
│   ├── PingDashboardViewModel.cs
│   ├── UptimeViewModel.cs              # CollectionViewSource, фільтри, Timer, IDisposable
│   ├── RdpSessionViewModel.cs
│   ├── ZabbixViewModel.cs              # Diff-оновлення колекції
│   ├── ResourceMonitorViewModel.cs     # Interlocked.Exchange для CTS, IDisposable
│   └── LogsViewModel.cs               # CleanupThreshold = MaxEntries + 50
│
├── Views/
│   ├── MainWindow.xaml(.cs)
│   ├── UptimeView.xaml(.cs)            # DataGrid, фільтр-бар, Delete кнопка per-row
│   ├── PingDashboardView.xaml(.cs)
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
  "LocalResourcePollIntervalSeconds": 3
}
```

> `OfflinePingIntervalSeconds` — мінімальне значення 5с (захист у коді через `Math.Max`). Recovery loop пінгує тільки сервери зі статусом Offline, тому не збільшує навантаження при здоровій інфраструктурі.

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
| `Group` | Група для візуального групування у Ping Dashboard та Uptime |
| `Type` | `Windows` / `Linux` / `Network`. Визначає кнопки керування та видимість у Server Dashboard |

**Важливі правила:**
- `quser` опитує тільки сервери з групою `Terminal Servers` (порівняння `OrdinalIgnoreCase`)
- Server Dashboard показує лише `Type = "Windows"` сервери (WMI/Event Log недоступні на Linux/Network)
- У `Name` має бути **доменне ім'я**, не IP — `quser` через Named Pipes вимагає NetBIOS-резолву

### Користувацькі налаштування

Зберігаються у `%LocalAppData%\AdminConsole\user_settings.json`, редагуються через Settings overlay (шестерня в sidebar):

| Поле | Опис |
|------|------|
| `CloseToTray` | `true` — згортання у трей при закритті; `false` — завершення процесу |

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

### Thread-safety

`CredentialStore` — Singleton. Всі поля credentials захищені `private readonly object _lock`. Читання/запис з `RdpMonitorService` (thread pool), `ZabbixPollerService` (thread pool) та UI-потоку — безпечні.

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

### UptimeTrackerService

Підписується на `PingBatchResultMessage`. Логіка переходів:

| Перехід | Дія |
|---------|-----|
| `Online → Offline` | Створює `DowntimeRecord` з `FellAt = Now`, вставляє на початок |
| `Offline → Online` | Заповнює `RecoveredAt` для відкритого інциденту |
| `Unknown/Checking → Offline` | Не створює запис (стартовий шум) |
| `Unknown/Checking → Online` | Тихо, без логу |

**Атомарний запис на диск:**
```
1. Серіалізуємо JSON → uptime-YYYY-MM.json.tmp
2. File.Move(.tmp → .json, overwrite: true)   ← атомарна операція NTFS
```

Якщо процес впаде під час запису — `.tmp` пошкоджений, але основний `.json` цілий.

**Два lock-и:**
- `_lock` — захищає `_records` і `_lastStatus` (швидкі in-memory операції)
- `_saveLock` — захищає I/O (повільні операції з диском)

`_messenger.Send(AppLogEntryMessage)` — завжди **поза** `_lock`, щоб уникнути потенційного дедлоку при синхронному виклику підписників.

**Видалення записів:**

При видаленні активного (`!IsResolved`) запису — `_lastStatus[IP]` скидається на `Online`, щоб трекер коректно відслідковував наступний перехід для цього сервера.

`ClearAllResolved` не робить поелементне `Remove` у VM — делегує сервісу, який через `PublishSnapshot → ApplySnapshot` робить один diff-прохід і один `RecordsView.View.Refresh()` замість N подій `CollectionChanged`.

### RdpMonitorService

- Опитує тільки `Group = "Terminal Servers"`
- `quser.exe` з `WaitForExitAsync` і таймаутом 30с
- `Regex` з `matchTimeout: 500ms` — захист від catastrophic backtracking
- Credentials реєструються у Credential Manager перед запитом і видаляються у `finally`
- До 3 спроб при `Logon failure` / `Access Denied`

### ZabbixPollerService

- Два режими: API Token (Zabbix 5.4+)
- Severity `4` (High) і `5` (Disaster)
- `OperationCanceledException` з `Task.Delay` перехоплюється коректно
- HTTP-таймаут 30с через `IHttpClientFactory`

### EventLogService

- Інкрементальний reader: наступні цикли читають від часу останнього читання
- `LastSnapshot` (до 20 записів) захищений `lock(_snapshotLock)` — читається з UI-потоку, пишеться з thread pool
- Зареєстрований як `AddSingleton<EventLogService>()` + `AddHostedService(sp => sp.GetRequiredService<EventLogService>())` — щоб DI повертав **той самий** екземпляр при запиті конкретного типу

### FileLoggerService

- `SemaphoreSlim _signal` явно `Dispose()`-ується через перевизначення `Dispose()` у `BackgroundService`
- Flush-loop: батчі по 50 записів, коалесценс-вікно 200 мс
- Фінальний flush при зупинці хосту

---

## Messenger та повідомлення

| Повідомлення | Видавець | Підписники | Коли |
|---|---|---|---|
| `PingBatchResultMessage` | `PingMonitorService` | `PingDashboardViewModel`, `UptimeTrackerService`, `ResourceMonitorViewModel` | Один раз за цикл (batch всіх результатів) |
| `UptimeUpdatedMessage` | `UptimeTrackerService` | `UptimeViewModel` | При кожній зміні інцидентів |
| `ResourceSnapshotUpdatedMessage` | `ResourceMonitorService` | `ResourceMonitorViewModel` | Кожні `LocalResourcePollIntervalSeconds` |
| `EventLogUpdatedMessage` | `EventLogService` | `ResourceMonitorViewModel` | При появі нових записів |
| `RdpSessionsUpdatedMessage` | `RdpMonitorService` | `RdpSessionViewModel` | Кожні `RdpPollIntervalSeconds` |
| `ZabbixProblemsUpdatedMessage` | `ZabbixPollerService` | `ZabbixViewModel` | Кожні `ZabbixPollIntervalSeconds` |
| `AppLogEntryMessage` | Будь-який сервіс / ViewModel | `LogsViewModel`, `FileLoggerService` | За подією |

**Чому batch для Ping важливий:** до переходу на `PingBatchResultMessage` кожен сервер публікував окреме повідомлення — при 15 серверах 15 окремих `Dispatcher.InvokeAsync` і перемальовувань за цикл. Тепер один batch → один прохід у підписниках.

**CPU/RAM та Event Log для remote через direct call, не Messenger** — `RemoteResourceService`/`RemoteEventLogService` повертають результат як значення з `await`, а не публікують подію. Навмисна асиметрія: remote-дані потрібні одному конкретному ViewModel в конкретний момент.

---

## Паралельність та потокобезпека

| Компонент | Механізм | Причина |
|-----------|----------|---------|
| `PingMonitorService._previousStatus` | `ConcurrentDictionary` + `TryUpdate` (CAS) | Main і recovery loop пишуть паралельно |
| `UptimeTrackerService._records` | `lock(_lock)` | `Receive` (thread pool) + `DeleteRecord` (UI) |
| `UptimeTrackerService` — I/O | `lock(_saveLock)` | `Receive` + `DeleteRecord` можуть одночасно викликати `SaveToDisk` |
| `EventLogService._lastSnapshot` | `lock(_snapshotLock)` | Thread pool пише, UI-потік читає |
| `CredentialStore` — всі поля | `lock(_lock)` | `RdpMonitorService`, `ZabbixPollerService`, UI |
| `ResourceMonitorViewModel._remoteResourceCts` | `Interlocked.Exchange` | `Dispose()` і `LoadRemoteResourceAsync` з різних потоків |
| `ResourceMonitorViewModel._disposed` | `volatile bool` | Видима між потоками без lock |
| `PingMonitorService._mainThrottle` | `SemaphoreSlim(10)` | Обмеження паралельних ICMP з основного циклу |
| `PingMonitorService._recoveryThrottle` | `SemaphoreSlim(5)` | Окремо від основного — recovery не блокується |
| `CredentialPromptCoordinator` | `SemaphoreSlim(1,1)` | Один модальний діалог одночасно |

---

## Logging

Rolling-файли у `logs/` поруч із `.exe` (`AppContext.BaseDirectory`):

```
logs/
├── app-2026-06-18.log
├── app-2026-06-19.log
├── uptime-2026-06.json
└── uptime-2026-07.json
```

**Формат App Log:**
```
[2026-06-19 10:24:01] [SUCCESS] [PingMonitor]  Kiev-dc1 (192.168.244.86) is back ONLINE. Latency: 1 ms.
[2026-06-19 10:24:03] [ERROR]   [PingMonitor]  Websvr3 (192.168.244.144) went OFFLINE.
[2026-06-19 10:24:15] [WARNING] [UptimeTracker] Websvr3 (192.168.244.144) перейшов у стан OFFLINE.
[2026-06-19 10:31:42] [INFO]    [UptimeTracker] Websvr3 (192.168.244.144) відновлено. Простій: 7хв 27с.
```

**Формат Uptime JSON** (`logs/uptime-2026-06.json`):
```json
[
  {
    "ServerName": "Server3",
    "ServerIp": "192.168.244.1",
    "ServerGroup": "DataBase Servers",
    "FellAt": "2026-06-19T10:24:15+03:00",
    "RecoveredAt": "2026-06-19T10:31:42+03:00"
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
| **Zabbix** | 5.0+ (API token з 5.4+;  |
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
- **Uptime & Incidents** — нова вкладка з повним журналом інцидентів, фільтрацією, збереженням на диск та видаленням записів
- **Recovery Loop** — прискорений пінг (кожні `OfflinePingIntervalSeconds`) тільки для Offline серверів
- **WMI Force shutdown/restart** — `Win32Shutdown` з Force-флагами (5/6) замість звичайних (1/2)

### Архітектурні покращення
- CAS-патерн (`TryUpdate`) замість `_transitionLock + Clear()` у `PingMonitorService`
- `RunLoopGuardedAsync` — взаємне скасування циклів через `LinkedCancellationTokenSource`
- `Interlocked.Exchange` для CTS у `ResourceMonitorViewModel` замість non-atomic replace
- `EventLogReader.IsReachableAsync` — спільний метод замість дублювання у двох сервісах
- `AppLogEntry.Formatted` — кешується при ініціалізації (immutable record)
- `CollectionViewSource` diff-оновлення у `ZabbixViewModel` і `UptimeViewModel` замість `Clear()`

### Виправлені баги
- `SemaphoreSlim` (`_pingThrottle`, `_recoveryThrottle`, `_signal`) тепер явно `Dispose()`-ується
- `EventLogService._lastSnapshot` захищений `lock` від race між thread pool і UI-потоком
- `CredentialStore` — всі поля credentials під `lock`, `RtlZeroMemory` замість `new byte[]` у `finally`
- `RdpMonitorService` — `Regex` з `matchTimeout: 500ms`
- `ZabbixApiClient` — `long.TryParse` замість `long.Parse` для `clock`
- `OpenSsh` — `Process.Start` до логу, не після
- `ManagementBaseObject outParams` — тепер у `using`
- Стартовий спам `[SUCCESS] is back ONLINE` прибраний — тихий перехід `Unknown/Checking → Online`
- `UptimeTrackerService` реєстрація у конструкторі — не пропускає перший `PingBatchResultMessage`
