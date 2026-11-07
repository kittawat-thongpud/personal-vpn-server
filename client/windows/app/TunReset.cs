using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
namespace PersonalVpn;
internal static class TunReset
{
    public static void Capture(Guid id)
    {
        // Missing capture IP / DNS does not prove that capture routes are gone.
        // Always let the GUID-scoped reset inspect and verify the route table.
        string system = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), system, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "reset-tun.ps1"), "-AdapterId", id.ToString("D") }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("TUN capture reset did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000)) { process.Kill(true); throw new InvalidOperationException("TUN capture reset timed out; the adapter was retained."); }
        Task.WaitAll(new Task[] { output, error }, 2000);
        if (process.ExitCode != 0) throw new InvalidOperationException("TUN route / DNS reset failed; the adapter was retained. " + (Environment.GetEnvironmentVariable("CI") == "true" ? error.Result : "Inspect backend logs before reconnecting."));
    }
}
