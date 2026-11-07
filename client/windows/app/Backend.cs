using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

// All children belong to a kill-on-close job: a GUI crash must not leave listeners behind.
public sealed class ProcessJob : IDisposable
{
    private readonly IntPtr handle;
    public ProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        var limits = new ExtendedLimits();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<ExtendedLimits>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)size)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("Could not supervise the VPN core safely.");
        }
    }
    public void Dispose() { CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, IntPtr data, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class OwnedCore : IDisposable
{
    public Process Process { get; }
    public string Name { get; }
    private string startupDiagnostic = "";
    public string StartupDiagnostic => startupDiagnostic;
    private readonly Task reader;
    private readonly Task errorReader;
    private readonly SessionLog log;
    private int errorCount;
    public OwnedCore(ProcessJob job, string binary, string[] arguments, SessionLog log)
    {
        this.log = log; Name = Path.GetFileNameWithoutExtension(binary);
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Name == "sing-box") { info.CreateNoWindow = false; info.WindowStyle = ProcessWindowStyle.Hidden; }
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        foreach (string variable in info.Environment.Keys.Where(x => x.StartsWith("HYSTERIA_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(variable);
        info.Environment["HYSTERIA_DISABLE_UPDATE_CHECK"] = "1";
        Process = Process.Start(info) ?? throw new InvalidOperationException("VPN core failed to launch.");
        job.Add(Process);
        reader = Drain(Process.StandardOutput);
        errorReader = Drain(Process.StandardError);
        log.Add("INFO", "backend.core_started", Name + " started (owned child).");
    }
    private async Task Drain(StreamReader stream)
    {
        // Fixed-size reads also bound a malicious or broken core's unterminated line.
        char[] buffer = new char[1024];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var line = new string(buffer, 0, count).ToLowerInvariant();
                if (Name == "sing-box" && Environment.GetEnvironmentVariable("CI") == "true") startupDiagnostic = (startupDiagnostic + line)[^Math.Min(4096, startupDiagnostic.Length + line.Length)..];
                if (Name == "sing-box" && (line.Contains("forwarding") || line.Contains("weak host") || line.Contains("unsafe interface binding")))
                {
                    if (Interlocked.Increment(ref errorCount) <= 20) log.Add("WARN", "backend.interface_binding", "Physical interface binding conflict; capture cannot safely bypass this interface. Inspect network sharing and reconnect.");
                    continue;
                }
                if (!(line.Contains("error") || line.Contains("fatal") || line.Contains("failed"))) continue;
                if (Interlocked.Increment(ref errorCount) > 20) continue;
                string category = line.Contains("certificate") || line.Contains("pinsha") ? "TLS / certificate verification failed."
                    : line.Contains("auth") ? "Authentication failed."
                    : line.Contains("timeout") ? "Network / handshake timed out." : "Core reported an error; health verification will decide connection state.";
                // No raw core output, credentials, URLs or configuration text enter session logs.
                log.Add("WARN", "backend.core_result", Name + ": " + category);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public bool Exited => Process.HasExited;
    public void Dispose()
    {
        try { if (!Process.HasExited && Name == "sing-box") ConsoleStop.Try(Process);
            if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(3000); } }
        catch (InvalidOperationException) { }
        try { Task.WaitAll(new[] { reader, errorReader }, 1000); } catch (AggregateException) { }
        Process.Dispose();
        log.Add("INFO", "backend.core_stopped", Name + " stopped.");
    }
}

public sealed class Backend : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object gate = new();
    private CancellationTokenSource? session;
    private CancellationTokenSource? cycle;
    private Task? worker;
    private long generation;
    private string networkSignature = NetworkSignature();
    private readonly Timer networkDebounce;
    private string state = "Disconnected", detail = "Ready", exit = "—";
    private Settings? active;
    private readonly List<OwnedCore> cores = new();
    private SystemProxyLease? proxyLease;
    private string captureStatus = "Not captured";
    private string lastTunnelExit = "—";
    public SessionLog Log { get; } = new();
    public TrafficMeter Traffic { get; } = new();
    public string State { get { lock (gate) return state; } }
    public string Detail { get { lock (gate) return detail; } }
    public string ExitIp { get { lock (gate) return exit; } }
    public Settings? Active { get { lock (gate) return active?.Snapshot(); } }
    public bool Running { get { lock (gate) return session != null; } }
    public string NativeTunDiagnostic { get { lock (gate) return Environment.GetEnvironmentVariable("CI") == "true" ? cores.LastOrDefault(x => x.Name == "sing-box")?.StartupDiagnostic ?? "" : ""; } }
    public string CaptureStatus { get { lock (gate) return captureStatus; } }
    public Backend()
    {
        networkDebounce = new Timer(_ => NetworkChanged(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkChange;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChange;
    }
    private void ScheduleNetworkCheck()
    {
        try { networkDebounce.Change(1200, Running ? 3000 : Timeout.Infinite); } catch (ObjectDisposedException) { }
    }
    private void OnNetworkChange(object? sender, EventArgs args) => ScheduleNetworkCheck();
    private void OnAvailabilityChange(object? sender, NetworkAvailabilityEventArgs args) => ScheduleNetworkCheck();
    private static string NetworkSignature()
    {
        try
        {
            return PhysicalNetwork.Signature() + "|" + string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => !x.Name.StartsWith("PersonalVPN", StringComparison.Ordinal))
        .OrderBy(x => x.Id).Select(x => x.Id + ":" + x.OperationalStatus + ":" +
            string.Join(",", x.GetIPProperties().UnicastAddresses.Select(a => a.Address + "/" + a.PrefixLength).OrderBy(a => a)) + ":" +
            string.Join(",", x.GetIPProperties().GatewayAddresses.Select(a => a.Address.ToString()).OrderBy(a => a)) + ":" +
            string.Join(",", x.GetIPProperties().DnsAddresses.Select(a => a.ToString()).OrderBy(a => a))));
        }
        catch (Exception error) when (error is NetworkInformationException or Win32Exception) { return ""; }
    }
    private void NetworkChanged()
    {
        lock (gate)
        {
            string observed = NetworkSignature();
            if (observed.Length == 0) { ScheduleNetworkCheck(); return; }
            if (observed == networkSignature) return;
            networkSignature = observed;
            if (session == null) return;
            generation++;
            cycle?.Cancel();
        }
        Log.Add("WARN", "backend.network_changed", "Network address / availability changed; discard old health result and re-verify.");
    }
    private void SetState(string value, string reason, long? expected = null)
    {
        lock (gate)
        {
            if (expected != null && expected.Value != generation) return;
            state = value; detail = reason;
            if (value != "Connected") exit = "—";
        }
        Log.Add(value is "Error" or "Reconnecting" ? "WARN" : "INFO", "state." + value.ToLowerInvariant(), reason);
    }
    public async Task StartAsync(Settings settings)
    {
        settings.Validate();
        await lifecycle.WaitAsync();
        try
        {
            await StopInternal();
            // A failed prior worker must recover before a new owner launches.
            TunLease.RecoverAbandoned(); SystemProxyLease.RecoverAbandoned();
            Traffic.Reset();
            lock (gate)
            {
                active = settings.Snapshot(); session = new CancellationTokenSource(); generation++;
                var token = session.Token;
                var snapshot = active.Snapshot();
                state = "Connecting"; detail = settings.CaptureMode == "TUN" ? "Starting TUN; authentication, adapter and capture checks required." : "Starting tunnel and selecting Personal VPN system proxy."; exit = "—";
                worker = Task.Run(() => Run(snapshot, token));
            }
            networkDebounce.Change(1200, 3000); // Includes metric changes without an address event.
        }
        finally { lifecycle.Release(); }
    }
    public async Task ConnectAsync(Settings settings)
    {
        if (settings.CaptureMode == "Proxy") { await ConnectProxyAsync(settings); return; }
        CaptureCapabilities.EnsureAvailable(settings.CaptureMode); settings.SystemProxy = false;
        await StartAsync(settings); await WaitForConnection(90);
    }
    private async Task WaitForConnection(int seconds)
    {
        CancellationTokenSource? requested; lock (gate) requested = session; var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            lock (gate) { if (session == null || session != requested) throw new OperationCanceledException("Connection cancelled."); }
            if (State == "Connected") return;
            if (State == "Error") throw new InvalidOperationException(Detail);
            if (State == "Reconnecting") throw new InvalidOperationException(Detail + " Retrying; inspect backend logs.");
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Connection verification timed out; inspect backend logs.");
    }
    public async Task ConnectProxyAsync(Settings settings)
    {
        CaptureCapabilities.EnsureAvailable(settings.CaptureMode);
        if (settings.CaptureMode != "Proxy") throw new InvalidOperationException("A TUN configuration cannot be started as a Proxy session.");
        settings.SystemProxy = true;
        await StartAsync(settings);
        CancellationTokenSource? requestedSession; lock (gate) requestedSession = session;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            lock (gate) { if (session != requestedSession || session == null) throw new OperationCanceledException("Connection was cancelled."); }
            if (State == "Connected") return;
            if (State == "Proxy conflict") { await ReclaimProxyAsync(); return; }
            if (State == "Reconnecting") throw new InvalidOperationException(Detail + " Automatic retries remain active. You can test another protocol separately.");
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Connection has not verified within 45 seconds. Check backend logs; retries remain active.");
    }
    public async Task<ProtocolProbeResult> TestProtocolAsync(Settings settings, string protocol)
    {
        var selected = settings.Snapshot(); selected.CaptureMode = "Proxy"; selected.Protocol = protocol; selected.SystemProxy = false;
        selected.HttpPort = FreePort(settings.HttpPort, settings.SocksPort);
        selected.SocksPort = FreePort(settings.HttpPort, settings.SocksPort, selected.HttpPort);
        string path = protocol == "REALITY" && selected.ChainReality ? "via configured upstream proxy" : "on current OS route";
        Log.Add("INFO", "backend.protocol_test_started", protocol + " " + path + "; isolated listeners, no system proxy change.");
        var elapsed = Stopwatch.StartNew();
        await using var probe = new Backend();
        try
        {
            await probe.StartAsync(selected);
            while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (probe.State == "Connected")
                {
                    var result = new ProtocolProbeResult(protocol, true, probe.ExitIp, "HTTPS verified " + path, elapsed.ElapsedMilliseconds);
                    Log.Add("INFO", "backend.protocol_test_result", protocol + " passed; exit " + result.ExitIp + "; " + path + "."); return result;
                }
                if (probe.State == "Reconnecting") break;
                await Task.Delay(200);
            }
            string reason = probe.State == "Reconnecting" ? probe.Detail : "Timed out before authenticated HTTPS verification.";
            Log.Add("WARN", "backend.protocol_test_result", protocol + " failed: " + reason + " Existing connection kept.");
            return new ProtocolProbeResult(protocol, false, "—", reason, elapsed.ElapsedMilliseconds);
        }
        catch (Exception error) when (error is InvalidOperationException || error is IOException)
        {
            Log.Add("WARN", "backend.protocol_test_result", protocol + " test failed (" + error.GetType().Name + "). Existing connection kept.");
            return new ProtocolProbeResult(protocol, false, "—", error is InvalidOperationException ? error.Message : "Profile / core files unavailable.", elapsed.ElapsedMilliseconds);
        }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopInternal(); } finally { lifecycle.Release(); }
    }
    public async Task ReclaimProxyAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (!Running || active == null || !(active.SystemProxy || active.CaptureMode == "TUN") || proxyLease == null) throw new InvalidOperationException("Connect with System proxy enabled and wait for tunnel verification first.");
            long expectedGeneration; lock (gate) expectedGeneration = generation;
            proxyLease.Reclaim();
            lock (gate) captureStatus = "Windows HTTP / HTTPS → Personal VPN";
            SetState("Verifying", "Windows proxy selected. Rechecking HTTPS without restarting the tunnel.");
            // WinINet's fresh snapshot is authoritative. .NET's global system-proxy
            // object may retain an older selection after a settings notification.
            if (!proxyLease.IsCurrent) throw new InvalidOperationException("Another app selected a different proxy again.");
            using var client = new HttpClient(new SocketsHttpHandler { Proxy = active.CaptureMode == "TUN" ? null : new WebProxy("http://127.0.0.1:" + active.HttpPort), UseProxy = active.CaptureMode != "TUN", ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(12) };
            string observed = await Health(client, CancellationToken.None);
            if (!proxyLease.IsCurrent) throw new InvalidOperationException("Another app changed the Windows proxy during verification.");
            lock (gate) { if (generation != expectedGeneration) throw new OperationCanceledException("Network changed during browser proxy verification."); }
            Log.Add("INFO", "backend.system_proxy_verified", "Windows selects Personal VPN and the selected HTTP listener passed HTTPS. Policy-routed exit: " + observed + ". Existing app sockets may need a new connection.");
            SetState("Connected", "System proxy reselected and HTTPS verified. Existing connections may need reconnecting.", expectedGeneration);
            lock (gate) { if (generation == expectedGeneration) exit = lastTunnelExit; }
        }
        catch
        {
            if (Running) SetState("Degraded", "Browser proxy verification failed. Check other proxy software and app-specific proxy settings.");
            throw;
        }
        finally { lifecycle.Release(); }
    }
    private async Task StopInternal()
    {
        Task? pending;
        lock (gate) { session?.Cancel(); cycle?.Cancel(); pending = worker; }
        networkDebounce.Change(Timeout.Infinite, Timeout.Infinite);
        Exception? failure = null;
        try { if (pending != null) await pending; }
        catch (Exception error) { failure = error; Log.Add("ERROR", "backend.session_cleanup_failed", "Previous session failed during cleanup (" + error.GetType().Name + "). Capture state will be reset before reuse."); }
        try { TunLease.RecoverAbandoned(); } // Includes a previously unplugged NIC that has returned.
        catch (Exception error) { failure ??= error; }
        try { proxyLease?.Dispose(); proxyLease = null; }
        catch { SetState("Error", "Windows proxy restore failed. Retry Disconnect; the recovery record was retained."); throw; }
        finally
        {
            lock (gate) { session?.Dispose(); session = null; worker = null; active = null; captureStatus = "Not captured"; lastTunnelExit = "—"; }
            Traffic.Unavailable();
        }
        if (failure != null) { SetState("Error", "Previous capture cleanup failed. Retry to reset the retained adapter."); throw new InvalidOperationException("Previous capture cleanup failed; recovery must finish before a new connection.", failure); }
        SetState("Disconnected", "Capture stopped; previous Windows proxy restored. Adapter retained for reuse.");
    }
    private async Task Run(Settings settings, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            long attemptGeneration;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (gate) { cycle = attempt; attemptGeneration = generation; }
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    SetState("Waiting for network", "No usable network. Waiting before another attempt.");
                    await Task.Delay(3000, attempt.Token); continue;
                }
                SetState(failures == 0 ? "Connecting" : "Reconnecting", "Starting " + settings.Protocol + "; authentication and TLS checks required.");
                await RunCycle(settings, attemptGeneration, attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (token.IsCancellationRequested) { SetState("Error", "Capture reset failed; retry Disconnect or Connect to recover."); Log.Add("ERROR", "backend.cleanup_failed", "Capture teardown failed (" + error.GetType().Name + "). Retry Disconnect; the recovery record was retained."); throw; }
                failures++;
                string reason = error is InvalidOperationException ? error.Message : "VPN attempt failed (" + error.GetType().Name + ").";
                SetState("Reconnecting", reason, attemptGeneration);
                int delay = Math.Min(30, 1 << Math.Min(failures, 5)) * 1000 + Random.Shared.Next(0, 750);
                Log.Add("WARN", "backend.retry", "Attempt " + failures + "; retry in " + (delay / 1000) + "s. No automatic Direct fallback.");
                try { await Task.Delay(delay, attempt.Token); } catch (OperationCanceledException) { }
            }
            finally { lock (gate) { if (cycle == attempt) cycle = null; } }
        }
    }
    private static int FreePort(params int[] excluded)
    {
        for (int tries = 0; tries < 50; tries++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            if (!excluded.Contains(port)) return port;
        }
        throw new InvalidOperationException("Could not allocate an internal loopback port.");
    }
    private static object Listener(string tag, string protocol, int port, bool udp = false) => new
    {
        tag, protocol, listen = "127.0.0.1", port,
        settings = protocol == "socks" ? (object)new { auth = "noauth", udp } : new { },
    };
    private async Task RunCycle(Settings settings, long expected, CancellationToken token)
    {
        TunLease.RecoverAbandoned();
        if (proxyLease != null) { proxyLease.Dispose(); proxyLease = null; }
        string runtimeRoot = Path.Combine(Store.Root, "runtime");
        Store.SecureDirectory(runtimeRoot);
        string folder = Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N"));
        Store.SecureDirectory(folder);
        using var job = new ProcessJob();
        using var trafficCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? trafficTask = null;
        var owned = new List<OwnedCore>();
        TunLease? adapterLease = null; bool tun = settings.CaptureMode == "TUN";
        var transports = Array.Empty<IPAddress>();
        try
        {
            if (tun) transports = await TunSupport.Resolve(settings, token);
            // Fail before launch if another program owns one of our public proxy ports.
            foreach (int port in new[] { settings.HttpPort, settings.SocksPort })
            {
                var reservation = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                try { reservation.Start(); }
                catch (SocketException) { throw new InvalidOperationException("Local proxy port " + port + " is already occupied. No other process was stopped."); }
                finally { reservation.Stop(); }
            }
            int health = FreePort(settings.HttpPort, settings.SocksPort);
            int inner = FreePort(settings.HttpPort, settings.SocksPort, health);
            int stats = FreePort(settings.HttpPort, settings.SocksPort, health, inner);
            var outbounds = new JsonArray();
            if (settings.Protocol == "Hysteria 2")
            {
                var hy = JsonNode.Parse(settings.Selected.Hysteria!)!.AsObject();
                hy["socks5"] = new JsonObject { ["listen"] = "127.0.0.1:" + inner, ["disableUDP"] = !tun };
                if (tun) { var endpoint = new Uri("tcp://" + hy["server"]!.GetValue<string>()); hy["server"] = (transports[0].AddressFamily == AddressFamily.InterNetworkV6 ? "[" + transports[0] + "]" : transports[0].ToString()) + ":" + endpoint.Port; }
                hy["quic"] = new JsonObject { ["maxIdleTimeout"] = "30s", ["keepAlivePeriod"] = "10s" };
                string path = Path.Combine(folder, "hysteria.json");
                await File.WriteAllTextAsync(path, hy.ToJsonString(), token);
                owned.Add(new OwnedCore(job, VerifyCore("hysteria.exe"), new[] { "client", "-c", path }, Log));
                outbounds.Add(new JsonObject { ["tag"] = "tunnel", ["protocol"] = "socks", ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = inner }) } });
            }
            else
            {
                var reality = JsonNode.Parse(settings.Selected.Xray!)!["outbounds"]![0]!.DeepClone();
                reality["tag"] = "tunnel";
                if (tun)
                {
                    reality["settings"]!["vnext"]![0]!["address"] = transports[0].ToString();
                    var user = reality["settings"]!["vnext"]![0]!["users"]![0]!;
                    if ((string?)user["flow"] == "xtls-rprx-vision") user["flow"] = "xtls-rprx-vision-udp443";
                }
                reality["streamSettings"]!["sockopt"] = settings.ChainReality ? new JsonObject { ["dialerProxy"] = "existing-proxy" } : new JsonObject();
                outbounds.Add(reality);
                if (settings.ChainReality) outbounds.Add(new JsonObject { ["tag"] = "existing-proxy", ["protocol"] = "http", ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = settings.UpstreamPort }) } });
            }
            outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
            outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
            var router = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["stats"] = new JsonObject(),
                ["api"] = new JsonObject { ["tag"] = "traffic-api", ["listen"] = "127.0.0.1:" + stats, ["services"] = new JsonArray("StatsService") },
                ["policy"] = new JsonObject { ["system"] = new JsonObject { ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true } },
                ["inbounds"] = System.Text.Json.JsonSerializer.SerializeToNode(new[] { Listener("http", "http", settings.HttpPort), Listener("socks", "socks", settings.SocksPort, tun), Listener("health", "socks", health) }),
                ["outbounds"] = outbounds,
                // AsIs: proxy hostnames reach the remote resolver; local IP rules match literal IP destinations only.
                ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = tun ? new JsonArray(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "tunnel" }) : Policy.Compile(settings) }
            };
            string routerPath = Path.Combine(folder, "xray.json");
            await File.WriteAllTextAsync(routerPath, router.ToJsonString(), token);
            owned.Add(new OwnedCore(job, VerifyCore("xray.exe"), new[] { "run", "-config", routerPath }, Log));
            lock (gate) { cores.Clear(); cores.AddRange(owned); }
            await WaitForListeners(owned, new[] { settings.HttpPort, settings.SocksPort, health }, token);
            Traffic.BeginCore(); trafficTask = CollectTraffic(stats, trafficCancellation.Token);
            SetState("Verifying", "Checking authenticated HTTPS through the selected server.", expected);
            using var healthHandler = new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + health), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.Zero };
            using var client = new HttpClient(healthHandler) { Timeout = TimeSpan.FromSeconds(10) };
            using var tunHealth = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(12) };
            int misses = 0, checks = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                EnsureAlive(owned);
                try
                {
                    string observed = await Health(client, token);
                    token.ThrowIfCancellationRequested();
                    if (tun && adapterLease == null)
                    {
                        SetState("Verifying", "Authenticated server verified; preparing TUN adapter and checking the captured path.", expected);
                        adapterLease = await TunLease.Start(folder, token);
                        foreach (var (nic, row) in PhysicalNetwork.Rows()) Log.Add("INFO", "backend.physical_link", nic.Name + " · " + (row.Family == 2 ? "IPv4" : "IPv6") + " · index " + row.Index + " · metric " + row.Metric + " · MTU " + row.Mtu + "; strong-host binding prepared. Direct uses destination routes, not a fixed Wi-Fi/LAN preference.");
                        Log.Add("INFO", "backend.adapter_reused", "Persistent adapter opened: " + adapterLease.AdapterId + ". No device creation or deletion for this session.");
                        var plan = TunPlan.Compile(settings, transports.Select(x => x.ToString()), new[] { VerifyCore("xray.exe"), VerifyCore("hysteria.exe") }, adapterLease.InterfaceName, TunSupport.MeshPrefixes(), Environment.ProcessPath);
                        if (Environment.GetEnvironmentVariable("CI") == "true") plan["log"]!["level"] = "debug";
                        string path = Path.Combine(folder, "tun.json"); await File.WriteAllTextAsync(path, plan.ToJsonString(), token);
                        var tunCore = new OwnedCore(job, VerifyCore("sing-box.exe"), new[] { "run", "-c", path }, Log); owned.Add(tunCore);
                        lock (gate) { cores.Clear(); cores.AddRange(owned); }
                        await TunSupport.WaitForAdapter(adapterLease, token, () => tunCore.Exited ? "TUN core stopped before adapter readiness. " + tunCore.StartupDiagnostic : null);
                        Log.Add("INFO", "backend.tun_started", "Owned TUN adapter ready; verifying DNS and HTTPS through system capture.");
                    }
                    if (tun)
                    {
                        if (adapterLease!.Exited) throw new InvalidOperationException("Adapter recovery guard stopped.");
                        string capturedExit = await Health(tunHealth, token);
                        if (capturedExit != observed) throw new InvalidDataException("Captured path did not use the authenticated VPN exit.");
                        observed = capturedExit;
                    }
                    if ((settings.SystemProxy || tun) && proxyLease == null) proxyLease = new SystemProxyLease(settings.HttpPort, Log, direct: tun);
                    bool captured = !(settings.SystemProxy || tun) || proxyLease!.IsCurrent;
                    lock (gate) captureStatus = tun ? "TUN · apps / services captured" : settings.SystemProxy ? captured ? "Windows HTTP / HTTPS → Personal VPN" : "Proxy changed by another app" : "Manual app proxy only";
                    lock (gate) { if (generation != expected) throw new OperationCanceledException(token); exit = observed; lastTunnelExit = observed; }
                    SetState(captured ? "Connected" : "Proxy conflict", captured
                        ? "Verified " + (tun ? "TUN " : "HTTPS ") + "exit " + observed + " · " + settings.Protocol
                        : "Tunnel works, but another app selected a different Windows proxy. Use proxy to select Personal VPN again.", expected);
                    lock (gate) { if (generation == expected) exit = observed; }
                    misses = 0; checks++;
                    Log.Add("INFO", "backend.health_ok", "Authenticated end-to-end check passed. Exit: " + observed);
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException || error is System.Text.Json.JsonException || error is InvalidDataException)
                {
                    token.ThrowIfCancellationRequested();
                    misses++;
                    SetState("Degraded", "Tunnel health check failed; verification retry " + misses + "/2.", expected);
                    Log.Add("WARN", "backend.health_failed", "HTTPS health result failed (" + error.GetType().Name + ").");
                    if (misses >= 2) throw new InvalidOperationException("Two tunnel health checks failed. Restarting owned VPN cores.");
                }
                for (int second = 0; second < (misses > 0 ? 3 : 25); second++)
                {
                    await Task.Delay(1000, token); EnsureAlive(owned);
                    if (adapterLease?.Exited == true) throw new InvalidOperationException("Adapter recovery guard stopped.");
                    if ((settings.SystemProxy || tun) && proxyLease != null && !proxyLease.IsCurrent)
                    {
                        lock (gate) captureStatus = WindowsProxy.Describe(WindowsProxy.Read());
                        if (State != "Proxy conflict") SetState("Proxy conflict", "Windows proxy changed outside Personal VPN. Use proxy to reclaim it; disable automatic proxy selection in the competing app if it keeps changing.", expected);
                    }
                }
            }
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            trafficCancellation.Cancel();
            try { if (trafficTask != null) await trafficTask; } catch (Exception error) { cleanupErrors.Add(error); }
            Traffic.Unavailable();
            lock (gate) cores.Clear();
            // Cleanup steps are independent; one exception cannot strand another
            // core, the TUN/physical journal, or the proxy recovery lease.
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                try { owned[i].Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
            }
            try { adapterLease?.Dispose(); if (adapterLease != null) Log.Add("INFO", "backend.tun_stopped", "Capture routes / DNS released and active physical binding flags restored; persistent adapter retained."); }
            catch (Exception error) { cleanupErrors.Add(error); }
            try { proxyLease?.Dispose(); proxyLease = null; }
            catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count != 0) throw new AggregateException("Capture cleanup is incomplete; recovery records were retained.", cleanupErrors);
            // Only a freshly generated child of our protected runtime directory is removed.
            if (Path.GetDirectoryName(Path.GetFullPath(folder)) != Path.GetFullPath(runtimeRoot)) throw new InvalidOperationException("Invalid runtime cleanup path.");
            TunLease.CleanRuntimeFolder(folder);
        }
    }
    private async Task CollectTraffic(int port, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) }) { Timeout = TimeSpan.FromSeconds(2) };
        bool failed = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var counters = await CoreTraffic.Read(client, port, token); Traffic.Update(counters.Up, counters.Down);
                if (failed) Log.Add("INFO", "backend.traffic_stats_recovered", "Live tunnel counters are available again.");
                failed = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is HttpRequestException || error is IOException || error is TaskCanceledException || error is OverflowException)
            {
                Traffic.Unavailable();
                if (!failed) Log.Add("WARN", "backend.traffic_stats_failed", "Live traffic counters unavailable (" + error.GetType().Name + "); tunnel health is checked separately.");
                failed = true;
            }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
        }
    }
    public static string VerifyCore(string name) => CoreIntegrity.Verify(name);
    private static void EnsureAlive(List<OwnedCore> owned)
    {
        if (owned.Any(x => x.Exited)) throw new InvalidOperationException("VPN core exited unexpectedly. Restarting and verifying a new connection.");
    }
    private static async Task WaitForListeners(List<OwnedCore> owned, int[] ports, CancellationToken token)
    {
        for (int i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested(); EnsureAlive(owned);
            bool ready = true;
            foreach (int port in ports)
            {
                try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, token); }
                catch (SocketException) { ready = false; break; }
            }
            if (ready) return;
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("Local proxy failed to start. Check whether the chosen port is already in use.");
    }
    private static async Task<string> Health(HttpClient client, CancellationToken token)
    {
        foreach (string url in new[] { "https://api.ipify.org/?format=json", "https://checkip.amazonaws.com/" })
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 4096) throw new InvalidDataException();
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                byte[] bytes = new byte[4097]; int total = 0, read;
                while (total < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(total), token)) > 0) total += read;
                if (total > 4096) throw new InvalidDataException();
                string text = System.Text.Encoding.UTF8.GetString(bytes, 0, total).Trim();
                string candidate = url.Contains("ipify") ? JsonNode.Parse(text)!["ip"]!.GetValue<string>() : text;
                if (IPAddress.TryParse(candidate, out _)) return candidate;
                throw new InvalidDataException();
            }
            catch (Exception error) when (!token.IsCancellationRequested && (error is HttpRequestException || error is TaskCanceledException || error is System.Text.Json.JsonException || error is InvalidDataException)) { }
        }
        throw new HttpRequestException("Independent HTTPS health endpoints failed.");
    }
    internal void KillOwnedCoreForTest()
    {
        lock (gate) { var process = cores.LastOrDefault()?.Process ?? throw new InvalidOperationException("No owned core."); process.Kill(); }
    }
    internal int RouterPidForTest { get { lock (gate) return cores.Last().Process.Id; } }
    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChange;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChange;
        try { await StopAsync(); }
        finally { await networkDebounce.DisposeAsync(); lifecycle.Dispose(); }
    }
}
public sealed record ProtocolProbeResult(string Protocol, bool Success, string ExitIp, string Reason, long ElapsedMs);
