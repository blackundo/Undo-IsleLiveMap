using System.Windows;
using System.Windows.Input;

namespace TheIsleOverlay.App;

public partial class TeamNameWindow : Window
{
    public TeamNameWindow(string action)
    {
        InitializeComponent();
        Title = action;
        ActionTitle.Text = action;
    }

    public string? DisplayName { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameInput.Focus();
        Keyboard.Focus(NameInput);
    }

    private void NameInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ConfirmButton is null)
        {
            return;
        }

        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameInput.Text);
        ValidationLabel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0)
        {
            ValidationLabel.Visibility = Visibility.Visible;
            NameInput.Focus();
            return;
        }

        DisplayName = name;
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
