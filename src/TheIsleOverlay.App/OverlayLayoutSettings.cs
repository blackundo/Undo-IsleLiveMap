using System.IO;
using System.Text.Json;

namespace TheIsleOverlay.App;

public sealed record OverlayLayoutSettings
{
    public int Version { get; init; } = OverlayLayoutRules.CurrentVersion;
    public double Scale { get; init; } = OverlayLayoutRules.DefaultScale;
    public string MapShape { get; init; } = OverlayLayoutRules.SquareMapShape;
    // Retained for migration from schema v4. New writes mirror the Prime
    // entry in WidgetVisibility so an older build still behaves sensibly.
    public bool MissionsVisible { get; init; } = true;
    public double? Left { get; init; }
    public double? Top { get; init; }
    public Dictionary<string, OverlayWidgetPosition> Widgets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> WidgetVisibility { get; init; } =
        OverlayLayoutRules.CreateDefaultWidgetVisibility();
}

public sealed record OverlayWidgetPosition
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Scale { get; init; } = OverlayLayoutRules.DefaultScale;
}

public static class OverlayLayoutRules
{
    public const int CurrentVersion = 5;
    public const string MapWidget = "map";
    public const string StatsWidget = "stats";
    public const string TeamWidget = "team";
    public const string PrimeWidget = "prime";
    public const string ControlsWidget = "controls";
    public const string SquareMapShape = "square";
    public const string CircleMapShape = "circle";
    public const double BaseWidth = 318d;
    public const double DefaultScale = 1d;
    public const double MinimumScale = 0.65d;
    public const double MaximumScale = 1.75d;
    public const double ButtonStep = 0.1d;

    public static OverlayLayoutSettings Normalize(OverlayLayoutSettings? settings)
    {
        settings ??= new OverlayLayoutSettings();
        // Do not use ToDictionary here: hand-edited JSON can contain both
        // "Map" and "map".  A deterministic last-entry-wins pass keeps the
        // rest of the settings instead of resetting everything on load.
        var widgets = new Dictionary<string, OverlayWidgetPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Widgets
                     ?? new Dictionary<string, OverlayWidgetPosition>(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsKnownWidget(pair.Key) || pair.Value is null)
            {
                continue;
            }

            widgets[NormalizeWidgetKey(pair.Key)] = new OverlayWidgetPosition
            {
                Left = FiniteOrZero(pair.Value.Left),
                Top = FiniteOrZero(pair.Value.Top),
                Scale = NormalizeScale(pair.Value.Scale)
            };
        }
        var widgetVisibility = CreateDefaultWidgetVisibility();
        if (settings.Version >= CurrentVersion)
        {
            foreach (var pair in settings.WidgetVisibility
                         ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase))
            {
                if (IsConfigurableWidget(pair.Key))
                {
                    widgetVisibility[NormalizeWidgetKey(pair.Key)] = pair.Value;
                }
            }
        }
        else
        {
            widgetVisibility[PrimeWidget] = settings.MissionsVisible;
        }
        return settings with
        {
            Version = CurrentVersion,
            Scale = NormalizeScale(settings.Scale),
            MapShape = NormalizeMapShape(settings.MapShape),
            MissionsVisible = widgetVisibility[PrimeWidget],
            Left = FiniteOrNull(settings.Left),
            Top = FiniteOrNull(settings.Top),
            Widgets = widgets,
            WidgetVisibility = widgetVisibility
        };
    }

    public static Dictionary<string, bool> CreateDefaultWidgetVisibility() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [MapWidget] = true,
            [StatsWidget] = true,
            [TeamWidget] = true,
            [PrimeWidget] = true
        };

    public static bool IsConfigurableWidget(string? value) =>
        value?.Trim().ToLowerInvariant() is
            MapWidget or StatsWidget or TeamWidget or PrimeWidget;

    private static string NormalizeWidgetKey(string value) => value.Trim().ToLowerInvariant();

    public static double NormalizeScale(double scale)
    {
        if (!double.IsFinite(scale))
        {
            return DefaultScale;
        }

        return Math.Round(
            Math.Clamp(scale, MinimumScale, MaximumScale),
            2,
            MidpointRounding.AwayFromZero);
    }

    public static double ScaleFromHorizontalDrag(double startingScale, double deltaDip) =>
        NormalizeScale(startingScale + deltaDip / BaseWidth);

    public static double ScaleFromWidgetDrag(
        double startingScale,
        double horizontalDeltaDip,
        double verticalDeltaDip)
    {
        var horizontal = double.IsFinite(horizontalDeltaDip) ? horizontalDeltaDip : 0d;
        var vertical = double.IsFinite(verticalDeltaDip) ? verticalDeltaDip : 0d;
        var dominantDelta = Math.Abs(horizontal) >= Math.Abs(vertical)
            ? horizontal
            : vertical;
        return NormalizeScale(startingScale + dominantDelta / BaseWidth);
    }

    public static string FormatScale(double scale) => $"{NormalizeScale(scale) * 100d:0}%";

    public static string NormalizeMapShape(string? shape) =>
        string.Equals(shape, CircleMapShape, StringComparison.OrdinalIgnoreCase)
            ? CircleMapShape
            : SquareMapShape;

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0d;

    private static bool IsKnownWidget(string? value) => value?.Trim().ToLowerInvariant() is
        MapWidget or StatsWidget or TeamWidget or PrimeWidget or ControlsWidget;
}

public static class OverlayWidgetVisibilityPolicy
{
    public static bool IsBlockVisible(
        bool wholeHudVisible,
        bool userEnabled,
        bool dataAvailable,
        bool editMode) =>
        wholeHudVisible
        && userEnabled
        && (dataAvailable || editMode);

    public static bool AreEditControlsVisible(bool wholeHudVisible, bool editMode) =>
        wholeHudVisible && editMode;

    public static bool CanEnterEditMode(bool wholeHudVisible) => wholeHudVisible;

    public static bool ShouldPositionMap(bool wholeHudVisible, bool mapBlockVisible) =>
        wholeHudVisible && mapBlockVisible;
}

public static class MapZoomRules
{
    public const double DefaultZoom = 4d;
    public const double MinimumZoom = 1d;
    public const double MaximumZoom = 20d;
    public const double WheelStep = 0.35d;

    public static double ZoomIn(double current) =>
        Math.Min(MaximumZoom, current + WheelStep);

    public static double ZoomOut(double current) =>
        Math.Max(MinimumZoom, current - WheelStep);
}

public enum MapFocusMode
{
    FollowPlayer,
    FreeLook
}

public static class MapPanRules
{
    public static TheIsleOverlay.Core.MapPoint ApplyDragToFocus(
        TheIsleOverlay.Core.MapPoint startingFocus,
        double horizontalDelta,
        double verticalDelta,
        double imageWidth,
        double imageHeight)
    {
        if (!IsPositiveFinite(imageWidth) || !IsPositiveFinite(imageHeight))
        {
            return Normalize(startingFocus);
        }

        var start = Normalize(startingFocus);
        return new TheIsleOverlay.Core.MapPoint(
            start.Left - NormalizeDelta(horizontalDelta) / imageWidth,
            start.Top - NormalizeDelta(verticalDelta) / imageHeight);
    }

    public static TheIsleOverlay.Core.MapPoint ClampFocus(
        TheIsleOverlay.Core.MapPoint focus,
        double viewportWidth,
        double viewportHeight,
        double imageWidth,
        double imageHeight)
    {
        var normalized = Normalize(focus);
        var horizontalLimit = CenterLimit(viewportWidth, imageWidth);
        var verticalLimit = CenterLimit(viewportHeight, imageHeight);
        return new TheIsleOverlay.Core.MapPoint(
            Math.Clamp(normalized.Left, horizontalLimit, 1d - horizontalLimit),
            Math.Clamp(normalized.Top, verticalLimit, 1d - verticalLimit));
    }

    private static double CenterLimit(double viewportSize, double imageSize)
    {
        if (!IsPositiveFinite(viewportSize) || !IsPositiveFinite(imageSize) || imageSize <= viewportSize)
        {
            return 0.5d;
        }

        return Math.Clamp(viewportSize / (2d * imageSize), 0d, 0.5d);
    }

    private static TheIsleOverlay.Core.MapPoint Normalize(TheIsleOverlay.Core.MapPoint value) =>
        new(
            Math.Clamp(NormalizeComponent(value.Left, 0.5d), 0d, 1d),
            Math.Clamp(NormalizeComponent(value.Top, 0.5d), 0d, 1d));

    private static double NormalizeComponent(double value, double fallback = 0d) =>
        double.IsFinite(value) ? value : fallback;

    private static double NormalizeDelta(double value) => double.IsFinite(value) ? value : 0d;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0d;
}

public sealed class OverlayLayoutSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public OverlayLayoutSettingsStore(string? path = null)
    {
        var overridePath = Environment.GetEnvironmentVariable(
            "ISLELIVEMAP_LAYOUT_SETTINGS_PATH");
        _path = string.IsNullOrWhiteSpace(path)
            ? string.IsNullOrWhiteSpace(overridePath)
                ? AppPaths.OverlayLayoutSettings
                : Path.GetFullPath(overridePath)
            : Path.GetFullPath(path);
    }

    public OverlayLayoutSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new OverlayLayoutSettings();
            }

            var settings = JsonSerializer.Deserialize<OverlayLayoutSettings>(
                File.ReadAllText(_path),
                JsonOptions);
            return OverlayLayoutRules.Normalize(settings);
        }
        catch
        {
            return new OverlayLayoutSettings();
        }
    }

    public void Save(OverlayLayoutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Overlay settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(OverlayLayoutRules.Normalize(settings), JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch
        {
            // Layout changes remain usable even if Windows temporarily blocks persistence.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }
}
