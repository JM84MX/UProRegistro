using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UProAttendance.Presentation;

public sealed class FingerprintArt : FrameworkElement
{
    private static readonly Geometry[] Ridges =
    [
        Geometry.Parse("M 91,92 C 91,85 96,80 103,80 C 110,80 116,86 116,94 C 116,109 112,124 105,136"),
        Geometry.Parse("M 83,109 C 85,102 86,96 86,90 C 86,81 93,74 103,74 C 114,74 123,82 123,94 C 123,113 117,131 108,146"),
        Geometry.Parse("M 79,125 C 86,114 91,103 91,94 M 97,106 C 98,101 98,97 98,92 C 98,89 100,87 103,87 C 106,87 109,90 109,94 C 109,107 105,121 99,132"),
        Geometry.Parse("M 82,137 C 93,122 102,107 102,94 M 91,143 C 94,139 96,136 98,132"),
        Geometry.Parse("M 71,121 C 76,111 79,101 79,91 C 79,78 90,67 103,67 C 118,67 130,79 130,94 C 130,114 125,134 117,149"),
        Geometry.Parse("M 65,111 C 69,104 71,98 71,91 C 71,74 85,60 103,60 C 122,60 138,75 138,94 C 138,112 134,130 128,143"),
        Geometry.Parse("M 60,99 C 63,95 64,90 65,85 C 68,67 83,53 103,53 C 126,53 145,72 145,94 C 145,106 144,118 141,129"),
        Geometry.Parse("M 59,81 C 65,61 82,46 103,46 C 128,46 149,65 152,90 M 152,98 C 152,103 152,108 151,112"),
        Geometry.Parse("M 66,62 C 75,48 89,39 103,39 C 121,39 137,47 147,61"),
    ];

    private static readonly Pen Inactive = MakePen(Color.FromRgb(211, 208, 203));
    private static readonly Pen Active = MakePen(Color.FromRgb(23, 23, 22));
    private static readonly Rect ArcBounds = new(42, 31, 122, 127);

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(FingerprintArt),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public void ShowAccepted(int samples, int total)
    {
        var target = (double)samples / total;
        var previous = Progress;
        BeginAnimation(ProgressProperty, null);
        Progress = target;
        if (SystemParameters.ClientAreaAnimation)
            BeginAnimation(ProgressProperty, new DoubleAnimation(previous, target, TimeSpan.FromMilliseconds(460))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var scale = Math.Min(ActualWidth / ArcBounds.Width, ActualHeight / ArcBounds.Height);
        if (scale <= 0)
            return;

        drawing.PushTransform(new TranslateTransform((ActualWidth - ArcBounds.Width * scale) / 2,
            (ActualHeight - ArcBounds.Height * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.PushTransform(new TranslateTransform(-ArcBounds.X, -ArcBounds.Y));

        for (var i = 0; i < Ridges.Length; i++)
        {
            drawing.DrawGeometry(null, Inactive, Ridges[i]);
            var intensity = Math.Clamp(Progress * Ridges.Length - i, 0, 1);
            if (intensity <= 0)
                continue;
            drawing.PushOpacity(intensity);
            drawing.DrawGeometry(null, Active, Ridges[i]);
            drawing.Pop();
        }

        drawing.Pop();
        drawing.Pop();
        drawing.Pop();
    }

    private static Pen MakePen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 2.65)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }
}
