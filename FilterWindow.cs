using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace DDLootFilter;

/// <summary>What the overlay knows right now, shown in the window's status bar.</summary>
internal sealed record OverlayStatus(bool Connected, string Message, int[] Matches);

/// <summary>Loads the shared resource dictionary (colours, fonts, control styles) into the application once.</summary>
internal static class Theme
{
    static bool loaded;
    public static void Ensure()
    {
        if (loaded) return;
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DDLootFilter;component/Theme.xaml", UriKind.Relative) });
        loaded = true;
    }
}

/// <summary>
/// The filter editor. Sidebar: one entry per character profile. Main pane: the open profile in Quick or
/// Advanced mode. Status bar: game connection, match counts and dot state. Each change is compiled and
/// saved immediately; the open profile and window placement are saved too and restored at the next launch.
/// </summary>
internal sealed class FilterWindow : Window
{
    readonly FilterConfig config;
    /// <summary>The profile being edited: Default or one character's.</summary>
    Profile profile;
    readonly Action<FilterSet> onChanged;
    readonly Action quit;
    /// <summary>Debounces saves from window moves and resizes, which fire continuously while dragging.</summary>
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool savePending;
    PlayerState[] players = [];
    string lineup = "", profilesShown = "";
    Gear? shownEquipped;
    readonly List<(int Type, Func<string[]> Ids, TextBlock Text)> quickCells = [], advancedCells = [];
    /// <summary>The class toggles on the Quick and Advanced views. Both edit the same profile setting, so they stay in sync.</summary>
    readonly List<CheckBox> classOnlyBoxes = [];
    bool forceClose;

    // Chrome
    readonly Grid root = new();
    Button maximizeButton = null!;
    readonly StackPanel profileList = new();
    readonly TextBlock profileTitle, profileSubtitle;
    readonly Border profileHelp;
    RadioButton quickMode = null!, advancedMode = null!;
    readonly ContentControl body = new();
    UIElement quickView = new Grid(), advancedView = new Grid();
    readonly Dictionary<string, (Ellipse Dot, Border CountPill, TextBlock Count)> slotItems = [];
    readonly Dictionary<string, UIElement> slotEditors = [];
    string? openSlot;

    // Status bar
    readonly Ellipse statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly TextBlock matchText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    CheckBox showDotsBox = null!;
    readonly Border warningBox = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 9, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBlock warningText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    T Res<T>(string key) => (T)FindResource(key);
    Brush B(string name) => Res<Brush>("Brush." + name);
    TextBlock Text(string text, string style) => new() { Text = text, Style = Res<Style>("Text." + style) };
    static Thickness M(double l, double t, double r, double b) => new(l, t, r, b);

    /// <summary>A small circled "?" that shows an explanation on hover.</summary>
    Border Help(string tip)
    {
        var help = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), BorderThickness = new Thickness(1), BorderBrush = B("BorderStrong"),
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = M(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Help,
            Child = new TextBlock { Text = "?", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = B("Text3"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = tip,
        };
        ToolTipService.SetInitialShowDelay(help, 100);
        ToolTipService.SetShowDuration(help, 60000);
        return help;
    }

    /// <summary>An element followed by a "?" help icon.</summary>
    StackPanel WithHelp(UIElement element, Border help)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(element); line.Children.Add(help);
        return line;
    }
    StackPanel WithHelp(UIElement element, string tip) => WithHelp(element, Help(tip));

    public string? Warning
    {
        set { warningText.Text = value ?? ""; warningBox.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    public FilterWindow(FilterConfig config, Action<FilterSet> onChanged, Action quit)
    {
        Theme.Ensure();
        this.config = config; this.onChanged = onChanged; this.quit = quit;
        profile = config;
        Title = "DD Loot Filter";
        Width = 1180; Height = 800; MinWidth = 1060; MinHeight = 620;
        Background = B("Window"); Foreground = B("Text");
        FontFamily = Res<FontFamily>("Font.Body"); FontSize = 13;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RestorePlacement();
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 36, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0), NonClientFrameEdges = NonClientFrameEdges.None
        });
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1; Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
        StateChanged += (_, _) => { ApplyWindowState(); RememberPlacement(); };
        LocationChanged += (_, _) => RememberPlacement();
        SizeChanged += (_, _) => RememberPlacement();
        saveTimer.Tick += (_, _) => SaveNow();

        profileTitle = Text("", "Title");
        profileSubtitle = Text("", "Secondary"); profileSubtitle.Margin = M(0, 2, 0, 0);
        profileHelp = Help("The template every new character starts from. Characters already seen keep their own profile.");
        profileHelp.Margin = M(8, 4, 0, 0);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = BuildTitleBar();
        root.Children.Add(titleBar);

        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = BuildSidebar();
        bodyGrid.Children.Add(sidebar);
        var main = BuildMain();
        Grid.SetColumn(main, 1); bodyGrid.Children.Add(main);
        Grid.SetRow(bodyGrid, 1); root.Children.Add(bodyGrid);

        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);

        quickMode.Checked += (_, _) => { profile.Advanced = false; body.Content = quickView; Changed(); };
        advancedMode.Checked += (_, _) => { profile.Advanced = true; body.Content = advancedView; Changed(); };
        RebuildProfileList();
        ShowProfile(config.OpenProfile is { } openKey && config.Characters.TryGetValue(openKey, out var open) ? open : config);
        SetStatus(new OverlayStatus(false, "Waiting for Dungeon Defenders…", []));

        Content = root;
        ApplyWindowState();
        // Without the window there's no way back in, so closing it quits the program.
        Closing += (_, e) => { if (!forceClose) { e.Cancel = true; Dispatcher.BeginInvoke(quit); } };
    }

    // ───────────────────────────── Chrome ─────────────────────────────

    UIElement BuildTitleBar()
    {
        var bar = new Grid { Height = 36, Background = B("Sidebar") };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = M(14, 0, 0, 0) };
        var accent = ((SolidColorBrush)B("Accent")).Color;
        brand.Children.Add(new Ellipse
        {
            Width = 10, Height = 10, Fill = B("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = M(0, 0, 9, 0),
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 }
        });
        brand.Children.Add(new TextBlock { Text = "DD Loot Filter", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(brand);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button Caption(string glyph, string style, string tip, Action click)
        {
            var b = new Button { Content = glyph, Style = Res<Style>(style), ToolTip = tip };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        Caption("", "Button.Caption", "Minimize", () => WindowState = WindowState.Minimized);
        maximizeButton = Caption("", "Button.Caption", "Maximize", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Caption("", "Button.CaptionClose", "Quit", () => Close());
        Grid.SetColumn(buttons, 2); bar.Children.Add(buttons);
        return bar;
    }

    UIElement BuildSidebar()
    {
        var dock = new DockPanel { Background = B("Sidebar") };

        var top = new StackPanel { Margin = M(20, 16, 20, 6) };
        top.Children.Add(Text("ACTIVE", "Section"));
        DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);

        var bottom = new StackPanel { Margin = M(12, 8, 12, 12) };
        bottom.Children.Add(new Border { Style = Res<Style>("Divider"), Margin = M(0, 0, 0, 12) });
        var quitButton = new Button { Content = "Quit DD Loot Filter", Style = Res<Style>("Button.DangerSubtle"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Stops the overlay and closes the program." };
        quitButton.Click += (_, _) => quit();
        bottom.Children.Add(quitButton);
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);

        dock.Children.Add(new ScrollViewer { Content = profileList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = M(12, 0, 12, 0) });
        return dock;
    }

    UIElement BuildMain()
    {
        var grid = new Grid { Background = B("Window") };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: profile name and state, with the primary action on the right.
        var header = new Grid { Margin = M(32, 24, 28, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(profileTitle); titleLine.Children.Add(profileHelp);
        titleStack.Children.Add(titleLine); titleStack.Children.Add(profileSubtitle);
        header.Children.Add(titleStack);
        var back = new Button { Content = "Return to game", Style = Res<Style>("Button.Primary"), VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(18, 8, 18, 8), ToolTip = "Minimizes this window. Your changes are already saved and in effect." };
        back.Click += (_, _) => WindowState = WindowState.Minimized;
        Grid.SetColumn(back, 1); header.Children.Add(back);
        grid.Children.Add(header);

        // Mode bar: the selected mode is the one applied in game.
        var modeBar = new Grid { Margin = M(32, 20, 28, 0) };
        modeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var modeHost = new Border { Style = Res<Style>("SegmentHost") };
        var modeStack = new StackPanel { Orientation = Orientation.Horizontal };
        quickMode = Segment("Quick", "filterMode", "Pick DPS, Builder or Hybrid per gear group.");
        advancedMode = Segment("Advanced", "filterMode", "Set conditions stat by stat for each slot.");
        quickMode.MinWidth = advancedMode.MinWidth = 96;
        modeStack.Children.Add(quickMode); modeStack.Children.Add(advancedMode);
        modeHost.Child = modeStack;
        modeBar.Children.Add(modeHost);
        showDotsBox = Switch("Show dots in game", true, on =>
        {
            if (profile.ShowDots == on) return;
            profile.ShowDots = on;
            RebuildProfileList();
            Changed();
        }, "Draw dots on this profile's matching drops.");
        showDotsBox.VerticalAlignment = VerticalAlignment.Center; showDotsBox.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(showDotsBox, 1); modeBar.Children.Add(showDotsBox);
        var modeDivider = new Border { Style = Res<Style>("Divider"), Margin = M(32, 16, 28, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var modeArea = new StackPanel();
        modeArea.Children.Add(modeBar); modeArea.Children.Add(modeDivider);
        Grid.SetRow(modeArea, 1); grid.Children.Add(modeArea);

        Grid.SetRow(body, 2); grid.Children.Add(body);
        return grid;
    }

    UIElement BuildStatusBar()
    {
        var bar = new Border { Background = B("Sidebar"), BorderBrush = B("BorderSubtle"), BorderThickness = new Thickness(0, 1, 0, 0), Height = 32, Padding = M(16, 0, 12, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var connection = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        connection.Children.Add(statusDot); connection.Children.Add(statusText);
        grid.Children.Add(connection);

        matchText.Foreground = B("Text2"); matchText.Margin = M(14, 0, 0, 0);
        Grid.SetColumn(matchText, 1); grid.Children.Add(matchText);

        warningBox.Background = B("WarningSoft");
        var warningLine = new StackPanel { Orientation = Orientation.Horizontal };
        warningLine.Children.Add(new TextBlock { Text = "", Style = Res<Style>("Text.Icon"), Foreground = B("Warning"), Margin = M(0, 0, 7, 0) });
        warningText.Foreground = B("Warning");
        warningLine.Children.Add(warningText);
        warningBox.Child = warningLine;
        Grid.SetColumn(warningBox, 3); grid.Children.Add(warningBox);


        bar.Child = grid;
        return bar;
    }

    // ───────────────────────────── Shared controls ─────────────────────────────

    RadioButton Segment(string text, string group, string? tip = null, bool accent = false) => new()
    {
        Content = text, GroupName = group, ToolTip = tip, Style = Res<Style>(accent ? "SegmentAccent" : "Segment")
    };

    /// <summary>A segmented control. Returns the host and each option's button, keyed by id.</summary>
    (Border Host, Dictionary<string, RadioButton> Options) Segmented(string group, IEnumerable<(string Id, string Label, string? Tip)> options, bool accent = false)
    {
        var host = new Border { Style = Res<Style>("SegmentHost") };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new Dictionary<string, RadioButton>();
        foreach (var (id, label, tip) in options)
        {
            var button = Segment(label, group, tip, accent);
            buttons[id] = button; stack.Children.Add(button);
        }
        host.Child = stack;
        return (host, buttons);
    }

    CheckBox Switch(string text, bool on, Action<bool> set, string? tip = null)
    {
        var box = new CheckBox { Content = text, IsChecked = on, Style = Res<Style>("Switch"), ToolTip = tip };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    Border Card() => new() { Style = Res<Style>("Card") };

    /// <summary>A labelled field: small label above the control.</summary>
    StackPanel Field(string label, UIElement control, string? tip = null)
    {
        var stack = new StackPanel();
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = B("Text2") };
        FrameworkElement head = tip == null ? text : WithHelp(text, tip);
        head.Margin = M(0, 0, 0, 6);
        stack.Children.Add(head);
        stack.Children.Add(control);
        return stack;
    }

    /// <summary>Min/max tier pickers. Each list shows the tier's usual equip level; "Any" stores null.</summary>
    (UIElement Panel, ComboBox Min, ComboBox Max) TierRange(string? min, string? max, Action<string?> setMin, Action<string?> setMax)
    {
        const string tip = "Tier range always applies, whichever stat conditions are set. Levels shown are the usual level needed to equip that tier; a few familiars and special items use their own.";
        ComboBox TierBox(string? selected, string anyText, Action<string?> set)
        {
            var box = new ComboBox { Width = 164 };
            box.Items.Add(new ComboBoxItem { Content = anyText, Tag = null });
            foreach (var tier in Catalog.Tiers) box.Items.Add(new ComboBoxItem { Content = tier.Display, Tag = tier.Name });
            box.SelectedIndex = Math.Max(0, Catalog.TierRank(selected) + 1);
            box.SelectionChanged += (_, _) => set((box.SelectedItem as ComboBoxItem)?.Tag as string);
            return box;
        }
        var minBox = TierBox(min, "Any (lowest)", setMin);
        var maxBox = TierBox(max, "Any (highest)", setMax);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var from = Field("Minimum tier", minBox, tip); from.Margin = M(0, 0, 12, 0);
        row.Children.Add(from);
        row.Children.Add(Field("Maximum tier", maxBox));
        return (row, minBox, maxBox);
    }

    CheckBox ClassOnlyBox()
    {
        classOnlyBoxes.RemoveAll(b => !ReferenceEquals(b.Tag, profile));
        CheckBox box = null!;
        box = Switch("Only show drops my class can equip", profile.ClassOnly, on =>
        {
            if (profile.ClassOnly == on) return;
            profile.ClassOnly = on;
            foreach (var other in classOnlyBoxes) other.IsChecked = on;
            Changed();
        }, "Uses the game's own class check (weapon type and accessory requirements). Level requirements aren't checked; use the tier range for that.");
        box.Tag = profile;
        classOnlyBoxes.Add(box);
        return box;
    }

    // ───────────────────────────── Profiles ─────────────────────────────

    /// <summary>Switches the editor to a profile, rebuilding both views from its settings.</summary>
    void ShowProfile(Profile target)
    {
        profile = target;
        string? key = ReferenceEquals(target, config) ? null : config.Characters.FirstOrDefault(kv => ReferenceEquals(kv.Value, target)).Key;
        if (config.OpenProfile != key) { config.OpenProfile = key; SaveSoon(); }
        quickCells.Clear();
        quickView = BuildQuick(() =>
        {
            profile.CopyQuickToAdvanced();
            BuildAdvanced();
            advancedMode.IsChecked = true;
        });
        BuildAdvanced();
        // Setting IsChecked only raises Checked when it changes, so set the content directly too.
        bool advanced = profile.Advanced == true;
        if (advanced) advancedMode.IsChecked = true; else quickMode.IsChecked = true;
        body.Content = advanced ? advancedView : quickView;
        foreach (var item in profileList.Children.OfType<RadioButton>()) item.IsChecked = ReferenceEquals(item.Tag, profile);
        UpdateProfileHeader();
        showDotsBox.IsChecked = profile.ShowDots;
        shownEquipped = null;
        UpdateShownEquipped();
    }

    /// <summary>Which player slot the open profile's character is in, or -1.</summary>
    int PlayerIndexOf(Profile target) =>
        Array.FindIndex(players, p => p.Character != null && config.Characters.TryGetValue(p.Character.Key, out var pr) && ReferenceEquals(pr, target));

    void UpdateProfileHeader()
    {
        if (ReferenceEquals(profile, config))
        {
            profileTitle.Text = "Default";
            profileSubtitle.Text = "Template for new characters";
            profileHelp.Visibility = Visibility.Visible;
            return;
        }
        profileTitle.Text = profile.Name ?? "Character";
        profileSubtitle.Text = $"{profile.Class} · Level {profile.Level}";
        profileHelp.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// One entry per profile, in three sections: Active (characters in the game now, in player order), Saved
    /// (other characters, most recently played first) and Template (Default, which new characters start from).
    /// </summary>
    void RebuildProfileList()
    {
        var inGame = players.Select((p, i) => (p, i)).Where(x => x.p.Character != null)
            .Select(x => (Key: x.p.Character!.Key, Badge: players.Length > 1 ? $"P{x.i + 1}" : null)).ToList();
        var saved = config.Characters.Where(kv => inGame.All(g => g.Key != kv.Key))
            .OrderByDescending(kv => kv.Value.LastSeen ?? DateTimeOffset.MinValue).ThenBy(kv => kv.Value.Name).Select(kv => kv.Key).ToList();
        string signature = string.Join("|", inGame.Select(g => g.Key + g.Badge + config.Characters[g.Key].Label + config.Characters[g.Key].ShowDots).Concat(saved.Select(k => k + config.Characters[k].Label + config.Characters[k].ShowDots)).Append("D" + config.ShowDots));
        if (signature == profilesShown && profileList.Children.Count > 0) return;
        profilesShown = signature;
        profileList.Children.Clear();
        if (inGame.Count == 0)
        {
            var empty = Text("No character in game", "Caption");
            empty.Margin = M(8, 0, 8, 6);
            profileList.Children.Add(empty);
        }
        foreach (var (key, badge) in inGame)
        {
            var p = config.Characters[key];
            // Split-screen: say which player each character is, in the subtitle.
            string subtitle = $"{p.Class} · Level {p.Level}" + (badge != null ? $" · {badge}" : "");
            profileList.Children.Add(ProfileItem(p, p.Name ?? "Character", subtitle, false,
                "This character's own filter. It follows the character whichever player slot they're in."));
        }
        if (saved.Count > 0)
        {
            var label = Text("SAVED", "Section"); label.Margin = M(8, 18, 0, 6);
            profileList.Children.Add(label);
            foreach (string key in saved)
            {
                var p = config.Characters[key];
                profileList.Children.Add(ProfileItem(p, p.Name ?? "Character", $"{p.Class} · Level {p.Level}", true, "A saved character who isn't in the game right now. Most recently played first."));
            }
        }
        var defaultLabel = Text("TEMPLATE", "Section"); defaultLabel.Margin = M(8, 18, 0, 6);
        profileList.Children.Add(defaultLabel);
        profileList.Children.Add(ProfileItem(config, "Default", "Starting point for new characters", false, "Starting settings for characters the filter hasn't seen yet."));
    }

    RadioButton ProfileItem(Profile target, string name, string subtitle, bool dim, string tip)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse
        {
            Width = 8, Height = 8, Margin = M(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = target.ShowDots ? B("Accent") : B("BorderStrong"), ToolTip = target.ShowDots ? "Dots on" : "Dots off",
        };
        grid.Children.Add(dot);
        var lines = new StackPanel();
        Grid.SetColumn(lines, 1);
        lines.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = dim ? B("Text2") : B("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        lines.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = B("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = M(0, 1, 0, 0) });
        grid.Children.Add(lines);
        var item = new RadioButton { Style = Res<Style>("NavItem"), GroupName = "profile", Content = grid, Tag = target, ToolTip = tip, IsChecked = ReferenceEquals(target, profile) };
        item.Checked += (_, _) => { if (!ReferenceEquals(profile, target)) ShowProfile(target); };
        return item;
    }

    /// <summary>
    /// Called with the local players every status tick. Gives new characters a profile, keeps the list
    /// current, and when the line-up changes (a hero swap, a player joining or leaving) jumps to Player 1's
    /// character unless the profile being edited is still in the game.
    /// </summary>
    public void SetPlayers(PlayerState[] current)
    {
        players = current;
        bool created = false;
        foreach (var p in current) if (p.Character != null) created |= config.EnsureProfile(p.Character, out _);
        if (created) Changed();
        RebuildProfileList();
        string now = string.Join("|", current.Select(p => p.Character?.Key ?? "-"));
        if (now != lineup)
        {
            var stamp = DateTimeOffset.Now;
            foreach (string key in lineup.Split('|').Concat(now.Split('|')))
                if (config.Characters.TryGetValue(key, out var seen)) seen.LastSeen = stamp;
            if (lineup.Length > 0 || current.Length > 0) SaveSoon();
            lineup = now;
            var keys = current.Where(p => p.Character != null).Select(p => p.Character!.Key).ToList();
            bool editingSomeoneHere = keys.Any(k => ReferenceEquals(config.Characters[k], profile));
            if (!editingSomeoneHere && keys.Count > 0) ShowProfile(config.Characters[keys[0]]);
            else UpdateProfileHeader();
        }
        UpdateShownEquipped();
    }

    /// <summary>The Equipped values show the gear of the character whose profile is open, if they're in the game.</summary>
    void UpdateShownEquipped()
    {
        int index = PlayerIndexOf(profile);
        var gear = index >= 0 ? players[index].Equipped : null;
        if (gear != null && ReferenceEquals(gear, shownEquipped)) return;
        shownEquipped = gear;
        RefreshEquipped();
    }

    // ───────────────────────────── Quick mode ─────────────────────────────

    UIElement BuildQuick(Action customize)
    {
        var panel = new StackPanel { Margin = M(32, 20, 28, 28), MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left };

        // Settings that apply to every group.
        var settings = Card();
        var settingsGrid = new Grid();
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (tierPanel, _, _) = TierRange(profile.QuickMinTier, profile.QuickMaxTier,
            v => { profile.QuickMinTier = v; Changed(); }, v => { profile.QuickMaxTier = v; Changed(); });
        var tierStack = new StackPanel();
        tierStack.Children.Add(tierPanel);
        settingsGrid.Children.Add(tierStack);
        var classStack = new StackPanel { Margin = M(40, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var classLabel = WithHelp(new TextBlock { Text = "Class filter", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = B("Text2") },
            "Uses the game's own class check (weapon type and accessory requirements). Level requirements aren't checked; use the tier range for that.");
        classLabel.Margin = M(0, 0, 0, 8);
        classStack.Children.Add(classLabel);
        classStack.Children.Add(ClassOnlyBox());
        Grid.SetColumn(classStack, 1); settingsGrid.Children.Add(classStack);
        settings.Child = settingsGrid;
        panel.Children.Add(settings);

        // Section heading with the "set all" shortcut.
        var sectionRow = new Grid { Margin = M(2, 26, 0, 10) };
        sectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sectionLabel = WithHelp(Text("GEAR GROUPS", "Section"),
            "A drop gets a dot when the chosen role's stats add up to more than the item your hero has equipped in the same slot.\n\n"
            + "Hero Speed isn't counted because it caps quickly. Weapons also count their tooltip damage for DPS and Hybrid. Armor set bonuses and future upgrades aren't included.");
        sectionLabel.VerticalAlignment = VerticalAlignment.Center;
        sectionRow.Children.Add(sectionLabel);
        var rows = new List<Action<Role?>>();
        var setAll = new StackPanel { Orientation = Orientation.Horizontal };
        setAll.Children.Add(new TextBlock { Text = "Set all to", FontSize = 12, Foreground = B("Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = M(0, 0, 4, 0) });
        foreach (var role in Roles.All.Cast<Role?>().Append(null))
        {
            var button = new Button { Content = role?.Label ?? "Off", Style = Res<Style>("Button.Subtle"), Padding = new Thickness(9, 4, 9, 4), FontSize = 12 };
            button.Click += (_, _) => { foreach (var set in rows) set(role); };
            setAll.Children.Add(button);
        }
        Grid.SetColumn(setAll, 1); sectionRow.Children.Add(setAll);
        panel.Children.Add(sectionRow);

        foreach (var group in Roles.Groups)
        {
            var card = Card(); card.Margin = M(0, 0, 0, 10); card.Padding = M(18, 14, 18, 14);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = M(0, 0, 20, 0) };
            var detail = Help("");
            var title = WithHelp(Text(group.Label, "Heading"), detail);
            var equippedLine = new WrapPanel { Margin = M(0, 8, 0, 0) };
            info.Children.Add(title); info.Children.Add(equippedLine);
            grid.Children.Add(info);

            Role? Current() => Roles.Find(profile.Quick.GetValueOrDefault(group.Id));
            void Describe()
            {
                var role = Current();
                if (role == null) { detail.Visibility = Visibility.Collapsed; equippedLine.Visibility = Visibility.Collapsed; return; }
                var first = Roles.Type(group.TypeIds[0]);
                detail.Visibility = Visibility.Visible;
                detail.ToolTip = "Compares " + string.Join(" + ", Roles.Stats(role, first).Select(id => first.Stats.First(s => s.Id == id).Label)) + " against your equipped item.";
                equippedLine.Visibility = Visibility.Visible;
                RefreshEquipped();
            }
            foreach (string typeId in group.TypeIds)
            {
                var type = Roles.Type(typeId);
                var pill = new Border { Style = Res<Style>("PillNeutral"), Margin = M(0, 0, 6, 4), Padding = new Thickness(9, 3, 9, 3), ToolTip = $"The role's stats added up on the {type.Label.ToLowerInvariant()} your hero has equipped." };
                var pillLine = new StackPanel { Orientation = Orientation.Horizontal };
                pillLine.Children.Add(new TextBlock { Text = group.TypeIds.Length > 1 ? type.Label : "Equipped", FontSize = 11, Foreground = B("Text3"), Margin = M(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                var value = new TextBlock { Text = "—", FontSize = 11.5, FontFamily = Res<FontFamily>("Font.Mono"), Foreground = B("Text"), VerticalAlignment = VerticalAlignment.Center };
                pillLine.Children.Add(value);
                pill.Child = pillLine;
                quickCells.Add((type.EquipmentType, () => Current() is { } r ? Roles.Stats(r, type) : [], value));
                equippedLine.Children.Add(pill);
            }

            var options = Roles.All.Cast<Role?>().Append(null).Select(role => (
                Id: role?.Id ?? "", Label: role?.Label ?? "Off",
                Tip: (string?)(role == null ? "Don't highlight this group." : $"Highlight {group.Label.ToLowerInvariant()} with more {role.Blurb} than what you have equipped.")));
            var (host, choices) = Segmented("quick_" + group.Id, options, accent: true);
            host.VerticalAlignment = VerticalAlignment.Center;
            foreach (var (id, choice) in choices)
            {
                var role = Roles.Find(id);
                choice.IsChecked = Current() == role;
                choice.Checked += (_, _) =>
                {
                    if (role == null) profile.Quick.Remove(group.Id); else profile.Quick[group.Id] = role.Id;
                    Describe(); Changed();
                };
            }
            rows.Add(role => choices[role?.Id ?? ""].IsChecked = true);
            Describe();
            Grid.SetColumn(host, 1); grid.Children.Add(host);
            card.Child = grid;
            panel.Children.Add(card);
        }

        var footer = new Grid { Margin = M(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var customizeLine = new StackPanel { Orientation = Orientation.Horizontal };
        customizeLine.Children.Add(new TextBlock { Text = "Fine-tune in Advanced", VerticalAlignment = VerticalAlignment.Center });
        customizeLine.Children.Add(new TextBlock { Text = "", Style = Res<Style>("Text.Icon"), Foreground = B("Text"), FontSize = 11, Margin = M(8, 1, 0, 0) });
        var customizeButton = new Button { Content = customizeLine, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Copies these choices into the Advanced rules (replacing them for the groups that aren't Off) so you can add or remove stats." };
        customizeButton.Click += (_, _) => customize();
        footer.Children.Add(customizeButton);
        panel.Children.Add(footer);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    // ───────────────────────────── Advanced mode ─────────────────────────────

    void BuildAdvanced()
    {
        advancedCells.Clear(); slotItems.Clear(); slotEditors.Clear();
        // Fresh per build: a view's elements can't be re-parented into the next one.
        var slotHost = new ContentControl();
        var grid = new Grid { Margin = M(32, 20, 28, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top strip: explanation and the class filter.
        var strip = new Grid { Margin = M(0, 0, 0, 18) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var classOnly = ClassOnlyBox(); classOnly.VerticalAlignment = VerticalAlignment.Center; classOnly.Margin = M(24, 0, 0, 0);
        Grid.SetColumn(classOnly, 1); strip.Children.Add(classOnly);
        Grid.SetColumnSpan(strip, 2); grid.Children.Add(strip);

        // Slot list on the left.
        var list = new StackPanel { Margin = M(0, 0, 20, 0) };
        var label = WithHelp(Text("GEAR SLOTS", "Section"), "Each slot has its own rule. Turn a slot on to highlight its drops, then set a condition per stat, or tick stats into a custom sum. A slot that's on with every stat ignored highlights all of its drops.");
        label.Margin = M(12, 0, 0, 8);
        list.Children.Add(label);
        foreach (var type in Catalog.Types)
        {
            slotEditors[type.Id] = BuildSlotEditor(type);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 7, Height = 7, Margin = M(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);
            var name = new TextBlock { Text = type.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var count = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = B("Text2") };
            var countPill = new Border { Style = Res<Style>("PillNeutral"), Child = count, Padding = new Thickness(7, 1, 7, 1), ToolTip = "Conditions set on this slot (stats, custom sum and tier limits)." };
            Grid.SetColumn(countPill, 2); row.Children.Add(countPill);
            var item = new RadioButton { Style = Res<Style>("NavItem"), GroupName = "slot", Content = row, Padding = new Thickness(12, 7, 10, 7) };
            item.Checked += (_, _) => { openSlot = type.Id; slotHost.Content = slotEditors[type.Id]; };
            slotItems[type.Id] = (dot, countPill, count);
            UpdateSlotItem(type);
            list.Children.Add(item);
            if ((openSlot ?? Catalog.Types[0].Id) == type.Id) item.IsChecked = true;
        }
        Grid.SetRow(list, 1); grid.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.Also(s => Grid.SetRow(s, 1)));

        Grid.SetRow(slotHost, 1); Grid.SetColumn(slotHost, 1); grid.Children.Add(slotHost);

        if (advancedView is ContentControl host) host.Content = grid;
        else advancedView = new ContentControl { Content = grid };
        RefreshEquipped();
    }

    UIElement BuildSlotEditor(GearType type)
    {
        var rule = profile.Rule(type.Id);
        var card = Card(); card.Padding = new Thickness(0); card.VerticalAlignment = VerticalAlignment.Top;
        var dock = new DockPanel();

        // Header: slot name and the on/off switch.
        var header = new Grid { Margin = M(20, 16, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(Text(type.Label, "Heading"));
        header.Children.Add(titles);
        var bodyPanel = new StackPanel { Margin = M(20, 16, 20, 18) };
        void ApplyEnabled()
        {
            bodyPanel.IsEnabled = rule.Enabled;
            bodyPanel.Opacity = rule.Enabled ? 1 : 0.5;
        }
        var enabled = Switch("Highlight drops", rule.Enabled, on => { rule.Enabled = on; ApplyEnabled(); Changed(type); }, $"Turn highlighting on or off for {type.Label.ToLowerInvariant()} drops.");
        enabled.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(enabled, 1); header.Children.Add(enabled);
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var divider = new Border { Style = Res<Style>("Divider") };
        DockPanel.SetDock(divider, Dock.Top); dock.Children.Add(divider);

        // Tier range and match mode side by side.
        var top = new WrapPanel { Margin = M(0, 0, 0, 12) };
        var (tierPanel, minTier, maxTier) = TierRange(rule.MinTier, rule.MaxTier,
            v => { rule.MinTier = v; Changed(type); }, v => { rule.MaxTier = v; Changed(type); });
        ((FrameworkElement)tierPanel).Margin = M(0, 0, 0, 6);
        top.Children.Add(tierPanel);
        var (matchHost, matchButtons) = Segmented("mode_" + type.Id,
        [
            ("all", "All conditions", "The drop must pass every condition you set."),
            ("any", "Any condition", "Passing one condition is enough."),
        ]);
        matchButtons[rule.MatchAny ? "any" : "all"].IsChecked = true;
        matchButtons["all"].Checked += (_, _) => { rule.MatchAny = false; Changed(type); };
        matchButtons["any"].Checked += (_, _) => { rule.MatchAny = true; Changed(type); };
        var matchField = Field("Match", matchHost, "How the stat conditions combine. The tier range always applies on top.");
        matchField.Margin = M(16, 0, 0, 6); matchField.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(matchField);
        bodyPanel.Children.Add(top);

        // Stat table.
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        var resets = new List<Action>();
        int row = 0;
        void AddRow(bool separator, params UIElement?[] cells)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (separator)
            {
                var line = new Border { Style = Res<Style>("Divider"), VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(line, row); Grid.SetColumnSpan(line, 5); table.Children.Add(line);
            }
            for (int c = 0; c < cells.Length; c++)
                if (cells[c] is { } cell) { Grid.SetRow(cell, row); Grid.SetColumn(cell, c); table.Children.Add(cell); }
            row++;
        }
        FrameworkElement Head(string text, string? tip = null, HorizontalAlignment align = HorizontalAlignment.Left, bool help = false)
        {
            var label = Text(text, "Section");
            FrameworkElement head = label;
            if (help && tip != null)
            {
                var icon = Help(tip); icon.Margin = M(4, 0, 0, 0);
                label.VerticalAlignment = VerticalAlignment.Center;
                head = WithHelp(label, icon);
            }
            else head.ToolTip = tip;
            head.Margin = help ? M(0, 0, 6, 8) : M(0, 0, 8, 8); head.HorizontalAlignment = align;
            return head;
        }
        TextBlock EquippedCell(Func<string[]> ids, string tip)
        {
            var cell = new TextBlock
            {
                Text = "—", FontFamily = Res<FontFamily>("Font.Mono"), FontSize = 12.5, Foreground = B("Text2"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = M(0, 0, 4, 0), ToolTip = tip
            };
            advancedCells.Add((type.EquipmentType, ids, cell));
            return cell;
        }
        // A condition picker (Ignore / At least + value / Better than equipped) bound to one stored string.
        (ComboBox Mode, TextBox Box) ConditionEditor(string? stored, Action<string?> store)
        {
            bool better = FilterConfig.IsBetter(stored);
            var mode = new ComboBox { Margin = M(0, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
            mode.Items.Add("Ignore"); mode.Items.Add("At least"); mode.Items.Add("Better than equipped");
            mode.SelectedIndex = better ? 2 : string.IsNullOrWhiteSpace(stored) ? 0 : 1;
            var box = new TextBox
            {
                Text = better ? "" : stored ?? "", Margin = M(0, 5, 10, 5), MinHeight = 32, ToolTip = "Whole number; may be negative.",
                Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Hidden
            };
            void Store()
            {
                store(mode.SelectedIndex == 2 ? FilterConfig.Better
                    : mode.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null);
                box.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Hidden;
                Validate(box); Changed(type);
            }
            Validate(box);
            box.TextChanged += (_, _) => Store();
            mode.SelectionChanged += (_, _) =>
            {
                Store();
                if (mode.SelectedIndex == 1 && IsVisible) { box.Focus(); box.SelectAll(); }
            };
            resets.Add(() => { box.Text = ""; mode.SelectedIndex = 0; });
            return (mode, box);
        }

        var sumLabel = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = M(10, 0, 8, 0), TextWrapping = TextWrapping.Wrap };
        sumLabel.Margin = M(10, 0, 0, 0);
        var sumHint = Help("");
        var sumText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sumText.Children.Add(sumLabel); sumText.Children.Add(sumHint);
        void UpdateSumLabel()
        {
            var picked = FilterConfig.SumIds(type, rule);
            sumLabel.Text = "Custom sum";
            sumHint.ToolTip = picked.Length == 0 ? "Tick stats in the Sum column to add them together."
                : string.Join(" + ", type.Stats.Where(st => picked.Contains(st.Id)).Select(st => st.Label));
        }
        UpdateSumLabel();

        AddRow(true, Head("STAT"), Head("SUM", "Tick stats to add them together in the custom sum at the bottom.", HorizontalAlignment.Center, help: true),
            Head("CONDITION"), Head("VALUE"), Head("EQUIPPED", "The same stat on the item your hero has equipped in this slot.", HorizontalAlignment.Right));
        foreach (var stat in type.Stats)
        {
            var label = new TextBlock { Text = stat.Label, VerticalAlignment = VerticalAlignment.Center, Margin = M(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = stat.Label };
            CheckBox? inSum = null;
            if (Catalog.Summable(stat.Id))
            {
                var check = new CheckBox { IsChecked = rule.SumStats.Contains(stat.Id), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Add this stat to the custom sum." };
                void Toggle(bool on)
                {
                    rule.SumStats.Remove(stat.Id);
                    if (on) rule.SumStats.Add(stat.Id);
                    UpdateSumLabel(); RefreshEquipped(); Changed(type);
                }
                check.Checked += (_, _) => Toggle(true);
                check.Unchecked += (_, _) => Toggle(false);
                resets.Add(() => check.IsChecked = false);
                inSum = check;
            }
            else label.Foreground = B("Text2");
            var (mode, valueBox) = ConditionEditor(rule.Min.GetValueOrDefault(stat.Id), v => { if (v == null) rule.Min.Remove(stat.Id); else rule.Min[stat.Id] = v; });
            string[] ids = [stat.Id];
            AddRow(true, label, inSum, mode, valueBox, EquippedCell(() => ids, "This stat on the item your hero has equipped in this slot."));
        }

        // Custom sum row, visually grouped.
        var (sumMode, sumBox) = ConditionEditor(rule.SumCondition, v => rule.SumCondition = v);
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sumBg = new Border { Background = B("Elevated"), CornerRadius = new CornerRadius(7), Margin = M(0, 10, 0, 0) };
        Grid.SetRow(sumBg, row); Grid.SetColumnSpan(sumBg, 5); table.Children.Add(sumBg);
        sumMode.Margin = M(0, 15, 10, 5); sumBox.Margin = M(0, 15, 10, 5);
        var sumEquipped = EquippedCell(() => FilterConfig.SumIds(type, rule), "The ticked stats added up on the item your hero has equipped in this slot.");
        sumEquipped.Margin = M(0, 10, 14, 0);
        sumText.Margin = M(0, 10, 0, 0);
        Grid.SetColumnSpan(sumText, 2);
        foreach (var (cell, col) in new (UIElement, int)[] { (sumText, 0), (sumMode, 2), (sumBox, 3), (sumEquipped, 4) })
        { Grid.SetRow(cell, row); Grid.SetColumn(cell, col); table.Children.Add(cell); }
        row++;
        bodyPanel.Children.Add(table);

        var clear = new Button { Content = "Clear this slot", Style = Res<Style>("Button.DangerSubtle"), HorizontalAlignment = HorizontalAlignment.Left, Margin = M(-10, 14, 0, 0), ToolTip = "Sets every condition on this slot back to Ignore and removes the tier limits." };
        clear.Click += (_, _) => { foreach (var reset in resets) reset(); minTier.SelectedIndex = 0; maxTier.SelectedIndex = 0; };
        bodyPanel.Children.Add(clear);

        dock.Children.Add(bodyPanel);
        ApplyEnabled();
        card.Child = dock;
        return new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = M(0, 0, 4, 0) };
    }

    static void Validate(TextBox box)
    {
        bool ok = string.IsNullOrWhiteSpace(box.Text) || FilterConfig.TryParse(box.Text, out _);
        box.Tag = ok ? null : "invalid";
    }

    void UpdateSlotItem(GearType type)
    {
        if (!slotItems.TryGetValue(type.Id, out var item)) return;
        var rule = profile.Rule(type.Id);
        int count = type.Stats.Count(s => FilterConfig.IsActive(rule.Min.GetValueOrDefault(s.Id)))
            + (FilterConfig.SumActive(type, rule) ? 1 : 0) + (rule.MinTier != null ? 1 : 0) + (rule.MaxTier != null ? 1 : 0);
        item.Dot.Fill = rule.Enabled ? B("Accent") : B("BorderStrong");
        item.Count.Text = count.ToString();
        item.CountPill.Visibility = rule.Enabled && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Changed(GearType type)
    {
        UpdateSlotItem(type);
        Changed();
    }

    void Changed()
    {
        onChanged(config.CompileAll());
        SaveSoon();
    }

    /// <summary>Saves once the current input event has finished, so one action that changes several settings writes the file once.</summary>
    void SaveSoon()
    {
        if (savePending) return;
        savePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, SaveNow);
    }

    void SaveNow()
    {
        savePending = false; saveTimer.Stop();
        try { config.Save(); Warning = null; }
        catch (Exception e) { Overlay.Log(e); Warning = "Could not save filter: " + e.Message; }
    }

    // ───────────────────────────── Window placement ─────────────────────────────

    void ApplyWindowState()
    {
        // A maximised borderless window overhangs the screen by its resize border.
        root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        maximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        maximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    /// <summary>Puts the window back where it was last time, if that spot is still on a connected screen.</summary>
    void RestorePlacement()
    {
        if (config.Window is not { Width: > 0, Height: > 0 } w) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(w.Left, w.Top, Math.Max(w.Width, MinWidth), Math.Max(w.Height, MinHeight));
        // Require the title bar area to be reachable, so a monitor that's been unplugged can't strand the window.
        var grab = new Rect(bounds.Left + 40, bounds.Top, Math.Max(1, bounds.Width - 80), 36);
        if (!screen.IntersectsWith(grab)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    void RememberPlacement()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
        var w = config.Window ??= new();
        bool maximized = WindowState == WindowState.Maximized;
        if (w.Left == r.Left && w.Top == r.Top && w.Width == r.Width && w.Height == r.Height && w.Maximized == maximized) return;
        w.Left = r.Left; w.Top = r.Top; w.Width = r.Width; w.Height = r.Height; w.Maximized = maximized;
        saveTimer.Stop(); saveTimer.Start();
    }

    // ───────────────────────────── Live state ─────────────────────────────

    public void SetStatus(OverlayStatus s)
    {
        statusDot.Fill = s.Connected ? B("Accent") : s.Message.StartsWith("Waiting", StringComparison.Ordinal) ? B("Warning") : B("Danger");
        statusText.Foreground = s.Connected ? B("Text") : B("Text2");
        if (statusText.Text != s.Message) statusText.Text = s.Message;
        string matches = !s.Connected || s.Matches.Length == 0 ? ""
            : s.Matches.Length == 1 ? $"{s.Matches[0]} match your filter"
            : "Matches  " + string.Join("  ·  ", s.Matches.Select((n, i) => $"P{i + 1} {n}"));
        if (matchText.Text != matches) matchText.Text = matches;
    }

    void RefreshEquipped()
    {
        var equipped = shownEquipped;
        foreach (var (type, ids, text) in quickCells.Concat(advancedCells))
        {
            string[] picked = ids();
            text.Text = equipped == null || picked.Length == 0 ? "—"
                : !equipped.TryGetValue(type, out var stats) ? "empty"
                : Condition.Value(stats, picked) is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
        }
    }

    public void ForceClose()
    {
        forceClose = true;
        if (saveTimer.IsEnabled || savePending) SaveNow();
        Close();
    }
}

internal static class UiExtensions
{
    public static T Also<T>(this T element, Action<T> configure) { configure(element); return element; }
}
