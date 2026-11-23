using System.Management;

namespace QuantumCore.App;

public sealed class SystemMonitorSnapshot
{
    public string Cpu { get; init; } = "Unavailable";
    public string Memory { get; init; } = "Unavailable";
    public string Disk { get; init; } = "Unavailable";
    public string Windows { get; init; } = Environment.OSVersion.VersionString;
    public string Temperature { get; init; } = "Not reported by hardware";
    public double MemoryPercent { get; init; }
    public double DiskPercent { get; init; }
}

public static class SystemMonitorService
{
    public static Task<SystemMonitorSnapshot> ReadAsync() => Task.Run(Read);

    private static SystemMonitorSnapshot Read()
    {
        string cpu = "Unavailable", memory = "Unavailable", disk = "Unavailable";
        string temperature = "Not reported by hardware";
        double memoryPercent = 0, diskPercent = 0;
        string windows = Environment.OSVersion.VersionString;

        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject item in search.Get())
            {
                cpu = item["Name"]?.ToString()?.Trim() ?? cpu;
                break;
            }
        }
        catch { }

        try
        {
            using var search = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory, Caption FROM Win32_OperatingSystem");
            foreach (ManagementObject item in search.Get())
            {
                double total = Convert.ToDouble(item["TotalVisibleMemorySize"]) / 1024d / 1024d;
                double free = Convert.ToDouble(item["FreePhysicalMemory"]) / 1024d / 1024d;
                memoryPercent = total > 0 ? Math.Clamp((total - free) / total * 100d, 0, 100) : 0;
                memory = $"{total - free:0.0} / {total:0.0} GB used";
                windows = item["Caption"]?.ToString() ?? windows;
                break;
            }
        }
        catch { }

        try
        {
            using var search = new ManagementObjectSearcher("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3");
            var drives = new List<string>();
            foreach (ManagementObject item in search.Get())
            {
                if (item["Size"] is null || item["FreeSpace"] is null) continue;
                double free = Convert.ToDouble(item["FreeSpace"]) / 1024d / 1024d / 1024d;
                double total = Convert.ToDouble(item["Size"]) / 1024d / 1024d / 1024d;
                drives.Add($"{item["DeviceID"]}  {free:0.0} GB free / {total:0.0} GB");
                if (item["DeviceID"]?.ToString()?.Equals("C:", StringComparison.OrdinalIgnoreCase) == true)
                    diskPercent = total > 0 ? Math.Clamp((total - free) / total * 100d, 0, 100) : 0;
            }
            if (drives.Count > 0) disk = string.Join("     •     ", drives);
        }
        catch { }

        try
        {
            using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject item in search.Get())
            {
                double kelvinTenths = Convert.ToDouble(item["CurrentTemperature"]);
                double celsius = kelvinTenths / 10d - 273.15d;
                if (celsius is > 0 and < 120)
                {
                    temperature = $"{celsius:0} °C (ACPI sensor)";
                    break;
                }
            }
        }
        catch { }

        return new SystemMonitorSnapshot { Cpu = cpu, Memory = memory, Disk = disk, Windows = windows, Temperature = temperature, MemoryPercent = memoryPercent, DiskPercent = diskPercent };
    }
}
