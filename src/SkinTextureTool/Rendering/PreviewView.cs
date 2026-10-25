using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;

namespace SkinTextureTool.Rendering;

/// <summary>
/// The 3D preview and the owner of the GL context. Work that needs the context (uploading meshes and images,
/// generating, reading textures back) is queued and runs at the start of the next frame.
/// </summary>
public sealed class PreviewView : ContentControl
{
    private readonly GLWpfControl _gl = new();
    private readonly Queue<Action> _work = new();
    // Keeps asking for frames while work is queued: the control only draws when invalidated, and requests
    // made before it is ready (or while another frame is in flight) would otherwise never be serviced.
    private readonly System.Windows.Threading.DispatcherTimer _pump = new() { Interval = TimeSpan.FromMilliseconds(30) };
    internal SkinRenderer Renderer { get; } = new();

    private float _yaw = MathF.PI, _pitch = 0.05f, _distance = 160f;
    private Vector3 _target;
    private Point? _lastMouse;

    /// <summary>0 lit, 1 albedo, 2 normal map, 3 specular, 4 subsurface.</summary>
    public int Mode { get; set; }

    public PreviewView()
    {
        Content = _gl;
        _gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        _gl.Render += _ => Draw();
        _gl.MouseDown += (_, e) => { _lastMouse = e.GetPosition(this); _gl.CaptureMouse(); };
        _gl.MouseUp += (_, _) => { _lastMouse = null; _gl.ReleaseMouseCapture(); };
        _gl.MouseMove += OnMouseMove;
        _gl.MouseWheel += (_, e) => { _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9f : 1.1f), 2f, 2000f); Refresh(); };
        _pump.Tick += (_, _) => { if (_work.Count == 0) _pump.Stop(); else _gl.InvalidateVisual(); };
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Runs <paramref name="action"/> with the GL context current, on the next frame.</summary>
    public Task Run(Action action) => Run<object?>(() => { action(); return null; });

    public Task<T> Run<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>();
        _work.Enqueue(() =>
        {
            try { done.SetResult(func()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        Refresh();
        _pump.Start();
        return done.Task;
    }

    public void Refresh() => _gl.InvalidateVisual();

    /// <summary>Points the camera at the loaded mesh.</summary>
    public void Frame()
    {
        _target = Renderer.Centre;
        _distance = Renderer.Radius * 2.6f;
        Refresh();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastMouse is not { } last) return;
        var now = e.GetPosition(this);
        var dx = (float)(now.X - last.X);
        var dy = (float)(now.Y - last.Y);
        _lastMouse = now;
        if (e.RightButton == MouseButtonState.Pressed)
            _target.Z += dy * _distance / 400f;
        else
        {
            _yaw -= dx * 0.01f;
            _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.5f, 1.5f);
        }
        Refresh();
    }

    private void Draw()
    {
        Renderer.Initialize();
        while (_work.Count > 0) _work.Dequeue()();

        GL.ClearColor(0.16f, 0.17f, 0.19f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (!Renderer.HasMesh) return;

        var aspect = (float)Math.Max(1, _gl.ActualWidth) / (float)Math.Max(1, _gl.ActualHeight);
        var eye = _target + new Vector3(
            _distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            _distance * MathF.Cos(_pitch) * MathF.Cos(_yaw),
            _distance * MathF.Sin(_pitch));
        var view = Matrix4.LookAt(eye, _target, Vector3.UnitZ);
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(35), aspect, 0.5f, 10000f);
        var light = Vector3.Normalize(eye - _target + new Vector3(0, 0, _distance * 0.6f) + Vector3.Cross(Vector3.UnitZ, eye - _target) * 0.4f);
        Renderer.DrawPreview(view * proj, eye, light, Mode);
    }
}
