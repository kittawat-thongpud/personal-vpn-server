using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace PersonalVpn;

internal static class NativeTrayValidation
{
    internal static void Check(TrayHost tray, string dismissal)
    {
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        using var outside = new Forms.Form { ShowInTaskbar = false, StartPosition = Forms.FormStartPosition.Manual,
            Bounds = new Rectangle(area.Right - 200, area.Bottom - 150, 180, 110), Text = "Isolated tray test" };
        outside.Show();
        ShowWindow(outside.Handle, 5); // Override the CI launcher's initial SW_HIDE.
        var watch = Stopwatch.StartNew();
        using var timer = new Forms.Timer { Interval = 40 };
        int phase = 0; Exception? failure = null;
        bool requested = false; Task? opened = null;
        outside.MouseUp += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Right || requested) return;
            requested = true;
            opened = tray.OpenMenu(Forms.Cursor.Position);
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.ElapsedMilliseconds > 3000) throw new InvalidOperationException("Native tray dismissal timed out: " + dismissal);
                if (!requested) return;
                if (phase == 2) return;
                var gui = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
                if (!GetGUIThreadInfo(GetCurrentThreadId(), ref gui) || (gui.Flags & 4) == 0 || gui.MenuOwner != tray.PopupForTest.Handle)
                    throw new InvalidOperationException("One right-click request did not enter a real native menu loop.");
                if (phase == 0)
                {
                    // Moving away alone must never close the popup.
                    if (!SetCursorPos(area.Left + 20, area.Top + 15)) throw new Win32Exception();
                    phase = 1; watch.Restart(); return;
                }
                if (watch.ElapsedMilliseconds < 160) return;
                phase = 2; watch.Restart();
                if (dismissal == "outside") Send(Mouse(2), Mouse(4));
                else if (dismissal == "selection") Send(Key(0x24), Key(0x24, true), Key(0x0D), Key(0x0D, true));
                else Send(Key(0x1B), Key(0x1B, true));
            }
            catch (Exception error) { failure = error; timer.Stop(); tray.PopupForTest.Cancel(); }
        };
        timer.Start();
        try
        {
            // The genuine right-click supplies foreground activation, as a
            // shell tray callback does. Calling TrackPopupMenu from a hidden
            // background test process is not an equivalent interaction.
            if (!SetCursorPos(outside.Bounds.X + 50, outside.Bounds.Y + 65)) throw new Win32Exception();
            Send(Mouse(8), Mouse(0x10));
            var deadline = Stopwatch.StartNew();
            while (opened == null && failure == null && deadline.ElapsedMilliseconds < 5000) { Forms.Application.DoEvents(); Thread.Sleep(10); }
            if (opened == null) throw new InvalidOperationException("Right-click did not reach the isolated tray host.");
            opened.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); outside.Close(); }
        if (failure != null) throw failure;
        if (phase != 2 || tray.PopupForTest.Tracking) throw new InvalidOperationException("Native popup closed before test input: " + dismissal + "; phase=" + phase + "; " + tray.PopupForTest.LastResult);
    }
    private static Input Mouse(uint flags) => new() { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = flags } } };
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
    private static void Send(params Input[] events)
    { if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length) throw new Win32Exception(); }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] events, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint id, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
