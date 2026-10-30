using System;
using System.Collections.Generic;
using System.IO;

namespace KlipperTrayIcon.Printers;

/// <summary>The parts of an RTP packet (RFC 3550) a video receiver needs.</summary>
public readonly record struct RtpPacket(bool Marker, int PayloadType, ushort Sequence, uint Timestamp, ReadOnlyMemory<byte> Payload)
{
    public static bool TryParse(ReadOnlyMemory<byte> packet, out RtpPacket rtp)
    {
        rtp = default;
        var b = packet.Span;
        if (b.Length < 12 || b[0] >> 6 != 2)
            return false;
        int offset = 12 + (b[0] & 0x0F) * 4; // CSRCs
        if ((b[0] & 0x10) != 0) // header extension
        {
            if (b.Length < offset + 4)
                return false;
            offset += 4 + ((b[offset + 2] << 8) | b[offset + 3]) * 4;
        }
        int end = b.Length;
        if ((b[0] & 0x20) != 0) // padding; the last byte says how much
            end -= b[^1];
        if (end < offset)
            return false;
        rtp = new RtpPacket(
            Marker: (b[1] & 0x80) != 0,
            PayloadType: b[1] & 0x7F,
            Sequence: (ushort)((b[2] << 8) | b[3]),
            Timestamp: (uint)((b[4] << 24) | (b[5] << 16) | (b[6] << 8) | b[7]),
            Payload: packet[offset..end]);
        return true;
    }
}

/// <summary>One video frame's NAL units, in Annex B form (each after a 00 00 00 01 start code).</summary>
public sealed record AccessUnit(byte[] Data, uint Timestamp, bool IsKeyframe);

/// <summary>
/// Turns H.264 RTP payloads (RFC 6184: single NAL unit, STAP-A and FU-A
/// packets) back into whole access units for a decoder. Output starts at the
/// first keyframe, and after a lost packet resumes at the next one, so the
/// decoder never sees a picture whose references are missing.
/// </summary>
public sealed class H264Depacketizer
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    private const int TypeIdr = 5, TypeSps = 7, TypeStapA = 24, TypeFuA = 28;

    private readonly byte[][] _parameterSets;
    private readonly List<byte[]> _nals = new();
    private MemoryStream? _fragment; // FU-A being reassembled
    private uint _timestamp;
    private ushort? _expectedSequence;
    private bool _broken; // the current access unit lost a packet
    private bool _waitForKeyframe = true;

    /// <param name="parameterSets">SPS/PPS from the SDP (sprop-parameter-sets); put in front of keyframes that lack their own.</param>
    public H264Depacketizer(IEnumerable<byte[]>? parameterSets = null)
    {
        _parameterSets = parameterSets == null ? [] : [.. parameterSets];
    }

    /// <summary>Feeds one RTP packet; adds any access units it completes to <paramref name="output"/>.</summary>
    public void Push(RtpPacket packet, List<AccessUnit> output)
    {
        if (_expectedSequence is ushort expected && packet.Sequence != expected)
        {
            _broken = true;
            _fragment = null;
        }
        _expectedSequence = (ushort)(packet.Sequence + 1);

        // A new timestamp means a new picture, even if the last one's marker got lost.
        if (packet.Timestamp != _timestamp && (_nals.Count > 0 || _fragment != null))
            Flush(output);
        _timestamp = packet.Timestamp;

        ReadPayload(packet.Payload.Span);
        if (packet.Marker)
            Flush(output);
    }

    private void ReadPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;
        int type = payload[0] & 0x1F;
        if (type is >= 1 and <= 23)
        {
            _nals.Add(payload.ToArray());
        }
        else if (type == TypeStapA)
        {
            var rest = payload[1..];
            while (rest.Length >= 2)
            {
                int size = (rest[0] << 8) | rest[1];
                if (size == 0 || rest.Length < 2 + size)
                {
                    _broken = true;
                    break;
                }
                _nals.Add(rest.Slice(2, size).ToArray());
                rest = rest[(2 + size)..];
            }
        }
        else if (type == TypeFuA && payload.Length >= 2)
        {
            byte indicator = payload[0], header = payload[1];
            bool start = (header & 0x80) != 0, end = (header & 0x40) != 0;
            if (start)
            {
                _fragment = new MemoryStream();
                _fragment.WriteByte((byte)((indicator & 0xE0) | (header & 0x1F)));
            }
            else if (_fragment == null)
            {
                _broken = true; // its start went missing
                return;
            }
            _fragment.Write(payload[2..]);
            if (end)
            {
                _nals.Add(_fragment.ToArray());
                _fragment = null;
            }
        }
        // STAP-B, MTAP and FU-B only exist in interleaved packetization mode, which cameras don't use.
    }

    private void Flush(List<AccessUnit> output)
    {
        if (_fragment != null)
            _broken = true; // ended mid-NAL
        bool keyframe = _nals.Exists(n => (n[0] & 0x1F) == TypeIdr);
        bool usable = _nals.Count > 0 && !_broken;
        if (!usable)
            _waitForKeyframe = true;
        else if (keyframe || !_waitForKeyframe)
        {
            _waitForKeyframe = false;
            var au = new MemoryStream();
            if (keyframe && !_nals.Exists(n => (n[0] & 0x1F) == TypeSps))
            {
                foreach (var set in _parameterSets)
                {
                    au.Write(StartCode);
                    au.Write(set);
                }
            }
            foreach (var nal in _nals)
            {
                au.Write(StartCode);
                au.Write(nal);
            }
            output.Add(new AccessUnit(au.ToArray(), _timestamp, keyframe));
        }
        _nals.Clear();
        _fragment = null;
        _broken = false;
    }
}
