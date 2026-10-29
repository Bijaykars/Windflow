// Walls: interpolated bounce-back on a signed-distance voxelisation, thin-wall blocking, and simulation-box
// faces that land inside a partition.  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --walls      (or as part of the default run)
//
// The solver places a no-slip wall halfway between a fluid cell centre and a solid one unless it knows
// better. VoxelGrid.Distance gives it the real surface position, and LbmSolver turns a fluid/solid pair of
// distances into the wall fraction along each link for Bouzidi-Firdaouss-Lallemand interpolated bounce-back.
// The Poiseuille scene below is the direct measurement of that: a channel whose walls deliberately do NOT
// sit on cell boundaries, so halfway bounce-back has to get the width wrong and the interpolated scheme has
// to get it right.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class WallChecks
{
    /// <summary>Runs every wall check and returns the number of failures (0 = all passed).</summary>
    public static int Run()
    {
        int failures = 0;

        bool Check(string label, bool pass, string detail)
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {label}  {detail}");
            CheckLog.Record(pass ? "PASS" : "FAIL", label, detail);
            if (!pass) failures++;
            return pass;
        }

        void Skip(string label, string detail) { Console.WriteLine($"[WAIT] {label}  {detail}"); CheckLog.Record("WAIT", label, detail); }

        Console.WriteLine("== wall checks ==");
        PoiseuilleChecks(Check, Skip);
        ThinWallCheck(Check);
        BoxFaceCheck(Check);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL WALL CHECKS PASSED" : $"{failures} WALL CHECK(S) FAILED");
        return failures;
    }

    // ---------------------------------------------------------------------------------------------
    // 50. Plane Poiseuille flow between walls that sit 0.3 of a cell inside the solid layer.
    //     6 fluid cells across, free-slip in z (so it is a true 2D channel), plug inlet, zero-gradient
    //     outlet. Re ~ 40, developed well inside the 48-cell length.
    // ---------------------------------------------------------------------------------------------

    const int Nx = 48, Ny = 8, Nz = 4;      // y = 0 and y = Ny-1 solid; 6 fluid cells across
    const float Dx = 0.005f;                // 5 mm cells: viscous enough for a laminar, quickly developed channel
    const float Uin = 0.024f;               // m/s, plug inlet; Re = U*H/nu ~ 40
    const float WallInset = 0.3f;           // of a cell: how far each wall face sits INSIDE the solid layer

    static void PoiseuilleChecks(Func<string, bool, string, bool> check, Action<string, string> skip)
    {
        // True wall faces, and so the true channel width.
        float wall0 = (1f + WallInset) * Dx, wall1 = (Ny - 1 - WallInset) * Dx;
        float trueWidth = wall1 - wall0;

        (float Width, float Peak, float MeanErr, string Backend, string Profile) RunChannel(Backend backend, bool interpolated)
        {
            var grid = Channel(interpolated, wall0, wall1);
            var model = new BuildingModel
            {
                Name = "poiseuille", GroundZ = 0,
                Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = Nz * Dx, Analysed = true } },
            };
            var options = new RunOptions { Backend = backend, WindProfileExponent = 0f };
            using var session = LbmSolver.Start(grid, model, new Vector2(Uin, 0f), options);
            session.Step(4 * session.PlannedSteps);
            session.ResetAveraging();
            session.Step(session.PlannedSteps);
            var field = session.ToField();

            // Profile three quarters of the way down the channel, averaged over z (all free-slip layers).
            // Coordinates in CELLS measured from the channel centre, not metres from the origin: fitting a
            // quadratic through x ~ 0.0075..0.0325 m builds normal equations spanning six orders of
            // magnitude, and the roots come out complex on perfectly good data.
            var y = new double[Ny - 2];
            var u = new double[Ny - 2];
            int xs = 3 * Nx / 4;
            double centre = (Ny - 1) * 0.5;
            for (int j = 1; j <= Ny - 2; j++)
            {
                double sum = 0;
                for (int z = 0; z < Nz; z++) sum += field.Ux[grid.Index(xs, j, z)];
                y[j - 1] = j + 0.5 - centre;
                u[j - 1] = sum / Nz;
            }
            var (a, b, c) = FitQuadratic(y, u);
            // u(y) = a y^2 + b y + c; the two roots are where the fit says the no-slip surfaces are.
            double disc = b * b - 4 * a * c;
            string profile = string.Join(",", Array.ConvertAll(u, v => v.ToString("F5")));
            if (a >= 0 || disc <= 0) return (float.NaN, float.NaN, float.NaN, session.Backend, profile);
            double r0 = (-b + Math.Sqrt(disc)) / (2 * a), r1 = (-b - Math.Sqrt(disc)) / (2 * a);
            float width = (float)Math.Abs(r1 - r0) * Dx;
            float peak = (float)(c - b * b / (4 * a));

            // How parabolic it actually is, as a sanity guard on the fit.
            double err = 0;
            for (int k = 0; k < u.Length; k++) err += Math.Abs(a * y[k] * y[k] + b * y[k] + c - u[k]);
            return (width, peak, (float)(err / u.Length / Math.Max(peak, 1e-9)), session.Backend, profile);
        }

        var interp = RunChannel(Backend.Cpu, interpolated: true);
        var halfway = RunChannel(Backend.Cpu, interpolated: false);
        float errInterp = MathF.Abs(interp.Width - trueWidth) / trueWidth;
        float errHalf = MathF.Abs(halfway.Width - trueWidth) / trueWidth;

        check("50a poiseuille interpolated wall: fitted channel width within 5% of the true 5.4 cells",
            errInterp <= 0.05f && interp.MeanErr <= 0.05f,
            $"width={interp.Width / Dx:F2} cells (true {trueWidth / Dx:F2}) err={errInterp * 100:F1}% " +
            $"peak={interp.Peak:F4} m/s (plug inlet {Uin:F3}, parabola peak should be ~1.5x) " +
            $"fitResidual={interp.MeanErr * 100:F2}% of peak u=[{interp.Profile}]");

        check("50b poiseuille halfway bounce-back is the one that misses the wall position",
            errHalf > errInterp,
            $"halfway width={halfway.Width / Dx:F2} cells err={errHalf * 100:F1}% vs interpolated {errInterp * 100:F1}% " +
            $"(the wall sits {WallInset:F1} of a cell inside the solid layer, so halfway has to read ~{2f * WallInset / (trueWidth / Dx) * 100:F0}% wide) u=[{halfway.Profile}]");

        if (!LbmSolver.GpuAvailable) { skip("50c poiseuille GPU = CPU within 1%", "no GPU available"); return; }
        var gpu = RunChannel(Backend.Gpu, interpolated: true);
        float parity = MathF.Abs(gpu.Width - interp.Width) / MathF.Max(interp.Width, 1e-9f);
        check("50c poiseuille interpolated wall GPU = CPU within 1%",
            parity <= 0.01f,
            $"cpu={interp.Width / Dx:F3} gpu={gpu.Width / Dx:F3} cells err={parity * 100:F2}% " +
            $"peak cpu={interp.Peak:F4} gpu={gpu.Peak:F4} m/s backend={gpu.Backend}");
    }

    /// <summary>The channel grid: solid at y = 0 and y = Ny-1, air everywhere else, free-slip top and
    /// bottom in z. The distance field is filled analytically (the exact distance to the nearer wall
    /// plane), so this checks the solver's use of it rather than the voxelizer's triangle search -- the
    /// thin-wall check below exercises that path.</summary>
    static VoxelGrid Channel(bool interpolated, float wall0, float wall1)
    {
        var grid = new VoxelGrid
        {
            Nx = Nx, Ny = Ny, Nz = Nz, Dx = Dx, Origin = Vector3.Zero, Cell = new byte[Nx * Ny * Nz],
        };
        for (int z = 0; z < Nz; z++)
        for (int y = 0; y < Ny; y++)
        for (int x = 0; x < Nx; x++)
            grid.Cell[grid.Index(x, y, z)] = y == 0 || y == Ny - 1 ? VoxelGrid.Solid : VoxelGrid.Air;

        if (!interpolated) return grid;

        var dist = new float[grid.Count];
        Array.Fill(dist, Voxelizer.DistanceFar);
        for (int z = 0; z < Nz; z++)
        for (int y = 0; y < Ny; y++)
        for (int x = 0; x < Nx; x++)
        {
            float yc = (y + 0.5f) * Dx;
            float d = MathF.Min(MathF.Abs(yc - wall0), MathF.Abs(yc - wall1));
            if (d < 2f * Dx) dist[grid.Index(x, y, z)] = d;
        }
        grid.Distance = dist;
        return grid;
    }

    /// <summary>Least-squares quadratic through the sampled profile.</summary>
    internal static (double A, double B, double C) FitQuadratic(double[] x, double[] y)
    {
        double s0 = x.Length, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double xi = x[i], xi2 = xi * xi;
            s1 += xi; s2 += xi2; s3 += xi2 * xi; s4 += xi2 * xi2;
            t0 += y[i]; t1 += y[i] * xi; t2 += y[i] * xi2;
        }
        // Solve the 3x3 normal equations by Cramer's rule.
        double D = s4 * (s2 * s0 - s1 * s1) - s3 * (s3 * s0 - s1 * s2) + s2 * (s3 * s1 - s2 * s2);
        double Da = t2 * (s2 * s0 - s1 * s1) - s3 * (t1 * s0 - t0 * s1) + s2 * (t1 * s1 - t0 * s2);
        double Db = s4 * (t1 * s0 - t0 * s1) - t2 * (s3 * s0 - s1 * s2) + s2 * (s3 * t0 - s2 * t1);
        double Dc = s4 * (s2 * t0 - s1 * t1) - s3 * (s3 * t0 - s2 * t1) + t2 * (s3 * s1 - s2 * s2);
        return (Da / D, Db / D, Dc / D);
    }

    // ---------------------------------------------------------------------------------------------
    // 51. A 50 mm partition in a 255 mm grid still blocks: the conservative "every cell a triangle
    //     touches is solid" rule is the leak guard, and interpolated bounce-back must not open it.
    // ---------------------------------------------------------------------------------------------

    static void ThinWallCheck(Func<string, bool, string, bool> check)
    {
        const float h = 3f, thin = 0.05f;
        var floor = new BuildingModel
        {
            Name = "thinwall", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = h, Analysed = true } },
        };
        floor.Obstacles.Add(WallBox(0, 0, 8, 0, 0.2f, h));
        floor.Obstacles.Add(WallBox(8, 0, 8, 6, 0.2f, h));
        floor.Obstacles.Add(WallBox(8, 6, 0, 6, 0.2f, h));
        floor.Obstacles.Add(WallBox(0, 6, 0, 0, 0.2f, h));
        floor.Obstacles.Add(WallBox(4, 0, 4, 6, thin, h));      // the 50 mm partition, floor to ceiling
        floor.Obstacles.Add(Box(new Vector3(-1, -1, -0.2f), new Vector3(9, 7, 0f)));
        floor.Obstacles.Add(Box(new Vector3(0, 0, h), new Vector3(8, 6, h + 0.2f)));
        floor.Openings.Add(Window("west", new Vector3(0, 3, 1.5f), -Vector3.UnitX));
        floor.Openings.Add(Window("east", new Vector3(8, 3, 1.5f), Vector3.UnitX));
        floor.Rooms.Add(RoomOf("west-room", 0.1f, 0.1f, 3.9f, 5.9f, h));
        floor.Rooms.Add(RoomOf("east-room", 4.1f, 0.1f, 7.9f, 5.9f, h));

        var result = Simulation.Run(floor, new Vector2(3, 0), new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu });
        var g = result.Field.Grid;

        // Every cell of the partition plane has to be solid, and no air may cross it.
        int xi = (int)MathF.Floor((4f - g.Origin.X) / g.Dx);
        int solid = 0, air = 0;
        double flux = 0;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        {
            var c = g.CellCenter(xi, y, z);
            if (c.Y < 0.1f || c.Y > 5.9f || c.Z < 0.05f || c.Z > h) continue;
            int idx = g.Index(xi, y, z);
            if (g.Cell[idx] == VoxelGrid.Solid) solid++;
            else { air++; flux += Math.Abs(result.Field.Ux[idx]) * g.Dx * g.Dx; }
        }
        float windward = MathF.Abs(result.Openings.First(o => o.Opening.Id == "west").FlowRate);

        check("51 thin wall: a 50 mm partition in a 255 mm grid is solid across the whole plane and leaks nothing",
            air == 0 && solid > 0 && flux <= 0.02f * Math.Max(windward, 1e-6f),
            $"partitionPlane solid={solid} air={air} cells, |flux| through it={flux:F4} m3/s vs " +
            $"{windward:F3} m3/s in at the west window, Dx={g.Dx * 1000:F0}mm (wall is {thin / g.Dx:F2} cells thick)");
    }

    // ---------------------------------------------------------------------------------------------
    // 52. A simulation box whose +X face lands inside a partition. Geometry copied from the app's sample
    //     house (src/WindFlow.App/SampleBuilding.cs, upper storey), with the box the app found diverging.
    // ---------------------------------------------------------------------------------------------

    static void BoxFaceCheck(Func<string, bool, string, bool> check)
    {
        const float W = 12f, D = 8f, L2 = 3f, Roof = 6f, ExtT = 0.25f, IntT = 0.12f, SlabT = 0.2f;
        float sz1 = L2 + SlabT;
        var m = new BuildingModel
        {
            Name = "boxface", GroundZ = 0,
            Storeys =
            {
                new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true },
                new Storey { Id = "L2", Name = "Level 2", Elevation = L2, Height = 3, Analysed = true },
            },
        };
        m.Obstacles.Add(WallBox(0, 0, W, 0, ExtT, Roof));
        m.Obstacles.Add(WallBox(W, 0, W, D, ExtT, Roof));
        m.Obstacles.Add(WallBox(0, D, W, D, ExtT, Roof));
        m.Obstacles.Add(WallBox(0, 0, 0, D, ExtT, Roof));
        m.Obstacles.Add(Box(new Vector3(-1, -1, -0.2f), new Vector3(W + 1, D + 1, 0)));
        m.Obstacles.Add(Box(new Vector3(0, 0, Roof), new Vector3(W, D, Roof + SlabT)));
        // L1/L2 slab with the stair void, as four strips (void x 7.1..8.9: over the hall, P12-03)
        m.Obstacles.Add(Box(new Vector3(0, 0, L2), new Vector3(W, 5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(0, 7.5f, L2), new Vector3(W, D, sz1)));
        m.Obstacles.Add(Box(new Vector3(0, 5f, L2), new Vector3(7.1f, 7.5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(8.9f, 5f, L2), new Vector3(W, 7.5f, sz1)));
        // L2 partitions -- the x = 5.5 m one is what the box face cuts through
        m.Obstacles.Add(WallBoxZ(5.5f, 0, 5.5f, D, IntT, sz1, Roof));
        m.Obstacles.Add(WallBoxZ(5.5f, 4.5f, W, 4.5f, IntT, sz1, Roof));
        m.Obstacles.Add(WallBoxZ(9, 4.5f, 9, D, IntT, sz1, Roof));

        float z2 = sz1 + 1.3f;
        m.Openings.Add(Window("w-b1-s", new Vector3(2.5f, 0, z2), -Vector3.UnitY, 1.4f, 1.3f));
        m.Openings.Add(Window("w-b1-n", new Vector3(2.5f, D, z2), Vector3.UnitY, 1.4f, 1.3f));
        m.Openings.Add(Window("w-b1-w", new Vector3(0, 4, z2), -Vector3.UnitX, 1.2f, 1.3f));
        m.Rooms.Add(new Room
        {
            Id = "bed1", Name = "Bedroom 1", Level = "L2", ZMin = sz1, ZMax = Roof,
            Loops = { new[] { new Vector2(0.2f, 0.2f), new Vector2(5.4f, 0.2f), new Vector2(5.4f, D - 0.2f), new Vector2(0.2f, D - 0.2f) } },
        });

        var box = new DomainBox(new Vector3(-0.85f, -0.85f, 3.1f), new Vector3(5.5f, 8.85f, 7.0f));
        var options = new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu, Domain = box };

        string detail;
        bool solved;
        try
        {
            var result = Simulation.Run(m, new Vector2(3, 0), options);
            var g = result.Field.Grid;
            var room = result.Rooms.FirstOrDefault();
            bool finite = true;
            for (int i = 0; i < g.Count && finite; i++)
                if (g.Cell[i] == VoxelGrid.Air && (float.IsNaN(result.Field.Ux[i]) || float.IsNaN(result.Field.P[i]))) finite = false;
            solved = finite && room != null && room.MeanSpeed >= 0 && !float.IsNaN(room.MeanSpeed);
            detail = $"grid {g.Nx}x{g.Ny}x{g.Nz} Dx={g.Dx * 1000:F0}mm finite={finite} " +
                $"roomMeanSpeed={room?.MeanSpeed ?? float.NaN:F3} m/s ACH={room?.Ach ?? float.NaN:F1} " +
                $"ledgerResidual={result.Ledger.Residual * 100:F0}% | " +
                string.Join(" | ", g.Warnings.Where(w => w.Contains("face") || w.Contains("sealed")));
        }
        catch (Exception ex)
        {
            solved = false;
            detail = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        check("52 simulation box with its +X face inside the x=5.5 m partition solves instead of diverging", solved, detail);
    }

    // ---------------------------------------------------------------------------------------------
    // Geometry helpers (local: Program's are private to it)
    // ---------------------------------------------------------------------------------------------

    static Opening Window(string id, Vector3 center, Vector3 normal, float w = 1.2f, float h = 1.2f) => new()
    {
        Id = id, Name = id, Kind = OpeningKind.Window, IsOpen = true,
        Center = center, Normal = normal, Width = w, Height = h, Depth = 0.2f,
    };

    static Room RoomOf(string id, float x0, float y0, float x1, float y1, float h) => new()
    {
        Id = id, Name = id, Level = "L1", ZMin = 0, ZMax = h,
        Loops = { new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) } },
    };

    static Mesh WallBox(float x0, float y0, float x1, float y1, float thickness, float height)
        => WallBoxZ(x0, y0, x1, y1, thickness, 0, height);

    static Mesh WallBoxZ(float x0, float y0, float x1, float y1, float thickness, float zMin, float zMax)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float half = thickness * 0.5f;
        if (MathF.Abs(x1 - x0) < 1e-6f) { minX -= half; maxX += half; }
        else { minY -= half; maxY += half; }
        return Box(new Vector3(minX, minY, zMin), new Vector3(maxX, maxY, zMax));
    }

    static Mesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] v =
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        };
        int[] idx = { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0 };
        return new Mesh { Id = Guid.NewGuid().ToString("N"), Vertices = v, Indices = idx };
    }
}
