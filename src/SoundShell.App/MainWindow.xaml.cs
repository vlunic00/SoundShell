using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using SoundShell.Audio;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace SoundShell.App;

public sealed partial class MainWindow : Window
{
    private const string CloseToTrayKey = "CloseToTray";
    private readonly AppWindow appWindow;
    private readonly TrayIcon trayIcon;
    private bool closeToTray;
    private bool exiting;

    public MainWindow()
    {
        closeToTray = ApplicationData.Current.LocalSettings.Values.TryGetValue(CloseToTrayKey, out var value)
            ? value is bool enabled && enabled
            : true;
        var options = App.Configuration.GetSection("Monitoring").Get<WindowsAudioSessionService.MonitoringOptions>()
            ?? WindowsAudioSessionService.MonitoringOptions.Default;
        var service = new WindowsAudioSessionService(App.LoggerFactory.CreateLogger<WindowsAudioSessionService>(), options);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        ViewModel = new MixerViewModel(service, action => dispatcher.TryEnqueue(() => action()), IconCache.Resolve);
        InitializeComponent();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(860, 560));
        appWindow.Closing += OnClosing;
        appWindow.Changed += OnAppWindowChanged;
        trayIcon = new TrayIcon(ShowWindow, Exit);
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public MixerViewModel ViewModel { get; }

    public bool CloseToTray
    {
        get => closeToTray;
        set
        {
            closeToTray = value;
            ApplicationData.Current.LocalSettings.Values[CloseToTrayKey] = value;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        ViewModel.Initialize();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!exiting && CloseToTray)
        {
            args.Cancel = true;
            appWindow.Hide();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange && appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
            appWindow.Hide();
    }

    private void ShowWindow()
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();
        appWindow.Show();
        Activate();
    }

    private void Exit()
    {
        exiting = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        appWindow.Closing -= OnClosing;
        appWindow.Changed -= OnAppWindowChanged;
        trayIcon.Dispose();
        ViewModel.Dispose();
        Log.CloseAndFlush();
    }
}
