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
- MEGA cloud uploads using the MEGA account session
- MEGA backups are packaged as ZIP archives before upload
- The MEGA session token (not the account password) can be remembered using Windows DPAPI, scoped to the current Windows user
- Disconnecting removes the locally stored session token
- The MEGA integration uses the community-maintained MegaApiClient package, not an official MEGA SDK

## Scheduled backups

- Daily and weekly schedules can be created separately
- Default start time is 03:00; weekly schedules default to Monday
- Schedules are stored encrypted with Windows DPAPI under the current Windows user
- Windows Task Scheduler launches Quantum Core in background execution mode
- Scheduled tasks are configured for the current interactive Windows user; the user must be signed in and the PC must be awake at the scheduled time
- Scheduled execution logs are written to the QuantumCore folder under the current user's Local AppData directory

## Current scope and limitations

- Schedule management currently supports create and remove; editing an existing schedule is not yet included
- Scheduled jobs require source paths and destination folders to remain available
- MEGA scheduled jobs require a saved, valid MEGA session token for the same Windows user
- Restore, incremental backups, persistent backup history, missed-run recovery, and encrypted backup archives are future increments
- The MEGA client library is in passive maintenance; review its suitability before a production release

## Design direction

- Quantum Operations command-center layout
- Minimal information density
- Deep navy surfaces with electric-blue and neon-cyan accents
- Animated Quantum Core visual
