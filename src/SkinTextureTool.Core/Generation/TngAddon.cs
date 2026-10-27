using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkinTextureTool.Core.Generation;

/// <summary>
/// A The New Gentleman add-on for exported hair: a small light plugin holding an armour addon (the hair mesh on
/// biped slot 52, for the human races TNG's own human add-on covers) and an armour that carries it with the
/// TNG_AddonFemale keyword and a name, which is all TNG looks for. Optionally a TNG ini in SKSE\Plugins\TNG that
/// assigns the add-on to one NPC. Follows TNG's addon guide and its ini reader (MIT).
/// </summary>
public static class TngAddon
{
    private static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    private static readonly ModKey Dawnguard = ModKey.FromFileName("Dawnguard.esm");
    private static readonly ModKey Dragonborn = ModKey.FromFileName("Dragonborn.esm");
    private static readonly ModKey Tng = ModKey.FromFileName("TheNewGentleman.esp");
    private static readonly FormKey AddonFemale = new(Tng, 0x000FFA);
    private static readonly FormKey DefaultRace = new(Skyrim, 0x000019);

    /// <summary>The races on TNG's human add-on (TNG_GenitalManMerC): the playable human and elf races, their vampires, and the DLC ones.</summary>
    private static readonly FormKey[] Races =
    {
        new(Skyrim, 0x0131F0), new(Skyrim, 0x013741), new(Skyrim, 0x013742), new(Skyrim, 0x013743), new(Skyrim, 0x013744),
        new(Skyrim, 0x013746), new(Skyrim, 0x013747), new(Skyrim, 0x013748), new(Skyrim, 0x013749), new(Skyrim, 0x088794),
        new(Skyrim, 0x08883C), new(Skyrim, 0x08883D), new(Skyrim, 0x088840), new(Skyrim, 0x088844), new(Skyrim, 0x088846),
        new(Skyrim, 0x088884), new(Skyrim, 0x0A82B9), new(Dawnguard, 0x00E88A), new(Dragonborn, 0x035538),
    };

    /// <param name="meshPath">The hair NIF's path under meshes (the _1 file; the game picks _0/_1 by weight).</param>
    /// <param name="npc">The NPC to assign it to, as "0x&lt;id&gt;~Plugin.esp" (the plugin that created the NPC), or null.</param>
    /// <returns>The files written, relative to <paramref name="output"/>.</returns>
    public static List<string> Write(string output, string file, string displayName, string meshPath, string? npc)
    {
        var plugin = file + " - TNG.esp";
        var mod = new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;

        var slot = new BodyTemplate { FirstPersonFlags = (BipedObjectFlag)(1u << (HairExporter.Slot - 30)), ArmorType = ArmorType.Clothing };
        var addon = mod.ArmorAddons.AddNew("BDH_" + file + "AA");
        addon.Race.SetTo(DefaultRace);
        foreach (var race in Races) addon.AdditionalRaces.Add(race);
        addon.BodyTemplate = slot.DeepCopy();
        addon.WorldModel = new GenderedItem<Model?>(null, new Model { File = meshPath });
        addon.WeightSliderEnabled = new GenderedItem<bool>(true, true);

        var armor = mod.Armors.AddNew("BDH_" + file);
        armor.Name = displayName;
        armor.Race.SetTo(DefaultRace);
        armor.BodyTemplate = slot.DeepCopy();
        armor.Armature.Add(addon.ToLink());
        armor.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { AddonFemale.ToLink<IKeywordGetter>() };

        Directory.CreateDirectory(output);
        // masters in load order (Skyrim.esm first), not alphabetical
        mod.WriteToBinary(Path.Combine(output, plugin), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(new[] { Skyrim, Dawnguard, Dragonborn, Tng }),
        });
        var written = new List<string> { plugin };

        if (npc != null)
        {
            var iniRel = Path.Combine("SKSE", "Plugins", "TNG", file + "_TNG.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(output, iniRel))!);
            File.WriteAllText(Path.Combine(output, iniRel),
                $"; Written by the skin texture tool: gives {npc} the {displayName} add-on.\n[NPCGenitalAddon]\n{npc} = {Loc(armor.FormKey.ID, plugin)}\n");
            written.Add(iniRel);
        }
        return written;
    }

    /// <summary>A form as TNG's inis name it: "0x&lt;local id&gt;~Plugin.esp".</summary>
    public static string Loc(uint id, string plugin) => $"0x{id:x}~{plugin}";
}
