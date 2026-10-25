using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace BF1942Options;

public sealed partial class MainWindow : Window
{
    Settings? current;
    bool loading;         // the switches are being set to what was read, so their events don't count as changes
    bool reading;         // the game folder is being read
    bool busy;            // the game folder (or the CD key) is being changed: nothing else may start meanwhile
    bool closeWhenDone;   // the window was closed while busy, and closes once that is done
    bool checking;        // the graphics card check runs: it picks a renderer when done, so no Apply meanwhile
    bool picking;         // the folder picker is open (it disables the window; the window stays open until it closes)
    static string NoOriginal(string file) => "The game's own " + file + " is not in Options\\Originals, so this can't be switched here.";
    static string NotIncluded => Game.Standalone ? "Not included in this download." : "Not included in this installer.";
    readonly string borderlessText, hiResText, sirenText, compatText;   // the hints, for an extra that is included
    int tab;              // the tab shown: 0 Fixes and extras, 1 BF42++ settings, 2 Programs
    readonly Border[] plusCards;                                         // one per group, in BF42ppSettings.cs's order
    readonly Dictionary<string, object> plusControls = new();            // each setting's switch, list, text box or ColorList
    // The Programs tab (Programs.cs), one card per program
    readonly Border[] programCards;
    readonly Dictionary<string, (TextBlock Title, TextBlock Status, Button Install, Button Remove)> programControls = new();
    bool checkingPrograms;   // what is installed is being read
    const string FixesSubtitle = "Turn the fixes and extras on or off. Nothing changes until you click Apply. Close the game first.";
    const string ProgramsSubtitle = "The programs and Windows components the installer adds. Each button acts at once. Close the game first.";
    const string PlusSubtitle = "BF42++'s own settings, in bf42++.ini. Point at a setting's name to see what it does. Nothing changes until you click Apply.";
    const string PlusOffSubtitle = "BF42++ is off on the Fixes and extras tab: these settings only work while it is on. Nothing changes until you click Apply.";
    // The title and the tabs next to each other from this width of the options area, in effective pixels
    const double TabsBesideTitle = 800;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    readonly IntPtr hwnd;
    double Scale => GetDpiForWindow(hwnd) / 96.0;

    // Width of the options area the window opens with in two and in three columns, and the widths below which
    // three columns become two and two become one (when the window is made narrower), in effective pixels
    const double TwoColumnsWidth = 920;
    const double ThreeColumnsWidth = 1140;
    const double ThreeColumnsBelow = 1040;
    const double OneColumnBelow = 720;
    const double ArtMinHeight = 300;
    int columns = 2;      // how many the cards are in now
    int fitColumns = 2;   // how many the window was fitted with: 3 on a screen too short for the page in two
    int pendingFit = 1;   // the page's texts (after ReloadAsync), plus the art when there is one
    bool sizingOrMoving;  // the user is dragging the window or its edge (WM_ENTERSIZEMOVE to WM_EXITSIZEMOVE)
    IntPtr dragScreen;    // the screen that drag started on

    // The smallest size that shows the whole page, in screen pixels, per display scale (DPI): measured at the
    // scale the window was fitted at (fitDpi, 0 until then: no limit), worked out from that for other scales,
    // and corrected there by KeepAllInView, as the page doesn't grow or shrink exactly with the scale.
    // WindowProc gives it to Windows whenever Windows asks (WM_GETMINMAXINFO), for the scale and the screen
    // the window is on at that moment - never more than that screen can show, so on a screen too small for the
    // page the window still fits and the options scroll. (OverlappedPresenter.PreferredMinimumWidth/Height
    // would have to be changed after every move instead.)
    readonly Dictionary<uint, SizeInt32> minSizes = new();
    uint fitDpi;

    SizeInt32 MinSize(uint dpi) => minSizes.TryGetValue(dpi, out SizeInt32 size) ? size : new SizeInt32(
        (int)Math.Round(minSizes[fitDpi].Width * (double)dpi / fitDpi), (int)Math.Round(minSizes[fitDpi].Height * (double)dpi / fitDpi));

    delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    const uint WM_GETMINMAXINFO = 0x0024, WM_NCDESTROY = 0x0082, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
    const uint WM_DPICHANGED = 0x02E0;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    readonly SubclassProc windowProc;   // kept, so it isn't collected while Windows still calls it

    public MainWindow()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        windowProc = WindowProc;
        SetWindowSubclass(hwnd, windowProc, UIntPtr.Zero, UIntPtr.Zero);
        // The page draws the title bar itself (AppTitleBar), with the icon and title centred on the same line.
        // Windows still draws the minimize/maximize/close buttons, in the theme's colours.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        var khaki = Windows.UI.Color.FromArgb(255, 0xE8, 0xDC, 0xC0);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = khaki;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0x3A, 0x34, 0x28);
        AppWindow.TitleBar.ButtonHoverForegroundColor = khaki;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 0x21, 0x1E, 0x17);
        // The installer's welcome-page art at the top of the left side, or the game's name when the build has none
        if (File.Exists(Game.Art))
        {
            Art.Source = new BitmapImage(new Uri(Game.Art));
            // The art's height is only known once it has loaded, so the window is fitted again then
            Art.ImageOpened += (_, _) => FitWhenReady();
            Art.ImageFailed += (_, _) => FitWhenReady();
            pendingFit++;
        }
        else { Art.Visibility = Visibility.Collapsed; ArtText.Visibility = Visibility.Visible; }
        if (Game.DiscordUrl == "") DiscordButton.Visibility = Visibility.Collapsed;
        else ToolTipService.SetToolTip(DiscordButton, Game.DiscordUrl);
        if (Game.ServerAddress == "") JoinButton.Visibility = Visibility.Collapsed;
        else
        {
            // "Battlefield 1942 - MoonGamers" -> "Join MoonGamers"
            string name = Game.ServerShortcut;
            int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
            JoinText.Text = "Join " + (dash >= 0 ? name[(dash + 3)..] : name != "" ? name : "the server");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(JoinButton, JoinText.Text);
        }
        string icon = Path.Combine(AppContext.BaseDirectory, "bf1942.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);   // taskbar and Alt-Tab
            TitleIcon.Source = new BitmapImage(new Uri(icon));
        }
        // The separate programs the installer can add, when build.ps1 listed them (none: no note)
        if (Game.SeparatePrograms is { } programs)
        {
            if (programs.Length == 0)
            {
                FooterNote.Text = "";
                FooterNote.Visibility = Visibility.Collapsed;
            }
            else if (programs.Length == 1)
                FooterNote.Text = programs[0] + " is a separate program: add or remove it on the Programs tab.";
            else
                FooterNote.Text = string.Join(", ", programs[..^1]) + " and " + programs[^1] +
                                  " are separate programs: add or remove them on the Programs tab.";
        }
        AppWindow.Closing += (_, args) =>
        {
            if (picking) args.Cancel = true;
            if (!busy) return;
            args.Cancel = true;
            CloseWhenIdle();
        };
        // Also when a message's height settles after it slides in, as the options area gets smaller then
        Scroller.SizeChanged += (_, _) => { ApplyLayout(); EqualizePages(); KeepAllInView(); };
        Status.SizeChanged += (_, _) => KeepAllInView();

        for (int i = 0; i < Game.FontNames.Length; i++)
            FontSize.Items.Add(new ComboBoxItem { Content = Game.FontNames[i] + (i == 2 ? " (default)" : "") + " - for " + Game.FontHints[i] });
        borderlessText = BorderlessHint.Text;
        hiResText = HiResHint.Text;
        sirenText = SirenHint.Text;
        compatText = CompatHint.Text;
        if (Game.ServerShortcut != "")
            SkipHint.Text += $" The \"{Game.ServerShortcut}\" shortcut always skips them.";
        // No CD key card: SetColumns leaves it out, so the card above it takes up the rest of its column
        if (!Game.SerialAllowed) SerialCard.Visibility = Visibility.Collapsed;
        var (w, h, r) = Game.PrimaryDisplay();
        ScreenNow.Text = $"Your main screen: {w} x {h} at {r} Hz.";
        plusCards = MakePlusCards();
        programCards = MakeProgramCards();
        ShowPage(0);
        columns = 0;
        SetColumns(2);

        Root.Loaded += async (_, _) =>
        {
            await OpenFolderAsync();
            FitWhenReady();
        };
    }

    // Shows what is in the game folder, or why it can't
    async Task OpenFolderAsync()
    {
        GameFolder.Text = Game.Dir;
        ToolTipService.SetToolTip(GameFolder, Game.Dir);
        for (int i = 0; i < Game.FontNames.Length; i++)
            ((ComboBoxItem)FontSize.Items[i]).IsEnabled = Game.FontAvailable(i);
        Guide.Visibility = File.Exists(Game.TroubleshootingPdf) ? Visibility.Visible : Visibility.Collapsed;
        CheckGraphics.IsEnabled = Game.VulkanCheckAvailable;
        BorderlessHint.Text = Game.BorderlessAvailable ? borderlessText
            : Environment.Is64BitOperatingSystem ? NotIncluded : "Only available on 64-bit Windows.";
        HiResHint.Text = Game.HiResAvailable ? hiResText : Game.HiResNoOriginal ? NoOriginal("menu.rfa") : NotIncluded;
        SirenHint.Text = Game.SirenAvailable ? sirenText : Game.SirenNoOriginal ? NoOriginal("Battle_of_Britain.rfa") : NotIncluded;
        CompatHint.Text = Game.CompatAvailable ? compatText : NotIncluded;
        // The versions of the fixes in this folder's library
        DxvkTitle.Text = ("DXVK " + Game.Version("VerDXVK")).Trim();
        DgVoodooTitle.Text = ("dgVoodoo2 " + Game.Version("VerDgVoodoo2")).Trim();
        BF42ppTitle.Text = ("BF42++ " + Game.Version("VerBF42PP")).Trim();
        // The radio buttons' content is a title and a description, so screen readers get the title as the name
        AutomationProperties.SetName(Dxvk, DxvkTitle.Text);
        AutomationProperties.SetName(DgVoodoo, DgVoodooTitle.Text);
        AutomationProperties.SetName(NoRenderer, "No graphics fix");
        Columns.Opacity = PlusColumns.Opacity = Game.Installed ? 1 : 0.5;
        // DataField42 and PunkBuster go in this game folder
        _ = RefreshProgramsAsync();
        if (Game.Installed)
        {
            await ReloadAsync();
            return;
        }
        current = null;
        UpdateControls();
        if (!Game.Standalone)
            Show(InfoBarSeverity.Error, !Game.GameFound
                ? "BF1942.exe is missing from " + Game.Dir + ". If an antivirus removed it, restore it there, or click Browse and pick the Battlefield 1942 folder."
                : "The fixes this installer keeps in the game folder are not in " + Game.Dir + ". Click Browse and pick the folder you installed Battlefield 1942 in with this installer, or run the installer again.");
        else if (!Game.GameFound)
            Show(InfoBarSeverity.Informational, "Click Browse and pick your Battlefield 1942 folder: the one with BF1942.exe in it.");
        else
        {
            // A standalone copy sets up any Battlefield 1942 folder with the library it brings
            Show(InfoBarSeverity.Informational, $"BF1942 Options isn't set up in this game folder yet. Set up copies its fixes and extras " +
                 $"to an Options folder there ({Game.BundledSize / 1048576.0:N0} MB). Nothing in the game changes until you click Apply.");
            var setUp = new Button { Content = "Set up", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            setUp.Click += SetUp_Click;
            Status.ActionButton = setUp;
        }
    }

    async void SetUp_Click(object sender, RoutedEventArgs e)
    {
        if (busy || reading || checking || picking || Game.Installed || !Game.GameFound) return;
        SetBusy(true);
        try
        {
            await Task.Run(Game.SetUp);
            try { Game.SaveFolder(); }
            catch (Exception ex) { Game.Log("Saving the game folder failed: " + ex.Message); }
            Status.IsOpen = false;
            await OpenFolderAsync();
            if (current != null) Show(InfoBarSeverity.Success, "Set up. Pick the fixes and extras you want and click Apply.");
        }
        catch (Exception ex)
        {
            Game.Log("Setting up failed: " + ex);
            closeWhenDone = false;
            Show(InfoBarSeverity.Error, "The game folder could not be set up: " + ex.Message +
                 " If an antivirus blocked it, add an exclusion for the game folder and click Browse to pick it again.");
        }
        SetBusy(false);
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (busy || reading || checking || picking) return;
        // A switch changed but not applied would be lost: it belongs to this game folder
        if (current != null && !Wanted().SameAs(current))
        {
            Show(InfoBarSeverity.Warning, "Click Apply first, or set the options back, before you pick another game folder.");
            return;
        }
        string? dir;
        picking = true;
        UpdateControls();
        try
        {
            string parent = Path.GetDirectoryName(Game.Dir) ?? Game.Dir;
            dir = await FolderPicker.PickAsync(hwnd, "Pick the Battlefield 1942 folder (the one with BF1942.exe in it)", parent);
        }
        catch (Exception ex)
        {
            Show(InfoBarSeverity.Error, "The folder picker could not open: " + ex.Message);
            return;
        }
        finally
        {
            picking = false;
            UpdateControls();
        }
        if (dir == null) return;
        if (!Game.HasGame(dir))
        {
            Show(InfoBarSeverity.Warning, "BF1942.exe is not in " + dir + ". Pick the Battlefield 1942 folder: the one with BF1942.exe in it.");
            return;
        }
        if (string.Equals(Game.FullPath(dir), Game.Dir, StringComparison.OrdinalIgnoreCase))
        {
            if (Game.Installed) return;
        }
        else if (!App.ClaimFolder(dir))
        {
            Show(InfoBarSeverity.Warning, "Battlefield 1942 Options is already open for " + dir + ".");
            return;
        }
        Game.UseFolder(dir);
        try { Game.SaveFolder(); }
        catch (Exception ex) { Game.Log("Saving the game folder failed: " + ex.Message); }
        Game.Log("Game folder: " + Game.Dir);
        Status.IsOpen = false;
        await OpenFolderAsync();
        if (current != null) Show(InfoBarSeverity.Success, "Showing the options for " + Game.Dir + ".");
    }

    IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == WM_NCDESTROY) RemoveWindowSubclass(window, windowProc, id);
        else if (message == WM_ENTERSIZEMOVE)
        {
            sizingOrMoving = true;
            dragScreen = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        }
        else if (message == WM_EXITSIZEMOVE)
        {
            sizingOrMoving = false;
            if (MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) != dragScreen) FitOnScreen();
            KeepAllInView();
        }
        IntPtr result = DefSubclassProc(window, message, wParam, lParam);
        // Another display scale without a drag: the window was moved to another screen by the keyboard, or the
        // scale of its screen was changed. WinUI has resized the window for it by now.
        if (message == WM_DPICHANGED && !sizingOrMoving) FitOnScreen();
        if (message == WM_GETMINMAXINFO && fitDpi > 0)
        {
            SizeInt32 min = MinSize(GetDpiForWindow(window));
            int width = min.Width, height = min.Height;
            var screen = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref screen))
            {
                width = Math.Min(width, screen.Work.Right - screen.Work.Left);
                height = Math.Min(height, screen.Work.Bottom - screen.Work.Top);
            }
            Marshal.WriteInt32(lParam, 24, width);    // MINMAXINFO.ptMinTrackSize
            Marshal.WriteInt32(lParam, 28, height);
        }
        return result;
    }

    // The window is sized once the texts and the art are in, as both change the page's height
    void FitWhenReady()
    {
        if (--pendingFit == 0) FitToContent();
    }

    // Sizes the window so the whole page shows without scrolling, as long as the screen is big enough: in two
    // columns, or in three (a wider, shorter page) when two are too tall for the screen and three fit its width
    void FitToContent()
    {
        double scale = Scale;
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double side = Page.ColumnDefinitions[0].Width.Value;
        SideButtons.Measure(new Windows.Foundation.Size(side, double.PositiveInfinity));
        SideFolder.Measure(new Windows.Foundation.Size(side, double.PositiveInfinity));
        double sidebar = ArtMinHeight + SideButtons.DesiredSize.Height + SideFolder.DesiredSize.Height;
        double width = 0, height = 0;
        foreach (Grid g in PageColumns) g.MinHeight = 0;   // the pages' own heights are measured (EqualizePages)
        foreach (int n in new[] { 2, 3 })
        {
            double w = side + (n == 2 ? TwoColumnsWidth : ThreeColumnsWidth) + Root.Padding.Left + Root.Padding.Right;
            if (n == 3 && w * scale > work.Width - 16) break;
            SetColumns(n);
            // The options decide the height, those of the tab that needs more, so switching tabs never scrolls. The
            // art on the left shrinks to what is left beside them, but always keeps at least ArtMinHeight, so the
            // buttons below it never push the window taller.
            double h = Page.RowDefinitions[0].Height.Value + Math.Max(MeasureTabs(w - side), sidebar);
            if (n == 3 && h >= height) { SetColumns(2); break; }
            fitColumns = n;
            width = w;
            height = h;
            if (h * scale <= work.Height - 48) break;
        }

        int cw = Math.Min((int)Math.Ceiling(width * scale), work.Width - 16);
        int ch = Math.Min((int)Math.Ceiling(height * scale), work.Height - 48);
        fitDpi = 0;   // no smallest size while the window is fitted
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        CorrectHeight(3, sidebar, work);
    }

    // The measurement can be off by a few pixels either way, so once the page is laid out at that size the
    // window is made exactly as tall as the options need (but never shorter than the left side needs). From
    // then on it can be made bigger, and the page grows with it, but not smaller than the size that shows
    // everything at once.
    void CorrectHeight(int tries, double sidebar, RectInt32 work)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            double spare = Scroller.ViewportHeight - Math.Max(Scroller.ExtentHeight, OtherPageHeight());
            spare = Math.Min(spare, Page.ActualHeight - Page.RowDefinitions[0].Height.Value - sidebar);
            int change = (int)Math.Round(spare * Scale);
            // On a screen too small for the page, the window gets the screen's whole height and the options
            // scroll - but never more than the screen (ResizeClient makes the window taller than asked, by about
            // the height of a title bar, as the page draws its own)
            change = Math.Max(change, AppWindow.Size.Height - work.Height);
            int width = Math.Min(AppWindow.Size.Width, work.Width);
            if (tries > 0 && (Math.Abs(change) >= 2 || width < AppWindow.Size.Width))
            {
                AppWindow.Resize(new SizeInt32(width, AppWindow.Size.Height - change));
                CorrectHeight(tries - 1, sidebar, work);
                return;
            }
            AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2,
                                          work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
            fitDpi = GetDpiForWindow(hwnd);
            minSizes.Clear();
            minSizes[fitDpi] = AppWindow.Size;
        });
    }

    // A long message can make the bottom row taller and take room from the cards, and on another display scale
    // the page can need a few pixels more than the smallest size worked out for it. The window then grows by
    // that much (as far as the screen allows), so every card stays in view - once the user lets go, if they
    // are dragging the window or its edge, rather than fighting the drag.
    void KeepAllInView()
    {
        if (pendingFit > 0 || columns == 1 || sizingOrMoving) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (sizingOrMoving) return;
            int grow = (int)Math.Ceiling((Scroller.ExtentHeight - Scroller.ViewportHeight) * Scale);
            RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            grow = Math.Min(grow, work.Height - AppWindow.Size.Height);
            if (grow <= 1) return;
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, AppWindow.Size.Height + grow));
            // Without a message it was the page itself that didn't fit, so at this scale the window needs at
            // least this height from now on
            uint dpi = GetDpiForWindow(hwnd);
            if (fitDpi > 0 && Status.Visibility == Visibility.Collapsed && AppWindow.Size.Height > MinSize(dpi).Height)
                minSizes[dpi] = new SizeInt32(MinSize(dpi).Width, AppWindow.Size.Height);
            // It grows downwards, so near the bottom of the screen it moves up by as much as went past it
            int below = AppWindow.Position.Y + AppWindow.Size.Height - (work.Y + work.Height);
            if (below > 0) AppWindow.Move(new PointInt32(AppWindow.Position.X, AppWindow.Position.Y - below));
        });
    }

    // Moved to a screen it doesn't fit on (a smaller one, or one with a larger display scale), the window is made
    // as big as that screen and moved fully onto it, so the bottom row (a message, Apply and Close) shows
    void FitOnScreen()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // A maximized window already fills its screen
            if (sizingOrMoving || fitDpi == 0 || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
                return;
            RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            if (AppWindow.Size.Width <= work.Width && AppWindow.Size.Height <= work.Height) return;
            var size = new SizeInt32(Math.Min(AppWindow.Size.Width, work.Width), Math.Min(AppWindow.Size.Height, work.Height));
            AppWindow.Resize(size);
            AppWindow.Move(new PointInt32(Math.Clamp(AppWindow.Position.X, work.X, work.X + work.Width - size.Width),
                                          Math.Clamp(AppWindow.Position.Y, work.Y, work.Y + work.Height - size.Height)));
        });
    }

    // The columns the window was fitted with, fewer when it is made narrower. Extra height in a bigger window
    // stays below the cards instead of opening up gaps inside them.
    void ApplyLayout()
    {
        if (pendingFit > 0) return;
        double w = Scroller.ActualWidth;
        SetColumns(w < OneColumnBelow ? 1 : w < ThreeColumnsBelow ? Math.Min(fitColumns, 2) : fitColumns);
        // The tabs go below the title when there isn't room for both on one line
        bool beside = w >= TabsBesideTitle;
        Grid.SetRow(Tabs, beside ? 0 : 1);
        Grid.SetColumn(Tabs, beside ? 1 : 0);
        Tabs.HorizontalAlignment = beside ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    // Shares the cards out over the columns, in the order they are read in. A hidden card (the CD key) is left
    // out, and the last card in each column takes up the rest of it.
    void SetColumns(int n)
    {
        if (n == columns) return;
        columns = n;
        Border[][] layout = n switch
        {
            1 => new[] { new[] { GraphicsCard, FixesCard, SerialCard, PlayerCard, ExtrasCard, DisplayCard } },
            2 => new[] { new[] { GraphicsCard, FixesCard, SerialCard }, new[] { PlayerCard, ExtrasCard, DisplayCard } },
            _ => new[] { new[] { GraphicsCard, FixesCard }, new[] { PlayerCard, SerialCard, ExtrasCard }, new[] { DisplayCard } },
        };
        Arrange(Columns, new[] { LeftColumn, RightColumn, ThirdColumn }, layout);
        // BF42++'s cards: HUD, chat and console, 3D map markers | controls, performance, graphics and files
        Border[] p = plusCards;
        Arrange(PlusColumns, new[] { PlusLeft, PlusRight, PlusThird }, n switch
        {
            1 => new[] { p },
            2 => new[] { p[..3], p[3..] },
            _ => new[] { new[] { p[0], p[2] }, new[] { p[1], p[3] }, p[4..] },
        });
        // The programs: runtimes and DirectPlay | DataField42, Rich Presence and PunkBuster, or two per column
        Border[] g = programCards;
        if (g.Length > 0)
            Arrange(ProgramColumns, new[] { ProgramsLeft, ProgramsRight, ProgramsThird }, n switch
            {
                1 => new[] { g },
                2 => new[] { g[..3], g[3..] },
                _ => new[] { g[..2], g[2..4], g[4..] },
            });
    }

    // Puts each column's cards in its grid, and shows as many grids as there are columns
    static void Arrange(Grid columnsGrid, Grid[] grids, Border[][] layout)
    {
        foreach (Grid g in grids)
        {
            g.Children.Clear();
            g.RowDefinitions.Clear();
        }
        columnsGrid.ColumnDefinitions.Clear();
        for (int c = 0; c < grids.Length; c++)
        {
            grids[c].Visibility = c < layout.Length ? Visibility.Visible : Visibility.Collapsed;
            if (c >= layout.Length) continue;
            columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (Border card in layout[c])
            {
                Grid.SetRow(card, grids[c].RowDefinitions.Count);
                grids[c].RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grids[c].Children.Add(card);
            }
            int last = grids[c].RowDefinitions.Count - 1;
            while (last > 0 && ((Border)grids[c].Children[last]).Visibility == Visibility.Collapsed) last--;
            grids[c].RowDefinitions[last].Height = new GridLength(1, GridUnitType.Star);
        }
    }

    void ShowSerial()
    {
        bool serial = Game.HasValidSerial();
        SerialNow.Text = serial ? "A valid CD key is registered." : "No valid CD key was found.";
        SerialHint.Text = serial
            ? "Nothing to do. Battlefield 1942 uses the key that is already there."
            : Game.Standalone ? "Battlefield 1942 needs one to play online. Generate a random key."
            : "Battlefield 1942 needs one to play online. Generate a random key; it is removed again when you uninstall the game.";
        GenerateSerial.IsEnabled = !serial;
    }

    async Task ReloadAsync()
    {
        reading = true;
        UpdateControls();
        try { current = await Task.Run(Game.Read); }
        catch (Exception ex)
        {
            // Nothing to switch until the state can be read: the switches no longer show what is in the folder
            current = null;
            Game.Log("Reading the game folder failed: " + ex);
            closeWhenDone = false;   // after an Apply the window was closed during: it stays open to show this
            Show(InfoBarSeverity.Error, "The game folder could not be read: " + ex.Message + " Close the game and other programs using it, then reopen this window.");
            return;
        }
        finally
        {
            reading = false;
            UpdateControls();
        }

        loading = true;
        Dxvk.IsChecked = current.Renderer == Renderer.DXVK;
        DgVoodoo.IsChecked = current.Renderer == Renderer.DgVoodoo;
        NoRenderer.IsChecked = current.Renderer == Renderer.None;
        RendererNow.Text = current.Renderer switch
        {
            Renderer.Unknown => "The graphics files in the game folder are neither DXVK nor dgVoodoo2. Pick one to replace them (they are kept in Options\\Backup).",
            Renderer.None => "No graphics fix is installed.",
            _ => "Installed now: " + Game.RendererName(current.Renderer) + ".",
        };
        ShowSerial();
        BF42pp.IsOn = current.BF42pp;
        Audio.IsOn = current.Audio;
        // Not while it is selected: WinUI throws when the selected item is removed (after an Apply with a font that
        // didn't come from the installer)
        if (FontSize.Items.Count > Game.FontNames.Length) FontSize.SelectedIndex = -1;
        while (FontSize.Items.Count > Game.FontNames.Length) FontSize.Items.RemoveAt(FontSize.Items.Count - 1);
        if (current.Font < 0)
            FontSize.Items.Add(new ComboBoxItem { Content = Game.FontMissing ? "No font (Font.rfa is missing)" : "Another font (not from the installer)" });
        FontSize.SelectedIndex = current.Font < 0 ? Game.FontNames.Length : current.Font;
        HiResUI.IsOn = current.HiResUI;
        HiResUI.IsEnabled = Game.HiResAvailable;
        Borderless.IsOn = current.Borderless;
        Borderless.IsEnabled = Game.BorderlessAvailable || current.Borderless;
        NoSiren.IsEnabled = Game.SirenAvailable;
        Compat.IsEnabled = Game.CompatAvailable || current.Compat;
        SkipIntro.IsOn = current.SkipIntro;
        NoSiren.IsOn = current.NoSiren;
        Compat.IsOn = current.Compat;
        PlayerName.Text = current.PlayerName;
        foreach (PlusSetting s in Game.PlusSettings)
        {
            string value = current.Plus[s.Key];
            switch (plusControls[s.Key])
            {
                case ToggleSwitch t: t.IsOn = value == "on"; break;
                case ComboBox c: c.SelectedIndex = Array.IndexOf(s.Choices!, value); break;
                case TextBox b: b.Text = s.Show(value); break;
                case ColorList l: l.Value = value; break;
            }
        }
        loading = false;
        ShowPlayer();
        UpdateApply();
    }

    // What "Use this name" would do with the name typed, or why it can't
    void ShowPlayer()
    {
        if (current == null) return;
        string name = PlayerName.Text.Trim(), profile = Game.ProfileFolderName(name);
        string? error = Game.PlayerNameError(name);
        bool same = name == current.PlayerName && profile == current.Profile;
        UseName.IsEnabled = error == null && !same;
        PlayerHint.Text = same || name == "" && current.PlayerName == ""
            ? "The name other players see. The game starts with the profile " + current.Profile + "."
            : error ?? (Game.ProfileExists(profile)
                ? "Sets this name in the profile " + profile + ", and the game starts with that profile."
                : "Creates the profile " + profile + " (a copy of " + current.Profile + ") with this name, and the game starts with it.");
    }

    void PlayerName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!loading) ShowPlayer();
    }

    void PlayerName_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (UseName.IsEnabled) UseName_Click(sender, e);
    }

    async void UseName_Click(object sender, RoutedEventArgs e)
    {
        if (current == null || busy || !GameClosed(thisFolderOnly: true)) return;
        string name = PlayerName.Text.Trim();
        SetBusy(true);
        try
        {
            string profile = await Task.Run(() => Game.SetPlayerName(name));
            current.PlayerName = name;
            current.Profile = profile;
            PlayerName.Text = name;
            Show(InfoBarSeverity.Success, "Done. The game starts with the profile " + profile + ", with the name " + name + ".");
        }
        catch (Exception ex)
        {
            Game.Log("Player name failed: " + ex);
            closeWhenDone = false;
            Show(InfoBarSeverity.Error, "The player name could not be set: " + ex.Message);
        }
        ShowPlayer();
        SetBusy(false);
    }

    Settings Wanted()
    {
        Settings w = current!.Clone();
        if (Dxvk.IsChecked == true) w.Renderer = Renderer.DXVK;
        else if (DgVoodoo.IsChecked == true) w.Renderer = Renderer.DgVoodoo;
        else if (NoRenderer.IsChecked == true) w.Renderer = Renderer.None;
        w.BF42pp = BF42pp.IsOn;
        w.Audio = Audio.IsOn;
        w.Font = FontSize.SelectedIndex >= 0 && FontSize.SelectedIndex < Game.FontNames.Length ? FontSize.SelectedIndex : -1;
        w.HiResUI = HiResUI.IsOn;
        w.Borderless = Borderless.IsOn;
        w.SkipIntro = SkipIntro.IsOn;
        w.NoSiren = NoSiren.IsOn;
        w.Compat = Compat.IsOn;
        // A value BF42++ wouldn't take stays as typed: it counts as a change, and PlusProblem keeps Apply off
        foreach (PlusSetting s in Game.PlusSettings)
            w.Plus[s.Key] = plusControls[s.Key] switch
            {
                ToggleSwitch t => t.IsOn ? "on" : "off",
                ComboBox c => c.SelectedIndex >= 0 ? s.Choices![c.SelectedIndex] : w.Plus[s.Key],
                TextBox b => s.Normalize(b.Text) ?? b.Text,
                ColorList l => l.Value,
                _ => w.Plus[s.Key],
            };
        return w;
    }

    // The first BF42++ setting with a value it wouldn't take, or null
    PlusSetting? PlusProblem() =>
        Game.PlusSettings.FirstOrDefault(s => plusControls[s.Key] is TextBox b && s.Normalize(b.Text) == null);

    // A card per group of BF42++ settings: each setting's name, with a tooltip that explains it, and its switch, list
    // or text box (which has the same tooltip, so it also shows when the keyboard gets there)
    Border[] MakePlusCards()
    {
        var cards = new List<Border>();
        foreach (var group in Game.PlusSettings.GroupBy(s => s.Card))
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = group.Key, Style = (Style)Application.Current.Resources["CardTitle"] });
            foreach (PlusSetting s in group)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 32 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = s.Label, Style = (Style)Application.Current.Resources["Setting"], VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(label, s.Tooltip);
                object control;
                FrameworkElement view;
                switch (s.Kind)
                {
                    case PlusKind.Switch:
                        var toggle = new ToggleSwitch { Style = (Style)Application.Current.Resources["Switch"] };
                        toggle.Toggled += (_, _) => UpdateApply();
                        control = view = toggle;
                        break;
                    case PlusKind.Choice:
                        var list = new ComboBox { MinWidth = 96, VerticalAlignment = VerticalAlignment.Center };
                        foreach (string c in s.Choices!) list.Items.Add(s.Show(c));
                        list.SelectionChanged += (_, _) => UpdateApply();
                        control = view = list;
                        break;
                    case PlusKind.Colors:
                        var colors = new ColorList(s.Label);
                        colors.Changed += UpdateApply;
                        control = colors;
                        view = colors.View;
                        break;
                    default:
                        var box = new TextBox { Width = s.Kind == PlusKind.Color ? 120 : 80, VerticalAlignment = VerticalAlignment.Center };
                        box.TextChanged += (_, _) =>
                        {
                            // Marked while BF42++ wouldn't take it; LostFocus says why
                            if (s.Normalize(box.Text) == null) box.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x5A, 0x4A));
                            else box.ClearValue(Control.BorderBrushProperty);
                            UpdateApply();
                        };
                        box.LostFocus += (_, _) => { if (s.Normalize(box.Text) == null) Show(InfoBarSeverity.Warning, s.Problem); };
                        control = view = box;
                        break;
                }
                Grid.SetColumn(view, 1);
                // The colour list's swatches and + have tooltips of their own
                if (control is not ColorList) ToolTipService.SetToolTip(view, s.Tooltip);
                AutomationProperties.SetName(view, s.Label);
                AutomationProperties.SetHelpText(view, s.Help);
                AutomationProperties.SetAutomationId(view, "Plus_" + s.Key);
                plusControls[s.Key] = control;
                row.Children.Add(label);
                row.Children.Add(view);
                panel.Children.Add(row);
            }
            cards.Add(new Border { Style = (Style)Application.Current.Resources["Card"], Child = panel });
        }
        return cards.ToArray();
    }

    // A card per program (Game.Programs): its name and version, who made it and what it does, what is installed
    // now, and Install (or Repair, Update, Turn on) and Remove. Shared runtimes have no Remove: other games and
    // programs use them too.
    Border[] MakeProgramCards()
    {
        var cards = new List<Border>();
        foreach (ProgramInfo p in Game.Programs)
        {
            var panel = new Grid { RowSpacing = 6 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            var text = new StackPanel { Spacing = 4 };
            var title = new TextBlock { Text = p.Name.ToUpperInvariant(), Style = (Style)Application.Current.Resources["CardTitle"], TextWrapping = TextWrapping.Wrap };
            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = "By " + p.Credit + ". " + p.Description + (p.Shared ? " Other games and programs use it too, so it stays installed." : ""),
                Style = (Style)Application.Current.Resources["Hint"],
            });
            var status = new TextBlock { Text = "Checking...", Style = (Style)Application.Current.Resources["Setting"], TextWrapping = TextWrapping.Wrap };
            text.Children.Add(status);
            panel.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 4, 0, 0) };
            var install = new Button { Content = "Install", IsEnabled = false };
            install.Click += (_, _) => ProgramAction(p, remove: false);
            var remove = new Button { Content = "Remove", IsEnabled = false, Visibility = p.Shared ? Visibility.Collapsed : Visibility.Visible };
            remove.Click += (_, _) => ProgramAction(p, remove: true);
            AutomationProperties.SetName(install, "Install " + p.Name);
            AutomationProperties.SetName(remove, "Remove " + p.Name);
            buttons.Children.Add(install);
            buttons.Children.Add(remove);
            Grid.SetRow(buttons, 1);
            panel.Children.Add(buttons);
            programControls[p.Id] = (title, status, install, remove);
            cards.Add(new Border { Style = (Style)Application.Current.Resources["Card"], Child = panel });
        }
        return cards.ToArray();
    }

    // Reads what is installed (DirectPlay takes a few seconds) and shows it on the cards
    async Task RefreshProgramsAsync()
    {
        if (checkingPrograms) return;
        checkingPrograms = true;
        UpdateControls();
        try
        {
            // All at once, so the quick ones don't wait for DirectPlay
            var checks = Game.Programs.Select(p => Task.Run(() => Game.CheckProgram(p))).ToArray();
            for (int i = 0; i < checks.Length; i++)
            {
                ProgramInfo p = Game.Programs[i];
                var (title, status, install, remove) = programControls[p.Id];
                ProgramState s;
                try { s = await checks[i]; }
                catch (Exception ex) { s = new ProgramState(false, "Could not be checked: " + ex.Message, false, "Install", false); }
                // With the version of the installer this game folder's library or this copy has
                title.Text = (p.Name.ToUpperInvariant() + " " + p.Version).Trim();
                status.Text = s.Text;
                install.Content = s.InstallLabel;
                AutomationProperties.SetName(install, s.InstallLabel + " " + p.Name);
                install.IsEnabled = s.CanInstall;
                remove.IsEnabled = s.CanRemove;
            }
        }
        finally
        {
            checkingPrograms = false;
            UpdateControls();
        }
    }

    async void ProgramAction(ProgramInfo p, bool remove)
    {
        if (busy || checkingPrograms || !GameClosed(thisFolderOnly: false)) return;
        SetBusy(true);
        Game.RestartNeeded = false;
        string doing = remove ? "Removing " : (string)programControls[p.Id].Install.Content switch { "Turn on" => "Turning on ", "Repair" => "Repairing ", "Update" => "Updating ", _ => "Installing " };
        Show(InfoBarSeverity.Informational, p.Id == "richpresence" && !remove && Game.DownloadsDotNet8
            ? "Downloading the .NET 8 Desktop Runtime it needs from Microsoft, then " + doing.ToLowerInvariant() + p.Name + "... This can take a few minutes."
            : doing + p.Name + (p.Id == "punkbuster42" && !remove
                ? ". Follow the PunkBuster installer, then click Install/Repair in the PunkBuster Services window and close it."
                : "... This can take a few minutes."));
        try
        {
            await Task.Run(() => { if (remove) Game.RemoveProgram(p); else Game.InstallProgram(p); });
            Show(InfoBarSeverity.Success, p.Name + (remove ? " is removed." : " is installed.") +
                 (Game.RestartNeeded ? " Restart Windows to finish." : ""));
        }
        catch (Exception ex)
        {
            Game.Log((remove ? "Removing " : "Installing ") + p.Name + " failed: " + ex);
            closeWhenDone = false;
            Show(InfoBarSeverity.Error, p.Name + (remove ? " could not be removed: " : " could not be installed: ") + ex.Message);
        }
        SetBusy(false);
        await RefreshProgramsAsync();
    }

    void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (plusControls.Count == 0) return;   // the first tab is selected while the window is made
        tab = sender.SelectedItem == PlusTab ? 1 : sender.SelectedItem == ProgramsTab ? 2 : 0;
        ShowPage(tab);
        // In case this tab needs more room than the others at this display scale
        KeepAllInView();
    }

    // The tabs' pages and their columns, in the tabs' order
    ContentControl[] PageViews => new[] { Options, PlusOptions, ProgramOptions };
    Grid[] PageColumns => new[] { Columns, PlusColumns, ProgramColumns };
    IEnumerable<int> TabsShown => new[] { 0, 1, 2 };

    // Shows a tab's page and says what it is for. FitToContent and CorrectHeight also show the others for a
    // moment, to measure them.
    void ShowPage(int page)
    {
        for (int i = 0; i < PageViews.Length; i++) PageViews[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = page == 0 ? FixesSubtitle : page == 2 ? ProgramsSubtitle : BF42pp.IsOn || current == null ? PlusSubtitle : PlusOffSubtitle;
    }

    // Every tab's cards as tall as the tallest tab needs at this width, so the last card in each column reaches
    // down to the same line on every tab. Not in one column, where the page scrolls anyway.
    void EqualizePages()
    {
        if (pendingFit > 0) return;
        foreach (Grid g in PageColumns) g.MinHeight = 0;
        if (columns == 1) return;
        double most = TabsShown.Max(PageHeight);
        ShowPage(tab);
        foreach (Grid g in PageColumns) g.MinHeight = most;
    }

    // The height a tab's page needs at the options area's width now (it is left shown)
    double PageHeight(int page)
    {
        ShowPage(page);
        Pages.Measure(new Windows.Foundation.Size(Scroller.ViewportWidth, double.PositiveInfinity));
        return Pages.DesiredSize.Height;
    }

    // The height the tallest page of the tabs not shown needs at the options area's width now
    double OtherPageHeight()
    {
        double height = TabsShown.Where(t => t != tab).Max(PageHeight);
        ShowPage(tab);
        return height;
    }

    // The height of the title, the page and the bottom row at this width, for the tab that needs more
    double MeasureTabs(double width)
    {
        double most = 0;
        foreach (int page in TabsShown)
        {
            ShowPage(page);
            Root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            most = Math.Max(most, Root.DesiredSize.Height);
        }
        ShowPage(tab);
        return most;
    }

    // The options only while the game folder's state is known and nothing is being changed (IsEnabled, so the
    // keyboard can't reach them either); Play and Join not while files are being changed. The programs don't
    // depend on the game folder's state.
    void UpdateControls()
    {
        Options.IsEnabled = PlusOptions.IsEnabled = Game.Installed && current != null && !reading && !busy;
        ProgramOptions.IsEnabled = !reading && !busy && !picking && !checkingPrograms;
        PlayButton.IsEnabled = JoinButton.IsEnabled = Game.Installed && !busy;
        BrowseButton.IsEnabled = !busy && !reading && !picking;
        UpdateApply();
    }

    void UpdateApply()
    {
        if (loading) return;
        ApplyButton.IsEnabled = Options.IsEnabled && current != null && !checking && !Wanted().SameAs(current) && PlusProblem() == null;
        // The BF42++ tab says when BF42++ is switched off
        if (tab == 1) ShowPage(1);
    }

    void SetBusy(bool on)
    {
        busy = on;
        UpdateControls();
        if (!on && closeWhenDone) Close();
    }

    // Closing while the game folder is being changed would stop that halfway, so the window closes once it is done
    void CloseWhenIdle()
    {
        if (!busy) { Close(); return; }
        closeWhenDone = true;
        Show(InfoBarSeverity.Informational, "Finishing the changes first - the window closes when they are done.");
    }

    void Changed(object sender, RoutedEventArgs e) => UpdateApply();
    void Status_Closed(InfoBar sender, InfoBarClosedEventArgs e)
    {
        Status.Visibility = Visibility.Collapsed;
        FooterNote.Visibility = FooterNote.Text != "" ? Visibility.Visible : Visibility.Collapsed;
    }
    void FontSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApply();

    void Show(InfoBarSeverity severity, string message)
    {
        Status.ActionButton = null;   // only the Set up message has one, which OpenFolderAsync adds
        Status.Severity = severity;
        Status.Message = message;
        Status.Visibility = Visibility.Visible;
        FooterNote.Visibility = Visibility.Collapsed;
        Status.IsOpen = true;
    }

    // Changing the game folder only needs this folder's game closed; playing, and the CD key all copies share,
    // need every copy closed
    bool GameClosed(bool thisFolderOnly)
    {
        string? running = Game.RunningProgram(thisFolderOnly);
        if (running == null) return true;
        Show(InfoBarSeverity.Warning, running + " is running. Close the game first, then try again.");
        return false;
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (current == null || busy || checking) return;
        Settings from = current, wanted = Wanted();
        if (PlusProblem() is { } problem)
        {
            Show(InfoBarSeverity.Warning, problem.Problem);
            return;
        }
        if (wanted.SameAs(from) || !GameClosed(thisFolderOnly: true)) return;

        SetBusy(true);
        try
        {
            Game.Log("Apply");
            await Task.Run(() => Game.Apply(from, wanted));
            // The game folder's own files that Apply replaced or removed
            string[] kept = Game.BackedUp.Select(f => Path.GetFileName(f)).Distinct().ToArray();
            Show(InfoBarSeverity.Success, "Done. Your changes are in place for the next time you start the game." + (kept.Length == 0 ? ""
                 : " The game folder's own " + string.Join(", ", kept) + (kept.Length == 1 ? " is" : " are") + " kept in Options\\Backup."));
        }
        catch (Exception ex)
        {
            Game.Log("Failed: " + ex);
            closeWhenDone = false;   // the window stays open, so the player sees what went wrong
            Show(InfoBarSeverity.Error, "Not everything could be changed: " + ex.Message +
                 " If an antivirus blocked it, add an exclusion for the game folder and try again." +
                 (Game.Standalone ? "" : " Running the installer again also puts every fix back."));
        }
        await ReloadAsync();
        SetBusy(false);
    }

    async void Resolution_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !GameClosed(thisFolderOnly: true)) return;
        SetBusy(true);
        try
        {
            string mode = await Task.Run(Game.SetDisplayMode);
            Show(InfoBarSeverity.Success, "The game is set to " + mode + ".");
        }
        catch (Exception ex)
        {
            Game.Log("Display mode failed: " + ex);
            closeWhenDone = false;
            Show(InfoBarSeverity.Error, "The resolution could not be changed: " + ex.Message);
        }
        SetBusy(false);
    }

    async void CheckGraphics_Click(object sender, RoutedEventArgs e)
    {
        CheckGraphics.IsEnabled = false;
        checking = true;
        UpdateApply();
        (int code, string details) result;
        try { result = await Task.Run(() => (Game.RunVulkanCheck(out string d), d)); }
        catch (Exception ex) { result = (-1, ex.Message); }
        CheckGraphics.IsEnabled = true;
        checking = false;

        string details = result.details.Replace("\r\n", " ").Replace('\n', ' ');
        // Whether the recommended fix is the one installed now - not whether something else is waiting for Apply
        if (result.code == 0)
        {
            Dxvk.IsChecked = true;
            Show(InfoBarSeverity.Success, "Your graphics card can run DXVK, the recommended graphics fix. " +
                 (current?.Renderer == Renderer.DXVK ? "It's already installed." : "Click Apply to use it."));
        }
        else if (result.code == 1)
        {
            DgVoodoo.IsChecked = true;
            Show(InfoBarSeverity.Informational, "Your graphics card can't run DXVK, so use dgVoodoo2. " +
                 (current?.Renderer == Renderer.DgVoodoo ? "It's already installed. " : "Click Apply to use it. ") + details);
        }
        else
            Show(InfoBarSeverity.Warning, "The check couldn't run, which is usually an antivirus blocking it. Pick DXVK for most graphics cards from 2016 or later, otherwise dgVoodoo2. " + details);
        UpdateApply();
    }

    void GenerateSerial_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !GameClosed(thisFolderOnly: false)) return;
        SetBusy(true);
        try
        {
            Game.GenerateSerial();
            Show(InfoBarSeverity.Success, "A new CD key is registered. You can play online now.");
        }
        catch (Exception ex)
        {
            Game.Log("CD key failed: " + ex);
            closeWhenDone = false;
            Show(InfoBarSeverity.Error, "The CD key could not be registered: " + ex.Message);
        }
        // Only the CD key is read again: the switches keep what the player picked and hasn't applied yet
        ShowSerial();
        SetBusy(false);
    }

    void Guide_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.TroubleshootingPdf));
    void OpenFolder_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.Dir));
    void Discord_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.DiscordUrl));
    void Play_Click(object sender, RoutedEventArgs e) { if (!busy && GameClosed(thisFolderOnly: false)) Run(() => Game.Play(current?.SkipIntro ?? Game.ReadSkipIntro(), "")); }
    void Join_Click(object sender, RoutedEventArgs e) { if (!busy && GameClosed(thisFolderOnly: false)) Run(() => Game.Play(true, Game.ServerAddress)); }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { Show(InfoBarSeverity.Error, ex.Message); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseWhenIdle();
}
