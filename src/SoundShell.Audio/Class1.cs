using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    public class WindowsAudioSessionService : IAudioSessionService
    {
        private readonly ILogger<WindowsAudioSessionService> _logger;
        private readonly MonitoringOptions _options;
        private readonly string _serviceInstanceId = Guid.NewGuid().ToString();

        public WindowsAudioSessionService() : this(NullLogger<WindowsAudioSessionService>.Instance, MonitoringOptions.Default) { }

        public WindowsAudioSessionService(ILogger<WindowsAudioSessionService> logger) : this(logger, MonitoringOptions.Default) { }

        public WindowsAudioSessionService(ILogger<WindowsAudioSessionService> logger, MonitoringOptions options)
        {
            _logger = logger ?? NullLogger<WindowsAudioSessionService>.Instance;
            _options = options ?? MonitoringOptions.Default;
        }

        public sealed class MonitoringOptions
        {
            public static MonitoringOptions Default { get; } = new MonitoringOptions();

            public int SessionRegistrationMaxAttempts { get; set; } = 5;
            public int SessionRegistrationBackoffMs { get; set; } = 200;
            public int PerSessionRegistrationMaxAttempts { get; set; } = 3;
            public int PerSessionRegistrationBackoffMs { get; set; } = 100;
            // Polling fallback (PoC compatibility)
            public bool EnablePollingFallback { get; set; } = true;
            public int PollingIntervalMs { get; set; } = 800;
        }

        private readonly MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
        private readonly object monitorLock = new object();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AudioSessionInfo> knownSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, AudioSessionInfo>(StringComparer.OrdinalIgnoreCase);
        private object comNotification;
        private bool comRegistered;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredSession> perSessionSinks = new System.Collections.Concurrent.ConcurrentDictionary<string, RegisteredSession>(StringComparer.OrdinalIgnoreCase);
        private Timer pollingTimer;
        private int pollingInProgress;

        private sealed class RegisteredSession
        {
            public IAudioSessionEvents Events { get; set; }
            public IAudioSessionControl NativeControl { get; set; }
        }

        public IReadOnlyList<AudioSessionInfo> GetAudioSessions()
        {
            return FetchAudioSessions();
        }

        // Separated out for testability: subclasses (tests) can override this to provide deterministic session sets.
        protected virtual IReadOnlyList<AudioSessionInfo> FetchAudioSessions()
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
                if (comRegistered)
                    return;

                // Attempt to register COM-based session notifications. If successful,
                // also register per-session sinks for existing sessions.
                try
                {
                    using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var sessionManager = device.AudioSessionManager;

                    var sink = new AudioSessionNotificationSink(this);

                    // Try registration with retries/backoff and log results
                    if (TryRegisterSessionNotificationWithRetry(sessionManager, sink, out var regEx))
                    {
                        comNotification = sink;
                        comRegistered = true;

                        // Register per-session event sinks for currently active sessions
                        var sessions = GetAudioSessions();
                        foreach (var s in sessions)
                        {
                            knownSessions[s.SessionIdentifier] = s;
                            try { RegisterPerSessionEventsById(s.SessionIdentifier); } catch (Exception ex) { _logger.LogWarning(ex, "RegisterPerSessionEventsById failed for {SessionId}", s.SessionIdentifier); }
                        }
                        // If polling fallback explicitly enabled, still avoid polling when COM notifications are successfully registered.
                    }
                    else
                    {
                        _logger.LogWarning(regEx, "RegisterSessionNotification ultimately failed after retries.");
                        comNotification = null;
                        comRegistered = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to obtain session manager for monitoring.");
                    comNotification = null;
                    comRegistered = false;
                }
                // If COM-based registration wasn't available and fallback is enabled, start polling
                if (!comRegistered && (_options?.EnablePollingFallback ?? false))
                {
                    _logger.LogInformation("Starting polling fallback for audio session monitoring (interval {Ms}ms)", _options.PollingIntervalMs);
                    StartPolling();
                }
            }
        }

        private bool TryRegisterSessionNotificationWithRetry(object sessionManager, IAudioSessionNotification sink, out Exception lastException)
        {
            lastException = null;
            var maxAttempts = _options?.SessionRegistrationMaxAttempts ?? 3;
            var backoffMs = _options?.SessionRegistrationBackoffMs ?? 200;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    try { Console.WriteLine($"TryRegisterSessionNotificationWithRetry attempt {attempt}"); } catch {}
                    // Prefer reflection invocation to avoid dynamic binder/runtime issues in test hosts.
                    var mi = sessionManager?.GetType().GetMethod("RegisterSessionNotification");
                    if (mi != null)
                    {
                        mi.Invoke(sessionManager, new object[] { sink });
                        try { Console.WriteLine($"TryRegisterSessionNotificationWithRetry reflection invoke succeeded on attempt {attempt}"); } catch {}
                    }
                    else
                    {
                        dynamic dyn = sessionManager;
                        dyn.RegisterSessionNotification(sink);
                        try { Console.WriteLine($"TryRegisterSessionNotificationWithRetry dynamic invoke succeeded on attempt {attempt}"); } catch {}
                    }

                    _logger.LogInformation("Registered session notification on attempt {Attempt}", attempt);
                    return true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Attempt {Attempt} to RegisterSessionNotification failed", attempt);
                    try { System.Threading.Thread.Sleep(backoffMs * attempt); } catch { }
                }
            }

            _logger.LogError(lastException, "Failed to register session notification after {Attempts} attempts", maxAttempts);
            return false;
        }

        public void StopMonitoring()
        {
            lock (monitorLock)
            {
                // Stop polling if active
                StopPolling();
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
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to unregister session notification");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed while attempting to unregister session notification");
                    }

                    comNotification = null;
                    comRegistered = false;
                }

                // Unregister any per-session sinks and release COM references
                foreach (var kv in perSessionSinks.Keys.ToList())
                {
                    try { UnregisterPerSessionEventsById(kv); } catch (Exception ex) { _logger.LogWarning(ex, "Error unregistering per-session sink {SessionId}", kv); }
                }

                knownSessions.Clear();
            }
        }

        private void StartPolling()
        {
            if (pollingTimer != null)
                return;

            var interval = (_options?.PollingIntervalMs > 0) ? _options.PollingIntervalMs : 800;
            pollingTimer = new Timer(_ => PollSessions(), null, 0, interval);
        }

        private void StopPolling()
        {
            try
            {
                var t = Interlocked.Exchange(ref pollingTimer, null);
                if (t != null)
                {
                    t.Change(Timeout.Infinite, Timeout.Infinite);
                    t.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StopPolling failed");
            }
            finally
            {
                Interlocked.Exchange(ref pollingInProgress, 0);
            }
        }

        private void PollSessions()
        {
            // Prevent reentrancy
            if (Interlocked.Exchange(ref pollingInProgress, 1) == 1)
                return;

            try
            {
                var current = GetAudioSessions();
                var currentById = current.ToDictionary(s => s.SessionIdentifier, StringComparer.OrdinalIgnoreCase);

                // Detect created sessions
                foreach (var kv in currentById)
                {
                    if (!knownSessions.ContainsKey(kv.Key))
                    {
                        knownSessions[kv.Key] = kv.Value;
                        try
                        {
                            RegisterPerSessionEventsById(kv.Key);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "RegisterPerSessionEventsById failed during polling for {SessionId}", kv.Key);
                        }

                        using (_logger.BeginScope(new System.Collections.Generic.Dictionary<string, object> { ["SessionId"] = kv.Key, ["ServiceInstanceId"] = _serviceInstanceId }))
                        {
                            SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = kv.Value, ChangeType = AudioSessionChangeType.Created });
                        }
                    }
                }

                // Detect removed sessions
                foreach (var knownKey in knownSessions.Keys.ToList())
                {
                    if (!currentById.ContainsKey(knownKey))
                    {
                        if (knownSessions.TryRemove(knownKey, out var removed))
                        {
                            try { UnregisterPerSessionEventsById(knownKey); } catch (Exception ex) { _logger.LogDebug(ex, "UnregisterPerSessionEventsById failed during polling for {SessionId}", knownKey); }
                            using (_logger.BeginScope(new System.Collections.Generic.Dictionary<string, object> { ["SessionId"] = knownKey, ["ServiceInstanceId"] = _serviceInstanceId }))
                            {
                                SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = removed, ChangeType = AudioSessionChangeType.Removed });
                            }
                        }
                    }
                }

                // Detect volume/mute changes
                foreach (var kv in currentById)
                {
                    if (knownSessions.TryGetValue(kv.Key, out var known))
                    {
                        if (Math.Abs(known.Volume - kv.Value.Volume) > 0.0001f || known.IsMuted != kv.Value.IsMuted)
                        {
                            knownSessions[kv.Key] = kv.Value;
                            var change = AudioSessionChangeType.VolumeChanged;
                            if (known.IsMuted != kv.Value.IsMuted)
                                change = AudioSessionChangeType.MutedChanged;
                            using (_logger.BeginScope(new System.Collections.Generic.Dictionary<string, object> { ["SessionId"] = kv.Key, ["ServiceInstanceId"] = _serviceInstanceId }))
                            {
                                SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = kv.Value, ChangeType = change });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling loop failed");
            }
            finally
            {
                Interlocked.Exchange(ref pollingInProgress, 0);
            }
        }

        // Internal test hook to invoke the polling logic synchronously from unit tests.
        internal void PollSessionsInternal()
        {
            PollSessions();
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
            catch (Exception ex)
            {
                // Don't throw from this helper; return numeric id if process name can't be resolved.
                try { Trace.WriteLine($"GetProcessName failed for PID {processId}: {ex.Message}"); } catch (Exception) { }
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
                var hr = Marshal.QueryInterface(pUnk, in iid, out var ppv);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetNativeSessionControl failed for session control");
                return null;
            }
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
                    // try with small retry/backoff (configurable)
                    var maxAttempts = _options?.PerSessionRegistrationMaxAttempts ?? 3;
                    var backoffMs = _options?.PerSessionRegistrationBackoffMs ?? 100;
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            native.RegisterAudioSessionNotification(sink);
                            perSessionSinks[sessionIdentifier] = new RegisteredSession { Events = sink, NativeControl = native };
                            _logger.LogInformation("Registered per-session audio events for {SessionId} on attempt {Attempt}", sessionIdentifier, attempt);
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Attempt {Attempt} to register per-session events failed for {SessionId}", attempt, sessionIdentifier);
                            try { System.Threading.Thread.Sleep(backoffMs * attempt); } catch { }
                        }
                    }
                    _logger.LogError("Failed to register per-session audio events for {SessionId} after {Attempts} attempts", sessionIdentifier, maxAttempts);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RegisterPerSessionEvents dynamic fallback failed");
            }

            // Fallback: try common registration method names via dynamic
            try
            {
                dynamic dyn = sessionControl;
                try { dyn.RegisterEventCallback(sink); } catch (Exception ex) { _logger.LogDebug(ex, "dyn.RegisterEventCallback failed"); }
                try { dyn.RegisterAudioSessionNotification(sink); } catch (Exception ex) { _logger.LogDebug(ex, "dyn.RegisterAudioSessionNotification failed"); }
                try { dyn.RegisterSessionNotification(sink); } catch (Exception ex) { _logger.LogDebug(ex, "dyn.RegisterSessionNotification failed"); }
                try { dyn.RegisterAudioSessionEvents(sink); } catch (Exception ex) { _logger.LogDebug(ex, "dyn.RegisterAudioSessionEvents failed"); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dynamic per-session registration methods failed for {SessionId}", sessionIdentifier);
            }

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
                    try
                    {
                        registered.NativeControl.UnregisterAudioSessionNotification(registered.Events);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while UnregisterAudioSessionNotification for {SessionId}", sessionIdentifier);
                    }

                    try
                    {
                        // Release underlying COM reference for native control
                        Marshal.ReleaseComObject(registered.NativeControl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "ReleaseComObject(native) failed for {SessionId}", sessionIdentifier);
                    }
                }

                if (registered.Events != null)
                {
                    try { Marshal.ReleaseComObject(registered.Events); } catch (Exception ex) { _logger.LogDebug(ex, "ReleaseComObject(events) failed for {SessionId}", sessionIdentifier); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fully unregister per-session events for {SessionId}", sessionIdentifier);
            }
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
                    parent._logger.LogInformation("OnSessionCreated called");
                    var comObj = Marshal.GetObjectForIUnknown(newSession);
                    if (comObj is AudioSessionControl sessionControl)
                    {
                        var info = CreateSessionInfo(sessionControl);
                        parent.knownSessions[info.SessionIdentifier] = info;
                        try
                        {
                            parent.RegisterPerSessionEvents(sessionControl, info.SessionIdentifier);
                        }
                        catch (Exception ex)
                        {
                            parent._logger.LogWarning(ex, "Failed to register per-session sink for {SessionId}", info.SessionIdentifier);
                        }

                        // add structured scope for this event
                        using (parent._logger.BeginScope(new System.Collections.Generic.Dictionary<string, object> { ["SessionId"] = info.SessionIdentifier, ["ServiceInstanceId"] = parent._serviceInstanceId }))
                        {
                            parent.SessionChanged?.Invoke(parent, new AudioSessionChangedEventArgs { Session = info, ChangeType = AudioSessionChangeType.Created });
                        }
                    }
                }
                catch (Exception ex)
                {
                    parent._logger.LogWarning(ex, "OnSessionCreated handler failed");
                }

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
                    parent._logger.LogInformation("OnSimpleVolumeChanged for {SessionId}: Volume={Volume:P0} Muted={Muted}", sessionId, NewVolume, NewMute);
                    var updated = parent.GetAudioSessions().FirstOrDefault(s => string.Equals(s.SessionIdentifier, sessionId, StringComparison.OrdinalIgnoreCase));
                    if (updated != null)
                    {
                        var change = AudioSessionChangeType.VolumeChanged;
                        if (updated.IsMuted != NewMute)
                            change = AudioSessionChangeType.MutedChanged;
                        parent.knownSessions[sessionId] = updated;
                        using (parent._logger.BeginScope(new System.Collections.Generic.Dictionary<string, object> { ["SessionId"] = sessionId, ["ServiceInstanceId"] = parent._serviceInstanceId }))
                        {
                            parent.SessionChanged?.Invoke(parent, new AudioSessionChangedEventArgs { Session = updated, ChangeType = change });
                        }
                    }
                }
                catch (Exception ex)
                {
                    parent._logger.LogWarning(ex, "OnSimpleVolumeChanged processing failed for {SessionId}", sessionId);
                }
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
