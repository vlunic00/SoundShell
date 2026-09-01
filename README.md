# SoundShell

SoundShell is a native Windows 10/11 per-application volume mixer. It groups active audio sessions by executable name, so one volume slider and mute control manage every session from an application.

## Features

- Live Windows audio-session discovery and monitoring via WASAPI
- Grouped application rows with icons, volume sliders, mute controls, and mixed-value states
- System-tray support, including a persisted close-to-tray preference
- A command-line diagnostic client for listing, watching, and changing sessions
- Framework-dependent x64 MSIX packaging

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- .NET 10 SDK

## Build and test

```powershell
dotnet restore SoundShell.sln -p:Platform=x64
dotnet build SoundShell.sln --no-restore -p:Platform=x64
dotnet test SoundShell.sln --no-build --no-restore -p:Platform=x64
```

## Run the desktop app

`SoundShell.App` is an MSIX-packaged WinUI application and must be deployed before it can be launched. `dotnet run --project src\SoundShell.App` runs a loose executable and is not supported.

For development, enable Windows Developer Mode, build the app, register its generated manifest, then launch the registered package:

```powershell
dotnet build src\SoundShell.App -p:Platform=x64
Add-AppxPackage -Register "$PWD\src\SoundShell.App\bin\x64\Debug\net10.0-windows10.0.19041.0\AppxManifest.xml"
$package = Get-AppxPackage -Name SoundShell
Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
```

For distribution, create and install a signed MSIX package as described in [BUILD.md](BUILD.md).

Run the diagnostic client:

```powershell
dotnet run --project src\SoundShell.PoC -- list
dotnet run --project src\SoundShell.PoC -- watch
```

See [BUILD.md](BUILD.md) for MSIX packaging and signing guidance, and [architecture.md](architecture.md) for the component design and deferred roadmap.

## Roadmap

Potential next features include startup at login, automatic updates, output-device selection, saved per-application profiles, and x86/ARM64 packages.
