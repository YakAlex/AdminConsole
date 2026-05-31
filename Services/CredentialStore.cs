using System.Runtime.InteropServices;
using System.Text;

namespace AdminConsole.Services;

/// <summary>
/// Зберігає credentials в Windows Credential Manager (DPAPI шифрування).
/// Паролі прив'язані до Windows-профілю поточного користувача.
/// Нічого не пишеться на диск у відкритому вигляді.
///
/// Ключі в Credential Manager:
///   "AdminConsole/RDP"    — логін/пароль для Terminal Servers
///   "AdminConsole/Zabbix" — API токен або логін/пароль Zabbix
/// </summary>
public sealed class CredentialStore
{
    // ── Ключі в Windows Credential Manager ───────────────────────────────────
    private const string RdpTarget    = "AdminConsole/RDP";
    private const string ZabbixTarget = "AdminConsole/Zabbix";

    // ── In-memory кеш (щоб не читати з DPAPI кожні 2 хвилини) ───────────────
    private string? _rdpUsername;
    private string? _rdpPassword;
    private string? _zabbixToken;      // API токен або пароль
    private string? _zabbixUsername;   // порожній якщо використовується токен

    public bool UserCancelledRdpPrompt    { get; private set; }
    public bool UserCancelledZabbixPrompt { get; private set; }

    // ── RDP ──────────────────────────────────────────────────────────────────

    public bool HasRdpCredentials =>
        !string.IsNullOrWhiteSpace(_rdpUsername) &&
        !string.IsNullOrWhiteSpace(_rdpPassword);

    public (string Username, string Password) GetRdp() =>
        (_rdpUsername ?? string.Empty, _rdpPassword ?? string.Empty);

    /// <summary>
    /// Завантажує RDP credentials з Windows Credential Manager.
    /// Викликається при старті — якщо збережені раніше, діалог не з'явиться.
    /// </summary>
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

    // ── Zabbix ───────────────────────────────────────────────────────────────

    public bool HasZabbixCredentials =>
        !string.IsNullOrWhiteSpace(_zabbixToken);

    /// <summary>True якщо збережено API токен (username порожній).</summary>
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

    /// <summary>Зберегти API токен (username залишається порожнім).</summary>
    public void StoreZabbixToken(string apiToken)
    {
        _zabbixUsername           = string.Empty;
        _zabbixToken              = apiToken;
        UserCancelledZabbixPrompt = false;
        WriteToVault(ZabbixTarget, string.Empty, apiToken);
    }

    /// <summary>Зберегти логін/пароль для старих версій Zabbix.</summary>
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

    // ── Windows Credential Manager (Win32 DPAPI) ─────────────────────────────

    private static (string Username, string Password)? ReadFromVault(string target)
    {
        if (!CredRead(target, CRED_TYPE.GENERIC, 0, out IntPtr credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            string username = cred.UserName ?? string.Empty;
            string password = cred.CredentialBlobSize > 0
                ? Encoding.Unicode.GetString(
                    PtrToByteArray(cred.CredentialBlob,
                        (int)cred.CredentialBlobSize))
                : string.Empty;
            return (username, password);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static void WriteToVault(string target, string username, string password)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);

        IntPtr blobPtr = Marshal.AllocHGlobal(passwordBytes.Length);
        try
        {
            Marshal.Copy(passwordBytes, 0, blobPtr, passwordBytes.Length);

            var cred = new CREDENTIAL
            {
                Type                 = CRED_TYPE.GENERIC,
                TargetName           = target,
                UserName             = username,
                CredentialBlob       = blobPtr,
                CredentialBlobSize   = (uint)passwordBytes.Length,
                Persist              = CRED_PERSIST.LOCAL_MACHINE,
                AttributeCount       = 0,
                Attributes           = IntPtr.Zero,
                Comment              = null,
                TargetAlias          = null
            };

            CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private static void DeleteFromVault(string target)
    {
        try { CredDelete(target, CRED_TYPE.GENERIC, 0); } catch { }
    }

    // ── P/Invoke helpers ─────────────────────────────────────────────────────

    // Marshal.PtrToByteArray не існує — використовуємо власний хелпер
    private static byte[] PtrToByteArray(IntPtr ptr, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return bytes;
    }

    // ── Win32 structures ─────────────────────────────────────────────────────

    private enum CRED_TYPE : uint { GENERIC = 1 }
    private enum CRED_PERSIST : uint { LOCAL_MACHINE = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint       Flags;
        public CRED_TYPE  Type;
        public string     TargetName;
        public string?    Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint       CredentialBlobSize;
        public IntPtr     CredentialBlob;
        public CRED_PERSIST Persist;
        public uint       AttributeCount;
        public IntPtr     Attributes;
        public string?    TargetAlias;
        public string?    UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target, CRED_TYPE type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(
        string target, CRED_TYPE type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}