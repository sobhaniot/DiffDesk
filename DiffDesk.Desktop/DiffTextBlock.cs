using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiffDesk.Desktop;

public sealed class DiffTextBlock : TextBlock
{
    public static readonly DependencyProperty TextValueProperty = DependencyProperty.Register(nameof(TextValue), typeof(string), typeof(DiffTextBlock), new FrameworkPropertyMetadata(string.Empty, OnDiffChanged));
    public static readonly DependencyProperty CompareValueProperty = DependencyProperty.Register(nameof(CompareValue), typeof(string), typeof(DiffTextBlock), new FrameworkPropertyMetadata(string.Empty, OnDiffChanged));
    public static readonly DependencyProperty IsChangedProperty = DependencyProperty.Register(nameof(IsChanged), typeof(bool), typeof(DiffTextBlock), new FrameworkPropertyMetadata(false, OnDiffChanged));
    public static readonly DependencyProperty SideIsLeftProperty = DependencyProperty.Register(nameof(SideIsLeft), typeof(bool), typeof(DiffTextBlock), new FrameworkPropertyMetadata(false, OnDiffChanged));

    public string TextValue { get => (string)GetValue(TextValueProperty); set => SetValue(TextValueProperty, value); }
    public string CompareValue { get => (string)GetValue(CompareValueProperty); set => SetValue(CompareValueProperty, value); }
    public bool IsChanged { get => (bool)GetValue(IsChangedProperty); set => SetValue(IsChangedProperty, value); }
    public bool SideIsLeft { get => (bool)GetValue(SideIsLeftProperty); set => SetValue(SideIsLeftProperty, value); }

    private static void OnDiffChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is DiffTextBlock block) block.RefreshInlines();
    }

    private void RefreshInlines()
    {
        Inlines.Clear();
        var value = TextValue ?? string.Empty;
        if (!IsChanged || value.Length == 0)
        {
            Inlines.Add(new Run(value));
            return;
        }

        var other = CompareValue ?? string.Empty;
        var prefix = 0;
        while (prefix < value.Length && prefix < other.Length && value[prefix] == other[prefix]) prefix++;
        var suffix = 0;
        while (suffix < value.Length - prefix && suffix < other.Length - prefix && value[value.Length - 1 - suffix] == other[other.Length - 1 - suffix]) suffix++;
        if (prefix > 0) Inlines.Add(new Run(value[..prefix]));

        var changedLength = value.Length - prefix - suffix;
        if (changedLength > 0)
        {
            var key = SideIsLeft ? "DiffLeftBrush" : "DiffRightBrush";
            var background = TryFindResource(key) as Brush;
            Inlines.Add(new Run(value.Substring(prefix, changedLength))
            {
                Background = background,
                FontWeight = FontWeights.SemiBold
            });
        }

        if (suffix > 0) Inlines.Add(new Run(value[^suffix..]));
    }
}
