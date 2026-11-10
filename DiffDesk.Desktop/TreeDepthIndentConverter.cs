using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DiffDesk.Desktop;

public sealed class TreeDepthIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = value is int number ? Math.Clamp(number, 0, 32) : 0;
        return new Thickness(depth * 18, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TreeGuide : FrameworkElement
{
    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
        nameof(Depth), typeof(int), typeof(TreeGuide), new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public int Depth
    {
        get => (int)GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var depth = Math.Clamp(Depth, 0, 32);
        var height = double.IsInfinity(availableSize.Height) ? 27 : Math.Min(27, availableSize.Height);
        return new Size(depth * 18, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var depth = Math.Clamp(Depth, 0, 32);
        if (depth == 0 || ActualHeight <= 0) return;

        var brush = new SolidColorBrush(Color.FromRgb(150, 158, 170));
        brush.Freeze();
        var pen = new Pen(brush, 1) { DashStyle = DashStyles.Dot };
        pen.Freeze();
        var centerY = Math.Round(ActualHeight / 2) + 0.5;
        var width = depth * 18;
        for (var level = 0; level < depth; level++)
        {
            var x = level * 18 + 9.5;
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        }
        drawingContext.DrawLine(pen, new Point((depth - 1) * 18 + 9.5, centerY), new Point(width, centerY));
    }
}
