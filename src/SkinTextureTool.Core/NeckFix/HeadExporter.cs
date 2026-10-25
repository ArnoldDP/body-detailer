using nifly;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Core.NeckFix;

/// <summary>
/// Writes a reshaped head: the NIF with the head shape's new vertex positions, and every FaceGen .tri next to it
/// built for the same vertices with its base positions replaced. Morphs in a .tri are offsets from the base, so
/// expressions, race and character-creation sliders keep working on the new shape.
/// </summary>
public static class HeadExporter
{
    public sealed record Report(string NifPath, List<string> TriPaths, List<string> Skipped);

    /// <param name="positions">The head shape's new positions, in the space BodyMesh loads (as drawn in game).</param>
    /// <param name="outputRoot">Folder standing in for Data: files land at their path under "meshes".</param>
    public static Report Export(ShapeData head, float[] positions, string outputRoot)
    {
        var relative = DataRelative(head.NifPath)
            ?? throw new InvalidOperationException($"{head.NifPath} isn't under a meshes folder, so its game path is unknown.");
        var nifOut = Path.Combine(outputRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(nifOut)!);

        // positions back into the shape's own vertex space, which the NIF and the .tri files store
        float[] local;
        using (var nif = new NifFile())
        {
            if (nif.Load(head.NifPath) != 0) throw new InvalidDataException($"Couldn't read {head.NifPath}.");
            var shape = nif.GetShapes().FirstOrDefault(s => s.name.get() == head.Name)
                ?? throw new InvalidDataException($"{head.Name} isn't in {head.NifPath}.");
            var toSkin = new MatTransform();
            if (!nif.GetShapeTransformGlobalToSkin(shape, toSkin)) toSkin = shape.transform.InverseTransform();
            var verts = new vectorVector3();
            local = new float[positions.Length];
            for (var i = 0; i < positions.Length; i += 3)
            {
                var p = toSkin.ApplyTransform(new Vector3(positions[i], positions[i + 1], positions[i + 2]));
                verts.Add(p);
                local[i] = p.x; local[i + 1] = p.y; local[i + 2] = p.z;
            }
            nif.SetVertsForShape(shape, verts);
            nif.CalcNormalsForShape(shape);
            nif.CalcTangentsForShape(shape);
            nif.UpdateSkinPartitions(shape);
            if (nif.Save(nifOut) != 0) throw new IOException($"Couldn't write {nifOut}.");
        }

        // every .tri in the head's mod built for these vertices: next to the head, and the expression and slider
        // morphs under facegenmorphs. Only ones named for this head (female/male) are touched.
        var tris = new List<string>(); var skipped = new List<string>();
        var modMeshes = MeshesFolder(head.NifPath)!;
        var stem = Path.GetFileNameWithoutExtension(head.NifPath).ToLowerInvariant();
        var female = stem.Contains("female");
        foreach (var tri in Directory.EnumerateFiles(modMeshes, "*.tri", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(tri).ToLowerInvariant();
            var forThisHead = name.Contains(stem) || (female ? name.Contains("female") : name.Contains("male") && !name.Contains("female"));
            if (!forThisHead || name.Contains("beard") || name.Contains("brow") || name.Contains("eye")) continue;
            var target = Path.Combine(outputRoot, DataRelative(tri)!);
            if (TriFile.PatchBase(tri, target, local)) tris.Add(target);
            else skipped.Add(Path.GetRelativePath(modMeshes, tri) + " (built for a different vertex count)");
        }
        return new Report(nifOut, tris, skipped);
    }

    /// <summary>"meshes\...\file.nif" from a full path, or null if there's no meshes folder in it.</summary>
    private static string? MeshesFolder(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
        var i = Array.FindLastIndex(parts, p => p.Equals("meshes", StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : string.Join(Path.DirectorySeparatorChar, parts[..(i + 1)]);
    }

    public static string? DataRelative(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
        var i = Array.FindLastIndex(parts, p => p.Equals("meshes", StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : Path.Combine(parts[i..]);
    }
}

/// <summary>Bethesda FaceGen .tri (FRTRI003): just enough to replace the base vertex positions.</summary>
public static class TriFile
{
    private const int HeaderSize = 64; // magic + 14 ints

    public static int VertexCount(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        return new string(r.ReadChars(8)) == "FRTRI003" ? r.ReadInt32() : -1;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/> with the first vertices replaced; false if the counts differ.</summary>
    public static bool PatchBase(string source, string target, float[] positions)
    {
        var data = File.ReadAllBytes(source);
        if (data.Length < HeaderSize || System.Text.Encoding.ASCII.GetString(data, 0, 8) != "FRTRI003") return false;
        var count = BitConverter.ToInt32(data, 8);
        if (count * 3 != positions.Length) return false;
        for (var i = 0; i < positions.Length; i++)
            BitConverter.TryWriteBytes(data.AsSpan(HeaderSize + i * 4, 4), positions[i]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, data);
        return true;
    }
}
