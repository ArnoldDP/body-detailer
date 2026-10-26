using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SkinTextureTool.Core.Patcher;

/// <summary>
/// Fits every NPC FaceGen head in the load order to its body and, unless it's a dry run, writes the fitted heads
/// into an output folder at their game paths. A head whose fit would fold a triangle is left out.
/// </summary>
public static class NpcBatch
{
    public sealed record Summary(int Npcs, int Fitted, int Written, Dictionary<string, int> Skipped, Dictionary<string, int> FittedByPlugin,
        int Folded, double LargestMove, TimeSpan Took, List<string> Notes)
    {
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Npcs} NPCs with their own FaceGen head; {Fitted} fitted to their body, {Written} written ({Took.TotalSeconds:0} s).");
            sb.AppendLine($"Largest vertex move {LargestMove:0.00} units; {Folded} heads left out because even a short bend would fold a triangle.");
            sb.AppendLine("Skipped:");
            foreach (var (why, n) in Skipped.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {n,6}  {why}");
            sb.AppendLine("Fitted, by the plugin that defines the NPC:");
            foreach (var (p, n) in FittedByPlugin.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {n,6}  {p}");
            if (Notes.Count > 0)
            {
                sb.AppendLine("Notes:");
                foreach (var n in Notes.Take(100)) sb.AppendLine("  " + n);
            }
            return sb.ToString();
        }
    }

    /// <param name="output">Folder standing in for Data; null for a dry run.</param>
    public static Summary Run(string mo2Folder, string? output, float band = 4f, Action<string>? progress = null)
    {
        var started = DateTime.Now;
        progress?.Invoke("Reading the MO2 instance…");
        var mo2 = new Mo2Instance(mo2Folder);
        var styles = new Cache<IModMasterStyledGetter, ModKey>(m => m.ModKey);
        var mods = new List<ISkyrimModGetter>();
        foreach (var plugin in mo2.Plugins)
        {
            try
            {
                var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(mo2.Files[plugin]), SkyrimRelease.SkyrimSE, new BinaryReadParameters { MasterFlagsLookup = styles });
                mods.Add(mod); styles.Set(mod);
            }
            catch { /* reported by the scan */ }
        }
        var cache = mods.ToImmutableLinkCache();
        using var data = new VirtualData(mo2);
        var patcher = new FaceGenPatcher(data, cache) { Band = band };
        // the tool's own output must never be read back as input
        var outputFull = output == null ? null : Path.GetFullPath(output);

        int npcs = 0, fitted = 0, written = 0, folded = 0, gentler = 0; double largest = 0;
        var skipped = new Dictionary<string, int>(); var byPlugin = new Dictionary<string, int>(); var notes = new List<string>();
        foreach (var npc in Enumerable.Reverse(mods).Npc().WinningOverrides())
        {
            if (!npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) continue;
            if (!data.Exists(FaceGenPatcher.FaceGenPath(npc))) continue;
            npcs++;
            if (npcs % 100 == 0) progress?.Invoke($"Fitting FaceGen heads… {npcs}");
            // a tight neck that would fold gets a gentler (shorter) bend before it's given up on
            FaceGenPatcher.Outcome? o = null; var folds = 0;
            try
            {
                foreach (var b in new[] { band, band * 0.6f, band * 0.35f })
                {
                    o = patcher.Fit(npc, b);
                    if (o.Skipped != null) break;
                    folds = Folds(o.Head.Positions, o.Patched, o.Head.Indices);
                    if (folds == 0) { if (b < band) gentler++; break; }
                }
            }
            catch (Exception ex) { Count(skipped, "error: " + ex.GetType().Name); notes.Add($"{npc.EditorID}: {ex.Message}"); continue; }
            if (o!.Skipped != null)
            {
                Count(skipped, o.Skipped.StartsWith("neck doesn't meet") ? "neck doesn't meet the body it wears" : o.Skipped);
                continue;
            }
            if (folds > 0)
            {
                folded++;
                notes.Add($"{o.Npc}: left out, even a short bend would fold {folds} triangle(s)");
                continue;
            }
            fitted++;
            largest = Math.Max(largest, o.LargestMove);
            Count(byPlugin, npc.FormKey.ModKey.FileName);
            if (outputFull != null)
            {
                try { patcher.Write(o, outputFull); written++; }
                catch (Exception ex) { notes.Add($"{o.Npc}: couldn't write: {ex.Message}"); }
            }
        }
        if (gentler > 0) notes.Insert(0, $"{gentler} heads needed a shorter bend to avoid folding a triangle.");
        return new Summary(npcs, fitted, written, skipped, byPlugin, folded, largest, DateTime.Now - started, notes);
    }

    private static void Count(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

    private static int Folds(float[] a, float[] b, uint[] idx)
    {
        var n = 0;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var fa = Face(a, idx, t); var fb = Face(b, idx, t);
            var la = Math.Sqrt(fa.X * fa.X + fa.Y * fa.Y + fa.Z * fa.Z); var lb = Math.Sqrt(fb.X * fb.X + fb.Y * fb.Y + fb.Z * fb.Z);
            if (la < 1e-12 || lb < 1e-12) continue;
            // a fold is a triangle flipped over or squashed nearly flat; turning a lot while keeping its size is
            // just a strong crease being straightened
            if ((fa.X * fb.X + fa.Y * fb.Y + fa.Z * fb.Z) / (la * lb) < 0 || lb < la * 0.2) n++;
        }
        return n;
    }

    private static (double X, double Y, double Z) Face(float[] P, uint[] I, int t)
    {
        int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
        double ux = P[b * 3] - P[a * 3], uy = P[b * 3 + 1] - P[a * 3 + 1], uz = P[b * 3 + 2] - P[a * 3 + 2];
        double vx = P[c * 3] - P[a * 3], vy = P[c * 3 + 1] - P[a * 3 + 1], vz = P[c * 3 + 2] - P[a * 3 + 2];
        return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }
}
