using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace SoundShell.Audio
{
    public sealed class AudioSessionInfo
    {
        public string SessionIdentifier { get; init; }
        public string SessionInstanceIdentifier { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; }
        public string DisplayName { get; init; }
        public float Volume { get; init; }
        public bool IsMuted { get; init; }
        public bool IsSystemSounds { get; init; }

        public override string ToString()
            => $"[{SessionIdentifier}] {ProcessName} (PID={ProcessId}) {DisplayName} Volume={Volume:P0} Muted={IsMuted} SystemSounds={IsSystemSounds}";
    }

    public interface IAudioSessionService : IDisposable
    {
        IReadOnlyList<AudioSessionInfo> GetAudioSessions();
        void SetSessionVolume(string sessionIdentifier, float volume);
        void SetSessionMute(string sessionIdentifier, bool isMuted);
        AudioSessionInfo FindSessionByProcessName(string processName);
    }

    public sealed class WindowsAudioSessionService : IAudioSessionService
    {
        private readonly MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();

        public IReadOnlyList<AudioSessionInfo> GetAudioSessions()
        {
            var sessions = new List<AudioSessionInfo>();
            using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;
            var enumerator = sessionManager.Sessions;

            for (var i = 0; i < enumerator.Count; i++)
            {
                using var sessionControl = enumerator[i];
                if (sessionControl == null)
                    continue;

                sessions.Add(CreateSessionInfo(sessionControl));
            }

            return sessions;
        }

        public AudioSessionInfo FindSessionByProcessName(string processName)
        {
            var normalized = processName.Trim().ToLowerInvariant();
            return GetAudioSessions().FirstOrDefault(session => session.ProcessName.ToLowerInvariant() == normalized);
        }

        public void SetSessionVolume(string sessionIdentifier, float volume)
        {
            if (volume < 0f || volume > 1f)
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0.0 and 1.0.");

            using var session = FindSessionControl(sessionIdentifier);
            session.SimpleAudioVolume.Volume = volume;
        }

        public void SetSessionMute(string sessionIdentifier, bool isMuted)
        {
            using var session = FindSessionControl(sessionIdentifier);
            session.SimpleAudioVolume.Mute = isMuted;
        }

        private static AudioSessionInfo CreateSessionInfo(AudioSessionControl session)
        {
            var sessionIdentifier = session.GetSessionIdentifier ?? string.Empty;
            var sessionInstanceIdentifier = session.GetSessionInstanceIdentifier ?? string.Empty;

            var processId = (int)session.GetProcessID;
            var processName = GetProcessName(processId);
            var displayName = !string.IsNullOrWhiteSpace(session.DisplayName)
                ? session.DisplayName
                : sessionIdentifier;

            return new AudioSessionInfo
            {
                SessionIdentifier = sessionIdentifier,
                SessionInstanceIdentifier = sessionInstanceIdentifier,
                ProcessId = processId,
                ProcessName = processName,
                DisplayName = displayName,
                Volume = session.SimpleAudioVolume.Volume,
                IsMuted = session.SimpleAudioVolume.Mute,
                IsSystemSounds = session.IsSystemSoundsSession
            };
        }

        private static string GetProcessName(int processId)
        {
            if (processId <= 0)
                return "System Sounds";

            try
            {
                using var process = Process.GetProcessById(processId);
                return string.IsNullOrWhiteSpace(process.ProcessName) ? processId.ToString() : process.ProcessName;
            }
            catch
            {
                return processId.ToString();
            }
        }

        private AudioSessionControl FindSessionControl(string sessionIdentifier)
        {
            using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;
            var enumerator = sessionManager.Sessions;

            for (var i = 0; i < enumerator.Count; i++)
            {
                using var sessionControl = enumerator[i];
                if (sessionControl == null)
                    continue;

                if (string.Equals(sessionControl.GetSessionIdentifier, sessionIdentifier, StringComparison.OrdinalIgnoreCase))
                    return sessionControl;
            }

            throw new InvalidOperationException($"Audio session '{sessionIdentifier}' not found.");
        }

        public void Dispose()
        {
            deviceEnumerator.Dispose();
        }
    }
}
