using System.Runtime.InteropServices;

namespace TheIsleOverlay.App;

public interface IGlobalShortcutNativeApi
{
    bool Register(IntPtr windowHandle, int id, ShortcutBinding binding);
    bool Unregister(IntPtr windowHandle, int id);
    int LastError { get; }
}

internal sealed class WindowsGlobalShortcutNativeApi : IGlobalShortcutNativeApi
{
    public int LastError => Marshal.GetLastWin32Error();

    public bool Register(IntPtr windowHandle, int id, ShortcutBinding binding) =>
        RegisterHotKey(windowHandle, id, binding.NativeModifiers, binding.VirtualKey);

    public bool Unregister(IntPtr windowHandle, int id) => UnregisterHotKey(windowHandle, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public sealed record ShortcutRegistrationFailure(
    OverlayShortcutAction Action,
    string Label,
    string Binding,
    int NativeError);

public sealed record ShortcutRegistrationResult(
    bool Success,
    OverlayShortcutSettings ActiveSettings,
    IReadOnlyList<ShortcutRegistrationFailure> Failures)
{
    public string FriendlyError => Failures.Count == 0
        ? string.Empty
        : string.Join(
            Environment.NewLine,
            Failures.Select(failure => failure.NativeError == 0
                ? $"{failure.Label}: {failure.Binding}"
                : $"{failure.Label}: {failure.Binding} đang bị game hoặc ứng dụng khác sử dụng."));
}

public static class OverlayInputSafetyPolicy
{
    // A global-hotkey collision must never make the full-screen overlay
    // interactive during startup.
    public static bool StartClickThrough(bool shortcutRegistrationSucceeded) => true;
}

public sealed class ShortcutRegistrationManager : IDisposable
{
    private readonly IGlobalShortcutNativeApi _nativeApi;
    private readonly IntPtr _windowHandle;
    private readonly bool _includeMapNotes;
    private readonly HashSet<int> _registeredIds = [];
    private bool _disposed;

    public ShortcutRegistrationManager(
        IntPtr windowHandle,
        bool includeMapNotes,
        IGlobalShortcutNativeApi? nativeApi = null)
    {
        _windowHandle = windowHandle;
        _includeMapNotes = includeMapNotes;
        _nativeApi = nativeApi ?? new WindowsGlobalShortcutNativeApi();
    }

    public OverlayShortcutSettings ActiveSettings { get; private set; } =
        OverlayShortcutSettings.Defaults;
    public bool HasCompleteRegistration { get; private set; }

    public ShortcutRegistrationResult RegisterInitial(OverlayShortcutSettings requested)
    {
        ThrowIfDisposed();
        UnregisterAll();
        var result = TryRegisterSet(requested);
        if (result.Success)
        {
            ActiveSettings = requested;
            return result;
        }

        if (requested != OverlayShortcutSettings.Defaults)
        {
            var fallback = TryRegisterSet(OverlayShortcutSettings.Defaults);
            if (fallback.Success)
            {
                ActiveSettings = OverlayShortcutSettings.Defaults;
            }
        }
        return result with { ActiveSettings = ActiveSettings };
    }

    /// <summary>
    /// Applies the complete set transactionally.  If any chord is unavailable,
    /// every new registration is removed and the previous working set is
    /// restored so the overlay is never left with only part of its controls.
    /// </summary>
    public ShortcutRegistrationResult TryApply(OverlayShortcutSettings requested)
    {
        ThrowIfDisposed();
        var previous = ActiveSettings;
        UnregisterAll();
        var requestedResult = TryRegisterSet(requested);
        if (requestedResult.Success)
        {
            ActiveSettings = requested;
            return requestedResult;
        }

        UnregisterAll();
        var rollbackResult = TryRegisterSet(previous);
        if (rollbackResult.Success)
        {
            ActiveSettings = previous;
        }
        return requestedResult with { ActiveSettings = ActiveSettings };
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private ShortcutRegistrationResult TryRegisterSet(OverlayShortcutSettings settings)
    {
        var validation = ShortcutSettingsManager.Validate(settings, _includeMapNotes);
        if (validation.Count > 0)
        {
            var invalid = validation.Select(message => new ShortcutRegistrationFailure(
                OverlayShortcutAction.EditMode,
                "Phím tắt không hợp lệ",
                message,
                0)).ToArray();
            return new(false, ActiveSettings, invalid);
        }

        var failures = new List<ShortcutRegistrationFailure>();
        foreach (var definition in ShortcutSettingsManager.Definitions(_includeMapNotes))
        {
            ShortcutBinding.TryParse(settings.For(definition.Action), out var binding, out _);
            if (_nativeApi.Register(_windowHandle, definition.Id, binding))
            {
                _registeredIds.Add(definition.Id);
                continue;
            }

            failures.Add(new ShortcutRegistrationFailure(
                definition.Action,
                definition.Label,
                binding.DisplayText,
                _nativeApi.LastError));
            break;
        }

        if (failures.Count == 0)
        {
            HasCompleteRegistration = true;
            return new(true, settings, []);
        }

        UnregisterAll();
        return new(false, ActiveSettings, failures);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds.ToArray())
        {
            _nativeApi.Unregister(_windowHandle, id);
        }
        _registeredIds.Clear();
        HasCompleteRegistration = false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
