using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SoundShell.Audio;
using Xunit;

namespace SoundShell.Unit
{
    public class PollingTests
    {
        private class SequenceService : WindowsAudioSessionService
        {
            private readonly Queue<IReadOnlyList<AudioSessionInfo>> sequence;

            public SequenceService(Queue<IReadOnlyList<AudioSessionInfo>> sequence)
                : base(NullLogger<WindowsAudioSessionService>.Instance, WindowsAudioSessionService.MonitoringOptions.Default)
            {
                this.sequence = sequence ?? new Queue<IReadOnlyList<AudioSessionInfo>>();
            }

            protected override IReadOnlyList<AudioSessionInfo> FetchAudioSessions()
            {
                if (sequence.Count == 0)
                    return Array.Empty<AudioSessionInfo>();
                return sequence.Dequeue();
            }
        }

        [Fact]
        public void Polling_Detects_Created_Volume_And_Removed()
        {
            var sid = "s-1";
            var firstEmpty = new List<AudioSessionInfo>();
            var created = new List<AudioSessionInfo>
            {
                new AudioSessionInfo { SessionIdentifier = "group", SessionInstanceIdentifier = sid, ProcessId = 1, ProcessName = "proc", DisplayName = "proc", Volume = 0.5f, IsMuted = false }
            };
            var changed = new List<AudioSessionInfo>
            {
                new AudioSessionInfo { SessionIdentifier = "group", SessionInstanceIdentifier = sid, ProcessId = 1, ProcessName = "proc", DisplayName = "proc", Volume = 0.2f, IsMuted = false }
            };

            var seq = new Queue<IReadOnlyList<AudioSessionInfo>>();
            seq.Enqueue(firstEmpty); // initial
            seq.Enqueue(created); // created
            seq.Enqueue(changed); // volume changed
            seq.Enqueue(firstEmpty); // removed

            using var svc = new SequenceService(seq);

            var events = new List<AudioSessionChangedEventArgs>();
            svc.SessionChanged += (s, e) => events.Add(e);

            // first poll: empty -> no events
            svc.PollSessionsInternal();
            Assert.Empty(events);

            // second poll: detect created
            svc.PollSessionsInternal();
            Assert.Single(events);
            Assert.Equal(AudioSessionChangeType.Created, events[0].ChangeType);
            events.Clear();

            // third poll: detect volume change
            svc.PollSessionsInternal();
            Assert.Single(events);
            Assert.Equal(AudioSessionChangeType.VolumeChanged, events[0].ChangeType);
            events.Clear();

            svc.PollSessionsInternal();
            Assert.Single(events);
            Assert.Equal(AudioSessionChangeType.Removed, events[0].ChangeType);
        }

        [Fact]
        public void Polling_Prefers_MutedChanged_When_Mute_And_Volume_Change_Together()
        {
            var before = new AudioSessionInfo
            {
                SessionInstanceIdentifier = "s-1", ProcessName = "proc", Volume = 0.5f, IsMuted = false
            };
            var after = new AudioSessionInfo
            {
                SessionInstanceIdentifier = "s-1", ProcessName = "proc", Volume = 0.2f, IsMuted = true
            };
            using var svc = new SequenceService(new Queue<IReadOnlyList<AudioSessionInfo>>());
            var events = new List<AudioSessionChangedEventArgs>();
            svc.SessionChanged += (_, e) => events.Add(e);

            svc.ApplySnapshot(new[] { before });
            events.Clear();
            svc.ApplySnapshot(new[] { after });

            Assert.Single(events);
            Assert.Equal(AudioSessionChangeType.MutedChanged, events[0].ChangeType);
            Assert.Equal(0.2f, events[0].Session.Volume);
            Assert.True(events[0].Session.IsMuted);
        }
    }
}
