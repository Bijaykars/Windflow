// 6k (P14-06): the online age of air (LbmAge.cs). A closed mixed room (supply + return patch, BENCH 8's scene at 3 x 3 x 2.4 m) on the CPU:
//   (a) tracer conservation: at steady state the scalar leaving through the return equals the scalar the source adds, within 1 %
//   (b) the exhaust-age identity: flux-weighted age at the return = V / Q, within 1 % (the same relation seen from the mass flux)
// and, in --hybrid-gpu, the GPU kernel against the CPU one on the same scene (AgeGpuCpu).

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static partial class FixChecks
{
    /// <summary>BENCH 8's mixed room, smaller: a closed 3 x 3 x 2.4 m room, a 0.5 m supply and return patch in the ceiling, 0.2 m3/s.</summary>
    static (BuildingModel Model, VoxelGrid Grid) AgeRoom(float dx)
    {
        const float L = 3f, Hh = 2.4f, t = 0.2f, q = 0.2f;
        var m = new BuildingModel { Name = "age-room", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = Hh, Analysed = true } } };
        m.Obstacles.Add(Box(new Vector3(-t, -t, -t), new Vector3(L + t, L + t, 0)));
        m.Obstacles.Add(Box(new Vector3(-t, -t, Hh), new Vector3(L + t, L + t, Hh + t)));
        m.Obstacles.Add(Box(new Vector3(-t, -t, 0), new Vector3(0, L + t, Hh)));
        m.Obstacles.Add(Box(new Vector3(L, -t, 0), new Vector3(L + t, L + t, Hh)));
        m.Obstacles.Add(Box(new Vector3(0, -t, 0), new Vector3(L, 0, Hh)));
        m.Obstacles.Add(Box(new Vector3(0, L, 0), new Vector3(L, L + t, Hh)));
        m.Rooms.Add(new Room { Id = "room", Name = "Room", Level = "L1", ZMin = 0, ZMax = Hh,
            Loops = { new[] { new Vector2(0.05f, 0.05f), new Vector2(L - 0.05f, 0.05f), new Vector2(L - 0.05f, L - 0.05f), new Vector2(0.05f, L - 0.05f) } } });
        m.Sources.Add(new AirSource { Id = "sup", Name = "Supply", Level = "L1", Kind = SourceKind.Supply, Center = new Vector3(0.75f, 0.75f, Hh - 0.1f), Direction = new Vector3(0, 0, -1), Width = 0.5f, Height = 0.5f, FlowRate = q });
        m.Sources.Add(new AirSource { Id = "ret", Name = "Return", Level = "L1", Kind = SourceKind.Return, Center = new Vector3(2.25f, 2.25f, Hh - 0.1f), Direction = new Vector3(0, 0, 1), Width = 0.5f, Height = 0.5f, FlowRate = q });
        var box = new DomainBox(new Vector3(-t, -t, -t), new Vector3(L + t, L + t, Hh + t));
        int target = (int)Math.Round(box.Size.X * box.Size.Y * box.Size.Z / (dx * dx * dx));
        return (m, Voxelizer.Build(m, target, null, box, true, 0.02f));
    }

    /// <summary>Flow to a settled average, the steady age on it, the online age started from that, then <paramref name="settleSteps"/> of it to reach its own steady state
    /// and a <paramref name="windowSteps"/> window (CPU: with the budget on). Returns the session's last window as a field and the budget.</summary>
    static (FlowField Field, double[] Budget, float Dt, int Steps, int Solved) AgeRoomRun(Backend be, int settleSteps, int windowSteps)
    {
        var (m, g) = AgeRoom(0.25f);
        var o = new RunOptions { Backend = be, WindProfileExponent = 0f, OnlineAge = true };
        using var s = LbmSolver.Start(g, m, Vector2.Zero, o);
        s.Step(8000);
        s.ResetAveraging(true);
        s.Step(3000);
        var frozen = ScalarSolver.Solve(m, s.ToField(), o, ageOnly: true).Age;
        if (!s.StartOnlineAge(frozen)) throw new InvalidOperationException("the online age did not start: " + s.OnlineAgeNote);
        s.Step(settleSteps);
        s.AgeDiagnostics(true);
        s.ResetAveraging(true);
        s.Step(windowSteps);
        var f = s.ToField();
        return (f, (double[])s.AgeBudget.Clone(), s.TimeStepSeconds, windowSteps, f.Age.Count(float.IsFinite));
    }

    /// <summary>BENCH 8's tracer: the same scheme driven with a point source S = 1 unit/s (a source plane) in the closed mixed room; at steady state the tracer leaving through the
    /// return, C_exhaust x Q, equals S. Returns (out / source of the budget, C_exh Q / S).</summary>
    static (double OutOverSource, double CexhQOverS) TracerRoomRun(int settleSteps, int windowSteps)
    {
        var (m, g) = AgeRoom(0.25f);
        var o = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, OnlineAge = true };
        using var s = LbmSolver.Start(g, m, Vector2.Zero, o);
        s.Step(8000);
        s.ResetAveraging(true);
        s.Step(3000);
        var frozen = ScalarSolver.Solve(m, s.ToField(), o, ageOnly: true).Age;
        if (!s.StartOnlineAge(frozen)) throw new InvalidOperationException("the online age did not start: " + s.OnlineAgeNote);
        float dt = s.TimeStepSeconds; double cellV = Math.Pow(g.Dx, 3);
        var mask = (byte[])typeof(LbmSolver.LbmSession).GetField("_ageMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;
        int cell = g.Index((int)((1.5f - g.Origin.X) / g.Dx), (int)((1.5f - g.Origin.Y) / g.Dx), (int)((1.2f - g.Origin.Z) / g.Dx));
        if (mask[cell] != 1) throw new InvalidOperationException("the source cell is not a solved air cell");
        var src = new float[g.Count]; src[cell] = (float)(1.0 * dt / cellV);               // S = 1 unit/s into one cell: S dt / cell volume per step
        s.StartOnlineScalar(new float[g.Count], (byte[])mask.Clone(), src);
        s.Step(settleSteps);
        s.AgeDiagnostics(true);
        s.Step(windowSteps);
        var b = s.AgeBudget;
        double sOut = (b[1] - b[4]) / windowSteps * cellV / dt, sIn = b[0] / windowSteps * cellV / dt;
        double q = b[2] / windowSteps * cellV / dt, cExh = b[1] / b[2];
        return (sOut / sIn, cExh * q / 1.0);
    }

    static void OnlineAgeChecks()
    {
        var (f, b, dt, steps, solved) = AgeRoomRun(Backend.Cpu, 40000, 6000);
        // lattice budget over the window: b[0] source added, b[1]/b[2] scalar/mass leaving through held cells (the return patch), b[4] diffusive scalar inflow from them
        double srcPerStep = b[0] / steps, outPerStep = (b[1] - b[4]) / steps;
        double conservation = outPerStep / srcPerStep;                                         // (a) scalar out / scalar in
        double tauExh = b[1] / b[2], tauNom = solved * (double)dt / (b[2] / steps);            // (b) flux-weighted exhaust age vs V / Q (cells x dt / mass per step)
        double identity = tauExh / tauNom;
        double mean = f.Age.Where(float.IsFinite).Average(a => (double)a);
        Check("6k tracer conservation: in a closed mixed room (supply + return) the online age's scalar leaving through the return equals what its source adds, within 1 %",
            Math.Abs(conservation - 1) <= 0.01, $"out/in {conservation:0.0000} · mass in/out {b[3] / b[2]:0.0000} · {solved} cells · mean age {mean:0.0} s");
        var (trOut, trCQ) = TracerRoomRun(60000, 6000);
        Check("6k tracer conservation with a point source: C_exhaust x Q = S, and the tracer out equals the tracer in, within 1 % (BENCH 8)",
            Math.Abs(trOut - 1) <= 0.01 && Math.Abs(trCQ - 1) <= 0.01, $"out/in {trOut:0.0000} · C_exh Q / S {trCQ:0.0000}");
        Check("6k exhaust-age identity: the flux-weighted age at the return equals V/Q, within 1 % (BENCH 8)",
            Math.Abs(identity - 1) <= 0.01, $"flux-weighted age {tauExh:0.0} s · V/Q {tauNom:0.0} s · ratio {identity:0.0000}");
        // V2: the Age array is NaN by design outside the age mask; averaging two healthy online-age windows must not count those cells as non-finite samples.
        var (w1, _, _, _, _) = AgeRoomRun(Backend.Cpu, 300, 200); var (w2, _, _, _, _) = AgeRoomRun(Backend.Cpu, 300, 200);
        int nanAge = w1.Age.Count(float.IsNaN);
        FlowFieldMean.Seed(w1, 1); FlowFieldMean.Add(w1, w2);
        Check("V2 averaging a healthy online-age run counts no non-finite samples and adds no \"Averaging:\" note (Age NaN outside its mask is no value)",
            nanAge > 0 && w1.NonFiniteSkipped == 0 && !w1.Grid.Warnings.Any(w => w.StartsWith(FlowFieldMean.NonFiniteTag, StringComparison.Ordinal)) && w1.Age.Count(float.IsNaN) == nanAge,
            $"age NaN cells {nanAge} · skipped {w1.NonFiniteSkipped} · notes {w1.Grid.Warnings.Count(w => w.StartsWith(FlowFieldMean.NonFiniteTag, StringComparison.Ordinal))}");
    }

    /// <summary>The GPU kernel against the CPU one on the same scene: same flow settle, the same online window, the volume-mean age and the cell-wise L1 difference.</summary>
    internal static (bool Pass, string Detail) AgeGpuCpu()
    {
        var cpu = AgeRoomRun(Backend.Cpu, 6000, 2000).Field.Age;
        var gpu = AgeRoomRun(Backend.Gpu, 6000, 2000).Field.Age;
        double sc = 0, sg = 0, l1 = 0; int n = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            if (!float.IsFinite(cpu[i]) || !float.IsFinite(gpu[i])) { if (float.IsFinite(cpu[i]) != float.IsFinite(gpu[i])) return (false, $"cell {i}: held on one backend only"); continue; }
            sc += cpu[i]; sg += gpu[i]; l1 += Math.Abs(cpu[i] - gpu[i]); n++;
        }
        double dMean = Math.Abs(sg - sc) / sc, dL1 = l1 / sc;
        return (n > 100 && dMean <= 0.01 && dL1 <= 0.03, $"{n} cells · mean age CPU {sc / n:0.0} s GPU {sg / n:0.0} s ({dMean * 100:0.00} %) · cell-wise L1 {dL1 * 100:0.00} % of the mean");
    }
}
