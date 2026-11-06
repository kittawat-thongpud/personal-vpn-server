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
            SetForegroundWindow(Handle);
            // Return the selection exactly once; do not also send WM_COMMAND.
            uint flags = 0x100 | 0x80 | 2 | 0x20;
            if (GetSystemMetrics(40) != 0) flags |= 8;
            uint selected = TrackPopupMenuEx(menu, flags, point.X, point.Y, Handle, IntPtr.Zero);
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
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Cancel(); DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EndMenu();
}
