using AdminConsole.Core.Models;
using AdminConsole.Services;
using AdminConsole.Services;
using AdminConsole.Configuration;
using AdminConsole.ViewModels;
using AdminConsole.Views;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Windows;

namespace AdminConsole;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        // Load MaterialDesign theme dictionaries in code before any
        // window is constructed. This is the most reliable approach
        // across all MD5 versions — it bypasses any XAML path issues.
        LoadMaterialDesignResources();

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory());
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
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

    private static void LoadMaterialDesignResources()
    {
        var mergedDicts = Application.Current.Resources.MergedDictionaries;

        // 1. BundledTheme — sets Dark base, BlueGrey primary, Cyan secondary
        mergedDicts.Add(new MaterialDesignThemes.Wpf.BundledTheme
        {
            BaseTheme = MaterialDesignThemes.Wpf.BaseTheme.Dark,
            PrimaryColor = MaterialDesignColors.PrimaryColor.BlueGrey,
            SecondaryColor = MaterialDesignColors.SecondaryColor.Cyan
        });

        // 2. Core MD3 defaults (controls, typography, etc.)
        mergedDicts.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml",
                UriKind.Absolute)
        });
    }

    private static void RegisterConfiguration(IConfiguration config, IServiceCollection services)
    {
        services.Configure<MonitoringSettings>(
            config.GetSection(MonitoringSettings.SectionName));
        services.Configure<List<ServerEntry>>(
            config.GetSection("Servers"));
    }

    private static void RegisterInfrastructure(IServiceCollection services)
    {
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddHttpClient("Zabbix");

        // Phase 2
        services.AddHostedService<PingMonitorService>();

        // Phase 3
        services.AddHostedService<ResourceMonitorService>();
        services.AddHostedService<EventLogService>();

        // Phase 4
        services.AddHostedService<FileLoggerService>();

        // Phase 5
        services.AddSingleton<RemoteManagementService>();
        services.AddSingleton<IDialogService, DialogService>();
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
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        base.OnExit(e);
    }
}