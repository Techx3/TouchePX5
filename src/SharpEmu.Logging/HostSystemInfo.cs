// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SharpEmu.Logging;

/// <summary>Provides best-effort information about the host system.</summary>
public static class HostSystemInfo
{
    private static readonly Lazy<string> CpuNameValue = new(GetCpuName);
    private static readonly Lazy<IReadOnlyList<string>> GpuNamesValue = new(GetGpuNames);
    private static readonly Lazy<string> GpuNameValue = new(GetPreferredGpuName);
    private static readonly Lazy<string> MemoryDescriptionValue = new(GetMemoryDescription);

    /// <summary>Host CPU name, or a safe fallback when it cannot be determined.</summary>
    public static string CpuName => CpuNameValue.Value;

    /// <summary>Preferred physical GPU name, or a safe fallback when it cannot be determined.</summary>
    public static string GpuName => GpuNameValue.Value;

    /// <summary>Physical display adapters visible to the host, in enumeration order.</summary>
    public static IReadOnlyList<string> GpuNames => GpuNamesValue.Value;

    /// <summary>
    /// Finds the 64-bit Vulkan ICD manifest installed with a Windows display
    /// driver when the system Vulkan registry entry is missing.
    /// </summary>
    public static string? FindVulkanDriverManifest(string? gpuName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(gpuName))
        {
            return null;
        }

        var manifestName = gpuName.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
            gpuName.Contains("geforce", StringComparison.OrdinalIgnoreCase)
                ? "nv-vk64.json"
                : null;
        if (manifestName is null)
        {
            return null;
        }

        try
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var driverStore = Path.Combine(
                windowsDirectory,
                "System32",
                "DriverStore",
                "FileRepository");
            return Directory
                .EnumerateFiles(driverStore, manifestName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // The selector remains usable through the system Vulkan registry.
            return null;
        }
    }

    /// <summary>Returns a concise description of the host hardware for diagnostic logs.</summary>
    public static string Summary =>
        $"Host hardware: CPU: {CpuName}; GPU: {GpuName}; RAM: {MemoryDescriptionValue.Value}.";

    private static string GetCpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return $"{Environment.ProcessorCount} logical processors";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                name = Regex.Replace(
                    name.Trim(),
                    @"\s+\d+-Core Processor$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return Regex.Replace(
                    name,
                    @"\s+with Radeon Graphics$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
        }

        return $"{Environment.ProcessorCount} logical processors";
    }

    private static string GetPreferredGpuName()
    {
        var preferredName = "unknown";
        var preferredScore = int.MinValue;
        foreach (var name in GpuNames)
        {
            var score = ScoreGpu(name);
            if (score > preferredScore)
            {
                preferredName = name;
                preferredScore = score;
            }
        }

        return preferredScore > 0 ? preferredName : "unknown";
    }

    private static IReadOnlyList<string> GetGpuNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var names = new List<string>();
            for (uint index = 0; ; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>(),
                };

                if (!EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                var name = device.DeviceString?.Trim();
                if (ScoreGpu(name) > 0 &&
                    !names.Contains(name!, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name!);
                }
            }

            return names;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static int ScoreGpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("basic display", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("geforce", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("radeon", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private static string GetMemoryDescription()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx
                {
                    dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
                };
                if (GlobalMemoryStatusEx(ref status))
                {
                    var megabytes = status.ullTotalPhys / (1024 * 1024);
                    var gigabytes = status.ullTotalPhys / (1024d * 1024 * 1024);
                    return $"{megabytes:N0} MB ({gigabytes:N1} GB)";
                }
            }
            catch (Exception)
            {
                // Hardware information is diagnostic only.
            }
        }

        return "unknown";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint deviceNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
