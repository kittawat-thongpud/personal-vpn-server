using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal static partial class Tests
{
    private sealed class ProbeResponses(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> remaining = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(remaining.Dequeue()());
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
            fallback[0].Message.Contains("target=ipify;") && fallback[0].Message.Contains("result=proxy_tunnel;") &&
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
