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
        Assert.Equal(7, m.Sheets.Count);
        foreach (var name in new[] { "idle", "graze", "idle2", "moo", "walk", "lie", "sleep" })
        {
            Assert.True(m.Animations.ContainsKey(name), $"manifest lacks {name}");
        }
        foreach (var file in m.Sheets.Values)
        {
            Assert.True(File.Exists(Path.Combine(m.BaseDirectory, file)), $"sheet {file} missing");
        }
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
