using System.Numerics;

namespace WindFlow.App.Viewport;

/// <summary>Z-up orbit camera (model units are metres, Z is up — see WindFlow.Core.Simulation and Voxelizer).
/// Mutated from the UI thread and read by the render thread, so every caller holds <see cref="Sync"/>.</summary>
public sealed class Camera
{
    public readonly object Sync = new();

    public Vector3 Target = new(0, 0, 1.5f);
    public float Distance = 28;
    public float Yaw = -2.2f;      // radians, around +Z
    public float Pitch = 0.42f;    // radians above the horizon
    public float Fov = 0.82f;      // radians, vertical
    public float SceneRadius = 20;

    public Vector3 Direction
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            return new Vector3(cp * MathF.Cos(Yaw), cp * MathF.Sin(Yaw), MathF.Sin(Pitch));
        }
    }

    public Vector3 Eye => Target + Direction * Distance;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ);

    public Matrix4x4 ViewProjection(float aspect)
    {
        float near = MathF.Max(0.02f, Distance * 0.002f);
        float far = Distance * 12f + SceneRadius * 12f + 50f;
        return View * Matrix4x4.CreatePerspectiveFieldOfView(Fov, MathF.Max(aspect, 0.05f), near, far);
    }

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.008f;
        Pitch = Math.Clamp(Pitch + dy * 0.008f, -1.52f, 1.52f);
    }

    /// <summary>Screen-parallel pan: one pixel of drag moves the target by one pixel at the target's depth.</summary>
    public void Pan(float dx, float dy, int viewportHeight)
    {
        var view = View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        float perPixel = 2f * Distance * MathF.Tan(Fov * 0.5f) / MathF.Max(viewportHeight, 1);
        Target += (-right * dx + up * dy) * perPixel;
    }

    public void Zoom(float wheelDelta) =>
        Distance = Math.Clamp(Distance * MathF.Exp(-wheelDelta * 0.0012f), 0.15f, 20000f);

    /// <summary>F: fit the whole model in view.</summary>
    public void Frame(Vector3 center, float radius)
    {
        Target = center;
        SceneRadius = radius;
        Distance = radius / MathF.Sin(Fov * 0.5f) * 0.80f;
    }
}
