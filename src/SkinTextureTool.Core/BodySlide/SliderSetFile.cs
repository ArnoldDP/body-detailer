using System.Xml.Linq;

namespace SkinTextureTool.Core.BodySlide;

/// <summary>One morph a slider applies to one shape: which OSD file and which entry in it.</summary>
public sealed record SliderData(string TargetShape, string OsdFile, string EntryName);

public sealed record Slider(string Name, bool Uv, bool Zap, bool Hidden, IReadOnlyList<SliderData> Data)
{
    /// <summary>Only sliders that move vertices can reshape normals; UV and zap sliders are left out.</summary>
    public bool MovesVertices => !Uv && !Zap && Data.Count > 0;
}

/// <summary>
/// A BodySlide slider set from a .osp file: its source mesh, the shapes it covers, and its sliders. The format
/// follows BodySlide and Outfit Studio's SliderSet/SliderData readers (GPL-3.0).
/// </summary>
public sealed record SliderSet(string Name, string OspPath, string DataFolder, string SourceFile, IReadOnlyList<string> Shapes, IReadOnlyList<Slider> Sliders)
{
    /// <summary>The ShapeData folder the set's .osd files live in: …\CalienteTools\BodySlide\ShapeData\&lt;DataFolder&gt;.</summary>
    public string ShapeDataFolder
    {
        get
        {
            // .osp files sit in …\BodySlide\SliderSets[\sub]; ShapeData is a sibling of SliderSets
            var dir = Path.GetDirectoryName(OspPath)!;
            while (dir != null && !string.Equals(Path.GetFileName(dir), "SliderSets", StringComparison.OrdinalIgnoreCase))
                dir = Path.GetDirectoryName(dir);
            var bodySlide = dir != null ? Path.GetDirectoryName(dir)! : Path.GetDirectoryName(OspPath)!;
            return Path.Combine(bodySlide, "ShapeData", DataFolder);
        }
    }

    public static List<SliderSet> Load(string ospPath)
    {
        var doc = XDocument.Load(ospPath);
        var sets = new List<SliderSet>();
        foreach (var set in doc.Descendants("SliderSet"))
        {
            var name = (string?)set.Attribute("name") ?? "";
            var dataFolder = set.Element("DataFolder")?.Value ?? name;
            var source = set.Element("SourceFile")?.Value ?? "";
            var shapes = set.Elements("Shape").Select(s => (string?)s.Attribute("target") ?? s.Value).ToList();
            var sliders = new List<Slider>();
            foreach (var s in set.Elements("Slider"))
            {
                var data = new List<SliderData>();
                foreach (var d in s.Elements("Data"))
                {
                    var target = (string?)d.Attribute("target") ?? shapes.FirstOrDefault() ?? "";
                    var dataName = (string?)d.Attribute("name") ?? "";
                    // "file.osd\entry" when local; otherwise the entry lives in the set's own .osd
                    var text = d.Value.Trim();
                    var split = text.LastIndexOf('\\');
                    var (file, entry) = split > 0 && text[..split].EndsWith(".osd", StringComparison.OrdinalIgnoreCase)
                        ? (text[..split], text[(split + 1)..])
                        : (Path.GetFileNameWithoutExtension(source) + ".osd", dataName);
                    data.Add(new SliderData(target, file, entry));
                }
                sliders.Add(new Slider(
                    (string?)s.Attribute("name") ?? "",
                    Flag(s, "uv"), Flag(s, "zap"), Flag(s, "hidden"), data));
            }
            sets.Add(new SliderSet(name, ospPath, dataFolder, source, shapes, sliders));
        }
        return sets;
    }

    private static bool Flag(XElement e, string name) =>
        string.Equals((string?)e.Attribute(name), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A BodySlide .osd morph file: "OSD\0", version, entry count, then per entry a name and its
/// (vertex index, offset) pairs. Follows BodySlide and Outfit Studio's DiffData reader (GPL-3.0).
/// </summary>
public static class OsdFile
{
    public sealed record Diff(ushort Index, float X, float Y, float Z);

    public static Dictionary<string, Diff[]> Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        var magic = r.ReadBytes(4);
        if (magic.Length != 4 || magic[1] != (byte)'D' || magic[2] != (byte)'S' || magic[3] != (byte)'O')
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a BodySlide .osd file.");
        r.ReadUInt32(); // version
        var count = r.ReadUInt32();
        var result = new Dictionary<string, Diff[]>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
            var n = r.ReadUInt16();
            var diffs = new Diff[n];
            for (var d = 0; d < n; d++)
                diffs[d] = new Diff(r.ReadUInt16(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            result[name] = diffs;
        }
        return result;
    }
}
