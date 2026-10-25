using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SkinTextureTool.Rendering;

internal sealed class GlProgram
{
    private readonly Dictionary<string, int> _locations = new();
    public int Handle { get; }

    public GlProgram(string vertex, string fragment)
    {
        Handle = GL.CreateProgram();
        var shaders = new[] { Compile(ShaderType.VertexShader, vertex), Compile(ShaderType.FragmentShader, fragment) };
        foreach (var s in shaders) GL.AttachShader(Handle, s);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + GL.GetProgramInfoLog(Handle));
        foreach (var s in shaders) { GL.DetachShader(Handle, s); GL.DeleteShader(s); }
    }

    private static int Compile(ShaderType type, string source)
    {
        var s = GL.CreateShader(type);
        GL.ShaderSource(s, source);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + GL.GetShaderInfoLog(s));
        return s;
    }

    public void Use() => GL.UseProgram(Handle);

    private int Loc(string name)
    {
        if (!_locations.TryGetValue(name, out var l)) _locations[name] = l = GL.GetUniformLocation(Handle, name);
        return l;
    }

    public void Set(string name, float v) => GL.Uniform1(Loc(name), v);
    public void Set(string name, int v) => GL.Uniform1(Loc(name), v);
    public void Set(string name, uint v) => GL.Uniform1(Loc(name), v);
    public void Set(string name, Vector2 v) => GL.Uniform2(Loc(name), v);
    public void Set(string name, Vector3 v) => GL.Uniform3(Loc(name), v);
    public void Set(string name, Vector4 v) => GL.Uniform4(Loc(name), v);
    public void Set(string name, Matrix4 m) => GL.UniformMatrix4(Loc(name), false, ref m);

    /// <summary>Binds a texture to a unit and points the sampler uniform at it.</summary>
    public void Texture(string name, int unit, int texture)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(Loc(name), unit);
    }
}
