using System.Runtime.InteropServices;
using System.Text;

namespace AdminConsole.Services;

/// <summary>
/// Зберігає credentials у Windows Credential Manager через Win32 API.
///
/// ВАЖЛИВО щодо P/Invoke структури CREDENTIAL:
/// Поля рядкового типу (TargetName, UserName тощо) у нативній структурі
/// є LPWSTR (wchar_t*). При маршалінгу через PtrToStructure їх треба
/// оголошувати як IntPtr і конвертувати вручну — НЕ як string,
/// бо CLR не може автоматично маршалити string у Sequential struct
/// при читанні нативної пам'яті.
/// </summary>
public sealed class CredentialStore
{
    private const string RdpTarget    = "AdminConsole/RDP";
    private const string ZabbixTarget = "AdminConsole/Zabbix";

    private string? _rdpUsername;
    private string? _rdpPassword;
    private string? _zabbixToken;
    private string? _zabbixUsername;
    private const string TelegramTarget = "AdminConsole/Telegram";
    private string? _telegramToken;
    private readonly object _lock = new();

    private volatile bool _userCancelledRdpPrompt;
    private volatile bool _userCancelledZabbixPrompt;
    
    public bool UserCancelledRdpPrompt
    {
        get => _userCancelledRdpPrompt;
        private set => _userCancelledRdpPrompt = value;
    }

    public bool UserCancelledZabbixPrompt
    {
        get => _userCancelledZabbixPrompt;
        private set => _userCancelledZabbixPrompt = value;
    }

    // ── RDP ──────────────────────────────────────────────────────────────────

    public bool HasRdpCredentials
    {
        get { lock (_lock) return !string.IsNullOrWhiteSpace(_rdpUsername) &&
                                  !string.IsNullOrWhiteSpace(_rdpPassword); }
    }

    public (string Username, string Password) GetRdp()
    {
        lock (_lock) return (_rdpUsername ?? string.Empty, _rdpPassword ?? string.Empty);
    }

    public void LoadRdpFromVault()
    {
        var cred = ReadFromVault(RdpTarget);
        if (cred is not null)
        {
            lock (_lock)
            {
                _rdpUsername = cred.Value.Username;
                _rdpPassword = cred.Value.Password;
            }
        }
    }

    public void StoreRdp(string username, string password)
    {
        lock (_lock)
        {
            _rdpUsername           = username;
            _rdpPassword           = password;
            UserCancelledRdpPrompt = false;
            WriteToVault(RdpTarget, username, password);
        }
    }

    public void ClearRdp()
    {
        lock (_lock)
        {
            _rdpUsername = null;
            _rdpPassword = null;
            DeleteFromVault(RdpTarget);
        }
        // Скидаємо прапорець скасування — інакше після автоматичного ClearRdp
        // (напр. через logon failure у RdpMonitorService) поллер думатиме,
        // що юзер уже "скасував" запит credentials, і більше сам їх не попросить.
        _userCancelledRdpPrompt = false;
    }

    
    public void MarkRdpCancelled()    => _userCancelledRdpPrompt    = true;
    
    /// <summary>
    /// Повертає тільки Username без пароля — для відображення у Settings.
    /// Ніколи не повертає пароль у VM де він не потрібен.
    /// </summary>
    public string GetRdpUsername()
    {
        lock (_lock) return _rdpUsername ?? string.Empty;
    }

    /// <summary>
    /// Скидає прапорець скасування діалогу.
    /// Критично після збереження credentials через Settings —
    /// без цього RdpMonitorService вважає що юзер скасував і більше
    /// не запитуватиме credentials навіть якщо вони вже збережені.
    /// </summary>
    public void ResetRdpCancelledFlag() => _userCancelledRdpPrompt  = false;

    /// <summary>
    /// Тимчасові credentials для quser /server:HOSTNAME (аналог cmdkey /add, через CredWrite).
    /// TargetName = hostname — має збігатись з аргументом quser /server:.
    /// </summary>
    public bool StoreQuserSession(string hostname, string username, string password) =>
        WriteToVault(hostname, username, password, CRED_PERSIST_SESSION);

    public void ClearQuserSession(string hostname) => DeleteFromVault(hostname);

    // ── Zabbix ───────────────────────────────────────────────────────────────

    public bool HasZabbixCredentials
    {
        get { lock (_lock) return !string.IsNullOrWhiteSpace(_zabbixToken); }
    }

    public bool ZabbixUsesApiToken
    {
        get { lock (_lock) return string.IsNullOrWhiteSpace(_zabbixUsername) &&
                                  !string.IsNullOrWhiteSpace(_zabbixToken); }
    }

    public (string Username, string Token) GetZabbix()
    {
        lock (_lock) return (_zabbixUsername ?? string.Empty, _zabbixToken ?? string.Empty);
    }

    public void LoadZabbixFromVault()
    {
        var cred = ReadFromVault(ZabbixTarget);
        if (cred is not null)
        {
            lock (_lock)
            {
                _zabbixUsername = cred.Value.Username;
                _zabbixToken    = cred.Value.Password;
            }
        }
    }

    public void StoreZabbixToken(string apiToken)
    {
        lock (_lock)
        {
            _zabbixUsername           = string.Empty;
            _zabbixToken              = apiToken;
            UserCancelledZabbixPrompt = false;
            WriteToVault(ZabbixTarget, string.Empty, apiToken);
        }
    }

    public void StoreZabbixCredentials(string username, string password)
    {
        lock (_lock)
        {
            _zabbixUsername           = username;
            _zabbixToken              = password;
            UserCancelledZabbixPrompt = false;
            WriteToVault(ZabbixTarget, username, password);
        }
    }

    public void ClearZabbix()
    {
        lock (_lock)
        {
            _zabbixUsername = null;
            _zabbixToken    = null;
            DeleteFromVault(ZabbixTarget);
        }
        // Скидаємо прапорець скасування — інакше після автоматичного ClearZabbix
        // поллер думатиме, що юзер уже "скасував" запит токена,
        // і більше сам його не попросить.
        _userCancelledZabbixPrompt = false;
    }

    public void MarkZabbixCancelled() => _userCancelledZabbixPrompt = true;

    
    /// <summary>
    /// Повертає замаскований токен для відображення у Settings.
    /// Показує тільки останні 4 символи: "••••••••ab3f"
    /// Якщо токен порожній — повертає порожній рядок.
    /// </summary>
    public string GetZabbixTokenMasked()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_zabbixToken)) return string.Empty;
            return _zabbixToken.Length <= 4
                ? new string('•', _zabbixToken.Length)
                : $"{"••••••••"}{_zabbixToken[^4..]}";
        }
    }

    /// <summary>
    /// Скидає прапорець скасування Zabbix діалогу.
    /// Аналогічно ResetRdpCancelledFlag — критично для коректної роботи
    /// ZabbixPollerService після збереження токену через Settings.
    /// </summary>
    public void ResetZabbixCancelledFlag() => _userCancelledZabbixPrompt = false;
    
    // ── Telegram ─────────────────────────────────────────────────────────────

    public bool HasTelegramCredentials
    {
        get { lock (_lock) return !string.IsNullOrWhiteSpace(_telegramToken); }
    }

    public string GetTelegramToken()
    {
        lock (_lock) return _telegramToken ?? string.Empty;
    }

    public void LoadTelegramFromVault()
    {
        var cred = ReadFromVault(TelegramTarget);
        if (cred is not null)
        {
            lock (_lock) _telegramToken = cred.Value.Password;
        }
    }

    public void StoreTelegramToken(string botToken)
    {
        lock (_lock)
        {
            _telegramToken = botToken;
            WriteToVault(TelegramTarget, string.Empty, botToken);
        }
    }

    public void ClearTelegram()
    {
        lock (_lock)
        {
            _telegramToken = null;
            DeleteFromVault(TelegramTarget);
        }
    }

    /// <summary>Маскований токен для показу в Settings — той самий підхід, що й Zabbix.</summary>
    public string GetTelegramTokenMasked()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_telegramToken)) return string.Empty;
            return _telegramToken.Length <= 4
                ? new string('•', _telegramToken.Length)
                : $"{"••••••••"}{_telegramToken[^4..]}";
        }
    }

    // ── Win32 Credential Manager ──────────────────────────────────────────────

    private static bool WriteToVault(
        string target, string username, string password,
        uint persist = CRED_PERSIST_ENTERPRISE)
    {
        // Порожній пароль — відхиляємо до виклику Win32 API
        if (string.IsNullOrEmpty(password))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CredentialStore] WriteToVault: порожній пароль для '{target}' — пропускаємо.");
            return false;
        }
        
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            Array.Clear(blob, 0, blob.Length); // ← затираємо одразу після Copy

            var cred = new CREDENTIAL_WRITE
            {
                Flags              = 0,
                Type               = CRED_TYPE_GENERIC,
                TargetName         = target,
                Comment            = null,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = persist,
                AttributeCount     = 0,
                Attributes         = IntPtr.Zero,
                TargetAlias        = null,
                UserName           = username
            };

            if (!CredWrite(ref cred, 0))
            {
                int err = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine(
                    $"[CredentialStore] CredWrite FAILED target='{target}' " +
                    $"win32err={err} (0x{err:X8})");
                return false;
            }
            return true;
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                RtlZeroMemory(blobPtr, (UIntPtr)blob.Length);
                Marshal.FreeCoTaskMem(blobPtr);
            }
        }
    }

    private static (string Username, string Password)? ReadFromVault(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
            return null;

        try
        {
            // Читаємо нативну структуру вручну через Marshal —
            // НЕ через PtrToStructure<T> з string полями,
            // бо CLR не може автоматично маршалити LPWSTR при читанні.
            //
            // Розміщення полів CREDENTIAL (x64):
            //   0:  Flags           uint32      4 байти
            //   4:  Type            uint32      4 байти
            //   8:  TargetName      IntPtr      8 байти (LPWSTR)
            //  16:  Comment         IntPtr      8 байти (LPWSTR)
            //  24:  LastWritten     FILETIME    8 байти
            //  32:  BlobSize        uint32      4 байти
            //  36:  (padding)                   4 байти
            //  40:  Blob            IntPtr      8 байти
            //  48:  Persist         uint32      4 байти
            //  52:  AttributeCount  uint32      4 байти
            //  56:  Attributes      IntPtr      8 байти
            //  64:  TargetAlias     IntPtr      8 байти (LPWSTR)
            //  72:  UserName        IntPtr      8 байти (LPWSTR)

            IntPtr targetNamePtr = Marshal.ReadIntPtr(credPtr, 8);
            IntPtr userNamePtr   = Marshal.ReadIntPtr(credPtr, 72);
            uint   blobSize      = (uint)Marshal.ReadInt32(credPtr, 32);
            IntPtr blobPtr       = Marshal.ReadIntPtr(credPtr, 40);

            string username = userNamePtr   != IntPtr.Zero
                ? Marshal.PtrToStringUni(userNamePtr)   ?? string.Empty
                : string.Empty;

            string password = blobPtr != IntPtr.Zero && blobSize > 0
                ? Marshal.PtrToStringUni(blobPtr, (int)blobSize / 2)
                : string.Empty;

            // Прибираємо завершальний null-символ якщо є
            password = password.TrimEnd('\0');

            return (username, password);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static void DeleteFromVault(string target)
    {
        CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    // ── Константи ────────────────────────────────────────────────────────────

    private const uint CRED_TYPE_GENERIC       = 1;
    private const uint CRED_PERSIST_SESSION    = 2;
    private const uint CRED_PERSIST_ENTERPRISE = 3;

    // ── Структура для ЗАПИСУ (string поля — CLR маршалить автоматично) ────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL_WRITE
    {
        public uint    Flags;
        public uint    Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint    CredentialBlobSize;
        public IntPtr  CredentialBlob;
        public uint    Persist;
        public uint    AttributeCount;
        public IntPtr  Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern void RtlZeroMemory(IntPtr dest, UIntPtr size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(
        ref CREDENTIAL_WRITE credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(
        string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}