using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using SkinTextureTool.Core.Mesh;
using SkinTextureTool.Core.NeckFix;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// Fits NPC FaceGen heads to the body each NPC actually wears. FaceGen necks are baked for the NPC's weight, and
/// the game draws the body blended between its _0 and _1 meshes by that weight, so the head is fitted to the
/// same blend. In-between neck vertices are snapped and tucked rather than merged, so the expression .tri files
/// the NPC's head part uses stay valid.
/// </summary>
public sealed class FaceGenPatcher
{
    public sealed record Outcome(string Npc, string FaceGenPath, string Body, float Weight, int SeamVertices, int Moved, float LargestMove,
        ShapeData Head, float[] Patched, ShapeData BodyShape, string? Skipped, IReadOnlyDictionary<int, int>? Merges = null);

    private readonly VirtualData _data;
    private readonly ILinkCache<ISkyrimMod, ISkyrimModGetter> _cache;
    private readonly Dictionary<string, ShapeData?> _bodies = new(StringComparer.OrdinalIgnoreCase);

    public float Band { get; init; } = 4f;

    public FaceGenPatcher(VirtualData data, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache) { _data = data; _cache = cache; }

    public static string FaceGenPath(INpcGetter npc) =>
        $@"meshes\actors\character\facegendata\facegeom\{npc.FormKey.ModKey.FileName}\{npc.FormKey.ID:X8}.nif";

    /// <summary>The body mesh path (without _0/_1) the NPC wears: its own skin, else its race's.</summary>
    public string? BodyPath(INpcGetter npc)
    {
        var female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        if (!npc.Race.TryResolve(_cache, out var race)) return null;
        IArmorGetter? skin = null;
        if (!npc.WornArmor.IsNull && npc.WornArmor.TryResolve(_cache, out var worn)) skin = worn;
        if (skin == null && race.Skin.TryResolve(_cache, out var raceSkin)) skin = raceSkin;
        if (skin == null) return null;
        // armour pieces are picked for the race, or for the race it borrows armour from (custom races mostly do)
        var races = new List<Mutagen.Bethesda.Plugins.FormKey> { race.FormKey };
        if (!race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);
        foreach (var link in skin.Armature)
        {
            if (!link.TryResolve(_cache, out var addon) || addon.BodyTemplate == null) continue;
            if (!addon.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Body)) continue;
            if (!races.Any(r => addon.Race.FormKey == r || addon.AdditionalRaces.Any(a => a.FormKey == r))) continue;
            var model = female ? addon.WorldModel?.Female?.File : addon.WorldModel?.Male?.File;
            if (model != null) return MeshPath(model.ToString());
        }
        return null;
    }

    public Outcome Fit(INpcGetter npc) => Fit(npc, Band);

    /// <param name="only">Merge exactly these vertex pairs (shared by every NPC using the same .tri files); null merges
    /// whatever this head needs; an empty map merges nothing (snap and tuck).</param>
    public Outcome Fit(INpcGetter npc, float band, IReadOnlyDictionary<int, int>? only = null)
    {
        var name = npc.EditorID ?? npc.FormKey.ToString();
        var faceGen = FaceGenPath(npc);
        var weight = Math.Clamp(npc.Weight, 0, 100) / 100f;
        var bodyPath = BodyPath(npc);
        Outcome Skip(string why, ShapeData? head = null) =>
            new(name, faceGen, bodyPath ?? "?", weight, 0, 0, 0, head!, head?.Positions ?? Array.Empty<float>(), null!, why);

        var file = _data.Materialize(faceGen);
        if (file == null) return Skip("no FaceGen head");
        if (bodyPath == null) return Skip("no body found for its skin and race");
        var body = Body(bodyPath, weight);
        if (body == null) return Skip($"body {bodyPath} couldn't be read");
        var head = BodyMesh.Load(file).Where(s => s.ModelSpaceNormals).MaxBy(s => s.VertexCount);
        if (head == null) return Skip("no head shape in the FaceGen file");
        var merge = only == null || only.Count > 0;
        var r = NeckReshaper.Reshape(head, body, band, merge: merge, only: only);
        if (r.SeamVertices < 12) return Skip($"neck doesn't meet the body ({r.SeamVertices} edge vertices on it)", head);
        return new Outcome(name, faceGen, bodyPath, weight, r.SeamVertices, r.MovedVertices, r.LargestMove, head, r.Positions, body, null, r.Merges);
    }

    /// <summary>Writes a fitted head into <paramref name="outputRoot"/> at its game path.</summary>
    public void Write(Outcome o, string outputRoot)
    {
        var source = _data.Materialize(o.FaceGenPath)!;
        HeadExporter.WriteNif(source, o.Head.Name, o.Patched, Path.Combine(outputRoot, o.FaceGenPath), o.Merges);
    }

    /// <summary>
    /// The .tri files the NPC's face uses at runtime (its own face part, else its race's default), as data paths
    /// that exist in the load order. NPCs sharing these must merge the same vertices.
    /// </summary>
    public List<string> FaceTris(INpcGetter npc)
    {
        var female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        IHeadPartGetter? face = null;
        foreach (var link in npc.HeadParts)
            if (link.TryResolve(_cache, out var hp) && hp.Type == HeadPart.TypeEnum.Face) { face = hp; break; }
        if (face == null && npc.Race.TryResolve(_cache, out var race))
        {
            var data = female ? race.HeadData?.Female : race.HeadData?.Male;
            foreach (var p in data?.HeadParts ?? Array.Empty<IHeadPartReferenceGetter>())
                if (p.Head.TryResolve(_cache, out var hp) && hp.Type == HeadPart.TypeEnum.Face) { face = hp; break; }
        }
        var tris = new List<string>();
        foreach (var part in face?.Parts ?? Array.Empty<IPartGetter>())
        {
            if (string.IsNullOrEmpty(part.FileName)) continue;
            var path = MeshPath(part.FileName!);
            if (_data.Exists(path)) tris.Add(path.ToLowerInvariant());
        }
        tris.Sort(StringComparer.Ordinal);
        return tris;
    }

    /// <summary>The body's skin shape blended between its _0 and _1 meshes by weight (0..1).</summary>
    private ShapeData? Body(string path, float weight)
    {
        var key = $"{path}|{weight:0.00}";
        if (_bodies.TryGetValue(key, out var cached)) return cached;
        ShapeData? Load(string suffix)
        {
            var file = _data.Materialize(Variant(path, suffix));
            return file == null ? null : BodyMesh.Load(file).Where(s => s.ModelSpaceNormals).MaxBy(s => s.VertexCount);
        }
        var heavy = Load("_1"); var thin = Load("_0");
        ShapeData? result = heavy ?? thin;
        if (heavy != null && thin != null && heavy.VertexCount == thin.VertexCount)
        {
            var p = new float[heavy.Positions.Length];
            for (var i = 0; i < p.Length; i++) p[i] = thin.Positions[i] + (heavy.Positions[i] - thin.Positions[i]) * weight;
            result = new ShapeData
            {
                Name = heavy.Name, NifPath = heavy.NifPath, Positions = p, Uvs = heavy.Uvs, Indices = heavy.Indices,
                DiffusePath = heavy.DiffusePath, ModelSpaceNormals = heavy.ModelSpaceNormals,
            };
        }
        _bodies[key] = result;
        return result;
    }

    private static string MeshPath(string model)
    {
        var p = model.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? p : @"meshes\" + p;
    }

    private static string Variant(string path, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_0") || stem.EndsWith("_1")) stem = stem[..^2];
        return Path.Combine(Path.GetDirectoryName(path)!, stem + suffix + Path.GetExtension(path));
    }
}
