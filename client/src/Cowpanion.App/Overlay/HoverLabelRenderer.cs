using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cowpanion.Core.Simulation;

namespace Cowpanion.App.Overlay;

/// <summary>
/// The hover name tag: one reusable Border+TextBlock on the bubble canvas, shown centred above the cow the cursor has
/// rested on for <see cref="ShowAfterSeconds"/>, faded in and out over 120 ms. Hidden while that cow has a bubble
/// (the bubble already names it) or is leaving. Read-only: never hit-test visible, the window stays click-through.
/// </summary>
internal sealed class HoverLabelRenderer
{
    public const double ShowAfterSeconds = 0.4;
    private const double FadeSeconds = 0.12;
    private const double EdgeMargin = 4;

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x40, 0x30, 0x20)));
    private static readonly Brush NameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x3A)));
    private static readonly FontFamily Font = new("Segoe UI, Segoe UI Emoji, Segoe UI Symbol");

    private readonly Canvas _canvas;
    private readonly HerdRenderer _herd;
    private readonly Border _root;
    private readonly TextBlock _text;
    private readonly TranslateTransform _move = new();
    private Cow? _cow;
    private string _shownText = "";
    private double _opacity;

    public HoverLabelRenderer(Canvas canvas, HerdRenderer herd)
    {
        _canvas = canvas;
        _herd = herd;
        _text = new TextBlock
        {
            FontFamily = Font,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = NameBrush,
        };
        _root = new Border
        {
            Background = Fill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 3),
            Child = _text,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            RenderTransform = _move,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_root, 500);
        _canvas.Children.Add(_root);
    }

    /// <summary>True while the tag is showing or fading (keeps the tick at active fps for smooth fades).</summary>
    public bool IsVisible => _opacity > 0 || _cow is not null;

    public void Update(HerdSimulator sim, double dt, double stripWidth)
    {
        Cow? target = null;
        var cows = sim.Cows;
        for (int i = 0; i < cows.Count; i++)
        {
            var c = cows[i];
            if (c.Hovered && c.HoverSeconds >= ShowAfterSeconds && !c.HasBubble && c.Lifecycle != CowLifecycle.Leaving)
            {
                target = c;
                break;
            }
        }

        if (target is not null)
        {
            _cow = target;
            string name = target.DisplayName ?? "cow";
            string text = target.IsSelf ? name + " (you)" : name;
            if (!string.Equals(text, _shownText, StringComparison.Ordinal))
            {
                _shownText = text;
                _text.Text = text;
                _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            _opacity = Math.Min(1, _opacity + dt / FadeSeconds);
        }
        else
        {
            _opacity = Math.Max(0, _opacity - dt / FadeSeconds);
            if (_opacity <= 0)
            {
                _cow = null;
            }
        }

        if (_opacity <= 0)
        {
            if (_root.Visibility != Visibility.Collapsed)
            {
                _root.Visibility = Visibility.Collapsed;
                _root.Opacity = 0;
            }
            return;
        }

        if (_root.Visibility != Visibility.Visible)
        {
            _root.Visibility = Visibility.Visible;
        }
        if (_root.Opacity != _opacity)
        {
            _root.Opacity = _opacity;
        }
        if (_cow is not null)
        {
            var rect = _herd.CowRect(_cow);
            double w = _root.DesiredSize.Width;
            double h = _root.DesiredSize.Height;
            double x = rect.X + rect.Width / 2 - w / 2;
            x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, stripWidth - w - EdgeMargin));
            double y = Math.Max(0, rect.Y - h - 2);
            _move.X = Math.Round(x);
            _move.Y = Math.Round(y);
        }
    }

    public void Clear()
    {
        _cow = null;
        _opacity = 0;
        _root.Opacity = 0;
        _root.Visibility = Visibility.Collapsed;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
