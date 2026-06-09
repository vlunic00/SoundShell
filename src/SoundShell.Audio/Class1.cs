using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

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

    public enum AudioSessionChangeType
    {
        Created,
        Removed,
        VolumeChanged,
        MutedChanged
    }

    public sealed class AudioSessionChangedEventArgs : EventArgs
    {
        public AudioSessionInfo Session { get; init; }
        public AudioSessionChangeType ChangeType { get; init; }
    }

    public interface IAudioSessionService : IDisposable
    {
        IReadOnlyList<AudioSessionInfo> GetAudioSessions();
        void SetSessionVolume(string sessionIdentifier, float volume);
        void SetSessionMute(string sessionIdentifier, bool isMuted);
        event EventHandler<AudioSessionChangedEventArgs> SessionChanged;
        void StartMonitoring();
        void StopMonitoring();
        AudioSessionInfo FindSessionByProcessName(string processName);
    }

    public sealed class WindowsAudioSessionService : IAudioSessionService
    {
        private readonly MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
        private System.Threading.CancellationTokenSource monitorCts;
        private readonly object monitorLock = new object();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AudioSessionInfo> knownSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, AudioSessionInfo>(StringComparer.OrdinalIgnoreCase);
        private object comNotification;
        private bool comRegistered;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredSession> perSessionSinks = new System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredSession>(StringComparer.OrdinalIgnoreCase);

        private sealed class RegisteredSession
        {
            public IAudioSessionEvents Events { get; set; }
            public IAudioSessionControl NativeControl { get; set; }
        }

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

        public event EventHandler<AudioSessionChangedEventArgs> SessionChanged;

        public void StartMonitoring()
        {
            lock (monitorLock)
            {
                if (monitorCts != null)
                    return;

                // Try to register COM-based session notifications. If that fails, fall back to polling.
                try
                {
                    using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var sessionManager = device.AudioSessionManager;
                    // create COM-visible notification sink
                    var sink = new AudioSessionNotificationSink(this);
                    // attempt to register via dynamic call to avoid explicit dependency on NAudio internals
                    try
                    {
                        dynamic dyn = sessionManager;
                        dyn.RegisterSessionNotification(sink);
                        comNotification = sink;
                        comRegistered = true;
                    }
                    catch
                    {
                        // registration failed - fall back to polling below
                    }
                }
                catch
                {
                    // ignore and fall back to polling
                }

                // if COM registration not available, start polling loop
                if (!comRegistered)
                {
                    monitorCts = new System.Threading.CancellationTokenSource();
                    var ct = monitorCts.Token;
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            // initial population
                            var current = GetAudioSessions();
                            foreach (var s in current)
                            {
                                knownSessions[s.SessionIdentifier] = s;
                                try { RegisterPerSessionEventsById(s.SessionIdentifier); } catch { }
                            }

                            while (!ct.IsCancellationRequested)
                            {
                                await System.Threading.Tasks.Task.Delay(800, ct).ConfigureAwait(false);
                                var sessions = GetAudioSessions();
                                var snapshot = sessions.ToDictionary(s => s.SessionIdentifier, StringComparer.OrdinalIgnoreCase);

                                // detect created
                                foreach (var kv in snapshot)
                                {
                                    if (!knownSessions.ContainsKey(kv.Key))
                                    {
                                        knownSessions[kv.Key] = kv.Value;
                                        try { RegisterPerSessionEventsById(kv.Key); } catch { }
                                        SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = kv.Value, ChangeType = AudioSessionChangeType.Created });
                                    }
                                }

                                // detect removed
                                foreach (var key in knownSessions.Keys)
                                {
                                    if (!snapshot.ContainsKey(key))
                                    {
                                        if (knownSessions.TryRemove(key, out var removed))
                                            SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = removed, ChangeType = AudioSessionChangeType.Removed });
                                    }
                                }

                                // detect volume/mute changes
                                foreach (var kv in snapshot)
                                {
                                    if (knownSessions.TryGetValue(kv.Key, out var old))
                                    {
                                        var neu = kv.Value;
                                        if (Math.Abs(old.Volume - neu.Volume) > 0.0001f)
                                        {
                                            knownSessions[kv.Key] = neu;
                                            SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = neu, ChangeType = AudioSessionChangeType.VolumeChanged });
                                        }
                                        else if (old.IsMuted != neu.IsMuted)
                                        {
                                            knownSessions[kv.Key] = neu;
                                            SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = neu, ChangeType = AudioSessionChangeType.MutedChanged });
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                    }, ct);
                }
            }
        }

        public void StopMonitoring()
        {
            lock (monitorLock)
            {
                // If COM registration was used, attempt to unregister
                if (comRegistered && comNotification != null)
                {
                    try
                    {
                        using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        var sessionManager = device.AudioSessionManager;
                        try
                        {
                            dynamic dyn = sessionManager;
                            dyn.UnregisterSessionNotification(comNotification);
                        }
                        catch { }
                    }
                    catch { }
                    comNotification = null;
                    comRegistered = false;
                }

                if (monitorCts != null)
                {
                    monitorCts.Cancel();
                    monitorCts.Dispose();
                    monitorCts = null;
                }

                knownSessions.Clear();
            }
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

        private IAudioSessionControl GetNativeSessionControl(AudioSessionControl sessionControl)
        {
            var pUnk = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(sessionControl);
                var iid = new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"); // IAudioSessionControl
                var hr = Marshal.QueryInterface(pUnk, ref iid, out var ppv);
                if (hr != 0 || ppv == IntPtr.Zero)
                    return null;

                try
                {
                    var native = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(ppv, typeof(IAudioSessionControl));
                    return native;
                }
                finally
                {
                    Marshal.Release(ppv);
                }
            }
            catch { return null; }
            finally
            {
                if (pUnk != IntPtr.Zero)
                    Marshal.Release(pUnk);
            }
        }

        private void RegisterPerSessionEventsById(string sessionIdentifier)
        {
            // find AudioSessionControl for identifier
            using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;
            var enumerator = sessionManager.Sessions;

            for (var i = 0; i < enumerator.Count; i++)
            {
                using var sessionControl = enumerator[i];
                if (sessionControl == null)
                    continue;

                if (string.Equals(sessionControl.GetSessionIdentifier, sessionIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    RegisterPerSessionEvents(sessionControl, sessionIdentifier);
                    return;
                }
            }
        }

        private void RegisterPerSessionEvents(AudioSessionControl sessionControl, string sessionIdentifier)
        {
            if (perSessionSinks.ContainsKey(sessionIdentifier))
                return;

            var sink = new AudioSessionEventsSink(this, sessionIdentifier);

            // Try typed COM registration via IAudioSessionControl
            try
            {
                var native = GetNativeSessionControl(sessionControl);
                if (native != null)
                {
                    try
                    {
                        native.RegisterAudioSessionNotification(sink);
                        perSessionSinks[sessionIdentifier] = new RegisteredSession { Events = sink, NativeControl = native };
                        return;
                    }
                    catch { }
                }
            }
            catch { }

            // Fallback: try common registration method names via dynamic
            try
            {
                dynamic dyn = sessionControl;
                try { dyn.RegisterEventCallback(sink); } catch { }
                try { dyn.RegisterAudioSessionNotification(sink); } catch { }
                try { dyn.RegisterSessionNotification(sink); } catch { }
                try { dyn.RegisterAudioSessionEvents(sink); } catch { }
            }
            catch { }

            perSessionSinks[sessionIdentifier] = new RegisteredSession { Events = sink, NativeControl = null };
        }

        private void UnregisterPerSessionEventsById(string sessionIdentifier)
        {
            if (!perSessionSinks.TryRemove(sessionIdentifier, out var registered))
                return;

            try
            {
                if (registered.NativeControl != null && registered.Events != null)
                {
                    try { registered.NativeControl.UnregisterAudioSessionNotification(registered.Events); } catch { }
                }
            }
            catch { }
        }

        

        // COM interop: IAudioSessionNotification sink
        [ComImport]
        [Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionNotification
        {
            [PreserveSig]
            int OnSessionCreated(IntPtr newSession);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int pRetVal);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(ref Guid Grouping, ref Guid EventContext);
            int RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);
            int UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private class AudioSessionNotificationSink : IAudioSessionNotification
        {
            private readonly WindowsAudioSessionService parent;

            public AudioSessionNotificationSink(WindowsAudioSessionService parent)
            {
                this.parent = parent;
            }

            public int OnSessionCreated(IntPtr newSession)
            {
                try
                {
                    var comObj = Marshal.GetObjectForIUnknown(newSession);
                    if (comObj is AudioSessionControl sessionControl)
                    {
                        var info = CreateSessionInfo(sessionControl);
                        parent.knownSessions[info.SessionIdentifier] = info;
                        try { parent.RegisterPerSessionEvents(sessionControl, info.SessionIdentifier); } catch { }
                        parent.SessionChanged?.Invoke(parent, new AudioSessionChangedEventArgs { Session = info, ChangeType = AudioSessionChangeType.Created });
                    }
                }
                catch { }

                return 0; // S_OK
            }
        }

        [ComImport]
        [Guid("24918ACC-64B3-37C1-8CA9-74A66E1F6F11")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEvents
        {
            void OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string NewDisplayName, ref Guid EventContext);
            void OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string NewIconPath, ref Guid EventContext);
            void OnSimpleVolumeChanged(float NewVolume, bool NewMute, ref Guid EventContext);
            void OnChannelVolumeChanged(uint ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, ref Guid EventContext);
            void OnGroupingParamChanged(ref Guid NewGroupingParam, ref Guid EventContext);
            void OnStateChanged(int NewState);
            void OnSessionDisconnected(int DisconnectReason);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private class AudioSessionEventsSink : IAudioSessionEvents
        {
            private readonly WindowsAudioSessionService parent;
            private readonly string sessionId;

            public AudioSessionEventsSink(WindowsAudioSessionService parent, string sessionId)
            {
                this.parent = parent;
                this.sessionId = sessionId;
            }

            public void OnDisplayNameChanged(string NewDisplayName, ref Guid EventContext) { }
            public void OnIconPathChanged(string NewIconPath, ref Guid EventContext) { }

            public void OnSimpleVolumeChanged(float NewVolume, bool NewMute, ref Guid EventContext)
            {
                try
                {
                    var updated = parent.GetAudioSessions().FirstOrDefault(s => string.Equals(s.SessionIdentifier, sessionId, StringComparison.OrdinalIgnoreCase));
                    if (updated != null)
                    {
                        var change = AudioSessionChangeType.VolumeChanged;
                        if (updated.IsMuted != NewMute)
                            change = AudioSessionChangeType.MutedChanged;
                        parent.knownSessions[sessionId] = updated;
                        parent.SessionChanged?.Invoke(parent, new AudioSessionChangedEventArgs { Session = updated, ChangeType = change });
                    }
                }
                catch { }
            }

            public void OnChannelVolumeChanged(uint ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, ref Guid EventContext) { }
            public void OnGroupingParamChanged(ref Guid NewGroupingParam, ref Guid EventContext) { }
            public void OnStateChanged(int NewState) { }
            public void OnSessionDisconnected(int DisconnectReason) { }
        }

        public void Dispose()
        {
            StopMonitoring();
            deviceEnumerator.Dispose();
        }
    }
}
