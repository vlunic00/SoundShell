using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SoundShell.Audio;

namespace SoundShell.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        var options = App.Configuration.GetSection("Monitoring").Get<WindowsAudioSessionService.MonitoringOptions>()
            ?? WindowsAudioSessionService.MonitoringOptions.Default;
        var service = new WindowsAudioSessionService(App.LoggerFactory.CreateLogger<WindowsAudioSessionService>(), options);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        ViewModel = new MixerViewModel(service, action => dispatcher.TryEnqueue(() => action()), IconCache.Resolve);
        InitializeComponent();
        Activated += OnActivated;
        Closed += (_, _) => ViewModel.Dispose();
    }

    public MixerViewModel ViewModel { get; }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        ViewModel.Initialize();
    }
}
