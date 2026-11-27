using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal static partial class Tests
{
    private sealed class ProbeResponses(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> remaining = new(responses);
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(remaining.Dequeue()());
        }
    }

    private sealed class PendingProbeResponses(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> remaining = new(responses);
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return remaining.Dequeue()(cancellationToken);
        }
    }

    // A response whose headers arrive normally but whose body never ends. It
    // can also delay a valid body's first bytes to exercise a fallback budget.
    private sealed class ProbeBody(string value, bool stall, TimeSpan delay = default) : Stream
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(value);
        private int position;
        internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; private set; }
        internal bool Released { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (position < bytes.Length)
                {
                    if (position == 0 && delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                    int count = Math.Min(buffer.Length, bytes.Length - position);
                    bytes.AsMemory(position, count).CopyTo(buffer);
                    position += count;
                    return count;
                }
                if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage ProbeResponse(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    private static async Task HealthProbeDeadlines()
    {
        // Both proxy and captured paths share VerifyAsync. Header completion
        // must not leave a body read waiting until the connection is stopped.
        foreach (string path in new[] { "outbound", "captured" })
        {
            var log = new SessionLog();
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var first = new ProbeBody("{\"ip\":\"", stall: true);
            using var second = new ProbeBody("203.0.113.10", stall: false, TimeSpan.FromMilliseconds(125));
            var responses = new ProbeResponses(() => ProbeResponse(first), () => ProbeResponse(second));
            using var client = new HttpClient(responses) { Timeout = Timeout.InfiniteTimeSpan };
            string observed = await HealthDiagnostics.VerifyAsync(client, log, "path=" + path, guard.Token, TimeSpan.FromMilliseconds(250));
            var entries = log.Snapshot();
            Assert(observed == "203.0.113.10" && !guard.IsCancellationRequested && first.Cancelled && first.Released &&
                second.Released && responses.Requests == 2 && entries.Length == 2 &&
                entries[0].Message.Contains("target=ipify; phase=response_body;") && entries[0].Message.Contains("result=timeout;") &&
                entries[1].Message.Contains("target=aws; phase=exit_validation;") && entries[1].Message.EndsWith("result=ok_via_fallback"),
                "A stalled " + path + " response body did not release the request and give fallback an independent deadline.");
        }

        var headerLog = new SessionLog();
        using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var client = new HttpClient(new PendingProbeResponses(
            async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("Unreachable"); },
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.10") })))
            { Timeout = Timeout.InfiniteTimeSpan })
        {
            string observed = await HealthDiagnostics.VerifyAsync(client, headerLog, "path=outbound", guard.Token, TimeSpan.FromMilliseconds(250));
            Assert(observed == "203.0.113.10" && headerLog.Snapshot()[0].Message.Contains("phase=response_headers;") &&
                headerLog.Snapshot()[0].Message.Contains("result=timeout;"), "A stalled response header was not bounded and attributed.");
        }

        // Outer Stop/network-generation cancellation must abort either phase
        // without producing endpoint-failure evidence or starting fallback.
        foreach (bool headers in new[] { true, false })
        {
            var log = new SessionLog();
            using var cancel = new CancellationTokenSource();
            using var body = new ProbeBody("{\"ip\":\"", stall: true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var responses = new PendingProbeResponses(async token =>
            {
                if (headers)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Unreachable");
                }
                return ProbeResponse(body);
            });
            using var client = new HttpClient(responses) { Timeout = Timeout.InfiniteTimeSpan };
            Task<string> verification = HealthDiagnostics.VerifyAsync(client, log, "path=captured", cancel.Token, TimeSpan.FromSeconds(3));
            await (headers ? entered.Task : body.Reading.Task).WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel();
            bool cancelled = false;
            try { await verification.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException error) { cancelled = error.CancellationToken == cancel.Token; }
            Assert(cancelled && responses.Requests == 1 && log.Snapshot().Length == 0 && (headers || body.Released),
                "Outer connection cancellation during " + (headers ? "headers" : "body") + " became a failed target or fallback.");
        }
    }

    internal static void HealthDiagnosticsPolicy()
    {
        const string context = "generation=4; attempt=2; protocol=Hysteria 2; mode=TUN; path=outbound";
        var log = new SessionLog();
        using (var client = new HttpClient(new ProbeResponses(
            () => throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "private.example"),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("203.0.113.10") })))
        {
            var observed = HealthDiagnostics.VerifyAsync(client, log, context, CancellationToken.None).GetAwaiter().GetResult();
            Assert(observed == "203.0.113.10", "Fallback HTTPS failed to verify the VPN exit.");
        }
        var fallback = log.Snapshot();
        Assert(fallback.Length == 2 &&
            fallback[0].Message.Contains("target=ipify; phase=response_headers;") && fallback[0].Message.Contains("result=proxy_tunnel;") &&
            fallback[1].Message.Contains("target=aws;") && fallback[1].Message.EndsWith("result=ok_via_fallback") &&
            fallback.All(entry => !entry.Message.Contains("private.example") && !entry.Message.Contains("203.0.113.10")),
            "Probe diagnostics lost attribution or included private values.");

        log.Clear();
        using (var client = new HttpClient(new ProbeResponses(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ip": 42}""") },
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            bool failed = false;
            try { HealthDiagnostics.VerifyAsync(client, log, context, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (HttpRequestException) { failed = true; }
            Assert(failed && log.Snapshot().Select(entry => entry.Message.Split("result=")[1])
                .SequenceEqual(new[] { "invalid_exit_response", "http_status; status=503" }),
                "Verification must reject both bad responses and classify each target.");
        }
        log.Clear();
        using (var cancel = new CancellationTokenSource())
        using (var client = new HttpClient(new ProbeResponses(() => throw new InvalidOperationException("Unexpected I/O"))))
        {
            cancel.Cancel();
            bool cancelled = false;
            try { HealthDiagnostics.VerifyAsync(client, log, context, cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && log.Snapshot().Length == 0, "A cancelled connection looked like a failure.");
        }
        var socketFailure = new HttpRequestException(HttpRequestError.ConnectionError, "ignored", new SocketException((int)SocketError.NetworkUnreachable));
        Assert(HealthDiagnostics.DescribeFailure(socketFailure) == "connection; http_error=ConnectionError; socket_error=NetworkUnreachable",
            "Read-only socket error evidence was lost.");
        Assert(HealthDiagnostics.DescribeFailure(new HttpRequestException(HttpRequestError.SecureConnectionError)) == "https_tls; http_error=SecureConnectionError",
            "HTTPS TLS errors must be identifiable.");
        Assert(HealthDiagnostics.DescribeFailure(new TaskCanceledException("ignored", new TimeoutException())).StartsWith("timeout;"),
            "A timed-out probe must be distinguishable from a protocol refusal.");

        HealthProbeDeadlines().GetAwaiter().GetResult();

        Assert(HysteriaDiagnostics.Classify("""{"level":"info","msg":"connected to server","addr":"private.example","count":1}""") == "authenticated",
            "A successful Hysteria handshake must be distinct from core startup.");
        Assert(HysteriaDiagnostics.Classify("""{"level":"warn","msg":"SOCKS5 TCP error","error":"connect error: timeout: no recent network activity","reqAddr":"private.example"}""") == "quic_no_activity",
            "The supplied Hysteria failure signature was not classified.");
        Assert(HysteriaDiagnostics.Classify("""{"level":"fatal","msg":"failed to initialize client","error":"connect error: x509: no certificate matches the pinned hash"}""") == "certificate",
            "A Hysteria certificate failure must be separate from a timeout.");
        Assert(HysteriaDiagnostics.Classify("""{"level":"warn","msg":"SOCKS5 TCP error","error":"use of closed network connection"}""") == null &&
            HysteriaDiagnostics.Classify("partial JSON") == null,
            "Shutdown noise and incomplete core records must remain out of diagnostic exports.");
    }
}
