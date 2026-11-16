using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DDLootFilter;

public static class App
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--diagnose"))
        {
            // Writes every drop in the current level, its decoded stats and whether the saved filter matches it.
            try
            {
                using var reader = new GameReader(Process.GetProcessesByName("DunDefGame").Single());
                var watch = Stopwatch.StartNew();
                reader.Refresh();
                double refreshMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var scan = reader.ScanDrops();
                double firstScanMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                for (int i = 0; i < 10; i++) scan = reader.ScanDrops();
                double scanMs = watch.Elapsed.TotalMilliseconds / 10;
                // --filter <path> evaluates a specific filter file instead of the saved one.
                int filterArg = Array.IndexOf(args, "--filter");
                var filter = (filterArg >= 0 && filterArg + 1 < args.Length
                    ? JsonSerializer.Deserialize<FilterConfig>(File.ReadAllText(args[filterArg + 1]))!
                    : FilterConfig.Load()).CompileAll();
                if (args.Contains("--render"))
                {
                    // Draws every drop (filter ignored) over the window size so dot placement can be checked against a capture.
                    Native.GetClientRect(reader.Window, out var rect);
                    int width = Math.Max(1, rect.Right), height = Math.Max(1, rect.Bottom);
                    var view = new DotVisual();
                    view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height));
                    view.Paint(reader.Views.Select(v => (reader.ReadCamera(v), (IEnumerable<Drop>)scan.Drops,
                        new Rect(v.X * width, v.Y * height, v.Width * width, v.Height * height))).ToArray());
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create("drops-overlay.png"); png.Save(output);
                }
                File.WriteAllText("drops.json", JsonSerializer.Serialize(new
                {
                    RefreshMilliseconds = refreshMs, FirstScanMilliseconds = firstScanMs, ScanMilliseconds = scanMs,
                    Players = reader.Views.Select((v, i) => new
                    {
                        v.Number, Controller = v.Controller.ToString("x"), Viewport = new { v.X, v.Y, v.Width, v.Height },
                        Camera = reader.ReadCamera(v),
                        scan.Players[i].Character,
                        Kit = scan.Players[i].Kit is { } kit ? new { kit.WeaponType, WeaponTypes = kit.WeaponTypes.Select(t => (int)t), Accessories = kit.Accessories.Select(a => (int)a) } : null,
                        Equipped = scan.Players[i].Equipped?.ToDictionary(kv => Catalog.Types.FirstOrDefault(t => t.EquipmentType == kv.Key)?.Label ?? $"Type {kv.Key}", kv => kv.Value),
                    }),
                    Drops = scan.Drops.Select(d => new
                    {
                        Ptr = d.Ptr.ToString("x"), d.Name, d.Type, d.Tier, d.TierName,
                        Slot = Catalog.Types.FirstOrDefault(t => t.EquipmentType == d.Type)?.Label ?? "Other",
                        // Whether each player (in view order) would see a dot on it.
                        Matches = scan.Players.Select(p => filter.For(p.Character).Matches(d, p)).ToArray(),
                        CanEquip = scan.Players.Select(p => p.Kit?.CanEquip(d.Requirement)).ToArray(),
                        Requirement = new { d.Requirement.CanBeEquipped, d.Requirement.WeaponType, Accessories = d.Requirement.Accessories.Select(a => (int)a) },
                        d.Location, d.Stats
                    })
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { File.WriteAllText("drops.json", e.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--render-ui"))
        {
            // Renders the filter window's content off-screen to filter-ui.png without showing it.
            int filterArg = Array.IndexOf(args, "--filter");
            var config = filterArg >= 0 && filterArg + 1 < args.Length
                ? JsonSerializer.Deserialize<FilterConfig>(File.ReadAllText(args[filterArg + 1]))!
                : FilterConfig.Load();
            if (args.Contains("--advanced")) config.Advanced = true; // render the Advanced view instead of Quick
            var window = new FilterWindow(config, _ => { }, () => { });
            int sizeArg = Array.IndexOf(args, "--size"); // --size WxH renders at another window size
            if (sizeArg >= 0 && sizeArg + 1 < args.Length && args[sizeArg + 1].Split('x') is [var w, var h]) { window.Width = double.Parse(w); window.Height = double.Parse(h); }
            // --playing pretends the first saved character is in the game, which switches the open profile and rebuilds both views.
            if (args.Contains("--playing") && config.Characters.FirstOrDefault() is { Value: { } saved } entry)
                window.SetPlayers([new PlayerState(new Character(entry.Key, saved.Name ?? "Hero", saved.Class ?? "", saved.Level ?? 0), null)]);
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var host = new System.Windows.Controls.Border { Background = window.Background, Child = content, Width = window.Width, Height = window.Height };
            TextElement.SetFontFamily(host, window.FontFamily); TextElement.SetFontSize(host, window.FontSize); TextElement.SetForeground(host, window.Foreground);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            host.Measure(new Size(window.Width, window.Height)); host.Arrange(new Rect(0, 0, window.Width, window.Height)); host.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create("filter-ui.png"); png.Save(output);
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new Overlay());
    }
}
