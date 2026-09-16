using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TheIsleOverlay.TeamRelay;

namespace TheIsleOverlay.App;

public partial class HomeWindow
{
    private bool _teamPanelInitialized;
    private bool _teamChanging;
    private bool _normalizingInviteCode;
    private bool _selectingTeamRelay;
    private TeamRelayState _pendingHomeTeamState = new();
    private DispatcherTimer? _homeTeamRenderTimer;
    private volatile bool _homeTeamStateDirty;

    private void InitializeTeamPanel()
    {
        if (_teamPanelInitialized)
        {
            return;
        }

        _teamPanelInitialized = true;
        ApplyTeamRelaySelection();
        App.CurrentTeam.StateChanged += TeamCoordinator_StateChanged;
        _pendingHomeTeamState = App.CurrentTeam.CurrentState;
        ApplyTeamState(_pendingHomeTeamState);
        _homeTeamRenderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            DispatcherPriority.Render,
            HomeTeamRenderTimer_Tick,
            Dispatcher);
        _homeTeamRenderTimer.Start();
    }

    private void DetachTeamPanel()
    {
        if (!_teamPanelInitialized)
        {
            return;
        }

        App.CurrentTeam.StateChanged -= TeamCoordinator_StateChanged;
        _homeTeamRenderTimer?.Stop();
        _homeTeamRenderTimer = null;
        _teamPanelInitialized = false;
    }

    private async void CreateTeamButton_Click(object sender, RoutedEventArgs e)
    {
        if (_teamChanging || !TryGetTeamDisplayName(out var displayName))
        {
            return;
        }

        SetTeamBusy(true, "ĐANG TẠO NHÓM…");
        try
        {
            await App.CurrentTeam.CreateAsync(displayName, _shutdown.Token);
            TeamErrorLabel.Text = "Nhóm đã sẵn sàng. Gửi mã mời cho bạn bè.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TeamErrorLabel.Text = FriendlyTeamError(exception);
        }
        finally
        {
            SetTeamBusy(false);
        }
    }

    private async void JoinTeamButton_Click(object sender, RoutedEventArgs e)
    {
        if (_teamChanging || !TryGetTeamDisplayName(out var displayName))
        {
            return;
        }

        var inviteCode = NormalizeInviteCode(InviteCodeTextBox.Text);
        if (inviteCode.Length != 6)
        {
            TeamErrorLabel.Text = "Mã mời phải có đúng 6 chữ hoặc số.";
            return;
        }

        SetTeamBusy(true, "ĐANG VÀO NHÓM…");
        try
        {
            await App.CurrentTeam.JoinAsync(inviteCode, displayName, _shutdown.Token);
            TeamErrorLabel.Text = "Đã vào nhóm. Mở overlay để chia sẻ vị trí và status.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TeamErrorLabel.Text = FriendlyTeamError(exception);
        }
        finally
        {
            SetTeamBusy(false);
        }
    }

    private async void LeaveTeamButton_Click(object sender, RoutedEventArgs e)
    {
        if (_teamChanging)
        {
            return;
        }

        SetTeamBusy(true, "ĐANG RỜI NHÓM…");
        try
        {
            await App.CurrentTeam.LeaveAsync(_shutdown.Token);
            TeamErrorLabel.Text = "Đã rời nhóm. Dữ liệu phiên của bạn đã được dọn.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TeamErrorLabel.Text = FriendlyTeamError(exception);
        }
        finally
        {
            SetTeamBusy(false);
        }
    }

    private async void TeamRelayRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_selectingTeamRelay
            || !_teamPanelInitialized
            || _teamChanging
            || sender is not System.Windows.Controls.RadioButton option
            || !Enum.TryParse<TeamRelayProvider>(option.Tag?.ToString(), out var provider))
        {
            return;
        }

        var endpoint = TeamRelayEndpoints.For(provider);
        if (endpoint.Provider == App.CurrentTeam.CurrentEndpoint.Provider)
        {
            ApplyTeamRelayPresentation(endpoint);
            return;
        }

        SetTeamBusy(true, "ĐANG ĐỔI RELAY…");
        try
        {
            await App.CurrentTeam.SwitchRelayAsync(endpoint, _shutdown.Token);
            App.CurrentApp.TeamRelayPreferences.Save(new TeamRelayPreferences
            {
                Provider = endpoint.Provider
            });
            ApplyTeamRelayPresentation(endpoint);
            TeamErrorLabel.Text = endpoint.IsRecommended
                ? "Đã chuyển sang relay Undo-Isle · tối đa 15 người."
                : "Đang dùng relay KLongDev cũ · tối đa 10 người. Khuyên chuyển sang Undo-Isle.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplyTeamRelaySelection();
            TeamErrorLabel.Text = FriendlyTeamError(exception);
        }
        finally
        {
            SetTeamBusy(false);
        }
    }

    private void CopyInviteCodeButton_Click(object sender, RoutedEventArgs e)
    {
        var inviteCode = App.CurrentTeam.CurrentState.Session?.InviteCode;
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            return;
        }

        try
        {
            Clipboard.SetText(inviteCode);
            TeamErrorLabel.Text = $"Đã copy mã {inviteCode}.";
        }
        catch
        {
            TeamErrorLabel.Text = "Windows chưa cho phép copy. Hãy nhập mã đang hiển thị.";
        }
    }

    private void InviteCodeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        InviteCodePlaceholder.Visibility = string.IsNullOrEmpty(InviteCodeTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_normalizingInviteCode)
        {
            return;
        }

        var normalized = NormalizeInviteCode(InviteCodeTextBox.Text);
        if (normalized == InviteCodeTextBox.Text)
        {
            return;
        }

        _normalizingInviteCode = true;
        InviteCodeTextBox.Text = normalized;
        InviteCodeTextBox.CaretIndex = normalized.Length;
        _normalizingInviteCode = false;
    }

    private void TeamCoordinator_StateChanged(object? sender, TeamRelayState state)
    {
        _pendingHomeTeamState = state;
        _homeTeamStateDirty = true;
    }

    private void HomeTeamRenderTimer_Tick(object? sender, EventArgs e)
    {
        if (!_homeTeamStateDirty)
        {
            return;
        }

        _homeTeamStateDirty = false;
        ApplyTeamState(_pendingHomeTeamState);
    }

    private void ApplyTeamState(TeamRelayState state)
    {
        var active = state.HasActiveSession;
        TeamInactivePanel.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        TeamActivePanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ApplyTeamRelayPresentation(App.CurrentTeam.CurrentEndpoint);

        TeamStateLabel.Text = state.ConnectionState switch
        {
            TeamRelayConnectionState.Connecting => "ĐANG KẾT NỐI",
            TeamRelayConnectionState.Live => "RELAY TRỰC TUYẾN",
            TeamRelayConnectionState.Reconnecting => "ĐANG NỐI LẠI",
            TeamRelayConnectionState.Expired => "PHIÊN ĐÃ HẾT",
            TeamRelayConnectionState.Error => "KHÔNG KẾT NỐI",
            _ => "CHƯA VÀO NHÓM"
        };
        TeamStateDot.Fill = HomeBrush(state.ConnectionState switch
        {
            TeamRelayConnectionState.Live => "#43D883",
            TeamRelayConnectionState.Connecting or TeamRelayConnectionState.Reconnecting => "#F3B63F",
            TeamRelayConnectionState.Expired or TeamRelayConnectionState.Error => "#DC5A56",
            _ => "#60687E"
        });

        if (active && state.Session is { } session)
        {
            ActiveInviteCodeLabel.Text = string.Join(" ", session.InviteCode.ToCharArray());
            TeamMemberCountLabel.Text = $"{state.Members.Count} / {session.MaxMembers} NGƯỜI";
            TeamErrorLabel.Text = state.ConnectionState switch
            {
                TeamRelayConnectionState.Reconnecting => "Mạng gián đoạn; app đang tự nối lại mà không làm mất nhóm.",
                TeamRelayConnectionState.Connecting => "Đang mở kênh realtime bảo mật…",
                _ => "Minimap và status đồng đội sẽ xuất hiện trong overlay."
            };
        }
        else if (state.ConnectionState is TeamRelayConnectionState.Expired or TeamRelayConnectionState.Error)
        {
            TeamErrorLabel.Text = state.Message ?? "Phiên nhóm đã kết thúc. Hãy tạo hoặc nhập mã lại.";
        }

        SetTeamBusy(_teamChanging);
    }

    private void SuggestTeamDisplayName(string steamIdSuffix)
    {
        if (string.IsNullOrWhiteSpace(TeamDisplayNameTextBox.Text)
            || string.Equals(TeamDisplayNameTextBox.Text.Trim(), "Survivor", StringComparison.OrdinalIgnoreCase))
        {
            TeamDisplayNameTextBox.Text = $"Survivor {steamIdSuffix}";
        }
    }

    private bool TryGetTeamDisplayName(out string displayName)
    {
        displayName = TeamDisplayNameTextBox.Text.Trim();
        if (displayName.Length is >= 1 and <= 32)
        {
            return true;
        }

        TeamErrorLabel.Text = "Tên hiển thị phải có từ 1 đến 32 ký tự.";
        return false;
    }

    private void SetTeamBusy(bool busy, string? message = null)
    {
        _teamChanging = busy;
        CreateTeamButton.IsEnabled = !busy;
        JoinTeamButton.IsEnabled = !busy;
        LeaveTeamButton.IsEnabled = !busy;
        TeamDisplayNameTextBox.IsEnabled = !busy;
        InviteCodeTextBox.IsEnabled = !busy;
        var canSelectRelay = !busy && !App.CurrentTeam.CurrentState.HasActiveSession;
        UndoIsleRelayRadio.IsEnabled = canSelectRelay;
        KLongDevRelayRadio.IsEnabled = canSelectRelay;
        if (!string.IsNullOrWhiteSpace(message))
        {
            TeamErrorLabel.Text = message;
        }
    }

    private string FriendlyTeamError(Exception exception) => exception switch
    {
        TeamRelayApiException { Code: "invite_not_found" } => "Không tìm thấy mã mời hoặc nhóm đã tự hết hạn.",
        TeamRelayApiException { Code: "team_full" } => "Nhóm đã đủ thành viên.",
        TeamRelayApiException { Code: "rate_limited" } => "Bạn thao tác quá nhanh. Chờ một chút rồi thử lại.",
        TimeoutException => "Relay không phản hồi trong 12 giây. Nút đã mở lại để bạn thử lại.",
        HttpRequestException => $"Không liên lạc được {App.CurrentTeam.CurrentEndpoint.BaseUri.Host}.",
        _ => $"Không mở được nhóm: {exception.Message}"
    };

    private void ApplyTeamRelaySelection()
    {
        var endpoint = App.CurrentTeam.CurrentEndpoint;
        _selectingTeamRelay = true;
        UndoIsleRelayRadio.IsChecked = endpoint.Provider == TeamRelayProvider.UndoIsle;
        KLongDevRelayRadio.IsChecked = endpoint.Provider == TeamRelayProvider.KLongDev;
        _selectingTeamRelay = false;
        ApplyTeamRelayPresentation(endpoint);
    }

    private void ApplyTeamRelayPresentation(TeamRelayEndpoint endpoint)
    {
        TeamRelayHeaderLabel.Text = $"TEAM LINK · {endpoint.DisplayName.ToUpperInvariant()}";
        TeamRelayRecommendationLabel.Text = endpoint.IsRecommended
            ? "Khuyên dùng relay Undo-Isle: nhóm 15 người và được ưu tiên hỗ trợ."
            : "Relay KLongDev cũ chỉ hỗ trợ tối đa 10 người. Khuyên chuyển sang Undo-Isle.";
        TeamRelayRecommendationLabel.Foreground = HomeBrush(
            endpoint.IsRecommended ? "#43D883" : "#F3B63F");
    }

    private static string NormalizeInviteCode(string? value) => new(
        (value ?? string.Empty)
        .Where(char.IsAsciiLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .Take(6)
        .ToArray());

    private static SolidColorBrush HomeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
