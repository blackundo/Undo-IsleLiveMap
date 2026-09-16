namespace TheIsleOverlay.ProClient;

public sealed record ProSkinEditorContext(
    string? Server,
    string Species,
    bool Female);

public sealed record ProGarageContext(
    string? Server,
    string? Species,
    double? Growth);

public interface IProFeatureController
{
    bool TryToggleSkinEditor(ProSkinEditorContext context);

    bool TryToggleGarage(ProGarageContext context);
}
