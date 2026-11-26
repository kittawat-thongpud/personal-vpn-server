using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal static class HealthDiagnostics
{
    private static readonly (string Name, string Url)[] Targets =
    {
        ("ipify", "https://api.ipify.org/?format=json"),
        ("aws", "https://checkip.amazonaws.com/")
    };

    internal static async Task<string> VerifyAsync(HttpClient client, SessionLog log, string context, CancellationToken token)
    {
        bool fallback = false;
        foreach (var (name, url) in Targets)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 4096) throw new InvalidDataException();
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                byte[] bytes = new byte[4097];
                int total = 0;
                while (total < bytes.Length)
                {
                    int read = await stream.ReadAsync(bytes.AsMemory(total), token);
                    if (read == 0) break;
                    total += read;
                }
                if (total > 4096) throw new InvalidDataException();

                string? candidate;
                if (name == "ipify")
                {
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, total));
                    candidate = json.RootElement.ValueKind == JsonValueKind.Object &&
                        json.RootElement.TryGetProperty("ip", out var ip) && ip.ValueKind == JsonValueKind.String
                        ? ip.GetString() : null;
                }
                else candidate = System.Text.Encoding.UTF8.GetString(bytes, 0, total).Trim();
                if (!IPAddress.TryParse(candidate, out _)) throw new InvalidDataException();
                if (fallback) Record(log, context, name, started, "ok_via_fallback");
                return candidate!;
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                Record(log, context, name, started, DescribeFailure(error));
                fallback = true;
            }
        }
        token.ThrowIfCancellationRequested();
        throw new HttpRequestException("Independent HTTPS health endpoints failed.");
    }

    private static void Record(SessionLog log, string context, string target, long started, string result) =>
        log.Add(result == "ok_via_fallback" ? "INFO" : "WARN", "backend.health_probe",
            $"{context}; target={target}; elapsed_ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}; result={result}");

    internal static string DescribeFailure(Exception error)
    {
        if (error is HttpRequestException { StatusCode: { } status }) return $"http_status; status={(int)status}";
        if (error is InvalidDataException or JsonException) return "invalid_exit_response";
        var category = error switch
        {
            HttpRequestException request => request.HttpRequestError,
            HttpIOException io => io.HttpRequestError,
            _ => HttpRequestError.Unknown
        };
        var timeout = error is OperationCanceledException;
        SocketError? socketError = null;
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            if (cause is TimeoutException) timeout = true;
            if (cause is SocketException socket) socketError = socket.SocketErrorCode;
        }
        string reason = timeout || socketError == SocketError.TimedOut ? "timeout" : category switch
        {
            HttpRequestError.NameResolutionError => "dns",
            HttpRequestError.ProxyTunnelError => "proxy_tunnel",
            HttpRequestError.SecureConnectionError => "https_tls",
            HttpRequestError.ConnectionError => "connection",
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => "http_response",
            _ => error is IOException ? "io" : "request_failed"
        };
        return socketError is { } code ? $"{reason}; http_error={category}; socket_error={code}" : $"{reason}; http_error={category}";
    }

    // This is route-table evidence only. It never sends a request outside the VPN.
    internal static string DescribePath(IPAddress? endpoint)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            string links = string.Join(",", interfaces.Where(nic => PhysicalNetwork.IsUnderlay(nic) &&
                nic.OperationalStatus == OperationalStatus.Up).Select(InterfaceKind).Distinct().OrderBy(kind => kind));
            string route = "unknown";
            if (endpoint != null)
            {
                int index = (int)TunSupport.BestInterfaceIndex(endpoint);
                var selected = index == 0 ? null : interfaces.FirstOrDefault(nic =>
                    (endpoint.AddressFamily == AddressFamily.InterNetwork
                        ? nic.GetIPProperties().GetIPv4Properties()?.Index
                        : nic.GetIPProperties().GetIPv6Properties()?.Index) == index);
                route = index == 0 ? "unavailable" : selected == null ? "other" : InterfaceKind(selected);
            }
            return $"links_up={(links.Length == 0 ? "none" : links)}; endpoint_route={route}; evidence=route_table";
        }
        catch (Exception error) when (error is NetworkInformationException or NotSupportedException or InvalidOperationException)
        {
            return "links_up=unknown; endpoint_route=unknown; evidence=route_table";
        }
    }

    private static string InterfaceKind(NetworkInterface nic)
    {
        if (nic.Name.StartsWith("PersonalVPN", StringComparison.OrdinalIgnoreCase)) return "personal_tun";
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return "loopback";
        if (!PhysicalNetwork.IsUnderlay(nic)) return "other";
        return nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet";
    }
}
