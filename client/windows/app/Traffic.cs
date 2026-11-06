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

public sealed record TrafficSample(DateTime Time, long Upload, long Download, double UploadRate, double DownloadRate, bool Available, long Session = 0);
public sealed class TrafficMeter
{
    private readonly object gate = new();
    private long session;
    private long upload, download, rawUp, rawDown, stamp = Stopwatch.GetTimestamp();
    private TrafficSample current = new(DateTime.MinValue, 0, 0, 0, 0, false);
    private readonly Queue<TrafficSample> history = new();
    public TrafficSample Snapshot { get { lock (gate) return current; } }
    public TrafficSample[] History { get { lock (gate) return history.ToArray(); } }
    public void Reset() { lock (gate) { session++; upload = download = 0; history.Clear(); BeginCore(); current = new(DateTime.UtcNow, 0, 0, 0, 0, false, session); } }
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
            current = new(DateTime.UtcNow, upload, download, tx / seconds, rx / seconds, true, session);
            history.Enqueue(current); while (history.Count > 60) history.Dequeue();
            rawUp = up; rawDown = down; stamp = now;
        }
    }
    public void Unavailable() { lock (gate) current = new(DateTime.UtcNow, upload, download, 0, 0, false, session); }
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

internal sealed class ChartScale
{
    public double Ceiling { get; private set; } = 1024;
    private int quiet;
    public void Reset() { Ceiling = 1024; quiet = 0; }
    public double Update(double peak)
    {
        double wanted = Nice(Math.Max(1024, peak * 1.12));
        if (wanted > Ceiling) { Ceiling = wanted; quiet = 0; }
        else if (wanted < Ceiling / 2) { if (++quiet >= 15) { Ceiling = Math.Max(wanted, Nice(Ceiling * .4)); quiet = 0; } }
        else quiet = 0;
        return Ceiling;
    }
    public static double Nice(double value)
    {
        double unit = value >= 1024 * 1024 ? 1024 * 1024 : value >= 1024 ? 1024 : 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, value / unit)))) * unit;
        return new[] { 1d, 2, 5, 10 }.First(x => x * magnitude >= value) * magnitude;
    }
}
public sealed class TrafficChart : FrameworkElement
{
    private readonly Queue<TrafficSample> samples = new();
    private readonly ChartScale scale = new();
    private DateTime last = DateTime.MinValue;
    private long session = -1;
    internal int SampleCount => samples.Count;
    public void Sample(TrafficSample sample)
    {
        if (sample.Session != session) { session = sample.Session; samples.Clear(); scale.Reset(); last = DateTime.MinValue; InvalidateVisual(); }
        if (!sample.Available || sample.Time == last) return;
        if (sample.Time < last) { samples.Clear(); scale.Reset(); }
        last = sample.Time; samples.Enqueue(sample);
        while (samples.Count > 60 || (samples.Count > 0 && last - samples.Peek().Time > TimeSpan.FromSeconds(60))) samples.Dequeue();
        scale.Update(samples.Max(x => Math.Max(x.UploadRate, x.DownloadRate))); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double left = 56, top = 7, right = Math.Max(left + 1, ActualWidth - 4), bottom = Math.Max(top + 1, ActualHeight - 17), width = right - left, height = bottom - top;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(43, 61, 85)), .6);
        void Label(string value, double x, double y, int align = 0)
        {
            var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9,
                new SolidColorBrush(Color.FromRgb(142, 160, 185)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(x - (align == 1 ? text.Width / 2 : align == 2 ? text.Width : 0), y));
        }
        for (int i = 0; i <= 2; i++)
        {
            double y = bottom - height * i / 2; drawing.DrawLine(grid, new Point(left, y), new Point(right, y));
            Label(i == 0 ? "0" : TrafficMeter.Format(scale.Ceiling * i / 2) + "/s", 0, y - 6);
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = left + width * i / 4; drawing.DrawLine(grid, new Point(x, top), new Point(x, bottom));
            Label(i == 4 ? "now" : "−" + (60 - 15 * i) + "s", x, bottom + 3, i == 0 ? 0 : i == 4 ? 2 : 1);
        }
        if (samples.Count < 2) return;
        foreach (bool upload in new[] { true, false })
        {
            var points = samples.Select(x => new Point(right - width * Math.Clamp((last - x.Time).TotalSeconds / 60, 0, 1), bottom - height * Math.Clamp((upload ? x.UploadRate : x.DownloadRate) / scale.Ceiling, 0, 1))).ToArray();
            var line = new StreamGeometry(); var area = new StreamGeometry();
            void Curve(StreamGeometryContext shape)
            {
                for (int i = 1; i < points.Length; i++)
                { var a = points[i - 1]; var b = points[i]; double mid = (a.X + b.X) / 2; shape.BezierTo(new Point(mid, a.Y), new Point(mid, b.Y), b, true, false); }
            }
            using (var shape = line.Open()) { shape.BeginFigure(points[0], false, false); Curve(shape); }
            using (var shape = area.Open()) { shape.BeginFigure(points[0], true, true); Curve(shape); shape.LineTo(new Point(points[^1].X, bottom), false, false); shape.LineTo(new Point(points[0].X, bottom), false, false); }
            var color = upload ? Color.FromRgb(42, 197, 166) : Color.FromRgb(110, 159, 228);
            drawing.DrawGeometry(new LinearGradientBrush(Color.FromArgb(38, color.R, color.G, color.B), Color.FromArgb(3, color.R, color.G, color.B), 90), null, area);
            drawing.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        }
    }
}
