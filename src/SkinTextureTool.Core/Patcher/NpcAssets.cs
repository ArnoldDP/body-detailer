using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkinTextureTool.Core.Patcher;

/// <summary>An MO2 load order read once: plugins (winners first), the link cache and the virtual Data folder.</summary>
public sealed class GameSession : IDisposable
{
    public Mo2Instance Mo2 { get; }
    public IReadOnlyList<ISkyrimModGetter> Priority { get; }
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> Cache { get; }
    public VirtualData Data { get; }

    public GameSession(string mo2Folder, string? extractFolder = null)
    {
        Mo2 = new Mo2Instance(mo2Folder);
        var styles = new Cache<IModMasterStyledGetter, ModKey>(m => m.ModKey);
        var mods = new List<ISkyrimModGetter>();
        foreach (var plugin in Mo2.Plugins)
        {
            try
            {
                var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(Mo2.Files[plugin]), SkyrimRelease.SkyrimSE, new BinaryReadParameters { MasterFlagsLookup = styles });
                mods.Add(mod); styles.Set(mod);
            }
            catch { /* unreadable plugins are reported by the scan */ }
        }
        Cache = mods.ToImmutableLinkCache();
        Priority = Enumerable.Reverse(mods).ToList();
        Data = new VirtualData(Mo2, extractFolder);
    }

    public void Dispose() => Data.Dispose();
}

/// <summary>
/// One NPC's meshes and textures as the game draws them: the FaceGen head, and the body, hands and feet from the
/// skin it wears, with the skin's own texture set where it swaps one in.
/// </summary>
public static class NpcAssets
{
    /// <remarks><see cref="Npc"/> is null for entries from the list cache; it's resolved from the session when needed.</remarks>
    public sealed record Entry(string Name, string EditorId, string FormKey, string Plugin, string Race, bool Female, INpcGetter? Npc)
    {
        public override string ToString() => $"{Name}  ({EditorId}, {Race}, {Plugin})";
    }

    /// <summary>A mesh to load, with texture slots (0 diffuse, 1 normal, 2 subsurface, 7 specular) to use instead of the NIF's own.</summary>
    public sealed record Piece(string MeshPath, string Label, string?[]? TextureOverride);

    /// <summary>NPCs with their own face (not templated from another), named first.</summary>
    public static List<Entry> List(GameSession game)
    {
        var list = new List<Entry>();
        foreach (var npc in game.Priority.Npc().WinningOverrides())
        {
            if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) continue;
            var race = npc.Race.TryResolve(game.Cache, out var r) ? r.EditorID ?? "" : "";
            var name = npc.Name?.String;
            list.Add(new Entry(string.IsNullOrWhiteSpace(name) ? npc.EditorID ?? npc.FormKey.ToString() : name!, npc.EditorID ?? "", npc.FormKey.ToString(),
                npc.FormKey.ModKey.FileName, race, npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female), npc));
        }
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record CachedList(string Mo2Folder, List<Entry> Npcs);

    /// <summary>The NPC list saved by <see cref="SaveCache"/> for this MO2 folder, or null. It may be out of date;
    /// the caller refreshes it once the load order has been read.</summary>
    public static List<Entry>? LoadCache(string path, string mo2Folder)
    {
        try
        {
            var cached = System.Text.Json.JsonSerializer.Deserialize<CachedList>(File.ReadAllText(path));
            return cached != null && string.Equals(cached.Mo2Folder, mo2Folder, StringComparison.OrdinalIgnoreCase) ? cached.Npcs : null;
        }
        catch { return null; }
    }

    public static void SaveCache(string path, string mo2Folder, IEnumerable<Entry> npcs)
    {
        try { File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new CachedList(mo2Folder, npcs.Select(e => e with { Npc = null }).ToList()))); }
        catch { /* only a cache */ }
    }

    /// <summary>The pieces to load for an NPC; body pieces use the variant nearer the NPC's weight.</summary>
    public static List<Piece> Pieces(GameSession game, Entry entry, out List<string> notes)
    {
        notes = new List<string>();
        var npc = entry.Npc ?? game.Cache.Resolve<INpcGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory(entry.FormKey));
        var pieces = new List<Piece>();
        var faceGen = FaceGenPatcher.FaceGenPath(npc);
        if (game.Data.Exists(faceGen)) pieces.Add(new Piece(faceGen, "FaceGen head", null));
        else notes.Add("No FaceGen head (it uses another NPC's face, or it's missing).");

        if (!npc.Race.TryResolve(game.Cache, out var race)) { notes.Add("Its race couldn't be found."); return pieces; }
        IArmorGetter? skin = null;
        if (!npc.WornArmor.IsNull && npc.WornArmor.TryResolve(game.Cache, out var worn)) { skin = worn; notes.Add($"Wears its own skin: {worn.EditorID}."); }
        if (skin == null && race.Skin.TryResolve(game.Cache, out var raceSkin)) skin = raceSkin;
        if (skin == null) { notes.Add("No skin found for it or its race."); return pieces; }

        var races = new List<FormKey> { race.FormKey };
        if (!race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);
        var suffix = npc.Weight >= 50 ? "_1" : "_0";
        foreach (var (label, slot) in new[] { ("body", BipedObjectFlag.Body), ("hands", BipedObjectFlag.Hands), ("feet", BipedObjectFlag.Feet) })
        {
            foreach (var link in skin.Armature)
            {
                if (!link.TryResolve(game.Cache, out var addon) || addon.BodyTemplate == null || !addon.BodyTemplate.FirstPersonFlags.HasFlag(slot)) continue;
                if (!races.Any(r => addon.Race.FormKey == r || addon.AdditionalRaces.Any(a => a.FormKey == r))) continue;
                var model = entry.Female ? addon.WorldModel?.Female?.File : addon.WorldModel?.Male?.File;
                if (model == null) continue;
                var mesh = FaceGenPatcher.Variant(MeshPath(model.ToString()), suffix);
                if (!game.Data.Exists(mesh)) mesh = MeshPath(model.ToString());
                string?[]? textures = null;
                var set = entry.Female ? addon.SkinTexture?.Female : addon.SkinTexture?.Male;
                if (set != null && !set.IsNull && set.TryResolve(game.Cache, out var txst))
                {
                    textures = new string?[9];
                    textures[0] = Tex(txst.Diffuse); textures[1] = Tex(txst.NormalOrGloss);
                    textures[2] = Tex(txst.GlowOrDetailMap); textures[7] = Tex(txst.BacklightMaskOrSpecular);
                }
                pieces.Add(new Piece(mesh, label, textures));
                break;
            }
        }
        return pieces;
    }

    private static string? Tex(object? path)
    {
        var s = path?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace('/', '\\').TrimStart('\\');
        return s.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? s : @"textures\" + s;
    }

    private static string MeshPath(string model)
    {
        var p = model.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? p : @"meshes\" + p;
    }
}
