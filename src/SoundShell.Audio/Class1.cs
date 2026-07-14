using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoundShell.Audio;

public sealed class AudioSessionInfo
{
    public string SessionIdentifier { get; init; }
    public string SessionInstanceIdentifier { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; }
    public string ExecutablePath { get; init; }
    public string DisplayName { get; init; }
    public float Volume { get; init; }
    public bool IsMuted { get; init; }
    public bool IsSystemSounds { get; init; }

    public override string ToString()
        => $"[{SessionInstanceIdentifier}] {ProcessName} (PID={ProcessId}) {DisplayName} Volume={Volume:P0} Muted={IsMuted} SystemSounds={IsSystemSounds}";
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
    void SetSessionVolume(string sessionInstanceIdentifier, float volume);
    void SetSessionMute(string sessionInstanceIdentifier, bool isMuted);
    event EventHandler<AudioSessionChangedEventArgs> SessionChanged;
    void StartMonitoring();
    void StopMonitoring();
    AudioSessionInfo FindSessionByProcessName(string processName);
}

public class WindowsAudioSessionService : IAudioSessionService
{
    public sealed class MonitoringOptions
    {
        public static MonitoringOptions Default => new();
        public bool EnablePollingFallback { get; set; } = true;
        public int PollingIntervalMs { get; set; } = 800;
    }

    private sealed class RegisteredSession
    {
        public AudioSessionControl Control { get; init; }
        public SessionEventsHandler Handler { get; init; }
    }

    private sealed class SessionEventsHandler : IAudioSessionEventsHandler
    {
        private readonly WindowsAudioSessionService service;
        private readonly string sessionInstanceIdentifier;

        public SessionEventsHandler(WindowsAudioSessionService service, string sessionInstanceIdentifier)
        {
            this.service = service;
            this.sessionInstanceIdentifier = sessionInstanceIdentifier;
        }

        public void OnVolumeChanged(float volume, bool isMuted)
            => service.UpdateVolume(sessionInstanceIdentifier, volume, isMuted);

        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
                service.RemoveSession(sessionInstanceIdentifier);
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            => service.RemoveSession(sessionInstanceIdentifier);

        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
    }

    private readonly ILogger<WindowsAudioSessionService> logger;
    private readonly MonitoringOptions options;
    private readonly string serviceInstanceId = Guid.NewGuid().ToString();
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly object monitorLock = new();
    private readonly ConcurrentDictionary<string, AudioSessionInfo> knownSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RegisteredSession> registeredSessions = new(StringComparer.OrdinalIgnoreCase);
    private MMDevice monitoringDevice;
    private AudioSessionManager monitoringManager;
    private Timer pollingTimer;
    private int pollingInProgress;
    private bool monitoringStarted;
    private bool disposed;

    public WindowsAudioSessionService()
        : this(NullLogger<WindowsAudioSessionService>.Instance, MonitoringOptions.Default) { }

    public WindowsAudioSessionService(ILogger<WindowsAudioSessionService> logger)
        : this(logger, MonitoringOptions.Default) { }

    public WindowsAudioSessionService(ILogger<WindowsAudioSessionService> logger, MonitoringOptions options)
    {
        this.logger = logger ?? NullLogger<WindowsAudioSessionService>.Instance;
        this.options = options ?? MonitoringOptions.Default;
    }

    public event EventHandler<AudioSessionChangedEventArgs> SessionChanged;

    public IReadOnlyList<AudioSessionInfo> GetAudioSessions() => FetchAudioSessions();

    protected virtual IReadOnlyList<AudioSessionInfo> FetchAudioSessions()
    {
        var sessions = new List<AudioSessionInfo>();
        using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var enumerator = device.AudioSessionManager.Sessions;

        for (var i = 0; i < enumerator.Count; i++)
        {
            using var control = enumerator[i];
            if (control != null)
                sessions.Add(CreateSessionInfo(control));
        }

        return sessions;
    }

    public void StartMonitoring()
    {
        lock (monitorLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (monitoringStarted)
                return;

            monitoringStarted = true;
            try
            {
                monitoringDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                monitoringManager = monitoringDevice.AudioSessionManager;
                monitoringManager.OnSessionCreated += OnSessionCreated;

                var sessions = monitoringManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                    RegisterSession(sessions[i], false);

                logger.LogInformation("Started native audio session monitoring");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Native audio session monitoring failed");
                StopNativeMonitoring();
                if (options.EnablePollingFallback)
                    StartPolling();
            }
        }
    }

    public void StopMonitoring()
    {
        lock (monitorLock)
        {
            if (!monitoringStarted)
                return;

            monitoringStarted = false;
            StopPolling();
            StopNativeMonitoring();
            knownSessions.Clear();
        }
    }

    public AudioSessionInfo FindSessionByProcessName(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        return GetAudioSessions().FirstOrDefault(session =>
            string.Equals(session.ProcessName, processName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void SetSessionVolume(string sessionInstanceIdentifier, float volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionInstanceIdentifier);
        if (volume is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0.0 and 1.0.");

        using var session = FindSessionControl(sessionInstanceIdentifier);
        session.SimpleAudioVolume.Volume = volume;
    }

    public void SetSessionMute(string sessionInstanceIdentifier, bool isMuted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionInstanceIdentifier);
        using var session = FindSessionControl(sessionInstanceIdentifier);
        session.SimpleAudioVolume.Mute = isMuted;
    }

    private void OnSessionCreated(object sender, IAudioSessionControl newSession)
    {
        try
        {
            RegisterSession(new AudioSessionControl(newSession), true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to register a newly created audio session");
        }
    }

    private void RegisterSession(AudioSessionControl control, bool raiseCreated)
    {
        if (control == null)
            return;

        try
        {
            var info = CreateSessionInfo(control);
            var id = info.SessionInstanceIdentifier;
            if (string.IsNullOrWhiteSpace(id) || registeredSessions.ContainsKey(id))
            {
                control.Dispose();
                return;
            }

            var handler = new SessionEventsHandler(this, id);
            control.RegisterEventClient(handler);
            if (!registeredSessions.TryAdd(id, new RegisteredSession { Control = control, Handler = handler }))
            {
                control.UnRegisterEventClient(handler);
                control.Dispose();
                return;
            }

            knownSessions[id] = info;
            if (raiseCreated)
                RaiseSessionChanged(info, AudioSessionChangeType.Created);
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    private void StopNativeMonitoring()
    {
        if (monitoringManager != null)
            monitoringManager.OnSessionCreated -= OnSessionCreated;

        foreach (var id in registeredSessions.Keys.ToArray())
            DisposeRegistration(id);

        monitoringManager?.Dispose();
        monitoringManager = null;
        monitoringDevice?.Dispose();
        monitoringDevice = null;
    }

    private void DisposeRegistration(string sessionInstanceIdentifier)
    {
        if (!registeredSessions.TryRemove(sessionInstanceIdentifier, out var registration))
            return;

        try
        {
            registration.Control.UnRegisterEventClient(registration.Handler);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to unregister events for {SessionId}", sessionInstanceIdentifier);
        }
        registration.Control.Dispose();
    }

    private void UpdateVolume(string sessionInstanceIdentifier, float volume, bool isMuted)
    {
        if (!knownSessions.TryGetValue(sessionInstanceIdentifier, out var current))
            return;

        var updated = Copy(current, volume, isMuted);
        knownSessions[sessionInstanceIdentifier] = updated;
        var changeType = current.IsMuted != isMuted
            ? AudioSessionChangeType.MutedChanged
            : AudioSessionChangeType.VolumeChanged;
        RaiseSessionChanged(updated, changeType);
    }

    private void RemoveSession(string sessionInstanceIdentifier)
    {
        if (!knownSessions.TryRemove(sessionInstanceIdentifier, out var removed))
            return;

        DisposeRegistration(sessionInstanceIdentifier);
        RaiseSessionChanged(removed, AudioSessionChangeType.Removed);
    }

    private void StartPolling()
    {
        if (pollingTimer != null)
            return;

        var interval = options.PollingIntervalMs > 0 ? options.PollingIntervalMs : 800;
        logger.LogInformation("Starting polling fallback at {IntervalMs}ms", interval);
        pollingTimer = new Timer(_ => PollSessions(), null, 0, interval);
    }

    private void StopPolling()
    {
        var timer = Interlocked.Exchange(ref pollingTimer, null);
        timer?.Dispose();
    }

    private void PollSessions()
    {
        if (Interlocked.Exchange(ref pollingInProgress, 1) == 1)
            return;

        try
        {
            ApplySnapshot(GetAudioSessions());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Polling audio sessions failed");
        }
        finally
        {
            Interlocked.Exchange(ref pollingInProgress, 0);
        }
    }

    internal void PollSessionsInternal() => PollSessions();

    internal void ApplySnapshot(IReadOnlyList<AudioSessionInfo> current)
    {
        var currentById = current
            .Where(session => !string.IsNullOrWhiteSpace(session.SessionInstanceIdentifier))
            .ToDictionary(session => session.SessionInstanceIdentifier, StringComparer.OrdinalIgnoreCase);

        foreach (var (id, session) in currentById)
        {
            if (knownSessions.TryAdd(id, session))
            {
                RaiseSessionChanged(session, AudioSessionChangeType.Created);
                continue;
            }

            var previous = knownSessions[id];
            if (Math.Abs(previous.Volume - session.Volume) > 0.0001f || previous.IsMuted != session.IsMuted)
            {
                knownSessions[id] = session;
                RaiseSessionChanged(session, previous.IsMuted != session.IsMuted
                    ? AudioSessionChangeType.MutedChanged
                    : AudioSessionChangeType.VolumeChanged);
            }
        }

        foreach (var id in knownSessions.Keys.Except(currentById.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            RemoveSession(id);
    }

    private void RaiseSessionChanged(AudioSessionInfo session, AudioSessionChangeType changeType)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = session.SessionInstanceIdentifier,
            ["ServiceInstanceId"] = serviceInstanceId
        }))
        {
            SessionChanged?.Invoke(this, new AudioSessionChangedEventArgs { Session = session, ChangeType = changeType });
        }
    }

    private AudioSessionControl FindSessionControl(string sessionInstanceIdentifier)
    {
        using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (var i = 0; i < sessions.Count; i++)
        {
            var control = sessions[i];
            if (control == null)
                continue;
            if (string.Equals(control.GetSessionInstanceIdentifier, sessionInstanceIdentifier, StringComparison.OrdinalIgnoreCase))
                return control;
            control.Dispose();
        }

        throw new InvalidOperationException($"Audio session instance '{sessionInstanceIdentifier}' not found.");
    }

    private static AudioSessionInfo CreateSessionInfo(AudioSessionControl session)
    {
        var processId = (int)session.GetProcessID;
        var process = GetProcessInfo(processId);
        return new AudioSessionInfo
        {
            SessionIdentifier = session.GetSessionIdentifier ?? string.Empty,
            SessionInstanceIdentifier = session.GetSessionInstanceIdentifier ?? string.Empty,
            ProcessId = processId,
            ProcessName = process.Name,
            ExecutablePath = process.Path,
            DisplayName = string.IsNullOrWhiteSpace(session.DisplayName)
                ? session.GetSessionIdentifier ?? string.Empty
                : session.DisplayName,
            Volume = session.SimpleAudioVolume.Volume,
            IsMuted = session.SimpleAudioVolume.Mute,
            IsSystemSounds = session.IsSystemSoundsSession
        };
    }

    private static AudioSessionInfo Copy(AudioSessionInfo session, float volume, bool isMuted) => new()
    {
        SessionIdentifier = session.SessionIdentifier,
        SessionInstanceIdentifier = session.SessionInstanceIdentifier,
        ProcessId = session.ProcessId,
        ProcessName = session.ProcessName,
        ExecutablePath = session.ExecutablePath,
        DisplayName = session.DisplayName,
        Volume = volume,
        IsMuted = isMuted,
        IsSystemSounds = session.IsSystemSounds
    };

    private static (string Name, string Path) GetProcessInfo(int processId)
    {
        if (processId <= 0)
            return ("System Sounds", null);

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = string.IsNullOrWhiteSpace(process.ProcessName) ? processId.ToString() : process.ProcessName;
            string path = null;
            try { path = process.MainModule?.FileName; } catch (Exception ex) { Trace.WriteLine($"Executable path lookup failed for PID {processId}: {ex.Message}"); }
            return (name, path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"GetProcessName failed for PID {processId}: {ex.Message}");
            return (processId.ToString(), null);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        StopMonitoring();
        disposed = true;
        deviceEnumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
