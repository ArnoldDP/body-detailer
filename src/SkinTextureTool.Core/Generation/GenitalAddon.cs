using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool.Core.Generation;

/// <summary>
/// A TNG genital add-on that gives women a penis (an armour with TNG_AddonFemale and TNG_SkinWithPenis, such as
/// SOS - TRX - Futanari Addon_NG.esp), resolved for one NPC: the armour addon for its race, the female mesh and the
/// texture set that addon swaps in.
/// </summary>
public sealed record GenitalAddon(IArmorGetter Armor, IArmorAddonGetter Addon, string MeshPath, ITextureSetGetter? Textures)
{
    private static readonly FormKey AddonFemale = new(NpcSkinPatch.Tng, 0x000FFA);

    public override string ToString() => $"{Armor.Name?.String ?? Armor.EditorID} ({Armor.FormKey.ModKey.FileName})";

    /// <summary>The add-ons in the load order (winning versions).</summary>
    public static List<IArmorGetter> List(GameSession game) =>
        game.Priority.Armor().WinningOverrides()
            .Where(a => a.Keywords?.Any(k => k.FormKey == AddonFemale) == true && a.Keywords.Any(k => k.FormKey == NpcSkinPatch.SkinWithPenis))
            .ToList();

    /// <summary>The add-on's piece for an NPC's race, or null when it has none for that race.</summary>
    public static GenitalAddon? For(GameSession game, IArmorGetter armor, INpcGetter npc)
    {
        if (!npc.Race.TryResolve(game.Cache, out var race)) return null;
        var races = new HashSet<FormKey> { race.FormKey };
        if (!race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);
        IArmorAddonGetter? fallback = null;
        foreach (var link in armor.Armature)
        {
            if (!link.TryResolve(game.Cache, out var addon) || addon.WorldModel?.Female?.File is not { } file) continue;
            var direct = races.Contains(addon.Race.FormKey);
            if (direct || addon.AdditionalRaces.Any(a => races.Contains(a.FormKey)))
            {
                var textures = addon.SkinTexture.Female.TryResolve(game.Cache, out var t) ? t : null;
                return new GenitalAddon(armor, addon, file.DataRelativePath.Path, textures);
            }
            fallback ??= addon;
        }
        return null;
    }

    /// <summary>
    /// Writes the NPC's patched genitals into its patch: a copy of the race's armour addon whose skin texture is a
    /// new texture set with the baked maps (the add-on's own texture sets stay as they are, for everyone else), on
    /// slot 52 of the NPC's skin copy, which also gets TNG_SkinWithPenis so animation frameworks know.
    /// </summary>
    /// <param name="textures">The baked maps' paths under textures: diffuse, normal (_msn), subsurface (_sk), specular (_s).</param>
    public (string Plugin, List<string> Notes) Write(GameSession game, FormKey npcKey, string output, (string Diffuse, string Normal, string Subsurface, string Specular) textures) =>
        NpcSkinPatch.AddPiece(game, npcKey, output, "Genitals", (mod, npc, race) =>
        {
            var set = Textures != null ? mod.TextureSets.DuplicateInAsNewRecord(Textures) : mod.TextureSets.AddNew();
            set.EditorID = $"BDH_Genitals_{npc.EditorID}TX";
            set.Diffuse = textures.Diffuse;
            set.NormalOrGloss = textures.Normal;
            set.GlowOrDetailMap = textures.Subsurface;
            set.BacklightMaskOrSpecular = textures.Specular;
            set.Flags |= TextureSet.Flag.HasModelSpaceNormalMap | TextureSet.Flag.FaceGenTextures;

            var addon = mod.ArmorAddons.DuplicateInAsNewRecord(Addon);
            addon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
                Addon.SkinTexture.Male.AsNullable(), set.ToNullableLink<ITextureSetGetter>());
            return addon;
        }, new[] { NpcSkinPatch.SkinWithPenis });
}
