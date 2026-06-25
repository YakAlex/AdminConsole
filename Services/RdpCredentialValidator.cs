using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AdminConsole.Services;

/// <summary>
/// Перевіряє RDP credentials через Win32 LogonUser.
/// LOGON32_LOGON_NETWORK — найлегший тип, не створює сесію,
/// не потребує SE_TCB_NAME права, але робить реальну автентифікацію
/// проти контролера домену.
/// </summary>
public sealed class RdpCredentialValidator
{
    /// <summary>
    /// Перевіряє credentials асинхронно на thread pool —
    /// LogonUser блокуючий Win32 виклик (до ~2с при недоступному DC).
    /// </summary>
    public Task<(bool Success, string? Error)> ValidateAsync(
        string username, string password, CancellationToken ct = default)
    {
        return Task.Run(() => Validate(username, password), ct);
    }

    private static (bool Success, string? Error) Validate(string username, string password)
    {
        string domain = string.Empty;
        string user   = username.Trim();

        // DOMAIN\username → розбиваємо
        if (user.Contains('\\'))
        {
            var parts = user.Split('\\', 2);
            domain = parts[0];
            user   = parts[1];
        }
        // user@domain.com → LogonUser сам резолвить через UPN, domain = ""

        IntPtr token = IntPtr.Zero;
        try
        {
            bool ok = LogonUser(
                lpszUsername:    user,
                lpszDomain:      domain,
                lpszPassword:    password,
                dwLogonType:     LOGON32_LOGON_NETWORK,
                dwLogonProvider: LOGON32_PROVIDER_DEFAULT,
                phToken:         out token);

            if (ok) return (true, null);

            int err = Marshal.GetLastWin32Error();
            string message = err switch
            {
                1326 => "Невірний логін або пароль",
                1327 => "Акаунт не має права на мережевий логон",
                1328 => "Заборонений час входу для цього акаунту",
                1329 => "Користувач не може входити з цієї робочої станції",
                1330 => "Пароль акаунту закінчився",
                1331 => "Акаунт вимкнено",
                1332 => "Не знайдено відповідності між іменем акаунту і SID",
                1907 => "Пароль потрібно змінити перед першим входом",
                _    => $"Win32 помилка {err} (0x{err:X8}): {new Win32Exception(err).Message}"
            };

            return (false, message);
        }
        finally
        {
            if (token != IntPtr.Zero)
                CloseHandle(token);
        }
    }

    private const int LOGON32_LOGON_NETWORK    = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(
        string     lpszUsername,
        string     lpszDomain,
        string     lpszPassword,
        int        dwLogonType,
        int        dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}