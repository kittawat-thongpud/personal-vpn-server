using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PersonalVpn;

public sealed partial class MainWindow
{
    private TrayHost? tray;
    private EventWaitHandle? showRequest;
    private bool quitInProgress, hideNoticeShown;
    private void InitializeTray()
    {
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/PersonalVPN;component/Assets/personal-vpn.ico", UriKind.Absolute));
        ShowInTaskbar = false;
        if (previewOnly) return;
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        tray = new TrayHost(TraySnapshot, TrayAction, exception => { RestoreFromTray(); ShowError(exception); });
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        Closed += (_, _) => { tray?.Dispose(); showRequest?.Dispose(); };
    }
    internal static string ShowEventName => @"Local\PersonalVPN-Show-" + Environment.UserName;
    internal static bool NotifyExistingWindow()
    {
        try { using var signal = EventWaitHandle.OpenExisting(ShowEventName); return signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private TrayState TraySnapshot()
    {
        var profile = settings.Profiles.FirstOrDefault(x => x.Id == settings.SelectedId);
        return new(backend.State, settings.Protocol, settings.CaptureMode, backend.Running, connectBusy || quitInProgress,
            dirty, IsVisible, backend.ExitIp, profile?.Xray != null, profile?.Hysteria != null,
            settings.Profiles.Select(x => new TrayProfile(x.Id, x.Name, x.Id == settings.SelectedId)).ToArray());
    }
    private void RestoreFromTray()
    {
        if (quitInProgress) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Refresh();
    }
    private void HideToTray()
    {
        Hide(); if (!hideNoticeShown && !previewOnly) { hideNoticeShown = true; tray?.ExplainHidden(); }
    }
    private async Task ToggleConnection()
    {
        if (connectBusy || quitInProgress) return;
        if (backend.Running && !dirty)
        {
            connectBusy = true; Refresh();
            try { if (backend.State == "Proxy conflict") await backend.ReclaimProxyAsync(); else await backend.StopAsync(); }
            finally { connectBusy = false; Refresh(); }
        }
        else await ConnectSelected();
    }
    private async Task TrayAction(string command)
    {
        if (quitInProgress) return;
        if (command == "exit") { await QuitApplication(); return; }
        if (command == "open") { RestoreFromTray(); return; }
        if (command == "hide") { HideToTray(); return; }
        if (command == "clear-logs") { ClearSessionLogs(); return; }
        if (command == "copy-exit") { if (backend.ExitIp != "—") Clipboard.SetText(backend.ExitIp); return; }
        if (command == "connect") { await ToggleConnection(); return; }
        if (command == "reconnect") { if (backend.Running && !connectBusy) await ConnectSelected(); return; }
        RestoreFromTray();
        if (command == "settings") Navigate("Settings");
        else if (command == "logs") Navigate("Diagnostics");
        else if (command == "export-logs") ExportSessionLogs();
        else if (command == "profiles") Navigate("Profiles");
        else if (command.StartsWith("mode:", StringComparison.Ordinal)) { Navigate("Home"); RequestCaptureMode(command[5..]); }
        else if (command.StartsWith("profile:", StringComparison.Ordinal))
        {
            if (connectBusy) return; string id = command[8..];
            if (settings.Profiles.Any(x => x.Id == id) && id != settings.SelectedId) { settings.SelectedId = id; Changed(); Navigate("Home"); }
        }
        else if (command.StartsWith("protocol:", StringComparison.Ordinal))
        {
            if (connectBusy) return; string protocol = command[9..]; if (protocol == settings.Protocol) return;
            if (MessageBox.Show(this, "Use " + protocol + "?" + (backend.Running ? " The current Personal VPN session will reconnect." : " This will be used on your next connection."), "Confirm protocol", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            settings.Protocol = protocol; settings.ChainReality = false; Changed(); Navigate("Home"); if (backend.Running) await ConnectSelected();
        }
    }
    private void ClearSessionLogs()
    {
        backend.Log.Clear(); lastLogText = ""; if (logText != null) logText.Text = ""; Refresh();
    }
    private void ExportSessionLogs()
    {
        var picker = new SaveFileDialog { FileName = "personal-vpn-session.json", Filter = "JSON|*.json" };
        if (picker.ShowDialog(this) == true) File.WriteAllText(picker.FileName, JsonSerializer.Serialize(backend.Log.Snapshot(), Store.JsonOptions));
    }
    internal bool HiddenCloseForTest()
    {
        if (!previewOnly) throw new InvalidOperationException("Preview only.");
        int count = backend.Log.Snapshot().Length; Close(); bool hidden = !IsVisible && !exiting;
        Show(); return hidden && !ShowInTaskbar && count == backend.Log.Snapshot().Length;
    }
    private async Task QuitApplication()
    {
        if (quitInProgress) return; quitInProgress = true; IsEnabled = false; timer.Stop(); tray?.Update();
        try { await backend.DisposeAsync(); }
        finally { exiting = true; tray?.Dispose(); showRequest?.Dispose(); Close(); }
    }
}
