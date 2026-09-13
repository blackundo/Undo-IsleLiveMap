using System.IO;
using TheIsleOverlay.App;

namespace TheIsleOverlay.App.Tests;

public sealed class TeamRelayPreferenceStoreTests
{
    [Fact]
    public void MissingPreference_DefaultsToUndoIsle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"team-relay-{Guid.NewGuid():N}.json");
        var preferences = new TeamRelayPreferenceStore(path).Load();

        Assert.Equal(TeamRelayProvider.UndoIsle, preferences.Provider);
        Assert.Equal("https://isle-relay.modundo.com/", TeamRelayEndpoints.Default.BaseUri.AbsoluteUri);
        Assert.Equal(15, TeamRelayEndpoints.UndoIsle.AdvertisedMaxMembers);
    }

    [Fact]
    public void KLongDevSelection_RoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"team-relay-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "preference.json");
        try
        {
            var store = new TeamRelayPreferenceStore(path);
            store.Save(new TeamRelayPreferences { Provider = TeamRelayProvider.KLongDev });

            Assert.Equal(TeamRelayProvider.KLongDev, store.Load().Provider);
            Assert.Equal("https://isle-relay.klong.dev/", TeamRelayEndpoints.KLongDev.BaseUri.AbsoluteUri);
            Assert.Equal(10, TeamRelayEndpoints.KLongDev.AdvertisedMaxMembers);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
