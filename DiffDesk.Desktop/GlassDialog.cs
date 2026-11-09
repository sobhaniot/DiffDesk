using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DiffDesk.Desktop;

internal enum GlassDialogTone
{
    Info,
    Success,
    Warning,
    Error
}

internal sealed class GlassDialog : Window
{
    private readonly bool _confirmation;

    private GlassDialog(string title, string message, GlassDialogTone tone, bool confirmation)
    {
        _confirmation = confirmation;
        Title = title;
        Width = 530;
        Height = Math.Clamp(228 + message.Split('\n').Length * 20, 278, 500);
        MinHeight = 278;
        MaxHeight = 500;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FlowDirection = FlowDirection.RightToLeft;
        Foreground = new SolidColorBrush(Color.FromRgb(244, 242, 250));
        ShowActivated = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && _confirmation) { DialogResult = false; e.Handled = true; }
        };

        var rim = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            Background = new LinearGradientBrush(Color.FromArgb(242, 37, 30, 57), Color.FromArgb(247, 19, 24, 42), 90),
            Effect = new DropShadowEffect { Color = Color.FromRgb(8, 7, 18), Opacity = .48, BlurRadius = 30, ShadowDepth = 0 }
        };
        var layout = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(56, ToneColor(tone).R, ToneColor(tone).G, ToneColor(tone).B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, ToneColor(tone).R, ToneColor(tone).G, ToneColor(tone).B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = ToneGlyph(tone), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ToneColor(tone)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        header.Children.Add(badge);
        var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0), Foreground = Brushes.White };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);
        var close = new Button { Content = "×", Width = 30, Height = 30, FontSize = 19, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), Foreground = new SolidColorBrush(Color.FromRgb(215, 215, 228)), BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
        close.Click += (_, _) => { if (_confirmation) DialogResult = false; else Close(); };
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var messageSurface = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(15, 12, 15, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            Background = new LinearGradientBrush(Color.FromArgb(45, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(224, 225, 237)), FlowDirection = FlowDirection.RightToLeft, TextAlignment = TextAlignment.Right } }
        };
        Grid.SetRow(messageSurface, 1);
        layout.Children.Add(messageSurface);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        if (confirmation)
        {
            var accept = MakeButton("ادامه", primary: true);
            accept.IsDefault = true;
            accept.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(accept);
            var cancel = MakeButton("انصراف", primary: false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(cancel);
        }
        else
        {
            var ok = MakeButton("متوجه شدم", primary: true);
            ok.IsDefault = true;
            ok.IsCancel = true;
            ok.Click += (_, _) => Close();
            buttons.Children.Add(ok);
        }
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        rim.Child = layout;
        Content = rim;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        new GlassDialog(title, message, GlassDialogTone.Warning, confirmation: true) { Owner = owner }.ShowDialog() == true;

    public static void Show(Window owner, string title, string message, GlassDialogTone tone = GlassDialogTone.Info) =>
        new GlassDialog(title, message, tone, confirmation: false) { Owner = owner }.ShowDialog();

    private static Button MakeButton(string label, bool primary) => new()
    {
        Content = label,
        MinWidth = 105,
        Height = 38,
        Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(14, 0, 14, 0),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Cursor = System.Windows.Input.Cursors.Hand,
        Foreground = Brushes.White,
        Background = primary
            ? new LinearGradientBrush(Color.FromRgb(206, 83, 157), Color.FromRgb(122, 46, 111), 0)
            : new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(primary ? (byte)140 : (byte)60, 255, 255, 255)),
        BorderThickness = new Thickness(1)
    };

    private static Color ToneColor(GlassDialogTone tone) => tone switch
    {
        GlassDialogTone.Success => Color.FromRgb(78, 211, 160),
        GlassDialogTone.Warning => Color.FromRgb(255, 190, 92),
        GlassDialogTone.Error => Color.FromRgb(255, 107, 107),
        _ => Color.FromRgb(214, 91, 159)
    };

    private static string ToneGlyph(GlassDialogTone tone) => tone switch
    {
        GlassDialogTone.Success => "✓",
        GlassDialogTone.Warning => "!",
        GlassDialogTone.Error => "×",
        _ => "i"
    };
}
