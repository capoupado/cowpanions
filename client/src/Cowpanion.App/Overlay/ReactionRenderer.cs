using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cowpanion.Core.Simulation;
using Cowpanion.Net;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Floating emoji reactions: a small burst of glyphs rises from the cow's head, sways, grows slightly and fades.
/// Each glyph is an Image showing a colour bitmap from <see cref="EmojiRasterizer"/> (WPF text cannot draw colour
/// emoji); when the rasteriser is unavailable the glyph falls back to a TextBlock. Elements are pooled and bitmaps come
/// from the rasteriser's cache, so the steady-state tick allocates nothing; at most <see cref="MaxLive"/> glyphs live at once.
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
    private const double GlyphDips = 22;

    private static readonly FontFamily Font = new("Segoe UI Emoji, Segoe UI Symbol");

    private sealed class Glyph
    {
        /// <summary>The element on the canvas: the Image normally, the TextBlock when falling back.</summary>
        public required FrameworkElement Element;
        public required Image Image;
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
    private readonly EmojiRasterizer _emoji;
    private readonly double _dpiScale;
    private readonly Random _rng = new();
    private readonly List<Glyph> _live = new();
    private readonly Stack<Glyph> _pool = new();

    /// <param name="dpiScale">The monitor's DPI scale; bitmaps are rendered at GlyphDips x scale pixels so they stay crisp.</param>
    public ReactionRenderer(Canvas canvas, HerdRenderer herd, EmojiRasterizer emoji, double dpiScale)
    {
        _canvas = canvas;
        _herd = herd;
        _emoji = emoji;
        _dpiScale = dpiScale;
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
            Show(g, clusters[i % clusters.Count]);
            g.Scale.CenterX = g.Width / 2;
            g.Scale.CenterY = g.Height / 2;
            g.Element.Opacity = 0;
            g.Element.Visibility = Visibility.Visible;
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
                if (g.Element.Opacity != 0)
                {
                    g.Element.Opacity = 0;
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
            g.Element.Opacity = Math.Clamp(opacity, 0, 1);
        }
    }

    /// <summary>Puts the emoji on the glyph: colour bitmap in the Image when the rasteriser has one, else text in the TextBlock.</summary>
    private void Show(Glyph g, string emoji)
    {
        BitmapSource? bitmap = _emoji.Render(emoji, GlyphDips, _dpiScale);
        if (bitmap is not null)
        {
            g.Image.Source = bitmap;
            // The bitmap was rendered at GlyphDips x scale pixels; pin the DIP size so layout never rounds it.
            g.Width = bitmap.PixelWidth / _dpiScale;
            g.Height = bitmap.PixelHeight / _dpiScale;
            g.Image.Width = g.Width;
            g.Image.Height = g.Height;
            g.Text.Visibility = Visibility.Collapsed;
            g.Element = g.Image;
            return;
        }
        g.Image.Source = null;
        g.Image.Visibility = Visibility.Collapsed;
        g.Text.Text = emoji;
        g.Text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        g.Width = g.Text.DesiredSize.Width;
        g.Height = g.Text.DesiredSize.Height;
        g.Element = g.Text;
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
        // Both elements share the transforms; only one is visible at a time (see Show).
        var image = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            RenderTransform = group,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Panel.SetZIndex(image, 400);
        _canvas.Children.Add(image);
        var text = new TextBlock
        {
            FontFamily = Font,
            FontSize = GlyphDips,
            IsHitTestVisible = false,
            RenderTransform = group,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(text, 400);
        _canvas.Children.Add(text);
        return new Glyph { Element = image, Image = image, Text = text, Scale = scale, Move = move };
    }

    private void Return(int index)
    {
        var g = _live[index];
        _live.RemoveAt(index);
        g.Cow = null;
        g.Element.Opacity = 0;
        g.Image.Visibility = Visibility.Collapsed;
        g.Text.Visibility = Visibility.Collapsed;
        _pool.Push(g);
    }
}
