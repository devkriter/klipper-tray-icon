using KlipperTrayIcon.Printers;
using KlipperTrayIcon.UI;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The stream list in Settings.</summary>
public class StreamItemsTests
{
    [Fact]
    public void Lists_every_stream_and_greys_out_those_not_offered()
    {
        var items = SettingsWindow.StreamItems([StreamKind.Mjpeg, StreamKind.Snapshots], "");

        Assert.Equal(["", "rtsp", "h264", "mjpeg", "snapshots"], items.Select(i => i.Name));
        Assert.Equal("Automatic (MJPEG)", items[0].Label);
        Assert.Equal([true, false, false, true, true], items.Select(i => i.Available));
        Assert.Equal("Not offered by this camera.", items[1].Description);
    }

    [Fact]
    public void A_saved_stream_not_offered_says_automatic_is_used()
    {
        var items = SettingsWindow.StreamItems([StreamKind.Mjpeg, StreamKind.Snapshots], "h264");

        var h264 = items.Single(i => i.Name == "h264");
        Assert.False(h264.Available);
        Assert.Contains("Automatic is used instead", h264.Description);
    }

    [Fact]
    public void Before_the_camera_is_asked_everything_can_be_picked()
    {
        foreach (var kinds in new IReadOnlyList<StreamKind>?[] { null, [] })
        {
            var items = SettingsWindow.StreamItems(kinds, "");
            Assert.Equal(5, items.Count);
            Assert.Equal("Automatic", items[0].Label);
            Assert.All(items, i => Assert.True(i.Available));
        }
    }
}
