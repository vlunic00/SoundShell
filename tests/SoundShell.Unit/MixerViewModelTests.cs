using System;
using System.Collections.Generic;
using SoundShell.App;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit;

public sealed class MixerViewModelTests
{
    private sealed class FakeAudioService : IAudioSessionService
    {
        public List<AudioSessionInfo> Sessions { get; } = new();
        public List<(string Id, float Volume)> VolumeChanges { get; } = new();
        public List<(string Id, bool Muted)> MuteChanges { get; } = new();
        public event EventHandler<AudioSessionChangedEventArgs> SessionChanged { add { } remove { } }
        public IReadOnlyList<AudioSessionInfo> GetAudioSessions() => Sessions;
        public void SetSessionVolume(string id, float volume) => VolumeChanges.Add((id, volume));
        public void SetSessionMute(string id, bool muted) => MuteChanges.Add((id, muted));
        public void StartMonitoring() { }
        public void StopMonitoring() { }
        public AudioSessionInfo FindSessionByProcessName(string name) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Groups_By_ProcessName_And_Synchronizes_Mixed_Members()
    {
        var service = new FakeAudioService();
        service.Sessions.Add(Session("one", "Chrome", 0.2f, false));
        service.Sessions.Add(Session("two", "chrome", 0.8f, true));
        using var viewModel = new MixerViewModel(service);

        viewModel.Initialize();

        var group = Assert.Single(viewModel.Groups);
        Assert.True(group.IsVolumeMixed);
        Assert.Equal(80d, group.Volume);
        Assert.Null(group.MuteState);

        group.Volume = 50d;
        group.MuteState = true;

        Assert.Equal(2, service.VolumeChanges.Count);
        Assert.All(service.VolumeChanges, change => Assert.Equal(0.5f, change.Volume));
        Assert.Equal(2, service.MuteChanges.Count);
        Assert.All(service.MuteChanges, change => Assert.True(change.Muted));
    }

    [Fact]
    public void Apply_Removes_The_Final_Session_Group()
    {
        var service = new FakeAudioService();
        var session = Session("one", "Spotify", 0.5f, false);
        service.Sessions.Add(session);
        using var viewModel = new MixerViewModel(service);
        viewModel.Initialize();

        viewModel.Apply(new AudioSessionChangedEventArgs { Session = session, ChangeType = AudioSessionChangeType.Removed });

        Assert.Empty(viewModel.Groups);
    }

    private static AudioSessionInfo Session(string id, string process, float volume, bool muted) => new()
    {
        SessionInstanceIdentifier = id,
        ProcessName = process,
        DisplayName = process,
        Volume = volume,
        IsMuted = muted
    };
}
