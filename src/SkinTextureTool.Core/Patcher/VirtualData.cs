using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// The Data folder as the game sees it: loose files (from the MO2 instance's enabled mods, overwrite and the
/// game folder) win over archives, and later archives over earlier ones.
/// </summary>
public sealed class VirtualData : IDisposable
{
    private readonly Mo2Instance _mo2;
    private readonly Dictionary<string, IArchiveFile> _archived = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _extractFolder;
    private readonly bool _keepExtracted;

    public IReadOnlyList<string> Archives { get; }
    public List<string> Problems { get; } = new();

    /// <param name="extractFolder">Where archived files are extracted to and kept; by default a temporary folder
    /// that is removed again.</param>
    public VirtualData(Mo2Instance mo2, string? extractFolder = null)
    {
        _mo2 = mo2;
        _keepExtracted = extractFolder != null;
        _extractFolder = extractFolder ?? Path.Combine(Path.GetTempPath(), "SkinTextureTool-extract-" + Environment.ProcessId);
        // archive indexes read in parallel, then merged in order (later archives win)
        var names = mo2.ArchivesInOrder().ToList();
        var read = new (List<IArchiveFile>? Files, string? Error)[names.Count];
        Parallel.For(0, names.Count, i =>
        {
            try { read[i] = (Archive.CreateReader(GameRelease.SkyrimSE, mo2.Files[names[i]]).Files.ToList(), null); }
            catch (Exception ex) { read[i] = (null, ex.Message); }
        });
        var archives = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            if (read[i].Files is not { } files) { Problems.Add($"Couldn't read archive {names[i]}: {read[i].Error}"); continue; }
            foreach (var file in files) _archived[Normalize(file.Path)] = file;
            archives.Add(names[i]);
        }
        Archives = archives;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>Every data path under a folder (loose or archived), backslashed.</summary>
    public IEnumerable<string> Paths(string folder)
    {
        var prefix = Normalize(folder);
        return _mo2.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(_archived.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public bool Exists(string relative) => _mo2.Files.ContainsKey(Normalize(relative)) || _archived.ContainsKey(Normalize(relative));

    /// <summary>A real file path for reading: the loose file, or the archived one extracted to a temporary folder.</summary>
    public string? Materialize(string relative)
    {
        var rel = Normalize(relative);
        if (_mo2.Files.TryGetValue(rel, out var loose)) return loose;
        if (!_archived.TryGetValue(rel, out var file)) return null;
        var target = Path.Combine(_extractFolder, rel);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.GetBytes());
        }
        return target;
    }

    public void Dispose()
    {
        if (_keepExtracted) return;
        try { if (Directory.Exists(_extractFolder)) Directory.Delete(_extractFolder, true); } catch { }
    }
}
