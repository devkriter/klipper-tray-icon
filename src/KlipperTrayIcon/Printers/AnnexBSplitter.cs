using System;
using System.Collections.Generic;
using System.IO;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// Splits a raw H.264 byte stream (Annex B: NAL units after 00 00 01 start
/// codes, as camera-streamer's video.h264 sends it) into access units - one
/// picture each - for <see cref="H264Decoder"/>. Nothing is passed on until
/// the first keyframe, so decoding starts clean.
/// </summary>
public sealed class AnnexBSplitter
{
    private const int MaxBuffered = 8 * 1024 * 1024; // a stream that never sends a start code shouldn't eat memory
    private const int TypeSlice = 1, TypeIdr = 5, TypeSei = 6, TypeSps = 7, TypePps = 8, TypeAud = 9;
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private byte[] _buffer = new byte[512 * 1024];
    private int _length;
    private readonly MemoryStream _unit = new();
    private bool _unitHasPicture, _unitIsKeyframe, _unitHasSps;
    private byte[]? _sps, _pps;
    private bool _started;

    /// <summary>Adds bytes from the stream; returns the access units they completed (Annex B).</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        var output = new List<byte[]>();
        if (_length + data.Length > MaxBuffered)
            _length = 0;
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        // A NAL unit is complete once the next start code shows up.
        int start = _buffer.AsSpan(0, _length).IndexOf(StartCode.AsSpan(1));
        if (start < 0)
        {
            Keep(Math.Max(0, _length - 2)); // a start code may be split across reads
            return output;
        }
        int payload = start + 3;
        while (true)
        {
            int next = _buffer.AsSpan(payload, _length - payload).IndexOf(StartCode.AsSpan(1));
            if (next < 0)
                break;
            next += payload;
            int end = next;
            while (end > payload && _buffer[end - 1] == 0)
                end--; // the extra zero of a 4-byte start code, or padding
            OnNal(_buffer.AsSpan(payload, end - payload), output);
            payload = next + 3;
        }
        // The next NAL unit's first bytes already say whether it begins a new
        // picture; if so, the current one is done - pass it on without
        // waiting for the rest of the next.
        if (_unitHasPicture && _length - payload >= 2 && StartsUnit(_buffer.AsSpan(payload, 2)))
            Flush(output);
        Keep(payload - 3); // from the unfinished NAL's start code
        return output;
    }

    /// <summary>Whether a NAL unit (its first two bytes are enough) begins a new access unit.</summary>
    private static bool StartsUnit(ReadOnlySpan<byte> nal)
    {
        int type = nal[0] & 0x1F;
        // first_mb_in_slice == 0 (its Exp-Golomb code is a single 1 bit): a new picture.
        bool firstSlice = type is TypeSlice or TypeIdr && nal.Length > 1 && (nal[1] & 0x80) != 0;
        return type is TypeSei or TypeSps or TypePps or TypeAud or (>= 14 and <= 18) || firstSlice;
    }

    private void Keep(int from)
    {
        Buffer.BlockCopy(_buffer, from, _buffer, 0, _length - from);
        _length -= from;
    }

    private void OnNal(ReadOnlySpan<byte> nal, List<byte[]> output)
    {
        if (nal.IsEmpty)
            return;
        int type = nal[0] & 0x1F;
        bool picture = type is TypeSlice or TypeIdr;
        if (_unitHasPicture && StartsUnit(nal))
            Flush(output);

        if (type == TypeSps)
            _sps = nal.ToArray();
        else if (type == TypePps)
            _pps = nal.ToArray();
        _unit.Write(StartCode);
        _unit.Write(nal);
        _unitHasPicture |= picture;
        _unitIsKeyframe |= type == TypeIdr;
        _unitHasSps |= type == TypeSps;
    }

    private void Flush(List<byte[]> output)
    {
        _started |= _unitIsKeyframe;
        if (_started)
        {
            if (_unitIsKeyframe && !_unitHasSps && _sps != null && _pps != null)
            {
                // Parameter sets sent once, before an earlier keyframe: repeat them.
                var unit = new MemoryStream();
                unit.Write(StartCode);
                unit.Write(_sps);
                unit.Write(StartCode);
                unit.Write(_pps);
                _unit.WriteTo(unit);
                output.Add(unit.ToArray());
            }
            else
                output.Add(_unit.ToArray());
        }
        _unit.SetLength(0);
        _unitHasPicture = _unitIsKeyframe = _unitHasSps = false;
    }
}
