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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        
        LoadMaterialDesignResources();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory());
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

        services.AddHostedService<PingMonitorService>();
        services.AddHostedService<ResourceMonitorService>();
        services.AddHostedService<EventLogService>();
        services.AddHostedService<FileLoggerService>();
        services.AddHostedService<RdpMonitorService>();
        services.AddHostedService<ZabbixPollerService>();

        services.AddSingleton<RemoteManagementService>();
        services.AddSingleton<CredentialStore>();          // ← новий

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

        // КРИТИЧНИЙ ПОРЯДОК: Show() перед StartAsync().
        // StartAsync() запускає RdpMonitorService / ZabbixPollerService, які
        // викликають ICredentialPrompt → Dispatcher.InvokeAsync → ShowDialog().
        // Якщо StartAsync() поставити раніше Show() — діалоги підуть на вікно,
        // яке ще не на екрані, або до OverlayDialogService.Attach().
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        await _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            // Блокуємо головний потік максимум на 3 секунди, щоб сервіси встигли завершитись
            Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(3))).Wait();
        }
        base.OnExit(e);
    }
}