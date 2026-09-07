using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Renders emoji strings to WPF bitmaps through Direct2D/DirectWrite. WPF's own text stack has no COLR/CPAL support,
/// so a TextBlock shows Segoe UI Emoji as black outlines; Direct2D with <see cref="DrawTextOptions.EnableColorFont"/>
/// draws the real coloured glyphs. Results are cached by (text, pixel size). Any failure logs once and returns null so
/// callers can fall back to a TextBlock — the rasteriser must never take the overlay down.
/// </summary>
internal sealed class EmojiRasterizer : IDisposable
{
    private const string FontName = "Segoe UI Emoji";
    private const int MaxCacheEntries = 64;
    /// <summary>Extra room around the glyph box for glyphs that overhang their advance (flags, skin-tone sequences).</summary>
    private const double PaddingFactor = 0.25;

    private readonly Dictionary<(string Text, int SizePx), BitmapSource?> _cache = new();
    private ID2D1Factory? _d2d;
    private IDWriteFactory? _dwrite;
    private IWICImagingFactory? _wic;
    private bool _failed;
    private bool _disposed;

    /// <summary>
    /// Renders <paramref name="emoji"/> at <paramref name="sizeDips"/> DIPs scaled by <paramref name="dpiScale"/>.
    /// The bitmap's DPI is set so that its WPF layout size equals the drawn size in DIPs. Null when rendering is unavailable.
    /// </summary>
    public BitmapSource? Render(string emoji, double sizeDips, double dpiScale)
    {
        if (_disposed || _failed || string.IsNullOrEmpty(emoji))
        {
            return null;
        }
        int sizePx = (int)Math.Ceiling(sizeDips * dpiScale);
        if (sizePx <= 0)
        {
            return null;
        }
        var key = (emoji, sizePx);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        BitmapSource? bitmap;
        try
        {
            bitmap = RenderUncached(emoji, sizePx, dpiScale);
        }
        catch (Exception ex)
        {
            _failed = true;
            AppLog.Info("emoji rasteriser disabled: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }
        _cache[key] = bitmap;
        return bitmap;
    }

    private BitmapSource RenderUncached(string emoji, int sizePx, double dpiScale)
    {
        EnsureFactories();
        float fontPx = sizePx;
        // Layout box: generous so wide sequences and overhanging glyphs are never clipped, then trimmed to the ink.
        int boxPx = (int)Math.Ceiling(fontPx * (1 + 2 * PaddingFactor) * Math.Max(1, CountClusters(emoji)));
        int boxHeightPx = (int)Math.Ceiling(fontPx * (1 + 2 * PaddingFactor));

        using var format = _dwrite!.CreateTextFormat(FontName, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, fontPx, "en-us");
        format.TextAlignment = TextAlignment.Center;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
        using var layout = _dwrite.CreateTextLayout(emoji, format, boxPx, boxHeightPx);
        var metrics = layout.Metrics;
        int inkWidth = Math.Clamp((int)Math.Ceiling(metrics.WidthIncludingTrailingWhitespace + fontPx * PaddingFactor), 1, boxPx);
        int inkHeight = Math.Clamp((int)Math.Ceiling(metrics.Height + fontPx * PaddingFactor), 1, boxHeightPx);

        using var wicBitmap = _wic!.CreateBitmap((uint)inkWidth, (uint)inkHeight, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        // 96 DPI on the D2D side: we lay out directly in pixels; WPF gets the DPI so the bitmap measures sizeDips.
        var props = new RenderTargetProperties(RenderTargetType.Default, new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, RenderTargetUsage.None, FeatureLevel.Default);
        using var target = _d2d!.CreateWicBitmapRenderTarget(wicBitmap, props);
        using var brush = target.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        target.BeginDraw();
        target.Clear(new Color4(0, 0, 0, 0));
        // Re-centre the layout inside the trimmed bitmap.
        using var fitted = _dwrite.CreateTextLayout(emoji, format, inkWidth, inkHeight);
        target.DrawTextLayout(Vector2.Zero, fitted, brush, DrawTextOptions.EnableColorFont);
        target.EndDraw();

        int stride = inkWidth * 4;
        var pixels = new byte[stride * inkHeight];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                wicBitmap.CopyPixels(new RectI(0, 0, inkWidth, inkHeight), (uint)stride, (uint)pixels.Length, (nint)p);
            }
        }
        double dpi = 96.0 * dpiScale;
        var source = BitmapSource.Create(inkWidth, inkHeight, dpi, dpi, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private void EnsureFactories()
    {
        _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _wic ??= new IWICImagingFactory();
    }

    private static int CountClusters(string s)
    {
        int n = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            n++;
        }
        return n;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cache.Clear();
        _wic?.Dispose();
        _dwrite?.Dispose();
        _d2d?.Dispose();
        _wic = null;
        _dwrite = null;
        _d2d = null;
    }
}
