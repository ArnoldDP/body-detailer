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
    /// <summary>The shader's texture slots as stored in the NIF (0 diffuse, 1 normal, 2 subsurface, 7 specular).</summary>
    public string?[] TexturePaths { get; init; } = Array.Empty<string?>();
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

            // Vertices are stored in the shape's skin space (a vanilla head sits around the origin); this is the
            // transform that puts them where the game draws them, as Outfit Studio does.
            var toGlobal = new MatTransform();
            if (!nif.GetShapeTransformGlobalToSkin(shape, toGlobal)) toGlobal = shape.transform.InverseTransform();
            toGlobal = toGlobal.InverseTransform();
            var pos = new float[verts.Count * 3];
            var uv = new float[verts.Count * 2];
            for (var i = 0; i < verts.Count; i++)
            {
                var p = toGlobal.ApplyTransform(verts[i]);
                pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
                uv[i * 2] = uvs[i].u; uv[i * 2 + 1] = uvs[i].v;
            }
            var idx = new uint[tris.Count * 3];
            for (var i = 0; i < tris.Count; i++)
            {
                idx[i * 3] = tris[i].p1; idx[i * 3 + 1] = tris[i].p2; idx[i * 3 + 2] = tris[i].p3;
            }

            string? diffuse = null;
            var msn = false;
            var slots = new string?[9];
            try
            {
                diffuse = nif.GetTexturePathByIndex(shape, 0);
                msn = nif.GetShader(shape)?.IsModelSpace() == true;
                for (var i = 0; i < slots.Length; i++)
                {
                    var t = nif.GetTexturePathByIndex(shape, (uint)i);
                    slots[i] = string.IsNullOrWhiteSpace(t) ? null : t;
                }
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
                TexturePaths = slots,
                ModelSpaceNormals = msn,
            });
        }
        return shapes;
    }
}
