using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// Decodes H.264 with the decoder built into Windows (Media Foundation's
/// H.264 decoder MFT), so no codec has to ship with the app. Output is BGRX,
/// halved in size for 1280px-and-wider video - the popup is 640 px wide.
/// COM is called through raw vtable slots (the slot numbers come from the
/// Windows SDK headers), which keeps this to a few calls with no wrappers.
/// </summary>
public sealed unsafe partial class H264Decoder : IDisposable
{
    private static readonly Guid CLSID_MSH264DecoderMFT = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    // vtable slots: IUnknown 0-2, IMFAttributes 3-32, then the derived interface's own.
    private const int Release = 2;
    private const int Attr_GetUINT32 = 7, Attr_GetUINT64 = 8, Attr_GetBlob = 15, Attr_SetUINT32 = 21, Attr_GetGUID = 10, Attr_SetGUID = 24;
    private const int Sample_SetSampleTime = 36, Sample_ConvertToContiguousBuffer = 41, Sample_AddBuffer = 42;
    private const int Buffer_Lock = 3, Buffer_Unlock = 4, Buffer_SetCurrentLength = 6;
    private const int Mft_GetOutputStreamInfo = 7, Mft_GetAttributes = 8, Mft_GetOutputAvailableType = 14,
                      Mft_SetInputType = 15, Mft_SetOutputType = 16, Mft_ProcessMessage = 23,
                      Mft_ProcessInput = 24, Mft_ProcessOutput = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags, cbSize, cbAlignment; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public void* pSample; public uint dwStatus; public void* pEvents; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFVideoArea { public int OffsetX, OffsetY, Width, Height; } // offsets are 16.16 fixed point

    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFShutdown();
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(void** type);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateSample(void** sample);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMemoryBuffer(uint maxLength, void** buffer);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, void* outer, uint context, in Guid iid, void** obj);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(void* reserved, uint coInit);

    private void* _mft;
    private uint _outBytes; // size of the output samples we allocate, when the decoder doesn't
    private bool _decoderAllocates;
    private bool _outputTypeSet;
    private int _bufferWidth, _bufferHeight, _stride;
    private MFVideoArea _visible;

    public H264Decoder()
    {
        // Usually a no-op: .NET's thread pool threads are already in the MTA.
        CoInitializeEx(null, 0 /* COINIT_MULTITHREADED */);
        Check(MFStartup(0x00020070 /* MF_VERSION */, 1 /* MFSTARTUP_LITE */));
        try
        {
            void* mft;
            Check(CoCreateInstance(CLSID_MSH264DecoderMFT, null, 1 /* CLSCTX_INPROC_SERVER */, IID_IMFTransform, &mft));
            _mft = mft;

            // Without this the decoder holds back several frames before showing any.
            void* attributes;
            if (Call(_mft, Mft_GetAttributes, &attributes) >= 0 && attributes != null)
            {
                Guid key = CODECAPI_AVLowLatencyMode;
                ((delegate* unmanaged<void*, Guid*, uint, int>)Slot(attributes, Attr_SetUINT32))(attributes, &key, 1);
                Unref(attributes);
            }

            void* type;
            Check(MFCreateMediaType(&type));
            try
            {
                SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat_H264);
                Check(((delegate* unmanaged<void*, uint, void*, uint, int>)Slot(_mft, Mft_SetInputType))(_mft, 0, type, 0));
            }
            finally { Unref(type); }

            TrySetOutputType(); // may have to wait for the stream's SPS
            ((delegate* unmanaged<void*, uint, nuint, int>)Slot(_mft, Mft_ProcessMessage))(_mft, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            ((delegate* unmanaged<void*, uint, nuint, int>)Slot(_mft, Mft_ProcessMessage))(_mft, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Decodes one access unit (Annex B); returns the pictures it completed, usually one.</summary>
    public List<PixelFrame> Decode(ReadOnlySpan<byte> accessUnit, long time100ns)
    {
        var frames = new List<PixelFrame>();
        void* sample = CreateSample(accessUnit, time100ns);
        try
        {
            while (true)
            {
                int hr = ((delegate* unmanaged<void*, uint, void*, uint, int>)Slot(_mft, Mft_ProcessInput))(_mft, 0, sample, 0);
                if (hr != MF_E_NOTACCEPTING)
                {
                    Check(hr);
                    break;
                }
                if (!Drain(frames)) // full, but has nothing to give: give up on this one
                    break;
            }
        }
        finally { Unref(sample); }
        Drain(frames);
        return frames;
    }

    /// <summary>Collects every picture the decoder has ready. False if it had none.</summary>
    private bool Drain(List<PixelFrame> frames)
    {
        bool any = false;
        while (true)
        {
            if (!_outputTypeSet && !TrySetOutputType())
                return any;
            var output = new MFT_OUTPUT_DATA_BUFFER { pSample = _decoderAllocates ? null : NewOutSample() };
            uint status;
            int hr = ((delegate* unmanaged<void*, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)Slot(_mft, Mft_ProcessOutput))(
                _mft, 0, 1, &output, &status);
            if (output.pEvents != null)
                Unref(output.pEvents);
            try
            {
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
                    return any;
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE || hr == MF_E_TRANSFORM_TYPE_NOT_SET)
                {
                    _outputTypeSet = false; // the picture size is known (or changed): pick the output format again
                    continue;
                }
                Check(hr);
                frames.Add(Convert(output.pSample));
                any = true;
            }
            finally
            {
                if (output.pSample != null)
                    Unref(output.pSample);
            }
        }
    }

    private bool TrySetOutputType()
    {
        for (uint i = 0; ; i++)
        {
            void* type;
            if (((delegate* unmanaged<void*, uint, uint, void**, int>)Slot(_mft, Mft_GetOutputAvailableType))(_mft, 0, i, &type) < 0)
                return false;
            try
            {
                if (GetGuid(type, MF_MT_SUBTYPE) != MFVideoFormat_NV12)
                    continue;
                if (((delegate* unmanaged<void*, uint, void*, uint, int>)Slot(_mft, Mft_SetOutputType))(_mft, 0, type, 0) < 0)
                    return false;

                ulong size = GetUInt64(type, MF_MT_FRAME_SIZE) ?? 0;
                _bufferWidth = (int)(size >> 32);
                _bufferHeight = (int)(size & 0xFFFFFFFF);
                uint? stride = GetUInt32(type, MF_MT_DEFAULT_STRIDE);
                _stride = stride is uint s && (int)s >= _bufferWidth ? (int)s : _bufferWidth;
                // The decoder pads the picture to whole 16x16 macroblocks (1080 -> 1088); this is the real picture.
                MFVideoArea area;
                uint blobSize;
                Guid apertureKey = MF_MT_MINIMUM_DISPLAY_APERTURE;
                bool hasArea = ((delegate* unmanaged<void*, Guid*, byte*, uint, uint*, int>)Slot(type, Attr_GetBlob))(
                    type, &apertureKey, (byte*)&area, (uint)sizeof(MFVideoArea), &blobSize) >= 0;
                _visible = hasArea && area.Width > 0 && area.Height > 0
                    ? area with { OffsetX = area.OffsetX >> 16, OffsetY = area.OffsetY >> 16 }
                    : new MFVideoArea { Width = _bufferWidth, Height = _bufferHeight };

                MFT_OUTPUT_STREAM_INFO info;
                Check(((delegate* unmanaged<void*, uint, MFT_OUTPUT_STREAM_INFO*, int>)Slot(_mft, Mft_GetOutputStreamInfo))(_mft, 0, &info));
                _decoderAllocates = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                _outBytes = Math.Max(info.cbSize, (uint)(_stride * _bufferHeight * 3 / 2));
                _outputTypeSet = true;
                return true;
            }
            finally { Unref(type); }
        }
    }

    /// <summary>
    /// An empty sample for one decoded picture. A fresh one every time: the
    /// decoder fails (E_FAIL) when handed back a sample it has already filled.
    /// </summary>
    private void* NewOutSample()
    {
        void* sample, buffer;
        Check(MFCreateSample(&sample));
        try
        {
            Check(MFCreateMemoryBuffer(_outBytes, &buffer));
            try { Check(((delegate* unmanaged<void*, void*, int>)Slot(sample, Sample_AddBuffer))(sample, buffer)); }
            finally { Unref(buffer); }
            return sample;
        }
        catch
        {
            Unref(sample);
            throw;
        }
    }

    private static void* CreateSample(ReadOnlySpan<byte> data, long time)
    {
        void* sample = null, buffer = null;
        try
        {
            Check(MFCreateSample(&sample));
            Check(MFCreateMemoryBuffer((uint)data.Length, &buffer));
            byte* ptr;
            uint max, current;
            Check(((delegate* unmanaged<void*, byte**, uint*, uint*, int>)Slot(buffer, Buffer_Lock))(buffer, &ptr, &max, &current));
            data.CopyTo(new Span<byte>(ptr, data.Length));
            ((delegate* unmanaged<void*, int>)Slot(buffer, Buffer_Unlock))(buffer);
            Check(((delegate* unmanaged<void*, uint, int>)Slot(buffer, Buffer_SetCurrentLength))(buffer, (uint)data.Length));
            Check(((delegate* unmanaged<void*, void*, int>)Slot(sample, Sample_AddBuffer))(sample, buffer));
            Check(((delegate* unmanaged<void*, long, int>)Slot(sample, Sample_SetSampleTime))(sample, time));
            return sample;
        }
        catch
        {
            if (sample != null) Unref(sample);
            throw;
        }
        finally
        {
            if (buffer != null) Unref(buffer);
        }
    }

    /// <summary>NV12 (BT.601, limited range) to BGRX, cropped to the visible picture.</summary>
    private PixelFrame Convert(void* sample)
    {
        void* buffer;
        Check(((delegate* unmanaged<void*, void**, int>)Slot(sample, Sample_ConvertToContiguousBuffer))(sample, &buffer));
        try
        {
            byte* data;
            uint max, length;
            Check(((delegate* unmanaged<void*, byte**, uint*, uint*, int>)Slot(buffer, Buffer_Lock))(buffer, &data, &max, &length));
            try
            {
                int scale = _visible.Width >= 1280 ? 2 : 1;
                int width = _visible.Width / scale, height = _visible.Height / scale;
                var frame = new PixelFrame(width, height);
                byte* luma = data, chroma = data + (long)_stride * _bufferHeight;
                if (length < (long)_stride * _bufferHeight * 3 / 2)
                    throw new InvalidOperationException("Decoder returned a short picture");
                fixed (byte* pixels = frame.Pixels)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int sy = _visible.OffsetY + y * scale;
                        byte* yRow = luma + (long)sy * _stride + _visible.OffsetX;
                        byte* uvRow = chroma + (long)(sy / 2) * _stride + (_visible.OffsetX & ~1);
                        byte* outRow = pixels + (long)y * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int sx = x * scale;
                            int lum = scale == 1
                                ? yRow[sx]
                                : (yRow[sx] + yRow[sx + 1] + yRow[sx + _stride] + yRow[sx + _stride + 1] + 2) >> 2;
                            int c = 298 * (lum - 16);
                            int u = uvRow[sx & ~1] - 128, v = uvRow[(sx & ~1) + 1] - 128;
                            outRow[x * 4 + 0] = Clamp((c + 516 * u + 128) >> 8);
                            outRow[x * 4 + 1] = Clamp((c - 100 * u - 208 * v + 128) >> 8);
                            outRow[x * 4 + 2] = Clamp((c + 409 * v + 128) >> 8);
                            outRow[x * 4 + 3] = 255;
                        }
                    }
                }
                return frame;
            }
            finally { ((delegate* unmanaged<void*, int>)Slot(buffer, Buffer_Unlock))(buffer); }
        }
        finally { Unref(buffer); }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Dispose()
    {
        if (_mft != null)
        {
            Unref(_mft);
            _mft = null;
            MFShutdown();
        }
        GC.SuppressFinalize(this);
    }

    ~H264Decoder() => Dispose();

    // ---- COM plumbing ----------------------------------------------------------------

    private static void* Slot(void* obj, int index) => (*(void***)obj)[index];
    private static int Call(void* obj, int slot, void** result) => ((delegate* unmanaged<void*, void**, int>)Slot(obj, slot))(obj, result);
    private static void Unref(void* obj) => ((delegate* unmanaged<void*, uint>)Slot(obj, Release))(obj);
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private static void SetGuid(void* attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged<void*, Guid*, Guid*, int>)Slot(attributes, Attr_SetGUID))(attributes, &key, &value));

    private static Guid GetGuid(void* attributes, Guid key)
    {
        Guid value;
        return ((delegate* unmanaged<void*, Guid*, Guid*, int>)Slot(attributes, Attr_GetGUID))(attributes, &key, &value) >= 0 ? value : Guid.Empty;
    }

    private static uint? GetUInt32(void* attributes, Guid key)
    {
        uint value;
        return ((delegate* unmanaged<void*, Guid*, uint*, int>)Slot(attributes, Attr_GetUINT32))(attributes, &key, &value) >= 0 ? value : null;
    }

    private static ulong? GetUInt64(void* attributes, Guid key)
    {
        ulong value;
        return ((delegate* unmanaged<void*, Guid*, ulong*, int>)Slot(attributes, Attr_GetUINT64))(attributes, &key, &value) >= 0 ? value : null;
    }
}
