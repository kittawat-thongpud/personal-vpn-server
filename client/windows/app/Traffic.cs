using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PersonalVpn;

public sealed record TrafficSample(DateTime Time, long Upload, long Download, double UploadRate, double DownloadRate, bool Available);
public sealed class TrafficMeter
{
    private readonly object gate = new();
    private long upload, download, rawUp, rawDown, stamp = Stopwatch.GetTimestamp();
    private TrafficSample current = new(DateTime.MinValue, 0, 0, 0, 0, false);
    private readonly Queue<TrafficSample> history = new();
    public TrafficSample Snapshot { get { lock (gate) return current; } }
    public TrafficSample[] History { get { lock (gate) return history.ToArray(); } }
    public void Reset() { lock (gate) { upload = download = 0; history.Clear(); BeginCore(); current = new(DateTime.UtcNow, 0, 0, 0, 0, false); } }
    public void BeginCore() { lock (gate) { rawUp = rawDown = 0; stamp = Stopwatch.GetTimestamp(); } }
    public void Update(long up, long down)
    {
        if (up < 0 || down < 0) throw new InvalidDataException("Negative traffic counters.");
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp(); double seconds = Math.Max(.05, (now - stamp) / (double)Stopwatch.Frequency);
            long tx = up >= rawUp ? up - rawUp : up, rx = down >= rawDown ? down - rawDown : down;
            upload = upload > long.MaxValue - tx ? long.MaxValue : upload + tx;
            download = download > long.MaxValue - rx ? long.MaxValue : download + rx;
            current = new(DateTime.UtcNow, upload, download, tx / seconds, rx / seconds, true);
            history.Enqueue(current); while (history.Count > 60) history.Dequeue();
            rawUp = up; rawDown = down; stamp = now;
        }
    }
    public void Unavailable() { lock (gate) current = new(DateTime.UtcNow, upload, download, 0, 0, false); }
    public static string Format(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" }; int index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return bytes.ToString(index == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[index];
    }
}

// Unary gRPC QueryStats using the core's StatsService only; no heap/debug endpoints.
// This narrow codec avoids a separate gRPC runtime dependency and resets no counters.
internal static class CoreTraffic
{
    public static async Task<(long Up, long Down)> Read(HttpClient client, int port, CancellationToken token)
    {
        byte[] pattern = Encoding.UTF8.GetBytes("outbound>>>tunnel>>>traffic>>>");
        byte[] body = new byte[7 + pattern.Length]; body[5] = 10; body[6] = (byte)pattern.Length;
        pattern.CopyTo(body, 7); BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1, 4), body.Length - 5);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:" + port + "/xray.app.stats.command.StatsService/QueryStats")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc"); request.Headers.TryAddWithoutValidation("TE", "trailers");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] header = new byte[5]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (header[0] != 0 || length < 0 || length > 65536) throw new InvalidDataException("Invalid stats response frame.");
        byte[] payload = new byte[length]; await stream.ReadExactlyAsync(payload, token);
        // Drain to trailers, with no unbounded allocation or additional response messages.
        byte[] trailing = new byte[1]; if (await stream.ReadAsync(trailing, token) != 0) throw new InvalidDataException("Unexpected stats frames.");
        if (response.TrailingHeaders.TryGetValues("grpc-status", out var status) && status.Any(x => x != "0")) throw new InvalidDataException("Stats service failed.");
        return Decode(payload);
    }
    internal static (long Up, long Down) Decode(ReadOnlySpan<byte> payload)
    {
        long up = 0, down = 0; int index = 0;
        while (index < payload.Length)
        {
            ulong tag = Varint(payload, ref index);
            if (tag != 10) { Skip(payload, ref index, tag); continue; }
            var stat = Bytes(payload, ref index); int cursor = 0; string name = ""; long value = 0;
            while (cursor < stat.Length)
            {
                ulong field = Varint(stat, ref cursor);
                if (field == 10) name = Encoding.UTF8.GetString(Bytes(stat, ref cursor));
                else if (field == 16) value = checked((long)Varint(stat, ref cursor));
                else Skip(stat, ref cursor, field);
            }
            if (name == "outbound>>>tunnel>>>traffic>>>uplink") up = value;
            if (name == "outbound>>>tunnel>>>traffic>>>downlink") down = value;
        }
        return (up, down);
    }
    private static ulong Varint(ReadOnlySpan<byte> data, ref int index)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (index >= data.Length) throw new InvalidDataException("Truncated stats field.");
            byte value = data[index++]; if (shift == 63 && value > 1) throw new InvalidDataException("Stats field overflow.");
            result |= (ulong)(value & 127) << shift; if (value < 128) return result;
        }
        throw new InvalidDataException("Invalid stats field.");
    }
    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> data, ref int index)
    {
        int length = checked((int)Varint(data, ref index));
        if (length < 0 || length > data.Length - index) throw new InvalidDataException("Invalid stats length.");
        var result = data.Slice(index, length); index += length; return result;
    }
    private static void Skip(ReadOnlySpan<byte> data, ref int index, ulong tag)
    {
        switch (tag & 7)
        {
            case 0: _ = Varint(data, ref index); break;
            case 2: _ = Bytes(data, ref index); break;
            case 1: index += 8; break;
            case 5: index += 4; break;
            default: throw new InvalidDataException("Unsupported stats field.");
        }
        if (index > data.Length) throw new InvalidDataException("Truncated stats response.");
    }
}

public sealed class TrafficChart : FrameworkElement
{
    private readonly Queue<(double Up, double Down)> samples = new();
    private static readonly Pen UpPen = new(new SolidColorBrush(Color.FromRgb(22, 200, 160)), 1.8);
    private static readonly Pen DownPen = new(new SolidColorBrush(Color.FromRgb(100, 168, 255)), 1.8);
    private DateTime last = DateTime.MinValue;
    public void Sample(TrafficSample sample)
    {
        if (!sample.Available || sample.Time == last) return;
        last = sample.Time; samples.Enqueue((sample.UploadRate, sample.DownloadRate));
        while (samples.Count > 60) samples.Dequeue(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double width = Math.Max(1, ActualWidth - 6), height = Math.Max(1, ActualHeight - 8);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(43, 61, 85)), .7);
        foreach (double fraction in new[] { 0d, .5, 1d }) drawing.DrawLine(grid, new Point(3, 4 + height * fraction), new Point(width + 3, 4 + height * fraction));
        if (samples.Count < 2) return;
        var values = samples.ToArray(); double peak = Math.Max(1024, values.Max(x => Math.Max(x.Up, x.Down)) * 1.12);
        foreach (bool upload in new[] { true, false })
        {
            var line = new StreamGeometry();
            using (var shape = line.Open())
            {
                for (int i = 0; i < values.Length; i++)
                {
                    var point = new Point(3 + width * (60 - values.Length + i) / 59, 4 + height * (1 - (upload ? values[i].Up : values[i].Down) / peak));
                    if (i == 0) shape.BeginFigure(point, false, false); else shape.LineTo(point, true, false);
                }
            }
            line.Freeze(); drawing.DrawGeometry(null, upload ? UpPen : DownPen, line);
        }
    }
}
