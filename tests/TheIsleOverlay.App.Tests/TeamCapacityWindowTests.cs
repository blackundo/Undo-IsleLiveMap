using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TheIsleOverlay.TeamRelay;

namespace TheIsleOverlay.App.Tests;

public sealed class TeamCapacityWindowTests
{
    [Theory]
    [InlineData(3)] [InlineData(7)] [InlineData(10)] [InlineData(21)] [InlineData(25)]
    public async Task UndoRelayAllowsEveryCapacityWithoutTierRestriction(int size)
    {
        await Sta(() =>
        {
            var window = new TeamCapacityWindow(TeamRelayEndpoints.UndoIsle, TeamAccessTier.Free);
            Assert.True(window.TrySelect(size));
            Assert.Equal(size, window.SelectedCapacity);
            window.Close();
        });
    }

    [Fact]
    public async Task KLongRelayUsesNewTieredCapacityChoices()
    {
        await Sta(() =>
        {
            var window = new TeamCapacityWindow(TeamRelayEndpoints.KLongDev, TeamAccessTier.Free);
            var panel = (UniformGrid)window.FindName("ChoicesPanel");
            Assert.Equal(4, panel.Children.Count);
            Assert.True(window.TrySelect(7));
            window.Close();
            window = new TeamCapacityWindow(TeamRelayEndpoints.KLongDev, TeamAccessTier.Free);
            Assert.False(window.TrySelect(21));
            Assert.Null(window.SelectedCapacity);
            Assert.Contains("yêu cầu Pro", ((TextBlock)window.FindName("MessageLabel")).Text);
            var pro = new TeamCapacityWindow(TeamRelayEndpoints.KLongDev, TeamAccessTier.Pro);
            Assert.True(pro.TrySelect(21));
            pro.Close();
            var output = Environment.GetEnvironmentVariable("ISLE_TEAM_UI_CAPTURE");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                Render(window, Path.Combine(output, "team-capacity-klong.png"));
                var undo = new TeamCapacityWindow(TeamRelayEndpoints.UndoIsle, TeamAccessTier.Free);
                Render(undo, Path.Combine(output, "team-capacity-undo.png"));
                undo.Close();
            }
            window.Close();
        });
    }

    [Fact]
    public async Task ModalSelectionReturnsCapacityBeforeNameStep()
    {
        await Sta(() =>
        {
            var window = new TeamCapacityWindow(TeamRelayEndpoints.UndoIsle, TeamAccessTier.Free);
            window.Loaded += (_, _) =>
            {
                var choices = (UniformGrid)window.FindName("ChoicesPanel");
                choices.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "RoomCapacity25")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            };
            Assert.True(window.ShowDialog());
            Assert.Equal(25, window.SelectedCapacity);
        });
    }

    private static async Task Sta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completed.SetResult(); } catch (Exception e) { completed.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(490, 510)); root.Arrange(new Rect(0, 0, 490, 510)); root.UpdateLayout();
        var choices = (UniformGrid)window.FindName("ChoicesPanel");
        Assert.True(choices.ActualHeight >= 224);
        foreach (var button in choices.Children.OfType<Button>())
        {
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            Assert.InRange(bounds.Bottom, 1d, root.ActualHeight - 50);
            Assert.True(button.ActualHeight >= 100);
        }
        var bmp = new RenderTargetBitmap(490, 510, 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
