# Build SoundShell

## Requirements

- x64 Windows 10 version 1809 or later, or Windows 11
- .NET 10 SDK
- PowerShell and the .NET CLI on `PATH`

All application dependencies restore from NuGet. Visual Studio and a separately installed WinUI workload are not required for command-line builds.

## Build and Test

From the repository root:

```powershell
dotnet restore SoundShell.sln -p:Platform=x64
dotnet build SoundShell.sln --no-restore -p:Platform=x64
dotnet test SoundShell.sln --no-build --no-restore -p:Platform=x64
```

Run the diagnostic console client with:

```powershell
dotnet run --project src\SoundShell.PoC -- list
dotnet run --project src\SoundShell.PoC -- watch
```

## Create the MSIX

Create an unsigned package suitable for external signing:

```powershell
dotnet build src\SoundShell.App\SoundShell.App.csproj `
  -p:Platform=x64 `
  -p:Configuration=Release `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false
```

Packages are written under `src\SoundShell.App\AppPackages`. To sign during the build, provide a matching code-signing certificate through `PackageCertificateThumbprint` or `PackageCertificateKeyFile` and set `AppxPackageSigningEnabled=true`. Do not commit certificate private keys.

Install a signed developer package with its generated `Add-AppDevPackage.ps1`, or deploy the signed MSIX and the generated dependency packages through the chosen release channel.

## Local Data

The packaged application writes rolling logs and extracted-icon cache files under its local application-data directory. The MSIX installation directory remains read-only.
