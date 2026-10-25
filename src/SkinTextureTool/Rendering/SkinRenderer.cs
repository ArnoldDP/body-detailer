using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Rendering;

/// <summary>RGBA pixels, top row first.</summary>
public sealed record ImageData(int Width, int Height, byte[] Rgba);

/// <summary>The four textures the tool exports, as RGBA with row 0 at v = 0 (the DDS layout).</summary>
public sealed record GeneratedTextures(int Size, byte[] Albedo, byte[] Normal, byte[] Specular, byte[] Subsurface, byte[] Rfaos);

/// <summary>
/// Owns every GL resource: the selected shapes' buffers, the generation targets and the preview program.
/// All methods must run with the preview's GL context current.
/// </summary>
internal sealed class SkinRenderer
{
    private sealed record GpuShape(int Vao, int[] Buffers, int IndexCount);

    private GlProgram? _generate, _bake, _bleed, _preview;
    private readonly List<GpuShape> _shapes = new();
    private int _emptyVao;
    private int _size;
    private int _albedo, _height, _spec, _sss, _normal, _rfaos;   // final (after bleed) textures live in the "A" set
    private int _albedoB, _specB, _sssB, _normalB, _rfaosB;
    private int _fbo;
    private readonly Dictionary<string, int> _images = new(StringComparer.OrdinalIgnoreCase);

    public bool HasMesh => _shapes.Count > 0;
    public bool HasTextures { get; private set; }
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
        _emptyVao = GL.GenVertexArray();
        _fbo = GL.GenFramebuffer();
    }

    public void SetMesh(IReadOnlyList<ShapeData> shapes, IReadOnlyList<SurfaceFrames> frames)
    {
        foreach (var s in _shapes) { GL.DeleteVertexArray(s.Vao); GL.DeleteBuffers(s.Buffers.Length, s.Buffers); }
        _shapes.Clear();
        HasTextures = false;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < shapes.Count; i++)
        {
            var s = shapes[i];
            var f = frames[i];
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
            _shapes.Add(new GpuShape(vao, buffers, s.Indices.Length));

            for (var v = 0; v < s.Positions.Length; v += 3)
            {
                var p = new Vector3(s.Positions[v], s.Positions[v + 1], s.Positions[v + 2]);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        if (_shapes.Count > 0)
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

    private void EnsureTargets(int size)
    {
        if (_size == size) return;
        foreach (var t in new[] { _albedo, _height, _spec, _sss, _normal, _rfaos, _albedoB, _specB, _sssB, _normalB, _rfaosB })
            if (t != 0) GL.DeleteTexture(t);
        _size = size;
        _albedo = Target(size, PixelInternalFormat.Rgba8);
        _height = Target(size, PixelInternalFormat.Rgba16f);
        _spec = Target(size, PixelInternalFormat.Rgba8);
        _sss = Target(size, PixelInternalFormat.Rgba8);
        _normal = Target(size, PixelInternalFormat.Rgba8);
        _albedoB = Target(size, PixelInternalFormat.Rgba8);
        _specB = Target(size, PixelInternalFormat.Rgba8);
        _sssB = Target(size, PixelInternalFormat.Rgba8);
        _normalB = Target(size, PixelInternalFormat.Rgba8);
        _rfaos = Target(size, PixelInternalFormat.Rgba8);
        _rfaosB = Target(size, PixelInternalFormat.Rgba8);
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

    private void DrawShapes()
    {
        foreach (var s in _shapes)
        {
            GL.BindVertexArray(s.Vao);
            GL.DrawElements(PrimitiveType.Triangles, s.IndexCount, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Runs the full pipeline into textures of the given size. <paramref name="images"/> maps file paths to
    /// already-uploaded textures for masks and the detail map.
    /// </summary>
    public void Generate(SkinSettings s, int size, IReadOnlyDictionary<string, int> images)
    {
        if (_shapes.Count == 0 || _generate == null || _bake == null || _bleed == null) return;
        GL.GetInteger(GetPName.FramebufferBinding, out int previousFbo);
        var previousViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, previousViewport);

        EnsureTargets(size);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.Viewport(0, 0, size, size);

        // 1. skin layers
        BindTargets(_albedo, _height, _spec, _sss, _rfaos);
        GL.ClearColor(0, 0, 0, 0);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _generate.Use();
        SetLayerUniforms(_generate, s);
        var unit = 0;
        foreach (var (name, layer) in Masks)
        {
            var path = layer(s).MaskPath;
            var has = path != null && images.TryGetValue(path, out var tex) && tex != 0;
            _generate.Texture("m" + name, unit++, has ? images[path!] : 0);
            _generate.Set("h" + name, has ? 1 : 0);
        }
        DrawShapes();

        // 2. model-space normal bake
        BindTargets(_normal);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _bake.Use();
        _bake.Texture("uHeight", 0, _height);
        _bake.Set("uTexel", new Vector2(1f / size, 1f / size));
        _bake.Set("uDetailStrength", s.Normals.Enabled ? s.Normals.DetailStrength : 0f);
        var detail = s.Normals.DetailMapPath;
        var hasDetail = s.Normals.Enabled && detail != null && images.TryGetValue(detail, out var dt) && dt != 0;
        _bake.Texture("uDetailMap", 1, hasDetail ? images[detail!] : 0);
        _bake.Set("uHasDetailMap", hasDetail ? 1 : 0);
        _bake.Set("uDetailMapStrength", s.Normals.DetailMapStrength);
        _bake.Set("uZCorrection", s.Normals.TangentZCorrection ? 1 : 0);
        DrawShapes();

        // 3. bleed past island edges, ping-ponging between the two texture sets
        _bleed.Use();
        GL.BindVertexArray(_emptyVao);
        var iterations = Math.Max(8, size / 128);
        int[] a = { _albedo, _normal, _spec, _sss, _rfaos }, b = { _albedoB, _normalB, _specB, _sssB, _rfaosB };
        for (var i = 0; i < iterations; i++)
        {
            BindTargets(b);
            for (var t = 0; t < 5; t++) _bleed.Texture("t" + t, t, a[t]);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            (a, b) = (b, a);
        }
        GL.BindVertexArray(0);
        (_albedo, _normal, _spec, _sss, _rfaos) = (a[0], a[1], a[2], a[3], a[4]);
        (_albedoB, _normalB, _specB, _sssB, _rfaosB) = (b[0], b[1], b[2], b[3], b[4]);

        foreach (var t in a)
        {
            GL.BindTexture(TextureTarget.Texture2D, t);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFbo);
        GL.Viewport(previousViewport[0], previousViewport[1], previousViewport[2], previousViewport[3]);
        HasTextures = true;
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

    public void DrawPreview(Matrix4 mvp, Vector3 eye, Vector3 light, int mode)
    {
        if (_preview == null) return;
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        _preview.Use();
        _preview.Set("uMvp", mvp);
        _preview.Set("uEye", eye);
        _preview.Set("uLight", light);
        _preview.Set("uMode", mode);
        _preview.Set("uReady", HasTextures ? 1 : 0);
        if (HasTextures)
        {
            _preview.Texture("uAlbedo", 0, _albedo);
            _preview.Texture("uNormal", 1, _normal);
            _preview.Texture("uSpec", 2, _spec);
            _preview.Texture("uSss", 3, _sss);
            _preview.Texture("uRfaos", 4, _rfaos);
        }
        DrawShapes();
    }

    /// <summary>Reads the current textures back for export: RGBA, row 0 at v = 0, alpha forced opaque.</summary>
    public GeneratedTextures Read()
    {
        byte[] Get(int tex, bool keepAlpha = false)
        {
            var data = new byte[_size * _size * 4];
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
            if (!keepAlpha) for (var i = 3; i < data.Length; i += 4) data[i] = 255;
            return data;
        }
        return new GeneratedTextures(_size, Get(_albedo), Get(_normal), Get(_spec), Get(_sss), Get(_rfaos, keepAlpha: true));
    }
}
