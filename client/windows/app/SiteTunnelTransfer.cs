using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PersonalVpn;

public sealed partial class MainWindow
{
    private async Task ImportSiteTunnel()
    {
        var dialog = new OpenFileDialog { Title = "Import Site Tunnel", Filter = "Site Tunnel JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        string path = dialog.FileName;
        var policy = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return SiteTunnelProfile.Import(stream);
        });
        if (quitInProgress) return;
        var summary = $"{policy.Entries.Count} site groups · {ModeName(policy.Mode)}\n\nReplace the current Site Tunnel rules?";
        if (MessageBox.Show(this, summary, "Import Site Tunnel", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var updated = settings.Snapshot();
        updated.Sites = policy;
        Store.Save(updated);
        settings = updated;
        settingsRevision++;
        dirty = backend.Running || connectBusy;
        Navigate("SiteTunnel");
    }

    private async Task ExportSiteTunnel()
    {
        byte[] content = SiteTunnelProfile.Export(settings.Sites!);
        var dialog = new SaveFileDialog { Title = "Export Site Tunnel", FileName = SiteTunnelProfile.FileName, Filter = "Site Tunnel JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) await File.WriteAllBytesAsync(dialog.FileName, content);
    }
}
