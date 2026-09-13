using System.Windows;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.App;

public partial class ProKeyActivationWindow : Window
{
    private readonly ProAccessService _service;
    private readonly string _hostVersion;
    private readonly CancellationTokenSource _shutdown = new();

    public ProKeyActivationWindow(ProAccessService service, string hostVersion)
    {
        _service = service;
        _hostVersion = hostVersion;
        InitializeComponent();
    }

    public ProAccessSnapshot? Access { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e) => KeyInput.Focus();

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(KeyInput.Text))
        {
            StatusLabel.Text = "Vui lòng nhập key kích hoạt.";
            KeyInput.Focus();
            return;
        }

        ActivateButton.IsEnabled = false;
        KeyInput.IsEnabled = false;
        try
        {
            Access = await _service.ActivateKeyAsync(KeyInput.Text, _hostVersion, _shutdown.Token);
            if (!_shutdown.IsCancellationRequested) DialogResult = true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ProAgentException exception)
        {
            StatusLabel.Text = exception.Message;
        }
        catch (ArgumentException)
        {
            StatusLabel.Text = "Key không đúng. Vui lòng kiểm tra và nhập lại.";
        }
        catch (Exception)
        {
            StatusLabel.Text = "Không lưu được kích hoạt. Vui lòng thử lại.";
        }
        finally
        {
            ActivateButton.IsEnabled = true;
            KeyInput.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closed(object? sender, EventArgs e) => _shutdown.Cancel();
}
