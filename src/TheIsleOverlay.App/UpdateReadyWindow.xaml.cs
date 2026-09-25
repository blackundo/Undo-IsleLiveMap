using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TheIsleOverlay.App;

public partial class UpdateReadyWindow : Window
{
    public bool ApplyRequested { get; private set; }

    public UpdateReadyWindow(string? version, bool premium = false)
    {
        InitializeComponent();
        ApplyUndoTheme(premium);
        VersionLabel.Text = string.IsNullOrWhiteSpace(version) ? "BẢN MỚI" : $"v{version}";
    }

    private void ApplyUndoTheme(bool premium)
    {
        Resources["ModalSurface"] = Brush(premium ? "#F20A060C" : "#F2070817");
        Resources["ModalInk"] = Brush(premium ? "#F3D8FF" : "#E9EFF4");
        Resources["ModalMuted"] = Brush(premium ? "#B684C9" : "#9EA4B3");
        Resources["ModalLine"] = Brush(premium ? "#44205C" : "#353B55");
        Resources["ModalLineStrong"] = Brush(premium ? "#67248E" : "#48506C");
        Resources["ModalAccent"] = Brush(premium ? "#B14CE6" : "#3745D4");
        Resources["ModalAccentBright"] = Brush(premium ? "#E0A0FF" : "#E8EDFF");
        Resources["ModalHover"] = Brush(premium ? "#1E082B" : "#181D36");
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyRequested = true;
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
