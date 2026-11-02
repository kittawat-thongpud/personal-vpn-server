using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace PersonalVpn;

public static class TrayValidation
{
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static IEnumerable<Forms.ToolStripMenuItem> Items(Forms.ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<Forms.ToolStripMenuItem>())
        { yield return item; foreach (var child in Items(item.DropDownItems)) yield return child; }
    }
    public static List<string> Checks()
    {
        var result = new List<string>(); var commands = new List<string>();
        var state = new TrayState("Disconnected", "REALITY", "Proxy", false, false, false, true, "—", true, true,
            new[] { new TrayProfile("test", "Japan & preview", true) });
        using var tray = new TrayHost(() => state, command => { commands.Add(command); return Task.CompletedTask; }, exception => throw exception, visible: false);
        var menu = Items(tray.MenuForTest.Items).Where(x => x.Name != "").ToDictionary(x => x.Name);
        Assert(menu["connect"].Enabled && !menu["reconnect"].Enabled && !menu["copy-exit"].Enabled, "Disconnected tray actions are incorrect.");
        Assert(menu["profile:test"].Checked && menu["protocol:REALITY"].Checked && menu["mode:Proxy"].Checked, "Tray selection lost.");
        menu["open"].PerformClick(); menu["logs"].PerformClick(); menu["clear-logs"].PerformClick();
        Assert(commands.SequenceEqual(new[] { "open", "logs", "clear-logs" }), "Tray action dispatched incorrectly.");
        state = state with { Status = "Connected", Running = true, ExitIp = "203.0.113.10", WindowVisible = false }; tray.Update();
        menu = Items(tray.MenuForTest.Items).Where(x => x.Name != "").ToDictionary(x => x.Name);
        Assert(menu["connect"].Text == "Disconnect" && menu["reconnect"].Enabled && menu["copy-exit"].Enabled && !menu.ContainsKey("hide"), "Connected / hidden tray state incorrect.");
        state = state with { Busy = true }; menu = Items(tray.MenuForTest.Items).Where(x => x.Name != "").ToDictionary(x => x.Name);
        Assert(!menu["connect"].Enabled && !menu["reconnect"].Enabled && menu["exit"].Enabled, "Busy tray must prevent duplicate connects and allow explicit exit.");
        state = state with { Busy = false, HasReality = false, Dirty = true }; menu = Items(tray.MenuForTest.Items).Where(x => x.Name != "").ToDictionary(x => x.Name);
        Assert(!menu["protocol:REALITY"].Enabled && menu["connect"].Text == "Apply & reconnect", "Unsupported protocol or dirty state incorrect.");
        tray.Dispose(); tray.Dispose(); tray.Update();
        result.Add("tray menu state, dispatch, profile / protocol checks, busy guards and idempotent disposal");
        CaptureCapabilities.EnsureAvailable("Proxy"); bool rejected = false;
        try { CaptureCapabilities.EnsureAvailable("TUN"); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && !CaptureCapabilities.TunAvailable, "Unimplemented TUN must not silently start Proxy or change networking.");
        result.Add("unavailable TUN guard preserves effective Proxy capture"); return result;
    }
}
