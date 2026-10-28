using System.Numerics;
using System.Xml.Linq;
using nifly;
using Vector3 = System.Numerics.Vector3;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool.Core.BodySlide;

/// <summary>A BodySlide preset: slider values (0..1) for the small (_0) and big (_1) weights, by slider name.</summary>
public sealed record SliderPreset(string Name, string? Set, IReadOnlyList<string> Groups, IReadOnlyDictionary<string, (float? Small, float? Big)> Values)
{
    public override string ToString() => Name;

    /// <summary>
    /// Every preset in the load order's CalienteTools\BodySlide\SliderPresets (the winning file of each name),
    /// as BodySlide's preset reader takes them: values in percent, by size.
    /// </summary>
    public static List<SliderPreset> All(VirtualData data)
    {
        var presets = new Dictionary<string, SliderPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in data.Paths(Path.Combine("CalienteTools", "BodySlide", "SliderPresets")).Where(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var file = data.Materialize(rel);
            if (file == null) continue;
            try
            {
                foreach (var p in XDocument.Load(file).Descendants("Preset"))
                {
                    var values = new Dictionary<string, (float?, float?)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in p.Elements("SetSlider"))
                    {
                        var name = (string?)s.Attribute("name"); var size = (string?)s.Attribute("size");
                        if (name == null || !float.TryParse((string?)s.Attribute("value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
                        values.TryGetValue(name, out var cur);
                        values[name] = size == "small" ? (v / 100f, cur.Item2) : size == "big" ? (cur.Item1, v / 100f) : (v / 100f, v / 100f);
                    }
                    var preset = new SliderPreset((string?)p.Attribute("name") ?? Path.GetFileNameWithoutExtension(file), (string?)p.Attribute("set"),
                        p.Elements("Group").Select(g => (string?)g.Attribute("name") ?? "").Where(g => g.Length > 0).ToList(), values);
                    presets[preset.Name] = preset;
                }
            }
            catch { /* a broken preset file is skipped, as BodySlide does */ }
        }
        return presets.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Builds a BodySlide project with a preset, as BodySlide's BuildBodies does (GPL-3.0): each slider's offsets added
/// to the reference shapes at its value (1 - value when inverted; the set's defaults where the preset has none), for
/// the small (_0) and big (_1) weights; normals and tangents recalculated; and with morphs, the .tri of every
/// slider's full offsets per shape, linked from the meshes by a BODYTRI string.
/// </summary>
public static class Builder
{
    /// <param name="output">The folder that stands in for Data.</param>
    /// <returns>The files written, relative to <paramref name="output"/>.</returns>
    public static List<string> Build(SliderSet set, SliderPreset? preset, string output, bool morphs, List<string> notes)
    {
        var doc = XDocument.Load(set.OspPath).Descendants("SliderSet").First(s => (string?)s.Attribute("name") == set.Name);
        var outputPath = (doc.Element("OutputPath")?.Value ?? "").Trim().Replace('/', '\\');
        var outputFile = (doc.Element("OutputFile")?.Value ?? set.Name).Trim();
        var genWeights = string.Equals((string?)doc.Element("OutputFile")?.Attribute("GenWeights"), "true", StringComparison.OrdinalIgnoreCase);
        // slider defaults from the set, by name
        var defaults = doc.Elements("Slider").ToDictionary(s => (string?)s.Attribute("name") ?? "", s => (Small: Pct(s, "small"), Big: Pct(s, "big"), Invert: Flag(s, "invert")), StringComparer.OrdinalIgnoreCase);
        float Value(string slider, bool big)
        {
            var d = defaults.GetValueOrDefault(slider);
            var v = preset != null && preset.Values.TryGetValue(slider, out var pv) && (big ? pv.Big : pv.Small) is { } set1 ? set1 : big ? d.Big : d.Small;
            return d.Invert ? 1 - v : v;
        }

        // the set's offsets, per slider and shape
        var osd = new Dictionary<string, Dictionary<string, OsdFile.Diff[]>>(StringComparer.OrdinalIgnoreCase);
        OsdFile.Diff[]? Diffs(SliderData data)
        {
            var path = Path.Combine(set.ShapeDataFolder, data.OsdFile);
            if (!osd.TryGetValue(path, out var file)) osd[path] = file = File.Exists(path) ? OsdFile.Read(path) : new();
            return file.TryGetValue(data.EntryName, out var d) ? d : null;
        }
        if (set.Sliders.Any(s => s.Zap)) notes.Add($"{set.Name} has zap sliders; they're left out of this build.");

        var source = Path.Combine(set.ShapeDataFolder, set.SourceFile);
        var written = new List<string>();
        var triRel = Path.Combine(outputPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? outputPath["meshes\\".Length..] : outputPath, outputFile + ".tri");
        foreach (var (suffix, big) in genWeights ? new[] { ("_0", false), ("_1", true) } : new[] { ("", true) })
        {
            using var nif = new NifFile();
            if (nif.Load(source) != 0) throw new IOException($"Couldn't read {source}.");
            foreach (var shapeName in set.Shapes)
            {
                var shape = nif.GetShapes().FirstOrDefault(s => s.name.get() == shapeName);
                if (shape == null) continue;
                var verts = nif.GetVertsForShape(shape).Select(v => new Vector3(v.x, v.y, v.z)).ToArray();
                foreach (var slider in set.Sliders.Where(s => s.MovesVertices))
                {
                    var value = Value(slider.Name, big);
                    if (value == 0) continue;
                    foreach (var data in slider.Data.Where(d => string.Equals(d.TargetShape, shapeName, StringComparison.OrdinalIgnoreCase)))
                        foreach (var d in Diffs(data) ?? Array.Empty<OsdFile.Diff>())
                            if (d.Index < verts.Length) verts[d.Index] += new Vector3(d.X, d.Y, d.Z) * value;
                }
                var list = new vectorVector3();
                foreach (var v in verts) list.Add(new nifly.Vector3(v.X, v.Y, v.Z));
                nif.SetVertsForShape(shape, list);
                nif.CalcNormalsForShape(shape);
                nif.CalcTangentsForShape(shape);
            }
            if (morphs) SetBodyTri(nif, triRel);
            var rel = Path.Combine(outputPath, outputFile + suffix + ".nif");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(output, rel))!);
            if (nif.Save(Path.Combine(output, rel)) != 0) throw new IOException($"Couldn't write {rel}.");
            written.Add(rel);
        }

        if (morphs)
        {
            WriteTri(Path.Combine(output, "meshes", triRel), set, Diffs);
            written.Add(Path.Combine("meshes", triRel));
        }
        return written;
    }

    /// <summary>
    /// The one BODYTRI link, where BodySlide puts it for Skyrim: on the first shape with vertices (the game drops a
    /// skinned armor mesh's root node when it attaches the shapes, so a link there never reaches RaceMenu), with
    /// every shape marked dynamic so its vertices can be moved.
    /// </summary>
    private static void SetBodyTri(NifFile nif, string triRel)
    {
        var header = nif.GetHeader();
        for (var i = (int)header.GetNumBlocks() - 1; i >= 0; i--)
            if (header.GetBlockTypeStringById((uint)i) == "NiStringExtraData" && Cast<NiStringExtraData>(header.GetBlockById((uint)i)).name.get() == "BODYTRI")
                header.DeleteBlock((uint)i);
        var shapes = nif.GetShapes().ToList();
        var target = shapes.FirstOrDefault(s => nif.GetVertsForShape(s).Count > 0);
        if (target == null) return;
        var link = new NiStringExtraData { name = new NiStringRef("BODYTRI"), stringData = new NiStringRef(triRel) };
        nif.AssignExtraData(target, link);
        GC.SuppressFinalize(link); // the NIF owns it now; the wrapper mustn't free it too
        foreach (var shape in shapes) nif.SetShapeDynamic(shape.name.get());
    }

    /// <summary>nifly hands blocks back as their base type; this views the same native object as <typeparamref name="T"/>.</summary>
    private static T Cast<T>(object block)
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var handle = (System.Runtime.InteropServices.HandleRef)typeof(NiObject).GetMethod("getCPtr", any, new[] { typeof(NiObject) })!.Invoke(null, new[] { block })!;
        return (T)typeof(T).GetConstructor(any, new[] { typeof(IntPtr), typeof(bool) })!.Invoke(new object[] { handle.Handle, false });
    }

    /// <summary>BodySlide's morph .tri ("PIRT"): per shape, every vertex-moving slider's full offsets as int16 scaled by the largest.</summary>
    private static void WriteTri(string path, SliderSet set, Func<SliderData, OsdFile.Diff[]?> diffs)
    {
        var shapes = new List<(string Shape, List<(string Morph, Dictionary<int, Vector3> Offsets)> Morphs)>();
        foreach (var shapeName in set.Shapes)
        {
            var morphs = new List<(string, Dictionary<int, Vector3>)>();
            foreach (var slider in set.Sliders.Where(s => s.MovesVertices))
            {
                var offsets = new Dictionary<int, Vector3>();
                foreach (var data in slider.Data.Where(d => string.Equals(d.TargetShape, shapeName, StringComparison.OrdinalIgnoreCase)))
                    foreach (var d in diffs(data) ?? Array.Empty<OsdFile.Diff>())
                        offsets[d.Index] = offsets.GetValueOrDefault(d.Index) + new Vector3(d.X, d.Y, d.Z);
                // dropped as BodySlide does (nifly's IsZero with its epsilon, per component)
                foreach (var k in offsets.Where(kv => MathF.Abs(kv.Value.X) < 1e-4f && MathF.Abs(kv.Value.Y) < 1e-4f && MathF.Abs(kv.Value.Z) < 1e-4f).Select(kv => kv.Key).ToList()) offsets.Remove(k);
                if (offsets.Count > 0) morphs.Add((slider.Name, offsets));
            }
            if (morphs.Count > 0) shapes.Add((shapeName, morphs));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("PIRT"u8.ToArray());
        w.Write((ushort)shapes.Count);
        foreach (var (shape, morphs) in shapes)
        {
            Name(w, shape);
            w.Write((ushort)morphs.Count);
            foreach (var (morph, offsets) in morphs)
            {
                Name(w, morph);
                var mult = offsets.Values.Max(v => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)))) / 0x7FFF;
                w.Write(mult);
                w.Write((ushort)offsets.Count);
                foreach (var (i, v) in offsets.OrderBy(kv => kv.Key))
                {
                    w.Write((ushort)i);
                    w.Write((short)(v.X / mult)); w.Write((short)(v.Y / mult)); w.Write((short)(v.Z / mult));
                }
            }
        }
        w.Write((ushort)0); // no UV morphs
    }

    private static void Name(BinaryWriter w, string s) { var b = System.Text.Encoding.UTF8.GetBytes(s); w.Write((byte)b.Length); w.Write(b); }
    private static float Pct(XElement e, string a) => float.TryParse((string?)e.Attribute(a), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v / 100f : 0f;
    private static bool Flag(XElement e, string a) => string.Equals((string?)e.Attribute(a), "true", StringComparison.OrdinalIgnoreCase);
}
