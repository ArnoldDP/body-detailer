using nifly;

namespace SkinTextureTool.Core.Mesh;

/// <summary>One shape from a body NIF, in the shape's own vertex space (the space model-space normal maps use).</summary>
public sealed class ShapeData
{
    public required string Name { get; init; }
    /// <summary>The NIF the shape came from; shapes from several NIFs can be loaded together.</summary>
    public required string NifPath { get; init; }
    public required float[] Positions { get; init; } // xyz
    public required float[] Uvs { get; init; }       // uv, v down as in the texture
    public required uint[] Indices { get; init; }
    public string? DiffusePath { get; init; }
    public bool ModelSpaceNormals { get; init; }

    public int VertexCount => Positions.Length / 3;

    /// <summary>Shapes that use the same diffuse texture are generated into one texture set, named after it.</summary>
    public string TextureName => DiffusePath != null ? Path.GetFileNameWithoutExtension(DiffusePath).ToLowerInvariant() : Name.ToLowerInvariant();
    public int TriangleCount => Indices.Length / 3;
}

public static class BodyMesh
{
    /// <summary>Reads every textured shape from a NIF file.</summary>
    public static List<ShapeData> Load(string path)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) throw new InvalidDataException($"Couldn't read {path}.");

        var shapes = new List<ShapeData>();
        foreach (var shape in nif.GetShapes())
        {
            var verts = nif.GetVertsForShape(shape);
            var uvs = nif.GetUvsForShape(shape);
            if (verts == null || uvs == null || verts.Count == 0 || uvs.Count != verts.Count) continue;
            var tris = new vectorTriangle();
            shape.GetTriangles(tris);
            if (tris.Count == 0) continue;

            var pos = new float[verts.Count * 3];
            var uv = new float[verts.Count * 2];
            for (var i = 0; i < verts.Count; i++)
            {
                pos[i * 3] = verts[i].x; pos[i * 3 + 1] = verts[i].y; pos[i * 3 + 2] = verts[i].z;
                uv[i * 2] = uvs[i].u; uv[i * 2 + 1] = uvs[i].v;
            }
            var idx = new uint[tris.Count * 3];
            for (var i = 0; i < tris.Count; i++)
            {
                idx[i * 3] = tris[i].p1; idx[i * 3 + 1] = tris[i].p2; idx[i * 3 + 2] = tris[i].p3;
            }

            string? diffuse = null;
            var msn = false;
            try
            {
                diffuse = nif.GetTexturePathByIndex(shape, 0);
                msn = nif.GetShader(shape)?.IsModelSpace() == true;
            }
            catch
            {
                // untextured shapes still load; they just aren't preselected
            }

            shapes.Add(new ShapeData
            {
                Name = shape.name.get(),
                NifPath = path,
                Positions = pos, Uvs = uv, Indices = idx,
                DiffusePath = string.IsNullOrWhiteSpace(diffuse) ? null : diffuse,
                ModelSpaceNormals = msn,
            });
        }
        return shapes;
    }
}
