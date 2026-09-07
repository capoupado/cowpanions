using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cowpanion.Core.Sprites;

namespace Cowpanion.App.Overlay;

/// <summary>
/// Slices every sheet once at load into frozen <see cref="CroppedBitmap"/> frames and caches them per
/// (variant, animation). Nothing is decoded or sliced during the tick.
/// </summary>
internal sealed class SpriteLibrary
{
    private readonly SpriteManifest _manifest;
    private readonly Dictionary<string, BitmapSource> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Variant, string Animation), BitmapSource[]> _frames = new();
    private readonly Dictionary<string, BitmapSource[]> _fileFrames = new(StringComparer.Ordinal);

    public SpriteLibrary(SpriteManifest manifest)
    {
        _manifest = manifest;
        foreach (var (variant, file) in manifest.Sheets)
        {
            _sheets[variant] = LoadFrozen(Path.Combine(manifest.BaseDirectory, file));
        }
        foreach (var anim in manifest.Animations.Values)
        {
            if (anim.UsesFiles)
            {
                var frames = new BitmapSource[anim.Files!.Count];
                for (int i = 0; i < frames.Length; i++)
                {
                    frames[i] = LoadFrozen(Path.Combine(manifest.BaseDirectory, anim.Files[i]));
                }
                _fileFrames[anim.Name] = frames;
            }
        }
        foreach (var variant in manifest.Sheets.Keys)
        {
            foreach (var anim in manifest.Animations.Values)
            {
                _frames[(variant, anim.Name)] = Slice(variant, anim);
            }
        }
    }

    public SpriteManifest Manifest => _manifest;

    /// <summary>Frames for a variant/animation with manifest fallbacks (unknown variant → default, unknown anim → idle).</summary>
    public BitmapSource[] GetFrames(string? variant, string animation, out SpriteAnimation resolved)
    {
        string v = _manifest.ResolveVariant(variant);
        resolved = _manifest.Resolve(animation);
        return _frames[(v, resolved.Name)];
    }

    public static int FrameIndex(SpriteAnimation anim, double elapsedSeconds)
    {
        if (anim.Frames <= 1)
        {
            return 0;
        }
        int idx = (int)(elapsedSeconds * anim.Fps);
        if (anim.Loop)
        {
            return idx % anim.Frames;
        }
        return idx >= anim.Frames ? anim.Frames - 1 : idx;
    }

    private BitmapSource[] Slice(string variant, SpriteAnimation anim)
    {
        if (anim.UsesFiles)
        {
            return _fileFrames[anim.Name];
        }
        var sheet = _sheets[variant];
        int fw = _manifest.FrameWidth;
        int fh = _manifest.FrameHeight;
        int columns = Math.Max(1, sheet.PixelWidth / fw);
        var frames = new BitmapSource[anim.Frames];
        for (int i = 0; i < anim.Frames; i++)
        {
            int col = i % columns;
            int row = anim.Row + i / columns;
            var rect = new Int32Rect(col * fw, row * fh, fw, fh);
            if (rect.X + fw > sheet.PixelWidth || rect.Y + fh > sheet.PixelHeight)
            {
                throw new SpriteManifestException($"Animation '{anim.Name}' frame {i} (row {row}, col {col}) is outside sheet '{_manifest.Sheets[variant]}' ({sheet.PixelWidth}x{sheet.PixelHeight}).");
            }
            var cropped = new CroppedBitmap(sheet, rect);
            cropped.Freeze();
            frames[i] = cropped;
        }
        return frames;
    }

    private static BitmapSource LoadFrozen(string path)
    {
        if (!File.Exists(path))
        {
            throw new SpriteManifestException($"Sprite sheet not found: '{path}'.");
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
