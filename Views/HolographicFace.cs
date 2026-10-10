using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ArsanGazERP.Views;

/// <summary>
/// Vector holographic Jarvis face. Modes: IDLE, LISTENING, THINKING, SPEAKING.
/// The mouth animation while speaking is simulated; it is not driven by the audio signal.
/// </summary>
public sealed class HolographicFace : Viewbox
{
    private const double DesignWidth = 200;
    private const double DesignHeight = 230;
    private const int MouthBarCount = 9;

    private static readonly Point[] HeadOutline =
    [
        new(100, 22), new(132, 30), new(152, 52), new(158, 84), new(154, 112), new(146, 138),
        new(132, 164), new(116, 182), new(100, 188), new(84, 182), new(68, 164), new(54, 138),
        new(46, 112), new(42, 84), new(48, 52), new(68, 30)
    ];

    private static readonly (Point From, Point To)[] MeshLines =
    [
        (new(100, 22), new(100, 60)), (new(68, 30), new(80, 70)), (new(132, 30), new(120, 70)),
        (new(80, 70), new(100, 60)), (new(100, 60), new(120, 70)), (new(48, 52), new(80, 70)),
        (new(152, 52), new(120, 70)), (new(42, 84), new(66, 104)), (new(158, 84), new(134, 104)),
        (new(56, 84), new(144, 84)), (new(100, 84), new(92, 124)), (new(100, 84), new(108, 124)),
        (new(92, 124), new(108, 124)), (new(54, 138), new(84, 120)), (new(84, 120), new(92, 124)),
        (new(146, 138), new(116, 120)), (new(116, 120), new(108, 124)), (new(66, 104), new(84, 120)),
        (new(134, 104), new(116, 120)), (new(68, 164), new(86, 142)), (new(132, 164), new(114, 142)),
        (new(86, 142), new(114, 142)), (new(100, 162), new(100, 188)), (new(84, 182), new(100, 162)),
        (new(116, 182), new(100, 162))
    ];

    private readonly Canvas _canvas = new() { Width = DesignWidth, Height = DesignHeight };
    private readonly SolidColorBrush _lineBrush = new(Color.FromRgb(79, 227, 255));
    private readonly SolidColorBrush _meshBrush = new(Color.FromArgb(90, 79, 227, 255));
    private readonly DropShadowEffect _glow = new()
    {
        Color = Color.FromRgb(0, 194, 242),
        BlurRadius = 14,
        ShadowDepth = 0,
        Opacity = 0.9
    };
    private readonly ScaleTransform _eyeScale = new(1, 1);
    private readonly ScaleTransform[] _mouthBars = new ScaleTransform[MouthBarCount];
    private readonly TextBlock _modeLabel = new()
    {
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Width = DesignWidth
    };

    public HolographicFace()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;
        _modeLabel.Foreground = _lineBrush;
        BuildRings();
        BuildHead();
        BuildEyes();
        BuildMouth();
        BuildScanLine();
        Canvas.SetTop(_modeLabel, 208);
        _canvas.Children.Add(_modeLabel);
        SetMode("IDLE");
    }

    public string Mode { get; private set; } = "IDLE";

    public void SetMode(string mode)
    {
        Mode = mode;
        (Color glow, string label) = mode switch
        {
            "LISTENING" => (Color.FromRgb(53, 212, 255), "DİNLİYOR"),
            "THINKING" => (Color.FromRgb(140, 160, 255), "DÜŞÜNÜYOR"),
            "SPEAKING" => (Color.FromRgb(139, 242, 255), "KONUŞUYOR"),
            _ => (Color.FromRgb(0, 194, 242), "BEKLEMEDE")
        };
        _glow.Color = glow;
        _lineBrush.Color = glow;
        _meshBrush.Color = Color.FromArgb(90, glow.R, glow.G, glow.B);
        _modeLabel.Text = $"J.A.R.V.I.S · {label}";
        _glow.BeginAnimation(
            DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(10, mode == "THINKING" ? 26 : 18, TimeSpan.FromSeconds(mode == "THINKING" ? 0.5 : 1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        AnimateMouth(mode);
    }

    private void BuildRings()
    {
        AddRotatingRing(196, 1.2, new DoubleCollection { 1, 4 }, 30, clockwise: true, opacity: 0.55);
        AddRotatingRing(182, 2.2, new DoubleCollection { 18, 10, 4, 10 }, 22, clockwise: false, opacity: 0.35);
    }

    private void AddRotatingRing(double size, double thickness, DoubleCollection dashes, double seconds, bool clockwise, double opacity)
    {
        RotateTransform rotation = new();
        Ellipse ring = new()
        {
            Width = size,
            Height = size,
            Stroke = _lineBrush,
            StrokeThickness = thickness,
            StrokeDashArray = dashes,
            Opacity = opacity,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotation
        };
        Canvas.SetLeft(ring, (DesignWidth - size) / 2);
        Canvas.SetTop(ring, 105 - size / 2);
        _canvas.Children.Add(ring);
        rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(clockwise ? 0 : 360, clockwise ? 360 : 0, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void BuildHead()
    {
        Polygon head = new()
        {
            Points = new PointCollection(HeadOutline),
            Stroke = _lineBrush,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = new LinearGradientBrush(
                Color.FromArgb(46, 0, 194, 242),
                Color.FromArgb(10, 0, 194, 242),
                90),
            Effect = _glow
        };
        _canvas.Children.Add(head);

        foreach ((Point from, Point to) in MeshLines)
        {
            _canvas.Children.Add(new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = _meshBrush,
                StrokeThickness = 0.8
            });
        }

        foreach (Point vertex in HeadOutline)
        {
            Ellipse node = new() { Width = 3, Height = 3, Fill = _lineBrush, Opacity = 0.8 };
            Canvas.SetLeft(node, vertex.X - 1.5);
            Canvas.SetTop(node, vertex.Y - 1.5);
            _canvas.Children.Add(node);
        }
    }

    private void BuildEyes()
    {
        foreach (double left in new[] { 66.0, 106.0 })
        {
            Ellipse eye = new()
            {
                Width = 28,
                Height = 9,
                Fill = new RadialGradientBrush(Colors.White, Color.FromRgb(0, 194, 242)),
                Effect = new DropShadowEffect { Color = Color.FromRgb(120, 235, 255), BlurRadius = 12, ShadowDepth = 0 },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _eyeScale
            };
            Canvas.SetLeft(eye, left);
            Canvas.SetTop(eye, 92);
            _canvas.Children.Add(eye);
        }

        DoubleAnimationUsingKeyFrames blink = new()
        {
            Duration = TimeSpan.FromSeconds(4.2),
            RepeatBehavior = RepeatBehavior.Forever
        };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.9))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.02))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.2))));
        _eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
    }

    private void BuildMouth()
    {
        const double barWidth = 4;
        const double gap = 3;
        double start = (DesignWidth - (MouthBarCount * barWidth + (MouthBarCount - 1) * gap)) / 2;
        for (int index = 0; index < MouthBarCount; index++)
        {
            ScaleTransform scale = new(1, 0.15);
            Rectangle bar = new()
            {
                Width = barWidth,
                Height = 18,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = _lineBrush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            Canvas.SetLeft(bar, start + index * (barWidth + gap));
            Canvas.SetTop(bar, 141);
            _canvas.Children.Add(bar);
            _mouthBars[index] = scale;
        }
    }

    private void AnimateMouth(string mode)
    {
        for (int index = 0; index < MouthBarCount; index++)
        {
            ScaleTransform scale = _mouthBars[index];
            double distanceFromCenter = Math.Abs(index - (MouthBarCount - 1) / 2.0) / ((MouthBarCount - 1) / 2.0);
            double envelope = 1 - distanceFromCenter * 0.6;
            AnimationTimeline animation = mode switch
            {
                "SPEAKING" => SpeakingBar(index, envelope),
                "LISTENING" => new DoubleAnimation(0.15, 0.15 + 0.35 * envelope, TimeSpan.FromSeconds(0.6))
                {
                    BeginTime = TimeSpan.FromMilliseconds(index * 70),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase()
                },
                _ => new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(250))
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }

    private static DoubleAnimationUsingKeyFrames SpeakingBar(int index, double envelope)
    {
        double[] pattern = [0.3, 0.9, 0.5, 1.0, 0.25, 0.75, 0.4, 0.95, 0.35];
        DoubleAnimationUsingKeyFrames frames = new() { RepeatBehavior = RepeatBehavior.Forever };
        double time = 0;
        for (int step = 0; step < pattern.Length; step++)
        {
            double value = 0.15 + pattern[(step + index * 2) % pattern.Length] * envelope;
            frames.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
            time += 0.09 + (index % 3) * 0.015;
        }
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.15 + pattern[(index * 2) % pattern.Length] * envelope, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        return frames;
    }

    private void BuildScanLine()
    {
        TranslateTransform move = new();
        Rectangle scan = new()
        {
            Width = DesignWidth,
            Height = 10,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0, 120, 235, 255), 0),
                    new(Color.FromArgb(120, 120, 235, 255), 0.5),
                    new(Color.FromArgb(0, 120, 235, 255), 1)
                },
                90),
            RenderTransform = move,
            IsHitTestVisible = false
        };
        Canvas scanLayer = new()
        {
            Width = DesignWidth,
            Height = DesignHeight,
            Clip = new PathGeometry
            {
                Figures =
                {
                    new PathFigure(HeadOutline[0], HeadOutline.Skip(1).Select(point => (PathSegment)new LineSegment(point, false)), true)
                }
            },
            IsHitTestVisible = false
        };
        scanLayer.Children.Add(scan);
        _canvas.Children.Add(scanLayer);
        move.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(16, 186, TimeSpan.FromSeconds(3.2))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }
}
