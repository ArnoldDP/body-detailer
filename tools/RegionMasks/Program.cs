using System.IO.Compression;
using SkinTextureTool.Core.Mesh;

var dir = @"C:\OCM\mods\Bodyslide Output\meshes\actors\character\character assets\";
var outDir = args.Length > 0 ? args[0] : "out";
var size = args.Length > 1 ? int.Parse(args[1]) : 2048;
var shapes = new List<ShapeData>();
shapes.AddRange(BodyMesh.Load(dir + "femalebody_1.nif"));
shapes.AddRange(BodyMesh.Load(dir + "femalehands_1.nif"));
shapes.AddRange(BodyMesh.Load(dir + "femalefeet_1.nif").Where(s => s.Name == "Feet"));
shapes.Add(BodyMesh.Load(@"C:\OCM\mods\High Poly Head SE\meshes\KL\High Poly Head\femalehead.nif").First());
var body = shapes.First(s => s.Name == "3BA");

// ---- landmarks measured from the meshes ----
float BX(int i) => body.Positions[i * 3]; float BY(int i) => body.Positions[i * 3 + 1]; float BZ(int i) => body.Positions[i * 3 + 2];
var armGap = new SortedDictionary<float, float> { [77] = 18f, [78] = 16.5f, [81] = 15.5f, [84] = 14f, [87] = 13.2f, [90] = 12.6f, [93] = 12.2f, [96] = 11.6f, [99] = 11.4f };
var armCentre = new List<(float Z, float X, float Y)>();
foreach (var (z, gx) in armGap)
{
    var pts = Enumerable.Range(0, body.VertexCount).Where(i => BX(i) > gx && Math.Abs(BZ(i) - z) < 0.6).ToList();
    if (pts.Count > 0) armCentre.Add((z, pts.Average(BX), pts.Average(BY)));
}
float Lerp(SortedDictionary<float, float> t, float z)
{
    var k = t.Keys.ToList();
    if (z <= k[0]) return t[k[0]]; if (z >= k[^1]) return t[k[^1]];
    for (var i = 1; i < k.Count; i++) if (z <= k[i]) { var f = (z - k[i - 1]) / (k[i] - k[i - 1]); return t[k[i - 1]] + (t[k[i]] - t[k[i - 1]]) * f; }
    return 0;
}
var legCentre = new SortedDictionary<float, (float X, float Y)>();
for (float z = 12; z <= 60; z += 2)
{
    var pts = Enumerable.Range(0, body.VertexCount).Where(i => BX(i) > 0.5f && Math.Abs(BZ(i) - z) < 0.6).ToList();
    legCentre[z] = (pts.Average(BX), pts.Average(BY));
}
(float X, float Y) LegC(float z)
{
    z = Math.Clamp(z, 12, 60); var k = legCentre.Keys.ToList();
    for (var i = 1; i < k.Count; i++) if (z <= k[i]) { var f = (z - k[i - 1]) / (k[i] - k[i - 1]); var a = legCentre[k[i - 1]]; var b = legCentre[k[i]]; return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f); }
    return legCentre[k[^1]];
}
// navel: the midline point sunk deepest below its neighbours above and below
var mid = Enumerable.Range(0, body.VertexCount).Where(i => Math.Abs(BX(i)) < 0.35f && BY(i) > 0 && BZ(i) > 70 && BZ(i) < 90).ToList();
var navel = mid.MaxBy(i => { var near = mid.Where(j => Math.Abs(BZ(j) - BZ(i)) is > 1.2f and < 2.5f).ToList(); return near.Count == 0 ? -9 : near.Average(BY) - BY(i); });
var navelP = (Y: BY(navel), Z: BZ(navel));
var nipple = Enumerable.Range(0, body.VertexCount).Where(i => BX(i) > 2 && BZ(i) > 75 && BZ(i) < 105).MaxBy(BY);
var nipP = (X: BX(nipple), Y: BY(nipple), Z: BZ(nipple));
Console.WriteLine($"navel y{navelP.Y:F1} z{navelP.Z:F1}; nipple {nipP}; arm centres " + string.Join(" ", armCentre.Select(c => $"z{c.Z}:({c.X:F1},{c.Y:F1})")));
var hands = shapes.First(s => s.Name == "artHands");
var wristZ = 77.5f; var fingerTipZ = Enumerable.Range(0, hands.VertexCount).Min(i => hands.Positions[i * 3 + 2]);

static float S(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
static float Band(float v, float a, float b, float f) => S(a - f, a + f, v) * (1 - S(b - f, b + f, v));
static float Ell(float x, float y, float z, float cx, float cy, float cz, float rx, float ry, float rz, float soft = 0.35f)
{
    var d = MathF.Sqrt(Sq((x - cx) / rx) + Sq((y - cy) / ry) + Sq((z - cz) / rz));
    return 1 - S(1 - soft, 1, d);
}
static float Sq(float v) => v * v;

var regions = new List<(string Name, string Category, Func<Ctx, float> F)>();
void R(string name, string cat, Func<Ctx, float> f) => regions.Add((name, cat, f));

float ArmT(Ctx c) => Math.Clamp((104f - c.Z) / (104f - 77f), 0, 1);
float Armness(Ctx c)
{
    if (c.Shape != "3BA") return 0;
    if (c.Z < 76.5f || c.Z > 106) return 0;
    var g = Lerp(armGap, c.Z);
    if (c.Z <= 98) return S(g - 0.6f, g + 0.6f, c.A);
    var g2 = 11.4f + (c.Z - 98) * 0.35f;
    return S(g2 - 1f, g2 + 1f, c.A) * (1 - S(103, 106, c.Z));
}
float Legness(Ctx c)
{
    if (c.Shape != "3BA") return 0;
    var crotch = 1 - Ell(c.A, c.Y, c.Z, 0, -1, 66, 3.5f, 6, 4, 0.6f);
    return (1 - S(58, 64, c.Z)) * crotch;
}
(float Across, float Fwd) LegDir(Ctx c) { var lc = LegC(c.Z); var dx = c.A - lc.X; var dy = c.Y - lc.Y; var l = MathF.Sqrt(dx * dx + dy * dy) + 1e-4f; return (dx / l, dy / l); }
(float Across, float Fwd) ArmDir(Ctx c)
{
    float cx, cy;
    if (c.Z <= armCentre[0].Z) { cx = armCentre[0].X; cy = armCentre[0].Y; }
    else if (c.Z >= armCentre[^1].Z) { cx = armCentre[^1].X - (c.Z - armCentre[^1].Z) * 0.45f; cy = armCentre[^1].Y; }
    else
    {
        var k = 1; while (armCentre[k].Z < c.Z) k++;
        var p = armCentre[k - 1]; var q = armCentre[k]; var f = (c.Z - p.Z) / (q.Z - p.Z);
        cx = p.X + (q.X - p.X) * f; cy = p.Y + (q.Y - p.Y) * f;
    }
    var dx = c.A - cx; var dy = c.Y - cy; var l = MathF.Sqrt(dx * dx + dy * dy) + 1e-4f; return (dx / l, dy / l);
}
float Torso(Ctx c) => c.Shape == "3BA" ? (1 - Armness(c)) * (1 - Legness(c)) * (1 - S(110, 112.5f, c.Z)) : 0;
float Front(Ctx c) => S(-1.5f, 1.5f, c.Y + 0.5f);

// ===== Head =====
bool H(Ctx c) => c.Shape == "FemaleHead";
R("Lips", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 0, 7.8f, 118.5f, 2.3f, 2.5f, 1.05f) * S(5.5f, 6.5f, c.Y));
R("Nose", "Face", c => !H(c) ? 0 : Band(c.Z, 119.6f, 124.6f, 0.5f) * (1 - S(0.9f + (124.6f - c.Z) * 0.18f, 1.6f + (124.6f - c.Z) * 0.2f, c.A)) * S(7f, 7.8f, c.Y));
R("Nose tip", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 0, 9.3f, 121f, 1.4f, 1.2f, 1.1f));
R("Cheeks", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 3.9f, 6.2f, 120.6f, 1.9f, 3f, 1.9f, 0.6f));
R("Under eyes", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 3.0f, 6.9f, 122.4f, 1.6f, 3f, 0.6f, 0.5f));
R("Eyelids", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 3.0f, 6.9f, 123.7f, 1.55f, 3f, 0.75f, 0.4f));
R("Brows", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 3.0f, 7.6f, 125.3f, 2.0f, 3f, 0.55f, 0.45f));
R("Forehead", "Face", c => !H(c) ? 0 : Band(c.Z, 125.9f, 130.5f, 0.6f) * (1 - S(3.8f, 5f, c.A)) * S(2.5f, 4f, c.Y));
R("Chin", "Face", c => !H(c) ? 0 : Ell(c.A, c.Y, c.Z, 0, 7.2f, 115.9f, 2.1f, 2.5f, 1.25f, 0.5f));
R("Jaw", "Face", c => !H(c) ? 0 : Band(c.Z, 114.5f, 120f, 0.8f) * S(3.8f, 5.0f, c.A) * S(-1f, 1.5f, c.Y));
R("Under chin", "Face", c => !H(c) ? 0 : Band(c.Z, 112.6f, 115.4f, 0.4f) * S(0.5f, 2.5f, c.Y) * (1 - S(4f, 5.5f, c.A)));
R("Ears", "Face", c => !H(c) ? 0 : S(5.3f, 5.9f, c.A) * Band(c.Z, 119.3f, 125.8f, 0.5f) * Band(c.Y, -2.2f, 2.2f, 0.5f));
R("Nape", "Face", c => !H(c) ? 0 : Band(c.Z, 112f, 124.5f, 0.8f) * S(1f, -1f, c.Y) * (1 - S(5f, 5.6f, c.A)));
R("Scalp", "Face", c => !H(c) ? 0 : Math.Max(S(129.5f, 131f, c.Z), S(124.5f, 126f, c.Z) * (1 - S(0.5f, 2.5f, c.Y))));
R("Neck (head)", "Face", c => !H(c) ? 0 : 1 - S(112.8f, 114f, c.Z));

// ===== Torso =====
R("Neck", "Torso", c => c.Shape != "3BA" ? 0 : S(107.5f, 110.5f, c.Z) * (1 - S(8f, 10.5f, c.A)));
R("Collarbone", "Torso", c => Torso(c) * Band(c.Z, 104.5f, 109.5f, 0.8f) * Band(c.A, 1.5f, 12f, 1f) * Front(c));
R("Upper chest", "Torso", c => Torso(c) * Band(c.Z, nipP.Z + 3f, 105f, 1.2f) * Band(c.A, 2f, 11f, 1f) * Front(c));
R("Sternum", "Torso", c => Torso(c) * Band(c.Z, 88f, 106f, 1f) * (1 - S(1.6f, 2.8f, c.A)) * Front(c));
R("Breasts", "Torso", c => Torso(c) * Ell(c.A, c.Y, c.Z, nipP.X + 0.5f, nipP.Y - 3, nipP.Z - 1, 5.6f, 6f, 6f, 0.4f) * S(1f, 3f, c.Y));
R("Areolae", "Torso", c => Torso(c) * Ell(c.A, c.Y, c.Z, nipP.X, nipP.Y, nipP.Z, 1.25f, 1.2f, 1.25f, 0.25f));
R("Nipples", "Torso", c => Torso(c) * Ell(c.A, c.Y, c.Z, nipP.X, nipP.Y, nipP.Z, 0.5f, 0.6f, 0.5f, 0.3f));
R("Underbust", "Torso", c => Torso(c) * Band(c.Z, nipP.Z - 9.5f, nipP.Z - 5.5f, 0.8f) * Band(c.A, 1.5f, 9f, 1f) * Front(c));
R("Belly", "Torso", c => Torso(c) * Band(c.Z, 70.5f, 86.5f, 1.2f) * (1 - S(5.5f, 7.5f, c.A)) * S(0.5f, 2.5f, c.Y));
R("Navel", "Torso", c => Torso(c) * Ell(c.A, c.Y, c.Z, 0, navelP.Y, navelP.Z, 0.7f, 1.5f, 0.85f, 0.35f));
R("Sides", "Torso", c => Torso(c) * Band(c.Z, 73f, 92f, 1.5f) * S(7f, 9f, c.A) * Band(c.Y, -5f, 3.5f, 1.5f));
R("Hips", "Torso", c => (c.Shape == "3BA" ? 1 - Armness(c) : 0) * Band(c.Z, 58f, 74f, 2f) * S(7f, 9.5f, c.A) * Band(c.Y, -6f, 4.5f, 1.5f));
R("Pubic region", "Torso", c => Torso(c) * Band(c.Z, 64.5f, 71.5f, 0.8f) * (1 - S(3.8f, 5.2f, c.A)) * S(0f, 2f, c.Y));
R("Upper back", "Torso", c => Torso(c) * Band(c.Z, 93f, 110f, 1.5f) * (1 - S(10f, 12f, c.A)) * S(1f, 3f, -c.Y));
R("Shoulder blades", "Torso", c => Torso(c) * Ell(c.A, c.Y, c.Z, 6.3f, -8.5f, 99f, 4f, 4f, 4.5f, 0.5f));
R("Lower back", "Torso", c => Torso(c) * Band(c.Z, 72f, 93f, 1.5f) * (1 - S(6.5f, 8.5f, c.A)) * S(1f, 3f, -c.Y));
R("Spine", "Torso", c => Torso(c) * Band(c.Z, 72f, 109f, 1f) * (1 - S(0.8f, 1.6f, c.A)) * S(2f, 4f, -c.Y));
R("Buttocks", "Torso", c => (c.Shape == "3BA" ? 1 : 0) * Band(c.Z, 55.5f, 73.5f, 1.5f) * Band(c.A, 0.6f, 11.5f, 1f) * S(3f, 5.5f, -c.Y));

// ===== Genitals =====
R("Vulva", "Genitals", c => c.Shape == "3BA_Vagina" ? 1 : c.Shape == "3BA" ? Ell(c.A, c.Y, c.Z, 0, 0.5f, 66.5f, 1.7f, 4.5f, 3f, 0.5f) : 0);
R("Labia (inner)", "Genitals", c => c.Shape == "3BA_Vagina" ? 1 - S(0.35f, 0.75f, c.A) : 0);
R("Anus", "Genitals", c => c.Shape == "3BA_Anus" ? 1 : c.Shape == "3BA" ? Ell(c.A, c.Y, c.Z, 0, -3.9f, 66.7f, 1.6f, 2.5f, 1.8f, 0.5f) : 0);
R("Perineum", "Genitals", c => c.Shape == "3BA" ? Ell(c.A, c.Y, c.Z, 0, -2.6f, 65.6f, 1.2f, 2f, 1.3f, 0.5f) : 0);

// ===== Joints =====
R("Shoulders", "Joints", c => c.Shape != "3BA" ? 0 : Band(c.Z, 101f, 110f, 1.2f) * S(10.5f, 12.5f, c.A));
R("Elbows", "Joints", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.44f, 0.62f, 0.05f) * S(0.2f, 0.6f, -d.Fwd); });
R("Inner elbows", "Joints", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.45f, 0.6f, 0.05f) * S(0.2f, 0.6f, d.Fwd); });
R("Wrists", "Joints", c => c.Shape == "artHands" ? S(75.5f, 77f, c.Z) : Armness(c) * (1 - S(78f, 80f, c.Z)));
R("Hip creases", "Joints", c => c.Shape != "3BA" ? 0 : Band(c.Z, 59f, 64.5f, 1f) * Band(c.A, 2.5f, 9.5f, 1f) * S(1f, 3f, c.Y));
R("Knees", "Joints", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 35f, 42.5f, 1f) * S(0f, 0.5f, d.Fwd); });
R("Back of knees", "Joints", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 35.5f, 42f, 1f) * S(0.3f, 0.7f, -d.Fwd); });
R("Ankles", "Joints", c => c.Shape == "3BA" ? Band(c.Z, 10f, 15.5f, 1f) : c.Shape == "Feet" ? S(8.5f, 10.5f, c.Z) : 0);
R("Knuckles", "Joints", c => c.Shape != "artHands" ? 0 : S(0.3f, 0.6f, c.Sx) * Band(FingerT(c), 0.5f, 0.66f, 0.04f));

// ===== Arms =====
R("Armpits", "Arms", c => c.Shape != "3BA" ? 0 : Ell(c.A, c.Y, c.Z, 11.8f, -1.2f, 99.8f, 2.6f, 3.2f, 2.4f, 0.5f));
R("Biceps", "Arms", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.12f, 0.48f, 0.08f) * S(0.1f, 0.5f, d.Fwd); });
R("Triceps", "Arms", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.1f, 0.5f, 0.08f) * S(0.1f, 0.5f, -d.Fwd); });
R("Inner upper arm", "Arms", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.12f, 0.5f, 0.08f) * S(0.2f, 0.6f, -d.Across); });
R("Forearms (outer)", "Arms", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.55f, 1.05f, 0.06f) * S(-0.2f, 0.3f, d.Across - d.Fwd * 0.5f); });
R("Forearms (inner)", "Arms", c => { var a = Armness(c); if (a == 0) return 0; var d = ArmDir(c); return a * Band(ArmT(c), 0.55f, 1.05f, 0.06f) * (1 - S(-0.2f, 0.3f, d.Across - d.Fwd * 0.5f)); });

// ===== Hands =====
bool Hd(Ctx c) => c.Shape == "artHands";
float FingerT(Ctx c) => Math.Clamp((wristZ - c.Z) / (wristZ - fingerTipZ), 0, 1);
R("Palms", "Hands", c => !Hd(c) ? 0 : S(0.1f, 0.45f, -c.Sx) * Band(FingerT(c), 0.12f, 0.6f, 0.06f));
R("Back of hands", "Hands", c => !Hd(c) ? 0 : S(0.1f, 0.45f, c.Sx) * Band(FingerT(c), 0.12f, 0.55f, 0.06f));
R("Fingers", "Hands", c => !Hd(c) ? 0 : S(0.55f, 0.65f, FingerT(c)));
R("Fingertips", "Hands", c => !Hd(c) ? 0 : S(0.86f, 0.93f, FingerT(c)));

// ===== Legs =====
R("Thighs (front)", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 42f, 64f, 1.5f) * S(0.2f, 0.6f, d.Fwd); });
R("Thighs (outer)", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 41f, 62f, 1.5f) * S(0.3f, 0.7f, d.Across); });
R("Thighs (inner)", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 41f, 62f, 1.5f) * S(0.3f, 0.7f, -d.Across); });
R("Thighs (back)", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 42f, 56f, 1.5f) * S(0.2f, 0.6f, -d.Fwd); });
R("Shins", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 13f, 36f, 1.2f) * S(0.2f, 0.6f, d.Fwd); });
R("Calves", "Legs", c => { var l = Legness(c); if (l == 0) return 0; var d = LegDir(c); return l * Band(c.Z, 15f, 36f, 1.5f) * S(-0.1f, 0.4f, -d.Fwd); });

// ===== Feet =====
bool Ft(Ctx c) => c.Shape == "Feet";
R("Soles", "Feet", c => !Ft(c) ? 0 : S(-0.35f, -0.65f, c.Nz) * (1 - S(1.8f, 2.6f, c.Z)));
R("Heels", "Feet", c => !Ft(c) ? 0 : (1 - S(5.5f, 7f, c.Z)) * S(-5.5f, -7f, c.Y));
R("Toes", "Feet", c => !Ft(c) ? 0 : S(2.5f, 4f, c.Y) * (1 - S(3f, 4f, c.Z)));
R("Top of feet", "Feet", c => !Ft(c) ? 0 : S(0.2f, 0.45f, c.Nz) * S(1.5f, 2.5f, c.Z) * (1 - S(8.5f, 10.5f, c.Z)) * (1 - S(2.5f, 4f, c.Y) * (1 - S(3f, 4f, c.Z))));

// ---- rasterise into each texture's UV layout ----
var groups = shapes.GroupBy(s => s.TextureName).ToList();
var frames = new Dictionary<ShapeData, SurfaceFrames>();
foreach (var s in shapes) frames[s] = SurfaceFrames.Build(new[] { s })[0];
var used = new Dictionary<string, List<string>>();
foreach (var g in groups)
{
    var buf = regions.Select(_ => new float[size * size]).ToArray();
    var cover = new bool[size * size];
    foreach (var s in g)
    {
        var n = frames[s].Normals; var P = s.Positions; var U = s.Uvs; var I = s.Indices;
        for (var t = 0; t + 2 < I.Length; t += 3)
        {
            int a = (int)I[t], b = (int)I[t + 1], cc = (int)I[t + 2];
            float ux = U[a * 2] * size, uy = U[a * 2 + 1] * size, vx = U[b * 2] * size, vy = U[b * 2 + 1] * size, wx = U[cc * 2] * size, wy = U[cc * 2 + 1] * size;
            var area = (vx - ux) * (wy - uy) - (wx - ux) * (vy - uy);
            if (MathF.Abs(area) < 1e-9f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(ux, Math.Min(vx, wx)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(ux, Math.Max(vx, wx))));
            int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(uy, Math.Min(vy, wy)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(Math.Max(uy, Math.Max(vy, wy))));
            for (var py = y0; py <= y1; py++)
            for (var px = x0; px <= x1; px++)
            {
                float qx = px + 0.5f, qy = py + 0.5f;
                var l1 = ((wx - vx) * (qy - vy) - (wy - vy) * (qx - vx)) / area;
                var l2 = ((ux - wx) * (qy - wy) - (uy - wy) * (qx - wx)) / area;
                var l3 = 1 - l1 - l2;
                if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                float Ip(float[] arr, int k) => arr[a * 3 + k] * l1 + arr[b * 3 + k] * l2 + arr[cc * 3 + k] * l3;
                var c = new Ctx(s.Name, Ip(P, 0), Ip(P, 1), Ip(P, 2), Ip(n, 0), Ip(n, 1), Ip(n, 2));
                var idx = py * size + px;
                cover[idx] = true;
                for (var r = 0; r < regions.Count; r++) buf[r][idx] = Math.Clamp(regions[r].F(c), 0, 1);
            }
        }
    }
    used[g.Key] = new();
    for (var r = 0; r < regions.Count; r++)
    {
        if (buf[r].Max() < 0.05f) continue;
        Dilate(buf[r], cover, size, 12);
        var path = Path.Combine(outDir, g.Key, regions[r].Name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WritePng(path, size, size, buf[r].Select(v => (byte)(v * 255 + 0.5f)).ToArray(), 1);
        used[g.Key].Add(regions[r].Name);
    }
    Console.WriteLine($"{g.Key}: {used[g.Key].Count} masks");
}
File.WriteAllLines(Path.Combine(outDir, "regions.txt"), regions.Select(r => r.Category + "|" + r.Name + "|" + string.Join(",", used.Where(u => u.Value.Contains(r.Name)).Select(u => u.Key))));

MakePreset(outDir, Environment.ExpandEnvironmentVariables(@"%AppData%\SkinTextureTool\last.json"), regions, used);

// ---- debug views coloured by strongest region ----
foreach (var (view, sign) in new[] { ("front", 1f), ("back", -1f) })
{
    int W = 900, Hh = 1400; var img = new byte[W * Hh * 3]; var zb = Enumerable.Repeat(float.MinValue, W * Hh).ToArray();
    float scale = Hh / 135f;
    foreach (var s in shapes)
    {
        var P = s.Positions; var n = frames[s].Normals; var I = s.Indices;
        var col = new (byte, byte, byte)[s.VertexCount];
        for (var i = 0; i < s.VertexCount; i++)
        {
            var c = new Ctx(s.Name, P[i * 3], P[i * 3 + 1], P[i * 3 + 2], n[i * 3], n[i * 3 + 1], n[i * 3 + 2]);
            int best = -1; float bv = 0.3f;
            for (var r = 0; r < regions.Count; r++) { var v = regions[r].F(c); if (v > bv) { bv = v; best = r; } }
            var shade = 0.55f + 0.45f * Math.Max(0, n[i * 3 + 1] * sign);
            var (cr, cg, cb) = best < 0 ? (200, 200, 200) : Hue(best * 0.6180339f % 1f);
            col[i] = ((byte)(cr * shade), (byte)(cg * shade), (byte)(cb * shade));
        }
        for (var t = 0; t + 2 < I.Length; t += 3)
        {
            int[] v = { (int)I[t], (int)I[t + 1], (int)I[t + 2] };
            var sx = v.Select(k => W / 2f - P[k * 3] * scale * sign).ToArray();
            var sy = v.Select(k => Hh - P[k * 3 + 2] * scale).ToArray();
            var sd = v.Select(k => P[k * 3 + 1] * sign).ToArray();
            var area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)sx.Min()), x1 = Math.Min(W - 1, (int)sx.Max() + 1), y0 = Math.Max(0, (int)sy.Min()), y1 = Math.Min(Hh - 1, (int)sy.Max() + 1);
            for (var py = y0; py <= y1; py++) for (var px = x0; px <= x1; px++)
            {
                float qx = px + 0.5f, qy = py + 0.5f;
                var l0 = ((sx[2] - sx[1]) * (qy - sy[1]) - (sy[2] - sy[1]) * (qx - sx[1])) / area;
                var l1 = ((sx[0] - sx[2]) * (qy - sy[2]) - (sy[0] - sy[2]) * (qx - sx[2])) / area;
                var l2 = 1 - l0 - l1;
                if (l0 < 0 || l1 < 0 || l2 < 0) continue;
                var d = sd[0] * l0 + sd[1] * l1 + sd[2] * l2;
                var k = py * W + px; if (d <= zb[k]) continue; zb[k] = d;
                var m = l0 >= l1 && l0 >= l2 ? 0 : l1 >= l2 ? 1 : 2;
                img[k * 3] = col[v[m]].Item1; img[k * 3 + 1] = col[v[m]].Item2; img[k * 3 + 2] = col[v[m]].Item3;
            }
        }
    }
    WritePng(Path.Combine(outDir, $"_preview_{view}.png"), W, Hh, img, 3);
}
File.WriteAllLines(Path.Combine(outDir, "_preview_legend.txt"), regions.Select((r, i) => { var (cr, cg, cb) = Hue(i * 0.6180339f % 1f); return $"{r.Name}: #{cr:X2}{cg:X2}{cb:X2}"; }));

static (int, int, int) Hue(float h)
{
    float r = Math.Clamp(MathF.Abs(h * 6 - 3) - 1, 0, 1), g = Math.Clamp(2 - MathF.Abs(h * 6 - 2), 0, 1), b = Math.Clamp(2 - MathF.Abs(h * 6 - 4), 0, 1);
    return ((int)(40 + r * 215), (int)(40 + g * 215), (int)(40 + b * 215));
}
static void Dilate(float[] v, bool[] cover, int size, int steps)
{
    var have = (bool[])cover.Clone();
    for (var s = 0; s < steps; s++)
    {
        var add = new List<(int, float)>();
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var i = y * size + x; if (have[i]) continue;
            float sum = 0; var n = 0;
            if (x > 0 && have[i - 1]) { sum += v[i - 1]; n++; }
            if (x < size - 1 && have[i + 1]) { sum += v[i + 1]; n++; }
            if (y > 0 && have[i - size]) { sum += v[i - size]; n++; }
            if (y < size - 1 && have[i + size]) { sum += v[i + size]; n++; }
            if (n > 0) add.Add((i, sum / n));
        }
        foreach (var (i, val) in add) { v[i] = val; have[i] = true; }
    }
}
static void WritePng(string path, int w, int h, byte[] data, int channels)
{
    using var fs = File.Create(path);
    fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] d)
    {
        var len = BitConverter.GetBytes(d.Length); Array.Reverse(len); fs.Write(len);
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(d).ToArray(); fs.Write(td);
        var crc = BitConverter.GetBytes(Crc(td)); Array.Reverse(crc); fs.Write(crc);
    }
    var ihdr = new byte[13];
    void Be(int o, int v) { ihdr[o] = (byte)(v >> 24); ihdr[o + 1] = (byte)(v >> 16); ihdr[o + 2] = (byte)(v >> 8); ihdr[o + 3] = (byte)v; }
    Be(0, w); Be(4, h); ihdr[8] = 8; ihdr[9] = (byte)(channels == 1 ? 0 : 2);
    Chunk("IHDR", ihdr);
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        for (var y = 0; y < h; y++) { z.WriteByte(0); z.Write(data, y * w * channels, w * channels); }
    Chunk("IDAT", ms.ToArray());
    Chunk("IEND", Array.Empty<byte>());
}
static uint Crc(byte[] d)
{
    var c = 0xFFFFFFFFu;
    foreach (var b in d) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
    return c ^ 0xFFFFFFFFu;
}


// ---- preset wiring every region to its masks; a few start enabled with tweaks ----
static void MakePreset(string outDir, string basePath, List<(string Name, string Category, Func<Ctx, float> F)> regions, Dictionary<string, List<string>> used)
{
    var root = System.Text.Json.JsonDocument.Parse(File.ReadAllText(basePath)).RootElement;
    var baseSettings = System.Text.Json.JsonSerializer.Deserialize<SkinTextureTool.Core.Generation.SkinSettings>(root.GetProperty("Settings").GetRawText())!;
    baseSettings.Regions.Clear();
    var tweaks = new Dictionary<string, Action<SkinTextureTool.Core.Generation.SkinSettings>>
    {
        ["Lips"] = s => { s.Material.Specular = 0.55f; s.Material.Roughness = 0.4f; s.Freckles.Enabled = false; s.Pores.Strength *= 0.3f; },
        ["Cheeks"] = s => { s.RedZones.Strength = Math.Min(1, s.RedZones.Strength + 0.25f); },
        ["Nose tip"] = s => { s.RedZones.Strength = Math.Min(1, s.RedZones.Strength + 0.2f); s.Pores.Strength = Math.Min(1, s.Pores.Strength * 1.6f); s.Material.Specular += 0.1f; },
        ["Areolae"] = s => { s.Base.Color = new(s.Base.Color.R * 0.78f, s.Base.Color.G * 0.62f, s.Base.Color.B * 0.6f); s.Freckles.Enabled = false; s.Pores.Strength *= 0.4f; },
        ["Nipples"] = s => { s.Base.Color = new(s.Base.Color.R * 0.72f, s.Base.Color.G * 0.55f, s.Base.Color.B * 0.55f); s.Freckles.Enabled = false; },
        ["Vulva"] = s => { s.Base.Color = new(s.Base.Color.R * 0.85f, s.Base.Color.G * 0.68f, s.Base.Color.B * 0.66f); s.Freckles.Enabled = false; s.Material.Specular += 0.1f; },
        ["Labia (inner)"] = s => { s.Base.Color = new(s.Base.Color.R * 0.85f, s.Base.Color.G * 0.55f, s.Base.Color.B * 0.55f); s.Material.Specular = 0.6f; s.Material.Roughness = 0.3f; },
        ["Anus"] = s => { s.Base.Color = new(s.Base.Color.R * 0.75f, s.Base.Color.G * 0.58f, s.Base.Color.B * 0.56f); s.Freckles.Enabled = false; },
        ["Elbows"] = s => { s.Base.Color = new(s.Base.Color.R * 0.94f, s.Base.Color.G * 0.88f, s.Base.Color.B * 0.86f); s.Cells.Strength = Math.Min(1, s.Cells.Strength + 0.3f); s.Material.Roughness += 0.15f; },
        ["Knees"] = s => { s.RedZones.Strength = Math.Min(1, s.RedZones.Strength + 0.2f); s.Cells.Strength = Math.Min(1, s.Cells.Strength + 0.25f); s.Material.Roughness += 0.15f; },
        ["Knuckles"] = s => { s.RedZones.Strength = Math.Min(1, s.RedZones.Strength + 0.25f); s.Cells.Strength = Math.Min(1, s.Cells.Strength + 0.3f); },
        ["Palms"] = s => { s.Base.Color = new(Math.Min(1, s.Base.Color.R * 1.06f), Math.Min(1, s.Base.Color.G * 1.04f), Math.Min(1, s.Base.Color.B * 1.03f)); s.Freckles.Enabled = false; s.Moles.Enabled = false; s.RedZones.Strength += 0.1f; },
        ["Soles"] = s => { s.Base.Color = new(Math.Min(1, s.Base.Color.R * 1.05f), Math.Min(1, s.Base.Color.G * 1.03f), Math.Min(1, s.Base.Color.B * 1.02f)); s.Freckles.Enabled = false; s.Moles.Enabled = false; s.Material.Roughness += 0.2f; },
        ["Fingertips"] = s => { s.RedZones.Strength = Math.Min(1, s.RedZones.Strength + 0.2f); },
        ["Armpits"] = s => { s.Base.Color = new(s.Base.Color.R * 0.93f, s.Base.Color.G * 0.9f, s.Base.Color.B * 0.9f); s.Freckles.Enabled = false; },
    };
    var settings = baseSettings.Clone();
    foreach (var (name, cat, _) in regions)
    {
        var region = new SkinTextureTool.Core.Generation.Region { Name = $"{cat}: {name}", Settings = baseSettings.CloneWithoutRegions() };
        foreach (var (group, names) in used)
            if (names.Contains(name)) region.MaskPaths[group] = Path.GetFullPath(Path.Combine(outDir, group, name + ".png"));
        if (region.MaskPaths.Count == 0) continue;
        region.Enabled = tweaks.TryGetValue(name, out var tweak);
        tweak?.Invoke(region.Settings);
        settings.Regions.Add(region);
    }
    settings.SavePreset(Path.Combine(outDir, "Regions preset.json"));
}

record Ctx(string Shape, float X, float Y, float Z, float Nx, float Ny, float Nz)
{
    public float A => MathF.Abs(X);
    public float Sx => Nx * MathF.Sign(X == 0 ? 1 : X);
}
