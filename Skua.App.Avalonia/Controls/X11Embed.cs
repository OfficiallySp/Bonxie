using System;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// The X11 calls behind the Linux tab host. A tab's client (another Bonxie process) puts its window inside a hidden
/// "parking" window of the host before showing it, so the window manager never takes it as a window of its own; the
/// host then shows it in the tab area through an <see cref="EmbeddedClientHost"/>. All calls go through one display
/// connection of Bonxie's own, kept open for the app's lifetime (the server destroys a connection's windows when it
/// closes). Avalonia's X11 error handler is in place, so a call on a window that has just gone doesn't end the app.
/// </summary>
internal static class X11Embed
{
    private const string LibX11 = "libX11.so.6";
    private const int ConfigureNotify = 22;
    private const long StructureNotifyMask = 1L << 17;
    private const int RevertToParent = 2;
    private const int IsViewable = 2;
    private const int XEventSize = 192;

    private static readonly object s_lock = new();
    private static IntPtr s_display;
    private static bool s_unavailable;

    /// <summary>The shared connection, or 0 off X11.</summary>
    public static IntPtr Display
    {
        get
        {
            lock (s_lock)
            {
                if (s_display == IntPtr.Zero && !s_unavailable && OperatingSystem.IsLinux())
                {
                    try
                    {
                        s_display = XOpenDisplay(IntPtr.Zero);
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                    {
                        s_unavailable = true;
                    }
                    if (s_display == IntPtr.Zero)
                        s_unavailable = true;
                }
                return s_display;
            }
        }
    }

    public static bool IsAvailable => Display != IntPtr.Zero;

    /// <summary>Host: an unmapped window that clients put their window in until the host shows it.</summary>
    public static IntPtr CreateParkingWindow()
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero)
            return IntPtr.Zero;
        lock (s_lock)
        {
            IntPtr window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 0, 0, 1, 1, 0, 0, 0x141414);
            XSync(display, false);
            return window;
        }
    }

    /// <summary>Client: moves <paramref name="window"/> into <paramref name="parent"/> and waits until the server has done it.</summary>
    public static bool Reparent(IntPtr window, IntPtr parent, int x = 0, int y = 0)
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero || window == IntPtr.Zero || parent == IntPtr.Zero)
            return false;
        lock (s_lock)
        {
            XReparentWindow(display, window, parent, x, y);
            XSync(display, false);
        }
        return true;
    }

    /// <summary>
    /// Gives <paramref name="window"/> the keyboard. The window manager only gives it to top-level windows, so an
    /// embedded window asks for it itself when it is clicked, and the host takes it back the same way.
    /// </summary>
    public static void Focus(IntPtr window)
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero || window == IntPtr.Zero)
            return;
        lock (s_lock)
        {
            // Only a viewable window can have the focus (BadMatch otherwise).
            if (XGetWindowAttributes(display, window, out XWindowAttributes attributes) == 0 || attributes.map_state != IsViewable)
                return;
            XGetInputFocus(display, out IntPtr current, out _);
            if (current == window)
                return;
            XSetInputFocus(display, window, RevertToParent, IntPtr.Zero);
            XFlush(display);
        }
    }

    /// <summary>The window that has the keyboard focus, or 0.</summary>
    public static IntPtr GetFocus()
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero)
            return IntPtr.Zero;
        lock (s_lock)
        {
            XGetInputFocus(display, out IntPtr focus, out _);
            return focus;
        }
    }

    /// <summary>Whether <paramref name="window"/> is <paramref name="ancestor"/> or inside it.</summary>
    public static bool IsInside(IntPtr window, IntPtr ancestor)
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero || window == IntPtr.Zero || ancestor == IntPtr.Zero)
            return false;
        lock (s_lock)
        {
            IntPtr current = window;
            for (int depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
            {
                if (current == ancestor)
                    return true;
                if (XQueryTree(display, current, out IntPtr root, out IntPtr parent, out IntPtr children, out _) == 0)
                    return false;
                if (children != IntPtr.Zero)
                    XFree(children);
                if (parent == root)
                    return false;
                current = parent;
            }
            return false;
        }
    }

    /// <summary>
    /// Tells an embedded Avalonia window where it is on the screen. A window inside another one gets no
    /// ConfigureNotify when that one moves, so it keeps its old screen position and opens its menus and popups
    /// there. A synthetic ConfigureNotify carries root coordinates (ICCCM 4.1.5), which Avalonia takes as they are.
    /// </summary>
    public static void SendScreenPosition(IntPtr window)
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero || window == IntPtr.Zero)
            return;
        lock (s_lock)
        {
            if (XGetGeometry(display, window, out IntPtr root, out _, out _, out uint width, out uint height, out uint border, out _) == 0)
                return;
            if (XTranslateCoordinates(display, window, root, 0, 0, out int rootX, out int rootY, out _) == 0)
                return;

            IntPtr ev = Marshal.AllocHGlobal(XEventSize);
            try
            {
                for (int i = 0; i < XEventSize; i += 8)
                    Marshal.WriteInt64(ev, i, 0);
                // XConfigureEvent on LP64: type, serial, send_event, display, event, window, x, y, width, height,
                // border_width, above, override_redirect.
                Marshal.WriteInt32(ev, 0, ConfigureNotify);
                Marshal.WriteInt32(ev, 16, 1);
                Marshal.WriteIntPtr(ev, 24, display);
                Marshal.WriteIntPtr(ev, 32, window);
                Marshal.WriteIntPtr(ev, 40, window);
                Marshal.WriteInt32(ev, 48, rootX);
                Marshal.WriteInt32(ev, 52, rootY);
                Marshal.WriteInt32(ev, 56, (int)width);
                Marshal.WriteInt32(ev, 60, (int)height);
                Marshal.WriteInt32(ev, 64, (int)border);
                XSendEvent(display, window, false, new IntPtr(StructureNotifyMask), ev);
                XFlush(display);
            }
            finally
            {
                Marshal.FreeHGlobal(ev);
            }
        }
    }

    /// <summary>Whether <paramref name="window"/> still exists.</summary>
    public static bool Exists(IntPtr window)
    {
        IntPtr display = Display;
        if (display == IntPtr.Zero || window == IntPtr.Zero)
            return false;
        lock (s_lock)
            return XGetWindowAttributes(display, window, out _) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual, root;
        public int @class, bit_gravity, win_gravity, backing_store;
        public UIntPtr backing_planes, backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed, map_state;
        public IntPtr all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport(LibX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

    [DllImport(LibX11)]
    private static extern int XReparentWindow(IntPtr display, IntPtr window, IntPtr parent, int x, int y);

    [DllImport(LibX11)]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, IntPtr time);

    [DllImport(LibX11)]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);

    [DllImport(LibX11)]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport(LibX11)]
    private static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    private static extern int XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int srcX, int srcY, out int destX, out int destY, out IntPtr child);

    [DllImport(LibX11)]
    private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);

    [DllImport(LibX11)]
    private static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr eventMask, IntPtr ev);

    [DllImport(LibX11)]
    private static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XSync(IntPtr display, bool discard);
}
