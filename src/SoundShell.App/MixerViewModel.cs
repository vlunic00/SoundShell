using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using SoundShell.Audio;

namespace SoundShell.App;

public sealed class MixerGroupViewModel : INotifyPropertyChanged
{
    private readonly IAudioSessionService service;
    private IReadOnlyList<AudioSessionInfo> members = Array.Empty<AudioSessionInfo>();
    private bool refreshing;
    private double volume;
    private bool? muteState;
    private bool volumeMixed;

    internal MixerGroupViewModel(string name, IAudioSessionService service)
    {
        Name = name;
        this.service = service;
    }

    public string Name { get; }
    public object Icon { get; private set; }
    public bool IsVolumeMixed => volumeMixed;
    public string VolumeText => volumeMixed ? "Mixed" : $"{volume:0}%";

    public double Volume
    {
        get => volume;
        set
        {
            if (refreshing || Math.Abs(volume - value) < 0.01)
                return;
            volume = value;
            volumeMixed = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVolumeMixed));
            OnPropertyChanged(nameof(VolumeText));
            foreach (var member in members)
            {
                try { service.SetSessionVolume(member.SessionInstanceIdentifier, (float)(value / 100d)); }
                catch (InvalidOperationException) { }
            }
        }
    }

    public bool? MuteState
    {
        get => muteState;
        set
        {
            if (refreshing || value == null || muteState == value)
                return;
            muteState = value;
            OnPropertyChanged();
            foreach (var member in members)
            {
                try { service.SetSessionMute(member.SessionInstanceIdentifier, value.Value); }
                catch (InvalidOperationException) { }
            }
        }
    }

    internal void Update(IReadOnlyList<AudioSessionInfo> sessions, Func<string, object> iconResolver)
    {
        members = sessions;
        refreshing = true;
        var volumes = sessions.Select(session => session.Volume * 100d).ToArray();
        volumeMixed = volumes.Length > 1 && volumes.Max() - volumes.Min() > 0.1d;
        volume = volumes.Length == 0 ? 0d : Math.Round(volumes.Max(), 1);
        var muted = sessions.Count(session => session.IsMuted);
        muteState = muted == 0 ? false : muted == sessions.Count ? true : null;
        Icon ??= iconResolver(sessions.Select(session => session.ExecutablePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)));
        refreshing = false;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(IsVolumeMixed));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(MuteState));
        OnPropertyChanged(nameof(Icon));
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MixerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAudioSessionService service;
    private readonly Action<Action> dispatch;
    private readonly Func<string, object> iconResolver;
    private readonly Dictionary<string, AudioSessionInfo> sessions = new(StringComparer.OrdinalIgnoreCase);
    private string errorMessage;
    private bool initialized;

    public MixerViewModel(IAudioSessionService service, Action<Action> dispatch = null, Func<string, object> iconResolver = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.dispatch = dispatch ?? (action => action());
        this.iconResolver = iconResolver ?? (_ => null);
    }

    public ObservableCollection<MixerGroupViewModel> Groups { get; } = new();
    public bool IsEmpty => Groups.Count == 0 && string.IsNullOrEmpty(ErrorMessage);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ErrorMessage
    {
        get => errorMessage;
        private set
        {
            errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public void Initialize()
    {
        if (initialized)
            return;
        initialized = true;
        try
        {
            foreach (var session in service.GetAudioSessions())
                sessions[session.SessionInstanceIdentifier] = session;
            RefreshGroups();
            service.SessionChanged += OnSessionChanged;
            service.StartMonitoring();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Audio sessions could not be loaded: {ex.Message}";
        }
    }

    internal void Apply(AudioSessionChangedEventArgs change)
    {
        if (change.ChangeType == AudioSessionChangeType.Removed)
            sessions.Remove(change.Session.SessionInstanceIdentifier);
        else
            sessions[change.Session.SessionInstanceIdentifier] = change.Session;
        RefreshGroups();
    }

    private void OnSessionChanged(object sender, AudioSessionChangedEventArgs change)
        => dispatch(() => Apply(change));

    private void RefreshGroups()
    {
        var grouped = sessions.Values
            .GroupBy(session => string.IsNullOrWhiteSpace(session.ProcessName) ? session.DisplayName : session.ProcessName,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var obsolete in Groups.Where(existing => grouped.All(group => !string.Equals(group.Key, existing.Name, StringComparison.OrdinalIgnoreCase))).ToArray())
            Groups.Remove(obsolete);

        for (var index = 0; index < grouped.Length; index++)
        {
            var group = grouped[index];
            var viewModel = Groups.FirstOrDefault(existing => string.Equals(existing.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (viewModel == null)
            {
                viewModel = new MixerGroupViewModel(group.Key, service);
                Groups.Insert(Math.Min(index, Groups.Count), viewModel);
            }
            else
            {
                var currentIndex = Groups.IndexOf(viewModel);
                if (currentIndex != index)
                    Groups.Move(currentIndex, index);
            }
            viewModel.Update(group.ToArray(), iconResolver);
        }

        ErrorMessage = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Dispose()
    {
        service.SessionChanged -= OnSessionChanged;
        service.StopMonitoring();
        service.Dispose();
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
