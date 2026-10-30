using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

public class RtpTests
{
    internal static byte[] Rtp(ushort seq, uint ts, bool marker, byte[] payload, int payloadType = 96) =>
        [0x80, (byte)((marker ? 0x80 : 0) | payloadType), (byte)(seq >> 8), (byte)seq,
         (byte)(ts >> 24), (byte)(ts >> 16), (byte)(ts >> 8), (byte)ts, 0, 0, 0, 1, .. payload];

    private static RtpPacket Parse(byte[] data)
    {
        Assert.True(RtpPacket.TryParse(data, out var packet));
        return packet;
    }

    [Fact]
    public void ParsesHeader()
    {
        var packet = Parse(Rtp(0xABCD, 0x01020304, true, [0x65, 1, 2]));
        Assert.True(packet.Marker);
        Assert.Equal(96, packet.PayloadType);
        Assert.Equal(0xABCD, packet.Sequence);
        Assert.Equal(0x01020304u, packet.Timestamp);
        Assert.Equal([0x65, 1, 2], packet.Payload.ToArray());
    }

    [Fact]
    public void SkipsCsrcsExtensionAndPadding()
    {
        byte[] data =
        [
            0xB1, 96, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, // V=2, padding, extension, 1 CSRC
            9, 9, 9, 9,                             // CSRC
            0xBE, 0xDE, 0, 1, 7, 7, 7, 7,           // extension: one 32-bit word
            0x41, 5, 6,                             // payload
            0, 0, 3,                                // 3 bytes of padding
        ];
        Assert.Equal([0x41, 5, 6], Parse(data).Payload.ToArray());
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(RtpPacket.TryParse(new byte[] { 0x80, 96, 0 }, out _));
        Assert.False(RtpPacket.TryParse(new byte[12] { 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _)); // version 1
    }
}

public class H264DepacketizerTests
{
    private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1E], Pps = [0x68, 0xCE, 0x38, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00], Slice = [0x41, 0x9A, 0x02];

    private static byte[] AnnexB(params byte[][] nals) => [.. nals.SelectMany(n => (byte[])[0, 0, 0, 1, .. n])];

    private readonly List<AccessUnit> _out = new();
    private H264Depacketizer _d = new();

    private void Push(ushort seq, uint ts, bool marker, byte[] payload)
    {
        Assert.True(RtpPacket.TryParse(RtpTests.Rtp(seq, ts, marker, payload), out var packet));
        _d.Push(packet, _out);
    }

    [Fact]
    public void SingleNalUnits()
    {
        Push(1, 100, false, Sps);
        Push(2, 100, false, Pps);
        Push(3, 100, true, Idr);
        Push(4, 200, true, Slice);
        Assert.Equal(2, _out.Count);
        Assert.Equal(AnnexB(Sps, Pps, Idr), _out[0].Data);
        Assert.True(_out[0].IsKeyframe);
        Assert.Equal(100u, _out[0].Timestamp);
        Assert.Equal(AnnexB(Slice), _out[1].Data);
        Assert.False(_out[1].IsKeyframe);
    }

    [Fact]
    public void StapA()
    {
        byte[] stap = [0x78, 0, (byte)Sps.Length, .. Sps, 0, (byte)Pps.Length, .. Pps];
        Push(1, 100, false, stap);
        Push(2, 100, true, Idr);
        Assert.Equal(AnnexB(Sps, Pps, Idr), Assert.Single(_out).Data);
    }

    [Fact]
    public void FuA()
    {
        // The IDR split in three: FU indicator keeps NRI (0x60) with type 28, FU header has S/E bits and type 5.
        Push(1, 100, false, [0x7C, 0x85, 0x88]);
        Push(2, 100, false, [0x7C, 0x05, 0x84]);
        Push(3, 100, true, [0x7C, 0x45, 0x00]);
        Assert.Equal(AnnexB([0x65, 0x88, 0x84, 0x00]), Assert.Single(_out).Data);
    }

    [Fact]
    public void WaitsForAKeyframe()
    {
        Push(1, 100, true, Slice); // can't be decoded without the frame before it
        Assert.Empty(_out);
        Push(2, 200, true, Idr);
        Push(3, 300, true, Slice);
        Assert.Equal(2, _out.Count);
    }

    [Fact]
    public void AddsSdpParameterSetsToKeyframesWithout()
    {
        _d = new H264Depacketizer([Sps, Pps]);
        Push(1, 100, true, Idr);
        Push(2, 200, false, Sps); // the stream's own SPS: nothing added
        Push(3, 200, true, Idr);
        Assert.Equal(AnnexB(Sps, Pps, Idr), _out[0].Data);
        Assert.Equal(AnnexB(Sps, Idr), _out[1].Data);
    }

    [Fact]
    public void LostPacketDropsFramesUntilNextKeyframe()
    {
        Push(1, 100, true, Idr);
        Push(2, 200, false, [0x7C, 0x81, 1]); // FU-A start of a P slice...
        Push(4, 200, true, [0x7C, 0x41, 3]);  // ...its middle (seq 3) never arrives
        Push(5, 300, true, Slice);            // references the broken frame
        Push(6, 400, true, Idr);
        Assert.Equal(2, _out.Count);
        Assert.Equal(100u, _out[0].Timestamp);
        Assert.Equal(400u, _out[1].Timestamp);
    }

    [Fact]
    public void NewTimestampEndsAFrameWhoseMarkerWasMissing()
    {
        Push(1, 100, false, Idr);
        Push(2, 200, true, Slice);
        Assert.Equal(2, _out.Count);
        Assert.Equal(AnnexB(Idr), _out[0].Data);
    }
}

public class RtspTests
{
    [Fact]
    public void ParsesSdpVideoTrack()
    {
        string sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Stream\r\nt=0 0\r\n" +
                     "m=audio 0 RTP/AVP 97\r\na=control:track2\r\n" +
                     "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n" +
                     "a=fmtp:96 packetization-mode=1;profile-level-id=42001E;sprop-parameter-sets=Z0IAHg==,aM44gA==\r\n" +
                     "a=control:track1\r\n";
        var video = SdpVideo.Parse(sdp);
        Assert.NotNull(video);
        Assert.Equal("track1", video.Control);
        Assert.Equal(96, video.PayloadType);
        Assert.Equal(2, video.ParameterSets.Count);
        Assert.Equal([0x67, 0x42, 0x00, 0x1E], video.ParameterSets[0]);
        Assert.Equal([0x68, 0xCE, 0x38, 0x80], video.ParameterSets[1]);
    }

    [Fact]
    public void IgnoresSdpWithoutH264() =>
        Assert.Null(SdpVideo.Parse("v=0\r\nm=video 0 RTP/AVP 26\r\na=rtpmap:26 JPEG/90000\r\n"));

    [Fact]
    public void AnswersDigestChallenge()
    {
        // The worked example from RFC 2069 (with its published erratum).
        string header = RtspClient.Authorization("Digest realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"",
                                                 "GET", "/dir/index.html", "Mufasa", "CircleOfLife");
        Assert.Contains("response=\"1949323746fe6a43ef61f9606e7febea\"", header);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header);
    }

    [Fact]
    public void AnswersBasicChallenge() =>
        Assert.Equal("Basic YmJscDoxMjM0NTY3OA==", RtspClient.Authorization("Basic realm=\"cam\"", "DESCRIBE", "rtsps://x/", "bblp", "12345678"));
}

/// <summary>
/// The whole Bambu camera path - TLS, RTSP with digest auth, interleaved
/// RTP, depacketizing and Windows' H.264 decoder - against a local server
/// that streams a short checked-in clip the way the printer does.
/// </summary>
public sealed class FakeBambuCameraTests : IAsyncLifetime
{
    private const string AccessCode = "12345678";
    private FakeRtspsCamera _camera = null!;

    public Task InitializeAsync()
    {
        _camera = new FakeRtspsCamera(AccessCode, Path.Combine(AppContext.BaseDirectory, "Assets", "smptebars_1080p.h264"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _camera.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DecodesLiveVideoAndHangsUpWhenDone()
    {
        using var backend = new BambuBackend("127.0.0.1", "SERIAL", AccessCode, cameraPort: _camera.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = DateTime.UtcNow;
        TimeSpan? firstFrameAfter = null;
        int frames = 0;
        await foreach (var frame in backend.GetWebcamFramesAsync(cts.Token))
        {
            using (frame)
            {
                firstFrameAfter ??= DateTime.UtcNow - started;
                var pixels = Assert.IsType<PixelFrame>(frame);
                // 1920x1080 is shown at half size; the decoder's 1088-line padding is cropped off.
                Assert.Equal(960, pixels.Width);
                Assert.Equal(540, pixels.Height);
                // SMPTE bars: 75% white, yellow, cyan, green, magenta, red, blue across the top.
                AssertColor(pixels, 1, 191, 191, 191);
                AssertColor(pixels, 3, 191, 191, 0);
                AssertColor(pixels, 5, 0, 191, 191);
                AssertColor(pixels, 7, 0, 191, 0);
                AssertColor(pixels, 9, 191, 0, 191);
                AssertColor(pixels, 11, 191, 0, 0);
                AssertColor(pixels, 13, 0, 0, 191);
            }
            if (++frames == 15)
                break; // like the popup hiding
        }
        Assert.Equal(15, frames);
        Assert.True(firstFrameAfter < TimeSpan.FromSeconds(3), $"first frame took {firstFrameAfter}");
        Assert.True(_camera.Authenticated);
        // The printer has one camera slot: it must be given back straight away.
        Assert.True(await _camera.ClientGone.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t => t.IsCompletedSuccessfully),
                    "connection still open after the stream was stopped");
    }

    [Fact]
    public async Task WrongAccessCodeEndsTheStream()
    {
        using var backend = new BambuBackend("127.0.0.1", "SERIAL", "wrong", cameraPort: _camera.Port);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (var frame in backend.GetWebcamFramesAsync(default))
                frame.Dispose();
        });
    }

    /// <summary>Checks the middle of bar <paramref name="fourteenths"/>/14 (bars are 1/7 of the width each) in the top half.</summary>
    private static void AssertColor(PixelFrame frame, int fourteenths, int r, int g, int b)
    {
        int x = frame.Width * fourteenths / 14, y = frame.Height / 4;
        int i = (y * frame.Width + x) * 4;
        byte[] p = frame.Pixels;
        const int tolerance = 24;
        Assert.True(Math.Abs(p[i + 2] - r) <= tolerance && Math.Abs(p[i + 1] - g) <= tolerance && Math.Abs(p[i] - b) <= tolerance,
                    $"bar {fourteenths}: expected RGB {r},{g},{b} got {p[i + 2]},{p[i + 1]},{p[i]}");
    }
}

/// <summary>
/// A camera server acting like a Bambu printer's: RTSP over TLS with a
/// self-signed certificate, digest auth (user "bblp"), RTP interleaved on the
/// TCP connection. Streams an Annex B H.264 file in a loop at 10 fps,
/// packetized as single NAL units, STAP-A and FU-A. One client at a time.
/// </summary>
internal sealed class FakeRtspsCamera : IDisposable
{
    private const string Realm = "IPCAM", Nonce = "0123456789abcdef";
    private const int MaxPayload = 1200;
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly string _accessCode;
    private readonly List<List<byte[]>> _accessUnits;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _clientGone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeRtspsCamera(string accessCode, string clipPath, int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _accessCode = accessCode;
        _accessUnits = SplitAccessUnits(File.ReadAllBytes(clipPath));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=bambu-camera-fake", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool Authenticated { get; private set; }
    /// <summary>Completes when a client that started playing has disconnected.</summary>
    public Task ClientGone => _clientGone.Task;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _cert.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            try { await ServeAsync(client); }
            catch (Exception) { }
            finally { client.Dispose(); }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var ssl = new SslStream(client.GetStream());
        await ssl.AuthenticateAsServerAsync(_cert);
        var reader = new StreamReader(ssl, Encoding.ASCII);
        var writeLock = new SemaphoreSlim(1, 1);
        bool playing = false;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Task? streaming = null;
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null)
                    break;
                if (line.Length == 0)
                    continue;
                string[] requestLine = line.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (string? h; !string.IsNullOrEmpty(h = await reader.ReadLineAsync());)
                    headers[h[..h.IndexOf(':')]] = h[(h.IndexOf(':') + 1)..].Trim();
                string method = requestLine[0], url = requestLine[1], cseq = headers.GetValueOrDefault("CSeq", "0");

                string response;
                if (!IsAuthorized(method, url, headers.GetValueOrDefault("Authorization")))
                    response = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Digest realm=\"{Realm}\", nonce=\"{Nonce}\"\r\n\r\n";
                else if (method == "DESCRIBE")
                {
                    Authenticated = true;
                    var sets = _accessUnits.SelectMany(au => au).Where(n => (n[0] & 0x1F) is 7 or 8).Take(2);
                    string sdp = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=fake\r\nt=0 0\r\nm=video 0 RTP/AVP 96\r\n" +
                                 "a=rtpmap:96 H264/90000\r\n" +
                                 $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={string.Join(",", sets.Select(Convert.ToBase64String))}\r\n" +
                                 "a=control:track1\r\n";
                    response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Base: {url}/\r\nContent-Type: application/sdp\r\n" +
                               $"Content-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n{sdp}";
                }
                else if (method == "SETUP")
                    response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nSession: 66334873;timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n\r\n";
                else if (method == "TEARDOWN")
                {
                    await WriteAsync(ssl, writeLock, Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n\r\n"));
                    break;
                }
                else
                    response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n\r\n";
                await WriteAsync(ssl, writeLock, Encoding.ASCII.GetBytes(response));

                if (method == "PLAY" && !playing)
                {
                    playing = true;
                    streaming = Task.Run(() => StreamAsync(ssl, writeLock, connection.Token));
                }
            }
        }
        finally
        {
            connection.Cancel();
            if (streaming != null)
                await streaming.ContinueWith(_ => { });
            if (playing)
                _clientGone.TrySetResult();
        }
    }

    private bool IsAuthorized(string method, string url, string? authorization)
    {
        if (authorization == null)
            return false;
        string expected = RtspClient.Authorization($"Digest realm=\"{Realm}\", nonce=\"{Nonce}\"", method, url, "bblp", _accessCode);
        return authorization == expected;
    }

    private async Task StreamAsync(SslStream ssl, SemaphoreSlim writeLock, CancellationToken ct)
    {
        ushort seq = 1000;
        uint timestamp = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var au in _accessUnits)
                {
                    var packets = Packetize(au, ref seq, timestamp);
                    foreach (var packet in packets)
                        await WriteAsync(ssl, writeLock, [(byte)'$', 0, (byte)(packet.Length >> 8), (byte)packet.Length, .. packet], ct);
                    timestamp += 9000; // 10 fps on the 90 kHz clock
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (Exception) { } // client went away
    }

    private static async Task WriteAsync(Stream stream, SemaphoreSlim writeLock, byte[] data, CancellationToken ct = default)
    {
        await writeLock.WaitAsync(ct);
        try { await stream.WriteAsync(data, ct); }
        finally { writeLock.Release(); }
    }

    private static List<byte[]> Packetize(List<byte[]> au, ref ushort seq, uint timestamp)
    {
        var payloads = new List<byte[]>();
        var small = new List<byte[]>(); // parameter sets go out together as one STAP-A
        foreach (var nal in au)
        {
            int type = nal[0] & 0x1F;
            if (type is 7 or 8)
            {
                small.Add(nal);
                continue;
            }
            if (small.Count > 0)
            {
                payloads.Add([(byte)((small[0][0] & 0x60) | 24), .. small.SelectMany(n => (byte[])[(byte)(n.Length >> 8), (byte)n.Length, .. n])]);
                small.Clear();
            }
            if (nal.Length <= MaxPayload)
            {
                payloads.Add(nal);
                continue;
            }
            for (int offset = 1; offset < nal.Length; offset += MaxPayload)
            {
                int length = Math.Min(MaxPayload, nal.Length - offset);
                byte header = (byte)(type | (offset == 1 ? 0x80 : 0) | (offset + length == nal.Length ? 0x40 : 0));
                payloads.Add([(byte)((nal[0] & 0xE0) | 28), header, .. nal.AsSpan(offset, length)]);
            }
        }
        var packets = new List<byte[]>();
        for (int i = 0; i < payloads.Count; i++)
            packets.Add(RtpTests.Rtp(seq++, timestamp, i == payloads.Count - 1, payloads[i]));
        return packets;
    }

    /// <summary>Splits an Annex B stream into NAL units grouped per picture.</summary>
    private static List<List<byte[]>> SplitAccessUnits(byte[] stream)
    {
        var nals = new List<byte[]>();
        int start = -1;
        for (int i = 0; i + 3 <= stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                if (start >= 0)
                    nals.Add(stream[start..(i > 0 && stream[i - 1] == 0 ? i - 1 : i)]);
                start = i + 3;
                i += 2;
            }
        }
        if (start >= 0)
            nals.Add(stream[start..]);

        var units = new List<List<byte[]>>();
        List<byte[]>? current = null;
        bool hasPicture = false;
        foreach (var nal in nals)
        {
            int type = nal[0] & 0x1F;
            bool vcl = type is 1 or 5;
            bool firstSliceOfPicture = vcl && (nal[1] & 0x80) != 0; // first_mb_in_slice == 0
            if (current == null || (hasPicture && (!vcl || firstSliceOfPicture)))
            {
                current = new List<byte[]>();
                units.Add(current);
                hasPicture = false;
            }
            current.Add(nal);
            hasPicture |= vcl;
        }
        return units;
    }
}
