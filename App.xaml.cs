using AdminConsole.Configuration;
using AdminConsole.Core.Models;
using AdminConsole.Services;
using AdminConsole.ViewModels;
using AdminConsole.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Text;
using System.Windows;

namespace AdminConsole;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Ignored
            }

            // Глобальний обробник необроблених винятків з UI-потоку.
            // Критично для async void OnStartup — виняток що виникне після
            // першого await не може бути перехоплений звичайним try/catch
            // навколо виклику, тому реєструємо запасний handler тут.
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"Необроблена критична помилка:\n\n{args.Exception.Message}\n\n" +
                    "Додаток буде закрито.",
                    "Admin Console — Критична помилка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
                Shutdown(1);
            };

            LoadMaterialDesignResources();

            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json",
                        optional: false, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    RegisterConfiguration(context.Configuration, services);
                    RegisterInfrastructure(services);
                    RegisterViewModels(services);
                    RegisterViews(services);
                })
                .Build();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Критична помилка ініціалізації додатку:\n\n{ex.Message}\n\n" +
                "Перевірте наявність та коректність файлу appsettings.json.",
                "Admin Console — Помилка запуску",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    // ── Resource loading ─────────────────────────────────────────────────────

    private static void LoadMaterialDesignResources()
    {
        var dicts = Application.Current.Resources.MergedDictionaries;

        dicts.Add(new MaterialDesignThemes.Wpf.BundledTheme
        {
            BaseTheme    = MaterialDesignThemes.Wpf.BaseTheme.Dark,
            PrimaryColor = MaterialDesignColors.PrimaryColor.BlueGrey,
            SecondaryColor = MaterialDesignColors.SecondaryColor.Cyan
        });

        dicts.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml",
                UriKind.Absolute)
        });
    }

    // ── DI registration ──────────────────────────────────────────────────────

    private static void RegisterConfiguration(
        IConfiguration config, IServiceCollection services)
    {
        services.Configure<MonitoringSettings>(
            config.GetSection(MonitoringSettings.SectionName));
        services.Configure<List<ServerEntry>>(
            config.GetSection("Servers"));
    }

    private static void RegisterInfrastructure(IServiceCollection services)
    {
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        services.AddHttpClient<ZabbixApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<PingMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<PingMonitorService>());
        services.AddHostedService<ResourceMonitorService>();

        // EventLogService реєструємо як Singleton (а не лише AddHostedService),
        // щоб ResourceMonitorViewModel міг отримати той самий екземпляр напряму
        // через конструктор (потрібно для читання LastSnapshot).
        // AddHostedService<T>() реєструє T лише під IHostedService — конкретний
        // тип T лишається нерезолвним без цього явного AddSingleton.
        services.AddSingleton<EventLogService>();
        services.AddHostedService(sp => sp.GetRequiredService<EventLogService>());

        services.AddHostedService<FileLoggerService>();
        services.AddSingleton<RdpMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<RdpMonitorService>());
        services.AddHostedService<ZabbixPollerService>();
        services.AddSingleton<TelegramAccessControlService>();
        services.AddHostedService<TelegramBotService>();
        services.AddSingleton<UptimeTrackerService>();
        services.AddHostedService(sp => sp.GetRequiredService<UptimeTrackerService>());
        services.AddSingleton<MaintenanceService>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        services.AddSingleton<RemoteEventLogService>();
        services.AddSingleton<RemoteResourceService>();
        services.AddSingleton<RemoteManagementService>();
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<RdpCredentialValidator>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<OverlayDialogService>();
        services.AddSingleton<IDialogService>(sp =>
            sp.GetRequiredService<OverlayDialogService>());
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<PingDashboardViewModel>();
        services.AddSingleton<ResourceMonitorViewModel>();
        services.AddSingleton<RdpSessionViewModel>();
        services.AddSingleton<ZabbixViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<UptimeViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private static void RegisterViews(IServiceCollection services)
    {
        // MainWindow — ICredentialPrompt (через CredentialPromptCoordinator).
        // Hosted services резолвлять ICredentialPrompt при StartAsync();
        // вікно має бути створене і показане ДО _host.StartAsync() (див. OnStartup).
        services.AddSingleton<MainWindow>();
        services.AddSingleton<CredentialPromptCoordinator>();
        services.AddSingleton<ICredentialPrompt>(sp =>
            sp.GetRequiredService<CredentialPromptCoordinator>());
    }

    // ── Host lifecycle ───────────────────────────────────────────────────────

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            mainWindow.InitTray();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Критична помилка при запуску:\n\n{ex.Message}\n\n" +
                "Перевірте файл appsettings.json та спробуйте знову.",
                "Admin Console — Помилка запуску",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host.Services
                .GetRequiredService<MainWindow>()
                .DisposeTrayIcon();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnExit] TrayIcon dispose error: {ex.Message}");
        }

        try
        {
            _host.Services
                .GetRequiredService<ResourceMonitorViewModel>()
                .Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnExit] ResourceMonitorViewModel dispose error: {ex.Message}");
        }

        try
        {
            _host.Services
                .GetRequiredService<UptimeViewModel>()
                .Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnExit] UptimeViewModel dispose error: {ex.Message}");
        }

        try
        {
            _host.Services
                .GetRequiredService<SettingsViewModel>()
                .Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnExit] SettingsViewModel dispose error: {ex.Message}");
        }

        try
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnExit] StopAsync error: {ex.Message}");
        }
        finally
        {
            _host.Dispose();
        }
        base.OnExit(e);
    }
}