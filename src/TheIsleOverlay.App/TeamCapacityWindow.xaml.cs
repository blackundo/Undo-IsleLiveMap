using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TheIsleOverlay.TeamRelay;

namespace TheIsleOverlay.App;

public partial class TeamCapacityWindow : Window
{
    private readonly TeamRelayEndpoint _endpoint;
    public int? SelectedCapacity { get; private set; }
    public TeamCapacityWindow(TeamRelayEndpoint endpoint)
    {
        _endpoint = endpoint;
        InitializeComponent();
        TierLabel.Text = $"{endpoint.DisplayName.ToUpperInvariant()} · TỐI ĐA {endpoint.AdvertisedMaxMembers} NGƯỜI";
        MessageLabel.Text = "Chọn quy mô để tiếp tục nhập tên.";
        var choices = endpoint.Provider == TeamRelayProvider.KLongDev
            ? new[] { endpoint.AdvertisedMaxMembers }
            : TeamRoomLimits.Choices.Where(size => size <= endpoint.AdvertisedMaxMembers);
        foreach (var size in choices)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = $"{size} người", FontSize = 23, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            label.Children.Add(new TextBlock { Text = endpoint.Provider == TeamRelayProvider.UndoIsle ? "FREE & PRO" : "KLONG RELAY", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            var button = new Button { Content = label, Style = (Style)FindResource("CapacityChoice") };
            AutomationProperties.SetAutomationId(button, $"RoomCapacity{size}");
            AutomationProperties.SetName(button, $"Phòng {size} người");
            AutomationProperties.SetHelpText(button, $"Tạo phòng {size} người trên {endpoint.DisplayName}");
            button.Click += (_, _) => { if (TrySelect(size)) DialogResult = true; };
            ChoicesPanel.Children.Add(button);
        }
    }
    internal bool TrySelect(int size)
    {
        if (!TeamRoomLimits.Choices.Contains(size) || size > _endpoint.AdvertisedMaxMembers)
        { MessageLabel.Text = $"{_endpoint.DisplayName} chỉ hỗ trợ tối đa {_endpoint.AdvertisedMaxMembers} người."; return false; }
        SelectedCapacity = size; return true;
    }
}
