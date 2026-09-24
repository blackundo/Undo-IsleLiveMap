using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TheIsleOverlay.TeamRelay;

namespace TheIsleOverlay.App;

public partial class TeamCapacityWindow : Window
{
    private readonly TeamRelayEndpoint _endpoint;
    private readonly TeamAccessTier _tier;
    public int? SelectedCapacity { get; private set; }
    public TeamCapacityWindow(TeamRelayEndpoint endpoint, TeamAccessTier tier)
    {
        _endpoint = endpoint;
        _tier = tier;
        InitializeComponent();
        TierLabel.Text = endpoint.Provider == TeamRelayProvider.UndoIsle
            ? "UNDO-ISLE · FREE & PRO · TỐI ĐA 25 NGƯỜI"
            : $"KLONGDEV · {(tier == TeamAccessTier.Pro ? "PRO · TỐI ĐA 21" : "FREE · TỐI ĐA 7")} NGƯỜI";
        MessageLabel.Text = "Chọn quy mô để tiếp tục nhập tên.";
        var choices = TeamRoomLimits.Choices.Where(size => size <= endpoint.AdvertisedMaxMembers);
        foreach (var size in choices)
        {
            var allowed = IsAllowed(size);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = $"{size} người", FontSize = 23, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            label.Children.Add(new TextBlock
            {
                Text = endpoint.Provider == TeamRelayProvider.UndoIsle
                    ? "FREE & PRO"
                    : size <= 7 ? "FREE" : allowed ? "PRO" : "KHÓA · CẦN PRO",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            });
            var button = new Button { Content = label, Style = (Style)FindResource("CapacityChoice"), Opacity = allowed ? 1 : .5 };
            AutomationProperties.SetAutomationId(button, $"RoomCapacity{size}");
            AutomationProperties.SetName(button, $"Phòng {size} người" + (allowed ? string.Empty : " · Cần Pro"));
            AutomationProperties.SetHelpText(button, allowed ? $"Tạo phòng {size} người trên {endpoint.DisplayName}" : ProRequiredMessage(size));
            button.Click += (_, _) => { if (TrySelect(size)) DialogResult = true; };
            ChoicesPanel.Children.Add(button);
        }
    }
    internal bool TrySelect(int size)
    {
        if (!TeamRoomLimits.Choices.Contains(size) || size > _endpoint.AdvertisedMaxMembers)
        { MessageLabel.Text = $"{_endpoint.DisplayName} chỉ hỗ trợ tối đa {_endpoint.AdvertisedMaxMembers} người."; return false; }
        if (!IsAllowed(size))
        { MessageLabel.Text = ProRequiredMessage(size); return false; }
        SelectedCapacity = size; return true;
    }
    private bool IsAllowed(int size) =>
        _endpoint.Provider == TeamRelayProvider.UndoIsle
        || size <= 7
        || _tier == TeamAccessTier.Pro;

    public static string ProRequiredMessage(int size) =>
        $"Relay KLongDev yêu cầu Pro để tạo phòng {size} người.";
}
