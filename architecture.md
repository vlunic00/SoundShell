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