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

    /// <summary>
    /// While on, left-drag paints (raising <see cref="Paint"/>) and the brush is drawn under the cursor;
    /// Alt+left-drag or middle-drag rotates instead.
    /// </summary>
    public bool PaintMode { get; set; }
    public float BrushRadius { get; set; } = 3f;
    public bool BrushMirror { get; set; }
    /// <summary>A brush dab: surface point, its normal, and whether it starts a new stroke.</summary>
    public event Action<Vector3, Vector3, bool>? Paint;
    public event Action? StrokeEnded;
    /// <summary>Triangles that can be picked: positions and indices per shape, as drawn.</summary>
    public IReadOnlyList<(float[] Positions, uint[] Indices)> PickMesh { get; set; } = Array.Empty<(float[], uint[])>();

    private Matrix4 _viewProj;
    private bool _painting;
    private Vector3? _lastDab;
    private Vector4 _brush; // xyz centre, w radius (0 = hidden)

    public PreviewView()
    {
        Content = _gl;
        _gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        _gl.Render += _ => Draw();
        _gl.MouseDown += (_, e) =>
        {
            _lastMouse = e.GetPosition(this);
            _gl.CaptureMouse();
            _painting = PaintMode && e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
            _lastDab = null;
            if (_painting) PaintAt(_lastMouse.Value, true);
        };
        _gl.MouseUp += (_, _) =>
        {
            _lastMouse = null;
            _gl.ReleaseMouseCapture();
            if (_painting) StrokeEnded?.Invoke();
            _painting = false;
        };
        _gl.MouseLeave += (_, _) => { if (_brush.W != 0) { _brush.W = 0; Refresh(); } };
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
        if (PaintMode && !_painting) UpdateBrush(e.GetPosition(this));
        if (_lastMouse is not { } last) return;
        var now = e.GetPosition(this);
        if (_painting)
        {
            _lastMouse = now;
            PaintAt(now, false);
            return;
        }
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

    private void UpdateBrush(Point p)
    {
        var hit = Pick(p);
        _brush = hit is { } h ? new Vector4(h.Position, BrushRadius) : Vector4.Zero;
        Refresh();
    }

    /// <summary>Dabs along the path from the last dab, spaced a fraction of the brush apart.</summary>
    private void PaintAt(Point p, bool first)
    {
        var hit = Pick(p);
        if (hit is not { } h) { UpdateBrush(p); return; }
        _brush = new Vector4(h.Position, BrushRadius);
        var spacing = BrushRadius * 0.25f;
        if (_lastDab is { } last && !first)
        {
            var d = (h.Position - last).Length;
            if (d < spacing) { Refresh(); return; }
            if (d < BrushRadius * 3)
                for (var t = spacing; t < d; t += spacing) Paint?.Invoke(Vector3.Lerp(last, h.Position, t / d), h.Normal, false);
        }
        Paint?.Invoke(h.Position, h.Normal, first);
        _lastDab = h.Position;
        Refresh();
    }

    /// <summary>The nearest surface point under the cursor, with the face normal turned towards the camera.</summary>
    public (Vector3 Position, Vector3 Normal)? Pick(Point p)
    {
        var w = Math.Max(1, _gl.ActualWidth); var h = Math.Max(1, _gl.ActualHeight);
        var x = (float)(p.X / w * 2 - 1); var y = (float)(1 - p.Y / h * 2);
        Matrix4 inv;
        try { inv = _viewProj.Inverted(); } catch { return null; }
        var a = new Vector4(x, y, -1, 1) * inv; var b = new Vector4(x, y, 1, 1) * inv;
        var origin = a.Xyz / a.W; var dir = (b.Xyz / b.W - origin).Normalized();
        var best = float.MaxValue; Vector3 bestN = default;
        foreach (var (P, I) in PickMesh)
            for (var t = 0; t + 2 < I.Length; t += 3)
            {
                var v0 = new Vector3(P[I[t] * 3], P[I[t] * 3 + 1], P[I[t] * 3 + 2]);
                var v1 = new Vector3(P[I[t + 1] * 3], P[I[t + 1] * 3 + 1], P[I[t + 1] * 3 + 2]);
                var v2 = new Vector3(P[I[t + 2] * 3], P[I[t + 2] * 3 + 1], P[I[t + 2] * 3 + 2]);
                var e1 = v1 - v0; var e2 = v2 - v0;
                var pv = Vector3.Cross(dir, e2);
                var det = Vector3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-9f) continue;
                var invDet = 1 / det;
                var tv = origin - v0;
                var u = Vector3.Dot(tv, pv) * invDet; if (u < 0 || u > 1) continue;
                var qv = Vector3.Cross(tv, e1);
                var v = Vector3.Dot(dir, qv) * invDet; if (v < 0 || u + v > 1) continue;
                var dist = Vector3.Dot(e2, qv) * invDet;
                if (dist <= 0 || dist >= best) continue;
                best = dist;
                bestN = Vector3.Cross(e1, e2).Normalized();
            }
        if (best == float.MaxValue) return null;
        if (Vector3.Dot(bestN, dir) > 0) bestN = -bestN;
        return (origin + dir * best, bestN);
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
        _viewProj = view * proj;
        var brush = PaintMode ? _brush : Vector4.Zero;
        var mirror = brush.W > 0 && BrushMirror ? new Vector4(-brush.X, brush.Y, brush.Z, brush.W) : Vector4.Zero;
        Renderer.DrawPreview(_viewProj, eye, light, Mode, PaintMode, brush, mirror);
    }
}
