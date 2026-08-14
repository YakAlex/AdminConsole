namespace AdminConsole.Utils;

/// <summary>
/// Обхід .NET single-file publish пастки для WPF: AppContext.BaseDirectory
/// у такому режимі резолвиться в тимчасову теку розпаковки бандла
/// (%TEMP%\.net\AdminConsole\&lt;hash&gt;\), а не в реальну директорію .exe.
/// </summary>
public static class AppPaths
{
    public static readonly string BaseDirectory = ResolveBaseDirectory();

    private static string ResolveBaseDirectory()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                return System.IO.Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        }
        catch
        {
            // ігноруємо — падаємо на AppContext.BaseDirectory нижче
        }
        return AppContext.BaseDirectory;
    }
}