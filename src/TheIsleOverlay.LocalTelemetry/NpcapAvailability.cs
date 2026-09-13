using System.Runtime.InteropServices;
using SharpPcap;

namespace TheIsleOverlay.LocalTelemetry;

public enum NpcapAvailabilityStatus
{
    Unknown,
    Ready,
    RuntimeFilesMissing,
    NativeLibraryLoadFailed,
    AccessDenied,
    NoCaptureDevices,
    ProbeFailed
}

public readonly record struct NpcapAvailability(
    bool IsAvailable,
    string? ErrorMessage = null,
    NpcapAvailabilityStatus Status = NpcapAvailabilityStatus.Unknown);

public static class NpcapAvailabilityProbe
{
    private static readonly string[] NativeLibraryNames = ["wpcap", "Packet"];

    public static NpcapAvailability Check(bool refresh = false)
    {
        _ = refresh;
        // Always create a fresh device list. CaptureDeviceList.Instance is a
        // process-wide singleton; if it was initialized before Npcap was
        // installed, a later Refresh can retain a failed native initialization
        // for the rest of the process.
        var runtimeDirectories = GetRuntimeDirectories();
        if (!runtimeDirectories.Any(HasRuntimeFilesInDirectory))
        {
            return Unavailable(
                NpcapAvailabilityStatus.RuntimeFilesMissing,
                "Windows có thể đang chạy dịch vụ Npcap nhưng thiếu wpcap.dll/Packet.dll. Hãy cài lại Npcap chính thức từ NPCAP.COM.");
        }

        EnsureNativeLibraryResolver();
        try
        {
            var devices = CaptureDeviceList.New();
            return devices.Count > 0
                ? new NpcapAvailability(true, null, NpcapAvailabilityStatus.Ready)
                : Unavailable(
                    NpcapAvailabilityStatus.NoCaptureDevices,
                    "Npcap đã tải được nhưng Windows chưa trả về adapter mạng nào. Hãy bật Wi‑Fi/Ethernet rồi thử lại.");
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(
                NpcapAvailabilityStatus.AccessDenied,
                "Npcap đang giới hạn quyền truy cập. Hãy chạy Isle Live Map bằng quyền quản trị hoặc cài lại Npcap với tùy chọn không giới hạn Administrators.");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or BadImageFormatException
                or TypeInitializationException)
        {
            return Unavailable(
                NpcapAvailabilityStatus.NativeLibraryLoadFailed,
                "Npcap đã có trên máy nhưng app chưa tải được thư viện native. Hãy đóng và mở lại Isle Live Map sau khi cài đặt.");
        }
        catch (PcapException)
        {
            return Unavailable(
                NpcapAvailabilityStatus.ProbeFailed,
                "Npcap đã được phát hiện nhưng không thể enumerate adapter. Hãy kiểm tra quyền truy cập và thử mở lại app.");
        }
        catch (Exception)
        {
            return Unavailable(
                NpcapAvailabilityStatus.ProbeFailed,
                "Không thể kiểm tra adapter của Npcap. Hãy đóng và mở lại Isle Live Map rồi thử lại.");
        }
    }

    public static bool HasRuntimeFiles() =>
        GetRuntimeDirectories().Any(HasRuntimeFilesInDirectory);

    internal static bool HasRuntimeFiles(string systemDirectory) =>
        HasRuntimeFilesInDirectory(Path.Combine(systemDirectory, "Npcap"));

    private static bool HasRuntimeFilesInDirectory(string npcapDirectory) =>
        File.Exists(Path.Combine(npcapDirectory, "wpcap.dll"))
        && File.Exists(Path.Combine(npcapDirectory, "Packet.dll"));

    private static IReadOnlyList<string> GetRuntimeDirectories()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new[]
        {
            Path.Combine(systemDirectory, "Npcap"),
            Path.Combine(windowsDirectory, "System32", "Npcap"),
            Path.Combine(windowsDirectory, "SysWOW64", "Npcap")
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void EnsureNativeLibraryResolver()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(CaptureDeviceList).Assembly,
                ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // The resolver is process-global and may already be installed by
            // another probe. The existing resolver remains valid.
        }
    }

    private static nint ResolveNativeLibrary(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!NativeLibraryNames.Any(name =>
                string.Equals(name, libraryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"{name}.dll", libraryName, StringComparison.OrdinalIgnoreCase)))
        {
            return nint.Zero;
        }

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : $"{libraryName}.dll";
        foreach (var directory in GetRuntimeDirectories())
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    private static NpcapAvailability Unavailable(
        NpcapAvailabilityStatus status,
        string message) => new(false, message, status);
}
