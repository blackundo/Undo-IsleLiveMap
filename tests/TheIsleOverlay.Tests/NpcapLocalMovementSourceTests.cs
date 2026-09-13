using System.Net.NetworkInformation;
using TheIsleOverlay.LocalTelemetry;

namespace TheIsleOverlay.Tests;

public sealed class NpcapLocalMovementSourceTests
{
    [Theory]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Wireless80211, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Tunnel, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Loopback, false)]
    [InlineData(OperationalStatus.Down, NetworkInterfaceType.Tunnel, false)]
    public void CaptureNetwork_IncludesActiveTunnelInterfaces(
        OperationalStatus status,
        NetworkInterfaceType type,
        bool expected)
    {
        Assert.Equal(
            expected,
            NpcapLocalMovementSource.IsEligibleCaptureNetwork(status, type));
    }

    [Fact]
    public void CaptureFilter_IncludesInboundAndOutboundGameTraffic()
    {
        var filter = NpcapLocalMovementSource.BuildCaptureFilter([7778, 7777]);

        Assert.Equal(
            "udp and (src port 7777 or dst port 7777 or src port 7778 or dst port 7778)",
            filter);
    }

    [Fact]
    public void CaptureFilter_CanaryOffKeepsGpsOnlyFilter()
    {
        Assert.Equal(
            "udp and (src port 7777 or src port 7778)",
            NpcapLocalMovementSource.BuildCaptureFilter(
                [7778, 7777],
                includeInbound: false));
    }

    [Fact]
    public void CaptureFilter_EmptyPortSetIsAlwaysFalse()
    {
        Assert.Equal(
            "udp and (false)",
            NpcapLocalMovementSource.BuildCaptureFilter([]));
    }

    [Theory]
    [InlineData(50_000, 7_777, true)]
    [InlineData(7_777, 50_000, false)]
    public void Direction_UsesGameOwnedLocalPort(
        int sourcePort,
        int destinationPort,
        bool expectedInbound)
    {
        var direction = NpcapLocalMovementSource.ClassifyDirection(
            sourcePort,
            destinationPort,
            new HashSet<int> { 7_777 });

        Assert.Equal(
            expectedInbound ? PacketDirection.Inbound : PacketDirection.Outbound,
            direction);
    }

    [Theory]
    [InlineData(50_000, 50_001)]
    [InlineData(7_777, 7_777)]
    public void Direction_RejectsUnownedOrAmbiguousTraffic(
        int sourcePort,
        int destinationPort)
    {
        Assert.Null(NpcapLocalMovementSource.ClassifyDirection(
            sourcePort,
            destinationPort,
            new HashSet<int> { 7_777 }));
    }

    [Fact]
    public void OwnedPortSnapshot_RefreshesWithoutMutatingPublishedSnapshot()
    {
        var snapshot = new NpcapLocalMovementSource.OwnedUdpPortSnapshot([7_777]);
        var before = snapshot.Current;

        Assert.True(snapshot.Replace([8_888]));

        Assert.Contains(7_777, before);
        Assert.DoesNotContain(8_888, before);
        Assert.DoesNotContain(7_777, snapshot.Current);
        Assert.Contains(8_888, snapshot.Current);
    }

    [Fact]
    public void OwnedPortSnapshot_DoesNotPublishEquivalentSet()
    {
        var snapshot = new NpcapLocalMovementSource.OwnedUdpPortSnapshot([7_778, 7_777]);
        var before = snapshot.Current;

        Assert.False(snapshot.Replace([7_777, 7_778]));
        Assert.Same(before, snapshot.Current);
    }

    [Fact]
    public void Direction_UsesRefreshedOwnerSetAfterSocketChurn()
    {
        var snapshot = new NpcapLocalMovementSource.OwnedUdpPortSnapshot([7_777]);

        Assert.Equal(
            PacketDirection.Outbound,
            NpcapLocalMovementSource.ClassifyDirection(
                7_777,
                50_000,
                snapshot.Current));

        snapshot.Replace([8_888]);

        Assert.Null(NpcapLocalMovementSource.ClassifyDirection(
            7_777,
            50_000,
            snapshot.Current));
        Assert.Equal(
            PacketDirection.Outbound,
            NpcapLocalMovementSource.ClassifyDirection(
                8_888,
                50_000,
                snapshot.Current));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void LocalVitalsCanary_RequiresExplicitOptIn(
        string? value,
        bool expected)
    {
        Assert.Equal(expected, LocalVitalsFeature.IsEnabled(value));
    }
}
