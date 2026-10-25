namespace SkinTextureTool.Core.BodySlide;

/// <summary>Applies BodySlide slider offsets to shape positions, caching the .osd files it reads.</summary>
public sealed class Morpher
{
    private readonly Dictionary<string, Dictionary<string, OsdFile.Diff[]>> _osd = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="positions"/> with each slider's offsets for <paramref name="shapeName"/> added,
    /// scaled by its value (1 = 100%). Values are on top of the mesh as built, so 0 leaves it unchanged.
    /// </summary>
    public float[] Apply(SliderSet set, string shapeName, float[] positions, IReadOnlyDictionary<string, float> values, List<string>? problems = null)
    {
        var result = (float[])positions.Clone();
        var vertexCount = positions.Length / 3;
        foreach (var slider in set.Sliders)
        {
            if (!slider.MovesVertices || !values.TryGetValue(slider.Name, out var value) || value == 0) continue;
            foreach (var data in slider.Data.Where(d => string.Equals(d.TargetShape, shapeName, StringComparison.OrdinalIgnoreCase)))
            {
                var diffs = Entry(set, data, problems);
                if (diffs == null) continue;
                foreach (var d in diffs)
                {
                    if (d.Index >= vertexCount) continue;
                    result[d.Index * 3] += d.X * value;
                    result[d.Index * 3 + 1] += d.Y * value;
                    result[d.Index * 3 + 2] += d.Z * value;
                }
            }
        }
        return result;
    }

    private OsdFile.Diff[]? Entry(SliderSet set, SliderData data, List<string>? problems)
    {
        var path = Path.Combine(set.ShapeDataFolder, data.OsdFile);
        if (!_osd.TryGetValue(path, out var file))
        {
            try { file = OsdFile.Read(path); }
            catch (Exception ex)
            {
                problems?.Add($"{data.OsdFile}: {ex.Message}");
                file = new Dictionary<string, OsdFile.Diff[]>();
            }
            _osd[path] = file;
        }
        return file.TryGetValue(data.EntryName, out var diffs) ? diffs : null;
    }
}
