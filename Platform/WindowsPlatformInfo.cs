using System;

namespace WindowsNotch.Platform;

public static class WindowsPlatformInfo
{
    public static bool IsWindows11OrGreater()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    }

    public static bool IsSupportedWindows10OrGreater()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
    }

    public static string GetEnvironmentSummary()
    {
        string osLabel = IsWindows11OrGreater() ? "Windows 11" : "Windows 10";
        return $"Foundation Ready · {osLabel} ({Environment.OSVersion.Version})";
    }
}
