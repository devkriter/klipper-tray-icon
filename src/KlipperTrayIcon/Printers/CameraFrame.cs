using System;
using System.Buffers;
using System.Threading;

namespace KlipperTrayIcon.Printers;

/// <summary>One picture from a printer's camera.</summary>
public abstract class CameraFrame : IDisposable
{
    public virtual void Dispose() { }
}

/// <summary>A JPEG, as MJPEG webcams (Klipper) send them.</summary>
public sealed class JpegFrame(byte[] data) : CameraFrame
{
    public byte[] Data { get; } = data;
}

/// <summary>
/// An already-decoded picture in 32-bit BGRX (rows of Width * 4 bytes). The
/// pixel buffer is pooled: Dispose once it has been copied out.
/// </summary>
public sealed class PixelFrame : CameraFrame
{
    private byte[]? _pixels;

    public PixelFrame(int width, int height)
    {
        Width = width;
        Height = height;
        _pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    /// <summary>May be longer than Stride * Height.</summary>
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(PixelFrame));

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is { } pixels)
            ArrayPool<byte>.Shared.Return(pixels);
    }
}
