using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

public class AnnexBSplitterTests
{
    // Tiny NAL units: the header byte, then (for slices) a first byte whose
    // top bit says whether the slice starts a new picture.
    private static readonly byte[] Sps = [0x67, 1, 2], Pps = [0x68, 3], Idr = [0x65, 0x88, 9], Slice = [0x41, 0x9A, 7];

    private static byte[] Stream(params byte[][] nals) =>
        nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    private static byte[] Unit(params byte[][] nals) => Stream(nals);

    private static List<byte[]> FeedByteByByte(AnnexBSplitter splitter, byte[] data)
    {
        var units = new List<byte[]>();
        foreach (byte b in data)
            units.AddRange(splitter.Feed([b])); // start codes split across reads too
        return units;
    }

    [Fact]
    public void GroupsNalUnitsIntoPictures()
    {
        var units = FeedByteByByte(new AnnexBSplitter(), Stream(Sps, Pps, Idr, Slice, Slice, Slice));
        // The last picture isn't complete until the next one starts.
        Assert.Equal([Unit(Sps, Pps, Idr), Unit(Slice), Unit(Slice)], units);
    }

    [Fact]
    public void WaitsForTheFirstKeyframe()
    {
        var units = new AnnexBSplitter().Feed(Stream(Slice, Slice, Sps, Pps, Idr, Slice, Slice));
        Assert.Equal([Unit(Sps, Pps, Idr), Unit(Slice)], units);
    }

    [Fact]
    public void RepeatsParameterSetsForKeyframesWithoutThem()
    {
        var units = new AnnexBSplitter().Feed(Stream(Sps, Pps, Idr, Slice, Idr, Slice));
        Assert.Equal([Unit(Sps, Pps, Idr), Unit(Slice), Unit(Sps, Pps, Idr)], units);
    }

    [Fact]
    public void AcceptsThreeByteStartCodes()
    {
        byte[] data = [0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 1, .. Idr, 0, 0, 1, .. Slice, 0, 0, 1, .. Slice];
        Assert.Equal([Unit(Sps, Pps, Idr), Unit(Slice)], new AnnexBSplitter().Feed(data));
    }
}
