using TheIsleOverlay.Core;

namespace TheIsleOverlay.ProClient;

public sealed record ProSkinEditorContext(
    string? Server,
    string Species,
    bool Female);

public sealed record ProGarageContext(
    string? Server,
    string? Species,
    double? Growth);

public sealed record ProFeatureCommandResult(bool Success, string? ErrorMessage = null);

public interface IProFeatureController
{
    Task<ProFeatureCommandResult> ToggleSkinEditorAsync(
        ProSkinEditorContext context,
        CancellationToken cancellationToken = default);

    Task<ProFeatureCommandResult> ToggleGarageAsync(
        ProGarageContext context,
        CancellationToken cancellationToken = default);
}

public interface IProRealtimeConnectionBridge
{
    void AttachRealtimeConnectionControl(IRealtimeConnectionControl control);
}
