using Cowpanion.Core.Sprites;

namespace Cowpanion.Core.Tests;

public class SpriteManifestTests
{
    private const string Full = """
        {
          "name": "cow", "frameWidth": 32, "frameHeight": 32, "defaultScale": 3,
          "facing": "left", "anchor": "bottom-center", "defaultVariant": "brown",
          "sheets": { "brown": "brown.png", "black0": "black0.png" },
          "animations": {
            "idle":  { "row": 0, "frames": 3, "fps": 3, "loop": true },
            "walk":  { "row": 4, "frames": 4, "fps": 8 },
            "moo":   { "row": 3, "frames": 4, "fps": 4, "loop": false },
            "blink": { "files": ["b0.png", "b1.png"], "fps": 10 }
          }
        }
        """;

    [Fact]
    public void Parses_multi_sheet_form()
    {
        var m = SpriteManifest.Parse(Full, "manifest.json", "C:\\x");
        Assert.Equal(32, m.FrameWidth);
        Assert.True(m.ArtFacesLeft);
        Assert.Equal("brown", m.DefaultVariant);
        Assert.Equal(2, m.Sheets.Count);
        Assert.Equal(new[] { "black0", "brown" }, m.VariantNames);
        Assert.Equal(4, m.Animations["walk"].Row);
        Assert.True(m.Animations["walk"].Loop);
        Assert.False(m.Animations["moo"].Loop);
        Assert.True(m.Animations["blink"].UsesFiles);
        Assert.Equal(2, m.Animations["blink"].Frames);
        Assert.Equal(Path.Combine("C:\\x", "black0.png"), m.SheetPath("black0"));
    }

    [Fact]
    public void Missing_animation_falls_back_to_idle()
    {
        var m = SpriteManifest.Parse(Full, "manifest.json", "");
        Assert.Equal("idle", m.Resolve("graze").Name);
        Assert.Equal("walk", m.Resolve("walk").Name);
    }

    [Fact]
    public void Unknown_variant_resolves_to_default()
    {
        var m = SpriteManifest.Parse(Full, "manifest.json", "");
        Assert.Equal("brown", m.ResolveVariant("purple"));
        Assert.Equal("brown", m.ResolveVariant(null));
        Assert.Equal("black0", m.ResolveVariant("black0"));
    }

    [Fact]
    public void Missing_idle_is_fatal_and_names_the_path()
    {
        string json = """{ "frameWidth": 32, "frameHeight": 32, "sheet": "cow.png", "animations": { "walk": { "row": 0, "frames": 4 } } }""";
        var ex = Assert.Throws<SpriteManifestException>(() => SpriteManifest.Parse(json, "C:\\some\\manifest.json", ""));
        Assert.Contains("C:\\some\\manifest.json", ex.Message);
        Assert.Contains("idle", ex.Message);
    }

    [Fact]
    public void Single_sheet_plan_form_is_supported()
    {
        string json = """{ "frameWidth": 32, "frameHeight": 32, "facing": "right", "sheet": "cow.png", "animations": { "idle": { "row": 1, "frames": 4, "fps": 4 } } }""";
        var m = SpriteManifest.Parse(json, "m.json", "");
        Assert.False(m.ArtFacesLeft);
        Assert.Single(m.Sheets);
        Assert.Equal("cow.png", m.Sheets[m.DefaultVariant]);
    }

    [Fact]
    public void Invalid_json_is_fatal_with_path()
    {
        var ex = Assert.Throws<SpriteManifestException>(() => SpriteManifest.Parse("{ nope", "bad.json", ""));
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void Real_manifest_in_repo_parses_and_covers_all_states()
    {
        string path = FindRepoManifest();
        var m = SpriteManifest.Load(path);
        int pugs = m.VariantNames.Count(v => v.StartsWith("pug_", StringComparison.Ordinal));
        Assert.True(pugs >= 1, "expected at least one pug_ variant");
        Assert.Equal(7 + pugs, m.Sheets.Count);
        foreach (var name in new[] { "idle", "graze", "idle2", "moo", "walk", "lie", "sleep" })
        {
            Assert.True(m.Animations.ContainsKey(name), $"manifest lacks {name}");
        }
        foreach (var file in m.Sheets.Values)
        {
            Assert.True(File.Exists(Path.Combine(m.BaseDirectory, file)), $"sheet {file} missing");
        }
    }

    [Fact]
    public void Every_sheet_in_repo_covers_every_row_animation()
    {
        // SpriteLibrary slices every variant x animation at startup and throws for a frame outside the sheet,
        // so a short sheet is a fatal crash for everyone who updates. Check the PNG header of each sheet.
        var m = SpriteManifest.Load(FindRepoManifest());
        var rowAnimations = m.Animations.Values.Where(a => !a.UsesFiles).ToList();
        Assert.NotEmpty(rowAnimations);
        int minWidth = m.FrameWidth * rowAnimations.Max(a => a.Frames);
        int minHeight = m.FrameHeight * (rowAnimations.Max(a => a.Row) + 1);
        foreach (var (variant, file) in m.Sheets)
        {
            var (width, height) = ReadPngSize(Path.Combine(m.BaseDirectory, file));
            Assert.True(width >= minWidth, $"{file} ({variant}) is {width} px wide, animations need {minWidth}");
            Assert.True(height >= minHeight, $"{file} ({variant}) is {height} px tall, animations need {minHeight}");
        }
    }

    [Fact]
    public void Every_variant_name_is_a_valid_protocol_variant()
    {
        var m = SpriteManifest.Load(FindRepoManifest());
        foreach (var name in m.VariantNames)
        {
            Assert.True(Cowpanion.Core.Configuration.ConfigValidator.IsValidVariant(name), $"variant {name} does not match the protocol rule");
        }
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        byte[] header = new byte[24];
        using (var fs = File.OpenRead(path))
        {
            Assert.Equal(header.Length, fs.Read(header, 0, header.Length));
        }
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(header.AsSpan(0, 8).SequenceEqual(signature), $"{path} is not a PNG");
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        return (width, height);
    }

    private static string FindRepoManifest()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "assets", "sprites", "cow", "manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("manifest.json not found above test output");
    }
}
