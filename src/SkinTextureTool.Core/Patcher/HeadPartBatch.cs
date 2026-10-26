using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using nifly;
using SkinTextureTool.Core.Mesh;
using SkinTextureTool.Core.NeckFix;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// The player-head side of the load order patcher: every playable face head part's mesh is reshaped to meet its
/// race's body (with the extra neck-edge vertices merged), and every .tri built for that mesh is patched to
/// match, wherever it lives: the head part's own morphs and any RaceMenu slider pack under facegenmorphs. A .tri
/// counts as built for the mesh when its stored base shape is the mesh's. Output goes to game paths.
/// </summary>
public static class HeadPartBatch
{
    public sealed record Outcome(int Heads, int Written, int Tris, List<string> Lines);

    public static Outcome Run(IEnumerable<ISkyrimModGetter> priority, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, VirtualData data,
        FaceGenPatcher patcher, string? output, float band, Action<string>? progress)
    {
        var lines = new List<string>();
        // playable face parts, grouped by the mesh they use (several races often share one)
        var byMesh = new Dictionary<string, (bool Female, List<IHeadPartGetter> Parts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in priority.HeadPart().WinningOverrides())
        {
            if (part.Type != HeadPart.TypeEnum.Face || !part.Flags.HasFlag(HeadPart.Flag.Playable)) continue;
            if (part.Model?.File == null) continue;
            var mesh = MeshPath(part.Model.File.ToString());
            if (!data.Exists(mesh)) continue;
            var female = part.Flags.HasFlag(HeadPart.Flag.Female) && !part.Flags.HasFlag(HeadPart.Flag.Male);
            if (!byMesh.TryGetValue(mesh, out var entry)) byMesh[mesh] = entry = (female, new List<IHeadPartGetter>());
            entry.Parts.Add(part);
        }

        // every .tri a RaceMenu slider pack might hold for these heads
        var looseTris = data.Paths(@"meshes\actors\character\facegenmorphs\").Where(p => p.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)).ToList();
        int written = 0, tris = 0;
        foreach (var (mesh, (female, parts)) in byMesh)
        {
            progress?.Invoke($"Fitting head mesh {Path.GetFileName(mesh)}…");
            var file = data.Materialize(mesh)!;
            ShapeData? head;
            try { head = BodyMesh.Load(file).Where(s => s.ModelSpaceNormals).MaxBy(s => s.VertexCount); }
            catch (Exception ex) { lines.Add($"{mesh}: couldn't read ({ex.Message})"); continue; }
            if (head == null) { lines.Add($"{mesh}: no head shape"); continue; }

            // the body of the first race the part is for, at full weight (the engine slims player heads with
            // SkinnyMorph, whose offsets merged vertices share with their partners)
            var bodyPath = parts.Select(p => BodyForPart(p, female, cache, patcher)).FirstOrDefault(b => b != null);
            var body = bodyPath == null ? null : patcher.LoadBody(bodyPath, 1f);
            if (body == null) { lines.Add($"{mesh}: no body found for its races"); continue; }

            NeckReshaper.Result? fit = null;
            foreach (var b in new[] { band, band * 0.6f, band * 0.35f })
            {
                var r = NeckReshaper.Reshape(head, body, b);
                if (r.SeamVertices < 12) break;
                if (NpcBatch.Folds(head.Positions, r.Positions, head.Indices, r.Merges) == 0) { fit = r; break; }
            }
            if (fit == null) { lines.Add($"{mesh}: left alone (its neck doesn't meet {Path.GetFileName(bodyPath)} or the fit would fold)"); continue; }

            var originalLocal = HeadExporter.LocalPositions(file, head.Name);
            float[] local;
            if (output != null)
            {
                local = HeadExporter.WriteNif(file, head.Name, fit.Positions, Path.Combine(output, mesh), fit.Merges);
                written++;
            }
            else local = fit.Positions;

            // .tri files built for this mesh: the parts' own, and slider packs under facegenmorphs
            var candidates = parts.SelectMany(p => p.Parts).Where(p => !string.IsNullOrEmpty(p.FileName)).Select(p => MeshPath(p.FileName!))
                .Concat(looseTris).Distinct(StringComparer.OrdinalIgnoreCase);
            var patched = 0;
            foreach (var tri in candidates)
            {
                if (TriFile.VertexCount(data, tri) != head.VertexCount) continue;
                var source = data.Materialize(tri);
                if (source == null || !TriFile.SameBase(source, originalLocal)) continue;
                if (output != null)
                {
                    try { if (TriFile.PatchBase(source, Path.Combine(output, tri), local, fit.Merges)) patched++; }
                    catch (Exception ex) { lines.Add($"{tri}: {ex.Message}"); }
                }
                else patched++;
            }
            tris += patched;
            lines.Add($"{mesh} ({(female ? "female" : "male")}, {parts.Count} head part(s)): seam {fit.SeamVertices} vertices, {fit.Merges.Count} merged, against {Path.GetFileName(bodyPath)}; {patched} .tri file(s)");
        }
        return new Outcome(byMesh.Count, written, tris, lines);
    }

    private static string? BodyForPart(IHeadPartGetter part, bool female, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, FaceGenPatcher patcher)
    {
        if (part.ValidRaces.TryResolve(cache, out var list))
            foreach (var item in list.Items)
                if (cache.TryResolve<IRaceGetter>(item.FormKey, out var race) && patcher.BodyPath(race, female) is { } path)
                    return path;
        return female ? @"meshes\actors\character\character assets\femalebody_1.nif" : @"meshes\actors\character\character assets\malebody_1.nif";
    }

    private static string MeshPath(string model)
    {
        var p = model.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? p : @"meshes\" + p;
    }
}
