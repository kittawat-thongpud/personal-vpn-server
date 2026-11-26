using System;
using System.Text.Json;

namespace PersonalVpn;

internal static class HysteriaDiagnostics
{
    // Hysteria 2 emits credentials, addresses and destinations in JSON fields.
    // Treat that text as input and export only these fixed diagnostic codes.
    internal static string? Classify(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var entry = json.RootElement;
            if (entry.ValueKind != JsonValueKind.Object) return null;
            string message = ReadString(entry, "msg");
            if (message == "connected to server") return "authenticated";
            if (ReadString(entry, "level") is not ("warn" or "error" or "fatal" or "panic")) return null;
            string error = ReadString(entry, "error").ToLowerInvariant();
            if (error.Contains("certificate") || error.Contains("pinned hash")) return "certificate";
            if (error.Contains("authentication") || error.Contains("auth error")) return "authentication";
            if (error.Contains("handshake timeout") || error.Contains("handshake did not complete")) return "quic_handshake_timeout";
            if (error.Contains("no recent network activity")) return "quic_no_activity";
            if (error.Contains("idle timeout")) return "quic_idle_timeout";
            if (error.Contains("stateless reset")) return "quic_reset";
            if (error.Contains("connection refused")) return "connection_refused";
            if (error.Contains("network is unreachable") || error.Contains("no route to host")) return "network_unreachable";
            if (error.Contains("no such host")) return "dns";
            if (error.Contains("timeout") || error.Contains("deadline exceeded")) return "timeout";
            if (error.Contains("use of closed network connection") || error.Contains("context canceled")) return null;
            if (message is "failed to read client config" or "failed to parse client config" or "failed to load client config")
                return "client_config";
            return message == "failed to initialize client" ? "startup_failure" : "stream_failure";
        }
        catch (JsonException) { return null; }
    }

    private static string ReadString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
