using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace PersonalVpn;

public sealed record TrayProfile(string Id, string Name, bool Selected);
public sealed record TrayState(string Status, string Protocol, string CaptureMode, bool Running,
    bool Busy, bool Dirty, bool WindowVisible, string ExitIp, bool HasReality, bool HasHysteria,
    IReadOnlyList<TrayProfile> Profiles);

// WinForms provides the native shell notification icon. WPF owns connection state
// and all actions run on its UI thread; this class never changes network settings.
public sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Func<TrayState> state;
    private readonly Func<string, Task> action;
    private readonly Action<Exception> error;
    private readonly Dictionary<string, Icon> icons = new();
    private bool disposed;
    private string lastStatus = "";
    private DateTime lastNotice = DateTime.MinValue;
    public TrayHost(Func<TrayState> state, Func<string, Task> action, Action<Exception> error, bool visible = true)
    {
        this.state = state; this.action = action; this.error = error;
        using var stream = typeof(TrayHost).Assembly.GetManifestResourceStream("PersonalVpn.TrayIcon.ico")
            ?? throw new InvalidOperationException("Application tray icon is missing.");
        using var source = new Icon(stream, new Size(32, 32));
        foreach (string color in new[] { "#8395B2", "#16C8A0", "#F1C77A" }) icons[color] = Badge(source, ColorTranslator.FromHtml(color));
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors());
        menu.BackColor = ColorTranslator.FromHtml("#152338"); menu.ForeColor = ColorTranslator.FromHtml("#ECF2FF");
        menu.ShowImageMargin = false; menu.ShowCheckMargin = true; menu.Font = new Font("Segoe UI", 10);
        menu.Opening += (_, args) =>
        {
            Rebuild();
            // WinForms initializes Cancel from the item count BEFORE Opening.
            // The first request starts empty, so populating alone needs two clicks.
            args.Cancel = menu.Items.Count == 0;
        };
        // Keep the shell's normal dismissal: hover alone does not close a menu,
        // but a click in another application can arrive as AppFocusChange.
        // Canceling that reason leaves the popup stuck above other windows.
        icon = new Forms.NotifyIcon { Icon = icons["#8395B2"], Text = "Personal VPN", ContextMenuStrip = menu };
        icon.MouseUp += async (_, e) => { if (e.Button == Forms.MouseButtons.Left) await Dispatch("open"); };
        icon.BalloonTipClicked += async (_, _) => await Dispatch("open");
        Update(); icon.Visible = visible;
    }
    private static Icon Badge(Icon source, Color color)
    {
        using var bitmap = source.ToBitmap(); using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var outline = new SolidBrush(ColorTranslator.FromHtml("#0D1627")); using var fill = new SolidBrush(color);
        graphics.FillEllipse(outline, 19, 19, 13, 13); graphics.FillEllipse(fill, 21, 21, 9, 9);
        IntPtr handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private async Task Dispatch(string command)
    {
        if (disposed) return;
        try { await action(command); } catch (Exception exception) { error(exception); }
    }
    private Forms.ToolStripMenuItem Item(string name, string text, bool enabled = true, bool selected = false)
    {
        var item = new Forms.ToolStripMenuItem(text) { Name = name, Enabled = enabled, Checked = selected, ForeColor = ColorTranslator.FromHtml("#ECF2FF") };
        item.Click += async (_, _) => await Dispatch(name); return item;
    }
    internal void Rebuild()
    {
        foreach (var item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose(); menu.Items.Clear();
        var s = state();
        menu.Items.Add(Item("status", "Personal VPN · " + s.Status, false));
        menu.Items.Add(Item("open", s.WindowVisible ? "Show Personal VPN" : "Open Personal VPN"));
        if (s.WindowVisible) menu.Items.Add(Item("hide", "Hide to tray"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("connect", s.Busy ? "Connecting…" : s.Running ? s.Dirty ? "Apply & reconnect" : s.Status == "Proxy conflict" ? "Use Personal VPN proxy" : "Disconnect" : "Connect", !s.Busy && s.Profiles.Count > 0));
        menu.Items.Add(Item("reconnect", "Reconnect", s.Running && !s.Busy));
        var servers = new Forms.ToolStripMenuItem("Server profiles") { Enabled = !s.Busy };
        foreach (var profile in s.Profiles) servers.DropDownItems.Add(Item("profile:" + profile.Id, profile.Name.Replace("&", "&&"), !s.Busy, profile.Selected));
        servers.DropDownItems.Add(new Forms.ToolStripSeparator()); servers.DropDownItems.Add(Item("profiles", "Manage / import profiles…")); menu.Items.Add(servers);
        var protocols = new Forms.ToolStripMenuItem("Protocol") { Enabled = !s.Busy };
        protocols.DropDownItems.Add(Item("protocol:REALITY", "REALITY", s.HasReality && !s.Busy, s.Protocol == "REALITY"));
        protocols.DropDownItems.Add(Item("protocol:Hysteria 2", "Hysteria 2", s.HasHysteria && !s.Busy, s.Protocol == "Hysteria 2")); menu.Items.Add(protocols);
        var capture = new Forms.ToolStripMenuItem("Capture mode");
        capture.DropDownItems.Add(Item("mode:Proxy", "Proxy", !s.Busy, s.CaptureMode == "Proxy"));
        capture.DropDownItems.Add(Item("mode:TUN", CaptureCapabilities.TunAvailable ? "TUN" : "TUN · in development", !s.Busy, s.CaptureMode == "TUN")); menu.Items.Add(capture);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("settings", "Settings")); menu.Items.Add(Item("logs", "Backend logs"));
        menu.Items.Add(Item("clear-logs", "Clear session logs")); menu.Items.Add(Item("export-logs", "Export session logs…"));
        menu.Items.Add(Item("copy-exit", "Copy exit IP", s.ExitIp != "—" && !string.IsNullOrWhiteSpace(s.ExitIp)));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(Item("exit", "Disconnect & exit"));
    }
    internal Forms.ContextMenuStrip MenuForTest => menu;
    public void Update()
    {
        if (disposed) return; var s = state();
        string text = "Personal VPN · " + s.Status + " · " + s.Protocol; icon.Text = text.Length > 63 ? text[..63] : text;
        icon.Icon = icons[s.Status == "Connected" ? "#16C8A0" : s.Running ? "#F1C77A" : "#8395B2"];
        if (s.Status != lastStatus && (s.Status is "Reconnecting" or "Proxy conflict") && DateTime.UtcNow - lastNotice > TimeSpan.FromSeconds(30))
        { lastNotice = DateTime.UtcNow; icon.ShowBalloonTip(4000, "Personal VPN · " + s.Status, "Open Backend logs for the connection result. No automatic Direct fallback.", Forms.ToolTipIcon.Warning); }
        lastStatus = s.Status;
    }
    public void ExplainHidden() => icon.ShowBalloonTip(3500, "Personal VPN is in the tray", "The VPN keeps running. Use Disconnect & exit from this menu to quit.", Forms.ToolTipIcon.Info);
    public void Dispose()
    {
        if (disposed) return; disposed = true; icon.Visible = false; icon.Dispose(); menu.Dispose(); foreach (var image in icons.Values) image.Dispose();
    }
    private sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => ColorTranslator.FromHtml("#152338");
        public override Color MenuItemSelected => ColorTranslator.FromHtml("#2C405F");
        public override Color MenuItemBorder => ColorTranslator.FromHtml("#41618A");
        public override Color MenuBorder => ColorTranslator.FromHtml("#344662");
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color CheckBackground => MenuItemSelected;
        public override Color CheckSelectedBackground => MenuItemSelected;
        public override Color SeparatorDark => MenuBorder;
        public override Color SeparatorLight => ToolStripDropDownBackground;
    }
}
