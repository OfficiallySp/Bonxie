using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.App.Avalonia.Controls;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DynamicResource = Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension;

namespace Skua.App.Avalonia.Views;

// Several host windows in one host process, tabs moving between them (dragged like browser tabs, or from the tab
// menu), and the Linux side of the tab host. A tab's client is a process of its own and keeps running while its tab
// moves: on Windows its window gets a new owner, on Linux a new parent.
public partial class TabbedHostWindow
{
    #region Windows of the process

    /// <summary>Every open host window, in the order they were opened.</summary>
    private static readonly List<TabbedHostWindow> s_windows = new();

    /// <summary>The same windows, the one used last first.</summary>
    private static readonly List<TabbedHostWindow> s_activationOrder = new();

    private static readonly CancellationTokenSource s_pipeCts = new();
    private static bool s_pipeServerStarted;
    private static bool s_processCallbacksInitialized;
    private static IntPtr s_parkingWindow;

    private readonly bool _startEmpty;
    private IScriptOption? _options;
    private DispatcherTimer? _titleSyncTimer;

    /// <summary>A window opened for a dragged-out tab: the tab it takes over once open.</summary>
    private TabInfo? _pendingAdoption;

    /// <summary>The host window new launches go to: the one used last.</summary>
    private static TabbedHostWindow? ActiveWindow
        => s_activationOrder.FirstOrDefault(w => !w._isClosing) ?? s_windows.FirstOrDefault(w => !w._isClosing);

    /// <summary>The tabs of every host window, in window then tab order.</summary>
    private static IEnumerable<TabInfo> AllTabs
        => s_windows.ToList().SelectMany(w => w.InstancesTabControl.Items.OfType<TabItem>()
            .Where(w._tabs.ContainsKey).Select(t => w._tabs[t]));

    /// <summary>Army Control acts on every account, whichever window its tab is in.</summary>
    private static List<TabInfo> ArmyTabs => AllTabs.ToList();

    /// <summary>Linux: the hidden window clients wait in until their tab shows them.</summary>
    private static IntPtr ParkingWindow
    {
        get
        {
            if (s_parkingWindow == IntPtr.Zero && !OperatingSystem.IsWindows())
                s_parkingWindow = X11Embed.CreateParkingWindow();
            return s_parkingWindow;
        }
    }

    private void InitializeMultiWindow()
    {
        s_activationOrder.Insert(0, this);
        Activated += (_, _) =>
        {
            s_activationOrder.Remove(this);
            s_activationOrder.Insert(0, this);
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _dragActive)
            {
                EndTabDrag(cancelled: true);
                e.Handled = true;
            }
        };

        if (!OperatingSystem.IsWindows())
        {
            _clientPanel = new ClientTilePanel();
            HostContainer.Child = _clientPanel;

            // The keyboard follows clicks: a click in a tab's client gives it the focus (see MainWindow.AttachToTabHost),
            // a click on the host's own controls takes it back.
            AddHandler(PointerPressedEvent, (_, _) => X11Embed.Focus(_hostHwnd), RoutingStrategies.Tunnel, handledEventsToo: true);
            SizeChanged += (_, _) => ScheduleScreenPositionSync();
        }
    }

    private static void InitializeProcessCallbacksOnce()
    {
        if (s_processCallbacksInitialized)
            return;
        s_processCallbacksInitialized = true;

        if (OperatingSystem.IsWindows())
            return;

        HotKeys.ActiveClientPid = ResolveHotkeyClientPid;
        HotKeys.BroadcastToClients = (msg, wParam, lParam) => Dispatcher.UIThread.Post(() =>
        {
            foreach (TabInfo info in AllTabs)
                PostToChild(info, msg, wParam, lParam);
        });
    }

    /// <summary>Linux: the tab a hotkey is for: the one with the keyboard, else the selected tab of the window used last.</summary>
    private static int ResolveHotkeyClientPid()
    {
        IntPtr focus = X11Embed.GetFocus();
        foreach (TabInfo info in AllTabs)
        {
            if (info.ChildHwnd != IntPtr.Zero && X11Embed.IsInside(focus, info.ChildHwnd))
                return info.Pid;
        }

        TabbedHostWindow? window = ActiveWindow;
        if (window?.InstancesTabControl.SelectedItem is TabItem tab && window._tabs.TryGetValue(tab, out TabInfo? selected))
            return selected.Pid;
        return 0;
    }

    /// <summary>Another host window takes over what was this one's for the whole process.</summary>
    private void HandOverProcessRoles()
    {
        TabbedHostWindow? next = s_activationOrder.FirstOrDefault(w => w != this && !w._isClosing);
        if (next is null)
            return;

        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow == this)
            desktop.MainWindow = next;
        if (HotKeys.HostHwnd == _hostHwnd)
            HotKeys.HostHwnd = next._hostHwnd;

        // The Linux hotkeys are grabbed on the main window; grab them on the new one.
        Dispatcher.UIThread.Post(() =>
        {
            try { Ioc.Default.GetRequiredService<IHotKeyService>().Reload(); }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void ReleaseWindowSubscriptions()
    {
        _titleSyncTimer?.Stop();
        _screenSyncTimer?.Stop();
        if (_options is not null)
            _options.PropertyChanged -= Options_PropertyChanged;
        StrongReferenceMessenger.Default.UnregisterAll(this);
        s_activationOrder.Remove(this);
    }

    #endregion

    #region Messages to and from the clients

    /// <summary>Posts a WM_SKUA_* message to a tab's client: a window message on Windows, its pipe elsewhere.</summary>
    private static bool PostToChild(TabInfo info, int msg, IntPtr wParam = default, IntPtr lParam = default)
    {
        if (info.ChildHwnd == IntPtr.Zero)
            return false;
        if (OperatingSystem.IsWindows())
            return PostMessage(info.ChildHwnd, (uint)msg, wParam, lParam);

        TabChannel.Post(IntPtr.Zero, info.Pid, msg, wParam, lParam);
        return true;
    }

    /// <summary>Sends a WM_SKUA_* message to a tab's client and waits for its answer.</summary>
    private static IntPtr SendToChild(TabInfo info, int msg, IntPtr wParam = default, IntPtr lParam = default)
        => info.ChildHwnd == IntPtr.Zero ? IntPtr.Zero : TabChannel.Send(info.ChildHwnd, info.Pid, msg, wParam, lParam);

    /// <summary>Off Windows: a message a client wrote to the host's pipe (on Windows these come to HostWindowSubclassProc).</summary>
    private static void HandleClientMessage(bool relay, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (relay)
        {
            foreach (TabInfo info in AllTabs)
                PostToChild(info, msg, wParam, lParam);
            return;
        }

        if (msg == TabChannel.WM_SKUA_CHILD_READY)
        {
            int pid = (int)lParam;
            foreach (TabbedHostWindow window in s_windows.ToList())
            {
                TabInfo? target = window._tabs.Values.FirstOrDefault(t => t.Pid == pid);
                if (target is not null)
                {
                    if (!window._isClosing && wParam != IntPtr.Zero)
                        window.AttachChildWindow(target, wParam);
                    return;
                }
            }
        }
        else if (msg == HotKeys.WM_SKUA_RELOAD_HOTKEYS)
        {
            try { Ioc.Default.GetRequiredService<IHotKeyService>().Reload(); }
            catch { }
            foreach (TabInfo info in AllTabs)
                PostToChild(info, HotKeys.WM_SKUA_RELOAD_HOTKEYS);
        }
        else if (msg == HotKeys.WM_SKUA_LOAD_SCRIPT_ALL)
        {
            try
            {
                IWindowService windowService = Ioc.Default.GetRequiredService<IWindowService>();
                windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
                windowService.ShowManagedWindow("Script Repo");
            }
            catch { }
        }
    }

    #endregion

    #region Linux tab area

    private ClientTilePanel? _clientPanel;
    private DispatcherTimer? _screenSyncTimer;

    private void AddEmbeddedClient(TabInfo info)
    {
        if (_clientPanel is null || info.ChildHwnd == IntPtr.Zero)
            return;

        info.Host ??= new EmbeddedClientHost(info.ChildHwnd);
        if (info.Host.Parent is Panel previous && previous != _clientPanel)
            previous.Children.Remove(info.Host);
        if (!_clientPanel.Children.Contains(info.Host))
            _clientPanel.Children.Add(info.Host);
    }

    private static void RemoveEmbeddedClient(TabInfo info)
    {
        if (info.Host?.Parent is Panel panel)
            panel.Children.Remove(info.Host);
    }

    /// <summary>
    /// Linux counterpart of the Windows placement in DoReposition: shows the selected tab's client (or all of them
    /// in grid view) and throttles the hidden ones the same way.
    /// </summary>
    private void LayoutEmbeddedClients()
    {
        if (_clientPanel is null)
            return;

        TabItem? selectedTab = InstancesTabControl.SelectedItem as TabItem;
        if (!_isGridViewEnabled && selectedTab is null)
        {
            selectedTab = (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
                ? _lastSelectedTab
                : InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
            if (selectedTab is not null)
            {
                _ignoreSelectionChange = true;
                InstancesTabControl.SelectedItem = selectedTab;
                _ignoreSelectionChange = false;
            }
        }

        List<TabInfo> ordered = InstancesTabControl.Items.OfType<TabItem>()
            .Where(_tabs.ContainsKey).Select(t => _tabs[t]).ToList();

        bool isLoading = _isGridViewEnabled
            ? ordered.Any(i => i.ChildHwnd == IntPtr.Zero)
            : selectedTab is not null && _tabs.TryGetValue(selectedTab, out TabInfo? selectedInfo) && selectedInfo.ChildHwnd == IntPtr.Zero;
        if (LoadingIndicator != null)
            LoadingIndicator.IsVisible = isLoading;

        if (WindowState == WindowState.Minimized || !IsVisible)
            return;

        foreach (TabInfo info in ordered)
        {
            if (info.Host is null)
                continue;

            bool shown = _isGridViewEnabled || info.Tab == selectedTab;
            info.Host.IsVisible = shown;
            if (shown && info.IsThrottled)
            {
                info.IsThrottled = false;
                PostToChild(info, WM_SKUA_THROTTLE, new IntPtr(0), IntPtr.Zero);
            }
            else if (!shown && !info.IsThrottled)
            {
                info.IsThrottled = true;
                PostToChild(info, WM_SKUA_THROTTLE, new IntPtr(1), IntPtr.Zero);
            }
        }

        _clientPanel.IsGrid = _isGridViewEnabled;
        _clientPanel.Order = ordered.Where(i => i.Host is not null).Select(i => (Control)i.Host!).ToList();

        TabInfo? active = selectedTab is not null && _tabs.TryGetValue(selectedTab, out TabInfo? sel) ? sel : ordered.FirstOrDefault();
        if (active is not null && active.ChildHwnd != IntPtr.Zero)
            HotKeys.ActiveChildHwnd = active.ChildHwnd;

        ScheduleScreenPositionSync();
    }

    /// <summary>Linux: tells the shown clients where they are on the screen once the window has stopped moving.</summary>
    private void ScheduleScreenPositionSync()
    {
        if (OperatingSystem.IsWindows() || _isClosing)
            return;

        if (_screenSyncTimer is null)
        {
            _screenSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _screenSyncTimer.Tick += (_, _) =>
            {
                _screenSyncTimer.Stop();
                foreach (TabInfo info in _tabs.Values)
                {
                    if (info.Host is { IsVisible: true } && info.ChildHwnd != IntPtr.Zero)
                        X11Embed.SendScreenPosition(info.ChildHwnd);
                }
            };
        }
        _screenSyncTimer.Stop();
        _screenSyncTimer.Start();
    }

    #endregion

    #region Moving tabs between windows

    /// <summary>
    /// Moves a tab to <paramref name="target"/> at <paramref name="index"/> (a reorder when it is already there).
    /// The client keeps running; a window left without tabs closes.
    /// </summary>
    private static void MoveTab(TabInfo info, TabbedHostWindow target, int index)
    {
        TabbedHostWindow source = info.Owner;
        bool stillThere = source._tabs.TryGetValue(info.Tab, out TabInfo? current) && current == info;
        if (!stillThere || target._isClosing || info.ChildHwnd == IntPtr.Zero)
        {
            // The tab closed (or its window did) on the way: an empty window opened for it goes too.
            if (target._tabs.Count == 0 && target != source && !target._isClosing)
                target.Close();
            return;
        }

        if (source == target)
        {
            source.ReorderTab(info.Tab, index);
            return;
        }

        string title = info.TitleBlock?.Text ?? "User";
        bool isCustomNamed = info.IsCustomNamed;
        source.DetachTab(info);
        target.AdoptTab(info, title, isCustomNamed, index);

        if (source._tabs.Count == 0 && !source._isClosing)
            source.Close();
        target.Activate();
    }

    /// <summary>
    /// Opens a window for the tab, with its top left near <paramref name="screenPoint"/> (the drop point of a drag),
    /// or beside this window from the tab menu.
    /// </summary>
    private void MoveTabToNewWindow(TabInfo info, PixelPoint? screenPoint)
    {
        if (info.ChildHwnd == IntPtr.Zero || !_tabs.ContainsKey(info.Tab) || _tabs.Count <= 1)
            return;

        double scaling = RenderScaling;
        PixelPoint position = screenPoint is { } point
            ? new PixelPoint(point.X - (int)(80 * scaling), point.Y - (int)(46 * scaling))
            : new PixelPoint(Position.X + (int)(40 * scaling), Position.Y + (int)(40 * scaling));

        TabbedHostWindow window = new(Array.Empty<string>(), startEmpty: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Bounds.Width > 100 ? Bounds.Width : Width,
            Height = Bounds.Height > 100 ? Bounds.Height : Height,
            Position = position,
            _pendingAdoption = info
        };
        window.Show();
    }

    private void ReorderTab(TabItem tab, int index)
    {
        int from = InstancesTabControl.Items.IndexOf(tab);
        if (from < 0)
            return;
        index = Math.Clamp(index, 0, InstancesTabControl.Items.Count - 1);
        if (index == from)
            return;

        _ignoreSelectionChange = true;
        InstancesTabControl.Items.RemoveAt(from);
        InstancesTabControl.Items.Insert(index, tab);
        _ignoreSelectionChange = false;
        if (!_isGridViewEnabled)
            InstancesTabControl.SelectedItem = tab;
        DoReposition();
        ScrollTabIntoView(tab);
    }

    /// <summary>Takes the tab out of this window without closing its client.</summary>
    private void DetachTab(TabInfo info)
    {
        TabItem tab = info.Tab;
        int index = InstancesTabControl.Items.IndexOf(tab);
        bool wasSelected = InstancesTabControl.SelectedItem == tab;

        _tabs.Remove(tab);
        if (_lastSelectedTab == tab)
            _lastSelectedTab = null;
        RemoveEmbeddedClient(info);

        _ignoreSelectionChange = true;
        InstancesTabControl.Items.Remove(tab);
        _ignoreSelectionChange = false;

        // Windows: what the fast move during a window drag reuses may still name the client that left.
        _gridTiles.Clear();
        _lastActiveHwnd = IntPtr.Zero;
        _lastX = _lastY = _lastW = _lastH = int.MinValue;

        if (_tabs.Count > 0)
        {
            if (_isGridViewEnabled && _tabs.Count <= 1)
            {
                ExitGridView();
            }
            else if (!_isGridViewEnabled && (wasSelected || InstancesTabControl.SelectedItem is null))
            {
                List<TabItem> remaining = InstancesTabControl.Items.OfType<TabItem>().ToList();
                if (remaining.Count > 0)
                    InstancesTabControl.SelectedItem = remaining[Math.Clamp(index, 0, remaining.Count - 1)];
            }
        }
        UpdateTabScrollButtons();
        DoReposition();
    }

    /// <summary>Gives this window a tab whose client is already running (from another window).</summary>
    private void AdoptTab(TabInfo info, string title, bool isCustomNamed, int index)
    {
        TabItem tab = CreateTabItem(info, title);
        info.IsCustomNamed = isCustomNamed;
        _tabs[tab] = info;
        InstancesTabControl.Items.Insert(Math.Clamp(index, 0, InstancesTabControl.Items.Count), tab);

        if (OperatingSystem.IsWindows())
        {
            // The client's window is owned by the host window it sits on, and posts its DPI changes there.
            if (_hostHwnd != IntPtr.Zero)
                SetWindowLongAny(info.ChildHwnd, GWL_HWNDPARENT, _hostHwnd);
            PostToChild(info, TabChannel.WM_SKUA_SET_HOST, _hostHwnd, IntPtr.Zero);
            _lastX = _lastY = _lastW = _lastH = int.MinValue;
            _lastActiveHwnd = IntPtr.Zero;
        }
        else
        {
            AddEmbeddedClient(info);
        }

        PostToChild(info, WM_SKUA_GRIDVIEW, new IntPtr(_isGridViewEnabled ? 1 : 0), IntPtr.Zero);
        _lastSelectedTab = tab;
        if (!_isGridViewEnabled)
            InstancesTabControl.SelectedItem = tab;
        DoReposition();
        UpdateTabScrollButtons();
        ScrollTabIntoView(tab);
    }

    /// <summary>The tab menu's "Move to New Window" and "Move to Window" entries.</summary>
    private void PopulateMoveTabMenu(TabInfo info, MenuItem newWindowItem, MenuItem otherWindowItem)
    {
        bool ready = info.ChildHwnd != IntPtr.Zero;
        newWindowItem.IsEnabled = ready && _tabs.Count > 1;

        otherWindowItem.Items.Clear();
        List<TabbedHostWindow> others = s_activationOrder.Where(w => w != this && !w._isClosing).ToList();
        otherWindowItem.IsVisible = others.Count > 0;
        otherWindowItem.IsEnabled = ready;
        foreach (TabbedHostWindow window in others)
        {
            List<string> names = window.InstancesTabControl.Items.OfType<TabItem>()
                .Select(t => window._tabs.TryGetValue(t, out TabInfo? i) ? i.TitleBlock?.Text : null)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            string header = names.Count == 0 ? "Empty window"
                : string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" +{names.Count - 3}" : "");
            MenuItem item = new() { Header = header };
            TabbedHostWindow target = window;
            item.Click += (_, _) => MoveTab(info, target, target.InstancesTabControl.Items.Count);
            otherWindowItem.Items.Add(item);
        }
    }

    #endregion

    #region Dragging tabs

    private const double TabDragThreshold = 6;

    private TabInfo? _dragInfo;
    private Control? _dragSource;
    private Point _dragStart;
    private bool _dragActive;
    private TabbedHostWindow? _dropWindow;
    private Popup? _dragGhost;
    private TextBlock? _dragGhostText;
    private Canvas? _dropMarkerLayer;
    private Border? _dropMarker;

    private void BeginTabDragCandidate(TabInfo info, Control source, PointerPressedEventArgs e)
    {
        _dragInfo = info;
        _dragSource = source;
        _dragStart = e.GetPosition(this);
        _dragActive = false;
        e.Pointer.Capture(source);
    }

    private void OnTabDragMoved(TabInfo info, Control source, PointerEventArgs e)
    {
        if (_dragInfo != info || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
            return;

        Point position = e.GetPosition(this);
        if (!_dragActive)
        {
            if (Math.Abs(position.X - _dragStart.X) < TabDragThreshold && Math.Abs(position.Y - _dragStart.Y) < TabDragThreshold)
                return;
            _dragActive = true;
            source.Cursor = new Cursor(StandardCursorType.DragMove);
        }

        UpdateDragFeedback(this.PointToScreen(position));
    }

    private void OnTabDragReleased(TabInfo info, PointerReleasedEventArgs e)
    {
        if (_dragInfo != info || e.InitialPressMouseButton != MouseButton.Left)
            return;

        bool wasDragging = _dragActive;
        PixelPoint screen = this.PointToScreen(e.GetPosition(this));
        Point grab = _dragStart;
        TabbedHostWindow? dropWindow = wasDragging ? FindDropWindow(screen, info) : null;
        int dropIndex = dropWindow?.GetDropIndex(screen, info) ?? -1;
        EndTabDrag(cancelled: false);
        if (!wasDragging)
            return;

        if (dropWindow is not null)
        {
            MoveTab(info, dropWindow, dropIndex);
        }
        else if (info.ChildHwnd != IntPtr.Zero)
        {
            if (_tabs.Count > 1)
            {
                MoveTabToNewWindow(info, screen);
            }
            else
            {
                // A lone tab dropped outside every tab row takes its window along, like a browser.
                Position = new PixelPoint(screen.X - (int)(grab.X * RenderScaling), screen.Y - (int)(grab.Y * RenderScaling));
            }
        }
    }

    private void OnTabDragCaptureLost(TabInfo info)
    {
        if (_dragInfo == info)
            EndTabDrag(cancelled: true);
    }

    private void EndTabDrag(bool cancelled)
    {
        if (_dragSource is not null)
            _dragSource.Cursor = null;
        _dragSource = null;
        _dragInfo = null;
        _dragActive = false;
        HideDragGhost();
        SetDropWindow(null, null, -1);
    }

    private void UpdateDragFeedback(PixelPoint screen)
    {
        if (_dragInfo is null)
            return;

        TabbedHostWindow? window = FindDropWindow(screen, _dragInfo);
        if (window is not null)
        {
            SetDropWindow(window, _dragInfo, window.GetDropIndex(screen, _dragInfo));
            HideDragGhost();
        }
        else
        {
            SetDropWindow(null, null, -1);
            // A tab whose client is still starting can only be reordered.
            if (_dragInfo.ChildHwnd != IntPtr.Zero)
                ShowDragGhost(screen, _tabs.Count > 1 ? "New window" : "Move window");
            else
                HideDragGhost();
        }
    }

    /// <summary>The host window whose tab row is under <paramref name="screen"/>: this one first, then the most recently used.</summary>
    private TabbedHostWindow? FindDropWindow(PixelPoint screen, TabInfo dragged)
    {
        if (TabStripScreenRect().Contains(screen))
            return this;
        if (dragged.ChildHwnd == IntPtr.Zero)
            return null;

        foreach (TabbedHostWindow window in s_activationOrder)
        {
            if (window == this || window._isClosing || !window.IsVisible || window.WindowState == WindowState.Minimized)
                continue;
            if (window.TabStripScreenRect().Contains(screen))
                return window;
        }
        return null;
    }

    /// <summary>The tab row on the screen, with some room above and below so a drop doesn't need a steady hand.</summary>
    private PixelRect TabStripScreenRect()
    {
        Point? rowTop = TabScrollViewer.TranslatePoint(new Point(0, 0), this);
        if (rowTop is null || Bounds.Width <= 0)
            return default;

        double top = Math.Max(0, rowTop.Value.Y - 12);
        double bottom = rowTop.Value.Y + TabScrollViewer.Bounds.Height + 12;
        PixelPoint topLeft = this.PointToScreen(new Point(0, top));
        PixelPoint bottomRight = this.PointToScreen(new Point(Bounds.Width, bottom));
        return new PixelRect(topLeft, bottomRight);
    }

    /// <summary>Where a tab dropped at <paramref name="screen"/> goes among this window's other tabs.</summary>
    private int GetDropIndex(PixelPoint screen, TabInfo dragged)
    {
        int index = 0;
        foreach (TabItem tab in InstancesTabControl.Items.OfType<TabItem>())
        {
            if (tab == dragged.Tab)
                continue;
            Point? center = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 0), this);
            if (center is not null && this.PointToScreen(center.Value).X < screen.X)
                index++;
        }
        return index;
    }

    private void SetDropWindow(TabbedHostWindow? window, TabInfo? dragged, int index)
    {
        if (_dropWindow is not null && _dropWindow != window)
            _dropWindow.ShowDropMarker(null, -1);
        _dropWindow = window;
        window?.ShowDropMarker(dragged, index);
    }

    /// <summary>A bar in the tab row where the dragged tab would go.</summary>
    private void ShowDropMarker(TabInfo? dragged, int index)
    {
        if (dragged is null || index < 0)
        {
            if (_dropMarkerLayer is not null)
                _dropMarkerLayer.IsVisible = false;
            return;
        }

        if (_dropMarkerLayer is null)
        {
            AdornerLayer? layer = AdornerLayer.GetAdornerLayer(TabScrollViewer);
            if (layer is null)
                return;
            _dropMarker = new Border { Width = 3, Height = 24, CornerRadius = new CornerRadius(1.5), IsHitTestVisible = false };
            _dropMarker[!Border.BackgroundProperty] = new DynamicResource("SkuaAccentBrush");
            _dropMarkerLayer = new Canvas { IsHitTestVisible = false };
            _dropMarkerLayer.Children.Add(_dropMarker);
            AdornerLayer.SetAdornedElement(_dropMarkerLayer, TabScrollViewer);
            layer.Children.Add(_dropMarkerLayer);
        }

        List<TabItem> others = InstancesTabControl.Items.OfType<TabItem>().Where(t => t != dragged.Tab).ToList();
        double x = 4;
        if (index < others.Count)
        {
            x = others[index].TranslatePoint(new Point(0, 0), TabScrollViewer)?.X ?? 4;
        }
        else if (others.Count > 0)
        {
            TabItem last = others[^1];
            x = (last.TranslatePoint(new Point(last.Bounds.Width, 0), TabScrollViewer)?.X ?? 4) + 2;
        }

        Canvas.SetLeft(_dropMarker!, Math.Max(0, x - 2));
        Canvas.SetTop(_dropMarker!, 5);
        _dropMarkerLayer.IsVisible = true;
    }

    /// <summary>A chip with the tab's name that follows the pointer outside every tab row.</summary>
    private void ShowDragGhost(PixelPoint screen, string action)
    {
        if (_dragGhost is null)
        {
            _dragGhostText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _dragGhostText[!TextBlock.ForegroundProperty] = new DynamicResource("SkuaTextBrush");
            MaterialIcon icon = new() { Kind = MaterialIconKind.OpenInNew, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
            icon[!MaterialIcon.ForegroundProperty] = new DynamicResource("SkuaAccentBrush");
            Border chip = new()
            {
                Padding = new Thickness(10, 5),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Opacity = 0.94,
                IsHitTestVisible = false,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, _dragGhostText } }
            };
            chip[!Border.BackgroundProperty] = new DynamicResource("SkuaSurfaceBrush");
            chip[!Border.BorderBrushProperty] = new DynamicResource("SkuaAccentBrush");

            _dragGhost = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.AnchorAndGravity,
                PlacementAnchor = PopupAnchor.TopLeft,
                PlacementGravity = PopupGravity.BottomRight,
                PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None,
                IsLightDismissEnabled = false,
                IsHitTestVisible = false,
                Child = chip
            };
            if (Content is Panel root)
                root.Children.Add(_dragGhost);
        }

        string title = _dragInfo?.TitleBlock?.Text ?? "Tab";
        _dragGhostText!.Text = $"{action}: {title}";
        Point local = this.PointToClient(screen);
        _dragGhost.PlacementRect = new Rect(local.X + 14, local.Y + 12, 1, 1);
        if (!_dragGhost.IsOpen)
            _dragGhost.IsOpen = true;
    }

    private void HideDragGhost()
    {
        if (_dragGhost is { IsOpen: true })
            _dragGhost.IsOpen = false;
    }

    #endregion
}
