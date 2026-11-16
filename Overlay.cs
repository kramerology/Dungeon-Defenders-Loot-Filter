using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DDLootFilter;

/// <summary>Transparent, click-through, topmost window over the game's client area that draws a dot on each matching drop.</summary>
internal sealed class Overlay : Window
{
    readonly DotVisual visual = new();
    readonly CancellationTokenSource shutdown = new();
    readonly FilterWindow filterWindow;
    /// <summary>The reader and its latest scan, swapped together so their player lists always line up.</summary>
    sealed record Live(GameReader Reader, Scan Scan);
    volatile Live? live;
    volatile FilterSet filters;
    string status = "Waiting for Dungeon Defenders…";
    nint hwnd;
    bool closing;
    (int X, int Y, int Width, int Height)? lastBounds;
    TimeSpan lastRenderingTime = TimeSpan.MinValue;
    long lastStatusTick;

    public Overlay()
    {
        Title = "DD Loot Filter Overlay";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Left = Top = 0; Width = Height = 1; Content = visual; IsHitTestVisible = false;
        var config = FilterConfig.Load();
        filters = config.CompileAll();
        filterWindow = new FilterWindow(config, compiled => filters = compiled, () => Close());
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80000 | 0x20 | 0x80 | 0x08000000);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        };
        Loaded += (_, _) => { _ = Task.Run(Monitor); filterWindow.Show(); };
        Closed += (_, _) =>
        {
            closing = true; shutdown.Cancel(); CompositionTarget.Rendering -= OnRendering;
            filterWindow.ForceClose(); live?.Reader.Dispose(); live = null;
        };
        CompositionTarget.Rendering += OnRendering;
    }

    nint Hook(nint handle, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x84) { handled = true; return -1; } // WM_NCHITTEST → HTTRANSPARENT
        return 0;
    }

    async Task Monitor()
    {
        GameReader? current = null;
        var sinceRefresh = Stopwatch.StartNew();
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    // A level change, or a periodic re-validation of the object table, rebuilds the reader.
                    if (current == null || current.Exited || !current.SameLevel() || sinceRefresh.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        var games = Process.GetProcessesByName("DunDefGame");
                        if (games.Length > 1) { foreach (var p in games) p.Dispose(); throw new IOException("Close the extra Dungeon Defenders instance."); }
                        if (games.Length == 0) throw new IOException("Waiting for Dungeon Defenders. Run it in windowed or borderless mode.");
                        var candidate = new GameReader(games[0]);
                        try { candidate.Refresh(); }
                        catch { candidate.Dispose(); throw; }
                        var previous = current; current = candidate; sinceRefresh.Restart();
                        // The UI thread may still be using the previous reader for this frame.
                        if (previous != null) _ = Task.Delay(1000).ContinueWith(_ => previous.Dispose());
                    }
                    var scan = current.ScanDrops();
                    live = new(current, scan);
                    status = "Connected";
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    live = null; status = e.Message; Log(e);
                    var dead = current; current = null;
                    if (dead != null) _ = Task.Delay(1000).ContinueWith(_ => dead.Dispose());
                    await Task.Delay(2000, shutdown.Token);
                }
                await Task.Delay(200, shutdown.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = args.RenderingTime;
        }
        DrawFrame();
    }

    void DrawFrame()
    {
        if (closing) return;
        var current = live; var currentFilters = filters;
        if (Environment.TickCount64 - lastStatusTick > 250)
        {
            lastStatusTick = Environment.TickCount64;
            // Each split-screen player is judged by their own character's profile and gear.
            var counts = current?.Scan.Players.Select(p => current.Scan.Drops.Count(d => currentFilters.For(p.Character).Matches(d, p))).ToArray() ?? [];
            filterWindow.SetStatus(new OverlayStatus(current != null, status, counts));
            filterWindow.SetPlayers(current?.Scan.Players ?? []);
        }
        try
        {
            var game = current?.Reader;
            if (game == null || game.Exited || Native.GetForegroundWindow() != game.Window || Native.IsIconic(game.Window))
            { visual.Clear(); return; }
            if (!Native.GetClientRect(game.Window, out var r) || r.Right <= 0 || r.Bottom <= 0) { visual.Clear(); return; }
            var origin = new Native.Point(); Native.ClientToScreen(game.Window, ref origin);
            var bounds = (origin.X, origin.Y, r.Right, r.Bottom);
            if (lastBounds != bounds)
            {
                // HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW
                Native.SetWindowPos(hwnd, -1, origin.X, origin.Y, r.Right, r.Bottom, 0x0010 | 0x0040);
                lastBounds = bounds;
            }
            var dpi = VisualTreeHelper.GetDpi(this);
            double width = r.Right / dpi.DpiScaleX, height = r.Bottom / dpi.DpiScaleY;
            var scan = current!.Scan;
            var views = new List<(Camera, IEnumerable<Drop>, Rect)>();
            for (int i = 0; i < game.Views.Count && i < scan.Players.Length; i++)
            {
                var view = game.Views[i]; var player = scan.Players[i]; var playerFilter = currentFilters.For(player.Character);
                if (!playerFilter.ShowDots) continue;
                var area = new Rect(view.X * width, view.Y * height, view.Width * width, view.Height * height);
                views.Add((game.ReadCamera(view), scan.Drops.Where(d => playerFilter.Matches(d, player)), area));
            }
            visual.Paint(views);
        }
        catch (Exception ex) { visual.Clear(); Log(ex); }
    }

    static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLootFilter", "overlay.log");
    static string? lastError;
    internal static void Log(Exception e)
    {
        if (lastError == e.Message) return; lastError = e.Message;
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"{DateTimeOffset.Now:u} {e}\n"); } catch { }
    }
}

internal sealed class DotVisual : FrameworkElement
{
    readonly VisualCollection children;
    readonly DrawingVisual surface = new();
    bool hasContent;
    static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(40, 255, 90)));
    static readonly Pen Outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(220, 0, 40, 10))), 2));
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public DotVisual() { children = new(this) { surface }; }
    protected override int VisualChildrenCount => children.Count;
    protected override Visual GetVisualChild(int index) => children[index];

    public void Clear()
    {
        if (!hasContent) return;
        using var dc = surface.RenderOpen(); hasContent = false;
    }

    /// <summary>Draws each player's matches with their camera, clipped to their part of the window.</summary>
    public void Paint(IEnumerable<(Camera Camera, IEnumerable<Drop> Matches, Rect Area)> views)
    {
        using var dc = surface.RenderOpen();
        hasContent = true;
        foreach (var (camera, matches, area) in views)
        {
            dc.PushClip(new RectangleGeometry(area));
            foreach (var drop in matches)
            {
                // Lift the dot slightly so it sits on the item rather than in the floor.
                if (!camera.Project(drop.Location + new V3(0, 0, 25), area.Width, area.Height, out var screen, out double depth)) continue;
                if (screen.X < -20 || screen.Y < -20 || screen.X > area.Width + 20 || screen.Y > area.Height + 20) continue;
                double radius = Math.Clamp(9000 / depth, 5, 11);
                dc.DrawEllipse(Fill, Outline, new Point(area.X + screen.X, area.Y + screen.Y), radius, radius);
            }
            dc.Pop();
        }
    }
}
