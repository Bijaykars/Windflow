// Regression checks for the 13 audited defects (CLAUDE_FIX_PROMPT.md section 3) beyond the quick cases in
// AuditChecks: closed-solid overlap rules (#1), opening footprint rules (#3), wall-link parity (#2), orifice head
// (#5), live updates (#7), convergence lead (#9), open Z faces (#11), room-flux faces (#12), running means.
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --fixes      (or as part of the default run)
//
// Mesh contract the geometry checks pin down (Voxelizer.FillObstacleVolumes / VoxelGeometry.cs):
//   * A "closed shell" is an edge-connected triangle component whose every edge is used as often in one direction as in
//     the other (batch 6a: multiplicity counts, so abutting boxes merged into one mesh are closed). Open sheets are never
//     filled: they are cut links only.
//   * A cell is solid if it is inside ANY closed shell (union), so overlapping boxes, L-corners and wall/slab
//     overlaps never cancel.
//   * A shell wound the opposite way that lies wholly inside another shell is that shell's CAVITY (hollow room,
//     slab hole). Any other reversed shell is just solid too. Same-sense nested shells union.
//   * Separate Mesh objects always union.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static partial class FixChecks
{
    const float Dx = .125f;
    static int failures;

    static void Check(string name, bool pass, string detail = "")
    {
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name} {detail}");
        CheckLog.Record(pass ? "PASS" : "FAIL", name, detail);
        if (!pass) failures++;
    }
    static void Note(string name, string detail) { Console.WriteLine($"[NOTE] {name} {detail}"); CheckLog.Record("NOTE", name, detail); }

    /// <summary>Runs every fix check and returns the number of failures (0 = all passed).</summary>
    public static int Run()
    {
        failures = 0;
        Console.WriteLine("== fix checks ==");
        GeometryChecks();
        OpeningFootprintChecks();
        SampleHouseNumbers();
        SolverChecks();
        SurfaceConvectionChecks();
        ThroatNormalOnlyCheck();
        FluxConservationChecks();
        OpeningWallChecks();
        OnlineAgeChecks();
        ImpCChecks();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL FIX CHECKS PASSED" : $"{failures} FIX CHECK(S) FAILED");
        return failures;
    }

    /// <summary>RP-06 (wall thickness from the geometry), RP-07 (the coarse Cd notes of an opening a fine box answers for are dropped), RP-09 (a Return patch is no source
    /// of air for the "connects only through a gap" note).</summary>
    static void OpeningWallChecks()
    {
        // RP-06: a window with the 0.1 m Depth fallback (not hosted by a wall) in a 0.3 m wall: t is the wall's 0.3 m. With no wall at all, the Depth stands.
        BuildingModel Wall(bool wall)
        {
            var m = new BuildingModel { Name = "rp06", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
            m.Obstacles.Add(Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f)));
            if (wall) m.Obstacles.Add(Box(new Vector3(2.85f, 0f, 0f), new Vector3(3.15f, 6f, 3f)));
            m.Openings.Add(new Opening { Id = "w", Name = "w", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(3f, 3f, 1.05f), Normal = Vector3.UnitX, Width = 0.9f, Height = 1.2f, Depth = 0.1f });
            return m;
        }
        var domain = new DomainBox(Vector3.Zero, new Vector3(6f, 6f, 3f));
        var mw = Wall(true); var gw = Voxelizer.Build(mw, 6912, 1, domain, sealUnreachable: false, minDx: 0.1f);
        var mn = Wall(false); var gn = Voxelizer.Build(mn, 6912, 1, domain, sealUnreachable: false, minDx: 0.1f);
        float tw = LbmSolver.OpeningWallCells(mw.Openings[0], gw.Dx) * gw.Dx, tn = LbmSolver.OpeningWallCells(mn.Openings[0], gn.Dx) * gn.Dx;
        Check("RP-06 a window whose Depth fell back to 0.1 m in a 0.3 m wall reads t from the wall (0.3 m); with no wall found its Depth stands",
            MathF.Abs(gw.Dx - 0.25f) < 1e-3f && MathF.Abs(mw.Openings[0].MeasuredDepth - 0.3f) < 0.01f && MathF.Abs(tw - 0.3f) < 0.01f && mn.Openings[0].MeasuredDepth == 0f && MathF.Abs(tn - 0.1f) < 1e-4f,
            $"Dx {gw.Dx * 1000:0} mm · measured {mw.Openings[0].MeasuredDepth:0.000} m, t = {tw:0.000} m · no wall: measured {mn.Openings[0].MeasuredDepth:0.000} m, t = {tn:0.000} m");

        // RP-07: the notes of the opening a fine box answers for go; another opening's stay.
        var gv = new VoxelGrid { Nx = 1, Ny = 1, Nz = 1, Dx = 1f, Cell = new byte[1] };
        gv.Warnings.Add("Opening W1: the lattice resolves this opening at Cd 0.58, below the requested 0.61; flow here may be about 4 % low");
        gv.Warnings.Add("Opening W1: the opening's discharge coefficient is uncertain by about ±26 % at this cell size; a finer quality level reduces it");
        gv.Warnings.Add("Opening W1: wall thickness taken from the geometry (0.30 m; the drawn depth says 0.10 m)");
        gv.Warnings.Add("Opening W2: the lattice resolves this opening at Cd 0.55, below the requested 0.61; flow here may be about 9 % low");
        gv.Warnings.Add("Opening W1: effective Cd outside 0.42-0.8 - treat its flow rate with care");
        Analysis.DropOpeningCdNotes(gv, new Opening { Id = "w1", Name = "W1" });
        Check("RP-07 the coarse Cd, uncertainty and wall notes of an opening the fine box answers for are dropped; another opening's note and this opening's other notes stay",
            gv.Warnings.Count == 2 && gv.Warnings[0].StartsWith("Opening W2: ", StringComparison.Ordinal) && gv.Warnings[1].Contains("treat its flow rate", StringComparison.Ordinal), $"left: [{string.Join(" | ", gv.Warnings)}]");

        // RP-09: two sealed rooms joined by a 0.5 m gap that the conservative mask closes in Cell (LbmAir: Solid in Cell) but the LBM keeps open. A Supply patch in the
        // left room is a source of air, so the right room is named; a Return patch is not (BalanceSealedPatches zeroes it in a sealed suite, a finer grid cannot help), so it is not.
        string Gap(byte mode)
        {
            var g = new VoxelGrid { Nx = 16, Ny = 8, Nz = 3, Dx = 0.25f, Origin = Vector3.Zero, Cell = new byte[16 * 8 * 3] };
            g.LbmAir = new bool[g.Count];
            for (int z = 0; z < 3; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 16; x++)
            {
                int i = g.Index(x, y, z);
                bool shell = x == 0 || x == 15 || y == 0 || y == 7 || z == 0 || z == 2;
                bool part = x == 8;
                g.Cell[i] = shell || part ? VoxelGrid.Solid : VoxelGrid.Air;
                if (part && !shell && (y == 3 || y == 4)) g.LbmAir[i] = true;
            }
            int f = g.Index(2, 3, 1); g.Cell[f] = VoxelGrid.Fixed; g.FixedCells.Add(new FixedCell(f, new Vector3(mode == 0 ? 1f : -1f, 0, 0), 0, mode));
            var right = new Room { Id = "right", Name = "Right", Level = "L1", ZMin = 0f, ZMax = 1f };
            right.Loops.Add([new Vector2(2.25f, 0.25f), new Vector2(3.75f, 0.25f), new Vector2(3.75f, 1.75f), new Vector2(2.25f, 1.75f)]);
            Voxelizer.NoteCellIsolatedRooms(g, new BuildingModel { Rooms = [right] });   // IMP-B: takes the model, for the merged room masks
            return string.Join(" | ", g.Warnings.Where(w => w.Contains("connects only through a gap", StringComparison.Ordinal)));
        }
        string withSupply = Gap(0), withReturn = Gap(1);
        Check("RP-09 a room behind a gap that closes in Cell is named when a Supply patch feeds the other room, not when only a Return patch does",
            withSupply.StartsWith("Right connects only through a gap", StringComparison.Ordinal) && withReturn.Length == 0, $"supply: [{withSupply}] · return: [{withReturn}]");
    }

    /// <summary>Opening-throat loss acts on the through-flow only: in a flow at 45 deg to the opening normal (with a
    /// vertical part too) the transverse/vertical force is zero (momentum not damped beyond the unforced case), and the
    /// normal force equals the isotropic -c rho |u| u of straight-through flow evaluated on u_n.</summary>
    static void ThroatNormalOnlyCheck()
    {
        const float c = 0.3f, rho = 1f, U = 0.04f;
        float tx = 0.6f, ty = 0.8f;                               // opening normal (any horizontal direction)
        float s = MathF.Sqrt(0.5f);
        // oblique: 45 deg in the horizontal between the normal and its perpendicular, plus a vertical part
        float ux = U * (s * tx - s * ty), uy = U * (s * ty + s * tx), uz = 0.5f * U;
        var (fx, fy, fz) = LbmSolver.DeviceForceProbe(rho, ux, uy, uz, tx, ty, 0f, -c);
        float un = ux * tx + uy * ty, ut = -ux * ty + uy * tx;
        float fn = fx * tx + fy * ty, ft = -fx * ty + fy * tx;
        float isoT = c * rho * MathF.Sqrt(ux * ux + uy * uy + uz * uz) * ut;   // what the isotropic form would remove
        float isoZ = c * rho * MathF.Sqrt(ux * ux + uy * uy + uz * uz) * uz;
        bool transverse = MathF.Abs(ft) <= 0.02f * isoT && MathF.Abs(fz) <= 0.02f * isoZ;
        // straight through: u = u_n, force must equal isotropic -c rho |u| u
        var (sx, sy, sz) = LbmSolver.DeviceForceProbe(rho, U * tx, U * ty, 0f, tx, ty, 0f, -c);
        float iso = -c * rho * U * U;
        bool straight = MathF.Abs(sx - iso * tx) <= 1e-4f * MathF.Abs(iso) && MathF.Abs(sy - iso * ty) <= 1e-4f * MathF.Abs(iso) && sz == 0f;
        bool normal = MathF.Abs(fn + c * rho * MathF.Abs(un) * un) <= 1e-4f * c * un * un;
        Check("F14 opening-throat loss is normal-only (oblique flow: transverse+vertical undamped, normal = isotropic form on u_n; straight-through = isotropic)",
            transverse && straight && normal, $"ft={ft:E2} (iso {-isoT:E2}) fz={fz:E2} (iso {-isoZ:E2}) fn={fn:E3} vs {-c * un * un:E3} straight_ratio={sx / (iso * tx):F6}");
    }

    // ---- mesh helpers ----------------------------------------------------------------------------------------

    static readonly int[] BoxIndices = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];

    static Mesh Box(Vector3 a, Vector3 b)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        return new() { Vertices = v, Indices = (int[])BoxIndices.Clone() };
    }

    /// <summary>Box with half extents rotated about +Z by <paramref name="degrees"/> around its centre.</summary>
    static Mesh RotatedBox(Vector3 centre, Vector3 half, float degrees)
    {
        var r = Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180f);
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
            v[i] = centre + Vector3.Transform(new(((i & 1) == 0 ? -1 : 1) * half.X, ((i & 2) == 0 ? -1 : 1) * half.Y, ((i & 4) == 0 ? -1 : 1) * half.Z), r);
        return new() { Vertices = v, Indices = (int[])BoxIndices.Clone() };
    }

    static Mesh Reversed(Mesh m)
    {
        var idx = (int[])m.Indices.Clone();
        for (int k = 0; k < idx.Length; k += 3) (idx[k], idx[k + 1]) = (idx[k + 1], idx[k]);
        return new() { Vertices = m.Vertices, Indices = idx };
    }

    static Mesh Merge(params Mesh[] parts)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var p in parts) { int o = v.Count; v.AddRange(p.Vertices); idx.AddRange(p.Indices.Select(i => i + o)); }
        return new() { Vertices = v.ToArray(), Indices = idx.ToArray() };
    }

    /// <summary>Closed square-annulus prism (a slab with a rectangular hole): one genus-1 shell, outward normals.</summary>
    static Mesh SlabWithHole(float x0, float y0, float x1, float y1, float hx0, float hy0, float hx1, float hy1, float z0, float z1)
    {
        Vector2[] o = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], h = [new(hx0, hy0), new(hx1, hy0), new(hx1, hy1), new(hx0, hy1)];
        var v = new List<Vector3>();
        foreach (var p in o) v.Add(new(p.X, p.Y, z0));   // 0..3
        foreach (var p in o) v.Add(new(p.X, p.Y, z1));   // 4..7
        foreach (var p in h) v.Add(new(p.X, p.Y, z0));   // 8..11
        foreach (var p in h) v.Add(new(p.X, p.Y, z1));   // 12..15
        var idx = new List<int>();
        void Quad(int a, int b, int c, int d) { idx.AddRange([a, b, c, a, c, d]); }
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            Quad(i, j, 4 + j, 4 + i);               // outer side, outward
            Quad(8 + i, 12 + i, 12 + j, 8 + j);     // hole side, facing the hole
            Quad(4 + i, 4 + j, 12 + j, 12 + i);     // top annulus, +Z
            Quad(i, 8 + i, 8 + j, j);               // bottom annulus, -Z
        }
        return new() { Vertices = v.ToArray(), Indices = idx.ToArray() };
    }

    static VoxelGrid Build(BuildingModel m) => Voxelizer.Build(m, 64000, domain: new(Vector3.Zero, new(5)), sealUnreachable: false);
    static BuildingModel Model(params Mesh[] obstacles) { var m = new BuildingModel { GroundZ = -10 }; m.Obstacles.AddRange(obstacles); return m; }

    static float BoxDist(Vector3 p, Vector3 lo, Vector3 hi)
    {
        var q = Vector3.Abs(p - (lo + hi) / 2) - (hi - lo) / 2;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0);
    }

    static float RotatedBoxDist(Vector3 p, Vector3 centre, Vector3 half, float degrees)
    {
        var r = Matrix4x4.CreateRotationZ(-degrees * MathF.PI / 180f);
        var q = Vector3.Abs(Vector3.Transform(p - centre, r)) - half;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0);
    }

    /// <summary>Cells predicted inside by a signed distance that stayed Air (a hole/cancellation), and cells more than
    /// one cell outside that came out Solid (an over-fill). Border cells are skipped for the second test: the box
    /// face closing rule may legitimately solidify them.</summary>
    static (int Missed, int Overfilled, int Solid) Compare(VoxelGrid g, Func<Vector3, float> sdf)
    {
        int missed = 0, over = 0, solid = 0;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            bool s = g.Cell[g.Index(x, y, z)] == VoxelGrid.Solid; if (s) solid++;
            float d = sdf(g.CellCenter(x, y, z));
            if (d < -1e-3f && !s) missed++;
            bool border = x == 0 || y == 0 || z == 0 || x == g.Nx - 1 || y == g.Ny - 1 || z == g.Nz - 1;
            if (d > 1.0f * g.Dx && s && !border) over++;
        }
        return (missed, over, solid);
    }

    static void GeometryChecks()
    {
        void Case(string name, BuildingModel m, Func<Vector3, float> sdf, string why)
        {
            var g = Build(m); var (missed, over, solid) = Compare(g, sdf);
            Check(name, missed == 0 && over == 0, $"{why}: missed={missed} overfilled={over} solid={solid}");
        }

        var a = (lo: new Vector3(1), hi: new Vector3(3)); var b = (lo: new Vector3(2), hi: new Vector3(4));
        Case("F1a two overlapping closed boxes: overlap and both bodies are solid",
            Model(Box(a.lo, a.hi), Box(b.lo, b.hi)),
            p => MathF.Min(BoxDist(p, a.lo, a.hi), BoxDist(p, b.lo, b.hi)), "union");
        // Same with the second box reversed (a Revit export with mixed winding): still solid, not subtracted.
        Case("F1a2 overlapping boxes, second one wound inside-out: still a union",
            Model(Box(a.lo, a.hi), Reversed(Box(b.lo, b.hi))),
            p => MathF.Min(BoxDist(p, a.lo, a.hi), BoxDist(p, b.lo, b.hi)), "union");
        // Both in ONE mesh object (the exporter merged them).
        Case("F1a3 overlapping boxes merged into one Mesh: union",
            Model(Merge(Box(a.lo, a.hi), Box(b.lo, b.hi))),
            p => MathF.Min(BoxDist(p, a.lo, a.hi), BoxDist(p, b.lo, b.hi)), "union");

        var w1 = (lo: new Vector3(.5f, .5f, .5f), hi: new Vector3(4.5f, .75f, 3.5f)); var w2 = (lo: new Vector3(.5f, .5f, .5f), hi: new Vector3(.75f, 4.5f, 3.5f));
        Case("F1b L-corner of two overlapping wall boxes: corner column is solid",
            Model(Box(w1.lo, w1.hi), Box(w2.lo, w2.hi)),
            p => MathF.Min(BoxDist(p, w1.lo, w1.hi), BoxDist(p, w2.lo, w2.hi)), "union");

        var wall = (lo: new Vector3(2, .5f, .5f), hi: new Vector3(2.25f, 4.5f, 3)); var slab = (lo: new Vector3(.5f, .5f, 2.5f), hi: new Vector3(4.5f, 4.5f, 2.75f));
        Case("F1c wall box poking through a slab box: crossing is solid, no cancelled pocket",
            Model(Box(wall.lo, wall.hi), Box(slab.lo, slab.hi)),
            p => MathF.Min(BoxDist(p, wall.lo, wall.hi), BoxDist(p, slab.lo, slab.hi)), "union");

        // Hollow room shell (outer outward, inner reversed) with a box standing in the cavity.
        var outer = Box(new(.25f), new(4.75f)); var inner = Reversed(Box(new(1), new(4)));
        var shell = Merge(outer, inner); var furniture = (lo: new Vector3(2, 2, 1.25f), hi: new Vector3(3, 3, 2.25f));
        float ShellDist(Vector3 p) => MathF.Max(BoxDist(p, new(.25f), new(4.75f)), -BoxDist(p, new(1), new(4)));
        Case("F1d box nested in a hollow room shell (separate Mesh): cavity stays air, box and shell solid",
            Model(shell, Box(furniture.lo, furniture.hi)),
            p => MathF.Min(ShellDist(p), BoxDist(p, furniture.lo, furniture.hi)), "cavity");
        Case("F1d2 box nested in a hollow room shell (same Mesh): same result",
            Model(Merge(outer, inner, Box(furniture.lo, furniture.hi))),
            p => MathF.Min(ShellDist(p), BoxDist(p, furniture.lo, furniture.hi)), "cavity");
        var gShell = Build(Model(shell, Box(furniture.lo, furniture.hi)));
        Check("F1d3 room air inside the shell is Air away from the box", !gShell.IsSolid(gShell.Index(12, 12, 12)) && !gShell.IsSolid(gShell.Index(12, 30, 20)) && gShell.IsSolid(gShell.Index(20, 20, 12)));

        // Rotated wall overlapping an axis-aligned wall.
        var rc = new Vector3(2.5f, 2.5f, 1.75f); var rh = new Vector3(1.5f, .15f, 1.25f); var ax = (lo: new Vector3(2.25f, .5f, .5f), hi: new Vector3(2.5f, 3.5f, 3));
        Case("F1e rotated (30 deg) wall overlapping an axis-aligned wall: union, no staircase gaps",
            Model(RotatedBox(rc, rh, 30), Box(ax.lo, ax.hi)),
            p => MathF.Min(RotatedBoxDist(p, rc, rh, 30), BoxDist(p, ax.lo, ax.hi)), "union");

        // Walls larger than the domain: the clip must not hollow or drop the part inside.
        var cw1 = (lo: new Vector3(-2, 2, -1), hi: new Vector3(7, 2.25f, 7))
            ; var cw2 = (lo: new Vector3(-2, 3.5f, .5f), hi: new Vector3(3, 3.75f, 3));
        Case("F1f walls cut by the domain boundary (spanning it / ending inside it): solid through the clip",
            Model(Box(cw1.lo, cw1.hi), Box(cw2.lo, cw2.hi)),
            p => MathF.Min(BoxDist(p, cw1.lo, cw1.hi), BoxDist(p, cw2.lo, cw2.hi)), "clipped union");

        // Two windows in one thick wall must not connect through the wall core.
        var thick = Box(new(2, .125f, .125f), new(2.5f, 4.875f, 4.875f));
        var m7 = Model(thick);
        foreach (float wy in new[] { 1.5f, 3.5f })
            m7.Openings.Add(new() { Id = "w" + wy, Center = new(2.25f, wy, 2.5f), Normal = Vector3.UnitX, Width = 1, Height = 1, Depth = .5f });
        var g7 = Build(m7);
        // Wall interior columns are x centres 2.0625 .. 2.4375 (indices 16..19).
        int airInWall = 0; for (int z = 0; z < g7.Nz; z++) for (int y = 0; y < g7.Ny; y++) for (int x = 16; x <= 19; x++) if (!g7.IsSolid(g7.Index(x, y, z)) && y > 1 && y < 38 && z > 1 && z < 38) airInWall++;
        var seed = g7.Index(17, 12, 20); var reach = new HashSet<int> { seed }; var todo = new Stack<int>(); todo.Push(seed);
        while (todo.Count > 0)
        {
            int i = todo.Pop(); int x = i % g7.Nx, y = i / g7.Nx % g7.Ny, z = i / (g7.Nx * g7.Ny);
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (nx < 16 || nx > 19 || ny < 0 || ny >= g7.Ny || nz < 0 || nz >= g7.Nz) continue;   // stay inside the wall thickness
                int j = g7.Index(nx, ny, nz); if (g7.IsSolid(j) || !reach.Add(j)) continue; todo.Push(j);
            }
        }
        bool otherReached = reach.Contains(g7.Index(17, 28, 20));
        Check("F3x two windows in one 4-cell-thick wall do not communicate through the wall core",
            airInWall == 2 * 8 * 8 * 4 && !otherReached && g7.IsSolid(g7.Index(17, 20, 20)) && g7.IsSolid(g7.Index(18, 20, 20)),
            $"air cells in wall={airInWall} (expect {2 * 8 * 8 * 4}) otherWindowReachedThroughWall={otherReached}");

        // Slab with a rectangular hole: genus-1 shell and the four-strip form the sample house uses.
        var hole = SlabWithHole(.5f, .5f, 4.5f, 4.5f, 2, 2, 3, 3, 2.5f, 2.75f);
        float HoleDist(Vector3 p) => MathF.Max(BoxDist(p, new(.5f, .5f, 2.5f), new(4.5f, 4.5f, 2.75f)), -BoxDist(p, new(2, 2, -9), new(3, 3, 9)));
        Case("F1g slab with a hole (one closed genus-1 shell): hole stays open, slab fully solid, no horizontal duct",
            Model(hole), HoleDist, "annulus");
        Mesh S(float x0, float y0, float x1, float y1) => Box(new(x0, y0, 2.5f), new(x1, y1, 2.75f));
        Case("F1g2 slab with a hole built from four overlapping-free strips",
            Model(S(.5f, .5f, 4.5f, 2), S(.5f, 3, 4.5f, 4.5f), S(.5f, 2, 2, 3), S(3, 2, 4.5f, 3)), HoleDist, "strips");
        var gh = Build(Model(hole));
        Check("F1g3 hole is a vertical shaft only: air above, in and below the hole, solid all round it at slab height",
            !gh.IsSolid(gh.Index(20, 20, 18)) && !gh.IsSolid(gh.Index(20, 20, 20)) && !gh.IsSolid(gh.Index(20, 20, 24)) &&
            gh.IsSolid(gh.Index(14, 20, 20)) && gh.IsSolid(gh.Index(26, 20, 20)) && gh.IsSolid(gh.Index(20, 14, 20)) && gh.IsSolid(gh.Index(20, 26, 20)));

        // Open sheets are barriers, never fill.
        var sheet = new Mesh { Vertices = [new(1.15625f, 0, 0), new(1.15625f, 5, 0), new(1.15625f, 0, 5), new(1.15625f, 5, 5)], Indices = [0, 1, 2, 1, 3, 2] };
        var gs = Build(Model(sheet));
        bool barrier = true; for (int z = 3; z < 37; z++) for (int y = 3; y < 37; y++) barrier &= gs.IsSolid(gs.Index(9, y, z));
        Check("F1h open sheet stays a one-cell conservative barrier and does not fill either side",
            barrier && !gs.IsSolid(gs.Index(6, 20, 20)) && !gs.IsSolid(gs.Index(13, 20, 20)));
        // Five sheets of a box with no lid: unmatched edges, so no fill; the interior stays air.
        var open5 = Box(new(1), new(4)); var idx5 = new List<int>(); for (int k = 0; k < open5.Indices.Length; k += 3) if (k != 6 && k != 9) idx5.AddRange([open5.Indices[k], open5.Indices[k + 1], open5.Indices[k + 2]]);
        open5 = new() { Vertices = open5.Vertices, Indices = idx5.ToArray() };
        var g5 = Build(Model(open5));
        Check("F1h2 open-topped box (lid missing) is not filled", !g5.IsSolid(g5.Index(20, 20, 16)));
    }

    // ---- #3 opening footprint ----------------------------------------------------------------------------------

    static int AirInWall(VoxelGrid g, int x0, int x1)
    {
        // P12-01 (batch 6a): the carve is counted on the LBM (cell-centre) mask; Cell also marks the near end of a link that clips the hole's edge.
        int n = 0; for (int z = 2; z < g.Nz - 2; z++) for (int y = 2; y < g.Ny - 2; y++) for (int x = x0; x <= x1; x++) { int i = g.Index(x, y, z); if (!g.IsSolid(i) || g.LbmAir.Length == g.Count && g.LbmAir[i]) n++; }
        return n;
    }

    static void OpeningFootprintChecks()
    {
        Mesh thick() => Box(new(2, .125f, .125f), new(2.5f, 4.875f, 4.875f));
        (int Throat, int Air, int TieY, int TieZ) Carve(float w, float h, float dy, float dz, Vector3 normal)
        {
            var o = new Opening { Id = "o", Center = new(2.25f, 2.5f + dy, 2.5f + dz), Normal = normal, Width = w, Height = h, Depth = .5f };
            var g = Build(new BuildingModel { GroundZ = -10, Obstacles = { thick() }, Openings = { o } });
            // RP-03: an axis whose hole edges lie exactly on cell centres carves both edge cells (closed footprint rule: mirror-symmetric, cut at q ~ 0), one cell more across than nW.
            // RP follow-up 4 (rule 3): the throat now counts by the same closed rule, so it matches the carve (before: the half-open throat counted nW).
            int Tie(float centre, float width, float origin) { float e = (centre - width / 2 - origin) / g.Dx - 0.5f; return MathF.Abs(e - MathF.Round(e)) < 1e-3f ? 1 : 0; }
            return (Analysis.ThroatCells(g, o).Count, AirInWall(g, 16, 19), Tie(o.Center.Y, w, g.Origin.Y), Tie(o.Center.Z, h, g.Origin.Z));
        }
        var bad = new List<string>();
        // (width, height) in cells: even x even, odd x odd, mixed; every sub-cell translation of the centre.
        foreach (var (w, h, nw, nh) in new[] { (1f, .75f, 8, 6), (1.375f, .625f, 11, 5), (1f, .625f, 8, 5), (.875f, 1f, 7, 8) })
        foreach (float fy in new[] { 0f, .25f, .5f, .75f, -.25f })
        foreach (float fz in new[] { 0f, .25f, .5f, .75f })
        {
            var (t, air, tieY, tieZ) = Carve(w, h, fy * Dx, fz * Dx, Vector3.UnitX);
            int wantAir = (nw + tieY) * (nh + tieZ) * 4;
            if (t != (nw + tieY) * (nh + tieZ) || air != wantAir) bad.Add($"{w}x{h}@({fy},{fz})dx throat={t} air={air} want {(nw + tieY) * (nh + tieZ)}/{wantAir}");
        }
        Check("F3a footprint is exactly nW x nH cells (even, odd, mixed) at fractional translations of the centre, 4 layers deep, and the throat counts the same cells as the carve opens (RP-03: one cell more across an axis whose edges lie on cell centres; RP follow-up 4)",
            bad.Count == 0, bad.Count == 0 ? "80 cases" : string.Join("; ", bad.Take(4)));

        // Same footprint with the wall turned 90 degrees (normal along Y) and with the normal reversed.
        var wallY = Box(new(.125f, 2, .125f), new(4.875f, 2.5f, 4.875f));
        var oy = new Opening { Id = "o", Center = new(2.5f, 2.25f, 2.5f), Normal = Vector3.UnitY, Width = 1.375f, Height = .625f, Depth = .5f };
        var gy = Build(new BuildingModel { GroundZ = -10, Obstacles = { wallY }, Openings = { oy } });
        oy.Normal = -Vector3.UnitY;
        var gy2 = Build(new BuildingModel { GroundZ = -10, Obstacles = { wallY }, Openings = { oy } });
        // RP follow-up 4 (rule 3): both axes' edges lie on cell centres here (x 1.8125-3.1875, z 2.1875-2.8125), so the carve and the throat are 12 x 6 (was 11 x 5 by the half-open throat rule)
        Check("F3b rotated 90 deg: 11 x 5 footprint plus one row and column on cell centres (12 x 6), independent of normal sign", Analysis.ThroatCells(gy, oy).Count == 72 && gy.Cell.SequenceEqual(gy2.Cell));

        // A window turned 30 degrees in a wall turned with it: area stays nominal within the stair-step error.
        var rwall = RotatedBox(new(2.5f, 2.5f, 2.5f), new(.25f, 2.2f, 2.3f), 30);
        var n30 = new Vector3(MathF.Cos(30 * MathF.PI / 180), MathF.Sin(30 * MathF.PI / 180), 0);
        var o30 = new Opening { Id = "o", Center = new(2.5f, 2.5f, 2.5f), Normal = n30, Width = 1.5f, Height = 1, Depth = .5f };
        var g30 = Build(new BuildingModel { GroundZ = -10, Obstacles = { rwall }, Openings = { o30 } });
        int t30 = Analysis.ThroatCells(g30, o30).Count; o30.Normal = -n30;
        var g30r = Build(new BuildingModel { GroundZ = -10, Obstacles = { rwall }, Openings = { o30 } });
        Check("F3c window rotated 30 deg: throat within 10% of nominal 12 x 8 and identical for the reversed normal",
            Math.Abs(t30 - 96) <= 9.6 && g30.Cell.SequenceEqual(g30r.Cell), $"throat={t30} nominal=96");

        // Two close parallel walls (one-cell gap): the carve of a window in the first must not breach the second.
        var wa = Box(new(2, .125f, .125f), new(2.25f, 4.875f, 4.875f)); var wb = Box(new(2.375f, .125f, .125f), new(2.625f, 4.875f, 4.875f));
        var oa = new Opening { Id = "o", Center = new(2.125f, 2.5f, 2.5f), Normal = Vector3.UnitX, Width = 1, Height = 1, Depth = .25f };
        var gp = Build(new BuildingModel { GroundZ = -10, Obstacles = { wa, wb }, Openings = { oa } });
        bool secondIntact = true; for (int z = 16; z < 24; z++) for (int y = 16; y < 24; y++) secondIntact &= gp.IsSolid(gp.Index(19, y, z)) && gp.IsSolid(gp.Index(20, y, z));
        bool firstOpen = true; for (int z = 16; z < 24; z++) for (int y = 16; y < 24; y++) firstOpen &= !gp.IsSolid(gp.Index(16, y, z)) && !gp.IsSolid(gp.Index(17, y, z));
        Check("F3d window in a wall with a second wall one cell behind: first is open, second is not breached", firstOpen && secondIntact,
            $"firstOpen={firstOpen} secondIntact={secondIntact}");

        // A window in a wall turned 45 / 135 deg must pass air at every sub-cell phase of the wall: the carve margin has
        // to cover the conservative halo of an off-axis wall (0.707 Dx), or one solid layer is left across the hole.
        int blocked = 0, cases = 0;
        foreach (float deg in new[] { 45f, 135f })
        foreach (float th in new[] { .125f, .25f, .3f })
        foreach (float ph in new[] { 0f, .13f, .25f, .37f, .5f, .63f, .75f, .87f })
        {
            float ang = deg * MathF.PI / 180f; var nn = new Vector3(MathF.Cos(ang), MathF.Sin(ang), 0);
            var ctr = new Vector3(2.5f, 2.5f, 2.5f) + nn * (ph * Dx);
            var op = new Opening { Id = "o", Center = ctr, Normal = nn, Width = 1f, Height = .75f, Depth = th };
            var go = Build(new BuildingModel { GroundZ = -10, Obstacles = { RotatedBox(ctr, new(th / 2, 7f, 2.3f), deg) }, Openings = { op } });
            int Cell(Vector3 p) => go.Index((int)MathF.Floor(p.X / Dx), (int)MathF.Floor(p.Y / Dx), (int)MathF.Floor(p.Z / Dx));
            int s0 = Cell(ctr + nn * (th / 2 + 2 * Dx)), s1 = Cell(ctr - nn * (th / 2 + 2 * Dx));
            var seen = new HashSet<int> { s0 }; var todo = new Queue<int>(); todo.Enqueue(s0);
            while (todo.Count > 0)
            {
                int i = todo.Dequeue(); int x = i % go.Nx, y = i / go.Nx % go.Ny, z = i / (go.Nx * go.Ny);
                foreach (var (ax, ay, az) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    int qx = x + ax, qy = y + ay, qz = z + az;
                    if (qx < 0 || qy < 0 || qz < 0 || qx >= go.Nx || qy >= go.Ny || qz >= go.Nz) continue;
                    int j = go.Index(qx, qy, qz); if (go.IsSolid(j) || !seen.Add(j)) continue; todo.Enqueue(j);
                }
            }
            cases++; if (!seen.Contains(s1)) blocked++;
        }
        Check("F3e window in a wall turned 45/135 deg is open through the wall at every phase (6-connected air path)", blocked == 0, $"blocked {blocked} of {cases}");
    }
    /// <summary>IMP-C (bRW2): fix 1 a room's column stops at its ceiling; fix 2 one age-cap rule (a room entirely at the cap is sealed, capped cells in any other room count);
    /// fix 3 a detected opening's sides decide exterior (indoor air in no room is indoor), an undetected one keeps the room-loop rule.</summary>
    static void ImpCChecks()
    {
        // Fix 1: 10 x 10 x 12 cells of 0.5 m; air x,y 2..7, z 2..10 except a slab at z = 6 (cell centre 3.25 m) and a 2 x 2 x 3-cell cabinet (x,y 3..4, z 2..4) under it. The slab is thinner than a cell: LBM counts its cells as fluid (LbmAir), the Cell mask as solid. The room's z range runs to 5.5 m, above the slab.
        var g = new VoxelGrid { Nx = 10, Ny = 10, Nz = 12, Dx = 0.5f, Origin = Vector3.Zero, Cell = new byte[1200] };
        for (int z = 0; z < 12; z++) for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++)
            g.Cell[g.Index(x, y, z)] = x is >= 2 and <= 7 && y is >= 2 and <= 7 && z is >= 2 and <= 10 && z != 6 && !(x is 3 or 4 && y is 3 or 4 && z is <= 4) ? VoxelGrid.Air : VoxelGrid.Solid;
        g.LbmAir = new bool[1200];
        for (int y = 2; y <= 7; y++) for (int x = 2; x <= 7; x++) g.LbmAir[g.Index(x, y, 6)] = true;
        var room = new Room { Id = "r", Name = "R", Level = "L1", ZMin = 1f, ZMax = 5.5f };
        room.Loops.Add([new Vector2(1.5f, 1.5f), new Vector2(3.5f, 1.5f), new Vector2(3.5f, 3.5f), new Vector2(1.5f, 3.5f)]);
        var cellMask = Analysis.RoomMask(g, room, out int nCell);
        var lbmMask = Analysis.RoomMask(g, room, out int nLbm, lbm: true);
        int above = 0;
        for (int i = 0; i < g.Count; i++) if ((cellMask[i] || lbmMask[i]) && i / (g.Nx * g.Ny) > 6) above++;
        Check("IMP-C 1: a room stops at its ceiling: the air above a slab inside the storey is not the room's, the air above a cabinet is (4 layers x 16 columns less 12 cabinet cells; LBM adds the 16 slab cells; none above the slab)",
            nCell == 52 && nLbm == 68 && above == 0, $"cell mask {nCell}, LBM mask {nLbm}, cells above the slab {above} (want 52, 68, 0)");

        // RA-07: a drawn 4 x 4 x 2.5 m room at Dx 0.11 m (the re-audit harness) with a thin 1.6 x 0.8 m table top at 0.75 m on four legs, a 0.11 m desk slab, and a 0.22 m beam across the room under the ceiling.
        // Air above each is open to the room at its sides, so the room's cells = the empty room's less only the furniture's own solid cells (Cell mask) or less the non-LBM-fluid ones (LBM mask).
        {
            const float d = 0.11f;
            var gr = new VoxelGrid { Nx = 40, Ny = 40, Nz = 24, Dx = d, Origin = Vector3.Zero, Cell = new byte[40 * 40 * 24] };
            var rm = new Room { Id = "r7", Name = "R7", Level = "L1", ZMin = 0f, ZMax = 2.5f };
            rm.Loops.Add([new Vector2(0.2f, 0.2f), new Vector2(4.2f, 0.2f), new Vector2(4.2f, 4.2f), new Vector2(0.2f, 4.2f)]);
            var e0 = Analysis.RoomMask(gr, rm, out int emptyCell); var e1 = Analysis.RoomMask(gr, rm, out int emptyLbm, lbm: true);
            gr.LbmAir = new bool[gr.Count];
            void Box(int x0, int x1, int y0, int y1, int z0, int z1, bool thin = false) { for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { int i = gr.Index(x, y, z); gr.Cell[i] = VoxelGrid.Solid; gr.LbmAir[i] = thin; } }
            Box(8, 22, 8, 15, 6, 6, thin: true); foreach (var (lx, ly) in new[] { (8, 8), (22, 8), (8, 15), (22, 15) }) Box(lx, lx, ly, ly, 0, 5);   // table 15 x 8 cells = 1.65 x 0.88 m, top at z cell 6 (0.77 m), thin: LBM counts it as fluid
            Box(26, 37, 24, 29, 6, 6); Box(26, 26, 24, 24, 0, 5); Box(37, 37, 29, 29, 0, 5);   // desk slab, a full solid cell, two legs
            Box(18, 19, 2, 37, 18, 19);   // beam along y, 2 cells wide, 2 cells tall (z 1.98-2.2 m): air above it (z 20..22) is open at the beam's sides
            var m0 = Analysis.RoomMask(gr, rm, out int fCell); var m1 = Analysis.RoomMask(gr, rm, out int fLbm, lbm: true);
            int owfCell = 0, owfLbm = 0, aboveBeam = 0;
            for (int i = 0; i < gr.Count; i++) { if (e0[i] && gr.Cell[i] == VoxelGrid.Solid) owfCell++; if (e1[i] && !Voxelizer.LbmFluid(gr, i)) owfLbm++; if (m0[i] && i / (gr.Nx * gr.Ny) >= 20 && (i % gr.Nx) is 18 or 19) aboveBeam++; }
            Check("RA-07: furniture does not clip the room: a table top, a desk slab and a beam leave the room mask at exactly the empty room less the furniture's own solid cells, air above the beam in the room",
                fCell == emptyCell - owfCell && fLbm == emptyLbm - owfLbm && aboveBeam > 0,
                $"empty {emptyCell}/{emptyLbm}; Cell mask {fCell} (want {emptyCell - owfCell}), LBM mask {fLbm} (want {emptyLbm - owfLbm}); cells above the beam in the room {aboveBeam}");

            // RA-07 storey guard: ZMax spans two storeys (slab at z cell 12, upper air z 13..29) with a 4 x 4-column stair void through the slab (about 1.2 % of the footprint): the void columns keep their
            // full height (pass 1 as before) but the upper storey's air beside the void is not flooded into.
            var gs = new VoxelGrid { Nx = 40, Ny = 40, Nz = 30, Dx = d, Origin = Vector3.Zero, Cell = new byte[40 * 40 * 30] };
            for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) if (!(x is >= 10 and <= 13 && y is >= 10 and <= 13)) gs.Cell[gs.Index(x, y, 12)] = VoxelGrid.Solid;
            var rs = new Room { Id = "r7s", Name = "R7S", Level = "L1", ZMin = 0f, ZMax = 3.3f };
            rs.Loops.Add([new Vector2(0.2f, 0.2f), new Vector2(4.2f, 0.2f), new Vector2(4.2f, 4.2f), new Vector2(0.2f, 4.2f)]);
            var ms = Analysis.RoomMask(gs, rs, out _, storeyTop: 1.4f);   // the storey's top: the slab's top face (z cell 12 ends at 1.43 m)
            int upperOutside = 0, upperVoid = 0;
            for (int i = 0; i < gs.Count; i++)
            {
                if (!ms[i] || i / (gs.Nx * gs.Ny) < 13) continue;
                int xx = i % gs.Nx, yy = i / gs.Nx % gs.Ny;
                if (xx is >= 10 and <= 13 && yy is >= 10 and <= 13) upperVoid++; else upperOutside++;
            }
            Check("RA-07: a room whose ZMax spans two storeys does not flood into the upper storey through a stair void: upper air outside the void columns is not in the mask, the void columns keep their full height",
                upperOutside == 0 && upperVoid > 0, $"upper-storey cells in the mask: outside the void {upperOutside} (want 0), in the void columns {upperVoid} (want > 0)");

            // RC-01 / RC-03: the flood is capped at the storey top, not the median column top, so furniture or a platform over MORE than half the plan loses no air, and a stair void over more than half
            // of the plan does not open the upper storey. Each scene: the room's cells = the empty room's less its own solid cells, exactly (lost air 0); both masks.
            void Scene(string name, int nz, float zMax, float top, Action<Action<int, int, int, int, int, int>> furnish)
            {
                var gx = new VoxelGrid { Nx = 40, Ny = 40, Nz = nz, Dx = d, Origin = Vector3.Zero, Cell = new byte[40 * 40 * nz], LbmAir = new bool[40 * 40 * nz] };
                var rx = new Room { Id = name, Name = name, Level = "L1", ZMin = 0f, ZMax = zMax };
                rx.Loops.Add([new Vector2(0.2f, 0.2f), new Vector2(4.2f, 0.2f), new Vector2(4.2f, 4.2f), new Vector2(0.2f, 4.2f)]);
                var ec = Analysis.RoomMask(gx, rx, out int nec, storeyTop: top); var el = Analysis.RoomMask(gx, rx, out int nel, lbm: true, storeyTop: top);
                furnish((x0, x1, y0, y1, z0, z1) => { for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) gx.Cell[gx.Index(x, y, z)] = VoxelGrid.Solid; });
                var fc = Analysis.RoomMask(gx, rx, out int nfc, storeyTop: top); var fl = Analysis.RoomMask(gx, rx, out int nfl, lbm: true, storeyTop: top);
                int own = 0, covered = 0, plan = 0;
                for (int i = 0; i < gx.Count; i++) if (ec[i] && gx.Cell[i] == VoxelGrid.Solid) own++;
                for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) { var c = gx.CellCenter(x, y, 0); if (c.X is > 0.2f and < 4.2f && c.Y is > 0.2f and < 4.2f) { plan++; if (gx.Cell[gx.Index(x, y, 0)] == VoxelGrid.Solid || Enumerable.Range(0, nz).Any(z => gx.Cell[gx.Index(x, y, z)] == VoxelGrid.Solid)) covered++; } }
                Check($"RC-01: {name} ({100 * covered / plan} % of the plan raised): no air lost, the room's cells are the empty room's less its own solid cells",
                    covered * 2 > plan && nfc == nec - own && nfl == nel - own, $"Cell {nfc} (want {nec - own}), LBM {nfl} (want {nel - own}), plan covered {100 * covered / plan} % (want > 50)");
            }
            Scene("desks58", 24, 2.5f, 2.5f, B => { B(2, 37, 2, 8, 6, 6); B(2, 37, 31, 37, 6, 6); B(2, 8, 9, 30, 6, 6); B(31, 37, 9, 30, 6, 6); });   // perimeter desks 0.8 m deep, a ring round a 2.4 m square
            Scene("loft62", 24, 2.5f, 2.5f, B => B(0, 39, 14, 39, 14, 15));   // 4 x 2.5 m sleeping platform
            Scene("mezz65", 48, 5.0f, 5.0f, B => B(0, 25, 0, 39, 23, 24));   // gallery over a double-height room (its storey is the double height: top 5.0 m)

            // RC-03: a stair void over 54 % of the plan, slab at z cell 12, upper air z 13..29, a separately drawn upper room over the same footprint.
            {
                var gt = new VoxelGrid { Nx = 40, Ny = 40, Nz = 30, Dx = d, Origin = Vector3.Zero, Cell = new byte[40 * 40 * 30] };
                bool InVoid(int x, int y) => x is >= 2 and <= 25 && y is >= 2 and <= 30;
                for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) if (!InVoid(x, y)) gt.Cell[gt.Index(x, y, 12)] = VoxelGrid.Solid;
                var loops = new Vector2[] { new(0.2f, 0.2f), new(4.2f, 0.2f), new(4.2f, 4.2f), new(0.2f, 4.2f) };
                var lowR = new Room { Id = "rl", Name = "Lower", Level = "L1", ZMin = 0f, ZMax = 3.3f }; lowR.Loops.Add(loops);
                var upR = new Room { Id = "ru", Name = "Upper", Level = "L2", ZMin = 1.43f, ZMax = 3.3f }; upR.Loops.Add(loops);
                var lo = Analysis.RoomMask(gt, lowR, out _, storeyTop: 1.4f); var up = Analysis.RoomMask(gt, upR, out int nUp);
                int outside = 0, inVoid = 0, sharedOutside = 0;
                for (int i = 0; i < gt.Count; i++)
                {
                    int xx = i % gt.Nx, yy = i / gt.Nx % gt.Ny;
                    if (lo[i] && i / (gt.Nx * gt.Ny) >= 13) { if (InVoid(xx, yy)) inVoid++; else outside++; }
                    if (lo[i] && up[i] && !InVoid(xx, yy)) sharedOutside++;
                }
                Check("RC-03: a stair void over 54 % of the plan does not open the upper storey: no upper-storey cell outside the void columns in the lower room, none shared with the separately drawn upper room (the void columns keep their full height)",
                    outside == 0 && sharedOutside == 0 && inVoid > 0 && nUp > 0, $"upper cells in the lower room: outside the void {outside} (want 0), in the void columns {inVoid} (want > 0); shared outside the void {sharedOutside} (want 0)");
            }
        }

        // Fix 2: 100 air cells, room A = cells 0..49 (10 of them at the 24 h cap, age 100 s elsewhere), room B = cells 50..99 all at the cap.
        var ga = new VoxelGrid { Nx = 100, Ny = 1, Nz = 1, Dx = 1f, Origin = Vector3.Zero, Cell = new byte[100] };
        var age = new float[100]; var maskA = new bool[100]; var maskB = new bool[100];
        for (int i = 0; i < 100; i++) { maskA[i] = i < 50; maskB[i] = i >= 50; age[i] = i >= 50 || i < 10 ? ScalarSolver.AgeCapSeconds : 100f; }
        var ra = new Room { Name = "A" }; var rb = new Room { Name = "B" };
        float rate = Analysis.BuildingFreshAch(ga, [ra, rb], age, out float sealedVol, out var sealedNames, r => ReferenceEquals(r, ra) ? maskA : maskB);
        float wantRate = 3600f / ((10 * ScalarSolver.AgeCapSeconds + 40 * 100f) / 50f);
        Check("IMP-C 2: the building rate drops a room entirely at the age cap (sealed, named) and keeps the capped cells of a room that is not (stale pockets)",
            MathF.Abs(rate - wantRate) < 1e-3f && sealedVol == 50f && sealedNames.SequenceEqual(["B"]),
            $"rate {rate:0.0000} ACH (want {wantRate:0.0000}), sealed {sealedVol} m3 {string.Join(",", sealedNames)}");

        // Fix 3: a detected opening's sides. Room loop 0..4 m; hall air beyond x = 4.
        var m = new BuildingModel();
        var hall = new Room { Id = "h", Name = "H", ZMin = 0f, ZMax = 3f };
        hall.Loops.Add([new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 4), new Vector2(0, 4)]);
        m.Rooms.Add(hall);
        Opening Door(float x, OpeningSide a, OpeningSide b) => new() { Center = new Vector3(x, 2, 1), Normal = Vector3.UnitX, Width = 1, Height = 2, Depth = 0.2f, SideA = a, SideB = b };
        var undetected = Door(4f, OpeningSide.Unknown, OpeningSide.Unknown);   // a drawn opening: the loop rule, the far side is no room, so exterior
        var toHall = Door(4f, OpeningSide.Room, OpeningSide.Indoor);
        var toOutside = Door(4f, OpeningSide.Room, OpeningSide.Outdoors);
        var hallToOutside = Door(8f, OpeningSide.Indoor, OpeningSide.Outdoors);
        Check("IMP-C 3: a detected opening is exterior only when a side is outdoor air (room to indoor non-room air is indoor); an undetected one keeps the room-loop rule",
            Analysis.IsExteriorOpening(m, undetected, 0.1f) && !Analysis.IsExteriorOpening(m, toHall, 0.1f) && Analysis.IsExteriorOpening(m, toOutside, 0.1f) && Analysis.IsExteriorOpening(m, hallToOutside, 0.1f),
            $"undetected {Analysis.IsExteriorOpening(m, undetected, 0.1f)}, room-to-hall {Analysis.IsExteriorOpening(m, toHall, 0.1f)}, room-to-outside {Analysis.IsExteriorOpening(m, toOutside, 0.1f)}, hall-to-outside {Analysis.IsExteriorOpening(m, hallToOutside, 0.1f)} (want True False True True)");
    }
}
