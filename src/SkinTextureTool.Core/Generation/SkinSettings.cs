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

/// <summary>How the model-space normal map is baked (the MuDynamicNormalMap-style pass).</summary>
public sealed class NormalLayer : Layer
{
    [Range(0, 3)] public float DetailStrength { get; set; } = 1f;
    [Range(0, 180)] public float SmoothDegree { get; set; } = 60f;
    /// <summary>Optional tangent-space detail normal maps blended in, as in MuDynamicNormalMap, one per texture group.</summary>
    public Dictionary<string, string> DetailMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DetailMapFor(string group) => DetailMaps.TryGetValue(group, out var p) ? p : null;
    [Range(0, 1)] public float DetailMapStrength { get; set; } = 1f;
    public bool TangentZCorrection { get; set; } = true;
}

public sealed class SkinSettings
{
    [Range(512, 8192)] public int Resolution { get; set; } = 2048;
    [Range(0.25f, 4)] public float Scale { get; set; } = 1f;
    public int Seed { get; set; } = 1;
    /// <summary>Run the noise on UV coordinates instead of 3D positions (shows seams, follows the UV layout).</summary>
    public bool UseUvCoordinates { get; set; }

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
    public MaterialLayer Material { get; set; } = new();
    public NormalLayer Normals { get; set; } = new();

    /// <summary>
    /// BodySlide slider amounts that reshape only the normal bake, per slider set name: slider name to amount
    /// (1 = +100%), on top of the mesh as built.
    /// </summary>
    public Dictionary<string, Dictionary<string, float>> NormalSliders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SkinSettings LoadPreset(string path) =>
        JsonSerializer.Deserialize<SkinSettings>(File.ReadAllText(path), Json) ?? new SkinSettings();

    public void SavePreset(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public SkinSettings Clone() => JsonSerializer.Deserialize<SkinSettings>(JsonSerializer.Serialize(this, Json), Json)!;
}
