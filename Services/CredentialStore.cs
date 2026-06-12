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

    public bool UserCancelledRdpPrompt    { get; private set; }
    public bool UserCancelledZabbixPrompt { get; private set; }

    // ── RDP ──────────────────────────────────────────────────────────────────

    public bool HasRdpCredentials =>
        !string.IsNullOrWhiteSpace(_rdpUsername) &&
        !string.IsNullOrWhiteSpace(_rdpPassword);

    public (string Username, string Password) GetRdp() =>
        (_rdpUsername ?? string.Empty, _rdpPassword ?? string.Empty);

    public void LoadRdpFromVault()
    {
        var cred = ReadFromVault(RdpTarget);
        if (cred is not null)
        {
            _rdpUsername = cred.Value.Username;
            _rdpPassword = cred.Value.Password;
        }
    }

    public void StoreRdp(string username, string password)
    {
        _rdpUsername           = username;
        _rdpPassword           = password;
        UserCancelledRdpPrompt = false;
        WriteToVault(RdpTarget, username, password);
    }

    public void ClearRdp()
    {
        _rdpUsername = null;
        _rdpPassword = null;
        DeleteFromVault(RdpTarget);
    }

    public void MarkRdpCancelled() => UserCancelledRdpPrompt = true;

    /// <summary>
    /// Тимчасові credentials для quser /server:HOSTNAME (аналог cmdkey /add, через CredWrite).
    /// TargetName = hostname — має збігатись з аргументом quser /server:.
    /// </summary>
    public bool StoreQuserSession(string hostname, string username, string password) =>
        WriteToVault(hostname, username, password, CRED_PERSIST_SESSION);

    public void ClearQuserSession(string hostname) => DeleteFromVault(hostname);

    // ── Zabbix ───────────────────────────────────────────────────────────────

    public bool HasZabbixCredentials =>
        !string.IsNullOrWhiteSpace(_zabbixToken);

    public bool ZabbixUsesApiToken =>
        string.IsNullOrWhiteSpace(_zabbixUsername) &&
        !string.IsNullOrWhiteSpace(_zabbixToken);

    public (string Username, string Token) GetZabbix() =>
        (_zabbixUsername ?? string.Empty, _zabbixToken ?? string.Empty);

    public void LoadZabbixFromVault()
    {
        var cred = ReadFromVault(ZabbixTarget);
        if (cred is not null)
        {
            _zabbixUsername = cred.Value.Username;
            _zabbixToken    = cred.Value.Password;
        }
    }

    public void StoreZabbixToken(string apiToken)
    {
        _zabbixUsername           = string.Empty;
        _zabbixToken              = apiToken;
        UserCancelledZabbixPrompt = false;
        WriteToVault(ZabbixTarget, string.Empty, apiToken);
    }

    public void StoreZabbixCredentials(string username, string password)
    {
        _zabbixUsername           = username;
        _zabbixToken              = password;
        UserCancelledZabbixPrompt = false;
        WriteToVault(ZabbixTarget, username, password);
    }

    public void ClearZabbix()
    {
        _zabbixUsername = null;
        _zabbixToken    = null;
        DeleteFromVault(ZabbixTarget);
    }

    public void MarkZabbixCancelled() => UserCancelledZabbixPrompt = true;

    // ── Win32 Credential Manager ──────────────────────────────────────────────

    private static bool WriteToVault(
        string target, string username, string password,
        uint persist = CRED_PERSIST_ENTERPRISE)
    {
        // Пароль кодуємо як Unicode bytes — саме так Windows зберігає credentials
        byte[] blob = Encoding.Unicode.GetBytes(password);

        // Виділяємо некероване сховище для blob
        IntPtr blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            // Для Write використовуємо окрему структуру де рядки — звичайні string,
            // бо тут ми ПЕРЕДАЄМО дані до Win32, а не читаємо з нативної пам'яті.
            // При передачі Marshal автоматично конвертує string → LPWSTR коректно.
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
            Marshal.FreeCoTaskMem(blobPtr);
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
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint    CredentialBlobSize;
        public IntPtr  CredentialBlob;
        public uint    Persist;
        public uint    AttributeCount;
        public IntPtr  Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

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