using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkinTextureTool.Core.Generation;

/// <summary>A slider's range, read by the UI to build its controls.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RangeAttribute(float min, float max) : Attribute
{
    public float Min { get; } = min;
    public float Max { get; } = max;
}

/// <summary>Marks a layer that can be driven by a greyscale mask image (white = full effect).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MaskableAttribute : Attribute;

public sealed record Rgb(float R, float G, float B)
{
    public static Rgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToInt32(hex[..2], 16) / 255f, Convert.ToInt32(hex[2..4], 16) / 255f, Convert.ToInt32(hex[4..6], 16) / 255f);
    }

    public string ToHex() => $"#{(int)(R * 255):X2}{(int)(G * 255):X2}{(int)(B * 255):X2}";
}

public abstract class Layer
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Greyscale mask images for layers marked [Maskable], one per texture group (keyed by texture name such as
    /// femalebody_1), since each group has its own UV layout. White means full effect.
    /// </summary>
    public Dictionary<string, string> MaskPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? MaskFor(string group) => MaskPaths.TryGetValue(group, out var p) ? p : null;
}

public sealed class BaseLayer : Layer
{
    public Rgb Color { get; set; } = Rgb.FromHex("#C99A82");
    [Range(0, 1)] public float Mottling { get; set; } = 0.25f;
    [Range(0.1f, 5)] public float MottlingScale { get; set; } = 1f;
}

[Maskable]
public sealed class ZoneLayer : Layer
{
    public Rgb Color { get; set; } = Rgb.FromHex("#C0504D");
    [Range(0, 1)] public float Strength { get; set; } = 0.2f;
    [Range(0.1f, 5)] public float Scale { get; set; } = 1f;
}

[Maskable]
public sealed class LipsLayer : Layer
{
    public Rgb Color { get; set; } = Rgb.FromHex("#A85A5A");
    [Range(0, 1)] public float Strength { get; set; } = 0.6f;
}

[Maskable]
public sealed class PoresLayer : Layer
{
    [Range(0, 1)] public float Strength { get; set; } = 0.5f;
    [Range(0.2f, 3)] public float Size { get; set; } = 1f;
    [Range(0.2f, 4)] public float Density { get; set; } = 1f;
    [Range(0, 1)] public float Dirt { get; set; } = 0.15f;
}

public sealed class CellsLayer : Layer
{
    [Range(0, 1)] public float Strength { get; set; } = 0.3f;
    [Range(0.2f, 4)] public float Scale { get; set; } = 1f;
}

public sealed class GoosebumpsLayer : Layer
{
    [Range(0, 1)] public float Strength { get; set; } = 0f;
    [Range(0.2f, 4)] public float Scale { get; set; } = 1f;
}

[Maskable]
public sealed class SpotLayer : Layer
{
    public Rgb Color { get; set; } = Rgb.FromHex("#8A5A3C");
    [Range(0, 1)] public float Amount { get; set; } = 0.3f;
    [Range(0.2f, 4)] public float Scale { get; set; } = 1f;
    [Range(0, 1)] public float Strength { get; set; } = 0.5f;
    [Range(0, 1)] public float Bump { get; set; } = 0f;
}

[Maskable]
public sealed class VeinsLayer : Layer
{
    public Rgb Color { get; set; } = Rgb.FromHex("#5A6E9A");
    [Range(0, 1)] public float Strength { get; set; } = 0.06f;
    [Range(0.2f, 4)] public float Scale { get; set; } = 1f;
    /// <summary>How far the veins stand out of the skin in the normal map.</summary>
    [Range(0, 1)] public float Bump { get; set; } = 0f;
}

/// <summary>
/// Procedural fur: colour, patterns and strands in the regular textures, plus an optional shell texture
/// (name_shell.dds next to the diffuse, with name_shell.json) for Bottled Shaders' fur shells. A mask per texture
/// sets where fur grows (white = fur).
/// </summary>
[Maskable]
public sealed class FurLayer : Layer
{
    public FurLayer() => Enabled = false;
    public Rgb Color { get; set; } = Rgb.FromHex("#8C7B6A");
    /// <summary>How much the fur colour replaces the skin colour in the diffuse.</summary>
    [Range(0, 1)] public float ColorStrength { get; set; } = 0.8f;
    [Range(0, 1)] public float ColorVariation { get; set; } = 0.3f;
    /// <summary>Strands per unit of surface; higher is finer fur.</summary>
    [Range(0.3f, 3)] public float Density { get; set; } = 1f;
    /// <summary>How stretched strands are along the fur direction (1 = round tufts).</summary>
    [Range(1, 8)] public float StrandLength { get; set; } = 4f;
    /// <summary>Fur direction: 0 straight down the body, 1 sweeping out away from the spine.</summary>
    [Range(0, 1)] public float Spread { get; set; } = 0.35f;
    /// <summary>Strand grooves in the normal map.</summary>
    [Range(0, 1)] public float NormalStrength { get; set; } = 0.4f;
    public Rgb PatternColor { get; set; } = Rgb.FromHex("#3C3028");
    [Range(0, 1)] public float Stripes { get; set; } = 0f;
    [Range(0.2f, 4)] public float StripeScale { get; set; } = 1f;
    [Range(0, 1)] public float Spots { get; set; } = 0f;
    [Range(0.2f, 4)] public float SpotScale { get; set; } = 1f;
    /// <summary>Lighter fur on the front of the torso, throat and inner limbs.</summary>
    [Range(0, 1)] public float LightBelly { get; set; } = 0.3f;
    public Rgb BellyColor { get; set; } = Rgb.FromHex("#D8CDBE");

    // Bottled Shaders fur shells (written to name_shell.json)
    public bool ExportShells { get; set; } = true;
    [Range(1, 32)] public int ShellCount { get; set; } = 12;
    [Range(0, 4)] public float ShellLength { get; set; } = 1.1f;
    [Range(0, 1)] public float Droop { get; set; } = 0.45f;
    [Range(0, 1)] public float RootDarkening { get; set; } = 1f;
    [Range(0, 1)] public float RootThreshold { get; set; } = 0f;
    [Range(0, 1)] public float TipThreshold { get; set; } = 1f;
    /// <summary>How much the shells take the shell texture's colour over the skin's diffuse.</summary>
    [Range(0, 1)] public float ShellColor { get; set; } = 1f;
    [Range(0, 8000)] public float FadeStart { get; set; } = 700f;
    [Range(0, 8000)] public float FadeEnd { get; set; } = 2500f;
}

public sealed class MaterialLayer : Layer
{
    [Range(0, 1)] public float Specular { get; set; } = 0.35f;
    [Range(0, 1)] public float SpecularVariation { get; set; } = 0.3f;
    public Rgb SubsurfaceColor { get; set; } = Rgb.FromHex("#D25A46");
    [Range(0, 1)] public float Subsurface { get; set; } = 0.5f;
    // Community Shaders Advanced Skin (_rfaos.dds)
    [Range(0, 1)] public float Roughness { get; set; } = 0.55f;
    [Range(0, 1)] public float RoughnessVariation { get; set; } = 0.25f;
    [Range(0, 1)] public float Fuzz { get; set; } = 0.5f;
    [Range(0, 1)] public float CavityOcclusion { get; set; } = 0.5f;
}

/// <summary>
/// Starts from the textures the loaded meshes already use (resolved through the MO2 load order when the tool
/// knows it, else next to the mesh) instead of a flat base: the procedural layers paint on top of them. Each
/// strength blends between the tool's own base (0) and the existing texture (1).
/// </summary>
public sealed class ExistingTexturesLayer : Layer
{
    public ExistingTexturesLayer() => Enabled = false;
    [Range(0, 1)] public float Diffuse { get; set; } = 1f;
    [Range(0, 1)] public float NormalMap { get; set; } = 1f;
    [Range(0, 1)] public float Specular { get; set; } = 1f;
    [Range(0, 1)] public float Subsurface { get; set; } = 1f;
}

/// <summary>How the model-space normal map is baked (the MuDynamicNormalMap-style pass).</summary>
public sealed class NormalLayer : Layer
{
    [Range(0, 3)] public float DetailStrength { get; set; } = 1f;
    [Range(0, 180)] public float SmoothDegree { get; set; } = 60f;
    /// <summary>Rings of vertices either side of a seam between meshes over which normals are blended.</summary>
    [Range(0, 20)] public int SeamBlend { get; set; } = 6;
    /// <summary>
    /// World units over which a smaller mesh's edge (a head's neck) is turned to follow the larger mesh it
    /// joins, so the body's surface carries on into it instead of meeting it at a crease. 0 turns it off.
    /// </summary>
    [Range(0, 20)] public float SeamConform { get; set; } = 8f;
    /// <summary>
    /// Times each triangle is split (into 4, 16, 64 pieces) with smoothly curving normals before baking, as in
    /// MuDynamicNormalMap, so low-poly curves don't bake as facets. Higher is smoother but slower.
    /// </summary>
    [Range(0, 3)] public int Subdivision { get; set; } = 2;
    /// <summary>Optional tangent-space detail normal maps blended in, as in MuDynamicNormalMap, one per texture group.</summary>
    public Dictionary<string, string> DetailMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DetailMapFor(string group) => DetailMaps.TryGetValue(group, out var p) ? p : null;
    [Range(0, 1)] public float DetailMapStrength { get; set; } = 1f;
    public bool TangentZCorrection { get; set; } = true;
}

/// <summary>
/// A masked area with its own skin settings, blended over the base skin (face, palms, knees…). Masks are per
/// texture group like layer masks; where no mask is set for a group, the region doesn't touch that group.
/// </summary>
public sealed class Region
{
    public string Name { get; set; } = "Region";
    public bool Enabled { get; set; } = true;
    [Range(0, 1)] public float Strength { get; set; } = 1f;
    public Dictionary<string, string> MaskPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The region's own settings; its Regions list is unused.</summary>
    public SkinSettings Settings { get; set; } = new();

    public string? MaskFor(string group) => MaskPaths.TryGetValue(group, out var p) ? p : null;
}

public sealed class SkinSettings
{
    [Range(512, 8192)] public int Resolution { get; set; } = 2048;
    [Range(0.25f, 4)] public float Scale { get; set; } = 1f;
    public int Seed { get; set; } = 1;
    /// <summary>Run the noise on UV coordinates instead of 3D positions (shows seams, follows the UV layout).</summary>
    public bool UseUvCoordinates { get; set; }

    /// <summary>Start from the meshes' current textures.</summary>
    public ExistingTexturesLayer StartFromExisting { get; set; } = new();
    public BaseLayer Base { get; set; } = new();
    public ZoneLayer RedZones { get; set; } = new() { Color = Rgb.FromHex("#C0504D"), Strength = 0.2f };
    public ZoneLayer YellowZones { get; set; } = new() { Color = Rgb.FromHex("#C8A050"), Strength = 0.12f, Scale = 0.7f };
    public ZoneLayer BlueZones { get; set; } = new() { Color = Rgb.FromHex("#5A6E9A"), Strength = 0.08f, Scale = 1.3f };
    public LipsLayer Lips { get; set; } = new();
    public PoresLayer Pores { get; set; } = new();
    public CellsLayer Cells { get; set; } = new();
    public GoosebumpsLayer Goosebumps { get; set; } = new();
    public SpotLayer Freckles { get; set; } = new() { Color = Rgb.FromHex("#A0643C"), Amount = 0.25f, Scale = 1.2f, Strength = 0.35f };
    public SpotLayer Moles { get; set; } = new() { Color = Rgb.FromHex("#4A2E22"), Amount = 0.08f, Scale = 1f, Strength = 0.8f, Bump = 0.2f };
    public SpotLayer Pimples { get; set; } = new() { Color = Rgb.FromHex("#C25A50"), Amount = 0.05f, Scale = 1f, Strength = 0.4f, Bump = 0.5f };
    public SpotLayer Blemishes { get; set; } = new() { Color = Rgb.FromHex("#B0605A"), Amount = 0.4f, Scale = 0.6f, Strength = 0.15f };
    public SpotLayer RedSpots { get; set; } = new() { Color = Rgb.FromHex("#C04844"), Amount = 0.2f, Scale = 1.5f, Strength = 0.2f };
    public VeinsLayer Veins { get; set; } = new();
    public FurLayer Fur { get; set; } = new();
    public MaterialLayer Material { get; set; } = new();
    public NormalLayer Normals { get; set; } = new();

    /// <summary>
    /// BodySlide slider amounts that reshape only the normal bake, per slider set name: slider name to amount
    /// (1 = +100%), on top of the mesh as built.
    /// </summary>
    public Dictionary<string, Dictionary<string, float>> NormalSliders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Masked areas with their own settings, applied in order over the base skin.</summary>
    public List<Region> Regions { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SkinSettings LoadPreset(string path) =>
        (JsonSerializer.Deserialize<SkinSettings>(File.ReadAllText(path), Json) ?? new SkinSettings()).WithPaths(ResolvePath);

    public void SavePreset(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Clone().WithPaths(PortablePath), Json));

    // Image paths inside the tool's folder are stored relative to it, so the folder (and presets) can move.
    private static readonly string AppFolder = Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>A path under the tool's folder made relative to it; anything else is left as it is.</summary>
    public static string PortablePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(AppFolder, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(AppFolder, full) : path;
    }

    /// <summary>A relative path resolved against the tool's folder.</summary>
    public static string ResolvePath(string path) => Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(AppFolder, path));

    /// <summary>Rewrites every image path (layer masks, detail maps, region masks) with <paramref name="map"/>, in place.</summary>
    public SkinSettings WithPaths(Func<string, string> map)
    {
        static void Map(Dictionary<string, string> paths, Func<string, string> map)
        {
            foreach (var key in paths.Keys.ToList()) paths[key] = map(paths[key]);
        }
        foreach (var layer in typeof(SkinSettings).GetProperties().Select(p => p.GetValue(this)).OfType<Layer>())
            Map(layer.MaskPaths, map);
        Map(Normals.DetailMaps, map);
        foreach (var region in Regions)
        {
            Map(region.MaskPaths, map);
            region.Settings.WithPaths(map);
        }
        return this;
    }

    /// <summary>A copy for a region to start from: these settings without their own regions.</summary>
    public SkinSettings CloneWithoutRegions()
    {
        var copy = Clone();
        copy.Regions.Clear();
        copy.NormalSliders.Clear();
        return copy;
    }

    public SkinSettings Clone() => JsonSerializer.Deserialize<SkinSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}
