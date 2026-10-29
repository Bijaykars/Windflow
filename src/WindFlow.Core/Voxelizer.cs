using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;

namespace WindFlow.Core;

public static partial class Voxelizer
{
    /// <param name="maxDegreeOfParallelism">RunOptions.MaxDegreeOfParallelism: caps the obstacle
    /// rasterization's worker count. null = the framework default (every core).</param>
    /// <param name="domain">RunOptions.Domain: solve inside this box instead of the whole building plus its
    /// wind-tunnel margin. Ignored when it is smaller than a metre on any axis.</param>
    /// <param name="sealUnreachable">true (the default) runs the enclosure seal: every air cell the outside
    /// cannot reach, and no Room loop seeds, becomes Solid (wall cavities, shut rooms, unanalysed storeys),
    /// and devices/Fixed patches in it are dropped. <b>The solver must be given the sealed grid</b>: an
    /// unreachable air pocket has nothing to drive it, and a Room-less enclosed interior would otherwise sit
    /// in the solve as dead air. false skips the seal and returns the geometry exactly as rasterised, which
    /// is what room DETECTION on a raw mesh needs (no Room loops yet, so sealing would erase the very
    /// interiors being looked for); build the solve grid again, sealed, once the rooms exist.</param>
    /// <param name="minDx">RunOptions.MinDx: lowers the 0.1 m cell-size floor so a smaller RunOptions.Domain
    /// box can resolve finer. Never raises the floor -- callers that omit it keep the default 0.1 m.</param>
    /// <param name="mode">RunOptions.DomainMode (6c): Full sizes the automatic domain by <see cref="PlanFull"/> for <paramref name="wind"/>
    /// (at Compact's Dx, within <paramref name="maxCells"/>, 0 = no limit); ignored with a domain box. Full with no wind is Compact.</param>
    public static VoxelGrid Build(BuildingModel model, int targetCells, int? maxDegreeOfParallelism = null,
        DomainBox? domain = null, bool sealUnreachable = true, float minDx = 0.1f,
        DomainMode mode = DomainMode.Compact, Vector2 wind = default, long maxCells = 0, float fixedAutoDx = 0f)
    {
        if (domain is { } box && box.IsValid) return BuildDomain(model, targetCells, maxDegreeOfParallelism, box, sealUnreachable, minDx: minDx);

        var grid = mode == DomainMode.Full && wind.Length() >= 1e-3f ? FullGrid(model, targetCells, minDx, wind, maxCells, fixedAutoDx) : AutoGrid(model, targetCells, minDx, fixedAutoDx);
        // 6a: the cell-centre (LBM) mask, its cut links, then the conservative Cell mask every placement and analysis reads.
        FillObstacleVolumes(grid, model.Obstacles.Where(m => !m.Furniture).ToList());
        var carved = new bool[grid.Count];
        var through = ApplyOpenings(grid, model.Openings, carved);
        FillObstacleVolumes(grid, model.Obstacles.Where(m => m.Furniture).ToList());   // RX-05: furniture after the carve, so the carve-through never clears it
        GroundAndLid(grid);
        ComputeWallLinks(grid, model.Obstacles, model.Openings, maxDegreeOfParallelism, through, groundLayer: true);
        ConservativeMask(grid, carved);
        var fixedOwner = ApplySources(grid, model.Sources);
        ApplyDevices(grid, model.Sources);
        if (sealUnreachable) { FloodFill(grid, fixedOwner, model.Rooms, sources: model.Sources); NoteCellIsolatedRooms(grid, model); }
        return grid;
    }

    /// <summary>DETECTION ONLY (6a addendum): the unsealed whole-building grid import-time room and opening detection reads
    /// (RoomDetect, OpeningDetect), built exactly as before batch 6a: every cell a surface triangle touches is Solid
    /// (<see cref="VoxelizeObstaclesForDetection"/>), closed volumes filled, openings carved, no cut links, no seal. Detection was
    /// calibrated on that touched-cell mask and behaves the same whatever the mesh quality. NEVER hand this grid to a solver: the
    /// physics masks (the cell-centre mask, cut links and the conservative Cell mask) come only from <see cref="Build"/>.</summary>
    public static VoxelGrid BuildDetection(BuildingModel model, float dx, int? maxDegreeOfParallelism = null)
    {
        var grid = DetectionGrid(model, dx);
        VoxelizeObstaclesForDetection(grid, model.Obstacles, maxDegreeOfParallelism);
        FillObstacleVolumes(grid, model.Obstacles);
        ApplyOpenings(grid, model.Openings, new bool[grid.Count]);
        ApplySources(grid, model.Sources);
        ApplyDevices(grid, model.Sources);
        GroundAndLid(grid);
        return grid;
    }

    /// <summary>IMP-B D1: the detection grid at a FIXED cell size <paramref name="dx"/> over the building's own plan bounds plus <see cref="DetectionMargin"/>
    /// (no wind-tunnel margin: detection reads the building only), so the cell size never depends on a cell budget. Its memory is
    /// Nx*Ny*Nz bytes, with Nz = (building height + margin)/dx: it scales with 1/dx^3 (halving the cell multiplies it by 8).</summary>
    static VoxelGrid DetectionGrid(BuildingModel model, float dx)
    {
        var (min, max) = PlanBounds(model);
        float height = BuildingHeight(model) + DetectionMargin;
        int Nx = (int)MathF.Ceiling((max.X - min.X + 2 * DetectionMargin) / dx);
        int Ny = (int)MathF.Ceiling((max.Y - min.Y + 2 * DetectionMargin) / dx);
        int Nz = (int)MathF.Ceiling(height / dx) + 1;   // layer 0 = ground

        CheckCellCount(Nx, Ny, Nz);
        return new VoxelGrid
        {
            Nx = Nx,
            Ny = Ny,
            Nz = Nz,
            Dx = dx,
            Origin = new Vector3(min.X - DetectionMargin, min.Y - DetectionMargin, model.GroundZ - dx),
            Cell = new byte[Nx * Ny * Nz],
            BuildingSize = new Vector3(max.X - min.X, max.Y - min.Y, BuildingHeight(model)),
        };
    }

    /// <summary>IMP-C: the detection grid's cell count at <paramref name="dx"/> (what <see cref="DetectionGrid"/> allocates); the fine 3D detection costs about 12 bytes a cell at its peak.</summary>
    public static long DetectionCells(BuildingModel model, float dx)
    {
        var (min, max) = PlanBounds(model);
        long nx = (long)MathF.Ceiling((max.X - min.X + 2 * DetectionMargin) / dx), ny = (long)MathF.Ceiling((max.Y - min.Y + 2 * DetectionMargin) / dx);
        return nx * ny * ((long)MathF.Ceiling((BuildingHeight(model) + DetectionMargin) / dx) + 1);
    }

    /// <summary>Air kept around the building (and above it) in the detection grid, metres.</summary>
    public const float DetectionMargin = 1f;

    /// <summary>OF-1 test seam (no product setting): the auto grid's origin moved by this many cells in x and y, to see how a result depends on where the grid falls.
    /// Env <c>WINDFLOW_ORIGIN_SHIFT</c> = "sx,sy" for a separate process; a check sets the field and restores it.</summary>
    internal static Vector2 OriginShiftCells = ParseOriginShift(System.Environment.GetEnvironmentVariable("WINDFLOW_ORIGIN_SHIFT"));

    static Vector2 ParseOriginShift(string? s)
    {
        var p = (s ?? "").Split(',');
        return p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
            && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) ? new Vector2(x, y) : Vector2.Zero;
    }

    /// <summary>The whole-building grid of <see cref="Build"/> without a domain box: plan bounds plus the wind-tunnel margin, nothing marked.</summary>
    static VoxelGrid AutoGrid(BuildingModel model, int targetCells, float minDx, float fixedAutoDx = 0f)
    {
        var (min, max, side, extW, extD, height) = AutoDomain(model);
        float Dx = fixedAutoDx > 0f ? fixedAutoDx : Math.Clamp(MathF.Cbrt(extW * extD * height / targetCells), minDx, 1.0f);   // fixedAutoDx: Ultra resume rebuilds the saved grid exactly

        int Nz = (int)MathF.Ceiling(height / Dx) + 1;   // layer 0 = ground
        int Nx = (int)MathF.Ceiling(extW / Dx);
        int Ny = (int)MathF.Ceiling(extD / Dx);

        CheckCellCount(Nx, Ny, Nz);
        var grid = new VoxelGrid
        {
            Nx = Nx,
            Ny = Ny,
            Nz = Nz,
            Dx = Dx,
            Origin = new Vector3(min.X - side + OriginShiftCells.X * Dx, min.Y - side + OriginShiftCells.Y * Dx, model.GroundZ - Dx),
            Cell = new byte[Nx * Ny * Nz],
            BuildingSize = new Vector3(max.X - min.X, max.Y - min.Y, BuildingHeight(model)),
        };
        return grid;
    }

    /// <summary>Height of the tallest obstacle above the ground, at least 3 m (what <see cref="AutoDomain"/> sizes the sky from).</summary>
    static float BuildingHeight(BuildingModel model)
    {
        float maxObstacleZ = model.GroundZ;
        foreach (var mesh in model.Obstacles)
            foreach (var v in mesh.Vertices)
                maxObstacleZ = MathF.Max(maxObstacleZ, v.Z);
        return MathF.Max(maxObstacleZ - model.GroundZ, 3f);
    }

    /// <summary>The automatic building-plus-wind-tunnel envelope Build uses when no RunOptions.Domain box is given: plan
    /// bounds, side margin, and the extents of the air domain. Dx = cbrt(extW x extD x height / cells), clamped.</summary>
    public static (Vector2 Min, Vector2 Max, float Side, float ExtW, float ExtD, float Height) AutoDomain(BuildingModel model)
    {
        var (min, max) = PlanBounds(model);
        float W = max.X - min.X, D = max.Y - min.Y;

        float Hb = BuildingHeight(model);

        float side = Math.Clamp(MathF.Max(W, D), 8f, 50f);
        float top = Math.Clamp(1.5f * Hb, 6f, 30f);
        float height = Hb + top;
        return (min, max, side, W + 2 * side, D + 2 * side, height);
    }

    /// <summary>6c: the Full wind domain's margins in metres beyond the plan bounds (Top: above the building height), the grid it makes at
    /// <see cref="Dx"/>, its blockage, and how far the VRAM guard stepped it down: 0 = full, 1 = downstream 5 H, 2 = side and top margins
    /// at the 3 % blockage limit, 3 = Compact (the margins are then Compact's). <see cref="Note"/> says so for steps 1-3.</summary>
    public sealed record FullDomainPlan(float West, float East, float South, float North, float Top, float Dx, long Cells, float Blockage, int StepDown, string Note);

    /// <summary>6c (P13b-03): COST 732 / Franke et al. 2007 margins for <paramref name="wind"/>, H = the building height
    /// (<see cref="VoxelGrid.BuildingSize"/>.Z): at least 5 H on inflow faces, 10 H plus the outflow sponge
    /// (<see cref="LbmSolver.WindSpongeCells"/> cells) on outflow faces, 5 H on tangential faces and on the top (faces classified like the
    /// solver's ring, <see cref="LbmSolver.RingClassThreshold"/>); then the side margins (the tangential faces, or the inflow faces of an
    /// oblique wind, which has none) and the top grow together a cell at a time until the blockage (<see cref="Envelope.Blockage(Vector3, float, float, float, Vector2)"/>)
    /// is at most <see cref="Envelope.BlockageLimit"/>. Over <paramref name="maxCells"/> (0 = no limit) it steps down: (1) downstream
    /// 10 H to 5 H; (2) side and top margins from Compact's, grown only to the blockage limit (Dx is never coarsened, so openings keep the
    /// cells they have); (3) Compact.</summary>
    public static FullDomainPlan PlanFull(BuildingModel model, Vector2 wind, float dx, long maxCells)
    {
        var (min, max, side, extW, extD, height) = AutoDomain(model);
        float W = max.X - min.X, D = max.Y - min.Y, H = BuildingHeight(model);
        var building = new Vector3(W, D, H);
        float speed = MathF.Max(wind.Length(), 1e-6f), th = LbmSolver.RingClassThreshold;
        float[] sn = [-wind.X / speed, wind.X / speed, -wind.Y / speed, wind.Y / speed];   // wind . outward normal: west, east, south, north
        bool[] outflow = sn.Select(v => v > th).ToArray();
        bool[] lateral = sn.Select(v => MathF.Abs(v) <= th).ToArray();
        bool lateralIsInflow = !lateral.Any(b => b);
        if (lateralIsInflow) lateral = sn.Select(v => v < -th).ToArray();
        float sponge = LbmSolver.WindSpongeCells * dx;

        long Cells(float[] m, float top) =>
            (long)MathF.Ceiling((W + m[0] + m[1]) / dx) * (long)MathF.Ceiling((D + m[2] + m[3]) / dx) * ((long)MathF.Ceiling((H + top) / dx) + 1);
        float Block(float[] m, float top) => Envelope.Blockage(building, W + m[0] + m[1], D + m[2] + m[3], H + top, wind) ?? 0f;
        float Grow(float[] m, float top, bool sides = true)
        {
            for (int guard = 0; guard < 100_000 && Block(m, top) > Envelope.BlockageLimit; guard++)
            {
                if (sides) for (int f = 0; f < 4; f++) if (lateral[f]) m[f] += dx;
                top += dx;
            }
            return top;
        }
        bool Fits(float[] m, float top) => maxCells <= 0 || Cells(m, top) <= maxCells;
        FullDomainPlan Plan(float[] m, float top, int step, string note) => new(m[0], m[1], m[2], m[3], top, dx, Cells(m, top), Block(m, top), step, note);

        var mm = new float[4];
        for (int f = 0; f < 4; f++) mm[f] = outflow[f] ? 10f * H + sponge : 5f * H;
        float t = Grow(mm, 5f * H);
        long full = Cells(mm, t);
        if (Fits(mm, t)) return Plan(mm, t, 0, "");

        for (int f = 0; f < 4; f++) if (outflow[f]) mm[f] = 5f * H + sponge;
        t = Grow(mm, t);   // RZ-08: for an oblique wind the shorter downstream margins shrink the section; grow back to the limit
        if (Fits(mm, t))
            return Plan(mm, t, 1, $"Full wind domain: downstream margin cut from 10 H to 5 H to fit graphics memory ({full / 1e6:0.#}M cells needed, {maxCells / 1e6:0.#}M allowed).");

        // RZ-08: the side faces start from Compact's margin; an oblique wind's "side" faces are its inflow faces, which stay at max(5 H, side),
        // so then only the top grows to the blockage limit.
        for (int f = 0; f < 4; f++) if (lateral[f]) mm[f] = lateralIsInflow ? MathF.Max(5f * H, side) : side;
        t = Grow(mm, height - H, sides: !lateralIsInflow);
        if (Fits(mm, t))
            return Plan(mm, t, 2, $"Full wind domain: downstream margin 5 H and the side and top margins cut to the {Envelope.BlockageLimit * 100:0} % blockage limit to fit graphics memory ({full / 1e6:0.#}M cells needed, {maxCells / 1e6:0.#}M allowed).");

        var compact = new[] { side, side, side, side };
        return Plan(compact, height - H, 3, $"Full wind domain does not fit graphics memory ({full / 1e6:0.#}M cells needed, {maxCells / 1e6:0.#}M allowed): the Compact domain was used.");
    }

    /// <summary>6c: the whole-building grid of <see cref="Build"/> on the Full domain (<see cref="PlanFull"/>) at Compact's Dx, so Full only
    /// adds cells; Compact itself when the VRAM guard steps all the way down. The plan's note goes into the grid's warnings.</summary>
    static VoxelGrid FullGrid(BuildingModel model, int targetCells, float minDx, Vector2 wind, long maxCells, float fixedAutoDx = 0f)
    {
        var (min, max, _, extW, extD, height) = AutoDomain(model);
        float Dx = fixedAutoDx > 0f ? fixedAutoDx : Math.Clamp(MathF.Cbrt(extW * extD * height / targetCells), minDx, 1.0f);
        var plan = PlanFull(model, wind, Dx, maxCells);
        VoxelGrid grid;
        if (plan.StepDown == 3) grid = AutoGrid(model, targetCells, minDx, fixedAutoDx);
        else
        {
            float H = BuildingHeight(model);
            int Nz = (int)MathF.Ceiling((H + plan.Top) / Dx) + 1;   // layer 0 = ground
            int Nx = (int)MathF.Ceiling((max.X - min.X + plan.West + plan.East) / Dx);
            int Ny = (int)MathF.Ceiling((max.Y - min.Y + plan.South + plan.North) / Dx);
            CheckCellCount(Nx, Ny, Nz);
            grid = new VoxelGrid
            {
                Nx = Nx, Ny = Ny, Nz = Nz, Dx = Dx,
                Origin = new Vector3(min.X - plan.West, min.Y - plan.South, model.GroundZ - Dx),
                Cell = new byte[Nx * Ny * Nz],
                BuildingSize = new Vector3(max.X - min.X, max.Y - min.Y, H),
            };
        }
        if (plan.Note.Length > 0) grid.Warnings.Add(plan.Note);
        return grid;
    }

    /// <summary>RunOptions.Domain: the air domain IS the box. No plan margin, no sky above the roof, so the
    /// whole cell budget goes inside the box and Dx falls as the box shrinks. The bottom is clamped to the
    /// ground when the box reaches it (layer 0 stays the solid ground layer the solver expects); a box
    /// floating above the ground gets an open bottom face instead. Geometry outside the box needs no
    /// filtering: every rasteriser here clamps its index range but tests real coordinates, so an obstacle or
    /// opening outside the box marks nothing.
    /// <para>The faces are left exactly as the geometry made them: air on a face is an open boundary (the
    /// solver classifies it inflow/outflow/free-slip by direction), and a wall the box cuts through stays a
    /// wall. See RunOptions.Domain for what that does and does not mean.</para></summary>
    static VoxelGrid BuildDomain(BuildingModel model, int targetCells, int? maxDop, DomainBox box, bool sealUnreachable = true,
        float fixedDx = 0f, float minDx = 0.1f)
    {
        bool grounded = box.Min.Z <= model.GroundZ + 1e-3f;
        float zLo = grounded ? model.GroundZ : box.Min.Z;
        float w = box.Max.X - box.Min.X, d = box.Max.Y - box.Min.Y;
        float h = MathF.Max(box.Max.Z - zLo, 1f);
        // Same 0.1 m floor as the whole-building path (RunOptions.MinDx can lower it): the lattice scaling
        // (viscosity, dt) is tuned for building scale, so a smaller box buys resolution down to the floor and
        // then stops. A nested fine box (fixedDx > 0) takes exactly half its coarse Dx instead, on a box
        // already snapped to coarse cell faces, so every coarse cell holds exactly 2 x 2 x 2 fine ones.
        bool nested = fixedDx > 0f;
        float Dx = nested ? fixedDx : Math.Clamp(MathF.Cbrt(w * d * h / targetCells), minDx, 1.0f);

        int Nx = Math.Max(6, nested ? (int)MathF.Round(w / Dx) : (int)MathF.Ceiling(w / Dx));
        int Ny = Math.Max(6, nested ? (int)MathF.Round(d / Dx) : (int)MathF.Ceiling(d / Dx));
        int Nz = Math.Max(6, (nested ? (int)MathF.Round(h / Dx) : (int)MathF.Ceiling(h / Dx)) + (grounded ? 1 : 0));

        CheckCellCount(Nx, Ny, Nz);
        var grid = new VoxelGrid
        {
            Nx = Nx,
            Ny = Ny,
            Nz = Nz,
            Dx = Dx,
            Origin = new Vector3(box.Min.X, box.Min.Y, grounded ? model.GroundZ - Dx : box.Min.Z),
            Cell = new byte[Nx * Ny * Nz],
            OpenZMin = !nested && !grounded,
            OpenZMax = !nested,
        };

        FillObstacleVolumes(grid, model.Obstacles.Where(m => !m.Furniture).ToList());
        var carved = new bool[grid.Count];
        var through = ApplyOpenings(grid, model.Openings, carved);
        FillObstacleVolumes(grid, model.Obstacles.Where(m => m.Furniture).ToList());   // RX-05
        if (grounded)
            for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++)
                grid.Cell[grid.Index(x, y, 0)] = VoxelGrid.Solid;
        ComputeWallLinks(grid, model.Obstacles, model.Openings, maxDop, through, groundLayer: grounded);
        ConservativeMask(grid, carved);
        var fixedOwner = ApplySources(grid, model.Sources);
        ApplyDevices(grid, model.Sources);

        CloseBlockedFaces(grid);
        if (sealUnreachable) { FloodFill(grid, fixedOwner, model.Rooms, domainFaces: true, grounded: grounded, sources: model.Sources); NoteCellIsolatedRooms(grid, model); }
        if (nested) return grid;   // its faces are driven by the coarse run, see LbmSolver.StartNested
        grid.Warnings.Add(
            $"Simulation box {w:0.#} x {d:0.#} x {box.Max.Z - zLo:0.#} m at {Dx * 1000:0} mm: its faces are " +
            "ordinary domain boundaries, so flow across a face that cuts through the building is only as " +
            "good as an open face.");
        return grid;
    }

    /// <summary>The fine grid of a one-way nested box (LbmSolver.StartNested): <paramref name="box"/> must
    /// already be snapped to the coarse grid's cell faces, and <paramref name="dx"/> is half the coarse Dx.
    /// Same rasterisation, openings, sources, devices, face closing and seal as a simulation box.</summary>
    internal static VoxelGrid BuildNested(BuildingModel model, DomainBox box, float dx, int? maxDop) =>
        BuildDomain(model, 0, maxDop, box, sealUnreachable: true, fixedDx: dx);

    /// <summary>A domain face is only an open boundary where it is really open air. A face the user drew
    /// through a partition is mostly wall, and the solver would otherwise hand those ring cells an
    /// inflow/outflow/open class and pour air into (or suck it out of) the middle of a solid: the sample
    /// house with its +X face inside the x = 5.5 m partition diverged within one burst. So:
    /// <list type="bullet">
    /// <item>a face with less than <see cref="OpenFaceFraction"/> of its cells in air is CLOSED - every
    /// cell of it becomes solid, i.e. a bounce-back wall, and the box is sealed on that side;</item>
    /// <item>on the faces that stay open, any single ring cell whose inward neighbour is solid becomes
    /// solid too, so a wall poking through an otherwise open face cannot leak.</item>
    /// </list>
    /// Only the simulation-box path needs this: the whole-building domain's ring is open sky and ground by
    /// construction (GroundAndLid).</summary>
    const float OpenFaceFraction = 0.35f;
    const float PartialFaceFraction = 0.6f;    // below this, the face is open but worth a note

    /// <summary>World coordinate of a domain face, for the warning text.</summary>
    static float FaceCoordinate(VoxelGrid grid, int axis, bool maxSide)
    {
        int n = axis == 0 ? grid.Nx : axis == 1 ? grid.Ny : grid.Nz;
        float origin = axis == 0 ? grid.Origin.X : axis == 1 ? grid.Origin.Y : grid.Origin.Z;
        return origin + (maxSide ? n - 0.5f : 0.5f) * grid.Dx;
    }

    static void CloseBlockedFaces(VoxelGrid grid)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        string[] names = { "-X", "+X", "-Y", "+Y", "-Z", "+Z" };
        int closed = 0;

        for (int f = 0; f < 6; f++)
        {
            int axis = f / 2;
            bool maxSide = (f & 1) != 0;
            int inward = maxSide ? -1 : 1;

            int total = 0, air = 0;
            ForEachFaceCell(grid, axis, maxSide, (x, y, z) =>
            {
                total++;
                if (grid.Cell[grid.Index(x, y, z)] != VoxelGrid.Solid) air++;
            });
            if (total == 0) continue;

            float fraction = (float)air / total;
            if (air > 0 && fraction < OpenFaceFraction)
            {
                ForEachFaceCell(grid, axis, maxSide, (x, y, z) => SetSolid(grid, grid.Index(x, y, z)));
                grid.Warnings.Add($"Simulation box face {names[f]} is only {fraction * 100:0}% open air: " +
                    "closed it off as a wall rather than treating a partition as an open boundary");
                closed++;
                continue;
            }
            if (air == 0) { closed++; continue; }   // already solid all over (the ground, typically)

            // Open enough to keep, but partly inside the building: the flow across it is only as good as
            // an open boundary, and the user should be told which face and how much of it.
            if (fraction < PartialFaceFraction)
                grid.Warnings.Add($"Simulation box face {names[f]} at {FaceCoordinate(grid, axis, maxSide):0.##} m is " +
                    $"{fraction * 100:0}% air: it is partly inside the building, so flow across it is only as " +
                    "good as an open boundary");

            ForEachFaceCell(grid, axis, maxSide, (x, y, z) =>
            {
                int idx = grid.Index(x, y, z);
                if (!LbmFluid(grid, idx)) return;
                int ix = x + (axis == 0 ? inward : 0), iy = y + (axis == 1 ? inward : 0), iz = z + (axis == 2 ? inward : 0);
                if (ix < 0 || ix >= Nx || iy < 0 || iy >= Ny || iz < 0 || iz >= Nz) return;
                // 6a: on the LBM's terms (a face cell that is Solid only in Cell is still a boundary cell for the solver): an inward
                // neighbour that is not LBM-fluid, or a cut inward link, leaves the face cell nothing to read its flow from.
                if (Linked(grid, x, y, z, 2 * axis + (inward > 0 ? 1 : 2)) < 0) SetSolid(grid, idx);
            });
        }

        if (closed == 6)
            grid.Warnings.Add("Simulation box: every face is blocked, so the air inside it is sealed - " +
                "only a device, a heat source or an opening inside the box can drive any flow");
    }

    /// <summary>6a: Solid in both masks (Cell and the LBM one, <see cref="VoxelGrid.LbmAir"/>).</summary>
    static void SetSolid(VoxelGrid grid, int i)
    {
        grid.Cell[i] = VoxelGrid.Solid;
        if (grid.LbmAir.Length == grid.Count) grid.LbmAir[i] = false;
    }

    static void ForEachFaceCell(VoxelGrid grid, int axis, bool maxSide, Action<int, int, int> body)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        if (axis == 0)
        {
            int x = maxSide ? Nx - 1 : 0;
            for (int z = 0; z < Nz; z++) for (int y = 0; y < Ny; y++) body(x, y, z);
        }
        else if (axis == 1)
        {
            int y = maxSide ? Ny - 1 : 0;
            for (int z = 0; z < Nz; z++) for (int x = 0; x < Nx; x++) body(x, y, z);
        }
        else
        {
            int z = maxSide ? Nz - 1 : 0;
            for (int y = 0; y < Ny; y++) for (int x = 0; x < Nx; x++) body(x, y, z);
        }
    }

    /// <summary>XY bounds of the whole building's footprint: every obstacle vertex, room loop point
    /// and opening centre, across all storeys.</summary>
    public static (Vector2 Min, Vector2 Max) PlanBounds(BuildingModel model)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;

        void Add(float x, float y)
        {
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
            any = true;
        }

        foreach (var mesh in model.Obstacles)
            foreach (var v in mesh.Vertices) Add(v.X, v.Y);
        foreach (var room in model.Rooms)
            foreach (var loop in room.Loops)
                foreach (var p in loop) Add(p.X, p.Y);
        foreach (var o in model.Openings) Add(o.Center.X, o.Center.Y);

        if (!any) throw new ArgumentException("Nothing to simulate in this building");
        return (new Vector2(minX, minY), new Vector2(maxX, maxY));
    }

    // DETECTION ONLY (6a addendum; restored from batch 4, fixes/snap-b4): conservative triangle/box rasterization for
    // BuildDetection. Do NOT call it from Build/BuildDomain or anything a solver reads: the LBM walls are the cell-centre mask
    // and cut links (ComputeWallLinks), and the solver-side Cell mask is ConservativeMask.
    // Every cell a triangle's AABB touches is tested with SAT
    // and marked solid on overlap. Flattened to one (mesh, triangle) list and Parallel.For'd over index
    // ranges (not Parallel.ForEach(obstacles, ...)) so a single big merged mesh -- one Mesh, many triangles
    // -- still spreads across every core instead of running on one thread. Concurrent writes of the same
    // value (1) into the shared byte[] are race-safe.
    static void VoxelizeObstaclesForDetection(VoxelGrid grid, List<Mesh> obstacles, int? maxDop)
    {
        // The test box is expanded by eps on all three axes (not just X/Y): with a whole-building domain,
        // walls, floor slabs and roof slabs all sit exactly on grid planes when their coordinates are
        // round and Dx divides evenly (the common case), including horizontal faces now that layers
        // above ground are real storeys, not just this floor. Expanding every axis is conservative
        // everywhere and guarantees a face lying exactly on a boundary plane marks the cells on both sides (open sheets only, see below).
        float eps = 1e-4f * grid.Dx;
        float half = grid.Dx * 0.5f + eps;
        var boxHalf = new Vector3(half, half, half);
        // P12-02: a triangle of a closed, consistently oriented shell (Material >= 0) is shrunk by eps instead, so a face
        // exactly on a grid plane marks neither neighbour cell; FillObstacleVolumes fills the shell's interior cells, and a
        // face off the plane still overlaps the cell it passes through. Open sheets keep the conservative +eps box.
        float halfShell = grid.Dx * 0.5f - eps;
        var boxHalfShell = new Vector3(halfShell, halfShell, halfShell);

        var tris = GeometryTriangles(obstacles);

        var po = new ParallelOptions();
        if (maxDop is int mdop && mdop > 0) po.MaxDegreeOfParallelism = mdop;
        Parallel.ForEach(Partitioner.Create(0, tris.Count), po, range =>
        {
            for (int k = range.Item1; k < range.Item2; k++)
            {
                Vector3 v0 = tris[k].A, v1 = tris[k].B, v2 = tris[k].C;
                var bh = tris[k].Material >= 0 ? boxHalfShell : boxHalf;

                Vector3 lo = Vector3.Min(v0, Vector3.Min(v1, v2)) - grid.Origin;
                Vector3 hi = Vector3.Max(v0, Vector3.Max(v1, v2)) - grid.Origin;

                int x0 = Math.Clamp((int)MathF.Floor(lo.X / grid.Dx), 0, grid.Nx - 1);
                int x1 = Math.Clamp((int)MathF.Floor(hi.X / grid.Dx), 0, grid.Nx - 1);
                int y0 = Math.Clamp((int)MathF.Floor(lo.Y / grid.Dx), 0, grid.Ny - 1);
                int y1 = Math.Clamp((int)MathF.Floor(hi.Y / grid.Dx), 0, grid.Ny - 1);
                int z0 = Math.Clamp((int)MathF.Floor(lo.Z / grid.Dx), 0, grid.Nz - 1);
                int z1 = Math.Clamp((int)MathF.Floor(hi.Z / grid.Dx), 0, grid.Nz - 1);

                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = grid.CellCenter(x, y, z);
                    if (TriBoxOverlap(c, bh, v0, v1, v2))
                        grid.Cell[grid.Index(x, y, z)] = VoxelGrid.Solid;
                }
            }
        });
    }

    /// <summary>Distance from every cell centre within <see cref="DistanceBand"/> cells of a surface to
    /// the nearest triangle, in metres; everything else keeps <see cref="DistanceFar"/>. Public so a
    /// hand-built grid can get one without going through <see cref="Build"/>.
    /// <para><b>Diagnostic only, no production caller</b> (<see cref="Build"/> derives wall positions from
    /// ComputeWallLinks on the final geometry): unsigned magnitudes are not a signed distance, since a
    /// conservative Solid mask does not establish the sign. It also replaces grid.WallLinks with a link
    /// table built from the obstacles alone (no openings), so do not call it on a grid a solver will use.</para>
    /// <para>Parallel over triangles, with a lock-free compare-and-swap minimum instead of a plain store, since two
    /// triangles can reach the same cell.</para></summary>
    public static void ComputeDistances(VoxelGrid grid, List<Mesh> obstacles, int? maxDop = null)
    {
        int count = grid.Count;
        var dist = new float[count];
        Array.Fill(dist, DistanceFar);

        var tris = new List<(Vector3[] Verts, int[] Idx, int I)>();
        foreach (var mesh in obstacles)
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
                tris.Add((mesh.Vertices, mesh.Indices, i));
        if (tris.Count == 0) { grid.Distance = dist; return; }

        float band = DistanceBand * grid.Dx;
        var po = new ParallelOptions();
        if (maxDop is int mdop && mdop > 0) po.MaxDegreeOfParallelism = mdop;
        Parallel.ForEach(Partitioner.Create(0, tris.Count), po, range =>
        {
            for (int k = range.Item1; k < range.Item2; k++)
            {
                var (verts, idx, i) = tris[k];
                Vector3 v0 = verts[idx[i]], v1 = verts[idx[i + 1]], v2 = verts[idx[i + 2]];

                Vector3 lo = Vector3.Min(v0, Vector3.Min(v1, v2)) - grid.Origin - new Vector3(band);
                Vector3 hi = Vector3.Max(v0, Vector3.Max(v1, v2)) - grid.Origin + new Vector3(band);
                int x0 = Math.Clamp((int)MathF.Floor(lo.X / grid.Dx), 0, grid.Nx - 1);
                int x1 = Math.Clamp((int)MathF.Floor(hi.X / grid.Dx), 0, grid.Nx - 1);
                int y0 = Math.Clamp((int)MathF.Floor(lo.Y / grid.Dx), 0, grid.Ny - 1);
                int y1 = Math.Clamp((int)MathF.Floor(hi.Y / grid.Dx), 0, grid.Ny - 1);
                int z0 = Math.Clamp((int)MathF.Floor(lo.Z / grid.Dx), 0, grid.Nz - 1);
                int z1 = Math.Clamp((int)MathF.Floor(hi.Z / grid.Dx), 0, grid.Nz - 1);

                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = Vector3.Distance(grid.CellCenter(x, y, z), ClosestOnTriangle(grid.CellCenter(x, y, z), v0, v1, v2));
                    if (d < band) AtomicMin(dist, grid.Index(x, y, z), d);
                }
            }
        });

        grid.Distance = dist;
        ComputeWallLinks(grid, obstacles, [], maxDop);
    }

    /// <summary>Metres, the value a cell keeps when no surface is near enough to matter (or none was
    /// computed): far enough that the solver reads it as "no wall position known here".</summary>
    public const float DistanceFar = 1e9f;
    const float DistanceBand = 2f;      // cells either side of a surface that get a distance

    static void AtomicMin(float[] arr, int i, float value)
    {
        float old = arr[i];
        while (value < old)
        {
            float prev = Interlocked.CompareExchange(ref arr[i], value, old);
            if (prev == old) break;
            old = prev;
        }
    }

    /// <summary>Closest point on a triangle to p (Ericson, Real-Time Collision Detection): the barycentric
    /// region test, which handles the three vertex, three edge and one face cases without a branchy
    /// projection.</summary>
    static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Separating-axis test (Akenine-Moller) for an axis-aligned box vs a triangle: box face normals,
    // the triangle normal, and the 9 cross products of box edges with triangle edges.
    static bool TriBoxOverlap(Vector3 boxCenter, Vector3 boxHalf, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        v0 -= boxCenter; v1 -= boxCenter; v2 -= boxCenter;
        Vector3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;

        if (!AxisOverlap(Vector3.UnitX, v0, v1, v2, boxHalf)) return false;
        if (!AxisOverlap(Vector3.UnitY, v0, v1, v2, boxHalf)) return false;
        if (!AxisOverlap(Vector3.UnitZ, v0, v1, v2, boxHalf)) return false;

        var n = Vector3.Cross(e0, e1);
        if (!AxisOverlap(n, v0, v1, v2, boxHalf)) return false;

        Span<Vector3> boxAxes = stackalloc Vector3[3] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        Span<Vector3> edges = stackalloc Vector3[3] { e0, e1, e2 };
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            var axis = Vector3.Cross(boxAxes[i], edges[j]);
            if (axis.LengthSquared() < 1e-12f) continue;   // parallel edge/axis: box-axis tests already cover it
            if (!AxisOverlap(axis, v0, v1, v2, boxHalf)) return false;
        }
        return true;
    }

    static bool AxisOverlap(Vector3 axis, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 boxHalf)
    {
        float p0 = Vector3.Dot(axis, v0), p1 = Vector3.Dot(axis, v1), p2 = Vector3.Dot(axis, v2);
        float triMin = MathF.Min(p0, MathF.Min(p1, p2));
        float triMax = MathF.Max(p0, MathF.Max(p1, p2));
        float r = boxHalf.X * MathF.Abs(axis.X) + boxHalf.Y * MathF.Abs(axis.Y) + boxHalf.Z * MathF.Abs(axis.Z);
        return triMax >= -r && triMin <= r;
    }

    /// <summary>Returns, per open opening whose carve went through its wall (<see cref="WallRunCells"/>), the range of the cleared cells'
    /// centres along its normal (made x-positive, as below): ComputeWallLinks stretches the opening's cut box over it.
    /// <paramref name="carved"/> marks every cell an open opening's carve wrote (Air in both masks, see ConservativeMask).</summary>
    static Dictionary<Opening, (float Lo, float Hi)> ApplyOpenings(VoxelGrid grid, List<Opening> openings, bool[] carved)
    {
        // Level is ignored: openings act on whichever storey their geometry actually sits in.
        var through = new Dictionary<Opening, (float Lo, float Hi)>();
        foreach (var o in openings) if (!o.IsOpen) ApplyOpening(grid, o, closed: true, carved);
        foreach (var o in openings) if (o.IsOpen && ApplyOpening(grid, o, closed: false, carved) is { } run) through[o] = run;
        return through;
    }

    static (float Lo, float Hi)? ApplyOpening(VoxelGrid grid, Opening o, bool closed, bool[] carved)
    {
        Vector3 n0 = o.Normal; n0.Z = 0;
        if (n0.LengthSquared() < 1e-8f) return null;   // no horizontal normal, nothing sensible to carve
        var n = Vector3.Normalize(n0);
        if (n.X < 0 || (n.X == 0 && n.Y < 0)) n = -n; // stable footprint ties when the normal is reversed
        var t = new Vector3(-n.Y, n.X, 0);

        float halfW = MathF.Max(o.Width, grid.Dx) * 0.5f;
        float halfH = MathF.Max(o.Height, grid.Dx) * 0.5f;
        // Open carve margin: half a cell (0.6x here, since exactly 0.5x can leave conservatively-marked
        // wall voxels behind on either side) is the minimum needed to remove those wall voxels; a full
        // +Dx margin over-carves and breaches thin partitions standing just behind the opening at coarse Dx.
        // The conservative halo of a wall turned off-axis reaches 0.5 Dx (|nx| + |ny|) from its faces (0.707 Dx at
        // 45 deg), so the margin is that halo + 0.1 Dx: exactly the 0.6 Dx above for an axis-aligned opening.
        // 6a: the cell-centre mask has no such halo; the margin is unchanged and matches the cut box ComputeWallLinks clears hits in.
        float halfD = closed ? MathF.Max(o.Depth, grid.Dx) * 0.5f : o.Depth * 0.5f + (0.1f + 0.5f * (MathF.Abs(n.X) + MathF.Abs(n.Y))) * grid.Dx;

        // World AABB of the oriented rectangle (max of its local half-extents can clip rotated corners).
        var reach = Vector3.Abs(t) * halfW + Vector3.Abs(n) * halfD + Vector3.UnitZ * halfH + new Vector3(grid.Dx);
        Vector3 lo = o.Center - reach - grid.Origin;
        Vector3 hi = o.Center + reach - grid.Origin;

        int x0 = Math.Clamp((int)MathF.Floor(lo.X / grid.Dx), 0, grid.Nx - 1);
        int x1 = Math.Clamp((int)MathF.Floor(hi.X / grid.Dx), 0, grid.Nx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y / grid.Dx), 0, grid.Ny - 1);
        int y1 = Math.Clamp((int)MathF.Floor(hi.Y / grid.Dx), 0, grid.Ny - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z / grid.Dx), 0, grid.Nz - 1);
        int z1 = Math.Clamp((int)MathF.Floor(hi.Z / grid.Dx), 0, grid.Nz - 1);

        var through = closed ? null : WallRunCells(grid, o, n, t, halfW, halfH, halfD);   // read before the carve changes anything
        byte value = closed ? VoxelGrid.Solid : VoxelGrid.Air;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var c = grid.CellCenter(x, y, z) - o.Center;
            float a = Vector3.Dot(c, t);
            float b = c.Z;
            float e = MathF.Abs(Vector3.Dot(c, n));
            if (InClosed(a, halfW, grid.Dx) && InClosed(b, halfH, grid.Dx) && e <= halfD)
            {
                grid.Cell[grid.Index(x, y, z)] = value;
                carved[grid.Index(x, y, z)] = !closed;
            }
        }
        if (through is not { Count: > 0 }) return null;
        float eLo = 0f, eHi = 0f;
        foreach (int i in through)
        {
            grid.Cell[i] = VoxelGrid.Air; carved[i] = true;
            float e = Vector3.Dot(grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny)) - o.Center, n);
            eLo = MathF.Min(eLo, e); eHi = MathF.Max(eHi, e);
        }
        return (eLo, eHi);
    }

    /// <summary>P12-05: an open opening is carved fully through its wall whatever <see cref="Opening.Depth"/> or centre offset says (a curtain-wall
    /// fallback Depth of 0.1 m, a centre 0.8 Dx off the wall mid-plane). From the opening's centre line, along the normal on both sides, the cells of the
    /// solid run that starts at the centre, or at a solid within the opening's own wall span beyond it, are returned, up to <see cref="CarveThroughCap"/> m, as the voxelizer
    /// first made them (furniture is not in the grid yet, RX-05): the run ends at the first air cell, so a second wall with a gap cell in front of it is not part of the run and stays; a centre in air
    /// starts it at a solid within Depth/2 + 1 Dx along the normal, else nothing is cleared. Only cells
    /// whose centre passes the footprint test are returned, so the opening's area is unchanged. A run that has not reached air within the cap is not a wall
    /// (a roof slab or a perpendicular wall that the footprint overlaps along the normal): it is left alone.</summary>
    const float CarveThroughCap = 1f;
    static List<int> WallRunCells(VoxelGrid grid, Opening o, Vector3 n, Vector3 t, float halfW, float halfH, float halfD)
    {
        var cells = new List<int>();
        float dx = grid.Dx, step = 0.5f * dx, span = o.Depth * 0.5f + dx;   // RX-05: how far from the centre a wall solid may start (never cross air into a second solid)
        int na = (int)MathF.Ceiling(2 * halfW / step), nb = (int)MathF.Ceiling(2 * halfH / step);
        for (int i = 0; i < na; i++)
        for (int j = 0; j < nb; j++)
        {
            float a = -halfW + (i + 0.5f) * step, b = -halfH + (j + 0.5f) * step;
            for (int side = -1; side <= 1; side += 2)
            {
                bool inWall = false;
                int mark = cells.Count;
                for (float s = 0; s <= CarveThroughCap; s += step)
                {
                    var p = o.Center + a * t + new Vector3(0, 0, b) + side * s * n - grid.Origin;
                    int x = (int)MathF.Floor(p.X / dx), y = (int)MathF.Floor(p.Y / dx), z = (int)MathF.Floor(p.Z / dx);
                    if (x < 0 || y < 0 || z < 0 || x >= grid.Nx || y >= grid.Ny || z >= grid.Nz) { if (inWall) cells.RemoveRange(mark, cells.Count - mark); break; }
                    int idx = grid.Index(x, y, z);
                    if (grid.Cell[idx] == VoxelGrid.Solid)
                    {
                        inWall = true;
                        var c = grid.CellCenter(x, y, z) - o.Center;
                        if (InClosed(Vector3.Dot(c, t), halfW, dx) && InClosed(c.Z, halfH, dx)) cells.Add(idx);
                        if (s + step > CarveThroughCap) cells.RemoveRange(mark, cells.Count - mark);   // still solid at the cap: not a wall
                    }
                    else if (inWall || s > span) break;   // air after the wall (kept); before it, RX-05: a centre in air starts the run at a wall solid within the opening's own wall span, else the hole is already open
                }
            }
        }
        return cells;
    }

    /// <summary>RP-03 / RP follow-up 4: the ONE footprint rule of the carve, the carve-through, the throat cells and the opening flux plan: closed, with the 1e-3 Dx the cut box of <see cref="ComputeWallLinks"/> is wider by, so a
    /// cell centred exactly on an edge of the hole is carved at the lower and the upper edge alike (the half-open rule above carved the lower one only, and the cut box
    /// is closed: the hole came out n - 0.5 cells wide and lopsided). Both edge cells are carved and their outward links are cut at q ~ 0: width n, mirror-symmetric.
    /// Before batch 5 the throat counted by a half-open rule (n cells where the carve opened n + 1 on an edge-on-centre hole); now the throat counts what the carve opens.</summary>
    internal static bool InClosed(float value, float half, float dx) => MathF.Abs(value) <= half + 1e-3f * dx;

    /// <summary>RU follow-up 1: a patch narrower than a cell can hold no cell centre at the chosen cell size, so it carves no cells and the device does nothing
    /// without being wrong anywhere else. Null when some cell (air or not) does fall in the patch (then "no air at its location" is the right note).</summary>
    internal static string? PatchVanishedNote(VoxelGrid grid, AirSource s, string kind) =>
        SourceCells(grid, s, _ => true).Count == 0
            ? $"{kind} {s.Name}: its patch is smaller than one cell at this cell size ({s.Width:0.##} x {s.Height:0.##} m, cell {grid.Dx:0.###} m), so it has no cells and does nothing here; use a finer quality level or a larger patch."
            : null;

    // Mechanical air sources: a rectangular patch normal to Direction is carved into Fixed cells wherever
    // it currently finds Air. Returns which source (by name) owns each Fixed cell index, purely so the
    // flood fill below can name a source in its warning if that patch turns out to be unreachable air.
    static Dictionary<int, string> ApplySources(VoxelGrid grid, List<AirSource> sources)
    {
        var owner = new Dictionary<int, string>();
        for (int si = 0; si < sources.Count; si++)
        {
            var s = sources[si];
            if (!s.Enabled || s.FlowRate <= 0) continue;
            if (s.Device != DeviceKind.Patch) continue;   // body-force device: ordinary air cells, see ApplyDevices

            var n = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : Vector3.UnitZ;
            // ponytail: a zero/degenerate Direction defaults to straight down; malformed input, not a
            // real scene, so it's not worth a warning.
            var velocity = n * (s.FlowRate / (s.Width * s.Height));

            var cells = SourceCells(grid, s, c => c == VoxelGrid.Air);
            if (cells.Count == 0)
            {
                grid.Warnings.Add(PatchVanishedNote(grid, s, "Source") ?? $"Source {s.Name}: no air at its location");   // RU follow-up 1
                continue;
            }

            foreach (var idx in cells)
            {
                // Cells already Fixed keep the first source that claimed them.
                grid.Cell[idx] = VoxelGrid.Fixed;
                // Every patch is a link-wise velocity wall with a flux controller in the solver (FixedCell.Supply): a Supply injects
                // (Mode 0), a Return extracts (1), a Fan passes air through (2). Return and Fan used to be equilibrium resets that
                // extracted whatever their carved area gave, not their FlowRate (RV-01 follow-up).
                grid.FixedCells.Add(new FixedCell(idx, velocity, si, s.Kind == SourceKind.Return ? (byte)1 : s.Kind == SourceKind.Fan ? (byte)2 : (byte)0));
                owner[idx] = s.Name;
            }
        }
        return owner;
    }

    /// <summary>Cells matching predicate inside the source's oriented patch (Width x Height, one cell thick,
    /// perpendicular to Direction). Retries the patch centre at offsets 0, +1, -1, +2, -2, +3, -3 x Dx along
    /// Direction -- kind-independent, since a flush-mounted patch's Direction may point either into open air
    /// (a normal ceiling supply, already inside the room) or into whatever it's flush-mounted on (a flush
    /// return grille, whose Direction by convention points INTO the wall/ceiling it's set into) -- the first
    /// offset that yields a non-empty result wins. Used both to carve the patch into Fixed cells (Build) and
    /// to recover the same cells afterwards for analysis (Analysis.AnalyzeSource), so both call sites must
    /// share this one search: identical geometry, identical attempt order, lands on identical cells.</summary>
    public static List<int> SourceCells(VoxelGrid grid, AirSource s, Func<byte, bool> predicate)
    {
        var n = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : Vector3.UnitZ;
        var up = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var t = Vector3.Normalize(Vector3.Cross(up, n));
        var b = Vector3.Cross(n, t);
        float halfW = s.Width * 0.5f, halfH = s.Height * 0.5f, halfT = grid.Dx * 0.5f;

        Span<int> offsets = stackalloc int[7] { 0, 1, -1, 2, -2, 3, -3 };
        foreach (int k in offsets)
        {
            var center = s.Center + n * (k * grid.Dx);
            var cells = CellsInBox(grid, center, n, t, b, halfW, halfH, halfT, predicate);
            if (cells.Count > 0) return cells;
        }
        return new List<int>();
    }

    static List<int> CellsInBox(VoxelGrid grid, Vector3 center, Vector3 n, Vector3 t, Vector3 b, float halfW, float halfH, float halfT, Func<byte, bool> predicate)
    {
        var reach = Vector3.Abs(t)*halfW + Vector3.Abs(b)*halfH + Vector3.Abs(n)*halfT + new Vector3(grid.Dx);
        Vector3 lo = center - reach - grid.Origin;
        Vector3 hi = center + reach - grid.Origin;
        int x0 = Math.Clamp((int)MathF.Floor(lo.X / grid.Dx), 0, grid.Nx - 1);
        int x1 = Math.Clamp((int)MathF.Floor(hi.X / grid.Dx), 0, grid.Nx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y / grid.Dx), 0, grid.Ny - 1);
        int y1 = Math.Clamp((int)MathF.Floor(hi.Y / grid.Dx), 0, grid.Ny - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z / grid.Dx), 0, grid.Nz - 1);
        int z1 = Math.Clamp((int)MathF.Floor(hi.Z / grid.Dx), 0, grid.Nz - 1);

        var result = new List<int>();
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int idx = grid.Index(x, y, z);
            if (!predicate(grid.Cell[idx])) continue;
            var c = grid.CellCenter(x, y, z) - center;
            if (MathF.Abs(Vector3.Dot(c, t)) <= halfW && MathF.Abs(Vector3.Dot(c, b)) <= halfH && MathF.Abs(Vector3.Dot(c, n)) <= halfT)
                result.Add(idx);
        }
        return result;
    }

    // ---- body-force devices (AirSource.Device != Patch) ------------------------------------------
    // A device is NOT carved into Fixed cells: its cells stay ordinary air and the solver adds a Guo body
    // force there (f = rho*(Target - u)/tau_relax), which accelerates the air already present instead of
    // creating any -- mass is conserved by construction, which a Fixed patch cannot do. Everything
    // geometric (which cells, and the target velocity in each, in m/s) is decided here; LbmSolver only
    // converts to lattice units and applies the force.

    const float AirDensity = 1.2f;          // kg/m3 at ~20 C, used only by the Thrust -> speed conversion
    const float HoodCaptureDepth = 0.3f;    // m of capture region below a Hood face when Radius is 0
    const float MaxTiltDeg = 75f;           // louvre/spread ceiling: the 1/cos(tilt) speed blows up past this

    /// <summary>Marks the air cells every non-Patch <see cref="AirSource"/> drives as one
    /// <see cref="DeviceRegion"/> per device. Public so a hand-built grid (test harness, benchmark) can get
    /// devices without going through <see cref="Build"/>; call it after obstacles, openings and the Fixed
    /// source patches are in place, and before the flood fill.</summary>
    public static void ApplyDevices(VoxelGrid grid, List<AirSource> sources)
    {
        for (int si = 0; si < sources.Count; si++)
        {
            var s = sources[si];
            if (!s.Enabled || s.Device == DeviceKind.Patch) continue;
            if (s.FlowRate <= 0f && s.Thrust <= 0f) continue;

            var region = BuildDevice(grid, s);
            if (region != null) region.SourceIndex = si;
            if (region == null || region.Cells.Length == 0)
            {
                grid.Warnings.Add(PatchVanishedNote(grid, s, "Device") ?? $"Device {s.Name}: no air at its location");   // RU follow-up 1
                continue;
            }
            grid.Devices.Add(region);
        }
    }

    static DeviceRegion? BuildDevice(VoxelGrid grid, AirSource s)
    {
        var n = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : -Vector3.UnitZ;
        var up = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var t = Vector3.Normalize(Vector3.Cross(up, n));
        var b = Vector3.Cross(n, t);
        return s.Device switch
        {
            DeviceKind.CeilingFan or DeviceKind.PedestalFan => BuildFanDisc(grid, s, n, t, b),
            DeviceKind.SplitAc => BuildSplitAc(grid, s, n, t, b),
            DeviceKind.CassetteAc => BuildCassette(grid, s, n, t, b),
            DeviceKind.ExhaustFan or DeviceKind.Hood => BuildExhaust(grid, s, n, t, b),
            DeviceKind.Diffuser => BuildDiffuser(grid, s, n, t, b),
            _ => null,
        };
    }

    /// <summary>Actuator disc: a circle of radius R, two cell layers thick, normal to Direction. The axial
    /// speed follows the classic hub-to-tip shape w(r) = (r/R)(1 - r/R) -- zero at the hub and at the tip,
    /// peak at mid-span -- scaled per layer so the disc's volume flow is exactly FlowRate. Swirl adds a
    /// tangential component of Swirl x the local axial speed, giving the rotating wake a real ceiling fan has.</summary>
    static DeviceRegion? BuildFanDisc(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b)
    {
        float r = s.Radius > 0f ? s.Radius : 0.5f * MathF.Max(s.Width, s.Height);
        if (r <= 0f) { grid.Warnings.Add($"Device {s.Name}: fan needs Radius (or Width/Height) > 0"); return null; }

        var cells = SearchPatch(grid, s.Center, n, t, b, r, r, grid.Dx, radial: true);
        if (cells.Count == 0) return null;
        // Eight cells across is where the hub-to-tip profile and the swirl start to mean anything; below
        // that the disc is a few blocks of forced air. Measured on a real model at 255 mm: a disc 4.7 cells
        // across delivered 28 % of its set flow. What the device actually achieved is reported per run as
        // DeviceResult.FlowRate against SetFlowRate, and a shortfall is repeated in the mass ledger notes.
        if (2f * r < FanDiscCells * grid.Dx)
            grid.Warnings.Add($"Device {s.Name}: the fan disc is only {2f * r / grid.Dx:0.#} cells across at " +
                $"{grid.Dx * 1000:0} mm (wants {FanDiscCells}); its profile and swirl are not resolved and it " +
                "will move less than its set flow - check FlowRate against the rating on the result, or solve a finer grid");
        // A fan draws through the gap between its disc and whatever it hangs under. Measured on an 8 x 6 m
        // room at 268 mm: 1.5 cells of clearance chokes the intake and the disc delivers 44 % of its set
        // flow; 3 cells gives 102 %, even with only 5 cells across the disc. The requirement is metric
        // (FanClearanceMetres, at least one cell): 3 cells at a 255 mm Preview grid is 0.77 m, which no
        // real fan on a 0.35 m downrod under a 3 m ceiling has, and such a fan still delivers 77 %. So the
        // clearance is only recorded here; Analysis warns "choked" when the run shows the fan actually
        // fell short (achieved/set below FanChokedRatio) with too little clear air above it.
        int need = Math.Max(1, (int)MathF.Ceiling(FanClearanceMetres / grid.Dx - 1e-3f));
        int clear = FreeCellsAlong(grid, s.Center, -n, need);

        float q = FlowOf(s, MathF.PI * r * r);
        var weight = new float[cells.Count];
        float wSum = 0f;
        for (int i = 0; i < cells.Count; i++)
        {
            float rr = MathF.Sqrt(cells[i].A * cells[i].A + cells[i].B * cells[i].B) / r;
            weight[i] = MathF.Max(rr * (1f - rr), 0f);
            wSum += weight[i];
        }
        if (wSum <= 1e-9f) for (int i = 0; i < cells.Count; i++) weight[i] = 1f;   // disc too coarse to shape

        var speed = LayerSpeeds(grid, cells, weight, q);
        float swirl = Math.Clamp(s.Swirl, 0f, 1f);
        var target = new Vector3[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            var radial = cells[i].A * t + cells[i].B * b;
            var v = n * speed[i];
            if (swirl > 0f && radial.LengthSquared() > 1e-12f)
                v += Vector3.Normalize(Vector3.Cross(n, Vector3.Normalize(radial))) * (swirl * speed[i]);
            target[i] = v;
        }
        var region = Region(s, cells, target, cells.Count, LayerCount(cells), q);
        region.Normal = n;
        region.IntakeClearance = clear * grid.Dx;
        region.IntakeShort = clear < need;
        return region;
    }

    /// <summary>Wall-mounted split unit: a thin discharge patch whose jet is tilted LouvreAngle below the
    /// horizontal, plus a linked return grille (by default immediately above the outlet) that pulls the same
    /// volume flow in. The jet speed is FlowRate / (carved area x cos(tilt)) so the flux through the grille
    /// face is FlowRate whatever the louvre setting.</summary>
    static DeviceRegion? BuildSplitAc(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b)
    {
        float halfW = MathF.Max(s.Width, grid.Dx) * 0.5f, halfH = MathF.Max(s.Height, grid.Dx) * 0.5f;
        var outlet = SearchPatch(grid, s.Center, n, t, b, halfW, halfH, grid.Dx * 0.5f, radial: false);
        if (outlet.Count == 0) return null;

        float tilt = Math.Clamp(s.LouvreAngle, 0f, MaxTiltDeg) * MathF.PI / 180f;
        var jet = Tilt(n, tilt);
        float cos = MathF.Max(Vector3.Dot(jet, n), 0.2f);
        float q = FlowOf(s, outlet.Count * grid.Dx * grid.Dx / MathF.Max(1, LayerCount(outlet)));
        var speed = LayerSpeeds(grid, outlet, Ones(outlet.Count), q);

        var cells = new List<DeviceCell>(outlet);
        var target = new List<Vector3>(outlet.Count);
        for (int i = 0; i < outlet.Count; i++) target.Add(jet * (speed[i] / cos));
        int outletCount = outlet.Count, layers = LayerCount(outlet);

        // Default return: flush on the wall behind the unit (the first air layer in front of it, up to
        // WallSearchCells back), its centre ReturnSeparationMetres (and >= 2 cells) above the outlet's centre
        // when the ceiling allows, otherwise as high as the ceiling allows -- never closer than one clear
        // cell above the outlet's top edge. Sitting in the outlet's own plane directly on top of it, the
        // grille drew from the jet's recirculation bubble and read 3.96 K below the air at its height; with
        // the grille flush under the ceiling, |T_return - T_layer| at Dx 0.1 m went 2.29 K at 0.3 m of
        // separation, 1.22 K at 0.4 m, 0.61 K at 0.5 m, 0.01 K at 0.7 m (check 41f).
        float retHalfH = MathF.Max(s.ReturnHeight > 0f ? s.ReturnHeight : s.Height, grid.Dx) * 0.5f;
        int back = FreeCellsAlong(grid, s.Center, -n, WallSearchCells);
        bool onWall = back < WallSearchCells;
        var wall = s.Center - n * (onWall ? back * grid.Dx : 0f);
        float minRise = halfH + retHalfH + grid.Dx;
        float rise = MathF.Max(minRise, MathF.Max(ReturnSeparationMetres, 2f * grid.Dx));
        float roof = CeilingAbove(grid, wall, rise + retHalfH);
        if (!float.IsNaN(roof)) rise = MathF.Max(minRise, MathF.Min(rise, roof - retHalfH - wall.Z));
        var auto = wall + Vector3.UnitZ * rise;
        // A grille flush on the wall pulls DOWN the wall toward the outlet, not into the wall: a force into
        // a solid moves no air, but the thermal lattice advects with the force-corrected velocity and pumped
        // heat into the corner (29.7 C measured in a 26 C room that is only ever cooled).
        bool userPlaced = !float.IsNaN(s.ReturnCenter.X);
        AddReturn(grid, s, n, t, b, auto, q, cells, target, !userPlaced && onWall ? -Vector3.UnitZ : -n);
        var region = Region(s, cells, target.ToArray(), outletCount, layers, q);
        region.Normal = n;
        SetGather(grid, s, region, n, userPlaced);
        if (cells.Count > outletCount)
        {
            // Centroid to centroid, over the cells actually carved (a user centre in a wall snaps to air).
            float gap = Vector3.Distance(Centroid(grid, cells, 0, outletCount), Centroid(grid, cells, outletCount, cells.Count));
            if (gap < ReturnWarnMetres)
                grid.Warnings.Add($"Device {s.Name}: return grille within {gap:0.##} m of the outlet: it re-ingests " +
                    $"its own cold jet, so the return temperature and duty read low - keep it >= {ReturnSeparationMetres:0.#} m away " +
                    "(the default placement does when the ceiling allows)");
        }
        return region;
    }

    static Vector3 Centroid(VoxelGrid grid, List<DeviceCell> cells, int from, int to)
    {
        var sum = Vector3.Zero;
        int plane = grid.Nx * grid.Ny;
        for (int i = from; i < to; i++)
        {
            int idx = cells[i].Index;
            sum += grid.CellCenter(idx % grid.Nx, idx / grid.Nx % grid.Ny, idx / plane);
        }
        return sum / Math.Max(1, to - from);
    }

    /// <summary>Height of the first solid face straight above <paramref name="from"/>, looking no further
    /// than <paramref name="reach"/> metres; NaN when there is none that close (or the domain ends first).</summary>
    static float CeilingAbove(VoxelGrid grid, Vector3 from, float reach)
    {
        var p = from - grid.Origin;
        int x = (int)MathF.Floor(p.X / grid.Dx), y = (int)MathF.Floor(p.Y / grid.Dx), z0 = (int)MathF.Floor(p.Z / grid.Dx);
        if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny) return float.NaN;
        int z1 = Math.Min(grid.Nz - 1, (int)MathF.Floor((p.Z + reach) / grid.Dx));
        for (int z = Math.Max(0, z0 + 1); z <= z1; z++)
            if (grid.Cell[grid.Index(x, y, z)] == VoxelGrid.Solid) return grid.Origin.Z + z * grid.Dx;
        return float.NaN;
    }

    /// <summary>Ceiling cassette: the outer band of a Width x Width square discharges outward (tilted
    /// LouvreAngle below the horizontal, four-way), and the cells it encloses are the central return.</summary>
    static DeviceRegion? BuildCassette(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b)
    {
        float half = MathF.Max(s.Width, 3f * grid.Dx) * 0.5f;
        var patch = SearchPatch(grid, s.Center, n, t, b, half, half, grid.Dx * 0.5f, radial: false);
        if (patch.Count == 0) return null;

        float band = MathF.Max(grid.Dx, 0.12f * half * 2f);
        float retHalf = s.ReturnWidth > 0f ? s.ReturnWidth * 0.5f : MathF.Max(half - band, 0f);
        var outlet = patch.Where(c => MathF.Max(MathF.Abs(c.A), MathF.Abs(c.B)) > half - band).ToList();
        var ret = patch.Where(c => MathF.Max(MathF.Abs(c.A), MathF.Abs(c.B)) <= retHalf).ToList();
        if (outlet.Count == 0) { outlet = patch; ret = new List<DeviceCell>(); }

        float q = FlowOf(s, outlet.Count * grid.Dx * grid.Dx);
        float tilt = Math.Clamp(s.LouvreAngle, 0f, MaxTiltDeg) * MathF.PI / 180f;
        // ponytail: a cassette discharges sideways out of a slot whose real area is the slot's vertical
        // face, which a cell-sized band cannot represent -- the speed here is FlowRate over the band's plan
        // area. Good enough for the room-scale jet; do not read the slot velocity itself as a rating.
        float speed = q / MathF.Max(outlet.Count * grid.Dx * grid.Dx, 1e-9f);
        var cells = new List<DeviceCell>(outlet);
        var target = new List<Vector3>(outlet.Count);
        foreach (var c in outlet)
        {
            var radial = c.A * t + c.B * b;
            var dir = radial.LengthSquared() > 1e-12f
                ? Vector3.Normalize(Vector3.Normalize(radial) * MathF.Cos(tilt) + n * MathF.Sin(tilt))
                : n;
            target.Add(dir * speed);
        }
        if (ret.Count > 0)
        {
            float retSpeed = q / (ret.Count * grid.Dx * grid.Dx);
            foreach (var c in ret) { cells.Add(c); target.Add(-n * retSpeed); }
        }
        else grid.Warnings.Add($"Device {s.Name}: the cassette has no room for a central return at this Dx");
        var region = Region(s, cells, target.ToArray(), outlet.Count, 1, q);
        SetGather(grid, s, region, n, userPlaced: false);   // the central return is fixed by the geometry
        return region;
    }

    /// <summary>Extract fan or hood: every layer of the region is driven along Direction at
    /// FlowRate / (layer area), so the air is pushed through the opening the device sits in. A Hood extends
    /// Radius (default 0.3 m) upstream of its face, giving it a capture volume rather than one cell layer.
    /// <para>The air has to have somewhere to go: an exhaust on an exterior wall must sit in an OPEN
    /// opening (the carved hole through the wall), which then behaves like today's Return boundary -- the
    /// air leaves through the domain's far-field ring. Without a path out, the device only stirs the room,
    /// and a Warning says so.</para></summary>
    static DeviceRegion? BuildExhaust(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b)
    {
        float halfW = MathF.Max(s.Width, grid.Dx) * 0.5f, halfH = MathF.Max(s.Height, grid.Dx) * 0.5f;
        float depth = s.Device == DeviceKind.Hood ? MathF.Max(s.Radius > 0f ? s.Radius : HoodCaptureDepth, 2f * grid.Dx)
                                                  : 2f * grid.Dx;
        // The capture region sits upstream of the face (the room side), so the hood pulls from the volume
        // under it rather than from the ceiling above it.
        var center = s.Center - n * (depth * 0.5f - grid.Dx * 0.5f);
        var cells = SearchPatch(grid, center, n, t, b, halfW, halfH, depth * 0.5f, radial: false);
        if (cells.Count == 0) return null;

        float q = FlowOf(s, cells.Count * grid.Dx * grid.Dx / MathF.Max(1, LayerCount(cells)));
        var speed = LayerSpeeds(grid, cells, Ones(cells.Count), q);
        var target = new Vector3[cells.Count];
        for (int i = 0; i < cells.Count; i++) target[i] = n * speed[i];

        if (DischargeBlocked(grid, s.Center, n)) grid.Warnings.Add(
            $"Device {s.Name}: a solid surface sits within {DischargeProbe:0.#} m in front of the extract, " +
            "so it cannot remove air - put it in an open opening through the wall");
        var region = Region(s, cells, target, cells.Count, LayerCount(cells), q);
        region.Normal = n;
        return region;
    }

    /// <summary>Supply diffuser: the patch of the Patch kind, but as a body force and with the jet fanned
    /// out by up to LouvreAngle at the rim (linearly with radius), which is what a real diffuser's cone
    /// does and what a single-direction patch cannot do.</summary>
    static DeviceRegion? BuildDiffuser(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b)
    {
        float halfW = MathF.Max(s.Width, grid.Dx) * 0.5f, halfH = MathF.Max(s.Height, grid.Dx) * 0.5f;
        var cells = SearchPatch(grid, s.Center, n, t, b, halfW, halfH, grid.Dx * 0.5f, radial: false);
        if (cells.Count == 0) return null;

        float q = FlowOf(s, cells.Count * grid.Dx * grid.Dx / MathF.Max(1, LayerCount(cells)));
        var speed = LayerSpeeds(grid, cells, Ones(cells.Count), q);
        float spread = Math.Clamp(s.LouvreAngle, 0f, MaxTiltDeg) * MathF.PI / 180f;
        float rMax = MathF.Max(MathF.Sqrt(halfW * halfW + halfH * halfH), 1e-6f);
        var target = new Vector3[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            var radial = cells[i].A * t + cells[i].B * b;
            float phi = spread * MathF.Min(radial.Length() / rMax, 1f);
            var dir = radial.LengthSquared() > 1e-12f
                ? Vector3.Normalize(n * MathF.Cos(phi) + Vector3.Normalize(radial) * MathF.Sin(phi))
                : n;
            // speed / cos(phi) keeps the flux through the patch face equal to FlowRate at every spread angle.
            target[i] = dir * (speed[i] / MathF.Max(MathF.Cos(phi), 0.2f));
        }
        var region = Region(s, cells, target, cells.Count, LayerCount(cells), q);
        region.Normal = n;
        return region;
    }

    // ---- device helpers --------------------------------------------------------------------------

    /// <summary>One cell of a device patch: its grid index, its two in-plane coordinates relative to the
    /// patch centre (along t and b) and which cell layer along the axis it sits in.</summary>
    readonly record struct DeviceCell(int Index, float A, float B, int Layer);

    static float[] Ones(int n) { var a = new float[n]; Array.Fill(a, 1f); return a; }

    const float ReturnSeparationMetres = 0.5f;   // default split-AC return centre above the outlet centre, see BuildSplitAc
    const float ReturnWarnMetres = 0.4f;         // closer than this (centroid to centroid) the return re-ingests its jet
    const float FanClearanceMetres = 0.25f;   // clear air a fan's intake side needs (>= 1 cell), see BuildFanDisc
    const int FanDiscCells = 8;         // cells across the disc below which the profile is not resolved

    /// <summary>How many consecutive non-solid cells lie along <paramref name="dir"/> from
    /// <paramref name="from"/>, up to <paramref name="max"/>. Leaving the domain counts as clear.</summary>
    static int FreeCellsAlong(VoxelGrid grid, Vector3 from, Vector3 dir, int max)
    {
        for (int k = 1; k <= max; k++)
        {
            var p = from + dir * (k * grid.Dx) - grid.Origin;
            int x = (int)MathF.Floor(p.X / grid.Dx), y = (int)MathF.Floor(p.Y / grid.Dx), z = (int)MathF.Floor(p.Z / grid.Dx);
            if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny || z < 0 || z >= grid.Nz) return max;
            if (grid.Cell[grid.Index(x, y, z)] == VoxelGrid.Solid) return k - 1;
        }
        return max;
    }

    /// <summary>P17-04: refuses a grid past <see cref="LbmSolver.MaxCells"/> (int kernel indices) before anything is allocated, in plain words.</summary>
    static void CheckCellCount(long nx, long ny, long nz)
    {
        long n = nx * ny * nz;
        if (n > LbmSolver.MaxCells)
            throw new InvalidOperationException($"The grid would have {n / 1e6:0.#} million cells; the solver can address at most {LbmSolver.MaxCells / 1e6:0.#} million. Use a coarser cell size or a lower quality.");
    }

    static int LayerCount(List<DeviceCell> cells) => cells.Select(c => c.Layer).Distinct().Count();

    /// <summary>FlowRate, or the flow a Thrust override implies: T = rho * Q * u with u = Q / area gives
    /// u = sqrt(T / (rho * area)) and Q = u * area.</summary>
    static float FlowOf(AirSource s, float area) =>
        s.Thrust > 0f && area > 1e-9f ? MathF.Sqrt(s.Thrust / (AirDensity * area)) * area : s.FlowRate;

    /// <summary>Rotates <paramref name="n"/> by <paramref name="tilt"/> toward straight down. A vertical
    /// axis (a cassette or a ceiling fan) has no "below the horizontal" to tilt into, so it is returned
    /// unchanged.</summary>
    static Vector3 Tilt(Vector3 n, float tilt)
    {
        var horizontal = new Vector3(n.X, n.Y, 0f);
        if (horizontal.LengthSquared() < 1e-8f || tilt <= 0f) return n;
        return Vector3.Normalize(Vector3.Normalize(horizontal) * MathF.Cos(tilt) - Vector3.UnitZ * MathF.Sin(tilt));
    }

    /// <summary>Per-cell speeds that make EVERY cell layer of the region carry the same volume flow
    /// <paramref name="q"/>: sum over a layer of speed x Dx^2 == q, with the shape given by
    /// <paramref name="weight"/>. Normalising against the cells actually carved (not the nominal Width x
    /// Height) is what keeps the measured flow equal to the set flow at a coarse Dx.</summary>
    static float[] LayerSpeeds(VoxelGrid grid, List<DeviceCell> cells, float[] weight, float q)
    {
        var perLayer = new Dictionary<int, float>();
        for (int i = 0; i < cells.Count; i++)
            perLayer[cells[i].Layer] = perLayer.GetValueOrDefault(cells[i].Layer) + weight[i];

        float cellArea = grid.Dx * grid.Dx;
        var speed = new float[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            float sum = perLayer[cells[i].Layer];
            speed[i] = sum > 1e-12f ? q * weight[i] / (sum * cellArea) : 0f;
        }
        return speed;
    }

    /// <summary>Appends the linked return (intake) cells of an AC unit: the grille pulls the same volume
    /// flow <paramref name="q"/> the outlet pushes, along <paramref name="pull"/> (toward the unit), so the
    /// pair is a pure momentum dipole that moves no net mass.</summary>
    static void AddReturn(VoxelGrid grid, AirSource s, Vector3 n, Vector3 t, Vector3 b, Vector3 autoCenter,
        float q, List<DeviceCell> cells, List<Vector3> target, Vector3 pull)
    {
        var center = float.IsNaN(s.ReturnCenter.X) ? autoCenter : s.ReturnCenter;
        float halfW = MathF.Max(s.ReturnWidth > 0f ? s.ReturnWidth : s.Width, grid.Dx) * 0.5f;
        float halfH = MathF.Max(s.ReturnHeight > 0f ? s.ReturnHeight : s.Height, grid.Dx) * 0.5f;
        var ret = SearchPatch(grid, center, n, t, b, halfW, halfH, grid.Dx * 0.5f, radial: false);
        if (ret.Count == 0)
        {
            grid.Warnings.Add($"Device {s.Name}: no air at the return grille, so the unit only blows");
            return;
        }
        var speed = LayerSpeeds(grid, ret, Ones(ret.Count), q);
        for (int i = 0; i < ret.Count; i++) { cells.Add(ret[i]); target.Add(pull * speed[i]); }
    }

    const int WallSearchCells = 5;      // how far behind a split unit the auto return looks for its wall
    const int JetExcludeCells = 3;      // cells of the discharge jet (from the outlet) kept out of the return gather

    /// <summary>The outlet cells, the first <see cref="JetExcludeCells"/> cells along each outlet cell's
    /// own discharge direction, and every face neighbour of those: where the air is the unit's own
    /// discharge rather than room air.</summary>
    static HashSet<int> JetRegion(VoxelGrid grid, DeviceRegion d)
    {
        var core = new HashSet<int>();
        int plane = grid.Nx * grid.Ny;
        for (int i = 0; i < d.OutletCount; i++)
        {
            int idx = d.Cells[i];
            core.Add(idx);
            float len = d.Target[i].Length();
            if (len <= 1e-9f) continue;
            var dir = d.Target[i] / len;
            var c = grid.CellCenter(idx % grid.Nx, idx / grid.Nx % grid.Ny, idx / plane);
            for (int k = 1; k <= JetExcludeCells; k++)
            {
                var p = c + dir * (k * grid.Dx) - grid.Origin;
                int x = (int)MathF.Floor(p.X / grid.Dx), y = (int)MathF.Floor(p.Y / grid.Dx), z = (int)MathF.Floor(p.Z / grid.Dx);
                if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny || z < 0 || z >= grid.Nz) break;
                core.Add(grid.Index(x, y, z));
            }
        }
        var jet = new HashSet<int>(core);
        foreach (int idx in core)
        {
            int x = idx % grid.Nx, y = idx / grid.Nx % grid.Ny, z = idx / plane;
            if (x > 0) jet.Add(idx - 1);
            if (x < grid.Nx - 1) jet.Add(idx + 1);
            if (y > 0) jet.Add(idx - grid.Nx);
            if (y < grid.Ny - 1) jet.Add(idx + grid.Nx);
            if (z > 0) jet.Add(idx - plane);
            if (z < grid.Nz - 1) jet.Add(idx + plane);
        }
        return jet;
    }

    /// <summary>Where a conditioning unit reads its return temperature: the air cell one step upstream
    /// (room side, +Direction -- the return pulls along -Direction) of each return cell, skipping solids,
    /// device cells and <see cref="JetRegion"/>. The return cells themselves are driven, and on a coarse
    /// grid they exchange directly with an outlet a cell or two away, so they read the unit's own
    /// discharge; the cell the grille draws from reads the room. Warns when a user-placed return overlaps
    /// the jet region (it will short-circuit, and the reading then says so).</summary>
    static void SetGather(VoxelGrid grid, AirSource s, DeviceRegion d, Vector3 n, bool userPlaced)
    {
        if (d.Cells.Length <= d.OutletCount) return;
        var jet = JetRegion(grid, d);
        var own = new HashSet<int>(d.Cells);
        int plane = grid.Nx * grid.Ny;
        bool overlaps = false;
        var gather = new HashSet<int>();
        for (int i = d.OutletCount; i < d.Cells.Length; i++)
        {
            int idx = d.Cells[i];
            if (jet.Contains(idx)) overlaps = true;
            var p = grid.CellCenter(idx % grid.Nx, idx / grid.Nx % grid.Ny, idx / plane) + n * grid.Dx - grid.Origin;
            int x = (int)MathF.Floor(p.X / grid.Dx), y = (int)MathF.Floor(p.Y / grid.Dx), z = (int)MathF.Floor(p.Z / grid.Dx);
            if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny || z < 0 || z >= grid.Nz) continue;
            int up = grid.Index(x, y, z);
            if (grid.Cell[up] != VoxelGrid.Air || own.Contains(up) || jet.Contains(up)) continue;
            gather.Add(up);
        }
        d.GatherCells = gather.ToArray();
        if (userPlaced && overlaps)
            grid.Warnings.Add($"Device {s.Name}: the return grille sits inside the outlet's own jet, so the unit " +
                "re-ingests its cold discharge and under-reports its return temperature and duty - move " +
                "ReturnCenter clear of the outlet (the default puts it on the wall above)");
        if (gather.Count == 0)
            grid.Warnings.Add($"Device {s.Name}: no room air in front of the return grille; its temperature is " +
                "read at the grille itself");
    }

    static DeviceRegion Region(AirSource s, List<DeviceCell> cells, Vector3[] target, int outletCount, int layers, float q) =>
        new()
        {
            SourceId = s.Id, SourceName = s.Name, Kind = s.Device,
            Cells = cells.Select(c => c.Index).ToArray(), Target = target,
            OutletCount = outletCount, OutletLayers = Math.Max(1, layers), FlowRate = q,
            SupplyTemperature = s.SupplyTemperature,
        };

    /// <summary>Marches <see cref="DischargeProbe"/> metres along the extract's direction: a solid surface
    /// that close in front of it means the air has nowhere to go (the device will only stir the room), and
    /// the caller warns. Leaving the domain, or an open path that far, is fine. Used only to warn -- a far
    /// wall across the room is left alone, since the air can still get round it.</summary>
    const float DischargeProbe = 1.0f;

    static bool DischargeBlocked(VoxelGrid grid, Vector3 center, Vector3 n)
    {
        int steps = (int)MathF.Ceiling(DischargeProbe / grid.Dx);
        for (int k = 1; k <= steps; k++)
        {
            var p = center + n * (k * grid.Dx) - grid.Origin;
            int x = (int)MathF.Floor(p.X / grid.Dx), y = (int)MathF.Floor(p.Y / grid.Dx), z = (int)MathF.Floor(p.Z / grid.Dx);
            if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny || z < 0 || z >= grid.Nz) return false;
            if (grid.Cell[grid.Index(x, y, z)] == VoxelGrid.Solid) return true;
        }
        return false;
    }

    /// <summary>Air cells of an oriented patch, retried at offsets 0, +-1, +-2, +-3 cells along the axis --
    /// the same search (and the same reason) as <see cref="SourceCells"/>: a device mounted flush on a wall
    /// or ceiling is commonly given a centre that lands inside it.</summary>
    static List<DeviceCell> SearchPatch(VoxelGrid grid, Vector3 center, Vector3 n, Vector3 t, Vector3 b,
        float halfW, float halfH, float halfDepth, bool radial)
    {
        Span<int> offsets = stackalloc int[7] { 0, 1, -1, 2, -2, 3, -3 };
        foreach (int k in offsets)
        {
            var cells = CollectPatch(grid, center + n * (k * grid.Dx), n, t, b, halfW, halfH, halfDepth, radial);
            if (cells.Count > 0) return cells;
        }
        return new List<DeviceCell>();
    }

    static List<DeviceCell> CollectPatch(VoxelGrid grid, Vector3 center, Vector3 n, Vector3 t, Vector3 b,
        float halfW, float halfH, float halfDepth, bool radial)
    {
        var reach = Vector3.Abs(t)*halfW + Vector3.Abs(b)*halfH + Vector3.Abs(n)*halfDepth + new Vector3(grid.Dx);
        Vector3 lo = center - reach - grid.Origin;
        Vector3 hi = center + reach - grid.Origin;
        int x0 = Math.Clamp((int)MathF.Floor(lo.X / grid.Dx), 0, grid.Nx - 1);
        int x1 = Math.Clamp((int)MathF.Floor(hi.X / grid.Dx), 0, grid.Nx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y / grid.Dx), 0, grid.Ny - 1);
        int y1 = Math.Clamp((int)MathF.Floor(hi.Y / grid.Dx), 0, grid.Ny - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z / grid.Dx), 0, grid.Nz - 1);
        int z1 = Math.Clamp((int)MathF.Floor(hi.Z / grid.Dx), 0, grid.Nz - 1);

        var result = new List<DeviceCell>();
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int idx = grid.Index(x, y, z);
            if (grid.Cell[idx] != VoxelGrid.Air) continue;   // devices live in air; never carve a wall or a Fixed patch
            var c = grid.CellCenter(x, y, z) - center;
            float a = Vector3.Dot(c, t), bb = Vector3.Dot(c, b), e = Vector3.Dot(c, n);
            if (MathF.Abs(e) > halfDepth) continue;
            if (radial ? a * a + bb * bb > halfW * halfW : MathF.Abs(a) > halfW || MathF.Abs(bb) > halfH) continue;
            result.Add(new DeviceCell(idx, a, bb, (int)MathF.Round(e / grid.Dx)));
        }
        return result;
    }

    /// <summary>Drops device cells the flood fill has just sealed off (unreachable air becomes Solid), so a
    /// device can never drive a cell that is not in the solve.</summary>
    static void PruneDevices(VoxelGrid grid)
    {
        foreach (var d in grid.Devices)
        {
            var keptCells = new List<int>(d.Cells.Length);
            var keptTarget = new List<Vector3>(d.Cells.Length);
            int outlet = 0;
            for (int i = 0; i < d.Cells.Length; i++)
            {
                if (grid.Cell[d.Cells[i]] != VoxelGrid.Air) continue;
                if (i < d.OutletCount) outlet++;
                keptCells.Add(d.Cells[i]);
                keptTarget.Add(d.Target[i]);
            }
            // The kept outlet cells stay first: the loop above walks the original order, which already is.
            d.Cells = keptCells.ToArray();
            d.Target = keptTarget.ToArray();
            d.OutletCount = outlet;
            d.GatherCells = d.GatherCells.Where(i => grid.Cell[i] == VoxelGrid.Air).ToArray();
        }
        for (int i = grid.Devices.Count - 1; i >= 0; i--)
            if (grid.Devices[i].Cells.Length == 0)
            {
                grid.Warnings.Add($"Device {grid.Devices[i].SourceName}: no air at its location");
                grid.Devices.RemoveAt(i);
            }
    }

    // Ground layer (z=0) is solid; the lid (z=Nz-1) is fully air; the four outer walls of the domain
    // are air for z >= 1. The solver uses these as its far-field/ground boundaries.
    static void GroundAndLid(VoxelGrid grid)
    {
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
            grid.Cell[grid.Index(x, y, 0)] = VoxelGrid.Solid;

        int top = grid.Nz - 1;
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
            grid.Cell[grid.Index(x, y, top)] = VoxelGrid.Air;

        for (int z = 1; z < grid.Nz; z++)
        {
            for (int y = 0; y < grid.Ny; y++)
            {
                grid.Cell[grid.Index(0, y, z)] = VoxelGrid.Air;
                grid.Cell[grid.Index(grid.Nx - 1, y, z)] = VoxelGrid.Air;
            }
            for (int x = 0; x < grid.Nx; x++)
            {
                grid.Cell[grid.Index(x, 0, z)] = VoxelGrid.Air;
                grid.Cell[grid.Index(x, grid.Ny - 1, z)] = VoxelGrid.Air;
            }
        }
    }

    // Whole-building domain: seal off any Air/Fixed cell the outside cannot reach (wall interiors,
    // unanalysed storeys behind closed openings, sealed rooms). 6a: BFS over the LBM's own connectivity (<see cref="Linked"/>: the
    // D3Q19 links between LBM-fluid cells that are not cut) from the domain's outer ring, which GroundAndLid guarantees is air for
    // z >= 1, so a pocket between open sheets is sealed too. Cells never reached become Solid in both masks, at zero cost to the solve;
    // a reached-away Fixed cell also drops out of FixedCells with a Warning.
    static void FloodFill(VoxelGrid grid, Dictionary<int, string> fixedOwner, List<Room> rooms,
        bool domainFaces = false, bool grounded = true, List<AirSource>? sources = null)
    {
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
        var visited = new bool[grid.Count];
        var queue = new Queue<int>();

        void Seed(int x, int y, int z)
        {
            int idx = grid.Index(x, y, z);
            if (grid.Cell[idx] == VoxelGrid.Air && !visited[idx])
            {
                visited[idx] = true;
                queue.Enqueue(idx);
            }
        }

        int zSeed = domainFaces && !grounded ? 0 : 1;
        for (int z = zSeed; z < Nz; z++)
        {
            for (int y = 0; y < Ny; y++) { Seed(0, y, z); Seed(Nx - 1, y, z); }
            for (int x = 0; x < Nx; x++) { Seed(x, 0, z); Seed(x, Ny - 1, z); }
        }
        // A simulation box has six real faces, and the top or the bottom may be the only open one (the
        // whole-building domain gets its lid for free: GroundAndLid makes it air, joined to the side rings).
        if (domainFaces)
            for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++)
            {
                Seed(x, y, Nz - 1);
                if (!grounded) Seed(x, y, 0);
            }

        // What the outer ring reaches, before any room is seeded from the inside: a Return/Fan patch outside this set has no path to
        // the outdoors or to anything that has one (the sealed-room balance, BalanceSealedPatches). Kept only when a source patch or a
        // Diffuser exists.
        bool[]? outside = null;
        if (grid.FixedCells.Count > 0 || grid.Devices.Any(d => d.Kind == DeviceKind.Diffuser))
        {
            Drain(grid, visited, queue);
            outside = (bool[])visited.Clone();
        }

        // A room with every opening closed has no path back to the outer ring above, so the BFS above
        // would otherwise seal it (and any source patches in it) to Solid and it silently vanishes from
        // results. model.Rooms only ever holds rooms on analysed storeys (BuildingModel's contract), so
        // seed every Air cell whose centre lies inside one of their loops (even-odd) and z-range too.
        foreach (var room in rooms)
        for (int z = 0; z < Nz; z++)
        {
            float cz = grid.Origin.Z + (z + 0.5f) * grid.Dx;
            if (cz < room.ZMin || cz > room.ZMax) continue;
            for (int y = 0; y < Ny; y++)
            for (int x = 0; x < Nx; x++)
            {
                var c = grid.CellCenter(x, y, z);
                if (Analysis.InsideLoops(room.Loops, new Vector2(c.X, c.Y)))
                    Seed(x, y, z);
            }
        }

        Drain(grid, visited, queue);

        for (int i = 0; i < grid.Count; i++)
        {
            if (visited[i]) continue;
            if (grid.LbmAir.Length == grid.Count) grid.LbmAir[i] = false;
            if (grid.Cell[i] == VoxelGrid.Air) grid.Cell[i] = VoxelGrid.Solid;
            else if (grid.Cell[i] == VoxelGrid.Fixed)
            {
                grid.Cell[i] = VoxelGrid.Solid;
                grid.Warnings.Add($"Source {(fixedOwner.TryGetValue(i, out var name) ? name : "?")}: no air at its location");
            }
        }

        grid.FixedCells = grid.FixedCells.Where(fc => visited[fc.Index]).ToList();
        if (outside != null && sources != null) BalanceSealedPatches(grid, sources, rooms, outside, visited);
        PruneDevices(grid);
    }

    /// <summary>RY-05: the nearer-end rule of <see cref="VoxelGeometry.ConservativeMask"/> can close, in <see cref="VoxelGrid.Cell"/>, a passage up to 2 cells
    /// wide that the LBM keeps open (its cells stay LBM-fluid, <see cref="VoxelGrid.LbmAir"/>). The age solver reads Cell, so a room behind such a passage reads
    /// as sealed. One face-connectivity pass over Cell (the solver's own): a Cell region that holds no outer-ring or source cell and still has an uncut LBM
    /// link, directly or through LbmAir cells, to a cell outside it is the case. RQ-03: a room is named only when that region holds at least half of its Cell-air
    /// cells (the RW-03 naming rule; a small pocket falls to the sealed-volume text) and the region's LBM component reaches the outdoors or a source patch (otherwise
    /// the room has no air path at any cell size and keeps the sealed-room notes).</summary>
    internal static void NoteCellIsolatedRooms(VoxelGrid grid, BuildingModel model)
    {
        var rooms = model.Rooms;
        if (grid.LbmAir.Length != grid.Count || rooms.Count == 0) return;
        int Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, n = grid.Count, slab = Nx * Ny;
        var seen = new bool[n]; var cur = new bool[n];
        var queue = new Queue<int>();
        void Push(int i) { if (!seen[i] && grid.Cell[i] != VoxelGrid.Solid) { seen[i] = true; queue.Enqueue(i); } }
        void Grow(List<int>? members)
        {
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); members?.Add(i);
                int x = i % Nx, y = i / Nx % Ny, z = i / slab;
                if (x > 0) Push(i - 1);
                if (x < Nx - 1) Push(i + 1);
                if (y > 0) Push(i - Nx);
                if (y < Ny - 1) Push(i + Nx);
                if (z > 0) Push(i - slab);
                if (z < Nz - 1) Push(i + slab);
            }
        }
        for (int i = 0; i < n; i++)
        {
            int x = i % Nx, y = i / Nx % Ny, z = i / slab;
            if (grid.Cell[i] == VoxelGrid.Fixed || x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || z == 0 || z == Nz - 1) Push(i);
        }
        Grow(null);   // what the outdoors and the sources reach

        var names = new SortedSet<string>(StringComparer.Ordinal);
        bool[]? lbmOut = null;   // LBM-connected to the outer ring or a supply patch (RP-09: never a Return or Fan); built on the first candidate only
        bool[][]? masks = null;   // IMP-B (OF-1): each room's cells as its ACH counts them, outline plus band (Analysis.RoomMasks); built when a candidate region first needs them
        for (int s = 0; s < n; s++)
        {
            if (seen[s] || grid.Cell[s] == VoxelGrid.Solid) continue;
            var comp = new List<int>();
            Push(s); Grow(comp);
            foreach (int i in comp) cur[i] = true;
            var passage = new HashSet<int>(); var walk = new Queue<int>();
            bool linked = false;
            // Out of the region along uncut LBM links: onto a Cell-air cell of another region, or onto an LbmAir cell and on through those.
            bool Leaves(int i)
            {
                int x = i % Nx, y = i / Nx % Ny, z = i / slab;
                for (int k = 1; k < 19; k++)
                {
                    int j = Linked(grid, x, y, z, k);
                    if (j < 0 || cur[j]) continue;
                    if (grid.Cell[j] != VoxelGrid.Solid) return true;
                    if (passage.Add(j)) walk.Enqueue(j);
                }
                return false;
            }
            foreach (int i in comp) if (Leaves(i)) { linked = true; break; }
            while (!linked && walk.Count > 0) linked = Leaves(walk.Dequeue());
            foreach (int i in comp) cur[i] = false;
            if (!linked) continue;
            if (lbmOut == null)
            {
                lbmOut = new bool[n]; var q0 = new Queue<int>();
                // RP-09: the outdoors and the supply patches only; a Return or Fan patch is not a source of air, and a suite sealed at every quality level (BalanceSealedPatches) is not helped by a finer grid.
                var supply = new bool[n];
                foreach (var f in grid.FixedCells) if (f.Mode == 0) supply[f.Index] = true;
                for (int i = 0; i < n; i++)
                {
                    int x = i % Nx, y = i / Nx % Ny, z = i / slab;
                    if (grid.Cell[i] != VoxelGrid.Solid && !lbmOut[i] && (supply[i] || x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || z == 0 || z == Nz - 1)) { lbmOut[i] = true; q0.Enqueue(i); }
                }
                Drain(grid, lbmOut, q0);
            }
            if (!lbmOut[comp[0]]) continue;
            masks ??= Analysis.RoomMasks(grid, model, out _);
            for (int r = 0; r < rooms.Count; r++)
            {
                int count = 0;
                foreach (int i in comp) if (masks[r][i]) count++;
                if (count == 0) continue;
                int total = 0;
                foreach (bool b in masks[r]) if (b) total++;
                if (2 * count >= total) names.Add(rooms[r].Name);
            }
        }
        foreach (var name in names)
            grid.Warnings.Add($"{name} connects only through a gap narrower than about 2 cells at this cell size; its age of air cannot be computed and shows as sealed. Use a finer quality level or widen the gap.");
    }

    /// <summary>The BFS of <see cref="FloodFill"/>: grows <paramref name="visited"/> over the LBM's links (<see cref="Linked"/>) from the queue.</summary>
    static void Drain(VoxelGrid grid, bool[] visited, Queue<int> queue)
    {
        int Nx = grid.Nx, Ny = grid.Ny;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int x = cur % Nx, y = (cur / Nx) % Ny, z = cur / (Nx * Ny);
            for (int k = 1; k < 19; k++)
            {
                int nIdx = Linked(grid, x, y, z, k);
                if (nIdx < 0 || visited[nIdx]) continue;
                visited[nIdx] = true;
                queue.Enqueue(nIdx);
            }
        }
    }

    /// <summary>RW-01 / RW-04 / RW-05. Supply and Return patches are both flux-controlled, so an air space the outer ring cannot reach
    /// (<paramref name="outside"/>: no open boundary, no path to another space that has one) with any supply / extract mismatch has no
    /// pressure anchor: the controllers would pump mass in or out for ever and the room's density would drift. For such a space every
    /// patch stays flux-controlled and the extract targets are scaled so the total extract equals the total supply
    /// (<see cref="VoxelGrid.PatchFlowOverride"/>; the patch's wall velocity is scaled with it). A space with supply but no extract, or extract
    /// but no supply, cannot move air: both go to 0 (the patch cells become plain air). A room note names the room and the balanced flow.
    /// A Diffuser device in such a space brings in no outdoor air (<see cref="VoxelGrid.NoFreshAir"/>).</summary>
    static void BalanceSealedPatches(VoxelGrid grid, List<AirSource> sources, List<Room> rooms, bool[] outside, bool[] visited)
    {
        var slotOf = new Dictionary<int, int>();
        for (int k = 0; k < grid.FixedCells.Count; k++) slotOf[grid.FixedCells[k].Index] = k;
        var done = new HashSet<int>();
        var assigned = new HashSet<int>();                     // diffuser regions already placed in a space
        var zeroed = new HashSet<int>();                       // patch cells of spaces that cannot move air
        int Nx = grid.Nx, Ny = grid.Ny;

        var seeds = grid.FixedCells.Select(fc => fc.Index)
            .Concat(grid.Devices.Where(d => d.Kind == DeviceKind.Diffuser && d.Cells.Length > 0).Select(d => d.Cells[0])).ToList();
        foreach (int start in seeds)
        {
            if (outside[start] || !visited[start] || done.Contains(start)) continue;
            // This seed's enclosed air space: LBM-fluid cells the outer ring did not reach, linked to it (6a: the seal's connectivity).
            var slots = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start); done.Add(start);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (slotOf.TryGetValue(cur, out int slot)) slots.Add(slot);
                int x = cur % Nx, y = (cur / Nx) % Ny, z = cur / (Nx * Ny);
                for (int k = 1; k < 19; k++)
                {
                    int nIdx = Linked(grid, x, y, z, k);
                    if (nIdx < 0 || outside[nIdx] || !visited[nIdx] || done.Contains(nIdx)) continue;
                    done.Add(nIdx); queue.Enqueue(nIdx);
                }
            }

            // RW-05: Diffusers whose outlet is in this space.
            var diffusers = new SortedSet<string>(StringComparer.Ordinal); var diffCells = new List<int>(); float diffFlow = 0f;
            for (int j = 0; j < grid.Devices.Count; j++)
            {
                var dv = grid.Devices[j];
                if (dv.Kind != DeviceKind.Diffuser || dv.Cells.Length == 0 || assigned.Contains(j) || !done.Contains(dv.Cells[0])) continue;
                assigned.Add(j);
                if (dv.SourceIndex >= 0) grid.NoFreshAir.Add(dv.SourceIndex);
                diffusers.Add(dv.SourceName); diffCells.Add(dv.Cells[0]);
                if (dv.SourceIndex >= 0 && dv.SourceIndex < sources.Count && sources[dv.SourceIndex].Enabled && sources[dv.SourceIndex].Kind == SourceKind.Supply) diffFlow += sources[dv.SourceIndex].FlowRate;
            }

            // Set flows of the distinct sources in this space (a source owns many cells): supply in, extract out; a Fan moves no net mass.
            var supply = new HashSet<int>(); var extract = new HashSet<int>();
            foreach (int slot in slots)
            {
                var fc = grid.FixedCells[slot];
                if (fc.Supply < 0 || fc.Supply >= sources.Count) continue;
                if (fc.Mode == 0) supply.Add(fc.Supply); else if (fc.Mode == 1) extract.Add(fc.Supply);
            }
            float s = supply.Sum(i => sources[i].FlowRate), r = extract.Sum(i => sources[i].FlowRate);
            bool patches = supply.Count + extract.Count > 0;
            if (patches && s > 0f && r > 0f && s == r) patches = false;   // already equal: nothing to do (RX-10c: any mismatch is scaled, no tolerance; a 0.09 % drift is 1.4 %/h in a 25 m3 room)
            if (!patches && diffusers.Count == 0) continue;

            // The rooms the space's patches sit in, for the notes.
            var roomNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (int cell in slots.Select(sl => grid.FixedCells[sl].Index).Concat(diffCells))
            {
                var c = grid.CellCenter(cell % Nx, (cell / Nx) % Ny, cell / (Nx * Ny));
                foreach (var room in rooms)
                    if (c.Z >= room.ZMin && c.Z <= room.ZMax && Analysis.InsideLoops(room.Loops, new Vector2(c.X, c.Y))) roomNames.Add(room.Name);
            }
            string where = roomNames.Count > 0 ? string.Join(", ", roomNames) : "(no room outline: " + string.Join(", ", supply.Concat(extract).Select(i => sources[i].Name)) + ")";

            if (patches)
            {
                float z = s > 0f && r > 0f ? s : 0f;           // the flow both sides run at: the supply total, or 0 when one side is missing
                float scale = r > 0f ? z / r : 0f;
                foreach (int i in extract) grid.PatchFlowOverride[i] = sources[i].FlowRate * scale;
                if (z == 0f) foreach (int i in supply) grid.PatchFlowOverride[i] = 0f;
                foreach (int slot in slots)
                {
                    var fc = grid.FixedCells[slot];
                    if (fc.Supply < 0 || fc.Supply >= sources.Count) continue;
                    if (z == 0f && fc.Mode != 2) zeroed.Add(fc.Index);
                    else if (fc.Mode == 1) grid.FixedCells[slot] = fc with { Velocity = fc.Velocity * scale };
                }
                // RY-06: scaling stays on any s != r, but float rounding of equal typed flows (6 x 30 vs 180 m3/h) is not worth a note
                if (MathF.Abs(s - r) > 1e-4f * MathF.Max(s, r)) grid.Warnings.Add(SealedBalanceNote(where, s, r, z, diffFlow));
            }
            if (diffusers.Count > 0)
                grid.Warnings.Add($"Room {where}: {string.Join(", ", diffusers)} supplies no outdoor air in this sealed room, so it counts as recirculating air. Real rooms need a leakage path or transfer opening — add one for a realistic result.");
        }

        if (zeroed.Count > 0)
        {
            foreach (int i in zeroed) grid.Cell[i] = VoxelGrid.Air;
            grid.FixedCells = grid.FixedCells.Where(fc => !zeroed.Contains(fc.Index)).ToList();
        }
    }

    /// <summary>RW-01 room note for a sealed room whose supply and extract differ (flows in m3/h); the App maps it to plain words.</summary>
    public static string SealedBalanceNote(string room, float supply, float extract, float balanced, float diffuser = 0f) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Room {0}: Supply ({1:0} m³/h) and extract ({2:0} m³/h) do not balance in this sealed room; WindFlow balanced them at {3:0} m³/h. " +
            "Real rooms need a leakage path or transfer opening — add one for a realistic result.", room, supply * 3600f, extract * 3600f, balanced * 3600f) +
        (diffuser > 0f ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
            " A Diffuser in this room moves {0:0} m³/h but adds no air, so it is not part of the supply here.", diffuser * 3600f) : "");
}
