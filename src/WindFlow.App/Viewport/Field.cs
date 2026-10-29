using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App.Viewport;

/// <summary>A solver velocity field prepared for the GPU, plus the places worth spawning particles.
/// Built off the render thread; the render thread only uploads it.
/// Velocity: texel (i,j,k) centred at Origin + (i+0.5)*Dx, xyz = m/s, w = air fraction of the source cells.
/// Occupancy: a separate full-resolution volume (1 = air, 0 = solid) so one-cell interior partitions still
/// block particles even though the velocity volume is downsampled past them.</summary>
public sealed class FieldVolume
{
    public const int MaxSide = 160;         // velocity volume, per the phase-1 budget
    public const int MaxOccupancySide = 320;

    public int Nx, Ny, Nz;
    public float Dx;
    public Vector3 Origin;
    public Half[] Texels = [];              // 4 per texel, RGBA16F

    public int Ox, Oy, Oz;
    public float ODx;
    public byte[] Occupancy = [];           // R8_UNorm, 255 = air
    public ulong OccupancyHash;

    public float SpeedMax = 1;              // 98th percentile of indoor speed, the colour-scale top

    /// <summary>Spawn candidates in three contiguous pools: room air, inflow at open openings, outdoor.
    /// w carries the pool index.</summary>
    public Vector4[] Spawn = [];
    public int SpawnRooms, SpawnInflow, SpawnOutside;
    public bool SpawnReused;                // occupancy unchanged: the GPU can keep the table it has

    public Vector3 BoxMin, BoxMax;          // the part of the domain worth drawing: building plus a margin
    public Vector3 HouseMin, HouseMax;      // the building envelope: inside it particles are drawn bright
    public bool[] RoomCells = [];           // per source cell: air inside an analysed room (kept for reuse)
    /// <summary>Velocity texels whose centre lies inside an analysed room, and that room's index in
    /// BuildingModel.Rooms. The still-air cloud and the rooms' still % are both read off these, so the
    /// picture and the panel count exactly the same cells.</summary>
    public int[] IndoorTexels = [];
    public short[] IndoorRoom = [];

    public Vector3 Size => new(Nx * Dx, Ny * Dx, Nz * Dx);
    public Vector3 OccSize => new(Ox * ODx, Oy * ODx, Oz * ODx);

    /// <summary>Batch path: one field out of a finished solve.</summary>
    public static FieldVolume Build(SimulationResult result, FieldVolume? previous = null)
    {
        var f = result.Field;
        var v = Build(f.Grid, result.Model, f.Ux, f.Uy, f.Uz, null, null, true);
        if (previous != null && previous.OccupancyHash == v.OccupancyHash) v.SpawnReused = true;
        return v;
    }

    /// <summary>Live path. <paramref name="reuse"/> supplies the geometry work (occupancy, room mask, spawn
    /// table, boxes) for an unchanged grid, and <paramref name="texels"/> recycles the velocity buffer, so a
    /// 10 Hz republish costs one downsample pass and nothing on the Large Object Heap.
    /// <paramref name="rescale"/> re-derives the colour scale, which is worth doing only occasionally.</summary>
    public static FieldVolume Build(VoxelGrid g, BuildingModel model, float[] ux, float[] uy, float[] uz,
        FieldVolume? reuse, Half[]? texels, bool rescale, bool snap = false)
    {
        int stride = Math.Max(1, (int)MathF.Ceiling(Math.Max(g.Nx, Math.Max(g.Ny, g.Nz)) / (float)MaxSide));
        var v = new FieldVolume
        {
            Nx = Math.Max(1, (g.Nx + stride - 1) / stride),
            Ny = Math.Max(1, (g.Ny + stride - 1) / stride),
            Nz = Math.Max(1, (g.Nz + stride - 1) / stride),
            Dx = g.Dx * stride,
            Origin = g.Origin,
        };
        int need = v.Nx * v.Ny * v.Nz * 4;
        bool sameShape = reuse != null && reuse.Nx == v.Nx && reuse.Ny == v.Ny && reuse.Nz == v.Nz &&
                         reuse.RoomCells.Length == g.Count;
        v.Texels = texels != null && texels.Length == need ? texels : new Half[need];

        var indoor = sameShape ? reuse!.RoomCells : RoomMask(model, g);
        v.RoomCells = indoor;
        var indoorSpeeds = rescale || !sameShape ? new List<float>(4096) : null;

        for (int k = 0; k < v.Nz; k++)
        for (int j = 0; j < v.Ny; j++)
        for (int i = 0; i < v.Nx; i++)
        {
            Vector3 sum = default;
            int air = 0, total = 0;
            for (int z = k * stride; z < Math.Min((k + 1) * stride, g.Nz); z++)
            for (int y = j * stride; y < Math.Min((j + 1) * stride, g.Ny); y++)
            for (int x = i * stride; x < Math.Min((i + 1) * stride, g.Nx); x++)
            {
                int idx = g.Index(x, y, z);
                total++;
                if (g.IsSolid(idx)) continue;
                air++;
                var cell = new Vector3(ux[idx], uy[idx], uz[idx]);
                sum += cell;
                if (indoorSpeeds != null && indoor[idx]) indoorSpeeds.Add(cell.Length());
            }

            var mean = air > 0 ? sum / air : Vector3.Zero;
            int o = ((k * v.Ny + j) * v.Nx + i) * 4;
            v.Texels[o] = (Half)mean.X;
            v.Texels[o + 1] = (Half)mean.Y;
            v.Texels[o + 2] = (Half)mean.Z;
            v.Texels[o + 3] = (Half)(total > 0 ? air / (float)total : 0f);
        }

        if (indoorSpeeds != null)
        {
            // Ease the scale rather than snapping it: a live run starts from rest and the p98 climbs.
            float p98 = Percentile(indoorSpeeds, 0.98f);
            v.SpeedMax = sameShape && !snap ? reuse!.SpeedMax + (p98 - reuse.SpeedMax) * 0.35f : p98;
        }
        else v.SpeedMax = reuse!.SpeedMax;

        if (sameShape && reuse!.Spawn.Length > 0)
        {
            v.Ox = reuse.Ox; v.Oy = reuse.Oy; v.Oz = reuse.Oz; v.ODx = reuse.ODx;
            v.Occupancy = reuse.Occupancy;
            v.OccupancyHash = reuse.OccupancyHash;
            v.BoxMin = reuse.BoxMin; v.BoxMax = reuse.BoxMax;
            v.HouseMin = reuse.HouseMin; v.HouseMax = reuse.HouseMax;
            v.Spawn = reuse.Spawn;
            v.SpawnRooms = reuse.SpawnRooms;
            v.SpawnInflow = reuse.SpawnInflow;
            v.SpawnOutside = reuse.SpawnOutside;
            v.IndoorTexels = reuse.IndoorTexels;
            v.IndoorRoom = reuse.IndoorRoom;
            v.SpawnReused = true;
            return v;
        }

        BuildOccupancy(v, g);
        FocusBox(model, v);
        BuildIndoorTexels(model, v);
        BuildSpawn(model, v, indoor, g, ux, uy, uz);
        return v;
    }

    /// <summary>A copy with the velocities perturbed, for the --fakelive hitch test. Occupancy, spawn table
    /// and boxes are shared by reference, exactly as a live republish from the solver would be.
    /// <paramref name="reuse"/> recycles a previously published volume's texel array: allocating a fresh one
    /// per update puts megabytes on the Large Object Heap ten times a second and the resulting collections
    /// stall the render thread. A live publisher must rotate a small pool the same way.</summary>
    public FieldVolume Perturb(float time, FieldVolume? reuse = null)
    {
        var copy = (FieldVolume)MemberwiseClone();
        copy.Texels = reuse != null && reuse.Texels.Length == Texels.Length ? reuse.Texels : new Half[Texels.Length];
        float swirl = MathF.Sin(time * 2.1f) * 0.35f;
        float gust = 1f + 0.22f * MathF.Sin(time * 1.3f);
        for (int i = 0; i < Texels.Length; i += 4)
        {
            float x = (float)Texels[i], y = (float)Texels[i + 1];
            copy.Texels[i] = (Half)((x * MathF.Cos(swirl) - y * MathF.Sin(swirl)) * gust);
            copy.Texels[i + 1] = (Half)((x * MathF.Sin(swirl) + y * MathF.Cos(swirl)) * gust);
            copy.Texels[i + 2] = (Half)((float)Texels[i + 2] * gust);
            copy.Texels[i + 3] = Texels[i + 3];
        }
        copy.SpawnReused = true;
        return copy;
    }

    /// <summary>Averages one per-cell scalar (pressure, temperature) onto this volume's texels, air cells
    /// only, reusing <paramref name="into"/>. Returns the 98th percentile of |value| for the colour scale.</summary>
    public float DownsampleScalar(VoxelGrid g, float[] values, Half[] into, out float p98)
    {
        int stride = Math.Max(1, (int)MathF.Ceiling(Math.Max(g.Nx, Math.Max(g.Ny, g.Nz)) / (float)MaxSide));
        var magnitudes = new List<float>(4096);
        for (int k = 0; k < Nz; k++)
        for (int j = 0; j < Ny; j++)
        for (int i = 0; i < Nx; i++)
        {
            double sum = 0;
            int air = 0;
            for (int z = k * stride; z < Math.Min((k + 1) * stride, g.Nz); z++)
            for (int y = j * stride; y < Math.Min((j + 1) * stride, g.Ny); y++)
            for (int x = i * stride; x < Math.Min((i + 1) * stride, g.Nx); x++)
            {
                int idx = g.Index(x, y, z);
                if (g.IsSolid(idx) || idx >= values.Length) continue;
                sum += values[idx];
                air++;
            }
            float mean = air > 0 ? (float)(sum / air) : 0f;
            into[(k * Ny + j) * Nx + i] = (Half)mean;
            if (air > 0) magnitudes.Add(MathF.Abs(mean));
        }
        p98 = Percentile(magnitudes, 0.98f);
        return p98;
    }

    static void BuildOccupancy(FieldVolume v, VoxelGrid g)
    {
        int stride = Math.Max(1, (int)MathF.Ceiling(Math.Max(g.Nx, Math.Max(g.Ny, g.Nz)) / (float)MaxOccupancySide));
        v.Ox = Math.Max(1, (g.Nx + stride - 1) / stride);
        v.Oy = Math.Max(1, (g.Ny + stride - 1) / stride);
        v.Oz = Math.Max(1, (g.Nz + stride - 1) / stride);
        v.ODx = g.Dx * stride;
        v.Occupancy = new byte[v.Ox * v.Oy * v.Oz];

        ulong hash = 14695981039346656037UL;
        for (int k = 0; k < v.Oz; k++)
        for (int j = 0; j < v.Oy; j++)
        for (int i = 0; i < v.Ox; i++)
        {
            bool solid = false;      // any solid source cell wins, so thin walls survive a downsample
            for (int z = k * stride; z < Math.Min((k + 1) * stride, g.Nz) && !solid; z++)
            for (int y = j * stride; y < Math.Min((j + 1) * stride, g.Ny) && !solid; y++)
            for (int x = i * stride; x < Math.Min((i + 1) * stride, g.Nx) && !solid; x++)
                solid = g.IsSolid(g.Index(x, y, z));

            byte value = solid ? (byte)0 : (byte)255;
            v.Occupancy[(k * v.Oy + j) * v.Ox + i] = value;
            hash = (hash ^ value) * 1099511628211UL;
        }
        v.OccupancyHash = hash;
    }

    /// <summary>The solver domain is a wind tunnel many times the building; drawing particles across all of it
    /// buries the interior. Keep the building plus a margin, and clamp to the domain.</summary>
    static void FocusBox(BuildingModel model, FieldVolume v)
    {
        var (min, max) = HouseBounds(model) ?? (v.Origin, v.Origin + v.Size);
        v.HouseMin = min;
        v.HouseMax = max;
        (v.BoxMin, v.BoxMax) = WithMargin(min, max, v.Origin, v.Origin + v.Size);
    }

    /// <summary>The region <see cref="FocusBox"/> keeps (building plus margin, clamped to the domain), for a
    /// caller that has to decide what to resample before any volume exists (the nested composite).</summary>
    public static (Vector3 Min, Vector3 Max) FocusRegion(BuildingModel model, Vector3 domainMin, Vector3 domainMax)
    {
        var (min, max) = HouseBounds(model) ?? (domainMin, domainMax);
        return WithMargin(min, max, domainMin, domainMax);
    }

    /// <summary>The building envelope from the obstacle meshes, the ground slab left out; null without any.</summary>
    static (Vector3 Min, Vector3 Max)? HouseBounds(BuildingModel model)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in model.Obstacles)
        {
            float top = float.MinValue;
            foreach (var p in mesh.Vertices) top = MathF.Max(top, p.Z);
            if (top <= model.GroundZ + 0.05f) continue;         // the ground slab is not part of the envelope
            foreach (var p in mesh.Vertices) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        return min.X > max.X ? null : (min, max);
    }

    static (Vector3 Min, Vector3 Max) WithMargin(Vector3 min, Vector3 max, Vector3 domainMin, Vector3 domainMax)
    {
        var size = max - min;
        var margin = new Vector3(MathF.Max(2.5f, size.X * 0.22f), MathF.Max(2.5f, size.Y * 0.22f), MathF.Max(2f, size.Z * 0.18f));
        return (Vector3.Max(min - margin, domainMin), Vector3.Min(max + margin, domainMax));
    }

    /// <summary>Which velocity texels belong to which room: texel centre inside the room loops and between its
    /// floor and ceiling, on an analysed storey (the same rule as <see cref="RoomMask"/>).</summary>
    static void BuildIndoorTexels(BuildingModel model, FieldVolume v)
    {
        var texels = new List<int>(8192);
        var rooms = new List<short>(8192);
        bool anyAnalysed = model.Storeys.Any(s => s.Analysed);
        for (int r = 0; r < model.Rooms.Count && r < short.MaxValue; r++)
        {
            var room = model.Rooms[r];
            if (room.Loops.Count == 0) continue;
            if (anyAnalysed && !string.IsNullOrEmpty(room.Level) && model.StoreyOf(room) is not { Analysed: true }) continue;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var loop in room.Loops)
                foreach (var p in loop)
                {
                    minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                    minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                }
            int x0 = Clamp((minX - v.Origin.X) / v.Dx, v.Nx), x1 = Clamp((maxX - v.Origin.X) / v.Dx, v.Nx);
            int y0 = Clamp((minY - v.Origin.Y) / v.Dx, v.Ny), y1 = Clamp((maxY - v.Origin.Y) / v.Dx, v.Ny);
            int z0 = Clamp((room.ZMin - v.Origin.Z) / v.Dx, v.Nz), z1 = Clamp((room.ZMax - v.Origin.Z) / v.Dx, v.Nz);
            for (int z = z0; z <= z1; z++)
            {
                float cz = v.Origin.Z + (z + 0.5f) * v.Dx;
                if (cz < room.ZMin || cz > room.ZMax) continue;
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = new Vector2(v.Origin.X + (x + 0.5f) * v.Dx, v.Origin.Y + (y + 0.5f) * v.Dx);
                    if (!InsideLoops(room.Loops, c)) continue;
                    texels.Add((z * v.Ny + y) * v.Nx + x);
                    rooms.Add((short)r);
                }
            }
        }
        v.IndoorTexels = [.. texels];
        v.IndoorRoom = [.. rooms];
    }

    /// <summary>Texels with less air than this are mostly wall: their mean speed describes a sliver of air
    /// beside a solid, so neither the cloud nor the room percentages count them.</summary>
    public const float StillMinAir = 0.5f;

    /// <summary>Per source cell: is this air inside an analysed room? Drives both the colour scale and where
    /// particles are seeded — the product is about indoor air, not the outdoor free stream.</summary>
    internal static bool[] RoomMask(BuildingModel model, VoxelGrid g)
    {
        var mask = new bool[g.Count];
        bool anyAnalysed = model.Storeys.Any(s => s.Analysed);
        // IMP-B (OF-1): each room's cells are the ones its ACH uses, outline plus band (Analysis.RoomMasks), not the bare outline.
        var masks = model.Rooms.Count > 0 ? Analysis.RoomMasks(g, model, out _) : [];
        for (int r = 0; r < model.Rooms.Count; r++)
        {
            var room = model.Rooms[r];
            if (room.Loops.Count == 0) continue;
            if (anyAnalysed && !string.IsNullOrEmpty(room.Level) && model.StoreyOf(room) is not { Analysed: true }) continue;
            for (int idx = 0; idx < mask.Length; idx++)
                if (masks[r][idx] && !g.IsSolid(idx)) mask[idx] = true;
        }
        return mask;
    }

    static int Clamp(float v, int n) => Math.Clamp((int)MathF.Floor(v), 0, n - 1);

    // Even-odd fill, matching Room.Loops' documented rule.
    static bool InsideLoops(List<Vector2[]> loops, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                if (loop[i].Y > p.Y != loop[j].Y > p.Y &&
                    p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                    inside = !inside;
        return inside;
    }

    static float Percentile(List<float> values, float q)
    {
        if (values.Count == 0) return 1f;
        values.Sort();
        float v = values[Math.Clamp((int)(values.Count * q), 0, values.Count - 1)];
        return MathF.Max(v, 0.15f);
    }

    /// <summary>Three contiguous pools, sampled uniformly on the GPU so the shares below are the shares seen.
    /// Fixed shares, not per-texel weights: the outdoor air is a hundred times the volume of the rooms and
    /// would swamp any weighting.</summary>
    static void BuildSpawn(BuildingModel model, FieldVolume v, bool[] indoorCells, VoxelGrid g,
        float[] ux, float[] uy, float[] uz)
    {
        const int Target = 1 << 16;
        var open = model.Openings.Where(o => o.IsOpen).ToArray();
        var rooms = new List<Vector4>();
        var inflow = new List<Vector4>();
        var mouthsAny = new List<Vector4>();
        var outside = new List<Vector4>();
        var houseCentre = (v.HouseMin + v.HouseMax) * 0.5f;

        for (int k = 0; k < v.Nz; k++)
        for (int j = 0; j < v.Ny; j++)
        for (int i = 0; i < v.Nx; i++)
        {
            int o = ((k * v.Ny + j) * v.Nx + i) * 4;
            if ((float)v.Texels[o + 3] < 0.85f) continue;
            var p = v.Origin + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) * v.Dx;
            if (p.X < v.BoxMin.X || p.Y < v.BoxMin.Y || p.Z < v.BoxMin.Z ||
                p.X > v.BoxMax.X || p.Y > v.BoxMax.Y || p.Z > v.BoxMax.Z) continue;

            int srcIdx = g.Index(Clamp((p.X - g.Origin.X) / g.Dx, g.Nx),
                                 Clamp((p.Y - g.Origin.Y) / g.Dx, g.Ny),
                                 Clamp((p.Z - g.Origin.Z) / g.Dx, g.Nz));

            float near = float.MaxValue;
            foreach (var op in open) near = MathF.Min(near, (op.Center - p).Length());

            if (near < 1.8f)
            {
                mouthsAny.Add(new Vector4(p, 1));
                var u = new Vector3(ux[srcIdx], uy[srcIdx], uz[srcIdx]);
                // Inflow: air at the mouth heading into the building. Seeding fresh air here is what makes
                // the jets start at the façade.
                if (Vector3.Dot(u, houseCentre - p) > 0.02f) inflow.Add(new Vector4(p, 1));
            }
            else if (indoorCells[srcIdx]) rooms.Add(new Vector4(p, 0));
            else outside.Add(new Vector4(p, 2));
        }
        if (inflow.Count == 0) inflow = mouthsAny;

        // Device outlets. Core hands us the exact driven cells, so a fan's plume is seeded at the blades and
        // a split unit's jet at the louvre: without this the plume only appears once ordinary room parcels
        // happen to drift through the disc, which takes several seconds of wall clock.
        var devicePoints = new List<Vector4>();
        foreach (var region in g.Devices)
            for (int c = 0; c < region.OutletCount && c < region.Cells.Length; c++)
            {
                int idx = region.Cells[c];
                int z = idx / (g.Nx * g.Ny), rem = idx - z * g.Nx * g.Ny;
                var p = g.CellCenter(rem % g.Nx, rem / g.Nx, z);
                if (p.X < v.BoxMin.X || p.Y < v.BoxMin.Y || p.Z < v.BoxMin.Z ||
                    p.X > v.BoxMax.X || p.Y > v.BoxMax.Y || p.Z > v.BoxMax.Z) continue;
                devicePoints.Add(new Vector4(p, 0));
            }

        var table = new List<Vector4>(Target);
        var rng = new Random(7);
        // Outlets come out of the room share (they ARE room air), so the indoor/outdoor balance is unchanged.
        int deviceBudget = devicePoints.Count > 0 ? (int)(Target * 0.06f) : 0;
        Fill(table, rooms, (int)(Target * 0.70f) - deviceBudget, rng);
        Fill(table, devicePoints, deviceBudget, rng);
        v.SpawnRooms = table.Count;
        Fill(table, inflow, (int)(Target * 0.25f), rng);
        v.SpawnInflow = table.Count - v.SpawnRooms;
        Fill(table, outside, Target - table.Count, rng);
        v.SpawnOutside = table.Count - v.SpawnRooms - v.SpawnInflow;
        if (table.Count == 0)
        {
            table.Add(new Vector4((v.BoxMin + v.BoxMax) * 0.5f, 0));
            v.SpawnRooms = 1;
        }
        v.Spawn = table.ToArray();
    }

    static void Fill(List<Vector4> table, List<Vector4> pool, int count, Random rng)
    {
        if (pool.Count == 0 || count <= 0) return;
        for (int i = 0; i < count; i++) table.Add(pool[rng.Next(pool.Count)]);
    }
}
