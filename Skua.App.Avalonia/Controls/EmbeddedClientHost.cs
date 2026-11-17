using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Collections.Generic;

namespace Skua.App.Avalonia.Controls;

/// <summary>
/// Linux tab host: shows one tab's client window (an X11 window of another Bonxie process) in the host window.
/// When the control leaves the visual tree (its tab moves to another host window, or the window closes), Avalonia
/// parks the client's window in a hidden window of its own instead of destroying it, and adopts it again where
/// the control comes back, so the client keeps running and stays logged in.
/// </summary>
public sealed class EmbeddedClientHost : NativeControlHost
{
    public EmbeddedClientHost(IntPtr clientWindow)
    {
        ClientWindow = clientWindow;
    }

    /// <summary>The client's top-level X11 window.</summary>
    public IntPtr ClientWindow { get; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        => new PlatformHandle(ClientWindow, "XID");

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // The window belongs to the client process; Avalonia has already parked it.
    }
}

/// <summary>
/// Lays out the tab area of the Linux tab host: the selected tab's client fills it, or in grid view every client
/// gets a cell (the same grid the Windows host uses). Visibility is set by the host; this places the visible ones.
/// </summary>
public sealed class ClientTilePanel : Panel
{
    private IReadOnlyList<Control> _order = Array.Empty<Control>();
    private bool _isGrid;

    /// <summary>The children in tab order.</summary>
    public IReadOnlyList<Control> Order
    {
        get => _order;
        set
        {
            _order = value;
            InvalidateArrange();
        }
    }

    public bool IsGrid
    {
        get => _isGrid;
        set
        {
            if (_isGrid == value)
                return;
            _isGrid = value;
            InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
            child.Measure(availableSize);
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<Control> visible = new();
        foreach (Control child in _order)
            if (child.IsVisible && Children.Contains(child))
                visible.Add(child);
        foreach (Control child in Children)
            if (child.IsVisible && !visible.Contains(child))
                visible.Add(child);

        if (!_isGrid || visible.Count <= 1)
        {
            foreach (Control child in visible)
                child.Arrange(new Rect(finalSize));
            return finalSize;
        }

        int n = visible.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        double cellW = Math.Floor(finalSize.Width / cols);
        double cellH = Math.Floor(finalSize.Height / rows);
        for (int i = 0; i < n; i++)
        {
            int r = i / cols;
            int c = i % cols;
            visible[i].Arrange(new Rect(c * cellW, r * cellH, cellW, cellH));
        }
        return finalSize;
    }
}
