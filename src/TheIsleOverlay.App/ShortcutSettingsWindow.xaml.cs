using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TheIsleOverlay.App;

public partial class ShortcutSettingsWindow : Window
{
    private readonly OverlayShortcutSettings _initial;

    public ShortcutSettingsWindow(OverlayShortcutSettings settings)
    {
        _initial = settings ?? OverlayShortcutSettings.Defaults;
        InitializeComponent();
    }

    public OverlayShortcutSettings? SelectedSettings { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EditModeInput.Text = _initial.EditMode;
        ToggleMissionsInput.Text = _initial.ToggleMissions;
        ToggleHudInput.Text = _initial.ToggleHud;
        MapNotesInput.Text = _initial.MapNotes;
        MutationGuideInput.Text = _initial.MutationGuide;
    }

    private void ShortcutInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox input) return;
        e.Handled = true;
        if (e.Key is Key.Tab)
        {
            input.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }
        if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None
            || key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            input.Clear();
            return;
        }

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        var keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            _ => null
        };
        if (parts.Count == 0 || keyName is null)
        {
            SetFeedback("Tổ hợp cần phím bổ trợ và một phím chữ, số, F1–F24 hoặc phím điều hướng.", true);
            return;
        }

        parts.Add(keyName);
        input.Text = string.Join('+', parts);
        input.CaretIndex = input.Text.Length;
        SetFeedback("Tổ hợp đã nhận. Bấm LƯU PHÍM TẮT để áp dụng.", false);
    }

    private void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = OverlayShortcutSettings.Defaults;
        EditModeInput.Text = defaults.EditMode;
        ToggleMissionsInput.Text = defaults.ToggleMissions;
        ToggleHudInput.Text = defaults.ToggleHud;
        MapNotesInput.Text = defaults.MapNotes;
        MutationGuideInput.Text = defaults.MutationGuide;
        SetFeedback("Đã khôi phục Ctrl+Shift+O cho Edit Mode và Alt+P cho toàn HUD.", false);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var candidate = new OverlayShortcutSettings
        {
            EditMode = EditModeInput.Text.Trim(),
            ToggleMissions = ToggleMissionsInput.Text.Trim(),
            ToggleHud = ToggleHudInput.Text.Trim(),
            MapNotes = MapNotesInput.Text.Trim(),
            MutationGuide = MutationGuideInput.Text.Trim()
        };
        var errors = ShortcutSettingsManager.Validate(candidate);
        if (errors.Count > 0)
        {
            SetFeedback(string.Join(" ", errors), true);
            return;
        }

        SelectedSettings = candidate;
        DialogResult = true;
    }

    public void SetExternalError(string message) => SetFeedback(message, true);

    private void SetFeedback(string message, bool isError)
    {
        FeedbackLabel.Text = message;
        FeedbackLabel.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#EF8D7C" : "#8FE3D0"));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
