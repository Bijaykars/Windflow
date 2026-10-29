using System.Diagnostics;
using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Validation;

/// <summary>One measured point on a profile line: where it is in the simulated model (metres), where it
/// plots along the line, the measured value and its bar (NaN = none given).</summary>
public sealed record MeasuredPoint(double Pos, Vector3 World, double Obs, double Bar);

/// <summary>A measured profile. Vertical = the position runs up the plot's vertical axis (a vertical
/// traverse) and the value along the horizontal one; otherwise position is horizontal.</summary>
public sealed class ProfileLine
{
    public string Name = "", Title = "", PosLabel = "", ValueLabel = "", BarLabel = "", MeasuredLabel = "measured";
    public bool Vertical;
    /// <summary>false: plotted and scored on its own, but left out of the case's overall metrics (e.g. the same
    /// points read off a second figure).</summary>
    public bool InOverall = true;
    public List<MeasuredPoint> Measured = new();
    public List<(double Pos, Vector3 World)> Dense = new();   // the simulated line, drawn continuous
}

/// <summary>Everything a case hands the runner.</summary>
public sealed class CaseSetup
{
    public BuildingModel Model = new();
    public RunOptions Options = new();
    public Vector2 Wind;
    /// <summary>Seconds per unit of the case's dimensionless time (e.g. H/u0); warm-up and blocks are in these units.</summary>
    public double TimeScale = 1;
    public double WarmupT = 60, BlockT = 10, ConvergeTol = 0.01;
    public int MinBlocks = 6, MaxBlocks = 30, ConvergeLag = 3;
    /// <summary>The compared quantity from a simulated velocity (e.g. u/u0).</summary>
    public Func<Vector3, double> Quantity = v => v.X;
    public List<ProfileLine> Lines = new();
    /// <summary>Absolute tolerance W of the hit rate / FAC2 threshold, in the compared quantity's units.</summary>
    public double W;
    public string WNote = "";
    public Dictionary<string, object> Info = new();
    /// <summary>Non-default variants (e.g. a finer grid) write to out/validation/&lt;id&gt;&lt;OutSuffix&gt; and label their rows.</summary>
    public string OutSuffix = "", TitleSuffix = "";
    /// <summary>Optional: a vertical x-z plane (world y) whose block-averaged field is drawn as field.png, over
    /// [SliceMin, SliceMax] (x, z). Quantity is applied to the in-plane velocity for the colour.</summary>
    public float? SliceY;
    public Vector2 SliceMin, SliceMax;
    public string SliceTitle = "", SliceUnit = "u0";
    public float SliceScale = 1, SliceMax01 = 0.5f;
    /// <summary>Called on the built grid before InspectGrid and the run, to reshape it (R2-05: the inlet velocity profile).</summary>
    public Action<VoxelGrid>? ShapeGrid;
    /// <summary>RZ-03: run a DomainBox case with the product's outer ring (closed top: the 6b NEE ring, outflow/inflow absorbing sponge and the
    /// prescribed-velocity lid, as the whole-building wind run has) instead of the user-box ring an open-top box gets. Wind cases only.</summary>
    public bool ProductRing;
    /// <summary>Called on the built grid before the run (geometry diagnostics into Info); may throw to refuse a bad grid.</summary>
    public Action<VoxelGrid, Dictionary<string, object>>? InspectGrid;
    /// <summary>Called once on the live session after the warm-up and before the first averaging block (tracer-tb16 starts its online scalar here).</summary>
    public Action<LbmSolver.LbmSession, VoxelGrid>? AfterWarmup;
    /// <summary>Called on the final averaged field (e.g. a mass balance) with the per-block fields' mean velocity.</summary>
    public Action<FlowField, Dictionary<string, object>>? CheckField;
    /// <summary>Scalars evaluated on every block-mean field (e.g. a ventilation flow rate); the runner stores the
    /// mean over blocks in Info[name], its standard error in Info[name + "_stderr"].</summary>
    public List<(string Name, Func<FlowField, double> Eval)> BlockScalars = new();
    /// <summary>Optional: the metrics.json "flow_error" text from Info after the run (else the Annex 20 wording).</summary>
    public Func<Dictionary<string, object>, string>? FlowSummary;
    /// <summary>Optional: the VALIDATION.md note for this run (else the Annex 20 wording).</summary>
    public Func<CaseRun, string>? Note;
    /// <summary>Optional: classifies a measured point (by line index and the point itself -- Pos is the line's own
    /// position axis, World.Z the world height) into a flow region (e.g. "ceiling jet", "recirculation core") for
    /// the report's q (hit-rate) miss breakdown. Purely a spatial label, not a solver diagnosis.</summary>
    public Func<int, MeasuredPoint, string>? RegionOf;
}

public abstract class ValidationCase
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Citation { get; }
    public abstract string Quantities { get; }
    public abstract CaseSetup Build(string dataDir, Dictionary<string, string> args);
}

/// <summary>Result of one run, the thing metrics.json and the VALIDATION.md row are made from.</summary>
public sealed class CaseRun
{
    public CaseSetup Setup = null!;
    public VoxelGrid Grid = null!;
    public double[][] PredMeasured = [];     // per line, cumulative block mean at the measured points
    public double[][] PredDense = [];        // per line, cumulative block mean along the dense line
    public Vector3[,]? Slice;                // block-mean velocity on CaseSetup.SliceY, [i, k] at SliceDx spacing
    public float SliceDx;
    public List<double> ConvergenceHistory = new();   // RMS change of the cumulative mean per block (quantity units)
    public double MaxStdErr;                 // max over measured points of std(block means)/sqrt(n)
    public int Blocks, WarmupSteps, TotalSteps;
    public double Dt, SimSeconds, WallSeconds, Mlups;
    public bool Converged;
    public string Backend = "";
}

public static class Runner
{
    /// <summary>Builds the grid through the product pipeline (Voxelizer.Build with RunOptions.Domain), steps an
    /// LbmSession through a warm-up, then averages in blocks: each block restarts the session's time average
    /// (ResetAveraging), steps BlockT, and samples the block mean at every profile point. The reported field is
    /// the mean of the block means. Converged when adding the last ConvergeLag blocks moved that cumulative mean
    /// by less than ConvergeTol (RMS over the measured points), with at least MinBlocks blocks.</summary>
    public static CaseRun Run(CaseSetup s)
    {
        var sw = Stopwatch.StartNew();
        var o = s.Options;
        bool gpu = o.Backend != Backend.Cpu && LbmSolver.GpuAvailable;
        int cells = o.TargetCells ?? Simulation.TargetCells(o.Quality, gpu);
        var grid = Voxelizer.Build(s.Model, cells, o.MaxDegreeOfParallelism, o.Domain,
            minDx: o.MinDx is { } md ? Math.Min(md, 0.1f) : 0.1f);   // same mapping as Simulation.Run
        if (s.ProductRing) grid.OpenZMax = false;   // RZ-03: LbmSolver.BuildRing then gives the box the whole-building ring
        s.Info["outer_ring"] = grid.OpenZMax ? "user-box ring (pre-6b equilibrium, open top)" : "product ring (6b NEE + sponge + lid, closed top)";
        s.ShapeGrid?.Invoke(grid);
        s.InspectGrid?.Invoke(grid, s.Info);
        Console.WriteLine($"  grid {grid.Nx} x {grid.Ny} x {grid.Nz} = {grid.Count:N0} cells, Dx = {grid.Dx * 1000:0.#} mm");
        foreach (var w in grid.Warnings) Console.WriteLine($"  grid warning: {w}");
        AuditPoints(s, grid);

        var run = new CaseRun { Setup = s, Grid = grid };
        using var session = LbmSolver.Start(grid, s.Model, s.Wind, o);
        run.Backend = session.Backend;
        // Actual solver lattice parameters (LbmSession, not the case's own guess) for effective-Re reports.
        s.Info["solver_dt_s"] = (double)session.TimeStepSeconds;
        s.Info["solver_nu_lat_used"] = (double)session.LatticeViscosity;
        s.Info["solver_velocity_scale"] = (double)session.VelocityScale;
        s.Info["solver_nu_lat_floor"] = (double)session.ViscosityFloor;
        session.Step(1);
        run.Dt = session.SimSeconds;
        if (run.Dt <= 0) throw new InvalidOperationException("session did not advance (inert: nothing drives the flow?)");
        int StepsFor(double t) => Math.Max(1, (int)Math.Ceiling(t * s.TimeScale / run.Dt));

        int warm = StepsFor(s.WarmupT) - 1;
        Console.WriteLine($"  backend {session.Backend}, dt = {run.Dt * 1000:0.###} ms, warm-up {s.WarmupT} T = {warm + 1:N0} steps");
        StepChunked(session, warm);
        run.WarmupSteps = (int)session.Steps;
        s.AfterWarmup?.Invoke(session, grid);

        int blockSteps = StepsFor(s.BlockT);
        var blocksM = new List<Vector3[][]>();
        var scalars = s.BlockScalars.Select(_ => new List<double>()).ToArray();
        var sumM = s.Lines.Select(l => new Vector3[l.Measured.Count]).ToArray();
        var sumD = s.Lines.Select(l => new Vector3[l.Dense.Count]).ToArray();
        var history = new List<double[]>();   // flattened cumulative mean at measured points, per block
        FlowField? last = null;
        int sNx = 0, sNz = 0; Vector3[,]? slice = null;
        if (s.SliceY is float sy)
        {
            sNx = (int)MathF.Round((s.SliceMax.X - s.SliceMin.X) / grid.Dx * 2); sNz = (int)MathF.Round((s.SliceMax.Y - s.SliceMin.Y) / grid.Dx * 2);
            slice = new Vector3[sNx, sNz];
            run.SliceDx = grid.Dx / 2;
        }
        for (int b = 0; b < s.MaxBlocks; b++)
        {
            session.ResetAveraging(startNow: true);
            StepChunked(session, blockSteps);
            var f = session.ToField();
            var bm = new Vector3[s.Lines.Count][];
            for (int li = 0; li < s.Lines.Count; li++)
            {
                var line = s.Lines[li];
                bm[li] = line.Measured.Select(p => Sampler.Velocity(f, p.World)).ToArray();
                for (int k = 0; k < bm[li].Length; k++) sumM[li][k] += bm[li][k];
                for (int k = 0; k < line.Dense.Count; k++) sumD[li][k] += Sampler.Velocity(f, line.Dense[k].World);
            }
            blocksM.Add(bm);
            for (int si = 0; si < scalars.Length; si++) scalars[si].Add(s.BlockScalars[si].Eval(f));
            if (slice != null)
                for (int i = 0; i < sNx; i++)
                    for (int k = 0; k < sNz; k++)
                        slice[i, k] += Sampler.Velocity(f, new Vector3(s.SliceMin.X + (i + 0.5f) * run.SliceDx, s.SliceY!.Value, s.SliceMin.Y + (k + 0.5f) * run.SliceDx));
            int n = b + 1;
            // No profile lines (tracer-tb16): convergence is judged on the cumulative mean of the first block scalar.
            var cum = s.Lines.Count == 0 ? [scalars[0].Average()] : sumM.SelectMany(a => a.Select(v => s.Quantity(v / n))).ToArray();
            history.Add(cum);
            if (last == null) last = f; else FlowFieldMean.Add(last, f);
            double change = double.NaN;
            if (history.Count > s.ConvergeLag)
            {
                var prev = history[^(s.ConvergeLag + 1)];
                change = Math.Sqrt(cum.Zip(prev, (x, y) => (x - y) * (x - y)).Average());
            }
            run.ConvergenceHistory.Add(change);
            Console.WriteLine($"  block {n}: t = {session.SimSeconds / s.TimeScale:0.0} T, change over last {s.ConvergeLag} blocks = {change:0.0000}, {session.Mlups:0} MLUPS");
            if (n >= s.MinBlocks && change < s.ConvergeTol) { run.Converged = true; break; }
        }

        int nb = blocksM.Count;
        run.Blocks = nb;
        run.PredMeasured = sumM.Select(a => a.Select(v => s.Quantity(v / nb)).ToArray()).ToArray();
        run.PredDense = sumD.Select(a => a.Select(v => s.Quantity(v / nb)).ToArray()).ToArray();
        double maxSe = 0;
        for (int li = 0; li < s.Lines.Count; li++)
            for (int k = 0; k < run.PredMeasured[li].Length; k++)
            {
                double se = BlockVelocity.StandardError(blocksM.Select(bm => bm[li][k]).ToArray(), s.Quantity);
                maxSe = Math.Max(maxSe, se);
            }
        run.MaxStdErr = maxSe;
        s.Info["stderr_method"] = "vector block bootstrap, 512 deterministic resamples; assumes independent blocks";
        if (slice != null)
        {
            for (int i = 0; i < sNx; i++) for (int k = 0; k < sNz; k++) slice[i, k] /= nb;
            run.Slice = slice;
        }
        for (int si = 0; si < scalars.Length; si++)
        {
            var v = scalars[si];
            double mean = v.Average(), sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : double.NaN;
            s.Info[s.BlockScalars[si].Name] = mean;
            s.Info[s.BlockScalars[si].Name + "_stderr"] = sd / Math.Sqrt(v.Count);
        }
        if (last != null) s.CheckField?.Invoke(last, s.Info);
        run.TotalSteps = (int)session.Steps;
        run.SimSeconds = session.SimSeconds;
        run.Mlups = session.Mlups;
        run.WallSeconds = sw.Elapsed.TotalSeconds;
        return run;
    }

    /// <summary>Logs every measured point that is outside the domain, clamped to an edge cell, or whose trilinear stencil
    /// touches a Solid cell (which reads 0), and stores the summary in Info["sampling_audit"]. Nothing is moved or
    /// corrected: the scored values are exactly what Sampler.Velocity returns.</summary>
    static void AuditPoints(CaseSetup s, VoxelGrid grid)
    {
        int n = 0, outside = 0, clamped = 0, touching = 0; double worst = 0;
        var listed = new List<string>();
        foreach (var line in s.Lines)
            foreach (var p in line.Measured)
            {
                n++;
                var a = Sampler.Audit(grid, p.World);
                if (a.Outside) outside++;
                if (a.Clamped) clamped++;
                if (a.SolidWeight > 1e-6) { touching++; worst = Math.Max(worst, a.SolidWeight); }
                if (a.Outside || a.Clamped || a.SolidWeight > 1e-6)
                    listed.Add($"{line.Name} pos {p.Pos:0.###} at ({p.World.X:0.###}, {p.World.Y:0.###}, {p.World.Z:0.###}) m: " +
                        $"{(a.Outside ? "OUTSIDE domain, " : "")}{(a.Clamped ? "clamped to edge cell, " : "")}{a.SolidWeight * 100:0}% of the stencil on solid cells");
            }
        string summary = $"{n} measured points: {outside} outside the domain, {clamped} clamped to an edge cell, {touching} with a stencil touching solid cells (up to {worst * 100:0}% of the weight on a zero-valued solid cell); values are NOT adjusted";
        s.Info["sampling_audit"] = summary;
        Console.WriteLine($"  sampling audit: {summary}");
        foreach (var l in listed.Take(15)) Console.WriteLine($"    {l}");
        if (listed.Count > 15) Console.WriteLine($"    ... and {listed.Count - 15} more");
    }

    static void StepChunked(LbmSolver.LbmSession session, int steps)
    {
        const int chunk = 2000;
        for (int done = 0; done < steps; done += chunk) session.Step(Math.Min(chunk, steps - done));
    }
}

public static class Sampler
{
    /// <summary>Trilinear interpolation of the cell-centred velocity at a world point. Solid cells hold 0
    /// (FlowField convention), so within half a cell of a wall this interpolates linearly to zero at the
    /// solid cell centre; points outside the grid clamp to the edge cells.</summary>
    public static Vector3 Velocity(FlowField f, Vector3 p)
    {
        var g = f.Grid;
        var q = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(q.X), y0 = (int)MathF.Floor(q.Y), z0 = (int)MathF.Floor(q.Z);
        float fx = q.X - x0, fy = q.Y - y0, fz = q.Z - z0;
        var sum = Vector3.Zero;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            int x = Math.Clamp(x0 + dx, 0, g.Nx - 1), y = Math.Clamp(y0 + dy, 0, g.Ny - 1), z = Math.Clamp(z0 + dz, 0, g.Nz - 1);
            float w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
            int i = g.Index(x, y, z);
            sum += w * new Vector3(f.Ux[i], f.Uy[i], f.Uz[i]);
        }
        return sum;
    }

    /// <summary>Where a measured point sits relative to the grid, WITHOUT changing what <see cref="Velocity"/> returns.
    /// Outside: the point is beyond the domain box. Clamped: it is nearer a domain face than the first/last cell
    /// centre, so Velocity extrapolates the edge cell as a constant. SolidWeight: the share of the trilinear stencil
    /// that falls on Solid cells, which hold 0 in a FlowField (a point half a cell from a wall therefore reads
    /// low by that share). Same stencil arithmetic as Velocity.</summary>
    public readonly record struct SampleAudit(bool Outside, bool Clamped, double SolidWeight);

    public static SampleAudit Audit(VoxelGrid g, Vector3 p)
    {
        var rel = (p - g.Origin) / g.Dx;
        bool outside = rel.X < 0 || rel.Y < 0 || rel.Z < 0 || rel.X > g.Nx || rel.Y > g.Ny || rel.Z > g.Nz;
        var q = rel - new Vector3(0.5f);
        bool clamped = q.X < 0 || q.Y < 0 || q.Z < 0 || q.X > g.Nx - 1 || q.Y > g.Ny - 1 || q.Z > g.Nz - 1;
        int x0 = (int)MathF.Floor(q.X), y0 = (int)MathF.Floor(q.Y), z0 = (int)MathF.Floor(q.Z);
        float fx = q.X - x0, fy = q.Y - y0, fz = q.Z - z0;
        double solid = 0;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            int x = Math.Clamp(x0 + dx, 0, g.Nx - 1), y = Math.Clamp(y0 + dy, 0, g.Ny - 1), z = Math.Clamp(z0 + dz, 0, g.Nz - 1);
            float w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
            if (g.Cell[g.Index(x, y, z)] == VoxelGrid.Solid) solid += w;
        }
        return new SampleAudit(outside, clamped, solid);
    }
}
