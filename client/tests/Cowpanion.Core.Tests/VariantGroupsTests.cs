using Cowpanion.Core.Sprites;

namespace Cowpanion.Core.Tests;

public class VariantGroupsTests
{
    // Ordinal order, as SpriteManifest.VariantNames gives it: the white cows sort after the pugs.
    private static readonly string[] Ordinal =
    {
        "black0", "black1", "brown", "kitty_black", "kitty_grey", "kitty_orange", "kitty_white",
        "pug_apricot", "pug_black", "pug_fawn", "pug_silver", "white0", "white1", "white_darkspots", "white_pinkspots",
    };

    [Theory]
    [InlineData("kitty_orange", "kitty")]
    [InlineData("kitty_white", "kitty")]
    [InlineData("pug_fawn", "pug")]
    [InlineData("brown", "cow")]
    [InlineData("white_pinkspots", "cow")]
    [InlineData("kitty", "cow")]
    [InlineData("pug", "cow")]
    [InlineData("Kitty_orange", "cow")]
    [InlineData("", "cow")]
    public void Species_is_told_by_the_name_prefix(string variant, string species)
    {
        Assert.Equal(species, VariantGroups.Species(variant));
    }

    [Fact]
    public void Chooser_lists_cows_then_kitties_then_pugs_each_in_ordinal_order()
    {
        var chooser = VariantGroups.ForChooser(Ordinal);
        Assert.Equal(new[]
        {
            "black0", "black1", "brown", "white0", "white1", "white_darkspots", "white_pinkspots",
            "kitty_black", "kitty_grey", "kitty_orange", "kitty_white",
            "pug_apricot", "pug_black", "pug_fawn", "pug_silver",
        }, chooser);
    }

    [Fact]
    public void Chooser_keeps_every_variant_exactly_once_whatever_the_input_order()
    {
        var shuffled = Ordinal.Reverse().ToArray();
        var chooser = VariantGroups.ForChooser(shuffled);
        Assert.Equal(Ordinal.Length, chooser.Count);
        Assert.Equal(Ordinal.OrderBy(v => v, StringComparer.Ordinal), chooser.OrderBy(v => v, StringComparer.Ordinal));
        Assert.Equal(VariantGroups.ForChooser(Ordinal), chooser);
    }

    [Fact]
    public void Chooser_of_nothing_is_empty()
    {
        Assert.Empty(VariantGroups.ForChooser(Array.Empty<string>()));
    }
}
