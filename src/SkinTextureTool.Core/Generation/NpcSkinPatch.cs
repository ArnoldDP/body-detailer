using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool.Core.Generation;

/// <summary>
/// Gives one NPC exported hair through records alone, the way a race's skin is given a TNG add-on by hand: an
/// armour addon with the hair mesh on slot 52, a copy of the NPC's skin (its own, or its race's) with that addon
/// appended and slot 52 added, and an override of the NPC wearing the copy. With The New Gentleman installed,
/// covering clothing takes slot 52 and hides the hair, and revealing clothing leaves it showing; TNG builds any
/// genitals it gives the NPC on top of this skin, so the hair stays.
/// </summary>
public static class NpcSkinPatch
{
    /// <returns>The plugin written (relative to <paramref name="output"/>) and notes for the user.</returns>
    public static (string Plugin, List<string> Notes) Write(GameSession game, FormKey npcKey, string output, string file, string meshPath)
    {
        var notes = new List<string>();
        var npc = game.Cache.Resolve<INpcGetter>(npcKey);
        var race = npc.Race.TryResolve(game.Cache, out var r) ? r : throw new InvalidDataException($"{npc.EditorID}'s race doesn't resolve.");
        var ownSkin = !npc.WornArmor.IsNull;
        var skin = !npc.WornArmor.IsNull && npc.WornArmor.TryResolve(game.Cache, out var worn) ? worn
            : race.Skin.TryResolve(game.Cache, out var raceSkin) ? raceSkin
            : throw new InvalidDataException($"Neither {npc.EditorID} nor its race has a skin.");
        if (npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
            notes.Add($"{npc.EditorID} takes its traits (skin included) from a template, so the game may ignore the skin set here.");

        var plugin = $"{file} - {npc.EditorID}.esp";
        var mod = new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        var slot = (BipedObjectFlag)(1u << (HairExporter.Slot - 30));

        // the hair: valid for the NPC's race and the race it borrows armour from, as the skin's own addons are
        var addon = mod.ArmorAddons.AddNew($"BDH_{file}_{npc.EditorID}AA");
        var armorRace = !race.ArmorRace.IsNull ? race.ArmorRace.FormKey : race.FormKey;
        addon.Race.SetTo(armorRace);
        if (armorRace != race.FormKey) addon.AdditionalRaces.Add(race.FormKey);
        addon.BodyTemplate = new BodyTemplate { FirstPersonFlags = slot, ArmorType = ArmorType.Clothing };
        addon.WorldModel = new GenderedItem<Model?>(new Model { File = meshPath }, new Model { File = meshPath });
        addon.WeightSliderEnabled = new GenderedItem<bool>(true, true);

        // the skin: the NPC's own, copied so others wearing it are untouched, with the hair on slot 52
        var copy = mod.Armors.DuplicateInAsNewRecord(skin);
        copy.EditorID = $"{skin.EditorID}_BDH_{npc.EditorID}";
        copy.Armature.Add(addon.ToLink());
        copy.BodyTemplate ??= new BodyTemplate { ArmorType = ArmorType.Clothing };
        copy.BodyTemplate.FirstPersonFlags |= slot;

        // the NPC wears it (an override of the winning record, so this plugin loads last, like a patch)
        var over = mod.Npcs.GetOrAddAsOverride(npc);
        over.WornArmor.SetTo(copy);
        notes.Add($"{npc.EditorID} now wears {copy.EditorID} (a copy of {skin.EditorID}, from {(ownSkin ? "its own skin" : "its race")}) with the hair on slot 52.");

        // masters in load order, with each master's light/full flags known (as the session read them)
        var order = Enumerable.Reverse(game.Priority).Select(m => m.ModKey).ToList();
        var styles = new Noggog.Cache<IModMasterStyledGetter, ModKey>(m => m.ModKey);
        foreach (var m in game.Priority) styles.Set(m);
        Directory.CreateDirectory(output);
        mod.WriteToBinary(Path.Combine(output, plugin), new BinaryWriteParameters
        {
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            MasterFlagsLookup = styles,
        });
        return (plugin, notes);
    }
}
