using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cowpanion.Core.Simulation;
using Cowpanion.Net;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Floating emoji reactions: a small burst of glyphs rises from the cow's head, sways, grows slightly and fades.
/// TextBlocks are pooled so the steady-state tick allocates nothing; at most <see cref="MaxLive"/> glyphs live at once.
/// </summary>
internal sealed class ReactionRenderer
{
    private const int MaxLive = 40;
    private const double LifeSeconds = 2.2;
    private const double FadeSeconds = 0.6;
    private const double StaggerSeconds = 0.12;
    private const double RiseDips = 90;
    private const double SwayDips = 8;
    private const double JitterDips = 18;
    private const double ScaleFrom = 0.8;
    private const double ScaleTo = 1.1;

    private static readonly FontFamily Font = new("Segoe UI Emoji, Segoe UI Symbol");

    private sealed class Glyph
    {
        public required TextBlock Text;
        public required ScaleTransform Scale;
        public required TranslateTransform Move;
        public Cow? Cow;
        public double Age;
        public double Jitter;
        public double Phase;
        public double Width;
        public double Height;
    }

    private readonly Canvas _canvas;
    private readonly HerdRenderer _herd;
    private readonly Random _rng = new();
    private readonly List<Glyph> _live = new();
    private readonly Stack<Glyph> _pool = new();

    public ReactionRenderer(Canvas canvas, HerdRenderer herd)
    {
        _canvas = canvas;
        _herd = herd;
    }

    public bool AnyActive => _live.Count > 0;

    /// <summary>Spawns 4–6 glyphs (more for longer reactions) cycling through the reaction's emoji, staggered.</summary>
    public void Emit(Cow cow, string reaction)
    {
        var clusters = ChatComposer.Graphemes(reaction);
        if (clusters.Count == 0)
        {
            return;
        }
        int n = 3 + Math.Min(clusters.Count, 3);
        for (int i = 0; i < n; i++)
        {
            if (_live.Count >= MaxLive)
            {
                return;
            }
            var g = Rent();
            g.Cow = cow;
            g.Age = -StaggerSeconds * i;
            g.Jitter = (_rng.NextDouble() * 2 - 1) * JitterDips;
            g.Phase = _rng.NextDouble() * Math.PI * 2;
            g.Text.Text = clusters[i % clusters.Count];
            g.Text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            g.Width = g.Text.DesiredSize.Width;
            g.Height = g.Text.DesiredSize.Height;
            g.Scale.CenterX = g.Width / 2;
            g.Scale.CenterY = g.Height / 2;
            g.Text.Opacity = 0;
            g.Text.Visibility = Visibility.Visible;
            _live.Add(g);
        }
    }

    public void Update(double dt)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var g = _live[i];
            g.Age += dt;
            if (g.Age >= LifeSeconds || g.Cow is null)
            {
                Return(i);
                continue;
            }
            if (g.Age < 0)
            {
                // Still waiting for its stagger slot.
                if (g.Text.Opacity != 0)
                {
                    g.Text.Opacity = 0;
                }
                continue;
            }
            double t = g.Age / LifeSeconds;
            var rect = _herd.CowRect(g.Cow);
            double headX = rect.X + rect.Width / 2 + g.Cow.Facing * rect.Width * 0.25;
            double x = headX + g.Jitter + SwayDips * Math.Sin(2 * Math.PI * g.Age * 0.9 + g.Phase) - g.Width / 2;
            double y = rect.Y - g.Height * 0.5 - RiseDips * t;
            double scale = ScaleFrom + (ScaleTo - ScaleFrom) * t;
            double opacity = 1.0;
            double remaining = LifeSeconds - g.Age;
            if (remaining < FadeSeconds)
            {
                opacity = remaining / FadeSeconds;
            }
            else if (g.Age < 0.1)
            {
                opacity = g.Age / 0.1;
            }
            g.Move.X = Math.Round(x);
            g.Move.Y = Math.Round(y);
            g.Scale.ScaleX = scale;
            g.Scale.ScaleY = scale;
            g.Text.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }

    public void ClearAll()
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            Return(i);
        }
    }

    private Glyph Rent()
    {
        if (_pool.Count > 0)
        {
            return _pool.Pop();
        }
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(move);
        var text = new TextBlock
        {
            FontFamily = Font,
            FontSize = 18,
            IsHitTestVisible = false,
            RenderTransform = group,
            Opacity = 0,
        };
        Panel.SetZIndex(text, 400);
        _canvas.Children.Add(text);
        return new Glyph { Text = text, Scale = scale, Move = move };
    }

    private void Return(int index)
    {
        var g = _live[index];
        _live.RemoveAt(index);
        g.Cow = null;
        g.Text.Opacity = 0;
        g.Text.Visibility = Visibility.Collapsed;
        _pool.Push(g);
    }
}
