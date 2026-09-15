using System.IO;

namespace TheIsleOverlay.App.Tests;

public sealed class MapNoteStoreTests
{
    [Fact]
    public void Store_AddsEditsDeletesAndRoundTripsGatewayNotes()
    {
        var path = TemporaryPath();
        try
        {
            var store = new MapNoteStore(path);
            var note = store.AddDefault(0.25d, 0.75d);
            Assert.Equal(MapNoteKind.Pin, note.Kind);
            Assert.Equal(0.25d, note.U);
            Assert.Equal(0.75d, note.V);

            Assert.True(store.ChangeKind(note.Id, MapNoteKind.Water));
            var restored = new MapNoteStore(path);
            var saved = Assert.Single(restored.Notes);
            Assert.Equal(MapNoteKind.Water, saved.Kind);
            Assert.Equal(note.WorldX, saved.WorldX, precision: 6);
            Assert.Equal(note.WorldY, saved.WorldY, precision: 6);

            Assert.True(restored.Delete(note.Id));
            Assert.Empty(new MapNoteStore(path).Notes);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public void Store_ClampsCoordinatesAndRecoversFromMalformedJson()
    {
        var path = TemporaryPath();
        try
        {
            var store = new MapNoteStore(path);
            var note = store.AddDefault(double.NaN, 5d);
            Assert.Equal(0.5d, note.U);
            Assert.Equal(1d, note.V);

            File.WriteAllText(path, "{broken");
            Assert.Empty(new MapNoteStore(path).Notes);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public void Store_FailedDeleteRestoresMemoryAndReturnsDurableError()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IsleLiveMap.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "map-notes.json");
        try
        {
            var store = new MapNoteStore(path);
            var note = store.AddDefault(0.25d, 0.75d);

            File.Delete(path);
            Directory.Delete(directory, recursive: true);
            File.WriteAllText(directory, "parent path is now a file");

            var result = store.TryDelete(note.Id);

            Assert.False(result.Success);
            Assert.Contains("Không thể lưu", result.Error);
            Assert.Contains(store.Notes, candidate => candidate.Id == note.Id);
        }
        finally
        {
            if (File.Exists(directory)) File.Delete(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string TemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        "IsleLiveMap.Tests",
        Guid.NewGuid().ToString("N"),
        "map-notes.json");

    private static void DeleteDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
