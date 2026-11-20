# Quantum Core

Quantum Core is a Windows desktop backup application, designed around the Quantum Operations visual concept.

## Platform

- Windows 10 (version 1809 or later) and Windows 11
- .NET 10
- WPF (Windows Presentation Foundation)

## Project structure

```
src/
  QuantumCore.App/
    App.xaml
    MainWindow.xaml
    QuantumCore.App.csproj
```

## Build and run

Install the .NET 10 SDK on a Windows development machine, then run:

```powershell
dotnet build .\src\QuantumCore.App\QuantumCore.App.csproj
dotnet run --project .\src\QuantumCore.App\QuantumCore.App.csproj
```

The first pull request establishes the desktop UI foundation only. Backup and restore operations, scheduling, persistence, and destination management will be implemented in subsequent increments.

## Design direction

- Quantum Operations command-center layout
- Minimal information density
- Deep navy surfaces with electric-blue and neon-cyan accents
- Animated Quantum Core visual
