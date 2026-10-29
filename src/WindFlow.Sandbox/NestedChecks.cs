// Voxelizer seal seam (check 47) and the nested 2:1 fine box (checks 48a-d).  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --nested      (or as part of the default run)

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class NestedChecks
{
    /// <summary>Runs every check here and returns the number of failures (0 = all passed).</summary>
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

        Console.WriteLine("== nested / voxelizer checks ==");
        SealCheck(Check);
        WholeDomainNestChecks(Check);
        HouseBoxChecks(Check);
        SpeedProbe();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL NESTED CHECKS PASSED" : $"{failures} NESTED CHECK(S) FAILED");
        return failures;
    }

    // ---------------------------------------------------------------------------------------------
    // 47. Voxelizer.Build(sealUnreachable: false): the sample house with every opening shut and no Room
    //     loops (a raw mesh before room detection). Sealed, its whole interior is unreachable and becomes
    //     Solid; unsealed, the interior air is still there for a detector to find.
    // ---------------------------------------------------------------------------------------------

    static void SealCheck(Func<string, bool, string, bool> check)
    {
        var model = SampleHouse.Build();
        model.Rooms.Clear();
        foreach (var o in model.Openings) o.IsOpen = false;

        int Indoor(VoxelGrid g)
        {
            int n = 0;
            for (int z = 0; z < g.Nz; z++)
            for (int y = 0; y < g.Ny; y++)
            for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z);
                if (c.X < 0.3f || c.X > SampleHouse.W - 0.3f || c.Y < 0.3f || c.Y > SampleHouse.D - 0.3f) continue;
                if (c.Z < 0.1f || c.Z > SampleHouse.Roof - 0.1f) continue;
                if (g.Cell[g.Index(x, y, z)] == VoxelGrid.Air) n++;
            }
            return n;
        }

        var raw = Voxelizer.Build(model, 300_000, sealUnreachable: false);
        var sealedGrid = Voxelizer.Build(model, 300_000);
        int rawIndoor = Indoor(raw), sealedIndoor = Indoor(sealedGrid);
        check("47 Voxelizer.Build(sealUnreachable: false) keeps a shut, Room-less interior; the sealed build removes it",
            rawIndoor > 1000 && sealedIndoor == 0 && raw.Count == sealedGrid.Count,
            $"indoor air cells unsealed={rawIndoor} sealed={sealedIndoor} grid={raw.Nx}x{raw.Ny}x{raw.Nz} Dx={raw.Dx * 1000:F0}mm");
    }

    /// <summary>48 NOTE (printed, not asserted -- GPU MLUPS swings under a shared GPU): the app's live case,
    /// the sample house at the GPU Preview budget with a Bedroom 1 box, timed per coarse step.
    /// Total = (coarse + 2 x fine cells) per coarse step / wall time, as the app reports it.</summary>
    public static void SpeedProbe()
    {
        if (!LbmSolver.GpuAvailable) { Console.WriteLine("[NOTE] 48 nested speed  no GPU available"); return; }
        var wind = new Vector2(0, 3);
        var model = SampleHouse.Build();
        var opts = new RunOptions { Backend = Backend.Gpu };
        var cg = Voxelizer.Build(model, Simulation.TargetCells(Quality.Preview, true));
        using var coarse = LbmSolver.Start(cg, model, wind, opts);
        var box = new DomainBox(new Vector3(-0.85f, -0.85f, 2.2f), new Vector3(6.4f, 8.85f, 7f));
        using var nest = LbmSolver.StartNested(coarse, box, opts, driveCoarse: true);
        nest.Step(50);                                   // JIT and warm-up
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        coarse.Step(200);
        double coarseOnly = sw0.Elapsed.TotalSeconds / 200;
        const int steps = 300;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        nest.Step(steps);
        double s = sw.Elapsed.TotalSeconds;
        double total = (double)(cg.Count + 2L * nest.FineCells) * steps / s / 1e6;
        string detail = $"coarse={cg.Count} fine={nest.FineCells} " +
            $"coarse step alone {coarseOnly * 1000:F2} ms | nested ms_per_coarse_step={s / steps * 1000:F2} = {steps / s:F0} coarse steps/s, " +
            $"total_mlups={total:F0} fine_mlups={nest.Mlups:F0} | {nest.Backend}";
        Console.WriteLine($"[NOTE] 48 nested speed (sample house, Bedroom 1 box, GPU)  {detail}");
        CheckLog.Record("NOTE", "48 nested speed (sample house, Bedroom 1 box, GPU)", detail);
    }

    // ---------------------------------------------------------------------------------------------
    // 48. Nested 2:1 fine box (LbmSolver.StartNested), one-way from a coarse run.
    // ---------------------------------------------------------------------------------------------

    const int CrossCoarseCells = 50_000;   // the cross scene at 1/8 of its usual budget: the fine box is then the usual grid

    /// <summary>Runs a nested box for a coarse session's planned length, averaging the same window the coarse
    /// run would (after its warm-up).</summary>
    static (FlowField Field, NestedStats Stats) RunNested(LbmSolver.LbmSession coarse, DomainBox box, RunOptions opts)
    {
        using var nest = LbmSolver.StartNested(coarse, box, opts, driveCoarse: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        nest.Step(nest.WarmupSteps);
        nest.ResetAveraging();
        nest.Step(Math.Max(1, nest.PlannedSteps - nest.WarmupSteps));
        sw.Stop();
        var field = nest.ToField();
        double mlups = (double)nest.FineCells * 2 * nest.Steps / sw.Elapsed.TotalSeconds / 1e6;
        return (field, new NestedStats(nest.FineCells, nest.FaceCells, nest.SpongeCells, nest.InflowCells, nest.Steps, mlups,
            sw.Elapsed.TotalSeconds, nest.Backend, BoxResidual(field)));
    }

    readonly record struct NestedStats(int FineCells, int FaceCells, int SpongeCells, int InflowCells, long CoarseSteps,
        double Mlups, double Seconds, string Backend, (float In, float Out, float Residual) Ledger)
    {
        public override string ToString() =>
            $"fine={FineCells} cells (faces {FaceCells}, {InflowCells} inflow; sponge {SpongeCells}) coarseSteps={CoarseSteps} " +
            $"{Seconds:F1}s {Mlups:F1} fine-MLUPS {Backend}";
    }

    /// <summary>The fine box's own mass ledger: volume flux across the planes one cell inside each face
    /// (the four sides, and the top -- zero on a free-slip lid), in versus out.</summary>
    static (float In, float Out, float Residual) BoxResidual(FlowField f)
    {
        var g = f.Grid;
        double inflow = 0, outflow = 0, a = g.Dx * g.Dx;
        void Add(int idx, float un)
        {
            if (g.Cell[idx] == VoxelGrid.Solid) return;
            if (un > 0) outflow += un * a; else inflow -= un * a;
        }
        for (int z = 1; z < g.Nz - 1; z++)
        for (int y = 1; y < g.Ny - 1; y++) { Add(g.Index(1, y, z), -f.Ux[g.Index(1, y, z)]); Add(g.Index(g.Nx - 2, y, z), f.Ux[g.Index(g.Nx - 2, y, z)]); }
        for (int z = 1; z < g.Nz - 1; z++)
        for (int x = 2; x < g.Nx - 2; x++) { Add(g.Index(x, 1, z), -f.Uy[g.Index(x, 1, z)]); Add(g.Index(x, g.Ny - 2, z), f.Uy[g.Index(x, g.Ny - 2, z)]); }
        for (int y = 2; y < g.Ny - 2; y++)
        for (int x = 2; x < g.Nx - 2; x++) Add(g.Index(x, y, g.Nz - 2), f.Uz[g.Index(x, y, g.Nz - 2)]);
        float max = (float)Math.Max(inflow, outflow);
        return ((float)inflow, (float)outflow, max > 1e-9f ? (float)(Math.Abs(inflow - outflow) / max) : 0f);
    }

    static float OpeningFlow(BuildingModel model, FlowField field, Vector2 wind, string id)
    {
        var r = Analysis.Analyze(model, field, wind, new RunOptions());
        return r.Openings.First(o => o.Opening.Id == id).FlowRate;
    }

    /// <summary>48a, 48c, 48d(a): the cross scene with the box = the whole coarse domain, so the fine box is
    /// a complete Dx/2 run whose only difference from a uniform fine run is that its faces come from the
    /// coarse field instead of the wind boundary.</summary>
    static void WholeDomainNestChecks(Func<string, bool, string, bool> check)
    {
        var wind = new Vector2(3, 0);
        var model = Program.BuildCross("nest-cross", true, true);

        (float East, float West, NestedStats Stats) Nested(Backend backend)
        {
            var opts = new RunOptions { Backend = backend };
            var cg = Voxelizer.Build(model, CrossCoarseCells);
            using var coarse = LbmSolver.Start(cg, model, wind, opts);
            var box = new DomainBox(cg.Origin + new Vector3(0, 0, cg.Dx), cg.Origin + new Vector3(cg.Nx, cg.Ny, cg.Nz) * cg.Dx);
            var (field, stats) = RunNested(coarse, box, opts);
            return (OpeningFlow(model, field, wind, "east"), OpeningFlow(model, field, wind, "west"), stats);
        }

        var cpu = Nested(Backend.Cpu);

        var fineGrid = Voxelizer.Build(model, 8 * CrossCoarseCells);
        var uniformField = LbmSolver.Solve(fineGrid, model, wind, new RunOptions { Backend = Backend.Cpu });
        float uEast = OpeningFlow(model, uniformField, wind, "east"), uWest = OpeningFlow(model, uniformField, wind, "west");
        float err = MathF.Abs(cpu.East - uEast) / MathF.Max(MathF.Abs(uEast), 1e-6f);
        check("48a nested box = whole domain at Dx/2: east opening flow within 10% of a uniform Dx/2 run",
            err <= 0.10f,
            $"nested Qeast={cpu.East:F3} Qwest={cpu.West:F3} | uniform Qeast={uEast:F3} Qwest={uWest:F3} (Dx {fineGrid.Dx * 1000:F0}mm " +
            $"{fineGrid.Nx}x{fineGrid.Ny}x{fineGrid.Nz}) err={err * 100:F1}% | {cpu.Stats}");

        var (inF, outF, res) = cpu.Stats.Ledger;
        check("48d(a) nested whole-domain box mass ledger residual <= 10%", res <= 0.10f,
            $"in={inF:F3} out={outF:F3} m3/s residual={res * 100:F1}%");

        if (!LbmSolver.GpuAvailable) { Console.WriteLine("[WAIT] 48c nested GPU = CPU within 1%  no GPU available"); CheckLog.Record("WAIT", "48c nested GPU = CPU within 1%", "no GPU available"); return; }
        var gpu = Nested(Backend.Gpu);
        float gErr = MathF.Abs(gpu.East - cpu.East) / MathF.Max(MathF.Abs(cpu.East), 1e-6f);
        check("48c nested box GPU = CPU within 1% (east opening flow)", gErr <= 0.01f,
            $"cpu={cpu.East:F4} gpu={gpu.East:F4} err={gErr * 100:F2}% | {gpu.Stats}");
    }

    /// <summary>48b, 48d(b): the sample house, a box around the hall whose west face cuts through the living
    /// room (L1, west of the x = 7 partition) and whose top face cuts through the landing (L2, above the
    /// hall and the stair void) -- the v1 simulation-box case whose open faces gave a door flow 5x the
    /// whole-building value. Nested, the fine box's opening flows must follow the whole-building run that
    /// drives it. The compared openings (hall window, kitchen-hall door) sit clear of the 8-cell (1.6 m)
    /// sponge band, where the fine field is by design pulled toward the coarse one; the landing's own door
    /// is cut by the top face and is not compared.</summary>
    static void HouseBoxChecks(Func<string, bool, string, bool> check)
    {
        var wind = new Vector2(0, 3);   // from the south: in through the south windows, out north and east
        var model = SampleHouse.Build();
        var opts = new RunOptions { Backend = Backend.Cpu };
        var cg = Voxelizer.Build(model, 400_000);
        using var coarse = LbmSolver.Start(cg, model, wind, opts);
        coarse.Step(coarse.WarmupSteps);            // develop the whole building first

        var box = new DomainBox(new Vector3(6.0f, 2.4f, 0f), new Vector3(14.4f, 10.0f, 4.4f));
        var (fine, stats) = RunNested(coarse, box, opts);
        var coarseField = coarse.ToField();          // same averaging window: ResetAveraging reset both

        string[] ids = ["w-hall-e", "d-kit-hall"];
        // Reference: the whole building at the box's own Dx (8x the coarse cells). The coarse run is NOT
        // the reference -- at 395 mm a 0.9 m door is 2.3 cells, and the coarse run's own opening flows sit
        // ~25 % off the resolved ones (measured 2026-09-24: hall window 1.126 vs 1.519, door 1.050 vs 0.840),
        // which is resolution, not nesting. GPU when there is one: 3.2 M cells.
        var ufGrid = Voxelizer.Build(model, 8 * 400_000);
        var ufField = LbmSolver.Solve(ufGrid, model, wind, new RunOptions { Backend = LbmSolver.GpuAvailable ? Backend.Gpu : Backend.Cpu });
        var ru = Analysis.Analyze(model, ufField, wind, opts);
        var rc = Analysis.Analyze(model, coarseField, wind, opts);
        var rf = Analysis.Analyze(model, fine, wind, opts);
        // The v1 simulation box (ordinary open faces) on the same box and cell count, for contrast.
        var v1 = Simulation.Run(model, wind, new RunOptions { Backend = Backend.Cpu, Domain = box, TargetCells = stats.FineCells });
        float Q(SimulationResult r, string id) => r.Openings.First(o => o.Opening.Id == id).FlowRate;
        var parts = new List<string>();
        bool ok = true;
        foreach (var id in ids)
        {
            float qu = Q(ru, id), qf = Q(rf, id);
            float e = MathF.Abs(qf - qu) / MathF.Max(MathF.Abs(qu), 1e-6f);
            ok &= e <= 0.25f;
            parts.Add($"{id} fine box={qf:F3} uniform={qu:F3} ({e * 100:F0}%) coarse={Q(rc, id):F3} v1 open-face box={Q(v1, id):F3}");
        }
        // Band 25 % unchanged by the carved-throat opening flow: box and uniform run share Dx and the same throat
        // integral, so the ratio still tests nesting alone. The kitchen-hall door's gap went 6 % -> 10 % (both sides
        // moved +12-17 %); an interior door is where one-way nesting cannot correct the coarse pressure balance.
        check("48b sample-house box across the living room and landing: fine opening flows within 25% of the uniform whole-building run at the box's Dx",
            ok, string.Join(" | ", parts) + $" | coarse Dx={cg.Dx * 1000:F0}mm uniform Dx={ufGrid.Dx * 1000:F0}mm " +
            $"({ufGrid.Count / 1e6:F1}M cells) | {stats}");
        var (inF, outF, res) = stats.Ledger;
        check("48d(b) sample-house fine box mass ledger residual <= 10%", res <= 0.10f,
            $"in={inF:F3} out={outF:F3} m3/s residual={res * 100:F1}%");

        // 48e: the sponge-band warning. The living-kitchen door (x = 7, about 1 m inside the west face) and the
        // Bedroom 2 door (cut by the top face) reach into the band; the two compared openings sit clear of it.
        var band = fine.Grid.Warnings.Where(w => w.Contains("sponge band")).ToList();
        bool Named(string name) => band.Any(w => w.StartsWith($"Opening {name} "));
        check("48e nested box warns for openings inside its sponge band, and only for those",
            Named("Living to kitchen") && Named("Bedroom 2 door") && !Named("Hall window") && !Named("Kitchen to hall"),
            $"band_warnings={band.Count}: " + string.Join(" / ", band.Select(w => w[..w.IndexOf(" is ")])));

        // 48f: a live session analyses the same grid on every pass; the grid's warnings must not grow.
        int f0 = fine.Grid.Warnings.Count, c0 = coarseField.Grid.Warnings.Count;
        Analysis.Analyze(model, fine, wind, opts);
        Analysis.Analyze(model, coarseField, wind, opts);
        int f1 = fine.Grid.Warnings.Count, c1 = coarseField.Grid.Warnings.Count;
        int roomLines = fine.Grid.Warnings.Count(w => w.StartsWith("Room ")) + coarseField.Grid.Warnings.Count(w => w.StartsWith("Room "));
        check("48f Analysis.Analyze again on the same grids adds no duplicate warnings", f1 == f0 && c1 == c0,
            $"fine_warnings={f0}->{f1} coarse_warnings={c0}->{c1} room_lines={roomLines}");
    }
}

/// <summary>The app's two-storey, seven-room sample house (src/WindFlow.App/SampleBuilding.cs), rebuilt here
/// without its (disabled) App-side fan so the harness does not depend on the App project. Same solids,
/// openings and room loops.</summary>
static class SampleHouse
{
    public const float ExtT = 0.25f, IntT = 0.12f, SlabT = 0.2f;
    public const float L1 = 0f, L2 = 3f, Roof = 6f;
    public const float W = 12f, D = 8f;

    public static BuildingModel Build()
    {
        var m = new BuildingModel
        {
            Name = "Sample house", GroundZ = 0,
            Storeys =
            {
                new Storey { Id = "L1", Name = "Level 1", Elevation = L1, Height = 3, Analysed = true },
                new Storey { Id = "L2", Name = "Level 2", Elevation = L2, Height = 3, Analysed = true },
            },
        };
        m.Obstacles.Add(Wall(0, 0, W, 0, ExtT, 0, Roof));
        m.Obstacles.Add(Wall(W, 0, W, D, ExtT, 0, Roof));
        m.Obstacles.Add(Wall(0, D, W, D, ExtT, 0, Roof));
        m.Obstacles.Add(Wall(0, 0, 0, D, ExtT, 0, Roof));
        m.Obstacles.Add(Box(new Vector3(-1, -1, -0.2f), new Vector3(W + 1, D + 1, 0)));
        m.Obstacles.Add(Box(new Vector3(0, 0, Roof), new Vector3(W, D, Roof + SlabT)));

        float sz0 = L2, sz1 = L2 + SlabT;
        m.Obstacles.Add(Box(new Vector3(0, 0, sz0), new Vector3(W, 5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(0, 7.5f, sz0), new Vector3(W, D, sz1)));
        // RX-02: the stair void is x 7.1..8.9, y 5..7.5, as in src/WindFlow.App/SampleBuilding.cs (the source: keep the two in sync; P12-03). The old 6 / 8.5 strips straddled the x = 7 partition.
        m.Obstacles.Add(Box(new Vector3(0, 5f, sz0), new Vector3(7.1f, 7.5f, sz1)));
        m.Obstacles.Add(Box(new Vector3(8.9f, 5f, sz0), new Vector3(W, 7.5f, sz1)));

        m.Obstacles.Add(Wall(7, 0, 7, D, IntT, L1, L2));
        m.Obstacles.Add(Wall(7, 4.5f, W, 4.5f, IntT, L1, L2));
        m.Obstacles.Add(Wall(5.5f, 0, 5.5f, D, IntT, sz1, Roof));
        m.Obstacles.Add(Wall(5.5f, 4.5f, W, 4.5f, IntT, sz1, Roof));
        m.Obstacles.Add(Wall(9, 4.5f, 9, D, IntT, sz1, Roof));

        float z1 = 1.5f, z2 = L2 + SlabT + 1.3f;
        m.Openings.Add(Win("w-liv-s", "Living south window", "L1", new Vector3(3.5f, 0, z1), -Vector3.UnitY, 1.6f, 1.3f, true));
        m.Openings.Add(Win("w-liv-n", "Living north window", "L1", new Vector3(3.5f, D, z1), Vector3.UnitY, 1.6f, 1.3f, true));
        m.Openings.Add(Win("w-liv-w", "Living west window", "L1", new Vector3(0, 4, z1), -Vector3.UnitX, 1.2f, 1.3f, false));
        m.Openings.Add(Win("w-kit-s", "Kitchen window", "L1", new Vector3(9.5f, 0, z1), -Vector3.UnitY, 1.2f, 1.1f, true));
        m.Openings.Add(Win("w-hall-e", "Hall window", "L1", new Vector3(W, 6.25f, z1), Vector3.UnitX, 1f, 1.1f, true));
        m.Openings.Add(Door("d-front", "Front door", "L1", new Vector3(9.5f, D, 1.05f), Vector3.UnitY, 1f, 2.1f, ExtT, false));
        m.Openings.Add(Win("w-b1-s", "Bedroom 1 south window", "L2", new Vector3(2.5f, 0, z2), -Vector3.UnitY, 1.4f, 1.3f, true));
        m.Openings.Add(Win("w-b1-n", "Bedroom 1 north window", "L2", new Vector3(2.5f, D, z2), Vector3.UnitY, 1.4f, 1.3f, true));
        m.Openings.Add(Win("w-b1-w", "Bedroom 1 west window", "L2", new Vector3(0, 4, z2), -Vector3.UnitX, 1.2f, 1.3f, true));
        m.Openings.Add(Win("w-b2-s", "Bedroom 2 window", "L2", new Vector3(9f, 0, z2), -Vector3.UnitY, 1.4f, 1.3f, true));
        m.Openings.Add(Win("w-bath-e", "Bathroom window", "L2", new Vector3(W, 6.25f, z2), Vector3.UnitX, 0.7f, 0.7f, false));
        m.Openings.Add(Door("d-liv-kit", "Living to kitchen", "L1", new Vector3(7, 2f, 1.05f), Vector3.UnitX, 0.9f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-kit-hall", "Kitchen to hall", "L1", new Vector3(9f, 4.5f, 1.05f), Vector3.UnitY, 0.9f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-b1", "Bedroom 1 door", "L2", new Vector3(5.5f, 6f, sz1 + 1.05f), Vector3.UnitX, 0.85f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-b2", "Bedroom 2 door", "L2", new Vector3(7f, 4.5f, sz1 + 1.05f), Vector3.UnitY, 0.85f, 2.1f, IntT, true));
        m.Openings.Add(Door("d-bath", "Bathroom door", "L2", new Vector3(9f, 6f, sz1 + 1.05f), Vector3.UnitX, 0.8f, 2.1f, IntT, false));

        m.Rooms.Add(Room("living", "Living", "L1", 0.15f, 0.15f, 6.9f, 7.85f, L1, L2));
        m.Rooms.Add(Room("kitchen", "Kitchen", "L1", 7.1f, 0.15f, 11.85f, 4.4f, L1, L2));
        m.Rooms.Add(Room("hall", "Hall", "L1", 7.1f, 4.6f, 11.85f, 7.85f, L1, L2));
        m.Rooms.Add(Room("bed1", "Bedroom 1", "L2", 0.15f, 0.15f, 5.4f, 7.85f, sz1, Roof));
        m.Rooms.Add(Room("bed2", "Bedroom 2", "L2", 5.6f, 0.15f, 11.85f, 4.4f, sz1, Roof));
        m.Rooms.Add(Room("landing", "Landing", "L2", 5.6f, 4.6f, 8.9f, 7.85f, sz1, Roof));
        m.Rooms.Add(Room("bath", "Bathroom", "L2", 9.1f, 4.6f, 11.85f, 7.85f, sz1, Roof));
        return m;
    }

    static Mesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] v =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[] idx = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0];
        return new Mesh { Id = Guid.NewGuid().ToString("N"), Vertices = v, Indices = idx };
    }

    static Mesh Wall(float x0, float y0, float x1, float y1, float thickness, float zMin, float zMax)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float half = thickness * 0.5f;
        if (MathF.Abs(x1 - x0) < 1e-6f) { minX -= half; maxX += half; }
        else { minY -= half; maxY += half; }
        return Box(new Vector3(minX, minY, zMin), new Vector3(maxX, maxY, zMax));
    }

    static Opening Win(string id, string name, string level, Vector3 c, Vector3 n, float w, float h, bool open) =>
        new() { Id = id, Name = name, Level = level, Kind = OpeningKind.Window, IsOpen = open, Center = c, Normal = n, Width = w, Height = h, Depth = ExtT };

    static Opening Door(string id, string name, string level, Vector3 c, Vector3 n, float w, float h, float depth, bool open) =>
        new() { Id = id, Name = name, Level = level, Kind = OpeningKind.Door, IsOpen = open, Center = c, Normal = n, Width = w, Height = h, Depth = depth };

    static Room Room(string id, string name, string level, float x0, float y0, float x1, float y1, float zMin, float zMax) =>
        new()
        {
            Id = id, Name = name, Level = level, ZMin = zMin, ZMax = zMax,
            Loops = { new Vector2[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } },
        };
}
