using System.Text.Json;

namespace Cowpanion.Core.Sprites;

public sealed class SpriteManifestException : Exception
{
    public SpriteManifestException(string message) : base(message)
    {
    }

    public SpriteManifestException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>One animation: either a row on the shared sheet, or a list of loose files.</summary>
public sealed record SpriteAnimation(string Name, int Row, int Frames, double Fps, bool Loop, IReadOnlyList<string>? Files)
{
    public bool UsesFiles => Files is not null;
}

/// <summary>
/// Data-driven sprite layout. Frame layout is never hardcoded; this is read from assets/sprites/cow/manifest.json.
/// Supports the plan's single-sheet form (<c>sheet</c>) and DECISIONS.md's multi-variant form (<c>sheets</c> +
/// <c>defaultVariant</c>).
/// </summary>
public sealed class SpriteManifest
{
    public required string Name { get; init; }
    public required int FrameWidth { get; init; }
    public required int FrameHeight { get; init; }
    public required int DefaultScale { get; init; }
    /// <summary>"left" or "right": the direction the source art faces.</summary>
    public required string Facing { get; init; }
    public required string Anchor { get; init; }
    public required string DefaultVariant { get; init; }
    /// <summary>variant → sheet file name (relative to the manifest directory).</summary>
    public required IReadOnlyDictionary<string, string> Sheets { get; init; }
    public required IReadOnlyDictionary<string, SpriteAnimation> Animations { get; init; }
    /// <summary>Directory the manifest was loaded from (empty when parsed from a string).</summary>
    public required string BaseDirectory { get; init; }

    public bool ArtFacesLeft => string.Equals(Facing, "left", StringComparison.OrdinalIgnoreCase);

    /// <summary>Variants in a stable (ordinal) order, used for deterministic derivation from clientId.</summary>
    public IReadOnlyList<string> VariantNames
    {
        get
        {
            var names = new List<string>(Sheets.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>Any missing animation falls back to idle (guaranteed to exist).</summary>
    public SpriteAnimation Resolve(string animationName)
    {
        if (Animations.TryGetValue(animationName, out var anim))
        {
            return anim;
        }
        return Animations["idle"];
    }

    /// <summary>Unknown variants render as the default variant.</summary>
    public string ResolveVariant(string? variant)
    {
        if (!string.IsNullOrEmpty(variant) && Sheets.ContainsKey(variant))
        {
            return variant;
        }
        return DefaultVariant;
    }

    public string SheetPath(string variant) => Path.Combine(BaseDirectory, Sheets[ResolveVariant(variant)]);

    public static SpriteManifest Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpriteManifestException($"Cannot read sprite manifest '{path}': {ex.Message}", ex);
        }
        return Parse(json, path, Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
    }

    public static SpriteManifest Parse(string json, string pathForErrors, string baseDirectory)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new SpriteManifestException($"Sprite manifest '{pathForErrors}' is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}' must be a JSON object.");
            }

            string name = GetString(root, "name", "cow");
            int frameWidth = GetInt(root, "frameWidth", pathForErrors);
            int frameHeight = GetInt(root, "frameHeight", pathForErrors);
            int defaultScale = root.TryGetProperty("defaultScale", out var ds) && ds.ValueKind == JsonValueKind.Number ? ds.GetInt32() : 3;
            string facing = GetString(root, "facing", "right");
            string anchor = GetString(root, "anchor", "bottom-center");

            var sheets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("sheets", out var sheetsEl) && sheetsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in sheetsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        sheets[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
            string defaultVariant = GetString(root, "defaultVariant", "");
            if (root.TryGetProperty("sheet", out var singleSheet) && singleSheet.ValueKind == JsonValueKind.String)
            {
                if (defaultVariant.Length == 0)
                {
                    defaultVariant = "default";
                }
                sheets.TryAdd(defaultVariant, singleSheet.GetString()!);
            }
            if (defaultVariant.Length == 0 && sheets.Count > 0)
            {
                var keys = new List<string>(sheets.Keys);
                keys.Sort(StringComparer.Ordinal);
                defaultVariant = keys[0];
            }

            var animations = new Dictionary<string, SpriteAnimation>(StringComparer.Ordinal);
            if (root.TryGetProperty("animations", out var animsEl) && animsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in animsEl.EnumerateObject())
                {
                    animations[prop.Name] = ParseAnimation(prop.Name, prop.Value, pathForErrors);
                }
            }

            if (!animations.ContainsKey("idle"))
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}' has no 'idle' animation. 'idle' is required because every missing animation falls back to it.");
            }
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': frameWidth/frameHeight must be positive.");
            }

            bool anyRowAnimation = false;
            foreach (var a in animations.Values)
            {
                if (!a.UsesFiles)
                {
                    anyRowAnimation = true;
                }
            }
            if (anyRowAnimation && sheets.Count == 0)
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}' uses row animations but declares no 'sheet' or 'sheets'.");
            }
            if (sheets.Count > 0 && !sheets.ContainsKey(defaultVariant))
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': defaultVariant '{defaultVariant}' is not in 'sheets'.");
            }

            return new SpriteManifest
            {
                Name = name,
                FrameWidth = frameWidth,
                FrameHeight = frameHeight,
                DefaultScale = Math.Clamp(defaultScale, 1, 8),
                Facing = facing,
                Anchor = anchor,
                DefaultVariant = defaultVariant,
                Sheets = sheets,
                Animations = animations,
                BaseDirectory = baseDirectory,
            };
        }
    }

    private static SpriteAnimation ParseAnimation(string name, JsonElement el, string pathForErrors)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': animation '{name}' must be an object.");
        }
        double fps = el.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number ? fpsEl.GetDouble() : 4.0;
        if (fps <= 0)
        {
            fps = 4.0;
        }
        bool loop = !el.TryGetProperty("loop", out var loopEl) || loopEl.ValueKind != JsonValueKind.False;

        if (el.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            var files = new List<string>();
            foreach (var f in filesEl.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String)
                {
                    files.Add(f.GetString()!);
                }
            }
            if (files.Count == 0)
            {
                throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': animation '{name}' has an empty 'files' list.");
            }
            return new SpriteAnimation(name, 0, files.Count, fps, loop, files);
        }

        int row = GetInt(el, "row", pathForErrors, $"animation '{name}'");
        int frames = GetInt(el, "frames", pathForErrors, $"animation '{name}'");
        if (row < 0 || frames <= 0)
        {
            throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': animation '{name}' has invalid row/frames.");
        }
        return new SpriteAnimation(name, row, frames, fps, loop, null);
    }

    private static string GetString(JsonElement el, string prop, string fallback)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    }

    private static int GetInt(JsonElement el, string prop, string pathForErrors, string context = "root")
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
        {
            return i;
        }
        throw new SpriteManifestException($"Sprite manifest '{pathForErrors}': {context} is missing integer '{prop}'.");
    }
}
