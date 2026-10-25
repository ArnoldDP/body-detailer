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
    private readonly string _extractFolder = Path.Combine(Path.GetTempPath(), "SkinTextureTool-extract-" + Environment.ProcessId);

    public IReadOnlyList<string> Archives { get; }
    public List<string> Problems { get; } = new();

    public VirtualData(Mo2Instance mo2)
    {
        _mo2 = mo2;
        var archives = new List<string>();
        foreach (var name in mo2.ArchivesInOrder())
        {
            try
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, mo2.Files[name]);
                foreach (var file in reader.Files) _archived[Normalize(file.Path)] = file; // later archives win
                archives.Add(name);
            }
            catch (Exception ex)
            {
                Problems.Add($"Couldn't read archive {name}: {ex.Message}");
            }
        }
        Archives = archives;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

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
        try { if (Directory.Exists(_extractFolder)) Directory.Delete(_extractFolder, true); } catch { }
    }
}
