namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// A Mod Organizer 2 instance read straight from its files, the way MO2 itself resolves them: enabled mods in
/// priority order over the game's Data folder (overwrite on top), and the profile's enabled plugins in load
/// order. Nothing depends on MO2's virtual file system, so the tool doesn't need launching through MO2.
/// </summary>
public sealed class Mo2Instance
{
    public string Root { get; }
    public string Profile { get; }
    public string GameData { get; }
    /// <summary>Data-relative path (backslashes, any case) → the real file that wins.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }
    /// <summary>Enabled plugins in load order.</summary>
    public IReadOnlyList<string> Plugins { get; }
    /// <summary>Base archives from the profile's Skyrim.ini, loaded before the plugins' own.</summary>
    public IReadOnlyList<string> IniArchives { get; }
    public int EnabledMods { get; }

    private static readonly string[] Implicit =
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    /// <summary>File the patcher leaves in its output folder; a mod containing it is never read as input.</summary>
    public const string OutputMarker = "Skin texture tool patch output.txt";

    /// <summary>Enabled mods left out because they hold this tool's own output.</summary>
    public IReadOnlyList<string> SkippedOutputMods { get; private set; } = Array.Empty<string>();

    /// <param name="exclude">Folders never read (the patcher's own output folder).</param>
    public Mo2Instance(string root, string? profile = null, IEnumerable<string>? exclude = null)
    {
        Root = root;
        var ini = ReadIni(Path.Combine(root, "ModOrganizer.ini"));
        string Setting(string key, string fallback) => ini.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        var baseDir = Setting("base_directory", root).Replace("%BASE_DIR%", root);
        string Dir(string key, string name) => Setting(key, Path.Combine(baseDir, name)).Replace("%BASE_DIR%", baseDir);
        Profile = profile ?? Setting("selected_profile", "Default");
        var gamePath = Setting("gamePath", "");
        GameData = Path.Combine(gamePath, "Data");
        var profileDir = Path.Combine(Dir("profiles_directory", "profiles"), Profile);
        var modsDir = Dir("mod_directory", "mods");
        var overwrite = Dir("overwrite_directory", "overwrite");

        // modlist.txt lists the highest priority first; '+' enabled, '-' disabled, '*' unmanaged (the game's own)
        var enabled = File.ReadLines(Path.Combine(profileDir, "modlist.txt"))
            .Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()).Reverse().ToList();
        EnabledMods = enabled.Count;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(folder, file);
                if (rel.StartsWith("fomod", StringComparison.OrdinalIgnoreCase) || rel.EndsWith("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
                files[rel] = file;
            }
        }
        Add(GameData);
        // the patcher's own output must never become its input
        var excluded = (exclude ?? Array.Empty<string>()).Select(e => Path.GetFullPath(e).TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (var mod in enabled)
        {
            var folder = Path.Combine(modsDir, mod);
            if (excluded.Contains(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)) || File.Exists(Path.Combine(folder, OutputMarker))) { skipped.Add(mod); continue; }
            Add(folder);
        }
        SkippedOutputMods = skipped;
        Add(overwrite);
        Files = files;

        // plugins: loadorder.txt holds the order; plugins.txt marks the enabled ones with '*'. Plugins it doesn't
        // mention (the base game, DLC, Creation Club) are always on.
        var pluginsTxt = File.ReadLines(Path.Combine(profileDir, "plugins.txt")).Where(l => l.Length > 0 && l[0] != '#').ToList();
        var listed = pluginsTxt.Select(l => l.TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var on = pluginsTxt.Where(l => l[0] == '*').Select(l => l[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderFile = Path.Combine(profileDir, "loadorder.txt");
        var order = File.Exists(orderFile)
            ? File.ReadLines(orderFile).Where(l => l.Length > 0 && l[0] != '#').ToList()
            : Implicit.Concat(pluginsTxt.Select(l => l.TrimStart('*'))).ToList();
        Plugins = order.Where(p => files.ContainsKey(p) && (on.Contains(p) || !listed.Contains(p) || Implicit.Contains(p, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // base archives: the profile's own Skyrim.ini when the profile has local inis, else the game's
        var skyrimIni = File.Exists(Path.Combine(profileDir, "skyrim.ini")) ? Path.Combine(profileDir, "skyrim.ini")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "Skyrim.ini");
        var archives = new List<string>();
        if (File.Exists(skyrimIni))
            foreach (var line in File.ReadLines(skyrimIni))
            {
                var kv = line.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim() is var key && (key.Equals("sResourceArchiveList", StringComparison.OrdinalIgnoreCase) || key.Equals("sResourceArchiveList2", StringComparison.OrdinalIgnoreCase)))
                    archives.AddRange(kv[1].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0));
            }
        IniArchives = archives;
    }

    /// <summary>Every archive the game loads, in the order it loads them (later ones win).</summary>
    public IEnumerable<string> ArchivesInOrder()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in IniArchives) if (seen.Add(a) && Files.ContainsKey(a)) yield return a;
        foreach (var plugin in Plugins)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin);
            foreach (var name in new[] { stem + ".bsa", stem + " - Textures.bsa" })
                if (seen.Add(name) && Files.ContainsKey(name)) yield return name;
        }
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var kv = line.Split('=', 2);
            if (kv.Length != 2) continue;
            var value = kv[1].Trim();
            if (value.StartsWith("@ByteArray(") && value.EndsWith(')')) value = value[11..^1];
            result.TryAdd(kv[0].Trim(), value.Replace(@"\\", @"\").Replace('/', '\\'));
        }
        return result;
    }

    /// <summary>The MO2 instance this program was started from, if any (its folder holds ModOrganizer.ini).</summary>
    public static string? FindFromProcess()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ModOrganizer.ini"))) return d.FullName;
        return null;
    }
}
