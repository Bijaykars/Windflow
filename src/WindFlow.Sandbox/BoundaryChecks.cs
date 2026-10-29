// Equilibrium-reset boundary cells (Return / Fan patches and the far-field ring): their density must be steady from
// step to step and the same on both backends.  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --boundary      (or as part of the default run)
//
// These cells are rewritten every step into whichever population buffer is current and are never streamed. The
// second buffer was never initialised, so after each swap it held zeros on the CPU (a Return/Fan cell then read its
// own rho as 0, clamped to 0.9, and flipped 0.9 <-> 1.0 every step; ring cells read rho = 0, NaN live velocity) and
// uninitialised device memory on the GPU. Fixed cells now impose rho0 = 1 and both buffers start as copies.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class BoundaryChecks
{
    // RZ-09: 2 x the 300-step inlet ramp, so the checks also see the state after it.
    const int Steps = 600, RampSteps = 300;
    // P13b-07 (6b): the NEE inflow face carries the interior rho, which the 300-step start-up ramp lifts to ~1.08 (fan scene); was 1.05 for a rho = 1 ring.
    const float RingRhoMax = 1.10f;
    // RZ-09: after the ramp the inflow-ring rho must come back to 1 +- 0.02, and no ring cell may flip by more than 2e-3 from step to step.
    const float InflowRhoBand = 0.02f, RingFlipMax = 2e-3f;

    /// <summary>Empty 64 x 24 x 24 domain (Dx 0.125 m), uniform 4 m/s wind: mean and turbulence intensity of the
    /// instantaneous u at 3 L downstream of the inflow face, mid-height, over 8 lateral points and many steps.</summary>
    static (float Mean, float Iu) TurbulenceProbe(Backend backend, bool turbulent)
    {
        const int nx = 64, ny = 24, nz = 24, L = 4;
        var grid = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = 0.125f, Cell = new byte[nx * ny * nz] };
        var model = new BuildingModel { Name = "tu", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = nz * 0.125f, Analysed = true } } };
        var options = new RunOptions { Backend = backend, WindProfileExponent = 0f };
        if (turbulent) options.WindTurbulence = new WindTurbulence { LengthScale = L * 0.125f, RefIu = 0.10f, Exponent = 0f };
        using var s = LbmSolver.Start(grid, model, new Vector2(4f, 0f), options);
        var ux = new float[grid.Count]; var uy = new float[grid.Count]; var uz = new float[grid.Count];
        int px = 1 + 3 * L, pz = nz / 2;
        double sum = 0, sq = 0; int cnt = 0;
        s.Step(3000);
        for (int it = 0; it < 4000; it++)
        {
            s.Step(4); s.ReadVelocity(ux, uy, uz);
            for (int j = 0; j < 8; j++)
            {
                float v = ux[grid.Index(px, 4 + 2 * j, pz)];
                sum += v; sq += (double)v * v; cnt++;
            }
        }
        double mean = sum / cnt;
        return ((float)mean, (float)(Math.Sqrt(Math.Max(0, sq / cnt - mean * mean)) / mean));
    }

    /// <summary>Runs every boundary-density check and returns the number of failures (0 = all passed).</summary>
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

        Console.WriteLine("== boundary density checks ==");
        // supply: sealed room, supply + Return patch, no wind (open ring). fan: Fan patch, cross wind (inflow ring).
        // unbalanced (RW-01, 57g): the supply scene with the extract set to half the supply; the sealed room is balanced (extract scaled to the supply), and the scaled Return cells must hold their rho like any other.
        var unbalanced = Program.BuildSupply();
        unbalanced.Sources[1].FlowRate = 0.5f * unbalanced.Sources[0].FlowRate;
        var scenes = new (string Id, string Name, BuildingModel Model, Vector2 Wind)[]
        {
            ("a", "supply", Program.BuildSupply(), Vector2.Zero),
            ("b", "fan", Program.BuildFan(), new Vector2(3, 0)),
            ("g", "unbalanced", unbalanced, Vector2.Zero),
        };
        var cpu = new Trace[scenes.Length];
        for (int s = 0; s < scenes.Length; s++)
        {
            var t = cpu[s] = RunScene(scenes[s].Model, scenes[s].Wind, Backend.Cpu);
            Check($"57{scenes[s].Id} {scenes[s].Name}: Return/Fan and ring cells hold a steady rho for {Steps} steps (CPU)",
                t.FixedFlip <= 1e-4f && t.RingMin >= 0.95f && t.RingMax <= RingRhoMax && t.NeighbourFlip <= 2e-3f &&
                t.RingFlip <= RingFlipMax && (t.InflowCells == 0 || (t.InflowMin >= 1f - InflowRhoBand && t.InflowMax <= 1f + InflowRhoBand)),
                $"fixed_flip={t.FixedFlip:E2} fixed_min={t.FixedMin:F4} fixed_max={t.FixedMax:F4} ring_min={t.RingMin:F4} ring_max={t.RingMax:F4} " +
                $"ring_flip_after_ramp={t.RingFlip:E2} final_inflow_rho={t.InflowMin:F4}..{t.InflowMax:F4} ({t.InflowCells} cells) " +
                $"neighbour_flip={t.NeighbourFlip:E2} neighbour_mean={t.NeighbourMean:F5} fixed_cells={t.FixedCells} ring_cells={t.RingCells}");
        }

        if (!LbmSolver.GpuAvailable) Skip("57c boundary rho GPU = CPU", "no GPU available");
        else
        {
            bool ok = true; var parts = new List<string>();
            for (int s = 0; s < scenes.Length; s++)
            {
                var g = RunScene(scenes[s].Model, scenes[s].Wind, Backend.Gpu);
                float d = 0f;
                for (int i = 0; i < g.Final.Length; i++) d = MathF.Max(d, MathF.Abs(g.Final[i] - cpu[s].Final[i]));
                ok &= d <= 1e-3f && g.FixedFlip <= 1e-4f && g.RingMin >= 0.95f && g.RingMax <= RingRhoMax;
                parts.Add($"{scenes[s].Name}_max_diff={d:E2} {scenes[s].Name}_gpu_fixed_flip={g.FixedFlip:E2} " +
                          $"{scenes[s].Name}_gpu_ring_min={g.RingMin:F4} {scenes[s].Name}_gpu_neighbour_mean={g.NeighbourMean:F5}");
            }
            Check($"57c boundary-cell and patch-neighbour rho: GPU = CPU within 1e-3 after {Steps} steps (supply, fan, unbalanced)", ok, string.Join(" ", parts));
        }

        Console.WriteLine();
        // Synthetic wind-inflow turbulence (RunOptions.WindTurbulence): empty domain, uniform inflow, target Iu 10 %, L = 4 cells.
        {
            var plain = TurbulenceProbe(Backend.Cpu, false);
            var tCpu = TurbulenceProbe(Backend.Cpu, true);
            bool nan = float.IsNaN(tCpu.Mean) || float.IsNaN(tCpu.Iu) || float.IsNaN(plain.Mean);
            float dMean = MathF.Abs(tCpu.Mean / plain.Mean - 1f);
            // Worst case for frozen convection: zero convection speed (every mode behaves as k perpendicular to U), so only the
            // Smirnov temporal frequency omega_m = g_m / tau can decorrelate. Time-averaged u' autocorrelation at lag 5 tau must be < 0.2.
            const double tau = 1000, lag = 5 * tau;
            var fld = new WindTurbulenceField(64, 4f, 12345); fld.SetTimeScale(tau);
            int ns = 4000; var us = new double[ns + (int)(lag / (tau / 4))];
            for (int i = 0; i < us.Length; i++) { fld.Eval(0, 0, 0, (float)(i * tau / 4), 0, 0, out float gx, out _, out _); us[i] = gx; }
            double r0 = 0, rl = 0; int sh = (int)(lag / (tau / 4));
            for (int i = 0; i < ns; i++) { r0 += us[i] * us[i]; rl += us[i] * us[i + sh]; }
            double acf = rl / r0;
            Check("57d turbulent inflow (Iu 10 %, L = 4 cells), 3 L downstream: mean u within 3 % of no-turbulence, Iu within 50 % of target, no NaN (CPU); inlet u' autocorrelation at 5 tau < 0.2 with no convection",
                !nan && dMean <= 0.03f && MathF.Abs(tCpu.Iu / 0.10f - 1f) <= 0.5f && acf < 0.2,
                $"mean_ratio_err={dMean:F4} Iu={tCpu.Iu:F4} (target 0.10) mean={tCpu.Mean:F4} plain_mean={plain.Mean:F4} plain_Iu={plain.Iu:F4} acf5tau={acf:F3}");
            if (!LbmSolver.GpuAvailable) Skip("57e turbulent inflow GPU = CPU", "no GPU available");
            else
            {
                var tGpu = TurbulenceProbe(Backend.Gpu, true);
                float dm = MathF.Abs(tGpu.Mean / tCpu.Mean - 1f);
                Check("57e turbulent inflow: GPU mean within 2 % of CPU, Iu within 50 % of target, no NaN", !float.IsNaN(tGpu.Mean) && dm <= 0.02f && MathF.Abs(tGpu.Iu / 0.10f - 1f) <= 0.5f,
                    $"mean_diff={dm:F4} gpu_mean={tGpu.Mean:F4} cpu_mean={tCpu.Mean:F4} gpu_Iu={tGpu.Iu:F4} cpu_Iu={tCpu.Iu:F4}");
            }
        }

        if (!LbmSolver.GpuAvailable) Skip("57f turbulent inflow host = GPU kernel", "no GPU available");
        else
        {
            // Oblique 4 m/s wind so both convection components and the rotated components are exercised, late in a run.
            const int nx = 64, ny = 24, nz = 24;
            var grid = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = 0.125f, Cell = new byte[nx * ny * nz] };
            var model = new BuildingModel { Name = "tu", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = nz * 0.125f, Analysed = true } } };
            var options = new RunOptions { Backend = Backend.Gpu, WindProfileExponent = 0f,
                WindTurbulence = new WindTurbulence { LengthScale = 0.5f, RefIu = 0.10f, Exponent = 0f } };
            using var s = LbmSolver.Start(grid, model, new Vector2(4f, 1.5f), options);
            double worst = 0; float uRef = 0; int entries = 0;
            foreach (long step in new long[] { 100_000, 100_001, 100_002, 100_003, 3 })
            {
                var (h, g, u) = s.TurbulenceCompare(step);
                uRef = u; entries = h.Length / 3;
                for (int i = 0; i < h.Length; i++) worst = Math.Max(worst, Math.Abs(h[i] - g![i]));
            }
            Check("57f turbulent inflow: GPU kernel = host version within 1e-4 U_ref at step ~1e5 (oblique wind)",
                entries > 0 && worst <= 1e-4 * uRef, $"max_du={worst:E2} U_ref={uRef:F4} (lattice) rel={worst / uRef:E2} entries={entries}");
        }

        Console.WriteLine(failures == 0 ? "ALL BOUNDARY CHECKS PASSED" : $"{failures} BOUNDARY CHECK(S) FAILED");
        return failures;
    }

    /// <summary>Per-step extremes over the Return/Fan cells, the ring and the air cells beside a Return/Fan cell;
    /// Final = rho at those cells (fixed, then ring, then neighbours) after the last step.</summary>
    readonly record struct Trace(float FixedFlip, float FixedMin, float FixedMax, float RingMin, float RingMax,
        float NeighbourFlip, float NeighbourMean, int FixedCells, int RingCells, float[] Final,
        float RingFlip = 0f, float InflowMin = float.NaN, float InflowMax = float.NaN, int InflowCells = 0);

    static Trace RunScene(BuildingModel model, Vector2 wind, Backend backend)
    {
        var grid = Voxelizer.Build(model, 250_000);
        // Batch 3b (RV-01 follow-up): Return and Fan patches are link-wise now (FixedCell.Mode != 0), so they are no longer Supply < 0; keep them in the set this check watches.
        var fixedIdx = grid.FixedCells.Where(f => f.Supply < 0 || f.Mode != 0).Select(f => f.Index).ToArray();
        var ring = new List<int>(); var inflow = new List<int>();
        float wl = wind.Length();
        for (int z = 0; z < grid.Nz; z++)
        for (int y = 0; y < grid.Ny; y++)
        for (int x = 0; x < grid.Nx; x++)
        {
            if (x != 0 && x != grid.Nx - 1 && y != 0 && y != grid.Ny - 1) continue;
            int i = grid.Index(x, y, z);
            if (grid.Cell[i] == VoxelGrid.Solid) continue;
            ring.Add(i);
            // RZ-09: inflow-face cells (the solver's rule: wind . outward normal < -0.1 |wind|), corners left out
            bool onX = x == 0 || x == grid.Nx - 1, onY = y == 0 || y == grid.Ny - 1;
            float sn = onX && !onY ? (x == 0 ? -wind.X : wind.X) : onY && !onX ? (y == 0 ? -wind.Y : wind.Y) : 0f;
            if (wl > 1e-6f && sn < -0.1f * wl) inflow.Add(i);
        }
        var neighbours = new HashSet<int>();
        foreach (int i in fixedIdx)
        {
            int x = i % grid.Nx, y = i / grid.Nx % grid.Ny, z = i / (grid.Nx * grid.Ny);
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            {
                int a = x + dx, b = y + dy, c = z + dz;
                if (a < 0 || b < 0 || c < 0 || a >= grid.Nx || b >= grid.Ny || c >= grid.Nz) continue;
                int j = grid.Index(a, b, c);
                if (grid.Cell[j] == VoxelGrid.Air) neighbours.Add(j);
            }
        }
        var nb = neighbours.ToArray();

        using var session = LbmSolver.Start(grid, model, wind, new RunOptions { Backend = backend });
        var rho = new float[grid.Count];
        var prevFixed = new float[fixedIdx.Length]; var prevNb = new float[nb.Length];
        float fixedFlip = 0f, fixedMin = float.MaxValue, fixedMax = float.MinValue, ringMin = float.MaxValue, ringMax = float.MinValue;
        float nbFlip = 0f, ringFlip = 0f;
        var prevRing = new float[ring.Count];
        for (int s = 0; s < Steps; s++)
        {
            session.Step(1);
            session.ReadDensity(rho);
            for (int k = 0; k < fixedIdx.Length; k++)
            {
                float r = rho[fixedIdx[k]];
                if (s > 0) fixedFlip = MathF.Max(fixedFlip, MathF.Abs(r - prevFixed[k]));
                prevFixed[k] = r; fixedMin = MathF.Min(fixedMin, r); fixedMax = MathF.Max(fixedMax, r);
            }
            for (int k = 0; k < ring.Count; k++)
            {
                float r = rho[ring[k]];
                ringMin = MathF.Min(ringMin, r); ringMax = MathF.Max(ringMax, r);
                if (s > RampSteps) ringFlip = MathF.Max(ringFlip, MathF.Abs(r - prevRing[k]));
                prevRing[k] = r;
            }
            for (int k = 0; k < nb.Length; k++)
            {
                float r = rho[nb[k]];
                // skip the start-up ramp: judge the last half of the run only
                if (s >= Steps / 2) nbFlip = MathF.Max(nbFlip, MathF.Abs(r - prevNb[k]));
                prevNb[k] = r;
            }
        }
        var final = fixedIdx.Concat(ring).Concat(nb).Select(i => rho[i]).ToArray();
        float nbMean = nb.Length > 0 ? nb.Average(i => rho[i]) : float.NaN;
        float inMin = inflow.Count > 0 ? inflow.Min(i => rho[i]) : float.NaN, inMax = inflow.Count > 0 ? inflow.Max(i => rho[i]) : float.NaN;
        return new Trace(fixedFlip, fixedMin, fixedMax, ringMin, ringMax, nbFlip, nbMean, fixedIdx.Length, ring.Count, final, ringFlip, inMin, inMax, inflow.Count);
    }
}
