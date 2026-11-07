using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

public sealed class TunLease : IDisposable
{
    public sealed class Record
    {
        public int ParentId { get; set; }
        public long ParentStarted { get; set; }
        public string Name { get; set; } = "";
        public Guid AdapterId { get; set; }
    }
    private readonly string folder;
    private readonly Record record;
    private readonly Process guard;
    private bool disposed;
    public string InterfaceName => record.Name;
    public Guid AdapterId => record.AdapterId;
    internal void ObserveAdapter(Guid id) { record.AdapterId = id; }
    public bool Exited => guard.HasExited;
    public static string Name => AdapterService.AdapterName;
    private static string LegacyName => "PersonalVPN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..8];
    private TunLease(string folder, Record record, Process guard) { this.folder = folder; this.record = record; this.guard = guard; }
    private static string ValidFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        string root = Path.GetFullPath(Path.Combine(Store.Root, "runtime"));
        if (Path.GetDirectoryName(folder) != root) throw new InvalidOperationException("Invalid adapter lease directory.");
        return folder;
    }
    public static async Task<TunLease> Start(string folder, CancellationToken token)
    {
        folder = ValidFolder(folder);
        AdapterService.EnsureRunning();
        var record = new Record { ParentId = Environment.ProcessId, ParentStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Name = Name, AdapterId = AdapterService.CurrentId() };
        await File.WriteAllTextAsync(Path.Combine(folder, "tun-lease.json"), JsonSerializer.Serialize(record), token);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--tun-guard"); info.ArgumentList.Add(folder);
        var guard = Process.Start(info) ?? throw new InvalidOperationException("Could not start adapter recovery guard.");
        var lease = new TunLease(folder, record, guard);
        try
        {
            for (int i = 0; i < 600; i++)
            {
                token.ThrowIfCancellationRequested();
                string error = Path.Combine(folder, "tun-error.txt");
                if (File.Exists(error)) throw new InvalidOperationException("Adapter preparation failed: " + (await File.ReadAllTextAsync(error, token)));
                if (guard.HasExited) throw new InvalidOperationException("Adapter recovery guard stopped.");
                if (File.Exists(Path.Combine(folder, "tun-ready")))
                { record.AdapterId = JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(Path.Combine(folder, "tun-lease.json"), token))!.AdapterId; return lease; }
                await Task.Delay(100, token);
            }
            throw new InvalidOperationException("Adapter preparation timed out.");
        }
        catch { lease.Dispose(); throw; }
    }
    public static int Watch(string folder)
    {
        folder = ValidFolder(folder);
        var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(Path.Combine(folder, "tun-lease.json")))!;
        if (record.Name != Name || record.ParentId == Environment.ProcessId) throw new InvalidOperationException("Invalid adapter owner.");
        try
        {
            using var parent = Process.GetProcessById(record.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != record.ParentStarted || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
            using var mutex = new Mutex(false, @"Global\PersonalVPN-TUN");
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another Personal VPN TUN session is still active.");
            try
            {
            var physical = new PhysicalNetworkLease(folder);
            try
            {
                TunReset.Capture(record.AdapterId);
                physical.Prepare();
                File.WriteAllText(Path.Combine(folder, "tun-ready"), "ready");
                int ticks = 0;
                while (!parent.WaitForExit(250))
                {
                    if (File.Exists(Path.Combine(folder, "tun-stop"))) break;
                    if (++ticks % 4 == 0) physical.Prepare(); // Hot-plugged links; native dial fails safely until prepared.
                }
            }
            finally
            {
                // Independent attempts: a TUN reset failure must not skip the
                // physical-interface restore, or the other way around.
                try { TunReset.Capture(record.AdapterId); }
                finally { physical.Restore(); }
            }
            File.WriteAllText(Path.Combine(folder, "tun-reset-done"), "done");
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(folder, "tun-error.txt"), exception is InvalidOperationException ? exception.Message : exception.GetType().Name);
            return 1;
        }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        File.WriteAllText(Path.Combine(folder, "tun-stop"), "stop");
        bool exited = guard.WaitForExit(50000);
        if (!exited) { guard.Kill(); guard.WaitForExit(3000); }
        int code = guard.HasExited ? guard.ExitCode : -1; guard.Dispose();
        // The independent guard resets capture even after a killed GUI. A device
        // remaining here is expected: the Windows service owns its lifetime.
        if (!exited || code != 0)
        {
            // Never reset another session when preparation failed on the mutex.
            // A journal belongs to this guard; ready means it owned capture.
            if (File.Exists(Path.Combine(folder, "physical-network.json")) || File.Exists(Path.Combine(folder, "tun-ready")))
            {
                using var mutex = new Mutex(false, @"Global\PersonalVPN-TUN");
                bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Capture recovery is still active; recovery data was retained.");
                try { try { TunReset.Capture(record.AdapterId); } finally { new PhysicalNetworkLease(folder).Restore(); } }
                finally { mutex.ReleaseMutex(); }
            }
        }
        // If preparation never published ready, Start retains its original error.
        // There is no active capture to reset, and another owner must be untouched.
    }
    internal static void CleanRuntimeFolder(string folder)
    {
        folder = ValidFolder(folder);
        if (!Directory.Exists(folder)) return;
        if (File.Exists(Path.Combine(folder, "physical-network.json")))
        {
            // An unplugged USB NIC can defer a field restore. Preserve just the
            // recovery metadata; remove all plaintext credentials/configs.
            var retained = new[] { "tun-lease.json", "physical-network.json", "tun-stop", "tun-reset-done" };
            foreach (string file in Directory.GetFiles(folder)) if (!retained.Contains(Path.GetFileName(file))) File.Delete(file);
            return;
        }
        Directory.Delete(folder, true);
    }
    public static void RecoverAbandoned()
    {
        string root = Path.Combine(Store.Root, "runtime"); if (!Directory.Exists(root)) return;
        foreach (string folder in Directory.GetDirectories(root))
        {
            string file = Path.Combine(folder, "tun-lease.json"); if (!File.Exists(file)) continue;
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(file))!;
            if (record.Name != Name && record.Name != LegacyName) throw new InvalidOperationException("Adapter recovery ownership mismatch.");
            bool alive = false;
            try { using var process = Process.GetProcessById(record.ParentId); alive = process.StartTime.ToUniversalTime().Ticks == record.ParentStarted; } catch (ArgumentException) { }
            if (alive && !File.Exists(Path.Combine(folder, "tun-stop"))) continue;
            File.WriteAllText(Path.Combine(folder, "tun-stop"), "stop");
            if (record.Name == Name)
            {
                using var mutex = new Mutex(false, @"Global\PersonalVPN-TUN");
                bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (File.Exists(Path.Combine(folder, "tun-reset-done"))) continue; // Only a deferred, absent-device journal remains.
                    throw new InvalidOperationException("Previous capture recovery is still active. Retry after it finishes; recovery data was retained.");
                }
                try { try { TunReset.Capture(record.AdapterId); } finally { new PhysicalNetworkLease(folder).Restore(); } }
                finally { mutex.ReleaseMutex(); }
            }
            else if (WintunAdapter.Exists(record.AdapterId)) WintunAdapter.RemoveOrphan(record.AdapterId, record.Name); // One-time legacy migration only.
        }
    }
}
