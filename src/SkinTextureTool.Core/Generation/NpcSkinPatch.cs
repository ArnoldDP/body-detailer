using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool.Core.Generation;

/// <summary>
/// Gives one NPC extra pieces (exported hair, patched genitals) through records alone, the way a race's skin is
/// given a TNG add-on by hand: armour addons on slot 52 appended to a copy of the NPC's skin (its own, or its
/// race's) with slot 52 added, and an override of the NPC wearing the copy. All of an NPC's pieces share one patch,
/// "Body Detailer - EditorID.esp": writing a piece again replaces it there and keeps the others. With The New
/// Gentleman installed, covering clothing takes slot 52 and hides the pieces; revealing clothing leaves them showing.
/// </summary>
public static class NpcSkinPatch
{
    public static readonly ModKey Tng = ModKey.FromFileName("TheNewGentleman.esp");
    public static readonly FormKey SkinWithPenis = new(Tng, 0x000FF7);
    public static readonly BipedObjectFlag Slot = (BipedObjectFlag)(1u << (HairExporter.Slot - 30));

    /// <summary>The patch plugin for an NPC.</summary>
    public static string PluginName(string editorId) => $"Body Detailer - {editorId}.esp";

    /// <summary>
    /// Adds (or replaces) one piece on the NPC's skin copy in its patch. <paramref name="makeAddon"/> creates the
    /// piece's armour addon (and anything it needs, such as a texture set) in the patch, for the NPC's race.
    /// </summary>
    /// <param name="part">The piece's name ("Hair", "Genitals"); records it made before are replaced.</param>
    /// <returns>The plugin written (relative to <paramref name="output"/>) and notes for the user.</returns>
    public static (string Plugin, List<string> Notes) AddPiece(GameSession game, FormKey npcKey, string output, string part,
        Func<SkyrimMod, INpcGetter, IRaceGetter, ArmorAddon> makeAddon, IEnumerable<FormKey>? keywords = null)
    {
        var notes = new List<string>();
        var npc = game.Cache.Resolve<INpcGetter>(npcKey);
        var race = npc.Race.TryResolve(game.Cache, out var r) ? r : throw new InvalidDataException($"{npc.EditorID}'s race doesn't resolve.");
        if (npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
            notes.Add($"{npc.EditorID} takes its traits (skin included) from a template, so the game may ignore the skin set here.");

        var plugin = PluginName(npc.EditorID ?? npcKey.IDString());
        var path = Path.Combine(output, plugin);
        var mod = File.Exists(path)
            ? SkyrimMod.CreateFromBinary(new ModPath(path), SkyrimRelease.SkyrimSE, new BinaryReadParameters { MasterFlagsLookup = Styles(game) })
            : new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;

        // the skin copy: the patch's own if it already has one for this NPC, else a copy of the NPC's skin
        var over = mod.Npcs.GetOrAddAsOverride(npc);
        var copy = over.WornArmor.IsNull ? null : mod.Armors.FirstOrDefault(a => a.FormKey == over.WornArmor.FormKey);
        if (copy == null)
        {
            var ownSkin = !npc.WornArmor.IsNull;
            var skin = ownSkin && npc.WornArmor.TryResolve(game.Cache, out var worn) ? worn
                : race.Skin.TryResolve(game.Cache, out var raceSkin) ? raceSkin
                : throw new InvalidDataException($"Neither {npc.EditorID} nor its race has a skin.");
            copy = mod.Armors.DuplicateInAsNewRecord(skin);
            copy.EditorID = $"{skin.EditorID}_BDH_{npc.EditorID}";
            copy.BodyTemplate ??= new BodyTemplate { ArmorType = ArmorType.Clothing };
            copy.BodyTemplate.FirstPersonFlags |= Slot;
            over.WornArmor.SetTo(copy);
            notes.Add($"{npc.EditorID} wears {copy.EditorID}, a copy of {skin.EditorID} ({(ownSkin ? "its own skin" : "its race's")}).");
        }

        // replace this piece: drop the addons (and their texture sets) it made last time
        var prefix = $"BDH_{part}_";
        foreach (var old in mod.ArmorAddons.Where(a => a.EditorID?.StartsWith(prefix) == true).ToList())
        {
            copy.Armature.RemoveAll(l => l.FormKey == old.FormKey);
            mod.ArmorAddons.Remove(old.FormKey);
        }
        foreach (var old in mod.TextureSets.Where(t => t.EditorID?.StartsWith(prefix) == true).ToList()) mod.TextureSets.Remove(old.FormKey);

        var addon = makeAddon(mod, npc, race);
        addon.EditorID = $"{prefix}{npc.EditorID}AA";
        copy.Armature.Add(addon.ToLink());
        foreach (var k in keywords ?? Array.Empty<FormKey>())
        {
            copy.Keywords ??= new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
            if (!copy.Keywords.Any(x => x.FormKey == k)) copy.Keywords.Add(k.ToLink<IKeywordGetter>());
        }
        notes.Add($"{part} added on slot 52 of {npc.EditorID}'s skin.");

        // masters in load order, with each master's light/full flags known (as the session read them)
        var order = Enumerable.Reverse(game.Priority).Select(m => m.ModKey).ToList();
        Directory.CreateDirectory(output);
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            MasterFlagsLookup = Styles(game),
        });
        return (plugin, notes);
    }

    /// <summary>Gives the NPC exported hair: an addon with the hair mesh, valid for its race (and the race it borrows armour from).</summary>
    public static (string Plugin, List<string> Notes) Write(GameSession game, FormKey npcKey, string output, string file, string meshPath) =>
        AddPiece(game, npcKey, output, "Hair", (mod, npc, race) =>
        {
            var addon = mod.ArmorAddons.AddNew();
            var armorRace = !race.ArmorRace.IsNull ? race.ArmorRace.FormKey : race.FormKey;
            addon.Race.SetTo(armorRace);
            if (armorRace != race.FormKey) addon.AdditionalRaces.Add(race.FormKey);
            addon.BodyTemplate = new BodyTemplate { FirstPersonFlags = Slot, ArmorType = ArmorType.Clothing };
            addon.WorldModel = new GenderedItem<Model?>(new Model { File = meshPath }, new Model { File = meshPath });
            addon.WeightSliderEnabled = new GenderedItem<bool>(true, true);
            return addon;
        });

    private static Noggog.Cache<IModMasterStyledGetter, ModKey> Styles(GameSession game)
    {
        var styles = new Noggog.Cache<IModMasterStyledGetter, ModKey>(m => m.ModKey);
        foreach (var m in game.Priority) styles.Set(m);
        return styles;
    }
}
