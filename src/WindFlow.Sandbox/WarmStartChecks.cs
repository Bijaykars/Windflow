// Warm start (RunOptions.WarmStart / LbmSolver.Start's warmStart): a finer run seeded from a coarser field must
// stay stable and land on the same averaged answer as a cold start, on both backends.  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --warmstart      (or as part of the default run)
//
// The GPU path once uploaded its seed in the CPU's AoS layout into the SoA population buffer, so every cell
// started from 19 scrambled populations; a 1.5 M -> 5.09 M cell warm start diverged within 200 steps while the
// same grid ran cleanly from rest. 56a is the regression check for that; 56b runs the CPU path the same way.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class WarmStartChecks
{
    /// <summary>Runs every warm-start check and returns the number of failures (0 = all passed).</summary>
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

        Console.WriteLine("== warm-start checks ==");
        var floor = Program.BuildCross("warm-start-parity", true, true);
        var wind = new Vector2(3, 0);
        var preview = Simulation.Run(floor, wind, new RunOptions { TargetCells = 120_000, Backend = Backend.Cpu }).Field;

        // Protocol: 4 x PlannedSteps of warm-up, then one averaging window of 2 x PlannedSteps, for both starts.
        // A single planned run is too short to compare: at its end the warm start still carries its coarse
        // seed (-6 to -7 % on east Q, both backends) and half-planned block means of this cross-ventilated room
        // wander +/-3 % on their own; after the longer warm-up the two starts track each other block by block.
        // 2x warm-up was enough until the calibrated opening model (2026-09-28): its throat loss cuts Q ~11 %, so
        // the room's flow-through is longer and the 126k seed (windows ~2 cells across) starts further from the fine
        // answer; at 2x the warm run still read -2.5 % (GPU), at 4x -1.1 %, at 8x -0.9 % -- seed memory washing
        // out, not a different answer. The 2 % band is unchanged.
        void Pair(string id, Backend backend, int cells)
        {
            string label = $"{id} {backend} warm start ({preview.Grid.Count / 1000}k -> {cells / 1000}k cells) stays stable past 1000 steps and matches a cold start's averaged Q within 2%";
            try
            {
                var opts = new RunOptions { TargetCells = cells, Backend = backend };
                (float Q, long Steps, string Backend) Solve(FlowField? seed)
                {
                    var grid = Voxelizer.Build(floor, cells, null, null, minDx: 0.1f);
                    using var session = LbmSolver.Start(grid, floor, wind, opts, seed);
                    session.Step(4 * session.PlannedSteps);
                    session.ResetAveraging();
                    session.Step(2 * session.PlannedSteps);
                    var r = Analysis.Analyze(floor, session.ToField(), wind, opts);
                    return (r.Openings.First(o => o.Opening.Id == "east").FlowRate, session.Steps, session.Backend);
                }
                var cold = Solve(null);
                var warm = Solve(preview);
                float err = MathF.Abs(warm.Q - cold.Q) / MathF.Max(MathF.Abs(cold.Q), 1e-9f);
                bool finite = float.IsFinite(warm.Q) && float.IsFinite(cold.Q);
                Check(label, finite && warm.Steps > 1000 && err <= 0.02f,
                    $"Qcold={cold.Q:F4} Qwarm={warm.Q:F4} err={err * 100:F2}% steps={warm.Steps} backend=\"{warm.Backend}\"");
            }
            catch (Exception ex) { Check(label, false, $"exception: {ex.Message}"); }
        }

        if (!LbmSolver.GpuAvailable) Skip("56a GPU warm start", "no GPU available");
        else Pair("56a", Backend.Gpu, 1_000_000);
        Pair("56b", Backend.Cpu, 400_000);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL WARM-START CHECKS PASSED" : $"{failures} WARM-START CHECK(S) FAILED");
        return failures;
    }
}
