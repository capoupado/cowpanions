using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cowpanion.Core.Simulation;
using Cowpanion.Core.Text;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Speech bubbles as WPF shapes + TextBlock. One bubble per cow (others queue), global max 4, 6 s dwell then a
/// 400 ms fade, tail flips near the strip edges, follows the cow, biases the cow toward Idle via the simulator.
/// </summary>
internal sealed class BubbleRenderer
{
    private const double DwellSeconds = 6.0;
    private const double FadeInSeconds = 0.15;
    private const double FadeOutSeconds = 0.4;
    private const int MaxVisible = 4;
    private const double TailHeight = 10;
    private const double EdgeMargin = 4;

    private sealed class Bubble
    {
        public required Cow Cow;
        public required Grid Root;
        public required Polygon Tail;
        public required TranslateTransform Move;
        public required TranslateTransform TailMove;
        public double Age;
        public double Width;
        public double Height;
        public bool FadingEarly;
    }

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x40, 0x30, 0x20)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x1E, 0x1A)));
    private static readonly Brush NameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x3A)));
    private static readonly FontFamily Font = new("Segoe UI, Segoe UI Emoji, Segoe UI Symbol");

    private readonly Canvas _canvas;
    private readonly HerdRenderer _herd;
    private readonly List<Bubble> _active = new();
    private readonly Dictionary<Cow, Queue<string>> _queues = new();
    private readonly List<Cow> _scratchCows = new();

    public BubbleRenderer(Canvas canvas, HerdRenderer herd)
    {
        _canvas = canvas;
        _herd = herd;
    }

    public bool AnyVisible => _active.Count > 0;

    public void Enqueue(Cow cow, string text)
    {
        if (!_queues.TryGetValue(cow, out var q))
        {
            q = new Queue<string>();
            _queues[cow] = q;
        }
        if (q.Count < 8)
        {
            q.Enqueue(text);
        }
    }

    public void ClearAll(HerdSimulator sim)
    {
        foreach (var b in _active)
        {
            _canvas.Children.Remove(b.Root);
            sim.SetBubble(b.Cow, false);
        }
        _active.Clear();
        _queues.Clear();
    }

    public void Update(HerdSimulator sim, double dt, double stripWidth)
    {
        // Drop bubbles/queues for cows that are gone or leaving.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var b = _active[i];
            if (b.Cow.Lifecycle == CowLifecycle.Leaving || !Contains(sim.Cows, b.Cow))
            {
                Remove(sim, i);
            }
        }
        _scratchCows.Clear();
        foreach (var kv in _queues)
        {
            if (kv.Key.Lifecycle == CowLifecycle.Leaving || !Contains(sim.Cows, kv.Key))
            {
                _scratchCows.Add(kv.Key);
            }
        }
        for (int i = 0; i < _scratchCows.Count; i++)
        {
            _queues.Remove(_scratchCows[i]);
        }

        // Start queued bubbles: one per cow, global cap.
        if (_active.Count < MaxVisible)
        {
            foreach (var kv in _queues)
            {
                if (_active.Count >= MaxVisible)
                {
                    break;
                }
                if (kv.Value.Count == 0 || HasActive(kv.Key))
                {
                    continue;
                }
                Show(sim, kv.Key, kv.Value.Dequeue());
            }
        }

        // Beyond the cap (can only happen transiently), the oldest fades early.
        while (_active.Count > MaxVisible)
        {
            var oldest = _active[0];
            if (!oldest.FadingEarly)
            {
                oldest.FadingEarly = true;
                oldest.Age = Math.Max(oldest.Age, DwellSeconds);
            }
            break;
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var b = _active[i];
            b.Age += dt;
            if (b.Age >= DwellSeconds + FadeOutSeconds)
            {
                Remove(sim, i);
                continue;
            }
            double opacity = 1.0;
            if (b.Age < FadeInSeconds)
            {
                opacity = b.Age / FadeInSeconds;
            }
            else if (b.Age > DwellSeconds)
            {
                opacity = 1.0 - (b.Age - DwellSeconds) / FadeOutSeconds;
            }
            if (b.Root.Opacity != opacity)
            {
                b.Root.Opacity = Math.Clamp(opacity, 0, 1);
            }
            Position(b, stripWidth);
        }
    }

    private static bool Contains(IReadOnlyList<Cow> cows, Cow cow)
    {
        for (int i = 0; i < cows.Count; i++)
        {
            if (ReferenceEquals(cows[i], cow))
            {
                return true;
            }
        }
        return false;
    }

    private bool HasActive(Cow cow)
    {
        for (int i = 0; i < _active.Count; i++)
        {
            if (ReferenceEquals(_active[i].Cow, cow))
            {
                return true;
            }
        }
        return false;
    }

    private void Show(HerdSimulator sim, Cow cow, string text)
    {
        string wrapped = BubbleText.Wrap(text);
        var body = new TextBlock
        {
            Text = wrapped,
            FontFamily = Font,
            FontSize = 14,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
        };
        var name = new TextBlock
        {
            Text = cow.DisplayName ?? "cow",
            FontFamily = Font,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = NameBrush,
            Margin = new Thickness(0, 0, 0, 1),
        };
        var stack = new StackPanel();
        stack.Children.Add(name);
        stack.Children.Add(body);
        var border = new Border
        {
            Background = Fill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 6),
            Child = stack,
            SnapsToDevicePixels = true,
        };
        var tailMove = new TranslateTransform();
        var tail = new Polygon
        {
            Points = new PointCollection { new Point(0, 0), new Point(14, 0), new Point(5, TailHeight) },
            Fill = Fill,
            Stroke = Stroke,
            StrokeThickness = 1.5,
            RenderTransform = tailMove,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var root = new Grid
        {
            IsHitTestVisible = false,
            Opacity = 0,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TailHeight) });
        Grid.SetRow(border, 0);
        Grid.SetRow(tail, 1);
        root.Children.Add(border);
        root.Children.Add(tail);
        var move = new TranslateTransform();
        root.RenderTransform = move;
        // Cover the border's bottom edge where the tail attaches.
        tail.Margin = new Thickness(0, -1.5, 0, 0);

        _canvas.Children.Add(root);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bubble = new Bubble
        {
            Cow = cow,
            Root = root,
            Tail = tail,
            Move = move,
            TailMove = tailMove,
            Width = root.DesiredSize.Width,
            Height = root.DesiredSize.Height,
        };
        _active.Add(bubble);
        sim.SetBubble(cow, true);
    }

    private void Remove(HerdSimulator sim, int index)
    {
        var b = _active[index];
        _canvas.Children.Remove(b.Root);
        _active.RemoveAt(index);
        if (!HasActive(b.Cow))
        {
            sim.SetBubble(b.Cow, false);
        }
    }

    private void Position(Bubble b, double stripWidth)
    {
        var rect = _herd.CowRect(b.Cow);
        double cowX = rect.X + rect.Width / 2;
        double x = cowX - b.Width / 2;
        x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, stripWidth - b.Width - EdgeMargin));
        double y = rect.Y - b.Height - 2;
        if (y < 0)
        {
            y = 0;
        }
        // The tail points at the cow; near the edges the bubble stays put and the tail slides toward the cow.
        double tailX = Math.Clamp(cowX - x - 7, 8, b.Width - 22);
        b.Move.X = Math.Round(x);
        b.Move.Y = Math.Round(y);
        b.TailMove.X = Math.Round(tailX);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
