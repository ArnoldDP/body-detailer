using System.Xml.Linq;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using nifly;
using SkinTextureTool.Core.BodySlide;
using SkinTextureTool.Core.Export;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Core.Generation;

/// <summary>
/// Writes grown hair as game files: a skinned NIF (bone weights blended from the body vertices around each card's
/// root), its card textures, and optionally a BodySlide project so the hair is built to any body preset along with
/// the body. Files go under one output folder that stands in for Data.
/// </summary>
public static class HairExporter
{
    /// <summary>The biped slot TNG and SOS genital add-ons use (SBP_52).</summary>
    public const int Slot = 52;
    /// <summary>The folder under meshes and textures the hair goes in.</summary>
    public const string Folder = "Body Detailer Hair";

    public sealed record Result(List<string> Written, List<string> Notes);

    /// <param name="surfaces">The shapes the hair grew on, in the order the hair mesh numbers them.</param>
    /// <param name="sets">The BodySlide slider set chosen for each surface's NIF, or null for none.</param>
    public static Result Export(string output, string name, HairMesh mesh, IReadOnlyList<ShapeData> surfaces, IReadOnlyList<SliderSet?> sets,
        HairMeshLayer look, byte[] atlas, int atlasSize)
    {
        var written = new List<string>(); var notes = new List<string>();
        var file = Safe(name);
        const string folder = Folder;
        var diffuseRel = Path.Combine("textures", folder, file + ".dds");
        var normalRel = Path.Combine("textures", folder, file + "_n.dds");
        if (mesh.VertexCount > ushort.MaxValue) throw new InvalidOperationException($"The hair has {mesh.VertexCount:N0} vertices; a game shape holds at most 65,535. Lower the density or the triangle budget.");

        // textures
        WriteDiffuse(Path.Combine(output, diffuseRel), atlas, atlasSize, look);
        WriteNormal(Path.Combine(output, normalRel), atlas, atlasSize);
        written.Add(diffuseRel); written.Add(normalRel);

        // the mesh as grown, on the body as loaded: usable straight away (both weights, until BodySlide builds them)
        var meshRel = Path.Combine("meshes", folder, file);
        var source = surfaces[0].NifPath;
        foreach (var w in new[] { "_0", "_1" })
        {
            WriteNif(source, surfaces.Select(s => s.Name).ToList(), mesh, mesh.Positions, Path.Combine(output, meshRel + w + ".nif"), file, diffuseRel, normalRel);
            written.Add(meshRel + w + ".nif");
        }

        // the BodySlide project: the hair rebuilt on the slider set's reference body, with every slider's offsets
        var set = sets.FirstOrDefault(s => s != null);
        if (set == null) notes.Add("No BodySlide slider set is chosen for the body (Normal sliders tab), so no BodySlide project was written.");
        else
        {
            try { written.AddRange(WriteBodySlide(output, file, folder, mesh, surfaces, set, diffuseRel, normalRel, notes)); }
            catch (Exception ex) { notes.Add("BodySlide project skipped: " + ex.Message); }
        }
        return new Result(written, notes);
    }

    // ---------- NIF ----------

    /// <summary>
    /// Builds the hair shape in a copy of <paramref name="sourceNif"/> (so it has the body's skeleton), skinned to the
    /// body's bones with each vertex's weights blended from its root triangle, then removes the body shapes.
    /// </summary>
    private static void WriteNif(string sourceNif, IReadOnlyList<string> surfaceShapes, HairMesh mesh, float[] positions, string target, string shapeName,
        string diffuseRel, string normalRel)
    {
        using var nif = new NifFile();
        if (nif.Load(sourceNif) != 0) throw new IOException($"Couldn't read {sourceNif}.");
        var shapes = nif.GetShapes().ToList();
        var bodies = surfaceShapes.Select(n => shapes.FirstOrDefault(s => s.name.get() == n) ?? throw new InvalidDataException($"{Path.GetFileName(sourceNif)} has no shape {n}.")).ToList();

        // bones: the union of the body shapes' bones (node block ids), and each shape's local bone index into it
        var union = new List<int>(); var local = new List<int[]>();
        foreach (var body in bodies)
        {
            var ids = new vectorint();
            nif.GetShapeBoneIDList(body, ids);
            local.Add(ids.Select(id => { var i = union.IndexOf(id); if (i < 0) { union.Add(id); i = union.Count - 1; } return i; }).ToArray());
        }
        var weights = bodies.Select(VertexWeights).ToList();

        var toSkin = new MatTransform();
        nif.GetShapeTransformGlobalToSkin(bodies[0], toSkin);
        var verts = new vectorVector3(); var uvs = new vectorVector2(); var norms = new vectorVector3(); var tris = new vectorTriangle();
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var p = toSkin.ApplyTransform(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
            verts.Add(p);
            uvs.Add(new Vector2(mesh.Uvs[v * 2], mesh.Uvs[v * 2 + 1]));
            norms.Add(new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]));
        }
        for (var i = 0; i < mesh.Indices.Length; i += 3)
            tris.Add(new Triangle((ushort)mesh.Indices[i], (ushort)mesh.Indices[i + 1], (ushort)mesh.Indices[i + 2]));

        var hair = nif.CreateShapeFromData(shapeName, verts, tris, uvs, norms);
        // weights: the root triangle's three vertices' weights, blended by the root's barycentric weights (by union
        // bone index), up to four per vertex
        var vertexWeights = new List<(int Bone, float Weight)>[mesh.VertexCount];
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var si = mesh.RootSurface[v];
            var blend = new Dictionary<int, float>();
            for (var k = 0; k < 3; k++)
            {
                var w = mesh.RootWeights[v * 3 + k];
                foreach (var (bone, bw) in weights[si][mesh.RootVertices[v * 3 + k]])
                {
                    var u = local[si][bone];
                    blend[u] = blend.GetValueOrDefault(u) + bw * w;
                }
            }
            var top = blend.Where(kv => kv.Value > 1e-4f).OrderByDescending(kv => kv.Value).Take(4).ToList();
            var sum = top.Sum(kv => kv.Value);
            vertexWeights[v] = top.Select(kv => (kv.Key, sum > 0 ? kv.Value / sum : 0)).ToList();
        }

        // skinned to only the bones it uses (as hand-made add-ons are), numbered 0..n-1
        var used = vertexWeights.SelectMany(w => w.Select(x => x.Bone)).Distinct().OrderBy(b => b).ToList();
        var compact = used.Select((u, i) => (u, i)).ToDictionary(x => x.u, x => x.i);
        nif.CreateSkinning(hair);
        var boneIds = new vectorint();
        foreach (var u in used) boneIds.Add(union[u]);
        nif.SetShapeBoneIDList(hair, boneIds);
        nif.SetShapeTransformGlobalToSkin(hair, toSkin);
        for (var b = 0; b < used.Count; b++)
        {
            var (si, li) = Enumerable.Range(0, bodies.Count).Select(s => (s, Array.IndexOf(local[s], used[b]))).First(x => x.Item2 >= 0);
            var t = new MatTransform();
            nif.GetShapeTransformSkinToBone(bodies[si], (uint)li, t);
            nif.SetShapeTransformSkinToBone(hair, (uint)b, t);
        }
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var bones = new vectoruchar(); var ws = new vectorfloat();
            foreach (var (bone, w) in vertexWeights[v]) { bones.Add((byte)compact[bone]); ws.Add(w); }
            nif.SetShapeVertWeights(shapeName, (ushort)v, bones, ws);
        }

        // one dismember partition on the add-on slot
        var parts = new NiVectorBSDismemberSkinInstancePartitionInfo();
        parts.push_back(new BSDismemberSkinInstance.PartitionInfo { flags = (PartitionFlags)1, partID = (ushort)Slot });
        var triParts = new vectorint();
        for (var i = 0; i < tris.Count; i++) triParts.Add(0);
        nif.SetShapePartitions(hair, parts, triParts, true);
        nif.UpdateSkinPartitions(hair);
        nif.CalcTangentsForShape(hair);

        // shading: skinned, two sided, alpha tested at the cutoff the hair texture's mips were built for
        var shader = nif.GetShader(hair);
        shader?.SetSkinned(true);
        shader?.SetDoubleSided(true);
        nif.SetTextureSlot(hair, diffuseRel, 0);
        nif.SetTextureSlot(hair, normalRel, 1);
        var alpha = new NiAlphaProperty { flags = 4844, threshold = (byte)(HairAtlas.Cutoff * 255) };
        nif.AssignAlphaProperty(hair, alpha);
        GC.SuppressFinalize(alpha); // the NIF owns it now; the wrapper mustn't free it too

        foreach (var body in nif.GetShapes().Where(s => s.name.get() != shapeName).ToList()) nif.DeleteShape(body);
        FillPartitionBones(nif, used.Count);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        if (nif.Save(target) != 0) throw new IOException($"Couldn't write {target}.");
    }

    /// <summary>
    /// Lists the shape's bones (0..n-1) in its skin partitions. nifly builds the partitions from per-bone weight
    /// lists, which its C# wrapper can't fill, so with weights set per vertex they come out with no bones and the
    /// game skins every vertex to the wrong bone. With every bone in the one partition, the vertices' bone indices
    /// read the same as in the skin, as in hand-made add-ons.
    /// </summary>
    private static void FillPartitionBones(NifFile nif, int boneCount)
    {
        var header = nif.GetHeader();
        for (uint i = 0; i < header.GetNumBlocks(); i++)
        {
            if (header.GetBlockTypeStringById(i) != "NiSkinPartition") continue;
            var block = header.GetBlockById(i);
            var cPtr = (System.Runtime.InteropServices.HandleRef)typeof(NiObject)
                .GetMethod("getCPtr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { block })!;
            var partition = (NiSkinPartition)typeof(NiSkinPartition)
                .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, new[] { typeof(IntPtr), typeof(bool) })!
                .Invoke(new object[] { cPtr.Handle, false });
            var blocks = partition.partitions;
            for (var p = 0; p < blocks.Count; p++)
            {
                var part = blocks[p];
                var bones = new vectoruint16();
                for (var b = 0; b < boneCount; b++) bones.Add((ushort)b);
                part.bones = bones;
                part.numBones = (ushort)boneCount;
                blocks[p] = part;
            }
            partition.partitions = blocks;
        }
    }

    /// <summary>Each vertex's (bone index, weight) pairs, from an SE triangle shape's vertex data.</summary>
    private static List<(int Bone, float Weight)>[] VertexWeights(NiShape shape)
    {
        var cPtr = (System.Runtime.InteropServices.HandleRef)typeof(NiShape)
            .GetMethod("getCPtr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { shape })!;
        var tri = (BSTriShape)typeof(BSTriShape)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, new[] { typeof(IntPtr), typeof(bool) })!
            .Invoke(new object[] { cPtr.Handle, false });
        var data = tri.vertData;
        if (data.Count == 0) throw new InvalidDataException($"{shape.name.get()} has no bone weights (not a skinned Skyrim SE shape).");
        var result = new List<(int, float)>[data.Count];
        for (var v = 0; v < data.Count; v++)
        {
            var d = data[v];
            result[v] = new List<(int, float)>();
            for (var k = 0; k < 4; k++) if (d.weights[k] > 0) result[v].Add((d.weightBones[k], d.weights[k]));
        }
        return result;
    }

    // ---------- BodySlide ----------

    private static IEnumerable<string> WriteBodySlide(string output, string file, string folder, HairMesh mesh, IReadOnlyList<ShapeData> surfaces,
        SliderSet set, string diffuseRel, string normalRel, List<string> notes)
    {
        // the reference body the set's sliders are offsets from
        var sourceNif = Path.Combine(set.ShapeDataFolder, set.SourceFile);
        if (!File.Exists(sourceNif)) throw new FileNotFoundException($"the slider set's reference mesh {sourceNif} isn't there");
        var reference = BodyMesh.Load(sourceNif);
        var refPositions = new List<float[]>();
        foreach (var s in surfaces)
        {
            var r = reference.FirstOrDefault(x => x.Name == s.Name) ?? throw new InvalidDataException($"the reference mesh has no shape {s.Name}");
            if (r.VertexCount != s.VertexCount) throw new InvalidDataException($"{s.Name} has {s.VertexCount} vertices but the reference has {r.VertexCount} (zapped sliders?)");
            refPositions.Add(r.Positions);
        }
        var positions = HairMeshBuilder.Rebase(mesh, refPositions);

        var name = file;
        var bodySlide = Path.Combine(output, "CalienteTools", "BodySlide");
        var dataDir = Path.Combine(bodySlide, "ShapeData", name);
        WriteNif(sourceNif, surfaces.Select(s => s.Name).ToList(), mesh, positions, Path.Combine(dataDir, name + ".nif"), file, diffuseRel, normalRel);

        // each slider's offsets for the hair, conformed as Outfit Studio does it: every hair vertex takes the offsets
        // of the body vertices nearest to it (distance weighted), so a card follows the skin under each part of it,
        // not just under its root
        var nearest = new (int Surface, int[] Vertices, float[] Weights)[mesh.VertexCount];
        Parallel.For(0, mesh.VertexCount, v =>
        {
            var si = mesh.RootSurface[v];
            var (verts, weights) = Nearest(refPositions[si], positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            nearest[v] = (si, verts, weights);
        });
        var osdCache = new Dictionary<string, Dictionary<string, OsdFile.Diff[]>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Slider, string Entry, Dictionary<int, System.Numerics.Vector3> Diffs)>();
        foreach (var slider in set.Sliders.Where(s => s.MovesVertices))
        {
            var perSurface = new Dictionary<int, System.Numerics.Vector3>[surfaces.Count];
            for (var si = 0; si < surfaces.Count; si++)
            {
                perSurface[si] = new Dictionary<int, System.Numerics.Vector3>();
                foreach (var data in slider.Data.Where(d => string.Equals(d.TargetShape, surfaces[si].Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var path = Path.Combine(set.ShapeDataFolder, data.OsdFile);
                    if (!osdCache.TryGetValue(path, out var osd)) osdCache[path] = osd = File.Exists(path) ? OsdFile.Read(path) : new();
                    if (!osd.TryGetValue(data.EntryName, out var diffs)) continue;
                    foreach (var d in diffs) perSurface[si][d.Index] = perSurface[si].GetValueOrDefault(d.Index) + new System.Numerics.Vector3(d.X, d.Y, d.Z);
                }
            }
            var hairDiffs = new Dictionary<int, System.Numerics.Vector3>();
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var (si, verts, weights) = nearest[v];
                var sum = System.Numerics.Vector3.Zero;
                for (var k = 0; k < verts.Length; k++)
                    if (perSurface[si].TryGetValue(verts[k], out var d)) sum += d * weights[k];
                if (sum.LengthSquared() > 1e-10f) hairDiffs[v] = sum;
            }
            if (hairDiffs.Count > 0) entries.Add((slider.Name, file + slider.Name, hairDiffs));
        }
        WriteOsd(Path.Combine(dataDir, name + ".osd"), entries.Select(e => (e.Entry, e.Diffs)).ToList());

        // the project: the body set's own sliders (names, defaults, inversion), each pointing at the hair's data
        var bodyDoc = XDocument.Load(set.OspPath);
        var bodySet = bodyDoc.Descendants("SliderSet").First(s => (string?)s.Attribute("name") == set.Name);
        var outFile = bodySet.Element("OutputFile");
        var project = new XElement("SliderSet", new XAttribute("name", name),
            new XElement("DataFolder", name),
            new XElement("SourceFile", name + ".nif"),
            new XElement("OutputPath", Path.Combine("meshes", folder)),
            new XElement("OutputFile", outFile?.Attributes().Where(a => a.Name == "GenWeights"), file),
            new XElement("Shape", new XAttribute("target", file), file));
        var withData = entries.ToDictionary(e => e.Slider, e => e.Entry, StringComparer.OrdinalIgnoreCase);
        foreach (var s in bodySet.Elements("Slider"))
        {
            var sliderName = (string?)s.Attribute("name") ?? "";
            if (!withData.TryGetValue(sliderName, out var entry)) continue;
            project.Add(new XElement("Slider", s.Attributes(),
                new XElement("Data", new XAttribute("name", entry), new XAttribute("target", file), new XAttribute("local", "true"), $"{name}.osd\\{entry}")));
        }
        var ospRel = Path.Combine("CalienteTools", "BodySlide", "SliderSets", name + ".osp");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(output, ospRel))!);
        new XDocument(new XElement("SliderSetInfo", new XAttribute("version", 1), project)).Save(Path.Combine(output, ospRel));
        notes.Add($"BodySlide: build \"{name}\" with your body preset to fit the hair to it ({entries.Count} sliders carried over from {set.Name}).");
        return new[] { ospRel, Path.Combine("CalienteTools", "BodySlide", "ShapeData", name, name + ".nif"), Path.Combine("CalienteTools", "BodySlide", "ShapeData", name, name + ".osd") };
    }

    /// <summary>The few body vertices nearest a point and their weights (inverse square distance, summing to 1).</summary>
    public static (int[] Vertices, float[] Weights) Nearest(float[] body, float x, float y, float z, int count = 6)
    {
        var best = new (float D, int I)[count];
        for (var k = 0; k < count; k++) best[k] = (float.MaxValue, -1);
        for (var j = 0; j < body.Length / 3; j++)
        {
            var dx = body[j * 3] - x; var dy = body[j * 3 + 1] - y; var dz = body[j * 3 + 2] - z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d >= best[count - 1].D) continue;
            var k = count - 1;
            while (k > 0 && best[k - 1].D > d) { best[k] = best[k - 1]; k--; }
            best[k] = (d, j);
        }
        var found = best.Where(b => b.I >= 0).ToArray();
        var w = found.Select(b => 1f / (b.D + 1e-4f)).ToArray();
        var total = w.Sum();
        return (found.Select(b => b.I).ToArray(), w.Select(x => x / total).ToArray());
    }

    /// <summary>A BodySlide .osd: "\0DSO" header, version 1, then each entry's name and (vertex, offset) pairs.</summary>
    private static void WriteOsd(string path, IReadOnlyList<(string Name, Dictionary<int, System.Numerics.Vector3> Diffs)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(new byte[] { 0, (byte)'D', (byte)'S', (byte)'O' });
        w.Write(1u);
        w.Write((uint)entries.Count);
        foreach (var (name, diffs) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            w.Write((byte)bytes.Length); w.Write(bytes);
            w.Write((ushort)diffs.Count);
            foreach (var (i, d) in diffs.OrderBy(kv => kv.Key)) { w.Write((ushort)i); w.Write(d.X); w.Write(d.Y); w.Write(d.Z); }
        }
    }

    // ---------- textures ----------

    /// <summary>The card diffuse: hair colour by each hair's shade and root darkening, alpha the coverage, with the
    /// coverage-preserving mips so hair survives at a distance. Empty texels take the hair colour (no dark fringes).</summary>
    private static void WriteDiffuse(string path, byte[] atlas, int size, HairMeshLayer look)
    {
        var px = new byte[atlas.Length];
        for (var i = 0; i < atlas.Length; i += 4)
        {
            var shade = atlas[i + 3] > 0 ? (0.6f + 0.6f * atlas[i] / 255f) * float.Lerp(1 - look.RootDarkening, 1, Math.Clamp(atlas[i + 1] / 255f / 0.6f, 0, 1)) : 1f;
            px[i] = (byte)Math.Clamp(look.Color.R * shade * 255, 0, 255);
            px[i + 1] = (byte)Math.Clamp(look.Color.G * shade * 255, 0, 255);
            px[i + 2] = (byte)Math.Clamp(look.Color.B * shade * 255, 0, 255);
            px[i + 3] = atlas[i + 3];
        }
        WriteBc7Mips(path, HairAtlas.Mips(px, size), size);
    }

    /// <summary>The card normal map (tangent space): hairs rounded across by their sideways direction, with the
    /// highlight from each hair's middle to its tip in the alpha (Skyrim reads specular from the normal map's alpha).</summary>
    private static void WriteNormal(string path, byte[] atlas, int size)
    {
        var px = new byte[atlas.Length];
        for (var i = 0; i < atlas.Length; i += 4)
        {
            var a = atlas[i + 3] / 255f;
            var x = (atlas[i + 2] / 255f - 0.5f) * 0.6f * a;
            var z = MathF.Sqrt(MathF.Max(1 - x * x, 0));
            px[i] = (byte)((x * 0.5f + 0.5f) * 255);
            px[i + 1] = 128;
            px[i + 2] = (byte)((z * 0.5f + 0.5f) * 255);
            var t = atlas[i + 1] / 255f;
            px[i + 3] = (byte)(255 * a * 0.6f * Smooth(0.35f, 0.65f, t) * Smooth(1f, 0.85f, t));
        }
        DdsExporter.Write(path, px, size, size);
    }

    private static float Smooth(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>A BC7 DDS from mip levels made here (the encoder would otherwise make its own).</summary>
    private static void WriteBc7Mips(string path, IReadOnlyList<byte[]> levels, int size)
    {
        var encoder = new BcEncoder { OutputOptions = { GenerateMipMaps = false, Quality = CompressionQuality.Balanced, Format = CompressionFormat.Bc7 } };
        var blocks = levels.Select((px, i) => encoder.EncodeToRawBytes(px, Math.Max(1, size >> i), Math.Max(1, size >> i), BCnEncoder.Encoder.PixelFormat.Rgba32)[0]).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x20534444u); // "DDS "
        w.Write(124u); w.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u); // caps, height, width, pixel format, mip count, linear size
        w.Write((uint)size); w.Write((uint)size); w.Write((uint)blocks[0].Length); w.Write(0u); w.Write((uint)blocks.Count);
        for (var i = 0; i < 11; i++) w.Write(0u);
        w.Write(32u); w.Write(0x4u); w.Write(0x30315844u); // pixel format: FourCC "DX10"
        for (var i = 0; i < 5; i++) w.Write(0u);
        w.Write(0x1000u | 0x400000u | 0x8u); // texture, mipmap, complex
        for (var i = 0; i < 4; i++) w.Write(0u);
        w.Write(98u); w.Write(3u); w.Write(0u); w.Write(1u); w.Write(0u); // DXGI BC7_UNORM, 2D, array of 1
        foreach (var b in blocks) w.Write(b);
    }

    public static string Safe(string name)
    {
        var s = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return s.Length == 0 ? "Hair" : s;
    }
}
