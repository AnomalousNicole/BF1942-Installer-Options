using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace BF1942Options;

public sealed partial class MainWindow : Window
{
    Settings? current;
    bool loading;
    const string NotIncluded = "Not included in this installer.";

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    readonly IntPtr hwnd;
    double Scale => GetDpiForWindow(hwnd) / 96.0;

    // Width of the options area the window opens with, and the width below which its two columns become one
    // (only on a screen too small for the whole page), in effective pixels
    const double StartColumnsWidth = 920;
    const double OneColumnBelow = 720;
    const double ArtMinHeight = 300;
    bool oneColumn;
    int pendingFit = 1;   // the page's texts (after ReloadAsync), plus the art when there is one
    SizeInt32 minSize;    // the smallest the window may be, in screen pixels at minDpi (no limit while minDpi is 0)
    uint minDpi;

    // WindowProc sets the smallest size when Windows asks for it (WM_GETMINMAXINFO), for the display scale and the
    // screen the window is on at that moment. OverlappedPresenter's PreferredMinimumWidth/Height can't do that:
    // they are in screen pixels and keep their value on a screen with another scale (microsoft-ui-xaml#10452),
    // and Windows holds the window to them while it resizes it for the new scale, before the app hears of the
    // change, so a window dragged from a 250% screen to a 100% one would stay two and a half times too big.
    delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    const uint WM_GETMINMAXINFO = 0x0024, WM_NCDESTROY = 0x0082, MONITOR_DEFAULTTONEAREST = 2;
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
        GameFolder.Text = Game.Dir;
        ToolTipService.SetToolTip(GameFolder, Game.Dir);
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
        // Also when a message's height settles after it slides in, as the options area gets smaller then
        Scroller.SizeChanged += (_, _) => { ApplyLayout(); KeepAllInView(); };
        Status.SizeChanged += (_, _) => KeepAllInView();

        for (int i = 0; i < Game.FontNames.Length; i++)
            FontSize.Items.Add(new ComboBoxItem
            {
                Content = Game.FontNames[i] + (i == 2 ? " (default)" : "") + " - for " + Game.FontHints[i],
                IsEnabled = Game.FontAvailable(i),
            });
        Guide.Visibility = File.Exists(Game.TroubleshootingPdf) ? Visibility.Visible : Visibility.Collapsed;
        CheckGraphics.IsEnabled = Game.VulkanCheckAvailable;
        if (!Game.BorderlessAvailable)
            BorderlessHint.Text = Environment.Is64BitOperatingSystem ? NotIncluded : "Only available on 64-bit Windows.";
        if (!Game.HiResAvailable) HiResHint.Text = NotIncluded;
        if (!Game.SirenAvailable) SirenHint.Text = NotIncluded;
        if (!Game.CompatAvailable) CompatHint.Text = NotIncluded;
        if (Game.ServerShortcut != "")
            SkipHint.Text += $" The \"{Game.ServerShortcut}\" shortcut always skips them.";
        if (!Game.SerialAllowed)
        {
            // No CD key card: the game fixes card is the last one in the left column and takes up the rest
            SerialCard.Visibility = Visibility.Collapsed;
            LeftColumn.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            LeftColumn.RowDefinitions[2].Height = new GridLength(0);
        }
        DxvkTitle.Text = ("DXVK " + Game.Version("VerDXVK")).Trim();
        DgVoodooTitle.Text = ("dgVoodoo2 " + Game.Version("VerDgVoodoo2")).Trim();
        BF42ppTitle.Text = ("BF42++ " + Game.Version("VerBF42PP")).Trim();
        // The radio buttons' content is a title and a description, so screen readers get the title as the name
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Dxvk, DxvkTitle.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DgVoodoo, DgVoodooTitle.Text);
        var (w, h, r) = Game.PrimaryDisplay();
        ScreenNow.Text = $"Your main screen: {w} x {h} at {r} Hz.";

        Root.Loaded += async (_, _) =>
        {
            if (!Game.Installed)
            {
                Columns.IsHitTestVisible = false;
                Columns.Opacity = 0.5;
                ApplyButton.IsEnabled = false;
                PlayButton.IsEnabled = JoinButton.IsEnabled = false;
                Show(InfoBarSeverity.Error, "BF1942 Options must stay in the folder that its installer created inside the Battlefield 1942 folder.");
            }
            else await ReloadAsync();
            FitWhenReady();
        };
    }

    IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == WM_NCDESTROY) RemoveWindowSubclass(window, windowProc, id);
        IntPtr result = DefSubclassProc(window, message, wParam, lParam);
        if (message == WM_GETMINMAXINFO && minDpi > 0)
        {
            uint dpi = GetDpiForWindow(window);
            int width = (int)Math.Round(minSize.Width * (double)dpi / minDpi);
            int height = (int)Math.Round(minSize.Height * (double)dpi / minDpi);
            // A screen too small for the whole page still takes the window; the options scroll then
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

    // Sizes the window so the whole page shows without scrolling, as long as the screen is big enough
    void FitToContent()
    {
        double scale = Scale;
        double width = Page.ColumnDefinitions[0].Width.Value + StartColumnsWidth + Root.Padding.Left + Root.Padding.Right;
        SetOneColumn(false);
        // The options decide the height. The art on the left shrinks to what is left beside them, but always
        // keeps at least ArtMinHeight, so the buttons below it never push the window taller.
        double side = Page.ColumnDefinitions[0].Width.Value;
        Root.Measure(new Windows.Foundation.Size(width - side, double.PositiveInfinity));
        SideButtons.Measure(new Windows.Foundation.Size(side, double.PositiveInfinity));
        SideFolder.Measure(new Windows.Foundation.Size(side, double.PositiveInfinity));
        double sidebar = ArtMinHeight + SideButtons.DesiredSize.Height + SideFolder.DesiredSize.Height;
        double height = Page.RowDefinitions[0].Height.Value + Math.Max(Root.DesiredSize.Height, sidebar);

        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int cw = Math.Min((int)Math.Ceiling(width * scale), work.Width - 16);
        int ch = Math.Min((int)Math.Ceiling(height * scale), work.Height - 48);
        minDpi = 0;   // no smallest size while the window is fitted
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
            double spare = Scroller.ViewportHeight - Scroller.ExtentHeight;
            spare = Math.Min(spare, Page.ActualHeight - Page.RowDefinitions[0].Height.Value - sidebar);
            int change = (int)Math.Round(spare * Scale);
            if (tries > 0 && Math.Abs(change) >= 2 && AppWindow.Size.Height - change <= work.Height)
            {
                AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, AppWindow.Size.Height - change));
                CorrectHeight(tries - 1, sidebar, work);
                return;
            }
            AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2,
                                          work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));
            minSize = AppWindow.Size;
            minDpi = GetDpiForWindow(hwnd);
        });
    }

    // A long message can make the bottom row taller and take room from the cards; the window then grows by
    // that much (as far as the screen allows), so every card stays in view
    void KeepAllInView()
    {
        if (pendingFit > 0 || oneColumn) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            int grow = (int)Math.Ceiling((Scroller.ExtentHeight - Scroller.ViewportHeight) * Scale);
            RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            grow = Math.Min(grow, work.Height - AppWindow.Size.Height);
            if (grow <= 1) return;
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, AppWindow.Size.Height + grow));
            // It grows downwards, so near the bottom of the screen it moves up by as much as went past it
            int below = AppWindow.Position.Y + AppWindow.Size.Height - (work.Y + work.Height);
            if (below > 0) AppWindow.Move(new PointInt32(AppWindow.Position.X, AppWindow.Position.Y - below));
        });
    }

    // Two columns, or one when the screen is too small for the page. Extra height in a bigger window stays
    // below the cards instead of opening up gaps inside them.
    void ApplyLayout() => SetOneColumn(Scroller.ActualWidth < OneColumnBelow);

    void SetOneColumn(bool one)
    {
        if (one == oneColumn) return;
        oneColumn = one;
        Grid.SetColumn(RightColumn, one ? 0 : 1);
        Grid.SetRow(RightColumn, one ? 1 : 0);
        Columns.ColumnDefinitions[1].Width = one ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Columns.ColumnSpacing = one ? 0 : 16;
        Columns.RowSpacing = one ? 16 : 0;
    }

    async Task ReloadAsync()
    {
        ApplyButton.IsEnabled = false;
        Columns.IsHitTestVisible = false;
        try { current = await Task.Run(Game.Read); }
        catch (Exception ex)
        {
            Game.Log("Reading the game folder failed: " + ex);
            Show(InfoBarSeverity.Error, "The game folder could not be read: " + ex.Message + " Close the game and other programs using it, then reopen this window.");
            return;
        }
        finally { Columns.IsHitTestVisible = current != null; }   // nothing to switch until the state could be read

        loading = true;
        Dxvk.IsChecked = current.Renderer == Renderer.DXVK;
        DgVoodoo.IsChecked = current.Renderer == Renderer.DgVoodoo;
        RendererNow.Text = current.Renderer switch
        {
            Renderer.Unknown => "The graphics files in the game folder are neither DXVK nor dgVoodoo2. Pick one to replace them.",
            Renderer.None => "No graphics fix is installed. Pick one and click Apply.",
            _ => "Installed now: " + Game.RendererName(current.Renderer) + ".",
        };
        bool serial = Game.HasValidSerial();
        SerialNow.Text = serial ? "A valid CD key is registered." : "No valid CD key was found.";
        SerialHint.Text = serial
            ? "Nothing to do. Battlefield 1942 uses the key that is already there."
            : "Battlefield 1942 needs one to play online. Generate a random key; it is removed again when you uninstall the game.";
        GenerateSerial.IsEnabled = !serial;
        BF42pp.IsOn = current.BF42pp;
        Audio.IsOn = current.Audio;
        while (FontSize.Items.Count > Game.FontNames.Length) FontSize.Items.RemoveAt(FontSize.Items.Count - 1);
        if (current.Font < 0) FontSize.Items.Add(new ComboBoxItem { Content = "Another font (not from the installer)" });
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
        loading = false;
        UpdateApply();
    }

    Settings Wanted()
    {
        Settings w = current!.Clone();
        if (Dxvk.IsChecked == true) w.Renderer = Renderer.DXVK;
        else if (DgVoodoo.IsChecked == true) w.Renderer = Renderer.DgVoodoo;
        w.BF42pp = BF42pp.IsOn;
        w.Audio = Audio.IsOn;
        w.Font = FontSize.SelectedIndex >= 0 && FontSize.SelectedIndex < Game.FontNames.Length ? FontSize.SelectedIndex : -1;
        w.HiResUI = HiResUI.IsOn;
        w.Borderless = Borderless.IsOn;
        w.SkipIntro = SkipIntro.IsOn;
        w.NoSiren = NoSiren.IsOn;
        w.Compat = Compat.IsOn;
        return w;
    }

    void UpdateApply()
    {
        if (loading || current == null) return;
        ApplyButton.IsEnabled = !Wanted().SameAs(current);
    }

    void Changed(object sender, RoutedEventArgs e) => UpdateApply();
    void Status_Closed(InfoBar sender, InfoBarClosedEventArgs e)
    {
        Status.Visibility = Visibility.Collapsed;
        FooterNote.Visibility = Visibility.Visible;
    }
    void FontSize_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApply();

    void Show(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.Visibility = Visibility.Visible;
        FooterNote.Visibility = Visibility.Collapsed;
        Status.IsOpen = true;
    }

    bool GameClosed()
    {
        string? running = Game.RunningProgram();
        if (running == null) return true;
        Show(InfoBarSeverity.Warning, running + " is running. Close the game first, then try again.");
        return false;
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        Settings from = current, wanted = Wanted();
        if (wanted.SameAs(from) || !GameClosed()) return;

        ApplyButton.IsEnabled = false;
        try
        {
            Game.Log("Apply");
            await Task.Run(() => Game.Apply(from, wanted));
            Show(InfoBarSeverity.Success, "Done. Your changes are in place for the next time you start the game.");
        }
        catch (Exception ex)
        {
            Game.Log("Failed: " + ex);
            Show(InfoBarSeverity.Error, "Not everything could be changed: " + ex.Message +
                 " If an antivirus blocked it, add an exclusion for the game folder and try again. Running the installer again also puts every fix back.");
        }
        await ReloadAsync();
    }

    async void Resolution_Click(object sender, RoutedEventArgs e)
    {
        if (!GameClosed()) return;
        try
        {
            string mode = await Task.Run(Game.SetDisplayMode);
            Show(InfoBarSeverity.Success, "The game is set to " + mode + ".");
        }
        catch (Exception ex)
        {
            Game.Log("Display mode failed: " + ex);
            Show(InfoBarSeverity.Error, "The resolution could not be changed: " + ex.Message);
        }
    }

    async void CheckGraphics_Click(object sender, RoutedEventArgs e)
    {
        CheckGraphics.IsEnabled = false;
        (int code, string details) result;
        try { result = await Task.Run(() => (Game.RunVulkanCheck(out string d), d)); }
        catch (Exception ex) { result = (-1, ex.Message); }
        CheckGraphics.IsEnabled = true;

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
    }

    async void GenerateSerial_Click(object sender, RoutedEventArgs e)
    {
        if (!GameClosed()) return;
        try
        {
            Game.GenerateSerial();
            Show(InfoBarSeverity.Success, "A new CD key is registered. You can play online now.");
        }
        catch (Exception ex)
        {
            Game.Log("CD key failed: " + ex);
            Show(InfoBarSeverity.Error, "The CD key could not be registered: " + ex.Message);
        }
        await ReloadAsync();
    }

    void Guide_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.TroubleshootingPdf));
    void OpenFolder_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.Dir));
    void Discord_Click(object sender, RoutedEventArgs e) => Run(() => Game.OpenAsUser(Game.DiscordUrl));
    void Play_Click(object sender, RoutedEventArgs e) { if (GameClosed()) Run(() => Game.Play(current?.SkipIntro ?? false, "")); }
    void Join_Click(object sender, RoutedEventArgs e) { if (GameClosed()) Run(() => Game.Play(true, Game.ServerAddress)); }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { Show(InfoBarSeverity.Error, ex.Message); }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
