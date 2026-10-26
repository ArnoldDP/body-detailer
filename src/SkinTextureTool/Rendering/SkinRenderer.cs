using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Rendering;

/// <summary>RGBA pixels, top row first.</summary>
public sealed record ImageData(int Width, int Height, byte[] Rgba);

/// <summary>The textures the tool exports, as RGBA with row 0 at v = 0 (the DDS layout).</summary>
public sealed record GeneratedTextures(int Size, byte[] Albedo, byte[] Normal, byte[] Specular, byte[] Subsurface, byte[] Rfaos);

/// <summary>A shape ready to upload: its mesh and the surface frames baked from it.</summary>
public sealed record MeshInput(ShapeData Shape, SurfaceFrames Frames);

/// <summary>
/// Owns every GL resource: per texture group (shapes sharing one diffuse texture) the shapes' buffers and the
/// generated textures, plus the programs. All methods must run with the preview's GL context current.
/// </summary>
internal sealed class SkinRenderer
{
    private sealed record GpuShape(int Vao, int[] Buffers, int IndexCount);

    /// <summary>One texture set and the shapes drawn with it. The "A" textures hold the finished result.</summary>
    private sealed class Group
    {
        public required string Name { get; init; }
        public List<GpuShape> Shapes { get; } = new();
        /// <summary>Typical world units per unit of UV across the group's surface.</summary>
        public float WorldPerUv;
        public int Size;
        public int Albedo, Height, Spec, Sss, Normal, Rfaos;
        public int AlbedoB, SpecB, SssB, NormalB, RfaosB, HeightB;
        public int AlbedoT, HeightT, SpecT, SssT, RfaosT; // a region's layers before blending
        public bool Ready;
        /// <summary>The meshes' current textures, uploaded when "start from existing" is on (0 when absent).</summary>
        public int ExAlbedo, ExNormal, ExSpec, ExSss;
        public IEnumerable<int> All => new[]
        {
            Albedo, Height, Spec, Sss, Normal, Rfaos, AlbedoB, SpecB, SssB, NormalB, RfaosB, HeightB,
            AlbedoT, HeightT, SpecT, SssT, RfaosT,
        };
    }

    private GlProgram? _generate, _bake, _bleed, _preview, _composite;
    private readonly List<Group> _groups = new();
    private int _emptyVao;
    private float _footprint = 0.05f;
    private int _fbo;
    private readonly Dictionary<string, int> _images = new(StringComparer.OrdinalIgnoreCase);

    public bool HasMesh => _groups.Count > 0;
    public Vector3 Centre { get; private set; }
    public float Radius { get; private set; } = 60f;

    private static readonly (string Uniform, Func<SkinSettings, Layer> Layer)[] Masks =
    {
        ("Red", s => s.RedZones), ("Yellow", s => s.YellowZones), ("Blue", s => s.BlueZones), ("Lips", s => s.Lips),
        ("Pores", s => s.Pores), ("Freck", s => s.Freckles), ("Mole", s => s.Moles), ("Pimple", s => s.Pimples),
        ("Blem", s => s.Blemishes), ("RedSpot", s => s.RedSpots), ("Veins", s => s.Veins),
    };

    public void Initialize()
    {
        if (_generate != null) return;
        _generate = new GlProgram(Shaders.UvVertex, Shaders.Generate);
        _bake = new GlProgram(Shaders.UvVertex, Shaders.BakeNormals);
        _bleed = new GlProgram(Shaders.ScreenVertex, Shaders.Bleed);
        _preview = new GlProgram(Shaders.PreviewVertex, Shaders.PreviewFragment);
        _composite = new GlProgram(Shaders.ScreenVertex, Shaders.Composite);
        _emptyVao = GL.GenVertexArray();
        _fbo = GL.GenFramebuffer();
    }

    /// <summary>Replaces the mesh: one group per texture name, each with its shapes.</summary>
    public void SetMesh(IReadOnlyList<(string Name, IReadOnlyList<MeshInput> Shapes)> groups)
    {
        foreach (var g in _groups)
        {
            foreach (var s in g.Shapes) { GL.DeleteVertexArray(s.Vao); GL.DeleteBuffers(s.Buffers.Length, s.Buffers); }
            foreach (var t in g.All) if (t != 0) GL.DeleteTexture(t);
        }
        _groups.Clear();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (name, shapes) in groups)
        {
            var group = new Group { Name = name };
            var scales = new List<float>();
            foreach (var (s, f) in shapes)
            {
                for (var v = 0; v + 1 < f.UvScales.Length; v += 2)
                    if (f.UvScales[v] > 0) scales.Add(MathF.Max(f.UvScales[v], f.UvScales[v + 1]));
                var vao = GL.GenVertexArray();
                GL.BindVertexArray(vao);
                var buffers = new int[7];
                GL.GenBuffers(7, buffers);
                Attribute(buffers[0], 0, 3, s.Positions);
                Attribute(buffers[1], 1, 2, s.Uvs);
                Attribute(buffers[2], 2, 3, f.Normals);
                Attribute(buffers[3], 3, 3, f.Tangents);
                Attribute(buffers[4], 4, 3, f.Bitangents);
                Attribute(buffers[5], 5, 2, f.UvScales);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, buffers[6]);
                GL.BufferData(BufferTarget.ElementArrayBuffer, s.Indices.Length * sizeof(uint), s.Indices, BufferUsageHint.StaticDraw);
                GL.BindVertexArray(0);
                group.Shapes.Add(new GpuShape(vao, buffers, s.Indices.Length));

                for (var v = 0; v < s.Positions.Length; v += 3)
                {
                    var p = new Vector3(s.Positions[v], s.Positions[v + 1], s.Positions[v + 2]);
                    min = Vector3.ComponentMin(min, p);
                    max = Vector3.ComponentMax(max, p);
                }
            }
            scales.Sort();
            group.WorldPerUv = scales.Count > 0 ? scales[scales.Count / 2] : 100f;
            _groups.Add(group);
        }
        if (_groups.Count > 0)
        {
            Centre = (min + max) / 2;
            Radius = (max - min).Length / 2;
        }
    }

    private static void Attribute(int buffer, int location, int size, float[] data)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(location);
        GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, 0, 0);
    }

    /// <summary>Uploads (or reuses) an image as a texture, keyed by its file path.</summary>
    public int ImageTexture(string key, ImageData image)
    {
        if (_images.TryGetValue(key, out var id)) return id;
        id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Rgba);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _images[key] = id;
        return id;
    }

    public void ForgetImage(string key)
    {
        if (_images.Remove(key, out var id)) GL.DeleteTexture(id);
    }

    private static void EnsureTargets(Group g, int size)
    {
        if (g.Size == size) return;
        foreach (var t in g.All) if (t != 0) GL.DeleteTexture(t);
        g.Size = size;
        g.Albedo = Target(size, PixelInternalFormat.Rgba8);
        g.Height = Target(size, PixelInternalFormat.Rgba16f);
        g.Spec = Target(size, PixelInternalFormat.Rgba8);
        g.Sss = Target(size, PixelInternalFormat.Rgba8);
        g.Normal = Target(size, PixelInternalFormat.Rgba8);
        g.Rfaos = Target(size, PixelInternalFormat.Rgba8);
        g.AlbedoB = Target(size, PixelInternalFormat.Rgba8);
        g.SpecB = Target(size, PixelInternalFormat.Rgba8);
        g.SssB = Target(size, PixelInternalFormat.Rgba8);
        g.NormalB = Target(size, PixelInternalFormat.Rgba8);
        g.RfaosB = Target(size, PixelInternalFormat.Rgba8);
        g.HeightB = Target(size, PixelInternalFormat.Rgba16f);
        g.AlbedoT = Target(size, PixelInternalFormat.Rgba8);
        g.HeightT = Target(size, PixelInternalFormat.Rgba16f);
        g.SpecT = Target(size, PixelInternalFormat.Rgba8);
        g.SssT = Target(size, PixelInternalFormat.Rgba8);
        g.RfaosT = Target(size, PixelInternalFormat.Rgba8);
    }

    private static int Target(int size, PixelInternalFormat format)
    {
        var id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, size, size, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return id;
    }

    private void BindTargets(params int[] textures)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        for (var i = 0; i < 5; i++)
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D,
                i < textures.Length ? textures[i] : 0, 0);
        GL.DrawBuffers(textures.Length, Enumerable.Range(0, textures.Length).Select(i => DrawBuffersEnum.ColorAttachment0 + i).ToArray());
    }

    private static void DrawShapes(Group g)
    {
        foreach (var s in g.Shapes)
        {
            GL.BindVertexArray(s.Vao);
            GL.DrawElements(PrimitiveType.Triangles, s.IndexCount, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Runs the full pipeline for every group (or just <paramref name="only"/>) into textures of the given size.
    /// <paramref name="images"/> maps file paths to uploaded textures for masks and detail maps.
    /// </summary>
    public void Generate(SkinSettings s, int size, IReadOnlyDictionary<string, int> images, string? only = null)
    {
        if (_generate == null || _bake == null || _bleed == null) return;
        GL.GetInteger(GetPName.FramebufferBinding, out int previousFbo);
        var previousViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, previousViewport);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.Viewport(0, 0, size, size);

        // every group band-limits its detail to the coarsest group's texel size at this resolution
        _footprint = _groups.Count > 0 ? _groups.Max(g => g.WorldPerUv) / size : 0.05f;
        foreach (var g in _groups.Where(g => only == null || g.Name == only))
            GenerateGroup(g, s, size, images);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFbo);
        GL.Viewport(previousViewport[0], previousViewport[1], previousViewport[2], previousViewport[3]);
    }

    private int Image(IReadOnlyDictionary<string, int> images, string? path) =>
        path != null && images.TryGetValue(path, out var t) ? t : 0;

    private void GenerateGroup(Group g, SkinSettings s, int size, IReadOnlyDictionary<string, int> images)
    {
        EnsureTargets(g, size);

        // 1. skin layers, then each region's layers blended over them through its mask
        RenderLayers(g, s, images, g.Albedo, g.Height, g.Spec, g.Sss, g.Rfaos);
        foreach (var region in s.Regions.Where(r => r.Enabled && r.Strength > 0))
        {
            var mask = Image(images, region.MaskFor(g.Name));
            if (mask == 0) continue;
            // regions start from the same existing textures as the main skin, so a masked area doesn't drop them
            region.Settings.StartFromExisting = s.StartFromExisting;
            RenderLayers(g, region.Settings, images, g.AlbedoT, g.HeightT, g.SpecT, g.SssT, g.RfaosT);
            BindTargets(g.AlbedoB, g.HeightB, g.SpecB, g.SssB, g.RfaosB);
            _composite!.Use();
            int[] baseSet = { g.Albedo, g.Height, g.Spec, g.Sss, g.Rfaos }, regionSet = { g.AlbedoT, g.HeightT, g.SpecT, g.SssT, g.RfaosT };
            for (var t = 0; t < 5; t++)
            {
                _composite.Texture("b" + t, t, baseSet[t]);
                _composite.Texture("r" + t, 5 + t, regionSet[t]);
            }
            _composite.Texture("uMask", 10, mask);
            _composite.Set("uStrength", region.Strength);
            GL.BindVertexArray(_emptyVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            (g.Albedo, g.AlbedoB) = (g.AlbedoB, g.Albedo);
            (g.Height, g.HeightB) = (g.HeightB, g.Height);
            (g.Spec, g.SpecB) = (g.SpecB, g.Spec);
            (g.Sss, g.SssB) = (g.SssB, g.Sss);
            (g.Rfaos, g.RfaosB) = (g.RfaosB, g.Rfaos);
        }

        // 2. model-space normal bake
        BindTargets(g.Normal);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _bake!.Use();
        _bake.Texture("uHeight", 0, g.Height);
        _bake.Set("uTexel", new Vector2(1f / size, 1f / size));
        _bake.Set("uDetailStrength", s.Normals.Enabled ? s.Normals.DetailStrength : 0f);
        var detail = s.Normals.Enabled ? Image(images, s.Normals.DetailMapFor(g.Name)) : 0;
        _bake.Texture("uDetailMap", 1, detail);
        _bake.Set("uHasDetailMap", detail != 0 ? 1 : 0);
        _bake.Set("uDetailMapStrength", s.Normals.DetailMapStrength);
        _bake.Set("uZCorrection", s.Normals.TangentZCorrection ? 1 : 0);
        var exNormal = s.StartFromExisting.Enabled ? g.ExNormal : 0;
        _bake.Texture("uExNormal", 2, exNormal);
        _bake.Set("hExNormal", exNormal != 0 ? 1 : 0);
        _bake.Set("uExNormalMix", s.StartFromExisting.NormalMap);
        DrawShapes(g);

        // 3. bleed past island edges, ping-ponging between the two texture sets
        _bleed!.Use();
        GL.BindVertexArray(_emptyVao);
        var iterations = Math.Max(8, size / 128);
        int[] a = { g.Albedo, g.Normal, g.Spec, g.Sss, g.Rfaos }, b = { g.AlbedoB, g.NormalB, g.SpecB, g.SssB, g.RfaosB };
        for (var i = 0; i < iterations; i++)
        {
            BindTargets(b);
            for (var t = 0; t < 5; t++) _bleed.Texture("t" + t, t, a[t]);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            (a, b) = (b, a);
        }
        GL.BindVertexArray(0);
        (g.Albedo, g.Normal, g.Spec, g.Sss, g.Rfaos) = (a[0], a[1], a[2], a[3], a[4]);
        (g.AlbedoB, g.NormalB, g.SpecB, g.SssB, g.RfaosB) = (b[0], b[1], b[2], b[3], b[4]);

        foreach (var t in a)
        {
            GL.BindTexture(TextureTarget.Texture2D, t);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        }
        g.Ready = true;
    }

    /// <summary>Runs the skin layers with <paramref name="s"/> (and its layer masks) into the given targets.</summary>
    private void RenderLayers(Group g, SkinSettings s, IReadOnlyDictionary<string, int> images, params int[] targets)
    {
        BindTargets(targets);
        GL.ClearColor(0, 0, 0, 0);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _generate!.Use();
        SetLayerUniforms(_generate, s);
        _generate.Set("uFootprint", _footprint);
        var ex = s.StartFromExisting;
        var useEx = ex.Enabled;
        _generate.Texture("uExAlbedo", 11, useEx ? g.ExAlbedo : 0); _generate.Set("hExAlbedo", useEx && g.ExAlbedo != 0 ? 1 : 0);
        _generate.Texture("uExSpec", 12, useEx ? g.ExSpec : 0); _generate.Set("hExSpec", useEx && g.ExSpec != 0 ? 1 : 0);
        _generate.Texture("uExSss", 13, useEx ? g.ExSss : 0); _generate.Set("hExSss", useEx && g.ExSss != 0 ? 1 : 0);
        _generate.Set("uExMix", new Vector3(ex.Diffuse, ex.Specular, ex.Subsurface));
        var unit = 0;
        foreach (var (name, layer) in Masks)
        {
            var tex = Image(images, layer(s).MaskFor(g.Name));
            _generate.Texture("m" + name, unit++, tex);
            _generate.Set("h" + name, tex != 0 ? 1 : 0);
        }
        DrawShapes(g);
    }

    private static Vector3 C(Rgb c) => new(c.R, c.G, c.B);

    private static void SetLayerUniforms(GlProgram p, SkinSettings s)
    {
        p.Set("uSeed", (uint)s.Seed);
        p.Set("uScale", s.Scale);
        p.Set("uUseUv", s.UseUvCoordinates ? 1 : 0);
        p.Set("uBaseColor", C(s.Base.Color));
        p.Set("uMottling", s.Base.Enabled ? s.Base.Mottling : 0f);
        p.Set("uMottlingScale", s.Base.MottlingScale);
        void Zone(string name, ZoneLayer z)
        {
            p.Set($"u{name}Color", C(z.Color));
            p.Set($"u{name}", new Vector3(z.Strength, z.Scale, z.Enabled ? 1 : 0));
        }
        Zone("Red", s.RedZones); Zone("Yellow", s.YellowZones); Zone("Blue", s.BlueZones);
        p.Set("uLipsColor", C(s.Lips.Color));
        p.Set("uLips", new Vector2(s.Lips.Strength, s.Lips.Enabled ? 1 : 0));
        p.Set("uPores", s.Pores.Enabled ? new Vector4(s.Pores.Strength, s.Pores.Size, s.Pores.Density, s.Pores.Dirt) : Vector4.Zero);
        p.Set("uCells", new Vector3(s.Cells.Strength, s.Cells.Scale, s.Cells.Enabled ? 1 : 0));
        p.Set("uGoose", new Vector3(s.Goosebumps.Strength, s.Goosebumps.Scale, s.Goosebumps.Enabled ? 1 : 0));
        void Spot(string name, SpotLayer l)
        {
            p.Set($"u{name}Color", C(l.Color));
            p.Set($"u{name}", l.Enabled ? new Vector4(l.Amount, l.Scale, l.Strength, l.Bump) : Vector4.Zero);
        }
        Spot("Freck", s.Freckles); Spot("Mole", s.Moles); Spot("Pimple", s.Pimples); Spot("Blem", s.Blemishes); Spot("RedSpot", s.RedSpots);
        p.Set("uVeinColor", C(s.Veins.Color));
        p.Set("uVeins", new Vector3(s.Veins.Strength, s.Veins.Scale, s.Veins.Enabled ? 1 : 0));
        p.Set("uMaterial", new Vector4(s.Material.Specular, s.Material.SpecularVariation, s.Material.Enabled ? s.Material.Subsurface : 0f, 0));
        p.Set("uSssColor", C(s.Material.SubsurfaceColor));
        p.Set("uPbr", new Vector4(s.Material.Roughness, s.Material.RoughnessVariation, s.Material.Enabled ? s.Material.Fuzz : 0f, s.Material.CavityOcclusion));
    }

    private readonly Dictionary<string, (int Texture, int Size)> _paint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Uploads a group's paint canvas (8-bit greys) to show over the preview.</summary>
    public void SetPaint(string group, int size, byte[] values)
    {
        if (!_paint.TryGetValue(group, out var p) || p.Size != size)
        {
            if (p.Texture != 0) GL.DeleteTexture(p.Texture);
            p = (GL.GenTexture(), size);
            GL.BindTexture(TextureTarget.Texture2D, p.Texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, size, size, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _paint[group] = p;
        }
        GL.BindTexture(TextureTarget.Texture2D, p.Texture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, size, size, PixelFormat.Red, PixelType.UnsignedByte, values);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    public void DrawPreview(Matrix4 mvp, Vector3 eye, Vector3 light, int mode, bool showPaint, Vector4 brush, Vector4 brushMirror)
    {
        if (_preview == null) return;
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        _preview.Use();
        _preview.Set("uMvp", mvp);
        _preview.Set("uEye", eye);
        _preview.Set("uLight", light);
        _preview.Set("uMode", mode);
        _preview.Set("uBrush", brush);
        _preview.Set("uBrushMirror", brushMirror);
        foreach (var g in _groups)
        {
            _preview.Set("uReady", g.Ready ? 1 : 0);
            if (g.Ready)
            {
                _preview.Texture("uAlbedo", 0, g.Albedo);
                _preview.Texture("uNormal", 1, g.Normal);
                _preview.Texture("uSpec", 2, g.Spec);
                _preview.Texture("uSss", 3, g.Sss);
                _preview.Texture("uRfaos", 4, g.Rfaos);
            }
            var paint = showPaint && _paint.TryGetValue(g.Name, out var p) ? p.Texture : 0;
            _preview.Texture("uPaint", 5, paint);
            _preview.Set("uHasPaint", paint != 0 ? 1 : 0);
            DrawShapes(g);
        }
    }

    public IReadOnlyList<string> GroupNames => _groups.Select(g => g.Name).ToList();

    /// <summary>Sets a group's existing textures (image-texture ids, 0 for none).</summary>
    public void SetExisting(string group, int albedo, int normal, int spec, int sss)
    {
        var g = _groups.FirstOrDefault(x => x.Name == group);
        if (g == null) return;
        (g.ExAlbedo, g.ExNormal, g.ExSpec, g.ExSss) = (albedo, normal, spec, sss);
    }

    /// <summary>Reads one group's textures back for export: RGBA, row 0 at v = 0.</summary>
    public GeneratedTextures Read(string group)
    {
        var g = _groups.First(x => x.Name == group);
        byte[] Get(int tex, bool keepAlpha = false)
        {
            var data = new byte[g.Size * g.Size * 4];
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
            if (!keepAlpha) for (var i = 3; i < data.Length; i += 4) data[i] = 255;
            return data;
        }
        return new GeneratedTextures(g.Size, Get(g.Albedo), Get(g.Normal), Get(g.Spec), Get(g.Sss), Get(g.Rfaos, keepAlpha: true));
    }
}
