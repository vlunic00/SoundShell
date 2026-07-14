# SoundShell Architecture

## Objective

SoundShell is a native Windows 10/11 per-application volume mixer. It groups active render sessions by executable name so one slider and mute control manage every session belonging to an application.

## Stack

- .NET 10 and C#
- NAudio/WASAPI for Windows audio sessions
- WinUI 3 and Windows App SDK 1.8.6
- MVVM using `INotifyPropertyChanged` and `ObservableCollection`
- x64 MSIX packaging

## Completed Phases

1. **Console spike:** enumerate sessions and set volume/mute from the command line.
2. **Audio service:** use NAudio's native `OnSessionCreated` and per-session event clients for lifecycle, volume, and mute changes. An 800 ms polling loop is retained only when native monitoring cannot initialize.
3. **Desktop mixer:** show executable icons, process-name groups, volume sliders, mute controls, empty/error states, and mixed values. Changing a mixed row synchronizes every underlying session instance.
4. **Desktop lifecycle:** hide minimized windows to the system tray, provide Show/Exit actions, and persist a default-on Close-to-tray preference.

## Components

- `SoundShell.Audio` owns WASAPI enumeration, monitoring, session-instance identity, and volume/mute operations.
- `SoundShell.PoC` remains the diagnostic command-line client.
- `SoundShell.App` owns grouping, icon caching, WinUI presentation, tray behavior, local settings, and MSIX packaging.

The service exposes individual session instances. Grouping stays in the desktop view model because it is presentation behavior rather than an audio-system primitive.

## Packaging and Trust

The packaged app runs as a normal medium-integrity desktop process. MSIX requires the `runFullTrust` manifest declaration for that process model; it does not request administrator elevation. This is necessary for WASAPI access, executable icon inspection, and the Win32 notification-area icon.

The package is framework-dependent, targets x64, and is unsigned by default. Release or developer builds supply signing credentials externally; private keys are never stored in the repository. Logs and cached icons are written beneath the package's local application-data folders rather than the read-only install directory.

## Configuration

`Monitoring:EnablePollingFallback` enables the compatibility fallback and `Monitoring:PollingIntervalMs` sets its interval. Serilog's minimum level is read from `appsettings.json`; packaged-app logs are written to local application data.

The PoC additionally supports `ASPNETCORE_ENVIRONMENT` for environment-specific configuration overrides.

## Deferred

- Startup at login and automatic updates
- Multiple output-device selection
- Per-window mapping where Windows exposes only process/session identity
- Saved per-application volume profiles
- x86 and ARM64 packages
