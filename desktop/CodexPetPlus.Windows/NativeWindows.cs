using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexPetPlus.Core;

namespace CodexPetPlus.Windows;

internal sealed record PetWindow(nint Handle, int ProcessId, string Title, PixelRect Bounds, uint Dpi);

internal static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; public readonly PixelRect Pixels => new(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    public static PetWindow? Inspect(nint handle)
    {
        if (handle == 0 || !IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var bounds)) return null;
        if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        GetWindowThreadProcessId(handle, out var pid);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        var dpi = Math.Max(96, GetDpiForWindow(handle));
        var maximum = 650 * dpi / 96;
        if (bounds.Pixels.Width is < 30 || bounds.Pixels.Height is < 30 || bounds.Pixels.Width > maximum || bounds.Pixels.Height > maximum) return null;
        var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
        return new(handle, pid, title.ToString(), bounds.Pixels, dpi);
    }

    public static PetWindow? UnderCursor()
    {
        if (!GetCursorPos(out var point)) return null;
        return Inspect(GetAncestor(WindowFromPoint(point), 2));
    }

    public static PetWindow? Find(string? preferredTitle)
    {
        var candidates = new List<PetWindow>();
        EnumWindows((window, _) => { if (Inspect(window) is { } pet) candidates.Add(pet); return true; }, 0);
        // A unique, explicitly named pet is safe to restore; ambiguous windows need selection.
        var named = candidates.Where(pet => pet.Title.Contains("pet", StringComparison.OrdinalIgnoreCase) || pet.Title.Contains("companion", StringComparison.OrdinalIgnoreCase) || pet.Title.Contains("펫")).ToList();
        if (named.Count == 1) return named[0];
        if (!string.IsNullOrEmpty(preferredTitle))
        {
            var previous = candidates.Where(pet => pet.Title == preferredTitle).ToList();
            if (previous.Count == 1) return previous[0];
        }
        return null;
    }

    public static PixelRect WorkArea(PetWindow pet)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(pet.Handle, 2), ref info) ? info.Work.Pixels : pet.Bounds;
    }

    public static void MakeCompanion(nint handle)
    {
        var style = (long)GetWindowLongPtr(handle, -20);
        SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000 | 0x00000080)); // no-activate, tool window
    }

    public static PanelPosition Follow(nint panel, PetWindow pet, int logicalWidth, int logicalHeight)
    {
        var scale = pet.Dpi / 96d;
        var width = (int)Math.Round(logicalWidth * scale); var height = (int)Math.Round(logicalHeight * scale);
        var position = Placement.BelowPet(pet.Bounds, WorkArea(pet), width, height, (int)Math.Round(5 * scale));
        SetWindowPos(panel, new nint(-1), position.X, position.Y, width, height, 0x0010); // keep app focus
        return position;
    }
}
