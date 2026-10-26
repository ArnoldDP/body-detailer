using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using SkinTextureTool.Core.Mesh;
using SkinTextureTool.Core.NeckFix;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// Wrists and ankles: every race's hands and feet meshes are bent so they leave the seam in the direction the body
/// arrives, each weight variant (_0, _1) against the body's matching variant, as the game blends them by the same
/// weight. The pieces already share their seam vertices with the body, so nothing is merged; skin weights and the
/// file's other shapes (nails…) are untouched.
/// </summary>
public static class LimbBatch
{
    public sealed record Outcome(int Meshes, int Written, List<string> Lines);

    private static readonly (string Name, BipedObjectFlag Flag)[] Pieces = { ("hands", BipedObjectFlag.Hands), ("feet", BipedObjectFlag.Feet) };

    public static Outcome Run(IEnumerable<ISkyrimModGetter> priority, ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, VirtualData data,
        FaceGenPatcher patcher, string? output, float band, Action<string>? progress, bool skipBeasts = true)
    {
        var lines = new List<string>();
        // (piece mesh, body mesh) pairs from every race's skin, each pair once
        var pairs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var race in priority.Race().WinningOverrides().Where(r => !skipBeasts || !patcher.IsBeast(r)))
            foreach (var female in new[] { true, false })
            {
                var body = patcher.BodyPath(race, female);
                if (body == null) continue;
                foreach (var (_, flag) in Pieces)
                    if (patcher.PiecePath(race, female, flag) is { } piece && !pairs.ContainsKey(piece))
                        pairs[piece] = body;
            }

        var written = 0;
        foreach (var (piece, body) in pairs)
        {
            progress?.Invoke($"Fitting {Path.GetFileName(piece)}…");
            foreach (var suffix in new[] { "_0", "_1" })
            {
                var piecePath = FaceGenPatcher.Variant(piece, suffix);
                var bodyPath = FaceGenPatcher.Variant(body, suffix);
                var pieceFile = data.Materialize(piecePath);
                var bodyFile = data.Materialize(bodyPath);
                if (pieceFile == null || bodyFile == null) continue;
                ShapeData? shape, bodyShape;
                try
                {
                    shape = BodyMesh.Load(pieceFile).Where(s => s.ModelSpaceNormals).MaxBy(s => s.VertexCount);
                    bodyShape = BodyMesh.Load(bodyFile).Where(s => s.ModelSpaceNormals).MaxBy(s => s.VertexCount);
                }
                catch (Exception ex) { lines.Add($"{piecePath}: couldn't read ({ex.Message})"); continue; }
                if (shape == null || bodyShape == null) { lines.Add($"{piecePath}: no skin shape"); continue; }

                NeckReshaper.Result? fit = null;
                foreach (var b in new[] { band, band * 0.6f, band * 0.35f })
                {
                    var r = NeckReshaper.Reshape(shape, bodyShape, b, merge: false, projectToPlane: true);
                    if (r.SeamVertices < 12) break;
                    if (NpcBatch.Folds(shape.Positions, r.Positions, shape.Indices, r.Merges) == 0) { fit = r; break; }
                }
                if (fit == null) { lines.Add($"{piecePath}: left alone (its edge doesn't meet {Path.GetFileName(bodyPath)} or the bend would fold)"); continue; }
                if (output != null)
                {
                    try { HeadExporter.WriteNif(pieceFile, shape.Name, fit.Positions, Path.Combine(output, piecePath)); written++; }
                    catch (Exception ex) { lines.Add($"{piecePath}: couldn't write ({ex.Message})"); continue; }
                }
                lines.Add($"{piecePath}: seam {fit.SeamVertices} vertices, {fit.MovedVertices} moved (at most {fit.LargestMove:0.00}), against {Path.GetFileName(bodyPath)}");
            }
        }
        return new Outcome(pairs.Count, written, lines);
    }
}
