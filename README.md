# AdminConsole

Десктопний WPF-додаток для адміністрування та моніторингу серверної інфраструктури домену. Написаний на C# / .NET 8, використовує патерн MVVM та Microsoft Generic Host для управління lifecycle фонових сервісів.

---

## Зміст

- [Можливості](#можливості)
- [Архітектура](#архітектура)
- [Структура проєкту](#структура-проєкту)
- [Залежності](#залежності)
- [Конфігурація](#конфігурація)
- [Безпека та credentials](#безпека-та-credentials)
- [Фонові та on-demand сервіси](#фонові-та-on-demand-сервіси)
- [Messenger та batching повідомлень](#messenger-та-batching-повідомлень)
- [Запуск та збірка](#запуск-та-збірка)
- [Вимоги до середовища](#вимоги-до-середовища)

---

## Можливості

| Модуль | Опис |
|---|---|
| **Ping Dashboard** | Безперервний ICMP-моніторинг усіх серверів з групуванням та індикацією затримки. Логує лише зміни стану (Online / Offline). Паралелізм обмежений `SemaphoreSlim(10)`. Результати за весь цикл публікуються одним пакетним повідомленням. |
| **Resource Monitor (Server Dashboard)** | Вибір вузла зі списку (`localhost` або будь-який Windows-сервер з `appsettings.json`) через `ComboBox`. Для `localhost` — CPU/RAM у реальному часі через `PerformanceCounter` та Win32 `GlobalMemoryStatusEx`. Для віддалених машин — ті самі метрики через WMI (`Win32_Processor`, `Win32_OperatingSystem`), з періодичним оновленням кожні 5с, поки сервер обраний. У цій же вкладці — останні системні помилки вибраного вузла (Event Log). |
| **RDP Sessions** | Опитування термінальних серверів через `quser /server:HOSTNAME`. Показує активні та відключені сесії, ім'я користувача, час входу та idle-час. |
| **Zabbix Alerts** | Інтеграція із Zabbix API (JSON-RPC 2.0). Відображає активні проблеми з severity High та Disaster з підтримкою API-токена. |
| **Event Log** | Локально — фоновий інкрементальний reader `System`/`Application` журналів. Для обраного у Server Dashboard віддаленого вузла — on-demand читання через WMI (`Win32_NTLogEvent`) з фільтром по часу (останні 3 дні) та типу (Error). |
| **Logs** | Єдиний агрегований потік подій усіх сервісів у реальному часі (App Logs — "журнал польотів" самої програми, не плутати з Event Log вузлів). Записується у rolling-файл (`logs/app-YYYY-MM-DD.log`). |
| **Remote Management** | Перезавантаження та вимкнення серверів через WMI (`Win32Shutdown`), відкриття RDP-сесії (`mstsc.exe`), запуск безперервного пінгу (`ping -t`) у окремому вікні. Доступність кнопок залежить від `Type` сервера (`Windows` / `Linux` / `Network`). |

---

## Архітектура

```text
┌──────────────────────────────────────────────────────────┐
│                      WPF UI (Views)                      │
│  MainWindow · PingDashboard · RdpSessions · Zabbix       │
│  ResourceMonitor (Server Dashboard) · Logs               │
└────────────────────────┬─────────────────────────────────┘
                         │  DataBinding (MVVM)
┌────────────────────────▼─────────────────────────────────┐
│                  ViewModels (MVVM)                       │
│  MainViewModel · PingDashboardViewModel                  │
│  RdpSessionViewModel · ZabbixViewModel                   │
│  ResourceMonitorViewModel (IDisposable) · LogsViewModel  │
└────────────────────────┬─────────────────────────────────┘
                         │  WeakReferenceMessenger (MVVM Toolkit)
┌────────────────────────▼─────────────────────────────────┐
│            Background Services (IHostedService)          │
│  PingMonitorService · RdpMonitorService                  │
│  ZabbixPollerService · ResourceMonitorService (local)    │
│  EventLogService (local, кешує LastSnapshot)             │
│  FileLoggerService                                       │
└────────────────────────┬─────────────────────────────────┘
                         │
┌────────────────────────▼─────────────────────────────────┐
│        On-demand сервіси (звичайні Singleton, не Hosted) │
│  RemoteResourceService   — WMI CPU/RAM з обраного вузла  │
│  RemoteEventLogService   — WMI Event Log з обраного вузла│
│  EventLogReader (static) — спільна логіка читання локально│
└────────────────────────┬─────────────────────────────────┘
                         │
┌────────────────────────▼─────────────────────────────────┐
│                 Infrastructure / Core                    │
│  CredentialStore (Win32 Credential Manager)              │
│  ZabbixApiClient (HttpClient / JSON-RPC)                 │
│  RemoteManagementService (WMI / Process)                 │
│  CredentialPromptCoordinator (SemaphoreSlim)             │
│  OverlayDialogService · UserSettingsService              │
└──────────────────────────────────────────────────────────┘
```

### Ключові архітектурні рішення

**Generic Host** — управляє lifecycle усіх `BackgroundService`-ів, DI-контейнером та конфігурацією. Забезпечує коректне завершення сервісів при закритті вікна.

**WeakReferenceMessenger** — шина повідомлень між сервісами та ViewModel-ами. Сервіси публікують повідомлення (`Send`), ViewModel-и підписуються (`RegisterAll`). Відсутня пряма залежність між шарами. Повідомлення не зберігають історію — якщо в момент `Send` нікого не підписано, повідомлення губиться (див. розділ про `EventLogService.LastSnapshot` нижче).

**On-demand сервіси замість BackgroundService для remote-даних** — `RemoteResourceService` та `RemoteEventLogService` не є `IHostedService`: вони викликаються напряму з `ResourceMonitorViewModel` у момент, коли користувач обирає віддалений сервер у Server Dashboard. Це свідомий вибір замість одного "універсального" BackgroundService, що опитує всі сервери одразу — навантаження на мережу (WMI/DCOM) створюється лише для того одного вузла, який реально переглядається.

**Подвійний шлях читання Event Log** — локальна машина читається через легкий `System.Diagnostics.EventLog(".")` (інкрементально, з `early exit` по таймстемпу). Віддалені машини читаються через WMI (`Win32_NTLogEvent`), бо `System.Diagnostics.EventLog` з чистою IP-адресою (без NetBIOS-резолву) ненадійний і часто мовчки повертає 0 записів.

**Захист від RPC/DCOM-таймаутів** — перед кожним WMI-запитом до віддаленої машини (`RemoteResourceService`, `RemoteEventLogService`) виконується короткий (1.5с) ICMP ping. Якщо вузол не відповідає — WMI-запит не виконується взагалі, замість очікування 30-60с на типовий DCOM-таймаут.

**CredentialPromptCoordinator** — серіалізує показ модальних вікон введення credentials через `SemaphoreSlim(1,1)`. Якщо кілька фонових сервісів одночасно потребують credentials, відкривається лише одне вікно; решта отримують той самий результат через спільний `Task`.

**OverlayDialogService** — модальні підтвердження (restart/shutdown) реалізовані як overlay-Grid у MainWindow, без сторонніх DialogHost-бібліотек.

**IDisposable на ResourceMonitorViewModel** — Server Dashboard для віддаленого вузла запускає self-rescheduling WMI-опитування (CPU/RAM кожні 5с), поки цей вузол обраний. Щоб уникнути "осиротілих" задач на thread pool при закритті програми, `App.xaml.cs` явно викликає `ResourceMonitorViewModel.Dispose()` у `OnExit`, до зупинки хосту.

---

## Структура проєкту

```text
AdminConsole/
├── App.xaml / App.xaml.cs          # Точка входу, DI-реєстрація, Host lifecycle
├── appsettings.json                # Конфігурація серверів та інтервалів
│
├── Configuration/
│   └── AppSettings.cs              # MonitoringSettings, UserSettings
│
├── Core/
│   ├── Models/                     # Чисті record/sealed-типи:
│   │   │                           # PingResult, RdpSessionInfo, ZabbixProblem,
│   │   │                           # ResourceSnapshot, AppLogEntry, EventLogEntry,
│   │   │                           # ServerEntry (з полем Type), ServerDashboardEntry
│   └── Messages/                   # Messenger-повідомлення між шарами:
│                                   # PingBatchResultMessage, EventLogUpdatedMessage,
│                                   # ResourceSnapshotUpdatedMessage,
│                                   # RdpSessionsUpdatedMessage, ZabbixProblemsUpdatedMessage,
│                                   # AppLogEntryMessage
│
├── Services/
│   ├── CredentialStore.cs          # Win32 Credential Manager (CredWrite/CredRead/CredFree)
│   ├── CredentialPromptCoordinator.cs  # Дедуплікація credential-діалогів
│   ├── PingMonitorService.cs       # ICMP-моніторинг (BackgroundService), SemaphoreSlim(10)
│   ├── RdpMonitorService.cs        # quser + CredentialManager (BackgroundService)
│   ├── ZabbixPollerService.cs      # Zabbix API polling (BackgroundService)
│   ├── ZabbixApiClient.cs          # JSON-RPC 2.0 HTTP-клієнт
│   ├── ResourceMonitorService.cs   # CPU/RAM локальної машини (BackgroundService)
│   ├── EventLogService.cs          # Event Log локальної машини (BackgroundService, кешує LastSnapshot)
│   ├── EventLogReader.cs           # Статична спільна логіка читання локального Event Log
│   ├── RemoteResourceService.cs    # On-demand WMI CPU/RAM з обраного віддаленого вузла
│   ├── RemoteEventLogService.cs    # On-demand WMI Event Log з обраного віддаленого вузла
│   ├── FileLoggerService.cs        # Rolling file logger (ConcurrentQueue)
│   ├── RemoteManagementService.cs  # WMI shutdown/restart, mstsc, ping
│   └── OverlayDialogService.cs     # Overlay-діалоги підтвердження
│
├── ViewModels/                     # ObservableObject + RelayCommand (MVVM Toolkit)
│   ├── MainViewModel.cs            # Навігація, Settings overlay
│   ├── PingDashboardViewModel.cs
│   ├── RdpSessionViewModel.cs
│   ├── ZabbixViewModel.cs
│   ├── ResourceMonitorViewModel.cs # Server Dashboard: вибір вузла, CPU/RAM, Event Log; IDisposable
│   └── LogsViewModel.cs
│
├── Views/                          # XAML + code-behind
│   └── MainWindow.xaml(.cs)        # ICredentialPrompt implementation; кнопка Settings — у Sidebar
│
└── Resources/
    └── icon.ico
```

---

## Залежності

| Пакет | Версія | Призначення |
|---|---|---|
| `CommunityToolkit.Mvvm` | 8.3.2 | `ObservableObject`, `RelayCommand`, `WeakReferenceMessenger` |
| `MaterialDesignThemes` | 5.1.0 | Material Design 3 UI компоненти та теми |
| `MaterialDesignColors` | 3.1.0 | Палітра кольорів Material Design |
| `Microsoft.Extensions.Hosting` | 8.0.1 | Generic Host, DI, `BackgroundService` |
| `Microsoft.Extensions.Http` | 8.0.1 | `IHttpClientFactory`, typed `HttpClient` |
| `Microsoft.Extensions.Configuration.Json` | 8.0.1 | `appsettings.json` конфігурація |
| `System.Management` | 8.0.0 | WMI (`ManagementScope`, `ManagementObjectSearcher`) — локальний і **remote** доступ |

Вбудовані (Windows Desktop SDK):
- `System.Diagnostics.PerformanceCounter` — CPU моніторинг (localhost)
- `System.Net.NetworkInformation.Ping` — ICMP (Ping Dashboard + pre-check перед WMI-запитами)
- `System.Diagnostics.EventLog` — Event Log локальної машини
- `advapi32.dll` (P/Invoke) — `CredWrite`, `CredRead`, `CredFree`, `CredDelete`
- `kernel32.dll` (P/Invoke) — `GlobalMemoryStatusEx`

---

## Конфігурація

Усі параметри зберігаються у `appsettings.json` поруч із виконуваним файлом. Зчитується з `AppContext.BaseDirectory` (а не `Directory.GetCurrentDirectory()`), тому коректно працює незалежно від робочої директорії запуску.

### Секція `Monitoring`

```jsonc
"Monitoring": {
  "PingIntervalSeconds": 30,           // Інтервал ICMP-опитування
  "ZabbixUrl": "http://<host>/zabbix/api_jsonrpc.php",  // URL Zabbix API
  "ZabbixPollIntervalSeconds": 60,     // Інтервал опитування Zabbix
  "RdpPollIntervalSeconds": 120,       // Інтервал опитування quser
  "LocalResourcePollIntervalSeconds": 3 // Інтервал CPU/RAM локальної машини
}
```

### Секція `Servers`

```jsonc
"Servers": [
  { "Name": "Server1",   "IP": "192.168.244.1",  "Group": "Group1", "Type": "Windows" },
  { "Name": "Server2",   "IP": "192.168.244.2",  "Group": "Group2", "Type": "Linux"   },
  { "Name": "Router",    "IP": "192.168.244.3",  "Group": "Group3", "Type": "Network" }
]
```

**Поля:**

| Поле | Опис |
|---|---|
| `Name` | Доменне ім'я хоста (використовується у `quser /server:Name`) та у Server Dashboard |
| `IP` | IP-адреса (використовується для Ping, RDP, WMI) |
| `Group` | Група для візуального групування у Ping Dashboard |
| `Type` | `Windows` / `Linux` / `Network`. Визначає набір кнопок керування у Ping Dashboard (RDP/Restart/Shutdown — лише для `Windows`) і **чи показується вузол у Server Dashboard** (лише `Windows`-сервери підтримують WMI/Event Log). За відсутності поля — `Windows` за замовчуванням (зворотна сумісність). |

> **Важливо для RDP-моніторингу:** у полі `Name` має бути вказано **доменне ім'я**, а не IP. `quser` працює через Named Pipes / NetBIOS; підключення по IP (RPC over TCP) у більшості доменних середовищ заблоковане.

> **Групи Terminal Servers:** лише сервери з групою `"Terminal Servers"` опитуються через `quser`. Назва групи чутлива до регістру не є — порівняння відбувається через `OrdinalIgnoreCase`.

> **Server Dashboard:** список вузлів у `ComboBox` будується як `localhost` (завжди перший) + усі сервери з `Type = "Windows"`. Сервери типу `Linux`/`Network` у цьому списку не з'являються — WMI та Windows Event Log на них недоступні за визначенням.

### Користувацькі налаштування (`UserSettings`)

Окремо від `appsettings.json` — зберігаються у `%LocalAppData%\AdminConsole\user_settings.json`, змінюються під час роботи програми через Settings overlay (кнопка-шестерня внизу sidebar):

| Поле | Опис |
|---|---|
| `CloseToTray` | `true` — закриття вікна згортає програму у трей; `false` — повністю завершує процес |

---

## Безпека та credentials

### Windows Credential Manager

Додаток **не зберігає паролі у файлах або реєстрі**. Усі credentials зберігаються у Windows Credential Manager через Win32 API (`CredWrite` / `CredRead`) з типом `CRED_TYPE_GENERIC`.

| Target | Вміст | Persist |
|---|---|---|
| `AdminConsole/RDP` | Доменний логін (`DOMAIN\user`) та пароль для `quser` | `CRED_PERSIST_ENTERPRISE` |
| `AdminConsole/Zabbix` | API-токен  Zabbix | `CRED_PERSIST_ENTERPRISE` |
| `<hostname>` | Тимчасові credentials для конкретного quser-запиту | `CRED_PERSIST_SESSION` |

Тимчасові session-credentials для `quser` (`CRED_PERSIST_SESSION`) автоматично видаляються у блоці `finally` після кожного запиту — незалежно від успіху або помилки.

### Захист паролів у пам'яті

- Масив байтів `blob` (UTF-16 LE пароль) затирається через `Array.Clear()` одразу після `Marshal.Copy` до некерованої пам'яті.
- Некерований буфер `blobPtr` перезаписується нулями перед `Marshal.FreeCoTaskMem` у блоці `finally`.

### WMI-доступ до віддалених машин

`RemoteResourceService` та `RemoteEventLogService` підключаються через `ManagementScope` із `ImpersonationLevel.Impersonate` — використовуються **поточні Windows-credentials** користувача, який запустив `AdminConsole.exe` (передається через DCOM). Окремих credentials для WMI не запитується і не зберігається. Якщо обліковий запис не має прав WMI на цільовій машині — повертається керована помилка (`"Access denied…"`) замість краша.

### Введення credentials

При першому запуску (або після відхилення токена Zabbix чи невірного пароля RDP) відображається модальний діалог. `CredentialPromptCoordinator` гарантує, що одночасно відкрите лише одне вікно введення, навіть якщо кілька фонових потоків запросили credentials одночасно.

Якщо користувач закрив діалог без введення — сервіс зупиняє опитування до перезапуску додатку (не показує повторні діалоги у циклі).

---

## Фонові та on-demand сервіси

### PingMonitorService (BackgroundService)

- Паралельно пінгує всі сервери з таймаутом 2000 мс, обмежено `SemaphoreSlim(10)` — не більше 10 одночасних ICMP-запитів незалежно від кількості серверів у конфігурації.
- Логує лише зміну стану (Online → Offline або навпаки), а не кожен успішний пінг.
- Результати **всього циклу** збираються і публікуються **одним** `PingBatchResultMessage` (а не окремим повідомленням на кожен сервер) — підписники (`PingDashboardViewModel`, `ResourceMonitorViewModel`) оновлюють UI за один прохід замість N окремих `Dispatcher`-викликів за цикл.
- При старті негайно публікує batch-стан `Checking` для всіх серверів.

### RdpMonitorService (BackgroundService)

- Опитує лише сервери з групою `Terminal Servers`.
- Виконує `quser.exe /server:<hostname>` асинхронно (`WaitForExitAsync`) з таймаутом 30 секунд і `CancellationToken`.
- Реєструє credentials у Credential Manager перед `quser` та видаляє їх у `finally`.
- При отриманні `Logon failure` або `Access Denied` — очищає збережені credentials та запитує нові (до 3 спроб).
- Парсить формат виводу `quser` (WS2008R2 / WS2012+) для Active та Disconnected сесій.

### ZabbixPollerService (BackgroundService)

- Підтримує два режими авторизації: **API Token** (Zabbix 5.4+) та **User/Password** (session token).
- Відстежує severity `4` (High) та `5` (Disaster).
- При отриманні `ZabbixAuthException` — очищає токен, запитує новий через діалог з linked `CancellationToken` (скасовується при зупинці сервісу або через 5-хвилинний таймаут очікування діалогу) та продовжує роботу без рекурсії.
- HTTP-таймаут: 30 секунд (налаштовано через `IHttpClientFactory`).

### ResourceMonitorService (BackgroundService, локальна машина)

- CPU: `PerformanceCounter("Processor", "% Processor Time", "_Total")`. Перший "прогрівальний" зчитує у `StartAsync`, реальні дані — починаючи з другого.
- RAM: Win32 `GlobalMemoryStatusEx` — точніше ніж `PerformanceCounter` для RAM.
- Інтервал — `LocalResourcePollIntervalSeconds` (за замовчуванням 3с).
- Вимикається автоматично на не-Windows платформах.

### EventLogService (BackgroundService, локальна машина)

- Сканує до 2000 останніх записів у журналах `System` та `Application` через спільний `EventLogReader.ReadErrors(".", since)`.
- Відбирає типи `Error` та `FailureAudit`.
- Інкрементальний режим: наступні цикли скануються лише від часу останнього читання, з early exit одразу як зустрівся вже прочитаний запис.
- Не публікує повідомлення, якщо за цикл немає нових записів (крім самого першого циклу).
- **Кешує** останній знімок у `LastSnapshot` (до 20 записів). Це потрібно тому, що `IMessenger` не зберігає історію повідомлень: якщо `ResourceMonitorViewModel` ще не підписаний у момент першої публікації (типовий випадок при старті програми — вкладку Resources ще не відкрили), повідомлення губиться. ViewModel читає `LastSnapshot` напряму при відкритті вкладки замість очікування наступного циклу.
- Зареєстрований у DI одночасно як `AddSingleton<EventLogService>()` **і** `AddHostedService(sp => sp.GetRequiredService<EventLogService>())` — конкретний тип, а не лише `IHostedService`, потрібен щоб `ResourceMonitorViewModel` міг отримати **той самий** екземпляр через конструктор (інакше DI створив би два незалежні об'єкти з різним станом).

### RemoteResourceService (Singleton, on-demand, **не** BackgroundService)

- Викликається з `ResourceMonitorViewModel` лише коли користувач обирає віддалений вузол у Server Dashboard.
- Перед WMI-запитом виконує власний короткий (1.5с) ping — не покладається на потенційно застарілий (до 30с) статус з `PingMonitorService`.
- CPU: `Win32_Processor.LoadPercentage`, усереднено по всіх логічних ядрах.
- RAM: `Win32_OperatingSystem.TotalVisibleMemorySize` / `FreePhysicalMemory`.
- WMI-таймаут — 8с (`ConnectionOptions.Timeout`).
- Поки обраний той самий віддалений вузол — self-rescheduling опитування кожні 5с (рідше за локальні 3с, бо WMI суттєво дорожчий за `PerformanceCounter`).
- `ManagementObjectSearcher.Get()` та кожен `ManagementObject` явно обгорнуті в `using` — без цього `ManagementObjectCollection` (окремий COM RCW над WMI-енумератором) не звільняється, що при періодичному опитуванні (кожні 5с) поступово накопичує пам'ять процесу.
- Обробляє `UnauthorizedAccessException` окремо — повертає керовану помилку замість краша, якщо поточний обліковий запис не має прав WMI на цільовій машині.

### RemoteEventLogService (Singleton, on-demand, **не** BackgroundService)

- Аналогічно до `RemoteResourceService` — викликається лише при виборі віддаленого вузла.
- На відміну від локального `EventLogService`, читає **через WMI** (`Win32_NTLogEvent`), а не `System.Diagnostics.EventLog` — останній ненадійний з чистими IP-адресами (вимагає NetBIOS-резолву, часто мовчки повертає 0 записів).
- WQL-запит фільтрує одразу на стороні провайдера: `EventType=1` (Error) **і** `TimeGenerated >= <3 дні тому>` — другий предикат дозволяє `Win32_NTLogEvent` раніше зупинити сканування, що суттєво пришвидшує запит на "галасливих" логах, де Error-записи перемежовані великою кількістю Information.
- Додатковий захисний ліміт `maxScanPerLog = 500` — підстраховка для серверів, що генерують аномально багато Error-записів навіть у вузькому часовому вікні.
- Той самий ping pre-check і `UnauthorizedAccessException`-обробка, що й у `RemoteResourceService`.

### EventLogReader (статичний клас)

- Спільна логіка читання Windows Event Log (`ReadErrors`, мапінг severity, обрізка повідомлень), параметризована іменем машини (`"."` для локальної).
- Використовується **лише** локальним `EventLogService`. `RemoteEventLogService` цей клас не використовує (читає через окремий WMI-шлях, описаний вище) — спроба читати remote через `System.Diagnostics.EventLog(name, ip)` виявилась ненадійною на практиці.

### FileLoggerService (BackgroundService)

- Всі `AppLogEntryMessage` з будь-якого сервісу або ViewModel потрапляють у `ConcurrentQueue`.
- Окремий flush-loop читає чергу батчами по 50 записів з коалесценс-вікном 200 мс.
- Rolling-файл змінюється кожен день: `logs/app-2026-06-19.log`, шлях — `AppContext.BaseDirectory/logs` (абсолютний, не залежить від поточної робочої директорії).
- При зупинці хоста виконує фінальний flush черги.

---

## Messenger та batching повідомлень

| Повідомлення | Видавець | Підписники | Періодичність |
|---|---|---|---|
| `PingBatchResultMessage` | `PingMonitorService` | `PingDashboardViewModel`, `ResourceMonitorViewModel` (badge статусу вузла) | Один раз за весь цикл пінгу (не на кожен сервер) |
| `ResourceSnapshotUpdatedMessage` | `ResourceMonitorService` | `ResourceMonitorViewModel` (лише коли обрано `localhost`) | Кожні `LocalResourcePollIntervalSeconds` |
| `EventLogUpdatedMessage` | `EventLogService` | `ResourceMonitorViewModel` (лише коли обрано `localhost`) | При появі нових записів (не публікується, якщо нічого нового) |
| `RdpSessionsUpdatedMessage` | `RdpMonitorService` | `RdpSessionViewModel` | Кожні `RdpPollIntervalSeconds` |
| `ZabbixProblemsUpdatedMessage` | `ZabbixPollerService` | `ZabbixViewModel` | Кожні `ZabbixPollIntervalSeconds` |
| `AppLogEntryMessage` | Будь-який сервіс/ViewModel | `LogsViewModel`, `FileLoggerService` | За подією |

> **Чому batching важливий:** до переходу на `PingBatchResultMessage` `PingMonitorService` публікував окреме повідомлення на кожен сервер — при 15+ серверах це означало 15+ окремих `Dispatcher.InvokeAsync` викликів і перемальовувань `DataGrid` за один цикл опитування. Зараз весь цикл збирається в один `ConcurrentBag`, формується один `PingBatchPayload` і публікується одним повідомленням — підписники застосовують усі оновлення за один прохід.

> **CPU/RAM та Event Log для remote-вузлів НЕ йдуть через Messenger** — `RemoteResourceService`/`RemoteEventLogService` викликаються напряму через `await` з `ResourceMonitorViewModel` і повертають результат як значення, а не публікують подію. Це навмисна асиметрія з локальним шляхом (через Messenger): remote-дані потрібні лише одному конкретному ViewModel у конкретний момент, тому публікація через шину повідомлень на всю програму була б зайвою.

---

## Запуск та збірка

### Збірка

```bash
dotnet build AdminConsole.csproj -c Release
```

### Публікація (self-contained)

```bash
dotnet publish AdminConsole.csproj -c Release -r win-x64 --self-contained true -o ./publish
```

### Запуск

```bash
./publish/AdminConsole.exe
```

або відкрити `AdminConsole.sln` / `AdminConsole.csproj` у Visual Studio 2022+ і запустити (`F5`).

---

## Вимоги до середовища

| Вимога | Деталі |
|---|---|
| **ОС** | Windows 10 / Windows Server 2016 або новіше |
| **.NET Runtime** | .NET 8 (або self-contained публікація) |
| **Права** | Член домену; права локального адміністратора для WMI-команд (restart/shutdown, Server Dashboard CPU/RAM/Event Log для віддалених вузлів) |
| **Мережа** | Доступ до Zabbix API по HTTP/HTTPS; ICMP (ping) до всіх серверів; Named Pipes / NetBIOS до Terminal Servers для `quser`; DCOM/WMI (TCP 135 + динамічний діапазон портів) до Windows-вузлів для Server Dashboard |
| **quser.exe** | Вбудований у Windows Server та Windows 10 Pro/Enterprise |
| **Zabbix** | Версія 5.0+ (API token підтримується з 5.4+; для старіших версій — user/password) |

### Firewall / мережеві вимоги для RDP-моніторингу

`quser /server:<hostname>` використовує Named Pipes через SMB (порт **445**). Підключення по IP замість доменного імені не підтримується (RPC over TCP, зазвичай заблокований).

На цільових Terminal Servers має бути відкритий:
- TCP 445 (SMB / Named Pipes)

### Firewall / мережеві вимоги для Server Dashboard (CPU/RAM/Event Log віддалених вузлів)

WMI/DCOM використовує TCP 135 для початкового підключення, після чого DCOM узгоджує додатковий порт із динамічного діапазону. На цільових машинах:
- Служба `Windows Management Instrumentation` має бути запущена
- Файрвол повинен дозволяти вхідні DCOM-з'єднання (правило `Windows Management Instrumentation (DCOM-In)` у `wf.msc`)
- Поточний користувач `AdminConsole.exe` має мати права WMI на цільовій машині (типово — членство в локальній групі `Administrators` або явний WMI namespace security)

> Якщо WMI недоступний — Server Dashboard коректно показує `"Access denied"` або `"Server is offline or unreachable"` замість краша; пінг-перевірка перед запитом запобігає довгому очікуванню DCOM-таймауту на офлайн-вузлах.

---

## Логування

Файли логів зберігаються у папці `logs/` поруч із виконуваним файлом (абсолютний шлях через `AppContext.BaseDirectory`):

```text
logs/
├── app-2026-06-18.log
└── app-2026-06-19.log
```

Формат запису (приклад):

```text
[2026-06-19 10:24:01 +03:00] [INF] [RdpMonitor] Tsvr3: знайдено 2 сесій.
[2026-06-19 10:24:03 +03:00] [ERR] [PingMonitor] Websvr2 (192.168.244.9) went OFFLINE.
```

> Це **App Logs** — журнал подій самого додатку. Не плутати з **Event Log** вузлів (Server Dashboard), який показує помилки операційної системи цільової машини, а не самого AdminConsole.