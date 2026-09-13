using System.Windows;
using System.Windows.Interop;

namespace TheIsleOverlay.App;

public partial class HomeWindow
{
    private void ShortcutSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var store = new ShortcutSettingsStore();
        var dialog = new ShortcutSettingsWindow(store.Load())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedSettings is not { } settings)
        {
            return;
        }

        using var registrationProbe = new ShortcutRegistrationManager(
            new WindowInteropHelper(this).Handle,
            includeMapNotes: true);
        var registration = registrationProbe.RegisterInitial(settings);
        if (!registration.Success)
        {
            MessageBox.Show(
                this,
                registration.FriendlyError,
                "Phím tắt đang bị trùng",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!store.TrySave(settings, out var error))
        {
            MessageBox.Show(
                this,
                error ?? "Không thể lưu phím tắt.",
                "Phím tắt overlay",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
