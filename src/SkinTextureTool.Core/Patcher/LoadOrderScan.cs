using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using nifly;
using Noggog;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// Phase 1 of the load order patcher: reads the enabled plugins and the virtual Data folder, and reports which
/// heads, bodies, hands and feet are in use per race, and which NPCs' FaceGen heads could be fitted to their
/// body. Writes nothing.
/// </summary>
public static class LoadOrderScan
{
    public sealed record MeshInfo(string Path, bool Found, int Vertices);
    public sealed record HeadInfo(string EditorId, string Plugin, MeshInfo Mesh, List<string> Tris);
    public sealed record RaceInfo(string EditorId, string Plugin, bool Playable, Dictionary<string, MeshInfo?> Female, Dictionary<string, MeshInfo?> Male,
        HeadInfo? FemaleHead, HeadInfo? MaleHead);
    public sealed record NpcSummary(int Total, int WithFaceGen, int FaceGenMissing, int Fixable, int CustomSkin, int Templated,
        Dictionary<string, int> FixableByPlugin, Dictionary<string, int> MismatchByPlugin, List<string> Examples);

    public sealed record Report(string DataFolder, int Plugins, IReadOnlyList<string> Archives, List<RaceInfo> Races, NpcSummary Npcs, List<string> Problems)
    {
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Data folder: {DataFolder}");
            sb.AppendLine($"{Plugins} plugins in the load order, {Archives.Count} archives read.");
            sb.AppendLine();
            sb.AppendLine("PLAYABLE RACES");
            foreach (var r in Races.Where(r => r.Playable))
            {
                sb.AppendLine($"  {r.EditorId}  ({r.Plugin})");
                foreach (var (sex, parts, head) in new[] { ("female", r.Female, r.FemaleHead), ("male", r.Male, r.MaleHead) })
                {
                    sb.AppendLine($"    {sex}: head {Describe(head?.Mesh)}{(head != null ? $" [{head.EditorId} from {head.Plugin}, {head.Tris.Count} tri file(s)]" : "")}");
                    foreach (var (slot, mesh) in parts) sb.AppendLine($"      {slot,-5} {Describe(mesh)}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("NPCS");
            sb.AppendLine($"  {Npcs.Total} NPC records; {Npcs.WithFaceGen} have a FaceGen head, {Npcs.FaceGenMissing} expect one that isn't there, {Npcs.Templated} take their face from a template.");
            sb.AppendLine($"  {Npcs.Fixable} FaceGen heads are built on the race's head mesh (same vertex count) and could be fitted to the body.");
            sb.AppendLine($"  {Npcs.CustomSkin} NPCs wear their own skin (a replacer body or hands/feet).");
            if (Npcs.FixableByPlugin.Count > 0)
            {
                sb.AppendLine("  Fixable FaceGen heads by plugin:");
                foreach (var (p, n) in Npcs.FixableByPlugin.OrderByDescending(kv => kv.Value)) sb.AppendLine($"    {n,6}  {p}");
            }
            if (Npcs.MismatchByPlugin.Count > 0)
            {
                sb.AppendLine("  FaceGen heads built on a different head mesh (would be skipped):");
                foreach (var (p, n) in Npcs.MismatchByPlugin.OrderByDescending(kv => kv.Value)) sb.AppendLine($"    {n,6}  {p}");
            }
            if (Npcs.Examples.Count > 0)
            {
                sb.AppendLine("  Examples:");
                foreach (var e in Npcs.Examples) sb.AppendLine("    " + e);
            }
            if (Problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("PROBLEMS");
                foreach (var p in Problems.Distinct().Take(200)) sb.AppendLine("  " + p);
            }
            return sb.ToString();
        }

        private static string Describe(MeshInfo? m) => m == null ? "(none)" : !m.Found ? $"{m.Path} (MISSING)" : $"{m.Path} ({m.Vertices} vertices)";
    }

    private static readonly (string Slot, BipedObjectFlag Flag)[] Slots =
    {
        ("body", BipedObjectFlag.Body), ("hands", BipedObjectFlag.Hands), ("feet", BipedObjectFlag.Feet),
    };

    public static Report Run(string mo2Folder, Action<string>? progress = null)
    {
        progress?.Invoke("Reading the MO2 instance…");
        var mo2 = new Mo2Instance(mo2Folder);
        progress?.Invoke($"{mo2.EnabledMods} enabled mods, {mo2.Plugins.Count} enabled plugins, {mo2.Files.Count} files; game data {mo2.GameData}");
        var problems = new List<string>();

        // plugins in load order; masters are read before the plugins that need them, so light/medium masters resolve
        progress?.Invoke("Reading plugins…");
        var styles = new Cache<IModMasterStyledGetter, ModKey>(m => m.ModKey);
        var mods = new List<ISkyrimModGetter>();
        foreach (var plugin in mo2.Plugins)
        {
            try
            {
                var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(mo2.Files[plugin]), SkyrimRelease.SkyrimSE,
                    new BinaryReadParameters { MasterFlagsLookup = styles });
                mods.Add(mod);
                styles.Set(mod);
            }
            catch (Exception ex)
            {
                problems.Add($"Couldn't read {plugin}: {ex.Message}");
            }
        }
        var priority = Enumerable.Reverse(mods).ToList(); // winners first
        var cache = mods.ToImmutableLinkCache();
        progress?.Invoke("Indexing archives…");
        using var data = new VirtualData(mo2);
        problems.AddRange(data.Problems);
        progress?.Invoke($"{data.Archives.Count} archives indexed");
        var vertexCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        MeshInfo Mesh(string? modelPath, bool weighted = false)
        {
            var rel = MeshPath(modelPath ?? "");
            if (weighted) rel = WeightVariant(rel, "_1");
            if (!vertexCounts.TryGetValue(rel, out var count))
            {
                progress?.Invoke("mesh " + rel);
                count = -1;
                var file = data.Materialize(rel);
                if (file != null) count = SkinVertexCount(file);
                vertexCounts[rel] = count;
            }
            return new MeshInfo(rel, count >= 0, Math.Max(count, 0));
        }

        // ---- races ----
        progress?.Invoke("Reading races…");
        var races = new List<RaceInfo>();
        var raceHeads = new Dictionary<FormKey, (HeadInfo? F, HeadInfo? M)>();
        foreach (var race in priority.Race().WinningOverrides())
        {
            var playable = race.Flags.HasFlag(Race.Flag.Playable);
            var female = new Dictionary<string, MeshInfo?>(); var male = new Dictionary<string, MeshInfo?>();
            if (race.Skin.TryResolve(cache, out var skin))
                foreach (var (slot, flag) in Slots)
                {
                    var addon = skin.Armature.Select(a => a.TryResolve(cache, out var aa) ? aa : null)
                        .FirstOrDefault(aa => aa != null && aa.BodyTemplate != null && aa.BodyTemplate.FirstPersonFlags.HasFlag(flag)
                                              && (aa.Race.FormKey == race.FormKey || aa.AdditionalRaces.Any(r => r.FormKey == race.FormKey)));
                    female[slot] = addon?.WorldModel?.Female?.File is { } ff ? Mesh(ff, weighted: true) : null;
                    male[slot] = addon?.WorldModel?.Male?.File is { } mf ? Mesh(mf, weighted: true) : null;
                }
            HeadInfo? Head(IHeadDataGetter? hd)
            {
                var face = hd?.HeadParts.Select(p => p.Head.TryResolve(cache, out var h) ? h : null)
                    .FirstOrDefault(h => h != null && h.Type == HeadPart.TypeEnum.Face);
                if (face?.Model?.File == null) return null;
                var tris = face.Parts.Where(p => !string.IsNullOrEmpty(p.FileName)).Select(p => $"{p.PartType}: {MeshPath(p.FileName!)}").ToList();
                return new HeadInfo(face.EditorID ?? face.FormKey.ToString(), face.FormKey.ModKey.FileName, Mesh(face.Model.File), tris);
            }
            var fh = Head(race.HeadData?.Female); var mh = Head(race.HeadData?.Male);
            raceHeads[race.FormKey] = (fh, mh);
            races.Add(new RaceInfo(race.EditorID ?? race.FormKey.ToString(), race.FormKey.ModKey.FileName, playable, female, male, fh, mh));
        }

        // ---- NPCs ----
        progress?.Invoke("Reading NPCs…");
        int total = 0, withFaceGen = 0, missing = 0, fixable = 0, customSkin = 0, templated = 0;
        var fixableBy = new Dictionary<string, int>(); var mismatchBy = new Dictionary<string, int>();
        var examples = new List<string>();
        foreach (var npc in priority.Npc().WinningOverrides())
        {
            total++;
            if (!npc.WornArmor.IsNull) customSkin++;
            if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) { templated++; continue; }
            // FaceGen lives under the plugin that first defined the NPC
            var faceGen = $@"meshes\actors\character\facegendata\facegeom\{npc.FormKey.ModKey.FileName}\{npc.FormKey.ID:X8}.nif";
            if (!data.Exists(faceGen))
            {
                if (npc.Race.TryResolve(cache, out var r) && r.Flags.HasFlag(Race.Flag.Playable)) missing++;
                continue;
            }
            withFaceGen++;
            if (total % 500 == 0) progress?.Invoke($"Reading NPCs… {total}");
            var female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
            var head = raceHeads.TryGetValue(npc.Race.FormKey, out var h) ? (female ? h.F : h.M) : null;
            // an NPC can pick its own face part; otherwise the race's default
            var own = npc.HeadParts.Select(p => p.TryResolve(cache, out var hp) ? hp : null).FirstOrDefault(hp => hp?.Type == HeadPart.TypeEnum.Face);
            var expected = own?.Model?.File != null ? Mesh(own.Model.File).Vertices : head?.Mesh.Vertices ?? 0;
            progress?.Invoke("facegen " + faceGen);
            var file = data.Materialize(faceGen);
            var faceVerts = file == null ? -1 : FaceGenHeadVertices(file);
            var origin = npc.FormKey.ModKey.FileName.String;
            if (faceVerts > 0 && faceVerts == expected)
            {
                fixable++;
                fixableBy[origin] = fixableBy.GetValueOrDefault(origin) + 1;
            }
            else
            {
                mismatchBy[origin] = mismatchBy.GetValueOrDefault(origin) + 1;
                if (examples.Count < 15) examples.Add($"{npc.EditorID} ({npc.FormKey}): FaceGen head {faceVerts} vertices, head mesh {expected}");
            }
        }
        var summary = new NpcSummary(total, withFaceGen, missing, fixable, customSkin, templated, fixableBy, mismatchBy, examples);
        return new Report($"{mo2.Root} (profile {mo2.Profile}); game data {mo2.GameData}", mods.Count, data.Archives, races, summary, problems);
    }

    private static string MeshPath(string model)
    {
        var p = model.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? p : @"meshes\" + p;
    }

    /// <summary>Body meshes come in _0 (thin) and _1 (heavy) variants; records name either.</summary>
    private static string WeightVariant(string path, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith("_0") || stem.EndsWith("_1")) stem = stem[..^2];
        return Path.Combine(Path.GetDirectoryName(path)!, stem + suffix + Path.GetExtension(path));
    }

    /// <summary>Vertex count of the NIF's skin shape (the one with a model-space normal map), or its largest shape.</summary>
    private static int SkinVertexCount(string file)
    {
        try
        {
            using var nif = new NifFile();
            if (nif.Load(file) != 0) return -1;
            var shapes = nif.GetShapes().ToList();
            var skin = shapes.FirstOrDefault(s => nif.GetShader(s)?.IsModelSpace() == true) ?? shapes.MaxBy(s => s.GetNumVertices());
            return skin == null ? -1 : (int)skin.GetNumVertices();
        }
        catch { return -1; }
    }

    /// <summary>The head shape in a FaceGen NIF: model-space normals and the most vertices.</summary>
    private static int FaceGenHeadVertices(string file)
    {
        try
        {
            using var nif = new NifFile();
            if (nif.Load(file) != 0) return -1;
            var shapes = nif.GetShapes().Where(s => nif.GetShader(s)?.IsModelSpace() == true).ToList();
            return shapes.Count == 0 ? -1 : (int)shapes.Max(s => s.GetNumVertices());
        }
        catch { return -1; }
    }
}
