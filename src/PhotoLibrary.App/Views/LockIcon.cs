using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace PhotoLibrary.App.Views;

/// <summary>A padlock outline, drawn as a path so it needs no icon font or image file.</summary>
public sealed class LockIcon : Avalonia.Controls.Shapes.Path
{
    private static readonly Geometry Shape = Geometry.Parse("M7,11 V8 A5,5 0 0 1 17,8 V11 M5,11 H19 V21 H5 Z M12,15 V17");

    public LockIcon()
    {
        Data = Shape;
        Stretch = Stretch.Uniform;
        StrokeThickness = 2;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        // Follow the light/dark theme's text colour.
        Bind(StrokeProperty, this.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
    }
}
