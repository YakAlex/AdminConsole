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
- [Фонові сервіси](#фонові-сервіси)
- [Запуск та збірка](#запуск-та-збірка)
- [Вимоги до середовища](#вимоги-до-середовища)

---

## Можливості

| Модуль | Опис |
|---|---|
| **Ping Dashboard** | Безперервний ICMP-моніторинг усіх серверів з групуванням та індикацією затримки. Логує лише зміни стану (Online / Offline). |
| **Resource Monitor** | Відображення завантаженості CPU та RAM локальної машини в реальному часі через `PerformanceCounter` та Win32 `GlobalMemoryStatusEx`. |
| **RDP Sessions** | Опитування термінальних серверів через `quser /server:HOSTNAME`. Показує активні та відключені сесії, ім'я користувача, час входу та idle-час. |
| **Zabbix Alerts** | Інтеграція із Zabbix API (JSON-RPC 2.0). Відображає активні проблеми з severity High та Disaster з підтримкою як API-токена, так і автентифікації user/password. |
| **Event Log** | Читає останні помилки із системного та прикладного журналів подій Windows (`System`, `Application`). |
| **Logs** | Єдиний агрегований потік подій усіх сервісів у реальному часі. Записується у rolling-файл (`logs/app-YYYY-MM-DD.log`). |
| **Remote Management** | Перезавантаження та вимкнення серверів через WMI (`Win32Shutdown`), відкриття RDP-сесії (`mstsc.exe`), запуск безперервного пінгу (`ping -t`) у окремому вікні. |

---

## Архітектура

```
┌─────────────────────────────────────────────────────┐
│                   WPF UI (Views)                    │
│  MainWindow · PingDashboard · RdpSessions · Zabbix  │
│  ResourceMonitor · Logs                             │
└────────────────────┬────────────────────────────────┘
                     │  DataBinding (MVVM)
┌────────────────────▼────────────────────────────────┐
│               ViewModels (MVVM)                     │
│  MainViewModel · PingDashboardViewModel             │
│  RdpSessionViewModel · ZabbixViewModel              │
│  ResourceMonitorViewModel · LogsViewModel           │
└────────────────────┬────────────────────────────────┘
                     │  WeakReferenceMessenger (MVVM Toolkit)
┌────────────────────▼────────────────────────────────┐
│          Background Services (IHostedService)       │
│  PingMonitorService · RdpMonitorService             │
│  ZabbixPollerService · ResourceMonitorService       │
│  EventLogService · FileLoggerService                │
└────────────────────┬────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────┐
│              Infrastructure / Core                  │
│  CredentialStore (Win32 Credential Manager)         │
│  ZabbixApiClient (HttpClient / JSON-RPC)            │
│  RemoteManagementService (WMI / Process)            │
│  CredentialPromptCoordinator (SemaphoreSlim)        │
│  OverlayDialogService · FileLoggerService           │
└─────────────────────────────────────────────────────┘
```

### Ключові архітектурні рішення

**Generic Host** — управляє lifecycle усіх `BackgroundService`-ів, DI-контейнером та конфігурацією. Забезпечує коректне завершення сервісів при закритті вікна.

**WeakReferenceMessenger** — шина повідомлень між сервісами та ViewModel-ами. Сервіси публікують повідомлення (`Send`), ViewModel-и підписуються (`RegisterAll`). Відсутня пряма залежність між шарами.

**CredentialPromptCoordinator** — серіалізує показ модальних вікон введення credentials через `SemaphoreSlim(1,1)`. Якщо кілька фонових сервісів одночасно потребують credentials, відкривається лише одне вікно; решта отримують той самий результат через спільний `Task`.

**OverlayDialogService** — модальні підтвердження (restart/shutdown) реалізовані як overlay-Grid у MainWindow, без сторонніх DialogHost-бібліотек.

---

## Структура проєкту

```
AdminConsole/
├── App.xaml / App.xaml.cs          # Точка входу, DI-реєстрація, Host lifecycle
├── appsettings.json                # Конфігурація серверів та інтервалів
│
├── Configuration/
│   └── AppSettings.cs              # Типізовані класи конфігурації (MonitoringSettings, ServerEntry)
│
├── Core/
│   ├── Models/                     # Чисті record-типи: PingResult, RdpSessionInfo,
│   │   │                           # ZabbixProblem, ResourceSnapshot, AppLogEntry, …
│   └── Messages/                   # Messenger-повідомлення між шарами
│
├── Services/
│   ├── CredentialStore.cs          # Win32 Credential Manager (CredWrite/CredRead/CredFree)
│   ├── CredentialPromptCoordinator.cs  # Дедуплікація credential-діалогів
│   ├── PingMonitorService.cs       # ICMP-моніторинг (BackgroundService)
│   ├── RdpMonitorService.cs        # quser + CredentialManager (BackgroundService)
│   ├── ZabbixPollerService.cs      # Zabbix API polling (BackgroundService)
│   ├── ZabbixApiClient.cs          # JSON-RPC 2.0 HTTP-клієнт
│   ├── ResourceMonitorService.cs   # CPU/RAM через PerformanceCounter + Win32
│   ├── EventLogService.cs          # Windows Event Log reader
│   ├── FileLoggerService.cs        # Rolling file logger (ConcurrentQueue)
│   ├── RemoteManagementService.cs  # WMI shutdown/restart, mstsc, ping
│   └── OverlayDialogService.cs     # Overlay-діалоги підтвердження
│
├── ViewModels/                     # ObservableObject + RelayCommand (MVVM Toolkit)
│   ├── MainViewModel.cs
│   ├── PingDashboardViewModel.cs
│   ├── RdpSessionViewModel.cs
│   ├── ZabbixViewModel.cs
│   ├── ResourceMonitorViewModel.cs
│   └── LogsViewModel.cs
│
├── Views/                          # XAML + code-behind
│   └── MainWindow.xaml(.cs)        # ICredentialPrompt implementation
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
| `System.Management` | 8.0.0 | WMI (`ManagementScope`, `ManagementObjectSearcher`) |

Вбудовані (Windows Desktop SDK):
- `System.Diagnostics.PerformanceCounter` — CPU моніторинг
- `System.Net.NetworkInformation.Ping` — ICMP
- `System.Diagnostics.EventLog` — Windows Event Log
- `advapi32.dll` (P/Invoke) — `CredWrite`, `CredRead`, `CredFree`, `CredDelete`
- `kernel32.dll` (P/Invoke) — `GlobalMemoryStatusEx`

---

## Конфігурація

Усі параметри зберігаються у `appsettings.json` поруч із виконуваним файлом.

### Секція `Monitoring`

```jsonc
"Monitoring": {
  "PingIntervalSeconds": 30,           // Інтервал ICMP-опитування
  "ZabbixUrl": "http://<host>/zabbix/api_jsonrpc.php",  // URL Zabbix API
  "ZabbixPollIntervalSeconds": 60,     // Інтервал опитування Zabbix
  "RdpPollIntervalSeconds": 120,       // Інтервал опитування quser
  "LocalResourcePollIntervalSeconds": 3 // Інтервал CPU/RAM
}
```

### Секція `Servers`

```jsonc
"Servers": [
  { "Name": "Kiev-dc1", "IP": "192.168.244.86", "Group": "Domain Controllers" },
  { "Name": "Tsvr3",    "IP": "192.168.244.73", "Group": "Terminal Servers"   },
  ...
]
```

**Поля:**

| Поле | Опис |
|---|---|
| `Name` | Доменне ім'я хоста (використовується у `quser /server:Name`) |
| `IP` | IP-адреса (використовується для Ping, RDP, WMI) |
| `Group` | Група для візуального групування у Ping Dashboard |

> **Важливо для RDP-моніторингу:** у полі `Name` має бути вказано **доменне ім'я**, а не IP. `quser` працює через Named Pipes / NetBIOS; підключення по IP (RPC over TCP) у більшості доменних середовищ заблоковане.

> **Групи Terminal Servers:** лише сервери з групою `"Terminal Servers"` опитуються через `quser`. Назва групи чутлива до регістру не є — порівняння відбувається через `OrdinalIgnoreCase`.

---

## Безпека та credentials

### Windows Credential Manager

Додаток **не зберігає паролі у файлах або реєстрі**. Усі credentials зберігаються у Windows Credential Manager через Win32 API (`CredWrite` / `CredRead`) з типом `CRED_TYPE_GENERIC`.

| Target | Вміст | Persist |
|---|---|---|
| `AdminConsole/RDP` | Доменний логін (`DOMAIN\user`) та пароль для `quser` | `CRED_PERSIST_ENTERPRISE` |
| `AdminConsole/Zabbix` | API-токен або логін/пароль Zabbix | `CRED_PERSIST_ENTERPRISE` |
| `<hostname>` | Тимчасові credentials для конкретного quser-запиту | `CRED_PERSIST_SESSION` |

Тимчасові session-credentials для `quser` (`CRED_PERSIST_SESSION`) автоматично видаляються у блоці `finally` після кожного запиту — незалежно від успіху або помилки.

### Захист паролів у пам'яті

- Масив байтів `blob` (UTF-16 LE пароль) затирається через `Array.Clear()` одразу після `Marshal.Copy` до некерованої пам'яті.
- Некерований буфер `blobPtr` перезаписується нулями перед `Marshal.FreeCoTaskMem` у блоці `finally`.

### Введення credentials

При першому запуску (або після відхилення токена Zabbix чи невірного пароля RDP) відображається модальний діалог. `CredentialPromptCoordinator` гарантує, що одночасно відкрите лише одне вікно введення, навіть якщо кілька фонових потоків запросили credentials одночасно.

Якщо користувач закрив діалог без введення — сервіс зупиняє опитування до перезапуску додатку (не показує повторні діалоги у циклі).

---

## Фонові сервіси

### PingMonitorService

- Паралельно пінгує всі сервери (`Task.WhenAll`) з таймаутом 2000 мс.
- Логує лише зміну стану (Online → Offline або навпаки), а не кожен успішний пінг.
- При старті негайно публікує стан `Checking` для всіх серверів.

### RdpMonitorService

- Опитує лише сервери з групою `Terminal Servers`.
- Виконує `quser.exe /server:<hostname>` асинхронно (`WaitForExitAsync`) з таймаутом 30 секунд і `CancellationToken`.
- Реєструє credentials у Credential Manager перед `quser` та видаляє їх у `finally`.
- При отриманні `Logon failure` або `Access Denied` — очищає збережені credentials та запитує нові (до 3 спроб).
- Парсить формат виводу `quser` (WS2008R2 / WS2012+) для Active та Disconnected сесій.

### ZabbixPollerService

- Підтримує два режими авторизації: **API Token** (Zabbix 5.4+) та **User/Password** (session token).
- Відстежує severity `4` (High) та `5` (Disaster).
- При отриманні `ZabbixAuthException` (код `-32602` або `-32500`) — очищає токен, запитує новий та продовжує роботу без рекурсії.
- HTTP-таймаут: 30 секунд (налаштовано через `IHttpClientFactory`).

### ResourceMonitorService

- CPU: `PerformanceCounter("Processor", "% Processor Time", "_Total")`. Перший "прогрівальний" зчитує у `StartAsync`, реальні дані — починаючи з другого.
- RAM: Win32 `GlobalMemoryStatusEx` — точніше ніж `PerformanceCounter` для RAM.
- Вимикається автоматично на не-Windows платформах.

### EventLogService

- Сканує до 2000 останніх записів у журналах `System` та `Application`.
- Відбирає типи `Error` та `FailureAudit`.
- Повертає до 20 записів, відсортованих за часом (найновіші першими).

### FileLoggerService

- Всі `AppLogEntryMessage` з будь-якого сервісу або ViewModel потрапляють у `ConcurrentQueue`.
- Окремий flush-loop читає чергу батчами по 50 записів з коалесценс-вікном 200 мс.
- Rolling-файл змінюється кожен день: `logs/app-2025-01-15.log`.
- При зупинці хоста виконує фінальний flush черги.

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
| **Права** | Член домену; права локального адміністратора для WMI-команд (restart/shutdown) |
| **Мережа** | Доступ до Zabbix API по HTTP/HTTPS; ICMP (ping) до всіх серверів; Named Pipes / NetBIOS до Terminal Servers для `quser` |
| **quser.exe** | Вбудований у Windows Server та Windows 10 Pro/Enterprise |
| **Zabbix** | Версія 5.0+ (API token підтримується з 5.4+; для старіших версій — user/password) |

### Firewall / мережеві вимоги для RDP-моніторингу

`quser /server:<hostname>` використовує Named Pipes через SMB (порт **445**). Підключення по IP замість доменного імені не підтримується (RPC over TCP, зазвичай заблокований).

На цільових Terminal Servers має бути відкритий:
- TCP 445 (SMB / Named Pipes)
- Служба `Remote Registry` (залежно від конфігурації домену)

---

## Логування

Файли логів зберігаються у папці `logs/` поруч із виконуваним файлом:

```
logs/
├── app-2026-06-11.log
└── app-2026-06-12.log
```

Формат запису (приклад):
```
[2026-06-12 10:24:01 +03:00] [INF] [RdpMonitor] Tsvr3: знайдено 2 сесій.
[2026-06-12 10:24:03 +03:00] [ERR] [PingMonitor] Websvr2 (192.168.244.9) went OFFLINE.
```
