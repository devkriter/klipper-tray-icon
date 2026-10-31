using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The vector NV12-to-BGRX rows give exactly what the per-pixel formula does.</summary>
public unsafe class ColorConversionTests
{
    private const int Width = 37, Stride = 96; // odd width: the per-pixel tail does the rest

    [Fact]
    public void FullSizeRowMatchesThePerPixelFormula()
    {
        var (luma, chroma) = RandomNv12();
        var output = new uint[Width];
        fixed (byte* y = luma, uv = chroma)
        fixed (uint* o = output)
        {
            int done = H264Decoder.ConvertRowFull(y, uv, o, Width);
            Assert.True(done >= 32, $"vector code did {done}");
            for (int x = 0; x < done; x++)
                Assert.Equal(H264Decoder.ToBgrx(luma[x], chroma[x & ~1] - 128, chroma[(x & ~1) + 1] - 128), output[x]);
        }
    }

    [Fact]
    public void HalfSizeRowMatchesThePerPixelFormula()
    {
        var (luma, chroma) = RandomNv12();
        var output = new uint[Width / 2];
        fixed (byte* y = luma, uv = chroma)
        fixed (uint* o = output)
        {
            int done = H264Decoder.ConvertRowHalf(y, uv, o, Width / 2, Stride);
            Assert.True(done >= 16, $"vector code did {done}");
            for (int x = 0; x < done; x++)
            {
                int sx = 2 * x;
                int lum = (luma[sx] + luma[sx + 1] + luma[sx + Stride] + luma[sx + Stride + 1] + 2) >> 2;
                Assert.Equal(H264Decoder.ToBgrx(lum, chroma[sx] - 128, chroma[sx + 1] - 128), output[x]);
            }
        }
    }

    [Fact]
    public void ExtremeValuesAreClamped()
    {
        Assert.Equal(0xFF000000u, H264Decoder.ToBgrx(0, 0, 0));       // below black
        Assert.Equal(0xFFFFFFFFu, H264Decoder.ToBgrx(255, 0, 0));     // above white
        Assert.Equal(0xFFFF0000u, H264Decoder.ToBgrx(81, -38, 112));  // pure red (BT.601 Y=81, U=90, V=240)
    }

    /// <summary>Two rows of luma and one of chroma with every extreme mixed in.</summary>
    private static (byte[] Luma, byte[] Chroma) RandomNv12()
    {
        var random = new Random(42);
        var luma = new byte[Stride * 2];
        var chroma = new byte[Stride];
        random.NextBytes(luma);
        random.NextBytes(chroma);
        luma[0] = 0; luma[1] = 255; chroma[0] = 0; chroma[1] = 255; chroma[2] = 255; chroma[3] = 0;
        return (luma, chroma);
    }
}

public class DecoderSizeTests
{
    [Fact]
    public void ConvertsAtTheSizeThePictureIsShown()
    {
        var clip = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "smptebars_1080p.h264"));
        var units = new AnnexBSplitter().Feed(clip);
        using var decoder = new H264Decoder();
        var sizes = new List<string>();
        long time = 0;
        foreach (var (unit, i) in units.Select((u, i) => (u, i)))
        {
            // The popup grows past half the video's width partway through.
            decoder.TargetWidth = i < units.Count / 2 ? 960 : 1600;
            foreach (var frame in decoder.Decode(unit, time += 333_333))
            {
                sizes.Add($"{frame.Width}x{frame.Height}");
                frame.Dispose();
            }
        }
        Assert.Equal("960x540", sizes[0]);
        Assert.Equal("1920x1080", sizes[^1]);
    }
}
