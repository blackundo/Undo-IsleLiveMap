using System.Windows;
using System.Windows.Input;

namespace TheIsleOverlay.App;

public partial class MutationGuideWindow : Window
{
    private readonly IReadOnlyList<MutationEntry> _entries;
    private bool _vietnamesePrimary;

    public MutationGuideWindow(
        bool vietnamesePrimary = true,
        string shortcutDisplay = "Alt+U")
    {
        InitializeComponent();
        _entries = MutationCatalog.LoadDefault().Entries;
        _vietnamesePrimary = vietnamesePrimary;
        // Keep the toggle copy aligned with the active game locale from the
        // first frame.  The XAML default is Vietnamese; without this update an
        // English game opened the guide with English content but a misleading
        // Vietnamese toggle label until the first click.
        LocaleButton.Content = _vietnamesePrimary
            ? "VIỆT CHÍNH · EN ĐỐI CHIẾU"
            : "EN CHÍNH · VIỆT ĐỐI CHIẾU";
        ShortcutLabel.Text = $"{shortcutDisplay.ToUpperInvariant()}  /  ESC ĐỂ ĐÓNG";
        RefreshList();
        Loaded += (_, _) => SearchTextBox.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshList();

    private void LocaleButton_Click(object sender, RoutedEventArgs e)
    {
        _vietnamesePrimary = !_vietnamesePrimary;
        LocaleButton.Content = _vietnamesePrimary
            ? "VIỆT CHÍNH · EN ĐỐI CHIẾU"
            : "EN CHÍNH · VIỆT ĐỐI CHIẾU";
        RefreshList();
    }

    private void RefreshList()
    {
        if (MutationList is null || ResultCountLabel is null || SearchTextBox is null)
        {
            return;
        }

        var results = MutationCatalog.Search(_entries, SearchTextBox.Text, _vietnamesePrimary);
        MutationList.ItemsSource = results;
        ResultCountLabel.Text = $" · {results.Count} MỤC";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
