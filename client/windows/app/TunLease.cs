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
    public bool Exited => guard.HasExited;
    public static string Name => "PersonalVPN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..8];
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
        var record = new Record { ParentId = Environment.ProcessId, ParentStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Name = Name, AdapterId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("wintun" + Name))) };
        await File.WriteAllTextAsync(Path.Combine(folder, "tun-lease.json"), JsonSerializer.Serialize(record), token);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--tun-guard"); info.ArgumentList.Add(folder);
        var guard = Process.Start(info) ?? throw new InvalidOperationException("Could not start adapter recovery guard.");
        var lease = new TunLease(folder, record, guard);
        try
        {
            for (int i = 0; i < 450; i++)
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
            using var mutex = new Mutex(true, @"Global\PersonalVPN-TUN-" + WindowsIdentity.GetCurrent().User!.Value, out bool created);
            if (!created) throw new InvalidOperationException("Another Personal VPN TUN session is still active.");
            using var adapter = new WintunAdapter(record.Name, record.AdapterId);
            record.AdapterId = adapter.AdapterId; File.WriteAllText(Path.Combine(folder, "tun-lease.json"), JsonSerializer.Serialize(record));
            File.WriteAllText(Path.Combine(folder, "tun-ready"), "ready");
            while (!parent.WaitForExit(250))
                if (File.Exists(Path.Combine(folder, "tun-stop"))) break;
            return 0; // finally / Dispose removes only this owned adapter.
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
        if (!guard.WaitForExit(8000)) { guard.Kill(); guard.WaitForExit(3000); }
        guard.Dispose();
        for (int i = 0; i < 30 && WintunAdapter.Exists(record.AdapterId); i++) Thread.Sleep(100);
        // Fallback for a killed guard / native teardown failure. Validate both the
        // exact recorded GUID and our adapter name before any device removal.
        if (WintunAdapter.Exists(record.AdapterId)) WintunAdapter.RemoveOrphan(record.AdapterId, record.Name);
        if (WintunAdapter.Exists(record.AdapterId)) throw new InvalidOperationException("Personal VPN adapter cleanup is incomplete. Reopen as Administrator for recovery.");
    }
    public static void RecoverAbandoned()
    {
        string root = Path.Combine(Store.Root, "runtime"); if (!Directory.Exists(root)) return;
        foreach (string folder in Directory.GetDirectories(root))
        {
            string file = Path.Combine(folder, "tun-lease.json"); if (!File.Exists(file)) continue;
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(file))!;
            if (record.Name != Name) throw new InvalidOperationException("Adapter recovery ownership mismatch.");
            bool alive = false;
            try { using var process = Process.GetProcessById(record.ParentId); alive = process.StartTime.ToUniversalTime().Ticks == record.ParentStarted; } catch (ArgumentException) { }
            if (alive) continue;
            File.WriteAllText(Path.Combine(folder, "tun-stop"), "stop");
            for (int i = 0; i < 30 && WintunAdapter.Exists(record.AdapterId); i++) Thread.Sleep(100);
            if (WintunAdapter.Exists(record.AdapterId)) WintunAdapter.RemoveOrphan(record.AdapterId, record.Name);
        }
    }
}
