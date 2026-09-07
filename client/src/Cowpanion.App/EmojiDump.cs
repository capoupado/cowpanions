using System.Windows.Media.Imaging;
using Cowpanion.App.Overlay;

namespace Cowpanion.App;

/// <summary>
/// <c>--dump-emoji PATH</c>: renders a sample of colour emoji through <see cref="EmojiRasterizer"/> into a PNG so the
/// colour-font path can be checked without a display. Returns the process exit code.
/// </summary>
internal static class EmojiDump
{
    private const string Sample = "❤️😂🎉👍🏽🇵🇹";

    public static int Run(string path)
    {
        try
        {
            using var rasterizer = new EmojiRasterizer();
            var bitmap = rasterizer.Render(Sample, 64, 1.0);
            if (bitmap is null)
            {
                Console.Error.WriteLine("EmojiRasterizer returned null (see cowpanion.log).");
                return 1;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}
