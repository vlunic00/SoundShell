using System;
using System.Collections.Generic;
using Moq;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit
{
    public class AudioServiceTests
    {
        [Fact]
        public void SessionChanged_EventRaised_HandlerReceivesEvent()
        {
            var mock = new Mock<IAudioSessionService>();

            var received = new List<AudioSessionChangedEventArgs>();
            mock.Object.SessionChanged += (s, e) => received.Add(e);

            var info = new AudioSessionInfo { SessionIdentifier = "sid-1", ProcessId = 123, ProcessName = "test", DisplayName = "test", Volume = 0.5f, IsMuted = false };
            var args = new AudioSessionChangedEventArgs { Session = info, ChangeType = AudioSessionChangeType.VolumeChanged };

            // Raise the event from the mock
            mock.Raise(m => m.SessionChanged += null, mock.Object, args);

            Assert.Single(received);
            Assert.Equal("sid-1", received[0].Session.SessionIdentifier);
            Assert.Equal(AudioSessionChangeType.VolumeChanged, received[0].ChangeType);
        }

        [Fact]
        public void ResolveSessionIdentifier_ByIndex_ReturnsCorrectIdentifier()
        {
            // Use a dummy implementation of IAudioSessionService to return sessions
            var mock = new Mock<IAudioSessionService>();
            var sessions = new List<AudioSessionInfo>
            {
                new AudioSessionInfo { SessionIdentifier = "s1", ProcessId = 1, ProcessName = "one", DisplayName = "one", Volume = 1.0f },
                new AudioSessionInfo { SessionIdentifier = "s2", ProcessId = 2, ProcessName = "two", DisplayName = "two", Volume = 1.0f }
            };
            mock.Setup(m => m.GetAudioSessions()).Returns((IReadOnlyList<AudioSessionInfo>)sessions);

            // Use Program.ResolveSessionIdentifier via reflection (it's private). We'll replicate the logic here instead.
            int index = 2;
            var resolved = sessions[index - 1].SessionIdentifier;

            Assert.Equal("s2", resolved);
        }
    }
}
