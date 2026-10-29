using System.Numerics;

namespace WindFlow.App.Viewport;

public enum WallMode { Clay, Ghost, Xray }

/// <summary>What a handle moves. X/Y/Z and Along are one axis (drawn as a double-headed arrow), GroundPlane and
/// Plane slide in a plane (a flat square), Rotate turns about a vertical axis (a ring).</summary>
public enum HandleAxis { X, Y, Z, GroundPlane, Rotate, Along, Plane }

public enum HandleShape { Arrow, Ring, Square }

/// <summary>One draggable handle. <see cref="Id"/> is stable across rebuilds so hover and drag survive a frame.
/// <paramref name="Dir"/> is the axis (Along), the plane normal (Plane) or ignored for the fixed kinds;
/// <paramref name="Size"/> is the drawn half-length / radius in pixels (0 = the shape's default);
/// <paramref name="Knob"/> is a ring's current-angle mark in radians, NaN for none; <paramref name="Gap"/> leaves
/// the middle of an arrow empty (pixels from the anchor) so it frames a glyph instead of overdrawing it.</summary>
public readonly record struct Handle(int Id, Vector3 Position, HandleAxis Axis, Vector3 Dir = default,
    float Size = 0f, float Knob = float.NaN, float Gap = 0f)
{
    public Vector3 Direction => Axis switch
    {
        HandleAxis.X => Vector3.UnitX,
        HandleAxis.Y => Vector3.UnitY,
        HandleAxis.Along or HandleAxis.Plane => Dir,
        _ => Vector3.UnitZ,
    };

    public HandleShape Shape => Axis switch
    {
        HandleAxis.Rotate => HandleShape.Ring,
        HandleAxis.GroundPlane or HandleAxis.Plane => HandleShape.Square,
        _ => HandleShape.Arrow,
    };
}

/// <summary>A handle laid out on screen, in pixels (y down). Arrow: the shaft runs Centre-U .. Centre+U.
/// Ring: the point at angle a is Centre + U cos a + V sin a. Square: corners Centre ± U ± V.</summary>
public readonly record struct GizmoLayout(Vector2 Centre, Vector2 U, Vector2 V);

/// <summary>The viewport's drag handles: where they are, which one the cursor is over, and what a drag does.
/// Handles are drawn as constant-pixel gizmos (Revit/Blender style) and picked against the same screen-space
/// layout the shader draws, so what looks grabbable is grabbable and nothing else is.</summary>
public static class Gizmos
{
    public const float PickSlack = 6f;            // pixels either side of a stroke
    public const float ArrowHalf = 30f, RingRadius = 30f, WindRing = 22f, SquareHalf = 9f;
    // A device gizmo frames the glyph it moves: arrows start clear of the hub, the ring sits outside the heads.
    public const float DeviceArrowHalf = 38f, DeviceGap = 14f, DeviceRing = 44f, DeviceSquareHalf = 11f;
    public const int RingSegments = 48;
    const float ProbeStep = 0.25f;                // metres: the world step projected to find a screen direction

    public const int SliceZ = 0, SliceCut = 1, Wind = 2;
    public const int BoxXMin = 10, BoxXMax = 11, BoxYMin = 12, BoxYMax = 13, BoxZMin = 14, BoxZMax = 15, BoxCentre = 16;
    /// <summary>The selected device: slide in its mount plane, slide along each in-plane axis, turn about Z.</summary>
    public const int DevMove = 20, DevU = 21, DevV = 22, DevYaw = 23;

    static readonly Handle[] None = [];

    public static Handle[] Build(Renderer renderer)
    {
        var list = new List<Handle>(12);
        var particles = renderer.Particles;
        var focusMin = particles.FocusMin;
        var focusMax = particles.FocusMax;
        bool haveFocus = focusMax.X > focusMin.X;

        if (haveFocus && particles.SliceHorizontal)
            list.Add(new Handle(SliceZ, new Vector3((focusMin.X + focusMax.X) * 0.5f, focusMin.Y, particles.SliceZ), HandleAxis.Z));

        if (haveFocus && particles.SliceVertical)
            list.Add(particles.SliceAxis == 1
                ? new Handle(SliceCut, new Vector3(particles.SliceOther, focusMin.Y, focusMax.Z), HandleAxis.X)
                : new Handle(SliceCut, new Vector3(focusMin.X, particles.SliceOther, focusMax.Z), HandleAxis.Y));

        var scene = renderer.CurrentScene;
        if (scene is { HasWind: true })
            list.Add(new Handle(Wind, scene.WindHandle, HandleAxis.Rotate, Vector3.UnitZ, WindRing));

        if (renderer.ShowBox)
        {
            var (min, max) = renderer.Box;
            if (max.X > min.X)
            {
                var c = (min + max) * 0.5f;
                // The centre square goes first: at the centre it wins a pick tie over anything laid over it.
                list.Add(new Handle(BoxCentre, c, HandleAxis.GroundPlane));
                list.Add(new Handle(BoxXMin, new Vector3(min.X, c.Y, c.Z), HandleAxis.X));
                list.Add(new Handle(BoxXMax, new Vector3(max.X, c.Y, c.Z), HandleAxis.X));
                list.Add(new Handle(BoxYMin, new Vector3(c.X, min.Y, c.Z), HandleAxis.Y));
                list.Add(new Handle(BoxYMax, new Vector3(c.X, max.Y, c.Z), HandleAxis.Y));
                list.Add(new Handle(BoxZMin, new Vector3(c.X, c.Y, min.Z), HandleAxis.Z));
                list.Add(new Handle(BoxZMax, new Vector3(c.X, c.Y, max.Z), HandleAxis.Z));
            }
        }

        if (scene != null && renderer.SelectedDevice >= 0)
            foreach (var d in scene.DevicePicks)
                if (d.Index == renderer.SelectedDevice) { AddDevice(list, d); break; }

        return list.Count == 0 ? None : list.ToArray();
    }

    /// <summary>The selected device's gizmo, built from the same mount rules the body drag follows: a square
    /// in the mount plane, an arrow per free in-plane axis (a split AC keeps its height, so it gets one), and a
    /// yaw ring only for the devices R can turn.</summary>
    static void AddDevice(List<Handle> list, DevicePick d)
    {
        var n = d.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(d.Normal) : Vector3.UnitZ;
        bool wall = MathF.Abs(n.Z) < 0.5f;
        var u = wall ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n)) : Vector3.UnitX;
        var v = wall ? Vector3.UnitZ : Vector3.UnitY;
        list.Add(new Handle(DevMove, d.Center, HandleAxis.Plane, n, DeviceSquareHalf));
        list.Add(new Handle(DevU, d.Center, HandleAxis.Along, u, DeviceArrowHalf, Gap: DeviceGap));
        if (!wall || d.FreeHeight) list.Add(new Handle(DevV, d.Center, HandleAxis.Along, v, DeviceArrowHalf, Gap: DeviceGap));
        if (d.Rotatable)
            list.Add(new Handle(DevYaw, d.Center, HandleAxis.Rotate, Vector3.UnitZ, DeviceRing,
                Knob: MathF.PI * 0.5f - d.Yaw * MathF.PI / 180f));   // compass yaw (0 = +Y, clockwise) -> ring angle from +X
    }

    /// <summary>Where a handle lands on screen. False when it is behind the camera. The renderer draws exactly
    /// this and <see cref="Pick"/> tests exactly this.</summary>
    public static bool Layout(Renderer renderer, in Handle h, out GizmoLayout layout)
    {
        layout = default;
        if (!renderer.Project(h.Position, out var centre)) return false;
        var dir = h.Direction;
        dir = dir.LengthSquared() > 1e-9f ? Vector3.Normalize(dir) : Vector3.UnitZ;
        float size = h.Size > 0 ? h.Size : h.Shape switch
        {
            HandleShape.Ring => RingRadius,
            HandleShape.Square => SquareHalf,
            _ => ArrowHalf,
        };

        if (h.Shape == HandleShape.Arrow)
        {
            if (!renderer.Project(h.Position + dir * ProbeStep, out var ahead)) return false;
            var d = ahead - centre;
            float len = d.Length();
            // An axis seen nearly end-on cannot be dragged with any precision (Blender hides it); the arrow
            // shrinks to its anchor dot instead of pointing at nothing.
            float unit = UnitPixels(renderer, h.Position);
            float fore = unit > 1e-3f ? Math.Clamp(len / unit, 0f, 1f) : 1f;
            float fade = SmoothStep(0.12f, 0.40f, fore);
            layout = new GizmoLayout(centre, len > 1e-3f ? d / len * (size * fade) : Vector2.Zero, Vector2.Zero);
            return true;
        }

        // Ring and square live in the plane perpendicular to dir; project its basis and scale the larger
        // projected axis to the wanted pixel size, so a ring seen from low down is a flat ellipse, as it should be.
        var (t, b) = Basis(dir);
        if (!renderer.Project(h.Position + t * ProbeStep, out var pt) ||
            !renderer.Project(h.Position + b * ProbeStep, out var pb)) return false;
        var tp = pt - centre;
        var bp = pb - centre;
        float m = MathF.Max(tp.Length(), bp.Length());
        if (m < 1e-3f) return false;
        float k = size / m;
        layout = new GizmoLayout(centre, tp * k, bp * k);
        return true;
    }

    /// <summary>Pixels per <see cref="ProbeStep"/> for a step perpendicular to the view at this depth: the
    /// yardstick an axis's projected length is measured against.</summary>
    static float UnitPixels(Renderer renderer, Vector3 p)
    {
        Vector3 eye;
        lock (renderer.Camera.Sync) eye = renderer.Camera.Eye;
        var view = p - eye;
        if (view.LengthSquared() < 1e-9f) return 0f;
        view = Vector3.Normalize(view);
        var q = Vector3.Cross(view, MathF.Abs(view.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        if (q.LengthSquared() < 1e-9f) return 0f;
        q = Vector3.Normalize(q);
        return renderer.Project(p, out var a) && renderer.Project(p + q * ProbeStep, out var bb) ? Vector2.Distance(a, bb) : 0f;
    }

    /// <summary>Basis of the plane perpendicular to <paramref name="n"/>; for a vertical normal it is X, Y so
    /// a floor square sits square to the building.</summary>
    static (Vector3 T, Vector3 B) Basis(Vector3 n)
    {
        var up = MathF.Abs(n.Z) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var t = Vector3.Normalize(Vector3.Cross(up, n));
        return (t, Vector3.Cross(n, t));
    }

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Distance in pixels from the cursor to the drawn shape (0 inside a square).</summary>
    public static float Distance(in GizmoLayout lay, in Handle h, Vector2 p)
    {
        switch (h.Shape)
        {
            case HandleShape.Arrow:
            {
                float len = lay.U.Length();
                if (h.Gap <= 0 || len <= h.Gap) return SegmentDistance(p, lay.Centre - lay.U, lay.Centre + lay.U);
                var g = lay.U * (h.Gap / len);          // the empty middle is not the arrow
                return MathF.Min(SegmentDistance(p, lay.Centre + g, lay.Centre + lay.U),
                                 SegmentDistance(p, lay.Centre - g, lay.Centre - lay.U));
            }
            case HandleShape.Ring:
            {
                float best = float.MaxValue;
                var prev = lay.Centre + lay.U;
                for (int i = 1; i <= RingSegments; i++)
                {
                    float a = i * MathF.Tau / RingSegments;
                    var next = lay.Centre + lay.U * MathF.Cos(a) + lay.V * MathF.Sin(a);
                    best = MathF.Min(best, SegmentDistance(p, prev, next));
                    prev = next;
                }
                return best;
            }
            default:
            {
                var r = p - lay.Centre;
                float det = lay.U.X * lay.V.Y - lay.U.Y * lay.V.X;
                if (MathF.Abs(det) > 1e-3f)
                {
                    float alpha = (r.X * lay.V.Y - r.Y * lay.V.X) / det;
                    float beta = (lay.U.X * r.Y - lay.U.Y * r.X) / det;
                    if (MathF.Abs(alpha) <= 1f && MathF.Abs(beta) <= 1f) return 0f;
                }
                Vector2 c0 = lay.Centre + lay.U + lay.V, c1 = lay.Centre - lay.U + lay.V,
                        c2 = lay.Centre - lay.U - lay.V, c3 = lay.Centre + lay.U - lay.V;
                return MathF.Min(MathF.Min(SegmentDistance(p, c0, c1), SegmentDistance(p, c1, c2)),
                                 MathF.Min(SegmentDistance(p, c2, c3), SegmentDistance(p, c3, c0)));
            }
        }
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Vertices the gizmo shader emits for a shape (see VSGizmo).</summary>
    public static int VertexCount(HandleShape shape) => shape switch
    {
        HandleShape.Ring => RingSegments * 6 + 24,
        HandleShape.Square => 30,
        _ => 42,
    };

    /// <summary>The handle whose drawn shape is under the cursor (nearest when two overlap), or -1.</summary>
    public static int Pick(Renderer renderer, int px, int py)
    {
        var handles = Build(renderer);
        if (handles.Length == 0) return -1;
        int best = -1;
        float bestDistance = PickSlack;
        var cursor = new Vector2(px, py);
        foreach (var handle in handles)
        {
            if (!Layout(renderer, handle, out var lay)) continue;
            float d = Distance(lay, handle, cursor);
            if (d < bestDistance) { bestDistance = d; best = handle.Id; }
        }
        return best;
    }

    public static bool TryFind(Renderer renderer, int id, out Handle handle)
    {
        foreach (var h in Build(renderer))
            if (h.Id == id) { handle = h; return true; }
        handle = default;
        return false;
    }

    /// <summary>Point on the axis through <paramref name="origin"/> closest to the cursor ray: a constrained drag.</summary>
    public static bool AxisDrag(Renderer renderer, int px, int py, Vector3 origin, Vector3 axis, out float coordinate)
    {
        coordinate = 0;
        if (!renderer.ScreenRay(px, py, out var eye, out var dir)) return false;
        float ad = Vector3.Dot(axis, dir);
        float denom = 1f - ad * ad;
        if (MathF.Abs(denom) < 1e-5f) return false;              // looking straight down the axis
        var w = eye - origin;
        coordinate = (Vector3.Dot(w, axis) - ad * Vector3.Dot(w, dir)) / denom;
        return true;
    }

    /// <summary>Where the cursor ray meets a horizontal plane, for the box's translate handle.</summary>
    public static bool PlaneDrag(Renderer renderer, int px, int py, float planeZ, out Vector3 point)
    {
        point = default;
        if (!renderer.ScreenRay(px, py, out var eye, out var dir) || MathF.Abs(dir.Z) < 1e-6f) return false;
        float t = (planeZ - eye.Z) / dir.Z;
        if (t <= 0) return false;
        point = eye + dir * t;
        return true;
    }

    /// <summary>Compass bearing (degrees, 0 = +Y, clockwise) of the cursor about <paramref name="pivot"/> on
    /// the horizontal plane through it; the same convention as <c>Devices.Yaw</c>. Null when the ray misses the
    /// plane or lands on the pivot.</summary>
    public static double? Bearing(Renderer renderer, int px, int py, Vector3 pivot)
    {
        if (!PlaneDrag(renderer, px, py, pivot.Z, out var point)) return null;
        var toward = new Vector2(point.X - pivot.X, point.Y - pivot.Y);
        if (toward.LengthSquared() < 0.01f) return null;
        return Math.Atan2(toward.X, toward.Y) * 180.0 / Math.PI;
    }
}
