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
    private const int PcapErrorBufferSize = 256;
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

        foreach (var directory in runtimeDirectories)
        {
            var result = ProbeNativeDirectory(directory);
            if (result.Status != NpcapAvailabilityStatus.RuntimeFilesMissing)
            {
                return result;
            }
        }

        return Unavailable(
            NpcapAvailabilityStatus.NativeLibraryLoadFailed,
            "Npcap đã có trên máy nhưng Windows chưa tải được thư viện capture. Hãy kiểm tra quyền hoặc cài lại Npcap.");
    }

    public static bool HasRuntimeFiles() =>
        GetRuntimeDirectories().Any(HasRuntimeFilesInDirectory);

    internal static bool HasRuntimeFiles(string systemDirectory) =>
        HasRuntimeFilesInDirectory(Path.Combine(systemDirectory, "Npcap"));

    private static bool HasRuntimeFilesInDirectory(string npcapDirectory) =>
        File.Exists(Path.Combine(npcapDirectory, "wpcap.dll"))
        && File.Exists(Path.Combine(npcapDirectory, "Packet.dll"));

    private static NpcapAvailability ProbeNativeDirectory(string directory)
    {
        if (!HasRuntimeFilesInDirectory(directory))
        {
            return Unavailable(
                NpcapAvailabilityStatus.RuntimeFilesMissing,
                "Npcap runtime files are not present in this directory.");
        }

        nint packetHandle = nint.Zero;
        nint pcapHandle = nint.Zero;
        nint devices = nint.Zero;
        nint errorBuffer = nint.Zero;
        try
        {
            // Load Packet first because wpcap depends on it. This probe uses
            // native exports directly and therefore cannot poison SharpPcap's
            // process-wide type initializer when Npcap is installed while the
            // app is already open.
            if (!NativeLibrary.TryLoad(Path.Combine(directory, "Packet.dll"), out packetHandle)
                || !NativeLibrary.TryLoad(Path.Combine(directory, "wpcap.dll"), out pcapHandle))
            {
                return Unavailable(
                    NpcapAvailabilityStatus.RuntimeFilesMissing,
                    "Npcap runtime files in this directory could not be loaded.");
            }

            var findPointer = NativeLibrary.GetExport(pcapHandle, "pcap_findalldevs");
            var freePointer = NativeLibrary.GetExport(pcapHandle, "pcap_freealldevs");
            var findAllDevices = Marshal.GetDelegateForFunctionPointer<PcapFindAllDevices>(findPointer);
            var freeAllDevices = Marshal.GetDelegateForFunctionPointer<PcapFreeAllDevices>(freePointer);
            errorBuffer = Marshal.AllocHGlobal(PcapErrorBufferSize);
            Marshal.Copy(new byte[PcapErrorBufferSize], 0, errorBuffer, PcapErrorBufferSize);

            var status = findAllDevices(out devices, errorBuffer);
            if (status == 0 && devices != nint.Zero)
            {
                freeAllDevices(devices);
                devices = nint.Zero;
                return new NpcapAvailability(true, null, NpcapAvailabilityStatus.Ready);
            }

            var nativeError = Marshal.PtrToStringAnsi(errorBuffer)?.Trim();
            if (!string.IsNullOrWhiteSpace(nativeError)
                && (nativeError.Contains("access", StringComparison.OrdinalIgnoreCase)
                    || nativeError.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    || nativeError.Contains("administrator", StringComparison.OrdinalIgnoreCase)))
            {
                return Unavailable(
                    NpcapAvailabilityStatus.AccessDenied,
                    "Npcap đang giới hạn quyền truy cập. Hãy chạy Isle Live Map bằng quyền quản trị hoặc cài lại Npcap với tùy chọn không giới hạn Administrators.");
            }

            return status == 0
                ? Unavailable(
                    NpcapAvailabilityStatus.NoCaptureDevices,
                    "Npcap đã tải được nhưng Windows chưa trả về adapter mạng nào. Hãy bật Wi‑Fi/Ethernet rồi thử lại.")
                : Unavailable(
                    NpcapAvailabilityStatus.ProbeFailed,
                    string.IsNullOrWhiteSpace(nativeError)
                        ? "Npcap đã được phát hiện nhưng không thể đọc danh sách adapter. Hãy kiểm tra quyền truy cập."
                        : $"Npcap không thể đọc adapter: {nativeError}");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or BadImageFormatException
                or EntryPointNotFoundException
                or MarshalDirectiveException)
        {
            return Unavailable(
                NpcapAvailabilityStatus.RuntimeFilesMissing,
                "Npcap runtime in this directory is not usable.");
        }
        catch
        {
            return Unavailable(
                NpcapAvailabilityStatus.ProbeFailed,
                "Không thể kiểm tra adapter của Npcap. Hãy kiểm tra quyền truy cập hoặc cài lại Npcap.");
        }
        finally
        {
            if (devices != nint.Zero && pcapHandle != nint.Zero)
            {
                try
                {
                    var freePointer = NativeLibrary.GetExport(pcapHandle, "pcap_freealldevs");
                    Marshal.GetDelegateForFunctionPointer<PcapFreeAllDevices>(freePointer)(devices);
                }
                catch
                {
                }
            }

            if (errorBuffer != nint.Zero)
            {
                Marshal.FreeHGlobal(errorBuffer);
            }

            if (pcapHandle != nint.Zero)
            {
                NativeLibrary.Free(pcapHandle);
            }

            if (packetHandle != nint.Zero)
            {
                NativeLibrary.Free(packetHandle);
            }
        }
    }

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PcapFindAllDevices(out nint devices, nint errorBuffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PcapFreeAllDevices(nint devices);
}
