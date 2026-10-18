namespace Cowpanion.Core.Sprites;

/// <summary>
/// Groups sprite variants by species for the choosers. Kitties and pugs are variants of the cow pack, told apart
/// only by their name prefix. <see cref="SpriteManifest.VariantNames"/> stays ordinal (variant derivation and filler
/// hashing depend on it); this is display order only.
/// </summary>
public static class VariantGroups
{
    public const string Cow = "cow";
    public const string Kitty = "kitty";
    public const string Pug = "pug";

    private static readonly string[] Order = { Cow, Kitty, Pug };

    /// <summary>"kitty" for <c>kitty_*</c>, "pug" for <c>pug_*</c>, otherwise "cow".</summary>
    public static string Species(string variant)
    {
        if (variant.StartsWith("kitty_", StringComparison.Ordinal))
        {
            return Kitty;
        }
        if (variant.StartsWith("pug_", StringComparison.Ordinal))
        {
            return Pug;
        }
        return Cow;
    }

    /// <summary>Cows, then kitties, then pugs; each group in ordinal order.</summary>
    public static IReadOnlyList<string> ForChooser(IReadOnlyList<string> variants)
    {
        return variants
            .OrderBy(v => Array.IndexOf(Order, Species(v)))
            .ThenBy(v => v, StringComparer.Ordinal)
            .ToList();
    }
}
