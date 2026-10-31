using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace KlipperTrayIcon.Printers;

/// <summary>Reads an HTTP MJPEG (multipart/x-mixed-replace) stream into JPEG frames.</summary>
public static class MjpegReader
{
    public static async IAsyncEnumerable<byte[]> ReadFramesAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parser = new JpegFrameParser();
        await foreach (var chunk in StreamDownload.ReadChunksAsync(http, url, headers, ct))
            foreach (var frame in parser.Feed(chunk))
                yield return frame;
    }
}

/// <summary>A never-ending HTTP download (a video stream), read in chunks.</summary>
public static class StreamDownload
{
    /// <summary>Gives up if the server hasn't even sent headers by then.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The body as it arrives. Each chunk reuses one buffer, so use it before
    /// asking for the next. Unreachable, an error status or a dropped
    /// connection just ends it.
    /// </summary>
    public static async IAsyncEnumerable<ArraySegment<byte>> ReadChunksAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string>? headers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null)
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage? response = await ConnectAsync(http, request, ct);
        if (response == null)
            yield break;

        using (response)
        {
            Stream? stream = await TryAsync(() => response.Content.ReadAsStreamAsync(ct));
            if (stream == null)
                yield break;

            await using (stream)
            {
                var chunk = new byte[64 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    int read = await TryAsync(() => stream.ReadAsync(chunk, ct).AsTask(), fallback: 0);
                    if (read == 0)
                        yield break;
                    yield return new ArraySegment<byte>(chunk, 0, read);
                }
            }
        }
    }

    private static async Task<HttpResponseMessage?> ConnectAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
            if (response.IsSuccessStatusCode)
                return response;
            response.Dispose();
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> action, T? fallback = default)
    {
        try { return await action(); }
        catch (Exception) { return fallback; }
    }
}

/// <summary>Splits a byte stream into JPEGs by their start (FFD8) and end (FFD9) markers.</summary>
public sealed class JpegFrameParser
{
    private const int MaxBuffered = 16 * 1024 * 1024; // a runaway non-JPEG stream shouldn't eat memory
    private byte[] _buffer = new byte[256 * 1024];
    private int _length;

    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            if (_length + data.Length > MaxBuffered)
                _length = 0;
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        var frames = new List<byte[]>();
        int consumed = 0;
        while (true)
        {
            var span = _buffer.AsSpan(consumed, _length - consumed);
            int start = span.IndexOf(StartMarker);
            if (start < 0)
            {
                // Keep a trailing 0xFF in case the marker is split across reads.
                consumed = _length > 0 && _buffer[_length - 1] == 0xFF ? _length - 1 : _length;
                break;
            }
            int end = span[(start + 2)..].IndexOf(EndMarker);
            if (end < 0)
            {
                consumed += start;
                break;
            }
            int frameLength = start + 2 + end + 2 - start;
            frames.Add(span.Slice(start, frameLength).ToArray());
            consumed += start + frameLength;
        }

        if (consumed > 0)
        {
            Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _length - consumed);
            _length -= consumed;
        }
        return frames;
    }

    private static ReadOnlySpan<byte> StartMarker => [0xFF, 0xD8];
    private static ReadOnlySpan<byte> EndMarker => [0xFF, 0xD9];
}
