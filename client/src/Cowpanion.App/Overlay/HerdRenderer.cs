using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Cowpanion.Core.Simulation;
using Cowpanion.Core.Sprites;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Draws the herd onto a Canvas by writing transforms directly. One <see cref="CowVisual"/> per cow, created on
/// spawn and removed on despawn; the per-tick path allocates nothing and touches only what changed.
/// Lanes: a cow's <see cref="Cow.Position"/>.Y is a depth offset — the cow is drawn that many DIPs higher and behind
/// front-lane cows (z-index). Emotes are procedural: Jump adds a vertical hop, Spin flips the facing rapidly.
/// </summary>
internal sealed class HerdRenderer
{
    private const double JumpHeightDips = 22;
    private const double SpinFlipSeconds = 0.125;
    private const int BaseZ = 100;

    private sealed class CowVisual
    {
        public required Cow Cow;
        public required Image Image;
        public required ScaleTransform Flip;
        public required TranslateTransform Move;
        public required Ellipse SelfMarker;
        public required TranslateTransform MarkerMove;
        public BitmapSource[]? Frames;
        public SpriteAnimation? Anim;
        public CowState LastState = (CowState)(-1);
        public int LastIdleVariant = -1;
        public string? LastVariant;
        public int LastFrame = -1;
        public int LastZ = int.MinValue;
        public int Stamp;
    }

    private readonly Canvas _canvas;
    private readonly SpriteLibrary _sprites;
    private readonly List<CowVisual> _visuals = new();
    private readonly Dictionary<Cow, CowVisual> _byCow = new();
    private readonly double _groundY;
    private int _scale;
    private int _stamp;

    public HerdRenderer(Canvas canvas, SpriteLibrary sprites, int scale, double groundY)
    {
        _canvas = canvas;
        _sprites = sprites;
        _scale = Math.Max(1, scale);
        _groundY = groundY;
    }

    public int Scale => _scale;

    public double CowWidthDips => _sprites.Manifest.FrameWidth * _scale;

    public double CowHeightDips => _sprites.Manifest.FrameHeight * _scale;

    public double GroundY => _groundY;

    public void SetScale(int scale)
    {
        scale = Math.Max(1, scale);
        if (scale == _scale)
        {
            return;
        }
        _scale = scale;
        foreach (var v in _visuals)
        {
            ApplySize(v);
        }
    }

    public void Render(HerdSimulator sim)
    {
        _stamp++;
        var cows = sim.Cows;
        for (int i = 0; i < cows.Count; i++)
        {
            var cow = cows[i];
            if (!_byCow.TryGetValue(cow, out var visual))
            {
                visual = CreateVisual(cow);
            }
            visual.Stamp = _stamp;
            Update(visual);
        }
        for (int i = _visuals.Count - 1; i >= 0; i--)
        {
            if (_visuals[i].Stamp != _stamp)
            {
                var v = _visuals[i];
                _canvas.Children.Remove(v.Image);
                _canvas.Children.Remove(v.SelfMarker);
                _byCow.Remove(v.Cow);
                _visuals.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Screen-space (strip DIP) rectangle of a cow's sprite, for bubbles, chat input placement and hit testing.
    /// Follows the lane depth (so everything attached to the cow moves with it) but not the Jump hop.
    /// </summary>
    public Rect CowRect(Cow cow)
    {
        double w = CowWidthDips;
        double h = CowHeightDips;
        return new Rect(cow.Position.X - w / 2, _groundY - h - Math.Round(cow.Position.Y), w, h);
    }

    /// <summary>The cow under <paramref name="p"/>; when rects overlap the front-most (lowest depth) cow wins.</summary>
    public Cow? HitTest(Point p, HerdSimulator sim)
    {
        var cows = sim.Cows;
        Cow? best = null;
        for (int i = 0; i < cows.Count; i++)
        {
            var cow = cows[i];
            if (CowRect(cow).Contains(p) && (best is null || cow.Position.Y < best.Position.Y))
            {
                best = cow;
            }
        }
        return best;
    }

    public void Clear()
    {
        foreach (var v in _visuals)
        {
            _canvas.Children.Remove(v.Image);
            _canvas.Children.Remove(v.SelfMarker);
        }
        _visuals.Clear();
        _byCow.Clear();
    }

    private CowVisual CreateVisual(Cow cow)
    {
        var flip = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(flip);
        group.Children.Add(move);
        var image = new Image
        {
            Stretch = Stretch.Fill,
            RenderTransform = group,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);

        var markerMove = new TranslateTransform();
        var marker = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xD7, 0x4A)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0x5A, 0x3A, 0x00)),
            StrokeThickness = 1,
            RenderTransform = markerMove,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        marker.Fill.Freeze();
        marker.Stroke.Freeze();

        var visual = new CowVisual
        {
            Cow = cow,
            Image = image,
            Flip = flip,
            Move = move,
            SelfMarker = marker,
            MarkerMove = markerMove,
        };
        ApplySize(visual);
        _canvas.Children.Add(image);
        _canvas.Children.Add(marker);
        _visuals.Add(visual);
        _byCow[cow] = visual;
        return visual;
    }

    private void ApplySize(CowVisual v)
    {
        double w = CowWidthDips;
        double h = CowHeightDips;
        v.Image.Width = w;
        v.Image.Height = h;
        v.Flip.CenterX = w / 2;
        v.Flip.CenterY = h / 2;
    }

    private void Update(CowVisual v)
    {
        var cow = v.Cow;

        // Animation selection only when state/variant changes (dictionary lookups are not per-frame work).
        int idleVariant = cow.State == CowState.Idle ? cow.IdleVariant : 0;
        if (v.Frames is null || cow.State != v.LastState || idleVariant != v.LastIdleVariant || !string.Equals(cow.Variant, v.LastVariant, StringComparison.Ordinal))
        {
            v.Frames = _sprites.GetFrames(cow.Variant, AnimationFor(cow), out var anim);
            v.Anim = anim;
            v.LastState = cow.State;
            v.LastIdleVariant = idleVariant;
            v.LastVariant = cow.Variant;
            v.LastFrame = -1;
        }

        int frame = SpriteLibrary.FrameIndex(v.Anim!, cow.AnimElapsed);
        if (frame != v.LastFrame)
        {
            v.Image.Source = v.Frames[frame];
            v.LastFrame = frame;
        }

        // Art faces left. Facing +1 means flipped. Spin: flip the rendered facing every 125 ms without a Turn.
        bool flipped = _sprites.Manifest.ArtFacesLeft ? cow.Facing > 0 : cow.Facing < 0;
        if (cow.Emote == CowEmote.Spin && ((int)Math.Floor(cow.EmoteElapsed / SpinFlipSeconds) & 1) == 1)
        {
            flipped = !flipped;
        }
        double sx = flipped ? -1 : 1;
        if (v.Flip.ScaleX != sx)
        {
            v.Flip.ScaleX = sx;
        }

        double w = CowWidthDips;
        double h = CowHeightDips;
        double depth = Math.Round(cow.Position.Y);
        double x = Math.Round(cow.Position.X - w / 2);
        double y = _groundY - h - depth;
        if (cow.Emote == CowEmote.Jump)
        {
            // Two hops over the emote: |sin| completes two arches per period.
            double hop = JumpHeightDips * Math.Abs(Math.Sin(2 * Math.PI * cow.EmoteElapsed / EmoteTiming.JumpSeconds));
            y -= Math.Round(hop);
        }
        y = Math.Round(y);
        if (v.Move.X != x)
        {
            v.Move.X = x;
        }
        if (v.Move.Y != y)
        {
            v.Move.Y = y;
        }

        // Back-lane cows (larger depth) draw behind front-lane cows.
        int z = BaseZ - (int)depth;
        if (z != v.LastZ)
        {
            Panel.SetZIndex(v.Image, z);
            Panel.SetZIndex(v.SelfMarker, z);
            v.LastZ = z;
        }

        var markerVisibility = cow.IsSelf ? Visibility.Visible : Visibility.Collapsed;
        if (v.SelfMarker.Visibility != markerVisibility)
        {
            v.SelfMarker.Visibility = markerVisibility;
        }
        if (cow.IsSelf)
        {
            v.MarkerMove.X = Math.Round(cow.Position.X - 4);
            v.MarkerMove.Y = y - 12;
        }
    }

    private static string AnimationFor(Cow cow)
    {
        switch (cow.State)
        {
            case CowState.Walk:
                return "walk";
            case CowState.Graze:
                return "graze";
            case CowState.LieDown:
                return "lie";
            case CowState.Sleep:
                return "sleep";
            case CowState.Moo:
                return "moo";
            case CowState.Idle:
                return cow.IdleVariant == 1 ? "idle2" : "idle";
            default:
                return "idle";
        }
    }
}
