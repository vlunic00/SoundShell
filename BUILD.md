# Build Instructions for SoundShell Backend PoC

## Required Environment

- Windows 10 or later
- .NET SDK 5.0 installed (`net5.0-windows` target)
- `dotnet` CLI available on the PATH

## Dependencies

Dependencies are restored automatically from NuGet. The current backend PoC relies on:
- `NAudio` (NuGet package)

## Build Steps

1. Open a terminal in the repository root:
   ```powershell
   cd "C:\Users\vedro\Desktop\Coding\SoundShell"
   ```
2. Restore NuGet packages and build the solution:
   ```powershell
   dotnet build
   ```

## Run the Proof of Concept

From the `src/SoundShell.PoC` directory, run:

```powershell
cd src\SoundShell.PoC
dotnet run -- list
```

This will enumerate the current audio sessions on the system.

## Notes

- The project currently targets `net5.0-windows` because of the installed SDK in this environment.
- If a newer Windows-capable .NET SDK is available, update the project file target frameworks accordingly.
- Do not include the `.github/` prompt files or temporary inspection projects in this commit unless they are explicitly required for the build.
