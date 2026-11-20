# Quantum Core

Quantum Core is a Windows desktop backup application, designed around the Quantum Operations visual concept.

## Platform

- Windows 10 (version 1809 or later) and Windows 11
- .NET 10
- WPF (Windows Presentation Foundation)

## Build and run

Install the .NET 10 SDK on a Windows development machine, then run:

```powershell
dotnet build .\src\QuantumCore.App\QuantumCore.App.csproj
dotnet run --project .\src\QuantumCore.App\QuantumCore.App.csproj
```

## Backup destinations

- Local folder backups
- MEGA cloud uploads using an MEGA account sign-in
- MEGA backups are packaged as a ZIP archive before upload
- MEGA credentials are requested for each upload and are not written to disk by Quantum Core
- The app reports cloud completion only after the upload operation succeeds

The MEGA integration uses the MIT-licensed MegaApiClient package. The package is community-maintained and is not an official MEGA SDK.

## Current scope

Backup history is session-only. Restore, scheduling, persistent history, destination management, resumable uploads, and saved encrypted sessions are future increments.

## Design direction

- Quantum Operations command-center layout
- Minimal information density
- Deep navy surfaces with electric-blue and neon-cyan accents
- Animated Quantum Core visual
