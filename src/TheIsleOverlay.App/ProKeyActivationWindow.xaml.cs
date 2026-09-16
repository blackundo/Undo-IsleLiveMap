using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Media;
using TheIsleOverlay.ProClient;

namespace TheIsleOverlay.App;

public partial class ProKeyActivationWindow : Window
{
    public static Uri FreeKeyPageUri => ProClientOptions.FreeKeyPageUri;

    private readonly ProAccessService _service;
    private readonly string _hostVersion;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _activationStored;

    public ProKeyActivationWindow(ProAccessService service, string hostVersion)
    {
        _service = service;
        _hostVersion = hostVersion;
        InitializeComponent();
    }

    public ProAccessSnapshot? Access { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e) => KeyInput.Focus();

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (Access?.AgentReady == true)
        {
            DialogResult = true;
            return;
        }

        if (!_activationStored && string.IsNullOrWhiteSpace(KeyInput.Text))
        {
            ShowStatus("!", "Chưa có key", "Vui lòng nhập key kích hoạt.", "#AF4EE7", "#281434", "#52246B");
            KeyInput.Focus();
            return;
        }

        SetBusy(true);
        ShowStatus("…", _activationStored ? "Đang kết nối lại Pro Agent" : "Đang xác thực key",
            _activationStored
                ? "Đang dùng lease đã lưu; key sẽ không bị gửi lại hoặc sử dụng lần hai."
                : "Đang xác thực với máy chủ, lưu lease và chuẩn bị Pro Agent…",
            "#AF4EE7", "#24182A", "#522766");
        try
        {
            Access = _activationStored
                ? await _service.InitializeAsync(_hostVersion, _shutdown.Token)
                : await _service.ActivateKeyAsync(KeyInput.Text, _hostVersion, _shutdown.Token);

            if (Access.AgentReady)
            {
                _activationStored = true;
                KeyInput.IsEnabled = false;
                ActivateButton.Content = "HOÀN TẤT";
                CloseButton.Content = "ĐÓNG";
                ShowStatus("✓", "Pro đã kích hoạt",
                    $"Pro Agent {Access.AgentVersion ?? ""} đã được xác minh và sẵn sàng sử dụng.",
                    "#6599D6", "#141E2D", "#344F72");
                return;
            }

            if (Access.IsAuthenticated)
            {
                _activationStored = true;
                KeyInput.IsEnabled = false;
                ActivateButton.Content = "THỬ KẾT NỐI LẠI";
                CloseButton.Content = "ĐÓNG VÀ GIỮ KEY";
                var detail = Access.StatusCode switch
                {
                    "local_key_rejected" => "Key đã được server nhận và lease đã lưu, nhưng backend hoặc Agent từ chối lease. Hãy kiểm tra signing key rồi thử lại; không cần key mới.",
                    "local_agent_rejected" => "Key đã được server nhận và lease đã lưu, nhưng Pro Agent không chấp nhận phiên hoặc không tương thích.",
                    _ => "Key đã được kích hoạt và lease đã lưu an toàn. Chưa tải hoặc khởi động được Pro Agent; kiểm tra mạng/Windows Security rồi thử lại."
                };
                ShowStatus("!", "Key đã lưu, Agent chưa sẵn sàng", detail,
                    "#BC5DF0", "#291733", "#592772");
                return;
            }

            _activationStored = false;
            Access = null;
            KeyInput.IsEnabled = true;
            ActivateButton.Content = "KÍCH HOẠT";
            ShowStatus("×", "Lease không còn hiệu lực",
                "Lease đã hết hạn hoặc bị thu hồi. Hãy dùng một key còn hiệu lực.",
                "#FF8E8E", "#351B1B", "#713333");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ProActivationPersistenceException)
        {
            ShowStatus("!", "Server đã nhận key nhưng máy chưa lưu được lease",
                "Không thể ghi credential được mã hóa vào LocalAppData. Hãy kiểm tra quyền thư mục hoặc antivirus rồi bấm lại; cùng thiết bị có thể nhận lại lease mà không mất key.",
                "#BC5DF0", "#291733", "#592772");
        }
        catch (ProApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ShowStatus("×", "Key không được chấp nhận",
                "Key không đúng, đã hết hạn, bị thu hồi hoặc đã được kích hoạt trên thiết bị khác.",
                "#FF8E8E", "#351B1B", "#713333");
        }
        catch (ProApiException exception) when (exception.StatusCode is null)
        {
            ShowStatus("!", "Không kết nối được máy chủ cấp phép",
                "Kiểm tra Internet rồi thử lại. Key chưa bị sử dụng nếu máy chủ chưa phản hồi.",
                "#BC5DF0", "#291733", "#592772");
        }
        catch (ProApiException exception)
        {
            ShowStatus("!", "Máy chủ chưa thể xử lý kích hoạt",
                $"Yêu cầu bị từ chối (HTTP {(int?)exception.StatusCode ?? 0}). Vui lòng thử lại sau.",
                "#BC5DF0", "#291733", "#592772");
        }
        catch (ArgumentException)
        {
            ShowStatus("×", "Key không hợp lệ", "Kiểm tra lại định dạng key rồi nhập lại.",
                "#FF8E8E", "#351B1B", "#713333");
        }
        catch (Exception)
        {
            ShowStatus("!", "Không hoàn tất được kích hoạt",
                "Đã xảy ra lỗi cục bộ trước khi hoàn tất. Key không bị báo sai; hãy thử lại hoặc kiểm tra log ứng dụng.",
                "#BC5DF0", "#291733", "#592772");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ProgressIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActivateButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        GetFreeKeyButton.IsEnabled = !busy;
        KeyInput.IsEnabled = !busy && !_activationStored;
    }

    private void GetFreeKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(FreeKeyPageUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowStatus("!", "Không mở được trang lấy key",
                $"Hãy thử lại hoặc mở {FreeKeyPageUri.AbsoluteUri} trong trình duyệt. {exception.Message}",
                "#BC5DF0", "#291733", "#592772");
        }
    }

    private void ShowStatus(string icon, string title, string detail, string accent, string background, string border)
    {
        static Brush Brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        StatusIcon.Text = icon;
        StatusIcon.Foreground = Brush(accent);
        StatusTitle.Text = title;
        StatusLabel.Text = detail;
        StatusPanel.Background = Brush(background);
        StatusPanel.BorderBrush = Brush(border);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Access is not null)
        {
            DialogResult = true;
            return;
        }
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e) => _shutdown.Cancel();
}
