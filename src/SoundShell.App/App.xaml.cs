using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Storage;

namespace SoundShell.App;

public partial class App : Application
{
    private Window window;

    public App()
    {
        InitializeComponent();
    }

    public static IConfiguration Configuration { get; private set; }
    public static ILoggerFactory LoggerFactory { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var logPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs", "soundshell-.log");
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(Configuration)
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddSerilog());

        window = new MainWindow();
        window.Activate();
    }
}
