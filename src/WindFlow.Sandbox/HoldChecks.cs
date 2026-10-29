// 6j (P14-01) held indoor temperature: RunOptions.HoldIndoorTemperature gives each room with an outline an ideal heater/cooler.
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --hold
// Two rooms side by side (3 x 3 x 2.5 m each, Dx 0.25, hand-built), joined by a 1 x 2 m doorway; one 1 x 1.25 m window in room A's west wall;
// a 300 W heater in room B; no wind, outdoor 10 C, set point 20 C. CPU Hybrid, then the same on the GPU when there is one.

using System.Numerics;
using WindFlow.Core;
using Environment = WindFlow.Core.Environment;

namespace WindFlow.Sandbox;

static class HoldChecks
{
    static int failures;
    const float Dx = 0.25f, TOut = 10f, TSet = 20f, Gains = 300f;
    const int Nx = 43, Ny = 30, Nz = 20, Settle = 56000, Average = 8000, ParitySettle = 8000, ParityAverage = 4000;

    static void Check(string name, bool pass, string detail = "")
    {
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name} {detail}");
        CheckLog.Record(pass ? "PASS" : "FAIL", name, detail);
        if (!pass) failures++;
    }

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("== held indoor temperature (6j) ==");
        var cpu = Solve(Backend.Cpu, hold: true);
        double sumDuty = cpu.Duty.Sum(), lhs = sumDuty + Gains;
        double closure = Math.Abs(lhs - cpu.Loss) / Math.Abs(lhs);
        Check("6j-a held rooms: every room's VOLUME mean within 0.5 K of the 20 C set point after settling (S2; outdoor 10 C, one window, 300 W in room B, CPU)",
            cpu.RoomT.All(t => Math.Abs(t - TSet) <= 0.5),
            $"volume means {string.Join(" / ", cpu.RoomT.Select(t => t.ToString("F3")))} C, occupied-zone means (reported, not held) {string.Join(" / ", cpu.OccT.Select(t => t.ToString("F3")))} C | duty {string.Join(" / ", cpu.Duty.Select(d => d.ToString("F1")))} W | {cpu.Detail}");
        Check("6j-b energy balance: duty + gains = ventilation enthalpy loss (link flux of rho c_p (T - T_out) out of the building's air) within 2 %",
            closure <= 0.02,
            $"duty {sumDuty:F1} W + gains {Gains:F0} W = {lhs:F1} W | loss {cpu.Loss:F1} W | storage {cpu.Storage:F1} W | closure {closure * 100:F2} % (with storage {Math.Abs(lhs - cpu.Loss - cpu.Storage) / Math.Abs(lhs) * 100:F3} %)");

        if (LbmSolver.GpuAvailable)
        {
            // Implementation parity, compared before the CPU and GPU trajectories decorrelate (float rounding grows into the chaotic
            // exchange flow; over the 64 k-step settled run the small room-B duty differs by ~5 % between two valid trajectories).
            var cpuP = Solve(Backend.Cpu, hold: true, settle: ParitySettle, average: ParityAverage);
            var gpu = Solve(Backend.Gpu, hold: true, settle: ParitySettle, average: ParityAverage);
            double worst = 0;
            for (int r = 0; r < cpuP.Duty.Length; r++) worst = Math.Max(worst, Math.Abs(gpu.Duty[r] - cpuP.Duty[r]) / Math.Max(Math.Abs(cpuP.Duty[r]), 1));
            Check($"6j-c GPU = CPU duty within 2 % per room ({ParitySettle} + {ParityAverage} steps)", worst <= 0.02,
                $"cpu {string.Join(" / ", cpuP.Duty.Select(d => d.ToString("F1")))} W, gpu {string.Join(" / ", gpu.Duty.Select(d => d.ToString("F1")))} W, worst {worst * 100:F2} %");
        }
        else Console.WriteLine("[WAIT] 6j-c GPU = CPU duty  no GPU");

        var free = Solve(Backend.Cpu, hold: false, steps: 600);
        Check("6j-d free-running: no room is held (no RoomHolds, HoldDutyWatts NaN) and room A (the window room) drifts below the 20 C start",
            free.Holds == 0 && free.DutyNaN && free.RoomT[0] < TSet, $"holds {free.Holds}, room means {string.Join(" / ", free.RoomT.Select(t => t.ToString("F3")))} C");

        // 6j addendum: a room holding a conditioning device's outlet (own supply temperature) is not held; the grid says so.
        {
            var (g, m, _) = Scene();
            var outlet = new List<int>();
            for (int y = 12; y <= 13; y++) for (int z = 8; z <= 9; z++) outlet.Add(g.Index(30, y, z));   // a 2 x 2 cell outlet in room B
            g.Devices.Add(new DeviceRegion { SourceId = "ac", SourceName = "split AC", Kind = DeviceKind.SplitAc, Cells = outlet.ToArray(),
                Target = outlet.Select(_ => new Vector3(-0.5f, 0, 0)).ToArray(), OutletCount = outlet.Count, FlowRate = 0.05f, SupplyTemperature = 14f });
            using var s = LbmSolver.Start(g, m, Vector2.Zero, new RunOptions { Backend = Backend.Cpu, Thermal = true, ThermalScheme = ThermalScheme.Hybrid });
            s.Step(100);
            var f = s.ToField();
            string note = "B is conditioned by its split AC; the ideal hold is off there.";
            Check("6j-e a room with a conditioning device (own supply temperature) is not held, and the grid notes it; the other room still is",
                f.RoomHolds.Count == 1 && f.RoomHolds[0].Room.Id == "A" && g.Warnings.Contains(note),
                $"held rooms: {string.Join(", ", f.RoomHolds.Select(h => h.Room.Id))} | note: {g.Warnings.FirstOrDefault(w => w.Contains("conditioned")) ?? "none"}");
        }

        // RT-07: a wind change that leaves the absorbing band where it is touches nothing of the hold; one that moves it filters the cached room
        // cells again (no new room masks) and keeps the integrator state.
        {
            var (g, m, _) = Scene();
            using var s = LbmSolver.Start(g, m, new Vector2(2, 0), new RunOptions { Backend = Backend.Cpu, Thermal = true, ThermalScheme = ThermalScheme.Hybrid });
            s.Step(300);
            T Get<T>(string name) => (T)s.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;
            var cells0 = Get<int[][]>("_holdCells"); var base0 = Get<object>("_holdBase"); double[] int0 = (double[])Get<double[]>("_holdInt").Clone();
            s.SetWind(new Vector2(1.5f, 0));
            bool same = ReferenceEquals(cells0, Get<int[][]>("_holdCells"));
            s.SetWind(new Vector2(0, 2));
            bool rebuilt = !ReferenceEquals(cells0, Get<int[][]>("_holdCells")), baseKept = ReferenceEquals(base0, Get<object>("_holdBase"));
            var int1 = Get<double[]>("_holdInt");
            bool stateKept = int0.Length == int1.Length && int0.Any(v => v != 0) && int0.Zip(int1).All(p => p.First == p.Second);
            Check("6j-f (RT-07) SetWind keeps the hold's room cells while the sponge band is unchanged; when it moves, the cached room masks are reused and the integrator state kept",
                same && rebuilt && baseKept && stateKept, $"band unchanged: cells kept {same} | band moved: rebuilt {rebuilt}, masks cached {baseKept}, integrator state kept {stateKept} (x{int0.Length} rooms, nonzero {int0.Any(v => v != 0)})");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL HOLD CHECKS PASSED" : $"{failures} HOLD CHECK(S) FAILED");
        return failures;
    }

    /// <summary>The scene (see the file header).</summary>
    internal static (VoxelGrid Grid, BuildingModel Model, bool[] Interior) Scene()
    {
        var g = new VoxelGrid { Nx = Nx, Ny = Ny, Nz = Nz, Dx = Dx, Origin = Vector3.Zero, Cell = new byte[Nx * Ny * Nz] };
        var interior = new bool[g.Count];
        for (int z = 0; z < Nz; z++) for (int y = 0; y < Ny; y++) for (int x = 0; x < Nx; x++)
        {
            bool inPlan = x >= 8 && x <= 34 && y >= 8 && y <= 21;
            bool wall = x == 8 || x == 21 || x == 34 || y == 8 || y == 21;
            bool solid = z == 0 || inPlan && (z == 11 || z >= 1 && z <= 10 && wall);
            if (x == 21 && y >= 14 && y <= 17 && z >= 1 && z <= 8) solid = false;    // doorway, 1 x 2 m
            if (x == 8 && y >= 13 && y <= 16 && z >= 4 && z <= 8) solid = false;     // window, 1 x 1.25 m, sill 0.75 m
            g.Cell[g.Index(x, y, z)] = solid ? VoxelGrid.Solid : VoxelGrid.Air;
            interior[g.Index(x, y, z)] = !solid && x >= 9 && x <= 33 && y >= 9 && y <= 20 && z >= 1 && z <= 10;
        }
        Room RoomOf(string id, float x0, float x1) => new()
        {
            Id = id, Name = id, Level = "L1", ZMin = 0.25f, ZMax = 2.75f,
            Loops = { new[] { new Vector2(x0, 2.25f), new Vector2(x1, 2.25f), new Vector2(x1, 5.25f), new Vector2(x0, 5.25f) } },
        };
        var m = new BuildingModel
        {
            Name = "hold", GroundZ = 0.25f,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0.25f, Height = 2.5f, Analysed = true } },
            Env = new Environment { IndoorTemperature = TSet, OutdoorTemperature = TOut },
            Rooms = { RoomOf("A", 2.25f, 5.25f), RoomOf("B", 5.5f, 8.5f) },
        };
        // footprint for Voxelizer.PlanBounds (the T0 indoor/outdoor split); never voxelized
        m.Obstacles.Add(new Mesh { Id = "plan", Vertices = [new(2, 2, 0), new(8.75f, 2, 0), new(8.75f, 5.5f, 0), new(2, 5.5f, 0)], Indices = [0, 1, 2, 0, 2, 3] });
        m.Heat.Add(new HeatSource { Id = "h", Name = "heater", Level = "L1", Kind = HeatKind.Equipment, Center = new Vector3(7, 3.75f, 0.5f),
            Width = 1, Depth = 1, Height = 0.5f, Watts = Gains });
        return (g, m, interior);
    }

    sealed record Result(double[] Duty, double[] RoomT, double[] OccT, double Loss, double Storage, int Holds, bool DutyNaN, string Detail);

    static Result Solve(Backend backend, bool hold, int steps = 0, int settle = Settle, int average = Average)
    {
        var (g, m, interior) = Scene();
        var opts = new RunOptions { Backend = backend, Thermal = true, ThermalScheme = ThermalScheme.Hybrid, HoldIndoorTemperature = hold };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var s = LbmSolver.Start(g, m, Vector2.Zero, opts);
        double cellV = Dx * Dx * Dx, rhoCp = 1.2 * 1005, dt = s.TimeStepSeconds;
        double E() => (s.HybridEnergy(i => interior[i]) - TOut * s.HybridMassSum(i => interior[i])) * rhoCp * cellV;
        double loss = double.NaN, storage = double.NaN;
        if (steps > 0) { s.ResetAveraging(startNow: true); s.Step(steps); }
        else
        {
            s.Step(Math.Max(s.WarmupSteps, settle));
            s.ResetAveraging(startNow: true);
            if (backend == Backend.Cpu) s.EnergyProbe = interior;
            double e0 = E();
            s.Step(average);
            storage = (E() - e0) / (average * dt);
            if (backend == Backend.Cpu) loss = s.EnergyProbeOut / average * rhoCp * cellV / dt;
        }
        var field = s.ToField();
        var result = Analysis.Analyze(m, field, Vector2.Zero, opts);
        Analysis.Finish(result);
        var roomT = new double[m.Rooms.Count];
        for (int r = 0; r < m.Rooms.Count; r++)
        {
            var mask = Analysis.RoomMask(g, m.Rooms[r], out _);
            double sum = 0; int n = 0;
            for (int i = 0; i < g.Count; i++) if (mask[i] && g.Cell[i] == VoxelGrid.Air) { sum += field.T[i]; n++; }
            roomT[r] = sum / n;
        }
        var duty = m.Rooms.Select(room => (double)result.Rooms.First(rr => rr.Room.Id == room.Id).HoldDutyWatts).ToArray();
        var occT = m.Rooms.Select(room => (double)result.Rooms.First(rr => rr.Room.Id == room.Id).MeanTemperature).ToArray();
        return new Result(duty, roomT, occT, loss, storage, field.RoomHolds.Count, duty.All(double.IsNaN),
            $"{backend} {s.Steps} steps, dt {dt * 1000:F2} ms ({s.Steps * dt:F0} s), averaged {average} steps, {sw.Elapsed.TotalSeconds:F0} s wall");
    }
}
