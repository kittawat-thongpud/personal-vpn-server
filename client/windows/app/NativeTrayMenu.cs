using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PersonalVpn;

// A real HMENU lets Explorer and USER32 own menu tracking, submenus, Escape and
// outside-click dismissal. A ToolStrip popup is not a shell context menu.
internal sealed class NativeTrayMenu : Forms.NativeWindow, IDisposable
{
    private readonly Dictionary<uint, string> commands = new();
    private bool disposed;
    internal bool Tracking { get; private set; }
    internal IntPtr ActiveMenu { get; private set; }
    internal string LastResult { get; private set; } = "not opened";

    internal NativeTrayMenu()
    {
        CreateHandle(new Forms.CreateParams { Caption = "Personal VPN tray menu owner", Style = unchecked((int)0x80000000), ExStyle = 0x80 });
    }

    private IntPtr Build(Forms.ToolStripItemCollection entries)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception();
        try
        {
            foreach (Forms.ToolStripItem entry in entries)
            {
                uint flags = entry.Enabled ? 0u : 3u;
                UIntPtr id;
                string? text = entry.Text;
                if (entry is Forms.ToolStripSeparator) { flags = 0x800; id = UIntPtr.Zero; text = null; }
                else if (entry is Forms.ToolStripMenuItem item && item.DropDownItems.Count > 0)
                {
                    IntPtr child = Build(item.DropDownItems);
                    flags |= 0x10;
                    id = unchecked((UIntPtr)(nuint)child);
                    if (!AppendMenu(menu, flags, id, text)) { DestroyMenu(child); throw new Win32Exception(); }
                    continue;
                }
                else
                {
                    uint command = (uint)commands.Count + 1;
                    commands.Add(command, entry.Name);
                    id = new UIntPtr(command);
                    if (entry is Forms.ToolStripMenuItem { Checked: true }) flags |= 8;
                }
                if (!AppendMenu(menu, flags, id, text)) throw new Win32Exception();
            }
            return menu;
        }
        catch { DestroyMenu(menu); throw; }
    }

    internal string? Show(Forms.ToolStripItemCollection entries, Point point)
    {
        if (disposed || Tracking) return null;
        commands.Clear();
        IntPtr menu = Build(entries);
        try
        {
            ActiveMenu = menu; Tracking = true;
            bool foreground = SetForegroundWindow(Handle);
            // Return the selection exactly once; do not also send WM_COMMAND.
            uint flags = 0x100 | 0x80 | 2 | 0x20;
            if (GetSystemMetrics(40) != 0) flags |= 8;
            Marshal.SetLastPInvokeError(0);
            uint selected = TrackPopupMenuEx(menu, flags, point.X, point.Y, Handle, IntPtr.Zero);
            LastResult = "foreground=" + foreground + "; result=" + selected + "; error=" + Marshal.GetLastWin32Error();
            return commands.TryGetValue(selected, out string? command) ? command : null;
        }
        finally
        {
            Tracking = false; ActiveMenu = IntPtr.Zero;
            // Required shell handoff: omitting this can dismiss the next popup
            // immediately, producing the apparent double-right-click bug.
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu); commands.Clear();
        }
    }

    internal void Cancel() { if (Tracking) EndMenu(); }
    internal void ValidateDefinition(Forms.ToolStripItemCollection entries)
    {
        commands.Clear(); IntPtr menu = Build(entries);
        try { Validate(menu, entries); }
        finally { DestroyMenu(menu); commands.Clear(); }
    }
    private void Validate(IntPtr menu, Forms.ToolStripItemCollection entries)
    {
        if (GetMenuItemCount(menu) != entries.Count) throw new InvalidOperationException("Native menu item count differs from the action model.");
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i]; uint flags = GetMenuState(menu, (uint)i, 0x400);
            if (flags == uint.MaxValue) throw new Win32Exception();
            if (entry is Forms.ToolStripSeparator) { if ((flags & 0x800) == 0) throw new InvalidOperationException("Native separator lost."); continue; }
            if (((flags & 3) == 0) != entry.Enabled) throw new InvalidOperationException("Native menu enabled state differs.");
            if (entry is Forms.ToolStripMenuItem item && item.DropDownItems.Count > 0)
            {
                IntPtr child = GetSubMenu(menu, i);
                if (child == IntPtr.Zero || (flags & 0x10) == 0) throw new InvalidOperationException("Native submenu lost.");
                Validate(child, item.DropDownItems);
            }
            else
            {
                uint id = GetMenuItemID(menu, i);
                if (!commands.TryGetValue(id, out string? name) || name != entry.Name) throw new InvalidOperationException("Native command identity differs.");
                if (entry is Forms.ToolStripMenuItem selected && ((flags & 8) != 0) != selected.Checked) throw new InvalidOperationException("Native checkmark lost.");
            }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Cancel(); DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EndMenu();
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
}
