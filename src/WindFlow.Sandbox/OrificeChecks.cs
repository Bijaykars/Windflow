// Isolated sharp-edged orifice: discharge coefficient of a square opening in a one-cell wall across a duct, the
// canonical case behind the cross-ventilation over-prediction (PLAN_V2 1.4b F3).  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --orifice      (or as part of the default run)
//
// Geometry and measurement follow the scratch rig (dbg3/dbg5 OrificeLab): n x n opening in a 1-cell wall, duct
// 8n x 8n cells across, 3n upstream and 8n downstream; Cd = Q / (A sqrt(2 dp / rho)), dp = upstream total pressure
// on the plane 2n upstream of the wall minus the static pressure on the downstream wall face farther than 1.5n
// from the opening edge. Differences from the rig: the duct walls are solid (the rig was periodic), and the inlet
// is Core's wind ring (feq(1, u_in), velocity-type) rather than a pressure inlet -- neither changes Cd, which is
// read from the measured Q and dp. Core's lattice-viscosity floor (NuLatMin) holds the opening Re near 1e3, not
// the 1e4 of the brief; the rig puts Cd at Re 1.2e3 within 0.01 of Re 1.2e4 (0.736 vs 0.745 at 8 cells).
// A sharp-edged orifice discharges at Cd ~0.61 (Idelchik; ISO 5167 orifice plates at beta -> 0).

using System.IO;
using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class OrificeChecks
{
    const float Dx = 0.05f;
    const float ULat = 0.08f;          // LbmSolver.ULat: the lattice speed a session assigns to its velocity scale
    // Inlet sizing, lattice units: the wind ring is feq(1, u_in), which gives way to the plenum over-pressure and
    // delivers about half its nominal flux here, so this nominal 0.2 puts the opening near 0.1 (opening Re 0.8-3e3
    // under the viscosity floor, where the rig puts Cd within 0.01 of its Re 1e4 value).
    const float UoLatTarget = 0.2f;

    /// <summary>Runs every orifice check and returns the number of failures (0 = all passed).</summary>
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
        void Info(string label, string detail) { Console.WriteLine($"[NOTE] {label}  {detail}"); CheckLog.Record("NOTE", label, detail); }
        void Skip(string label, string detail) { Console.WriteLine($"[WAIT] {label}  {detail}"); CheckLog.Record("WAIT", label, detail); }

        Console.WriteLine("== orifice checks ==");
        bool gpu = LbmSolver.GpuAvailable;
        var big = gpu ? Backend.Gpu : Backend.Cpu;
        var def = new RunOptions().OpeningModel;

        var r8 = RunOrificeCells(8, def, big);
        // P13a-03 (rule 3): band was [0.60, 0.65]. This cell rig is the aligned hole (offset 0), which resolves higher than the phase mean the Cd table
        // is built on (8 cells: 0.743 aligned vs 0.654 mean, 58b), so it calibrates to 0.68, not 0.61.
        Check($"58a sharp square orifice 8 cells across: Cd in [0.65, 0.72] (default opening model {def}; aligned hole)",
            r8.Cd >= 0.65f && r8.Cd <= 0.72f, r8.Detail);

        // 58b (6d, P13a-03: the product wall route, phase-mean; was the hand-built rig at t = 1): the calibrated Cd of a sharp square orifice in a
        // 1-cell wall at 4/8/16 cells across, averaged over where the hole sits in the grid, next to the Resolved Cd the table holds for it.
        // The whole (n, t) grid is `--orifice-run table:<n>:cal` (fixes/b6d/cal*.txt).
        var parts = new List<string>();
        foreach (int n in new[] { 4, 8, 16 })
        {
            if (n == 16 && !gpu) { parts.Add("n16=skipped_no_gpu"); continue; }
            var rows = PhaseTable(n, OpeningModel.Calibrated, [1f], quiet: true);
            parts.Add($"n{n}_calibrated_mean={rows[0].Mean:F3} n{n}_phases=[{string.Join(" ", rows[0].Cd.Select(v => v.ToString("F3")))}] n{n}_table_resolved={LbmSolver.CdSolver(n, 1f):F3}");
        }
        Info("58b orifice Cd vs cells across (4/8/16), wall 1 cell, product route, mean over the phase triples, Calibrated opening model", string.Join(" ", parts));

        // 58d/58e (6d, P13a-03): a hole centred in the duct is treated alike at its lower and upper jambs (it was not: a link crossing the wall exactly on the
        // hole's edge was cut at the lower y / z jamb and not the upper), and a hole and its mirror image (offset +-0.25 cell) flow alike.
        int mirrorBad = 0, mirrorSeen = 0;
        // RP-03: edges on cell faces (the hole centred on the duct, even n) and edges on cell centres (a duct of odd side, the hole centred on a cell centre).
        string mirrorParts = "";
        foreach (bool onCentre in new[] { false, true })
        foreach (var (n, t) in new[] { (4, 1f), (8, 1f), (8, 0.5f), (4, 2f) })
        {
            var (b, c) = MirrorPattern(n, t, onCentre); mirrorBad += b; mirrorSeen += c;
            mirrorParts += $" {(onCentre ? "centre" : "face")}:n{n}t{t}={b}";
        }
        Check("58d orifice rig: masks and wall links of a centred hole are mirror-identical in y, z and both (n 4/8, wall 0.5-2; hole edges on cell faces and on cell centres)", mirrorBad == 0, $"mismatches={mirrorBad} of {mirrorSeen}{mirrorParts}");
        var mp = RunOrifice(4, 4, OpeningModel.Resolved, Backend.Cpu, 1f, 0f, 0.25f);
        var mm = RunOrifice(4, 4, OpeningModel.Resolved, Backend.Cpu, 1f, 0f, -0.25f);
        Check("58e orifice Cd (4 cells, wall 1, Resolved) of the hole offset +0.25 cell and of its mirror image (-0.25) within 0.5%", MathF.Abs(mp.Cd - mm.Cd) <= 0.005f * mp.Cd,
            $"cd_plus={mp.Cd:F4} cd_minus={mm.Cd:F4}");

        // RP-04: 58c at a configuration with K > 0 (8 cells, wall 1 cell: the table resolves above 0.61 there), so the Forchheimer throat loss runs on both backends.
        // Phase triple 1 (below), product route.
        if (!gpu) Skip("58c orifice Cd GPU = CPU", "no GPU available");
        else
        {
            var (sx, sy, sz) = Triples[0];
            var c = RunOrifice(8, 8, OpeningModel.Calibrated, Backend.Cpu, 1f, sx, sy, sz);
            var g = RunOrifice(8, 8, OpeningModel.Calibrated, Backend.Gpu, 1f, sx, sy, sz);
            Check("58c orifice Cd (8 cells, wall 1, Calibrated, K > 0, product route) GPU = CPU within 1%", c.LossK > 0.05f && MathF.Abs(g.Cd - c.Cd) <= 0.01f * c.Cd,
                $"cd_cpu={c.Cd:F4} cd_gpu={g.Cd:F4} lossK={c.LossK:F3} q_cpu={c.Q:E4} q_gpu={g.Q:E4}");
            // RP-04, 58f: the calibrated Cd averaged over four of the eight phase triples is the Cd asked for (0.61), within 3 %.
            var rows = PhaseTable(8, OpeningModel.Calibrated, [1f], quiet: true, triples: Quad);
            Check("58f orifice Cd (8 cells, wall 1, Calibrated, product route), phase mean of 4 triples within 3% of 0.61", MathF.Abs(rows[0].Mean / LbmSolver.DefaultOpeningCd - 1f) <= 0.03f,
                $"mean={rows[0].Mean:F4} phases=[{string.Join(" ", rows[0].Cd.Select(v => v.ToString("F4")))}] triples=[{string.Join(" ", Quad.Select(i => Triples[i]))}]");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL ORIFICE CHECKS PASSED" : $"{failures} ORIFICE CHECK(S) FAILED");
        return failures;
    }

    /// <summary>`--orifice-run spec...`: one rig run per spec, `n:res|cal:t[:shift[:nh]]` (the product route; nh makes the hole n x nh cells,
    /// n then being its width) or `cells:n:res|cal:wall` (the hand-built cell rig). GPU from 8 cells across, else CPU. Prints each run's detail.</summary>
    internal static int RunSpecs(string[] specs)
    {
        bool gpu = LbmSolver.GpuAvailable;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var spec in specs)
        {
            var p = spec.Split(':');
            Result r;
            if (p[0] == "mirror")
            {
                var (bad, seen) = MirrorPattern(int.Parse(p[1]), float.Parse(p[2], inv), p.Length > 3 && p[3] == "centre");
                Console.WriteLine($"[RUN] {spec}  mirror mismatches {bad} of {seen} cell comparisons");
                continue;
            }
            if (p[0] == "sweep")   // sweep:<res|cal>:<n,n,...>[:<t,t,...>[:<csv path>]]
            {
                Sweep(p[1] == "res" ? OpeningModel.Resolved : OpeningModel.Calibrated, p[2].Split(',').Select(int.Parse).ToArray(),
                    p.Length > 3 && p[3].Length > 0 ? p[3].Split(',').Select(v => float.Parse(v, inv)).ToArray() : GridT, p.Length > 4 ? string.Join(':', p.Skip(4)) : null);
                continue;
            }
            if (p[0] == "table")
            {
                PhaseTable(int.Parse(p[1]), p[2] == "res" ? OpeningModel.Resolved : OpeningModel.Calibrated, p.Length > 3 ? p[3].Split(',').Select(v => float.Parse(v, inv)).ToArray() : GridT);
                continue;
            }
            if (p[0] == "cells")
            {
                int n = int.Parse(p[1]);
                r = RunOrificeCells(n, p[2] == "res" ? OpeningModel.Resolved : OpeningModel.Calibrated, gpu && n >= 8 ? Backend.Gpu : Backend.Cpu, int.Parse(p[3]));
            }
            else
            {
                int n = int.Parse(p[0]), nh = p.Length > 4 ? int.Parse(p[4]) : n;
                var m = p[1] == "res" ? OpeningModel.Resolved : OpeningModel.Calibrated;
                r = RunOrifice(n, nh, m, gpu && n * nh >= 64 ? Backend.Gpu : Backend.Cpu, float.Parse(p[2], inv), p.Length > 3 ? float.Parse(p[3], inv) : 0f, p.Length > 5 ? float.Parse(p[5], inv) : 0f, p.Length > 6 ? float.Parse(p[6], inv) : null);
            }
            Console.WriteLine($"[RUN] {spec}  {r.Detail}");
        }
        return 0;
    }

    /// <summary>The calibration grid in wall thickness, cells (6d, P13a-03); the cells-across grid is LbmSolver's CdSolverCells.</summary>
    internal static readonly float[] GridT = { 0.5f, 1f, 2f, 3f, 4f };

    /// <summary>RP-01: the phase triples (x-normal wall offset in cells, y offset, z offset) of the calibration, a fixed low-discrepancy set of the values {0.125, 0.375, 0.625, 0.875},
    /// none on a tie. Offset p and 1 - p are mirror images (Cd agrees to 0.02 %, fixes/b6d/f1.txt), so the classes are A = {0.125, 0.875} and B = {0.375, 0.625}; the eight
    /// triples take each of the eight A/B class patterns of (x, y, z) once, and each value appears twice per axis. The same eight at every (n, t).
    /// Order: (x, y, z) = (0.125, 0.875, 0.125) AAA, (0.875, 0.125, 0.375) AAB, (0.125, 0.625, 0.875) ABA, (0.875, 0.375, 0.625) ABB,
    /// (0.375, 0.125, 0.875) BAA, (0.625, 0.875, 0.625) BAB, (0.375, 0.375, 0.125) BBA, (0.625, 0.625, 0.375) BBB.</summary>
    internal static readonly (float X, float Y, float Z)[] Triples =
    [
        (0.125f, 0.875f, 0.125f), (0.875f, 0.125f, 0.375f), (0.125f, 0.625f, 0.875f), (0.875f, 0.375f, 0.625f),
        (0.375f, 0.125f, 0.875f), (0.625f, 0.875f, 0.625f), (0.375f, 0.375f, 0.125f), (0.625f, 0.625f, 0.375f),
    ];
    /// <summary>The four triples used at n = 16 and by 58f: AAA, ABB, BAB, BBA (the even-parity patterns, so every axis sees both classes twice).</summary>
    internal static readonly int[] Quad = [0, 3, 5, 6];

    /// <summary>RP-01 calibration: for each wall thickness t, the rig (product route) at the phase triples (all eight, or <paramref name="triples"/> indices; n = 16 uses
    /// <see cref="Quad"/>). Prints the per-triple Cd, the mean and the largest deviation from it. Returns (t, mean, maxDev, Cd per triple) rows.</summary>
    internal static List<(float T, float Mean, float MaxDev, float[] Cd)> PhaseTable(int n, OpeningModel model, float[] ts, bool quiet = false, int[]? triples = null, System.IO.StreamWriter? csv = null)
    {
        bool gpu = LbmSolver.GpuAvailable;
        var be = gpu && n >= 8 ? Backend.Gpu : Backend.Cpu;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int[] idx = triples ?? (n >= 16 ? Quad : Enumerable.Range(0, Triples.Length).ToArray());
        var rows = new List<(float, float, float, float[])>();
        foreach (float t in ts)
        {
            var cd = new float[idx.Length]; var tie = "";
            for (int k = 0; k < idx.Length; k++)
            {
                var (sx, sy, sz) = Triples[idx[k]];
                var r = RunOrifice(n, n, model, be, t, sx, sy, sz);
                cd[k] = r.Cd;
                bool throatOk = r.Detail.Contains($"throat={n * n} ");
                if (!throatOk) tie += $" THROAT!={n * n}@{idx[k]}";
                if (csv != null)
                {
                    csv.WriteLine(string.Join(',', model, n, t.ToString("0.###", inv), idx[k], sx.ToString(inv), sy.ToString(inv), sz.ToString(inv), r.Cd.ToString("F5", inv), r.LossK.ToString("F4", inv), throatOk ? "ok" : "throat"));
                    csv.Flush();
                }
            }
            float mean = cd.Average();
            float dev = cd.Max(v => MathF.Abs(v - mean) / mean);
            rows.Add((t, mean, dev, cd));
            if (!quiet) Console.WriteLine($"[TABLE] n={n} model={model} t={t} cd_by_triple=[{string.Join(" ", cd.Select(v => v.ToString("F4")))}] triples=[{string.Join(" ", idx)}] mean={mean:F4} maxdev={dev * 100:F1}%{(model == OpeningModel.Calibrated ? $" mean_vs_0.61={(mean / 0.61f - 1) * 100:+0.0;-0.0}% {(MathF.Abs(mean / 0.61f - 1) <= 0.03f ? "OK" : LbmSolver.CdSolver(n, t) < 0.61f ? "CLAMPED (K = 0: the lattice resolves below the target)" : "MISS")}" : "")}{tie}");
        }
        return rows;
    }

    /// <summary>RP-01: the whole sweep of one model over the given n and t, one CSV (model,n,t,triple,x,y,z,cd,lossK,throat), then the summary rows (mean, X = largest deviation from
    /// the mean as a fraction): `--orifice-run sweep:res:4,8,16` writes fixes/bRP/orifice-res-n4-8-16.csv (repo root found from the exe by WindFlow.sln).</summary>
    internal static void Sweep(OpeningModel model, int[] ns, float[] ts, string? path)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (path == null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WindFlow.sln"))) dir = dir.Parent;
            string root = Path.Combine(dir?.FullName ?? ".", "audits", "2026-10-12", "fixes", "bRP");
            Directory.CreateDirectory(root);
            path = Path.Combine(root, $"orifice-{(model == OpeningModel.Resolved ? "res" : "cal")}-n{string.Join('-', ns)}.csv");
        }
        using var csv = new System.IO.StreamWriter(path, false);
        csv.WriteLine("model,n,t,triple,x,y,z,cd,lossK,throat");
        var summary = new List<string>();
        foreach (int n in ns)
            foreach (var r in PhaseTable(n, model, ts, csv: csv))
                summary.Add(string.Join(',', "summary," + model, n, r.T.ToString("0.###", inv), "mean", r.Mean.ToString("F4", inv), "X", r.MaxDev.ToString("F4", inv)));
        foreach (var l in summary) csv.WriteLine(l);
        Console.WriteLine($"[SWEEP] wrote {path}");
    }

    internal readonly record struct Result(float Cd, float Q, float Dp, float Re, float LossK, float ModelledCd, string Detail);

    /// <summary>Axis-aligned closed box mesh (the index pattern every Sandbox scene uses).</summary>
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

    /// <summary>The rig through the product's own route (6d, P13a-03): the duct walls and the plate are closed boxes, the opening is
    /// an open <see cref="Opening"/> that carves the plate, and <see cref="Voxelizer.Build"/> makes the cell-centre mask and the cut
    /// links. <paramref name="t"/> is the plate thickness in cells; the plate is centred in its ceil(t) cells, then moved
    /// <paramref name="shift"/> cells along +x (a whole-cell plate at shift 0 sits on cell faces and reproduces the hand-built rig).
    /// <paramref name="nw"/> x <paramref name="nh"/> is the hole; the cells-across n of the calibration is its hydraulic diameter.</summary>
    internal sealed record Rig(VoxelGrid Grid, BuildingModel Model, Opening Opening, float NRef, int Nw, int Nh, int Xw, float Xb, int SideY, int SideZ, int Y0, int Z0, float Dx);

    internal static Rig BuildRig(int nw, int nh, float t, float shift, float off, float? offZ = null, bool oddSide = false)
    {
        float nRef = 2f * nw * nh / (nw + nh);                  // = n for a square hole
        int up = (int)MathF.Ceiling(3f * nRef), down = (int)MathF.Ceiling(8f * nRef);
        int sideY = nw == nh ? 8 * nw : nw + 2 * (int)MathF.Ceiling((MathF.Max(8f * nRef, 2f * nw) - nw) / 2f);
        int sideZ = nw == nh ? 8 * nh : nh + 2 * (int)MathF.Ceiling((MathF.Max(8f * nRef, 2f * nh) - nh) / 2f);
        if (oddSide) { sideY++; sideZ++; }                      // RP-03 mirror check: the duct centre is a cell centre, so a hole centred on it has its edges on cell centres (even n)
        float xa = 1 + up + (MathF.Ceiling(t) - t) / 2f + shift, xb = xa + t;       // plate faces, cells
        int xw = 1 + up, Ny = sideY + 2, Nz = sideZ + 2, Nx = xw + (int)MathF.Ceiling(xb - xw) + down + 1;
        int y0 = 1 + (sideY - nw) / 2, z0 = 1 + (sideZ - nh) / 2;
        // The box is Nx x Ny x Nz cells of Dx; a target 1e-4 below Nx*Ny*Nz makes Voxelizer's Dx a hair larger (3e-5), so its ceil() gives exactly Nx, Ny, Nz.
        float w = Nx * Dx, d = Ny * Dx, h = Nz * Dx;
        int cells = (int)(Nx * Ny * Nz * (1 - 1e-4));
        float dx = MathF.Cbrt(w * d * h / cells);
        Vector3 P(float x, float y, float z) => new(x * dx, y * dx, z * dx);
        var bm = new BuildingModel { Name = "orifice", GroundZ = -10, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = Nz * dx, Analysed = true } } };
        bm.Obstacles.Add(Box(P(-1, -1, -1), P(Nx + 1, 1, Nz + 1)));             // duct walls, one cell
        bm.Obstacles.Add(Box(P(-1, Ny - 1, -1), P(Nx + 1, Ny + 1, Nz + 1)));
        bm.Obstacles.Add(Box(P(-1, -1, -1), P(Nx + 1, Ny + 1, 1)));
        bm.Obstacles.Add(Box(P(-1, -1, Nz - 1), P(Nx + 1, Ny + 1, Nz + 1)));
        bm.Obstacles.Add(Box(P(xa, -1, -1), P(xb, Ny + 1, Nz + 1)));             // the plate, whole cross-section; the opening carves it
        var opening = new Opening
        {
            Id = "orifice", Name = "orifice", Level = "L1", Kind = OpeningKind.Window, IsOpen = true,
            Center = P(0.5f * (xa + xb), y0 + 0.5f * nw + off, z0 + 0.5f * nh + (offZ ?? off)), Normal = Vector3.UnitX,
            Width = nw * dx, Height = nh * dx, Depth = t * dx,
        };
        bm.Openings.Add(opening);
        var grid = Voxelizer.Build(bm, cells, 1, new DomainBox(Vector3.Zero, new Vector3(w, d, h)), sealUnreachable: false, minDx: 0.01f);
        if (grid.Nx != Nx || grid.Ny != Ny || grid.Nz != Nz || grid.Dx != dx)
            throw new InvalidOperationException($"orifice rig grid {grid.Nx}x{grid.Ny}x{grid.Nz} Dx {grid.Dx} is not {Nx}x{Ny}x{Nz} Dx {dx}");
        grid.OpenZMin = grid.OpenZMax = false;      // a closed duct, like the hand-built rig: the box path would give it an open top and the equilibrium ring
        return new Rig(grid, bm, opening, nRef, nw, nh, xw, xb, sideY, sideZ, y0, z0, dx);
    }

    static readonly int[] LinkX = [0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0];
    static readonly int[] LinkY = [0, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 0, 0, 1, -1, 1, -1];
    static readonly int[] LinkZ = [0, 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, -1, 1, 1, -1, -1, 1];

    /// <summary>6d: a hole centred in a symmetric duct must look the same from both sides. Compares the mesh-route rig's masks and wall links
    /// with the same grid mirrored in y, in z and in both (the link of the mirrored direction at the mirrored cell). Returns the mismatch count.</summary>
    internal static (int Mismatch, int Compared) MirrorPattern(int n, float t, bool edgeOnCentre = false)
    {
        var g = (edgeOnCentre ? BuildRig(n, n, t, 0f, 0.5f, 0.5f, oddSide: true) : BuildRig(n, n, t, 0f, 0f)).Grid;
        int C = g.Count, bad = 0, seen = 0;
        int Mirror(int q, bool my, bool mz)
        {
            for (int k = 0; k < 19; k++)
                if (LinkX[k] == LinkX[q] && LinkY[k] == (my ? -LinkY[q] : LinkY[q]) && LinkZ[k] == (mz ? -LinkZ[q] : LinkZ[q])) return k;
            throw new InvalidOperationException();
        }
        foreach (var (my, mz) in new[] { (true, false), (false, true), (true, true) })
            for (int z = 0; z < g.Nz; z++)
            for (int y = 0; y < g.Ny; y++)
            for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, z), j = g.Index(x, my ? g.Ny - 1 - y : y, mz ? g.Nz - 1 - z : z);
                seen++;
                if (g.Cell[i] != g.Cell[j] || (g.LbmAir.Length == C && g.LbmAir[i] != g.LbmAir[j])) { bad++; continue; }
                for (int q = 1; q < 19; q++)
                    if (g.WallLinks[q * C + i] != g.WallLinks[Mirror(q, my, mz) * C + j]) { bad++; break; }
            }
        return (bad, seen);
    }

    internal static Result RunOrifice(int nw, int nh, OpeningModel model, Backend backend, float t = 1f, float shift = 0f, float off = 0f, float? offZ = null)
    {
        var (grid, bm, opening, nRef, _, _, xw, xb, sideY, sideZ, y0, z0, dx) = BuildRig(nw, nh, t, shift, off, offZ);
        int Nz = grid.Nz, Ny = grid.Ny;
        var options = new RunOptions { Backend = backend, WindProfileExponent = 0f, OpeningModel = model };
        // Wind 1 m/s along +x; the velocity scale puts the inlet at UoLatTarget x A/A_duct in lattice units, so the
        // opening runs near 0.1 instead of 64x past the lattice speed limit.
        float uInLat = UoLatTarget * nw * nh / ((float)sideY * sideZ);
        using var session = new LbmSolver.LbmSession(grid, bm, new Vector2(1f, 0f), options, uMaxOverride: ULat / uInLat);
        int half = (int)(800 * nRef);             // the rig's step budget: 1600 n total, half warm-up
        session.Step(Math.Max(half, session.WarmupSteps));
        session.ResetAveraging();
        session.Step(half);
        var f = session.ToField();

        const float rhoAir = 1.2f;
        double q = 0;
        foreach (int i in Analysis.ThroatCells(grid, opening)) q += f.Ux[i];
        q *= dx * dx;
        double pUp = UpstreamTotalPressure(f, grid, xw - 2 * (int)nRef, rhoAir);
        int xDn = xw + (int)MathF.Ceiling(xb - xw);             // first cell wholly behind the plate
        double pDn = 0; int nd = 0;
        for (int z = 1; z < Nz - 1; z++)
        for (int y = 1; y < Ny - 1; y++)
        {
            int dz = z < z0 ? z0 - z : z >= z0 + nh ? z - (z0 + nh - 1) : 0;
            int dy = y < y0 ? y0 - y : y >= y0 + nw ? y - (y0 + nw - 1) : 0;
            if (Math.Max(dz, dy) < 1.5f * nRef) continue;
            pDn += f.P[grid.Index(xDn, y, z)]; nd++;
        }
        pDn /= nd;
        double dp = pUp - pDn, area = nw * nh * dx * dx;
        float cd = dp > 0 ? (float)(q / (area * Math.Sqrt(2 * dp / rhoAir))) : float.NaN;
        float uoLat = (float)(q / area) * session.VelocityScale;
        float re = uoLat * nRef / session.LatticeViscosity;
        var r = Analysis.Analyze(bm, f, new Vector2(1f, 0f), options).Openings.FirstOrDefault();
        float lossK = r?.LossK ?? float.NaN, modelled = r?.ModelledCd ?? float.NaN;
        string detail = $"cd={cd:F4} n={nRef:F2} t={t:F2} shift={shift:F3} off={off:F3}/{offZ ?? off:F3} model={model} q={q:E4} dp={dp:E4} uo_lat={uoLat:F4} re_lat={re:F0} nu_lat={session.LatticeViscosity:E2} " +
                        $"lossK={lossK:F3} modelled_cd={modelled:F3} app_cd={r?.EffectiveCd ?? float.NaN:F4} q_link={r?.FlowRate ?? float.NaN:E4} throat={Analysis.ThroatCells(grid, opening).Count} steps={session.Steps} backend=\"{session.Backend}\"";
        return new Result(cd, (float)q, (float)dp, re, lossK, modelled, detail);
    }

    internal static Result RunOrifice(int n, OpeningModel model, Backend backend, float t = 1f, float shift = 0f, float off = 0f, float? offZ = null) =>
        RunOrifice(n, n, model, backend, t, shift, off, offZ);

    /// <summary>The hand-built cell rig of before 6d (halfway bounce-back walls, whole-cell plate of <paramref name="wall"/> cells): kept as the
    /// cross-check of the product route above.</summary>
    internal static Result RunOrificeCells(int n, OpeningModel model, Backend backend, int wall = 1)
    {
        int side = 8 * n, up = 3 * n, down = 8 * n;
        int Ny = side + 2, Nz = side + 2, xw = 1 + up, Nx = xw + wall + down + 1;
        var grid = new VoxelGrid { Nx = Nx, Ny = Ny, Nz = Nz, Dx = Dx, Origin = Vector3.Zero, Cell = new byte[Nx * Ny * Nz] };
        int y0 = 1 + (side - n) / 2, z0 = 1 + (side - n) / 2;
        for (int z = 0; z < Nz; z++)
        for (int y = 0; y < Ny; y++)
        for (int x = 0; x < Nx; x++)
        {
            bool duct = y >= 1 && y <= Ny - 2 && z >= 1 && z <= Nz - 2;
            bool hole = y >= y0 && y < y0 + n && z >= z0 && z < z0 + n;
            bool solid = !duct || (x >= xw && x < xw + wall && !hole);
            grid.Cell[grid.Index(x, y, z)] = solid ? VoxelGrid.Solid : VoxelGrid.Air;
        }
        var opening = new Opening
        {
            Id = "orifice", Name = "orifice", Level = "L1", Kind = OpeningKind.Window, IsOpen = true,
            Center = new Vector3((xw + 0.5f * wall) * Dx, (y0 + 0.5f * n) * Dx, (z0 + 0.5f * n) * Dx), Normal = Vector3.UnitX,
            Width = n * Dx, Height = n * Dx, Depth = wall * Dx,
        };
        var bm = new BuildingModel
        {
            Name = "orifice", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = Nz * Dx, Analysed = true } },
            Openings = { opening },
        };
        var options = new RunOptions { Backend = backend, WindProfileExponent = 0f, OpeningModel = model };
        float uInLat = UoLatTarget * n * n / (side * (float)side);
        using var session = new LbmSolver.LbmSession(grid, bm, new Vector2(1f, 0f), options, uMaxOverride: ULat / uInLat);
        int half = 800 * n;
        session.Step(Math.Max(half, session.WarmupSteps));
        session.ResetAveraging();
        session.Step(half);
        var f = session.ToField();

        const float rhoAir = 1.2f;
        double q = 0;
        foreach (int i in Analysis.ThroatCells(grid, opening)) q += f.Ux[i];
        q *= Dx * Dx;
        double pUp = UpstreamTotalPressure(f, grid, xw - 2 * n, rhoAir);
        double pDn = 0; int nd = 0;
        for (int z = 1; z < Nz - 1; z++)
        for (int y = 1; y < Ny - 1; y++)
        {
            int dz = z < z0 ? z0 - z : z >= z0 + n ? z - (z0 + n - 1) : 0;
            int dy = y < y0 ? y0 - y : y >= y0 + n ? y - (y0 + n - 1) : 0;
            if (Math.Max(dz, dy) < 1.5f * n) continue;
            pDn += f.P[grid.Index(xw + wall, y, z)]; nd++;
        }
        pDn /= nd;
        double dp = pUp - pDn, area = n * n * Dx * Dx;
        float cd = dp > 0 ? (float)(q / (area * Math.Sqrt(2 * dp / rhoAir))) : float.NaN;
        float uoLat = (float)(q / area) * session.VelocityScale;
        float re = uoLat * n / session.LatticeViscosity;
        var r = Analysis.Analyze(bm, f, new Vector2(1f, 0f), options).Openings.FirstOrDefault();
        float lossK = r?.LossK ?? float.NaN, modelled = r?.ModelledCd ?? float.NaN;
        string detail = $"cd={cd:F4} n={n} wall={wall} cells-rig model={model} q={q:E4} dp={dp:E4} uo_lat={uoLat:F4} re_lat={re:F0} nu_lat={session.LatticeViscosity:E2} " +
                        $"lossK={lossK:F3} modelled_cd={modelled:F3} steps={session.Steps} backend=\"{session.Backend}\"";
        return new Result(cd, (float)q, (float)dp, re, lossK, modelled, detail);
    }

    /// <summary>Upstream reference of the Cd rig: spatial mean over the duct cross-section at cell column
    /// <paramref name="xu"/> of the pointwise total pressure P + 0.5 rho (Ux^2 + Uy^2 + Uz^2), Pa.
    /// Averaging definition: the field is already a TIME mean, so this is 0.5 rho |mean(u)|^2 (the head of the
    /// mean flow), NOT 0.5 rho mean(|u|^2), which would also count unsteady/turbulent kinetic energy. The two
    /// agree for a steady flow. All three components are included: a transverse velocity carries head too
    /// (0.6 Pa for 1 m/s at rho = 1.2), which an Ux-only form drops.</summary>
    internal static double UpstreamTotalPressure(FlowField f, VoxelGrid grid, int xu, float rho)
    {
        double sum = 0; int count = 0;
        for (int z = 1; z < grid.Nz - 1; z++)
        for (int y = 1; y < grid.Ny - 1; y++)
        {
            int i = grid.Index(xu, y, z);
            sum += f.P[i] + 0.5 * rho * (f.Ux[i] * f.Ux[i] + f.Uy[i] * f.Uy[i] + f.Uz[i] * f.Uz[i]); count++;
        }
        return sum / count;
    }
}
