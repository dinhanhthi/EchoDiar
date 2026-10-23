using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace EchoDiar.App.Views;

/// <summary>
/// A settings field's input and its buttons on one line, like a horizontal StackPanel when there is room; a StackPanel
/// never shrinks, so a narrow window clipped the trailing buttons. With <see cref="InputWidth"/> set, the first child is
/// the input: that wide when there is room (always in the card's right column, which gives its content any width),
/// narrower down to <see cref="MinInputWidth"/> when the width is limited (the card wrapped its content below the
/// label). The children that still do not fit go to the next lines. Collapsed children take no room.
/// </summary>
public sealed partial class FieldPanel : Panel
{
    /// <summary>The input's width when there is room; NaN (the default) when there is no input, only buttons.</summary>
    public double InputWidth { get; set; } = double.NaN;

    public double MinInputWidth { get; set; } = 140;

    public double Spacing { get; set; } = 8;

    private bool HasInput => !double.IsNaN(InputWidth) && Children.Count > 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        for (var i = HasInput ? 1 : 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
        }
        if (HasInput) Children[0].Measure(new Size(InputSize(availableSize.Width), availableSize.Height));
        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>The input's width: what the other children leave of <paramref name="available"/>, within its limits.</summary>
    private double InputSize(double available)
    {
        var others = 0.0;
        for (var i = 1; i < Children.Count; i++)
        {
            if (Children[i].Visibility == Visibility.Collapsed) continue;
            others += Spacing + Children[i].DesiredSize.Width;
        }
        var width = Math.Clamp(available - others, Math.Min(MinInputWidth, InputWidth), InputWidth);
        return Math.Max(0, Math.Min(width, available));
    }

    /// <summary>Flows the children into lines within <paramref name="available"/>; arranges them when asked. Returns the size used.</summary>
    private Size Layout(double available, bool arrange)
    {
        var inputWidth = HasInput ? InputSize(available) : 0;
        List<List<(UIElement Child, double Width)>> lines = [];
        List<(UIElement Child, double Width)>? line = null;
        var x = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed) continue;
            var width = HasInput && i == 0 ? inputWidth : child.DesiredSize.Width;
            // Half a pixel of slack: layout rounding may give a little less than the measured width.
            if (line is null || x + Spacing + width > available + 0.5)
            {
                lines.Add(line = []);
                x = width;
            }
            else
            {
                x += Spacing + width;
            }
            line.Add((child, width));
        }

        var used = 0.0;
        var y = 0.0;
        foreach (var items in lines)
        {
            var height = items.Max(item => item.Child.DesiredSize.Height);
            var left = 0.0;
            foreach (var (child, width) in items)
            {
                if (arrange) child.Arrange(new Rect(left, y, width, height));
                left += width + Spacing;
            }
            used = Math.Max(used, left - Spacing);
            y += height + Spacing;
        }
        return new Size(used, lines.Count == 0 ? 0 : y - Spacing);
    }
}
