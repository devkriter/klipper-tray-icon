using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>The video track of an RTSP DESCRIBE answer.</summary>
public sealed record SdpVideo(string Control, int PayloadType, string Codec, IReadOnlyList<byte[]> ParameterSets)
{
    /// <summary>The first H.264 video track in an SDP document, or null.</summary>
    public static SdpVideo? Parse(string sdp)
    {
        bool inVideo = false;
        string control = "*";
        int payloadType = -1;
        string codec = "";
        byte[][] sets = [];
        foreach (var raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("m="))
            {
                if (inVideo)
                    break; // only the first video track
                inVideo = line.StartsWith("m=video ");
                if (inVideo)
                {
                    var fields = line.Split(' ');
                    if (fields.Length > 3)
                        int.TryParse(fields[3], out payloadType);
                }
                continue;
            }
            if (!inVideo)
                continue;
            if (line.StartsWith("a=control:"))
                control = line["a=control:".Length..].Trim();
            else if (line.StartsWith($"a=rtpmap:{payloadType} "))
                codec = line.Split(' ')[1].Split('/')[0];
            else if (line.StartsWith($"a=fmtp:{payloadType} ") &&
                     Regex.Match(line, @"sprop-parameter-sets=([^;\s]+)") is { Success: true } m)
            {
                try { sets = m.Groups[1].Value.Split(',').Where(s => s.Length > 0).Select(Convert.FromBase64String).ToArray(); }
                catch (FormatException) { sets = []; }
            }
        }
        return inVideo && codec.Equals("H264", StringComparison.OrdinalIgnoreCase)
            ? new SdpVideo(control, payloadType, codec, sets)
            : null;
    }
}

/// <summary>
/// A minimal RTSP client (RFC 2326) for one H.264 camera stream: DESCRIBE,
/// SETUP and PLAY with the video interleaved on the same TCP connection
/// (RTP over TCP - no UDP ports or firewall prompts). rtsps:// runs it all
/// over TLS; the certificate isn't checked, since printers use self-signed ones.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    // A camera that sends nothing for this long has stalled.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly Uri _uri;
    private readonly string _url; // without credentials
    private readonly string _user, _password;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[256 * 1024];
    private int _start, _end;
    private TcpClient? _tcp;
    private Stream? _stream;
    private int _cseq;
    private string? _authChallenge; // WWW-Authenticate from a 401
    private string? _session;
    private TimeSpan _sessionTimeout = TimeSpan.FromSeconds(60);
    private string? _contentBase;
    private int _videoChannel;
    private Timer? _keepAlive;

    public RtspClient(Uri uri)
    {
        _uri = uri;
        _url = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
        var userInfo = uri.UserInfo.Split(':', 2);
        _user = Uri.UnescapeDataString(userInfo[0]);
        _password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    }

    public SdpVideo? Video { get; private set; }

    /// <summary>Connects and asks for the video. Throws if the camera refuses.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        ct = timeout.Token;

        bool tls = _uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase);
        int port = _uri.IsDefaultPort || _uri.Port < 0 ? (tls ? 322 : 554) : _uri.Port;
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_uri.Host, port, ct);
        _stream = _tcp.GetStream();
        if (tls)
        {
            var ssl = new SslStream(_stream, false, (_, _, _, _) => true);
            _stream = ssl;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _uri.Host }, ct);
        }

        var describe = await RequestAsync("DESCRIBE", _url, ["Accept: application/sdp"], ct);
        string sdp = Encoding.UTF8.GetString(describe.Body);
        Video = SdpVideo.Parse(sdp) ?? throw new IOException("Camera offers no H.264 video");
        _contentBase = describe.Header("Content-Base") ?? describe.Header("Content-Location") ?? _url;

        var setup = await RequestAsync("SETUP", ControlUrl(Video.Control),
                                       ["Transport: RTP/AVP/TCP;unicast;interleaved=0-1"], ct);
        string? session = setup.Header("Session") ?? throw new IOException("Camera didn't start a session");
        var parts = session.Split(';');
        _session = parts[0].Trim();
        foreach (var part in parts.Skip(1))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("timeout", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(kv[1], out int seconds) && seconds > 0)
                _sessionTimeout = TimeSpan.FromSeconds(seconds);
        }
        if (setup.Header("Transport") is string transport &&
            Regex.Match(transport, @"interleaved=(\d+)") is { Success: true } channel)
            _videoChannel = int.Parse(channel.Groups[1].Value);

        await RequestAsync("PLAY", _contentBase, ["Range: npt=0.000-"], ct);

        // Keep the session alive (the camera drops silent clients after its timeout).
        var interval = TimeSpan.FromSeconds(Math.Max(5, _sessionTimeout.TotalSeconds / 2));
        _keepAlive = new Timer(_ => _ = SendQuietlyAsync("OPTIONS", _url), null, interval, interval);
    }

    /// <summary>RTP packets of the video track, until cancelled or the camera hangs up.</summary>
    public async IAsyncEnumerable<RtpPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);
            await FillAsync(1, timeout.Token);
            if (_buffer[_start] == (byte)'$')
            {
                // Interleaved binary data: '$', channel, 16-bit length, packet.
                await FillAsync(4, timeout.Token);
                int channel = _buffer[_start + 1];
                int length = (_buffer[_start + 2] << 8) | _buffer[_start + 3];
                await FillAsync(4 + length, timeout.Token);
                var data = new ReadOnlyMemory<byte>(_buffer, _start + 4, length);
                _start += 4 + length;
                // The packet points into the read buffer: it's only valid until the next one is read.
                if (channel == _videoChannel && RtpPacket.TryParse(data, out var rtp) && rtp.PayloadType == Video!.PayloadType)
                    yield return rtp;
            }
            else
            {
                await ReadResponseAsync(timeout.Token); // e.g. the answer to a keep-alive
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _keepAlive?.Dispose();
        // Say goodbye so the camera frees its single viewer slot at once,
        // instead of when it notices the connection is gone.
        if (_session != null && _stream != null)
            await SendQuietlyAsync("TEARDOWN", _contentBase ?? _url, TimeSpan.FromMilliseconds(500));
        _stream?.Dispose();
        _tcp?.Dispose();
    }

    private string ControlUrl(string control)
    {
        if (control == "*" || control.Length == 0)
            return _contentBase!;
        if (control.Contains("://"))
            return control;
        return _contentBase!.EndsWith('/') ? _contentBase + control : _contentBase + "/" + control;
    }

    // ---- requests --------------------------------------------------------------

    private sealed record Response(int Status, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    private async Task<Response> RequestAsync(string method, string url, string[] headers, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await SendAsync(method, url, headers, ct);
            var response = await ReadResponseAsync(ct);
            if (response.Status == 401 && attempt == 0 && response.Header("WWW-Authenticate") is string challenge &&
                _user.Length > 0)
            {
                _authChallenge = challenge;
                continue;
            }
            if (response.Status == 401)
                throw new UnauthorizedAccessException("Camera rejected the access code");
            if (response.Status != 200)
                throw new IOException($"Camera answered {method} with {response.Status}");
            return response;
        }
    }

    private async Task SendAsync(string method, string url, string[] headers, CancellationToken ct)
    {
        var request = new StringBuilder();
        request.Append($"{method} {url} RTSP/1.0\r\n");
        request.Append($"CSeq: {Interlocked.Increment(ref _cseq)}\r\n");
        request.Append("User-Agent: KlipperTrayIcon\r\n");
        if (_authChallenge != null)
            request.Append($"Authorization: {Authorization(_authChallenge, method, url, _user, _password)}\r\n");
        if (_session != null)
            request.Append($"Session: {_session}\r\n");
        foreach (var header in headers)
            request.Append(header).Append("\r\n");
        request.Append("\r\n");
        byte[] bytes = Encoding.UTF8.GetBytes(request.ToString());
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream!.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendQuietlyAsync(string method, string url, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            await SendAsync(method, url, [], cts.Token);
        }
        catch (Exception) { }
    }

    /// <summary>The Authorization header answering a Basic or Digest (RFC 2617, MD5) challenge.</summary>
    public static string Authorization(string challenge, string method, string url, string user, string password)
    {
        if (!challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        var fields = Regex.Matches(challenge, @"(\w+)=(?:""([^""]*)""|([^,\s]*))")
                          .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(),
                                        m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        string realm = fields.GetValueOrDefault("realm", ""), nonce = fields.GetValueOrDefault("nonce", "");
        string ha1 = Md5($"{user}:{realm}:{password}"), ha2 = Md5($"{method}:{url}");
        var header = new StringBuilder($"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{url}\"");
        if (fields.TryGetValue("qop", out var qop) && qop.Split(',').Any(q => q.Trim() == "auth"))
        {
            string cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), nc = "00000001";
            header.Append($", qop=auth, nc={nc}, cnonce=\"{cnonce}\", response=\"{Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}")}\"");
        }
        else
        {
            header.Append($", response=\"{Md5($"{ha1}:{nonce}:{ha2}")}\"");
        }
        if (fields.TryGetValue("opaque", out var opaque))
            header.Append($", opaque=\"{opaque}\"");
        return header.ToString();
    }

    private static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    // ---- reading -----------------------------------------------------------------

    private async Task<Response> ReadResponseAsync(CancellationToken ct)
    {
        string status = await ReadLineAsync(ct);
        var match = Regex.Match(status, @"^RTSP/\d\.\d (\d{3})");
        if (!match.Success)
            throw new IOException("Not an RTSP response: " + status);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (string line; (line = await ReadLineAsync(ct)).Length > 0;)
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        byte[] body = [];
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out int length) && length > 0)
        {
            if (length > _buffer.Length)
                throw new IOException("RTSP response too large");
            await FillAsync(length, ct);
            body = _buffer.AsSpan(_start, length).ToArray();
            _start += length;
        }
        return new Response(int.Parse(match.Groups[1].Value), headers, body);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        for (int scanned = 0; ; )
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(_buffer, _start, newline - _start).TrimEnd('\r');
                _start = newline + 1;
                return line;
            }
            scanned = _end - _start;
            if (scanned >= 8192)
                throw new IOException("RTSP header line too long");
            await FillAsync(scanned + 1, ct);
        }
    }

    /// <summary>Makes sure at least <paramref name="count"/> unread bytes are in the buffer.</summary>
    private async Task FillAsync(int count, CancellationToken ct)
    {
        if (_end - _start >= count)
            return;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        while (_end < count)
        {
            int read = await _stream!.ReadAsync(_buffer.AsMemory(_end), ct);
            if (read == 0)
                throw new EndOfStreamException("Camera closed the connection");
            _end += read;
        }
    }
}
