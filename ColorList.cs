// The buddy colours on the BF42++ settings tab (a PlusKind.Colors setting): a swatch per colour, in the order BF42++
// hands them out when ADD BUDDY is clicked again and again, and + to add one from a palette or any colour from a
// colour picker. Clicking a swatch moves it or takes it out. Players see the colours and their order instead of
// having to type colour names.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BF1942Options;

sealed class ColorList
{
    // More would push the swatches into the setting's name in a narrow column; BF42++'s default has four
    const int Most = 8;
    const double Swatch = 18;
    // Colours that stand out on the scoreboard, BF42++'s defaults first
    static readonly string[] Palette =
    {
        "lime", "dodgerblue", "yellow", "blueviolet", "red", "orange",
        "deeppink", "cyan", "white", "gold", "hotpink", "springgreen",
        "tomato", "violet", "deepskyblue", "chartreuse", "magenta", "coral",
    };

    readonly List<string> colors = new();
    readonly StackPanel swatches = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly Button add;
    readonly Flyout addFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    readonly ColorPicker picker = new()
    {
        IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false,
        IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Box, Visibility = Visibility.Collapsed,
    };
    readonly string name;

    // When the player adds, moves or takes out a colour (not when Value is set)
    public event Action? Changed;

    // What the window shows: the swatches and +. A panel rather than a UserControl subclass, which the trimmed app
    // can't lay out (InvalidCastException in Measure).
    public readonly StackPanel View;

    public ColorList(string name)
    {
        this.name = name;
        add = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 28, Height = 28, Padding = new Thickness(0), MinWidth = 0,
            Flyout = addFlyout,
        };
        AutomationProperties.SetName(add, "Add a " + name.ToLowerInvariant().TrimEnd('s'));
        ToolTipService.SetToolTip(add, "Add a colour");
        addFlyout.Content = MakePalette();
        addFlyout.Closed += (_, _) => picker.Visibility = Visibility.Collapsed;
        View = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { swatches, add } };
    }

    // The colours as bf42++.ini has them, separated by spaces
    public string Value
    {
        get => string.Join(' ', colors);
        set
        {
            colors.Clear();
            colors.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(PlusColors.IsColor));
            Show();
        }
    }

    static SolidColorBrush Brush(string color)
    {
        uint rgb = PlusColors.Rgb(color);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    static Border Chip(string color, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(3), Background = Brush(color),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)), BorderThickness = new Thickness(1),
    };

    void Show()
    {
        swatches.Children.Clear();
        for (int i = 0; i < colors.Count; i++)
        {
            int at = i;
            string color = colors[i];
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Move left", "", at > 0, () => Move(at, at - 1)));
            menu.Items.Add(Item("Move right", "", at < colors.Count - 1, () => Move(at, at + 1)));
            // BF42++ falls back to its own colours when the list is empty, so the last one stays
            menu.Items.Add(Item("Take out", "", colors.Count > 1, () => { colors.RemoveAt(at); Done(); }));
            var swatch = new Button
            {
                Content = Chip(color, Swatch), Padding = new Thickness(4), MinWidth = 0, Flyout = menu,
                VerticalAlignment = VerticalAlignment.Center,
            };
            string what = Ordinal(at + 1) + ": " + color;
            ToolTipService.SetToolTip(swatch, what + ". Click to move it or take it out.");
            AutomationProperties.SetName(swatch, name + ", " + what);
            swatches.Children.Add(swatch);
        }
        add.IsEnabled = colors.Count < Most;
    }

    static MenuFlyoutItem Item(string text, string glyph, bool enabled, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    void Move(int from, int to)
    {
        (colors[from], colors[to]) = (colors[to], colors[from]);
        Done();
    }

    void Add(string color)
    {
        addFlyout.Hide();
        if (colors.Count >= Most) return;
        colors.Add(color);
        Done();
    }

    void Done()
    {
        Show();
        Changed?.Invoke();
    }

    // The palette, six to a row, and Other colour... for the colour picker
    StackPanel MakePalette()
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        const int perRow = 6;
        for (int c = 0; c < perRow; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < (Palette.Length + perRow - 1) / perRow; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < Palette.Length; i++)
        {
            string color = Palette[i];
            var b = new Button { Content = Chip(color, 24), Padding = new Thickness(3), MinWidth = 0 };
            b.Click += (_, _) => Add(color);
            ToolTipService.SetToolTip(b, color);
            AutomationProperties.SetName(b, color);
            Grid.SetRow(b, i / perRow);
            Grid.SetColumn(b, i % perRow);
            grid.Children.Add(b);
        }
        var other = new HyperlinkButton { Content = "Other colour...", Padding = new Thickness(0) };
        var addPicked = new Button { Content = "Add this colour", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right };
        other.Click += (_, _) =>
        {
            bool open = picker.Visibility == Visibility.Collapsed;
            picker.Visibility = addPicked.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        };
        picker.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => addPicked.Visibility = picker.Visibility);
        addPicked.Click += (_, _) => Add($"#{picker.Color.R:x2}{picker.Color.G:x2}{picker.Color.B:x2}");
        return new StackPanel { Spacing = 10, Children = { grid, other, picker, addPicked } };
    }
}
