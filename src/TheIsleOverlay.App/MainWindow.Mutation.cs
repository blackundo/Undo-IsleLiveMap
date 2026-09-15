using System.Windows;
using System.IO;
using TheIsleOverlay.Localization;

namespace TheIsleOverlay.App;

public partial class MainWindow
{
    private MutationGuideWindow? _mutationGuideWindow;

    private void ToggleMutationGuide()
    {
        if (_mutationGuideWindow is not null)
        {
            _mutationGuideWindow.Close();
            return;
        }

        var locale = File.Exists(GameLanguageSettings.DefaultConfigPath)
            ? GameLanguageSettings.Read(File.ReadAllText(GameLanguageSettings.DefaultConfigPath))
            : GameLocale.English;
        var window = new MutationGuideWindow(
            vietnamesePrimary: locale == GameLocale.Vietnamese,
            shortcutDisplay: _shortcutSettings.MutationGuide)
        {
            Owner = this
        };
        window.Closed += MutationGuideWindow_Closed;
        _mutationGuideWindow = window;
        window.Show();
        window.Activate();
    }

    private void MutationGuideWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is MutationGuideWindow window)
        {
            window.Closed -= MutationGuideWindow_Closed;
        }

        _mutationGuideWindow = null;
        Activate();
    }

    private void CloseMutationGuide()
    {
        if (_mutationGuideWindow is null)
        {
            return;
        }

        var window = _mutationGuideWindow;
        _mutationGuideWindow = null;
        window.Closed -= MutationGuideWindow_Closed;
        window.Close();
    }
}
