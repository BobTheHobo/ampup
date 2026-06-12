using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AmpUp.Controls;

/// <summary>
/// Compact effect picker — trigger row styled like ActionPicker, but the flyout
/// is a multi-column grid of effect chips instead of one long list (27 effects
/// as a single column overflows the screen). Flips upward when there isn't
/// enough room below the trigger. Uses a borderless Window flyout, not a Popup,
/// per the project's WPF pitfalls.
/// </summary>
public class EffectGridPicker : Border
{
    private const int Columns = 4;
    private const double ChipWidth = 104;
    private const double ChipHeight = 30;
    private const double GridPadding = 8;

    private readonly TextBlock _iconBlock;
    private readonly TextBlock _displayText;
    private readonly TextBlock _chevron;

    private Window? _flyout;
    private bool _isOpen;
    private int _selectedIndex = -1;
    private readonly List<(string Display, string Value, string Icon, Color Color, string Tooltip)> _items = new();

    public event EventHandler? SelectionChanged;

    public string SelectedValue => _selectedIndex >= 0 ? _items[_selectedIndex].Value : "none";

    public EffectGridPicker()
    {
        this.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
        this.SetResourceReference(Border.BorderBrushProperty, "BgDarkBrush");
        BorderThickness = new Thickness(1.5);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(8, 0, 8, 0);
        Height = 36;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _iconBlock = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        Grid.SetColumn(_iconBlock, 0);
        grid.Children.Add(_iconBlock);

        _displayText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_displayText, 1);
        grid.Children.Add(_displayText);

        _chevron = new TextBlock
        {
            Text = "▾",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            Margin = new Thickness(7, 0, 0, 0),
        };
        Grid.SetColumn(_chevron, 2);
        grid.Children.Add(_chevron);

        Child = grid;

        MouseLeftButtonUp += (_, _) =>
        {
            if (_isOpen) CloseFlyout();
            else OpenFlyout();
        };
        MouseEnter += (_, _) => { if (!_isOpen) BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, ThemeManager.Accent.R, ThemeManager.Accent.G, ThemeManager.Accent.B)); };
        MouseLeave += (_, _) => { if (!_isOpen) this.SetResourceReference(Border.BorderBrushProperty, "BgDarkBrush"); };
        Unloaded += (_, _) => CloseFlyout();
    }

    public void AddItem(string display, string value, string icon, Color color, string tooltip)
    {
        _items.Add((display, value, icon, color, tooltip));
        if (_selectedIndex < 0)
            SetSelectedIndex(0, fireEvent: false);
    }

    public void Select(string value)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Value == value)
            {
                SetSelectedIndex(i, fireEvent: false);
                return;
            }
        }
        SetSelectedIndex(0, fireEvent: false);
    }

    private void SetSelectedIndex(int index, bool fireEvent)
    {
        if (index < 0 || index >= _items.Count) return;
        _selectedIndex = index;
        var item = _items[index];
        _iconBlock.Text = item.Icon;
        _iconBlock.Foreground = new SolidColorBrush(item.Color);
        _displayText.Text = item.Display;
        if (fireEvent)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Flyout ───────────────────────────────────────────────────────

    private void OpenFlyout()
    {
        var accent = ThemeManager.Accent;

        var wrap = new WrapPanel
        {
            Width = Columns * (ChipWidth + 4),
            Orientation = Orientation.Horizontal,
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int capturedIdx = i;
            var item = _items[i];
            bool selected = i == _selectedIndex;

            var chipText = new TextBlock
            {
                Text = item.Display,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = selected
                    ? new SolidColorBrush(accent)
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chipRow.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontSize = 12,
                Width = 18,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(item.Color),
                Margin = new Thickness(0, 0, 5, 0),
            });
            chipRow.Children.Add(chipText);

            var chip = new Border
            {
                Width = ChipWidth,
                Height = ChipHeight,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2),
                Padding = new Thickness(7, 0, 5, 0),
                Cursor = Cursors.Hand,
                Background = selected
                    ? new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromArgb(0x77, accent.R, accent.G, accent.B))
                    : Brushes.Transparent,
                ToolTip = string.IsNullOrEmpty(item.Tooltip) ? null : item.Tooltip,
                Child = chipRow,
            };
            chip.MouseEnter += (_, _) =>
            {
                if (capturedIdx != _selectedIndex)
                {
                    chip.Background = new SolidColorBrush(Color.FromArgb(0x14, accent.R, accent.G, accent.B));
                    chipText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                }
            };
            chip.MouseLeave += (_, _) =>
            {
                if (capturedIdx != _selectedIndex)
                {
                    chip.Background = Brushes.Transparent;
                    chipText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                }
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                SetSelectedIndex(capturedIdx, fireEvent: true);
                CloseFlyout();
            };
            wrap.Children.Add(chip);
        }

        var popupBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B)),
            Padding = new Thickness(GridPadding),
            Child = wrap,
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");

        var outerBorder = new Border { Child = popupBorder };
        outerBorder.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");

        _flyout = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = true,
            Background = (Brush)Application.Current.FindResource("BgDarkBrush"),
            Content = outerBorder,
        };

        // The grid has a fixed deterministic size, so we can decide up front
        // whether it fits below the trigger or needs to open upward.
        int rows = (int)Math.Ceiling(_items.Count / (double)Columns);
        double flyoutHeight = rows * (ChipHeight + 4) + GridPadding * 2 + 2;
        double flyoutWidth = Columns * (ChipWidth + 4) + GridPadding * 2 + 2;

        var belowPos = PointToScreen(new Point(0, ActualHeight + 2)); // physical px
        var abovePos = PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(this);
        double dpiX = 1.0, dpiY = 1.0;
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
        }

        var workArea = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)belowPos.X, (int)belowPos.Y)).WorkingArea;
        bool openUp = belowPos.Y + flyoutHeight * dpiY > workArea.Bottom;

        double left = belowPos.X / dpiX;
        double top = openUp
            ? abovePos.Y / dpiY - flyoutHeight - 2
            : belowPos.Y / dpiY;
        // Keep the grid on-screen horizontally too
        double workRight = workArea.Right / dpiX;
        if (left + flyoutWidth > workRight) left = Math.Max(workArea.Left / dpiX, workRight - flyoutWidth);

        _flyout.Left = left;
        _flyout.Top = top;
        _flyout.Deactivated += (_, _) => CloseFlyout();
        _flyout.KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseFlyout(); };

        // Slide + fade in (slides up when opening upward)
        var translate = new TranslateTransform(0, openUp ? 8 : -8);
        popupBorder.RenderTransform = translate;
        popupBorder.Opacity = 0;

        _flyout.Show();
        _isOpen = true;

        var slideAnim = new DoubleAnimation(openUp ? 8 : -8, 0, new Duration(TimeSpan.FromMilliseconds(120)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        popupBorder.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120))));

        BorderBrush = new SolidColorBrush(accent);
        _chevron.Foreground = new SolidColorBrush(accent);
    }

    private void CloseFlyout()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _flyout?.Close();
        _flyout = null;
        this.SetResourceReference(Border.BorderBrushProperty, "BgDarkBrush");
        _chevron.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    }
}
