using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool.Core.BodySlide;

/// <summary>Finds the BodySlide slider set that builds a mesh in the load order, from the sets' output paths.</summary>
public static class SliderSetFinder
{
    /// <param name="meshPath">The mesh's data path ("meshes\…\name_1.nif").</param>
    /// <param name="shape">The shape that has to be in the set (several sets can build to the same file).</param>
    public static SliderSet? ForMesh(VirtualData data, string meshPath, string shape)
    {
        // what a set's OutputPath\OutputFile looks like for this mesh: under meshes, without the weight suffix
        var target = meshPath.Replace('/', '\\');
        if (target.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) target = target["meshes\\".Length..];
        target = Path.ChangeExtension(target, null);
        if (target.EndsWith("_0") || target.EndsWith("_1")) target = target[..^2];
        foreach (var rel in data.Paths(Path.Combine("CalienteTools", "BodySlide", "SliderSets")).Where(p => p.EndsWith(".osp", StringComparison.OrdinalIgnoreCase)))
        {
            var file = data.Materialize(rel);
            if (file == null) continue;
            List<SliderSet> sets;
            try { sets = SliderSet.Load(file); } catch { continue; }
            foreach (var set in sets)
                if (set.Shapes.Contains(shape, StringComparer.OrdinalIgnoreCase) && string.Equals(OutputOf(file, set.Name), target, StringComparison.OrdinalIgnoreCase))
                {
                    // its ShapeData can come from another mod than its .osp: take the load order's copy
                    var source = data.Materialize(Path.Combine("CalienteTools", "BodySlide", "ShapeData", set.DataFolder, set.SourceFile));
                    return source != null ? set with { DataFolderPath = Path.GetDirectoryName(source) } : set;
                }
        }
        return null;
    }

    /// <summary>A set's OutputPath\OutputFile (under meshes), as written in its .osp.</summary>
    private static string? OutputOf(string osp, string setName)
    {
        var doc = System.Xml.Linq.XDocument.Load(osp);
        var set = doc.Descendants("SliderSet").FirstOrDefault(s => (string?)s.Attribute("name") == setName);
        var path = set?.Element("OutputPath")?.Value.Trim(); var file = set?.Element("OutputFile")?.Value.Trim();
        if (path == null || file == null) return null;
        path = path.Replace('/', '\\');
        if (path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) path = path["meshes\\".Length..];
        return Path.Combine(path, file);
    }
}
