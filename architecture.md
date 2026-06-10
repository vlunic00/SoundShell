# Project Specification: Windows Per-App Volume Mixer

## Objective
Build a native Windows utility that allows independent volume control for individual open windows/applications (e.g., lowering game volume while keeping YouTube loud).

## Tech Stack
- **Target OS:** Windows 10/11
- **Runtime:** .NET 8+ (Console PoC leading to WinUI 3 desktop application)
- **Language:** C#
- **Core Library:** NAudio (specifically wrapping WASAPI `IAudioSessionManager2` and `AudioSessionControl`)
- **Architecture Pattern:** MVVM (Model-View-ViewModel)

## Implementation Phases
1. **Phase 1 (Spike):** Create a .NET Console App to find a specific process (like 'chrome') and adjust its `SimpleAudioVolume` using NAudio.
2. **Phase 2 (Service):** Implement `WindowsAudioService` utilizing `IAudioSessionNotification` to react to OS audio session events without aggressive polling.

Implementation note (current): a lightweight event model was added to the audio library exposing session lifecycle and property-change events. To ensure broad compatibility while a COM-based notification wrapper is prepared, the current PoC implementation uses a short-interval (800ms) polling loop as a pragmatic fallback that raises events for session Created/Removed/VolumeChanged/MutedChanged. This is intentionally designed to be replaceable by a proper `IAudioSessionNotification` registration once a stable wrapper is implemented.
3. **Phase 3 (UI):** Build a WinUI 3 interface displaying process names, extracted icons, and volume sliders data-bound to the audio service.
4. **Phase 4 (System):** Minimize to System Tray and configure the app package with `runFullTrust` capability to bypass UWP sandboxing.

## Environment Note
This project is developed across multiple machines using VS Code and the .NET CLI. Keep dependencies strictly in Nuget and avoid absolute local file paths.

## Configuration
Configuration is provided via `appsettings.json` with optional environment-specific overrides (e.g. `appsettings.Development.json`) and environment variables. The PoC reads configuration on startup and binds the `Monitoring` section to the service `MonitoringOptions` so runtime behavior can be adjusted without recompilation.

- **Logging**: configure minimum level and path. Example keys: `Logging:MinimumLevel`, `Logging:Path`. Serilog is configured via the `Serilog` section in `appsettings.json`.
- **Monitoring**: tuning for retry/backoff and registration behavior. Example keys: `Monitoring:SessionRegistrationMaxAttempts`, `Monitoring:SessionRegistrationBackoffMs`, `Monitoring:PerSessionRegistrationMaxAttempts`, `Monitoring:PerSessionRegistrationBackoffMs`.
- **Features**: feature flags (e.g. `Features:EnablePollingFallback`) can be added to gate behavior for testing or compatibility.

Environment variables override config values. Useful variables already supported:
- `ASPNETCORE_ENVIRONMENT` — selects `appsettings.{ENV}.json` (e.g. `Development`).
- `SOUNDLOG_PATH` — (legacy) overrides the log directory. Prefer editing `appsettings.json` or `Serilog` configuration.
- `SOUNDLOG_LEVEL` — (legacy) overrides log level; prefer `Serilog:MinimumLevel` in config.

For production, keep configuration in `appsettings.json` and use environment-specific overrides or CI secrets to adjust values. Consider using `reloadOnChange` for `appsettings.json` in development to allow changing log levels without restarting the process.