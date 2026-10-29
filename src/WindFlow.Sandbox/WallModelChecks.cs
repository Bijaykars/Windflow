// Equilibrium wall function (RunOptions.WallModel = WernerWengle): a tangential slip velocity imposed through the moving-wall term
// of the Bouzidi bounce-back, after Han, Ooka and Kikumoto, Build. Environ. 195 (2021) 107764. Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --wallmodel
// WM1 laminar safety, WM2 sign + law + bulk speed-up in a force-driven periodic channel, WM3 CPU = GPU, WM4 mass in a closed box.
// The periodic channel uses the Sandbox-only RunOptions hooks TestBodyForceX / TestPeriodicX (ghost columns, period Nx - 2).

using System.Numerics;
using ILGPU.Runtime;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class WallModelChecks
{
    const float NuAir = 1.5e-5f;      // LbmSolver.NuPhysAir
    const float ULat = 0.08f;         // LbmSolver.ULat
    const float NuFloor = 5e-4f;      // LbmSolver.NuLatMin

    static BuildingModel Model(int nz, float dx) => new()
    {
        Name = "wm", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = nz * dx, Analysed = true } },
    };

    /// <summary>Channel between solid planes y = 0 and y = Ny - 1 (halfway walls, no WallLinks); everything else Air.</summary>
    static VoxelGrid Channel(int nx, int ny, int nz, float dx)
    {
        var g = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = dx, Origin = Vector3.Zero, Cell = new byte[nx * ny * nz] };
        for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            g.Cell[g.Index(x, y, z)] = y == 0 || y == ny - 1 ? VoxelGrid.Solid : VoxelGrid.Air;
        return g;
    }

    static IEnumerable<Backend> Backends => LbmSolver.GpuAvailable ? new[] { Backend.Cpu, Backend.Gpu } : new[] { Backend.Cpu };

    // ---- WM1: F2a-style channel, physical nu = lattice nu --------------------------------------------------------

    static (float[] Profile, float NuLat, float NuPhys) RunLaminar(bool model)
    {
        const int nx = 48, ny = 8, nz = 4; const float dx = 0.005f, uin = 0.024f;
        var grid = Channel(nx, ny, nz, dx);
        using var s = LbmSolver.Start(grid, Model(nz, dx), new Vector2(uin, 0f),
            new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, WallModel = model ? WallModel.WernerWengle : WallModel.None });
        s.Step(4 * s.PlannedSteps); s.ResetAveraging(); s.Step(s.PlannedSteps);
        var f = s.ToField(); var prof = new float[ny - 2]; int xs = 3 * nx / 4;
        for (int j = 1; j <= ny - 2; j++) { float sum = 0; for (int z = 0; z < nz; z++) sum += f.Ux[grid.Index(xs, j, z)]; prof[j - 1] = sum / nz; }
        return (prof, s.LatticeViscosity, NuAir * s.TimeStepSeconds / (dx * dx));
    }

    // ---- WM2/WM3: force-driven periodic channel, small physical nu (the floor binds) -------------------------------

    const int PNx = 12, PNy = 18, PNz = 4; const float PDx = 0.01f;
    const float PHalf = (PNy - 2) / 2f;              // h: half the wall-to-wall distance, cells
    const float PGravity = 6e-7f;                    // body force, lattice units

    static LbmSolver.LbmSession PeriodicSession(Backend be, bool model, float g)
    {
        var grid = Channel(PNx, PNy, PNz, PDx);
        return new LbmSolver.LbmSession(grid, Model(PNz, PDx), Vector2.Zero,
            new RunOptions { Backend = be, WallModel = model ? WallModel.WernerWengle : WallModel.None, TestBodyForceX = g, TestPeriodicX = true },
            uMaxOverride: 1f);
    }

    /// <summary>Lattice-unit y profile of the time-mean Ux (x, z averaged) over the averaging window; the first node is index 0.</summary>
    static float[] MeanProfile(LbmSolver.LbmSession s, int warm, int avg)
    {
        s.Step(warm); s.ResetAveraging(startNow: true); s.Step(avg);
        var f = s.ToField(); var grid = f.Grid; var prof = new float[PNy - 2];
        for (int y = 1; y < PNy - 1; y++)
        {
            double sum = 0; int n = 0;
            for (int z = 0; z < PNz; z++) for (int x = 1; x < PNx - 1; x++) { sum += f.Ux[grid.Index(x, y, z)]; n++; }
            prof[y - 1] = (float)(sum / n * s.VelocityScale);
        }
        return prof;
    }

    /// <summary>Independent restatement of the Werner-Wengle wall shear u_tau^2 (A = 8.3, B = 1/7), lattice units.</summary>
    static double WernerWengleUtau2(double U, double d, double nu)
    {
        if (U * d / nu <= 11.81 * 11.81) return nu * U / d;
        double ut = Math.Pow(U / (8.3 * Math.Pow(d / nu, 1.0 / 7)), 1.0 / (1 + 1.0 / 7));
        return ut * ut;
    }

    // ---- WM4: closed box with a swirl --------------------------------------------------------------------------------

    static (double Mass, bool Finite) BoxMass(LbmSolver.LbmSession s, VoxelGrid g)
    {
        double mass = 0; bool finite = true; int c = g.Count;
        float[] f = s.NestGpu ? s.NestDCur!.View.GetAsArray1D() : s.NestCur;
        for (int i = 0; i < c; i++)
        {
            if (g.Cell[i] == VoxelGrid.Solid) continue;
            for (int q = 0; q < 19; q++) { float v = f[s.NestGpu ? q * c + i : 19 * i + q]; finite &= float.IsFinite(v); mass += v; }
        }
        return (mass, finite);
    }

    static (double Dm, bool Finite, float PeakU, double Dm1000) RunBox(Backend be, bool model)
    {
        const int n = 14; const float dx = 0.01f;
        var g = new VoxelGrid { Nx = n, Ny = n, Nz = n, Dx = dx, Origin = Vector3.Zero, Cell = new byte[n * n * n] };
        for (int z = 0; z < n; z++) for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            g.Cell[g.Index(x, y, z)] = x == 0 || y == 0 || z == 0 || x == n - 1 || y == n - 1 || z == n - 1 ? VoxelGrid.Solid : VoxelGrid.Air;
        using var s = new LbmSolver.LbmSession(g, Model(n, dx), Vector2.Zero,
            new RunOptions { Backend = be, WallModel = model ? WallModel.WernerWengle : WallModel.None }, uMaxOverride: 1f);
        int[] cx = [0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0];
        int[] cy = [0, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 0, 0, 1, -1, 1, -1];
        int[] cz = [0, 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, -1, 1, 1, -1, -1, 1];
        float c0 = (n - 1) / 2f, k = 0.04f / (c0 * 1.4142f), peak = 0;
        var f = new float[g.Count * 19];
        for (int z = 1; z < n - 1; z++) for (int y = 1; y < n - 1; y++) for (int x = 1; x < n - 1; x++)
        {
            int i = g.Index(x, y, z); float ux = -k * (y - c0), uy = k * (x - c0), uz = 0;   // rigid rotation about the z axis
            peak = Math.Max(peak, MathF.Sqrt(ux * ux + uy * uy));
            for (int q = 0; q < 19; q++)
            {
                float w = q == 0 ? 1f / 3 : q <= 6 ? 1f / 18 : 1f / 36, cu = cx[q] * ux + cy[q] * uy + cz[q] * uz;
                f[s.NestGpu ? q * g.Count + i : 19 * i + q] = w * (1 + 3 * cu + 4.5f * cu * cu - 1.5f * (ux * ux + uy * uy + uz * uz));
            }
        }
        if (s.NestGpu) { s.NestDCur!.View.CopyFromCPU(f); s.NestDNxt!.View.CopyFromCPU(f); } else { f.CopyTo(s.NestCur, 0); f.CopyTo(s.NestNxt, 0); }
        var (m0, fin0) = BoxMass(s, g);
        s.Step(1000); double dm1000 = Math.Abs(BoxMass(s, g).Mass - m0) / m0;
        s.Step(2000);
        var (m1, fin1) = BoxMass(s, g);
        return (Math.Abs(m1 - m0) / m0, fin0 && fin1, peak, dm1000);
    }

    /// <summary>Runs every wall-model check and returns the number of failures (0 = all passed).</summary>
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
        Console.WriteLine("== wall-model checks ==");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // WM1: laminar safety. nu_phys = nu_lat (no floor), so the viscous branch gives u_s* = U (1 - nu_p/nu_w) = 0.
        var off = RunLaminar(false); var on = RunLaminar(true);
        float peak = off.Profile.Max(), dev = off.Profile.Zip(on.Profile, (a, b) => Math.Abs(a - b)).Max() / peak;
        Check("WM1 laminar Poiseuille (no floor binding): wall model on within 2% of off",
            dev <= 0.02f && Math.Abs(off.NuLat - off.NuPhys) < 1e-3f * off.NuPhys,
            $"max|on-off|/peak={dev * 100:F4}% nu_lat={off.NuLat:E3} nu_phys={off.NuPhys:E3} ({sw.Elapsed.TotalSeconds:F0}s)");

        // WM2a: sign. Wall at -x (solid plane x = 0), fluid at rest, slip state u_s = +0.01 x. The populations handed to the
        // collision must carry +rho u_s of extra x momentum (the wall drags the fluid toward +x).
        {
            var g = new VoxelGrid { Nx = 3, Ny = 3, Nz = 3, Dx = 0.01f, Cell = new byte[27] };
            for (int z = 0; z < 3; z++) for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) g.Cell[g.Index(x, y, z)] = x == 0 ? VoxelGrid.Solid : VoxelGrid.Air;
            var cur = new float[27 * 19];
            for (int i = 0; i < 27; i++) for (int q = 0; q < 19; q++) cur[19 * i + q] = q == 0 ? 1f / 3 : q <= 6 ? 1f / 18 : 1f / 36;
            var flags = new byte[27]; int node = g.Index(1, 1, 1);
            var wm = new float[81]; wm[3 * node] = 0.01f / 0.9f;      // 0.9 survives the relaxation toward the zero target
            float baseline = LbmSolver.GatherMomentumX(g, cur, flags, 1, 1, 1, null, 0f, 0f);
            float driven = LbmSolver.GatherMomentumX(g, cur, flags, 1, 1, 1, wm, 1.2e-4f, 5e-4f);
            Check("WM2a sign: u_s = +x on a wall at -x adds +rho u_s of x momentum to the node (Bouzidi qw = 1/2)",
                MathF.Abs(baseline) < 1e-6f && driven > 0 && MathF.Abs(driven - 0.01f) < 2e-4f, $"baseline={baseline:E2} with u_s={driven:E4} (expect 1.0000E-002)");
        }

        // WM2b: bulk speed-up and the law at the first node.
        const int warm = 400_000, avg = 40_000;
        float[] pOff, pOn;
        using (var s = PeriodicSession(Backend.Cpu, false, PGravity)) pOff = MeanProfile(s, warm, avg);
        using (var s = PeriodicSession(Backend.Cpu, true, PGravity)) pOn = MeanProfile(s, warm, avg);
        double bulkOff = pOff.Average(), bulkOn = pOn.Average();
        double nuP = NuAir * ULat / PDx;   // dt = ULat dx / uMax, uMax = 1  ->  nu_p = NuAir dt / dx^2
        double u1 = 0.5 * (pOn[0] + pOn[^1]), d = 0.5, gh = PGravity * PHalf, ut2 = WernerWengleUtau2(u1, d, nuP);
        Check("WM2b force-driven periodic channel, floor binding: WW law at the first node's mean U gives u_tau^2 within 15% of G h",
            Math.Abs(ut2 / gh - 1) <= 0.15, $"nu_p={nuP:E3} (floor {NuFloor:E1}) U1={u1:E4} d={d} u_tau^2(WW)={ut2:E4} G h={gh:E4} ratio={ut2 / gh:F3} ({sw.Elapsed.TotalSeconds:F0}s)");
        Check("WM2c wall model on raises the bulk velocity over no-slip bounce-back", bulkOn > bulkOff,
            $"bulk off={bulkOff:E4} on={bulkOn:E4} ratio={bulkOn / bulkOff:F3} first-node off={0.5 * (pOff[0] + pOff[^1]):E4} on={u1:E4}");

        // WM3: CPU = GPU after 500 steps (a 20x stronger drive than WM2 so the slip is well off zero by then).
        if (!LbmSolver.GpuAvailable) Console.WriteLine("[WAIT] WM3 CPU = GPU  no GPU available");
        else
        {
            float[] pc, pg;
            using (var s = PeriodicSession(Backend.Cpu, true, 20 * PGravity)) pc = MeanProfile(s, 0, 500);
            using (var s = PeriodicSession(Backend.Gpu, true, 20 * PGravity)) pg = MeanProfile(s, 0, 500);
            float pk = pc.Max(), dd = pc.Zip(pg, (a, b) => Math.Abs(a - b)).Max() / pk;
            Check("WM3 CPU = GPU on the WM2 channel after 500 steps (within 1e-3 of peak)", dd < 1e-3f && pk > 0, $"max|cpu-gpu|/peak={dd:E2} peak={pk:E3}");
        }

        // WM4: mass in a closed box.
        foreach (var be in Backends)
        {
            var (dm0, _, _, _) = RunBox(be, false);
            var (dm, fin, pu, dm1k) = RunBox(be, true);
            // The float32 weights do not sum to exactly 1, so a sealed box drifts ~1.5e-8/step with or without the model (a fixed 1e-5 limit
            // cannot pass either way); the model must not add to that drift: within 10 % of the model-off drift.
            Check($"WM4 closed box with swirl, wall model on ({be}): |dMass|/mass drift within 10 % of the model-off drift over 3000 steps, no NaN", fin && dm <= 1.1 * dm0,$"dm/m={dm:E2} (model off: {dm0:E2}; at 1000 steps {dm1k:E2}) finite={fin} swirl peak u={pu:F3}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL WALL-MODEL CHECKS PASSED" : $"{failures} WALL-MODEL CHECK(S) FAILED");
        return failures;
    }
}
