using System;
using System.Collections.Generic;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit
{
    public class AudioServiceTests
    {
        private sealed class FakeAudioSessionService : IAudioSessionService
        {
            public IReadOnlyList<AudioSessionInfo> Sessions { get; init; } = Array.Empty<AudioSessionInfo>();
            public event EventHandler<AudioSessionChangedEventArgs> SessionChanged;
            public IReadOnlyList<AudioSessionInfo> GetAudioSessions() => Sessions;
            public void Raise(AudioSessionChangedEventArgs args) => SessionChanged?.Invoke(this, args);
            public void SetSessionVolume(string sessionIdentifier, float volume) { }
            public void SetSessionMute(string sessionIdentifier, bool isMuted) { }
            public void StartMonitoring() { }
            public void StopMonitoring() { }
            public AudioSessionInfo FindSessionByProcessName(string processName) => null;
            public void Dispose() { }
        }

        [Fact]
        public void SessionChanged_EventRaised_HandlerReceivesEvent()
        {
            var service = new FakeAudioSessionService();

            var received = new List<AudioSessionChangedEventArgs>();
            service.SessionChanged += (s, e) => received.Add(e);

            var info = new AudioSessionInfo { SessionIdentifier = "group", SessionInstanceIdentifier = "sid-1", ProcessId = 123, ProcessName = "test", DisplayName = "test", Volume = 0.5f, IsMuted = false };
            var args = new AudioSessionChangedEventArgs { Session = info, ChangeType = AudioSessionChangeType.VolumeChanged };

            service.Raise(args);

            Assert.Single(received);
            Assert.Equal("sid-1", received[0].Session.SessionInstanceIdentifier);
            Assert.Equal(AudioSessionChangeType.VolumeChanged, received[0].ChangeType);
        }

        [Fact]
        public void ResolveSessionIdentifier_ByIndex_ReturnsCorrectIdentifier()
        {
            var sessions = new List<AudioSessionInfo>
            {
                new AudioSessionInfo { SessionInstanceIdentifier = "s1", ProcessId = 1, ProcessName = "one", DisplayName = "one", Volume = 1.0f },
                new AudioSessionInfo { SessionInstanceIdentifier = "s2", ProcessId = 2, ProcessName = "two", DisplayName = "two", Volume = 1.0f }
            };
            var service = new FakeAudioSessionService { Sessions = sessions };
            var resolved = service.GetAudioSessions()[1].SessionInstanceIdentifier;

            Assert.Equal("s2", resolved);
        }
    }
}
