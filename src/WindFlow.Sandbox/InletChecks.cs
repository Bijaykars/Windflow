// Supply inlet: the link-wise velocity inlet (Ladd moving-wall bounce-back on the patch links) and its
// flux controller deliver the set flow at every patch width.  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --inlet      (or as part of the default run)
//
// The old equilibrium-reset patch imposed u0 at the patch cell CENTRE, not a flux through its face, and
// delivered 0.42 / 0.61 / 0.68 / 0.71 of the set flow at 1 / 2 / 4 / 6 cells across (Annex 20 2D). This scene
// measures delivered/set directly: a duct-shaped room, the patch flush in the first air column of the closed
// end wall, the far end open, and the flux of the time-averaged velocity through two cross-sections
// downstream. Mass conservation makes both cross-sections carry whatever the patch really injects.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static class InletChecks
{
    const int Nx = 64, Ny = 26, Nz = 26;      // walls at y/z = 0 and N-1, closed at x = 0, open at x = Nx-1
    const float Dx = 0.05f;
    const float U0 = 1f;                      // m/s face velocity; FlowRate = U0 * (n*Dx)^2

    /// <summary>Runs every inlet check and returns the number of failures (0 = all passed).</summary>
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

        Console.WriteLine("== inlet checks ==");
        int[] widths = { 1, 2, 4, 6 };
        string[] ids = { "55a", "55b", "55c", "55d" };
        var cpu = new Result[widths.Length];
        for (int w = 0; w < widths.Length; w++)
        {
            int n = widths[w];
            var link = RunDuct(n, linkWise: true, Backend.Cpu);
            var eq = RunDuct(n, linkWise: false, Backend.Cpu);
            cpu[w] = link;
            bool pass = MathF.Abs(link.Plane1 - 1f) <= 0.02f && MathF.Abs(link.Plane2 - 1f) <= 0.02f;
            Check($"{ids[w]} supply patch {n} cell(s) across delivers its set flow within 2% (link-wise inlet + flux controller)", pass,
                $"delivered_over_set={link.Plane1:F4} plane2_over_set={link.Plane2:F4} eqreset_over_set={eq.Plane1:F4} " +
                $"scale={link.Scale:F4} gain={link.Gain:F4} updates={link.Updates} links={link.Links} cells={link.Cells} " +
                $"link_flux_over_set={link.LinkRatio:F4} steps={link.Steps} backend={link.Backend}");
        }

        // The controller at work: a corner patch loses the diagonal links into the two walls, so at scale 1 its
        // links carry only (4 x 1 - 4/6)/4 = 5/6 of the set flow; the loop has to settle on scale 6/5.
        var cornerRun = RunDuct(2, linkWise: true, Backend.Cpu, corner: true);
        var cornerEq = RunDuct(2, linkWise: false, Backend.Cpu, corner: true);
        Check("55f corner supply patch (2 cells, against side wall and ceiling): controller restores the set flow within 2%",
            MathF.Abs(cornerRun.Plane1 - 1f) <= 0.02f && MathF.Abs(cornerRun.Scale - 1.2f) <= 0.02f && MathF.Abs(cornerRun.Gain - 1f) <= 0.01f,
            $"delivered_over_set={cornerRun.Plane1:F4} plane2_over_set={cornerRun.Plane2:F4} eqreset_over_set={cornerEq.Plane1:F4} " +
            $"scale={cornerRun.Scale:F4} (expect 1.2) gain={cornerRun.Gain:F4} updates={cornerRun.Updates} links={cornerRun.Links} " +
            $"link_flux_over_set={cornerRun.LinkRatio:F4}");

        // Batch 3b (RV-01 follow-up): a Return grille is the mirror image, a link-wise sink with the same PI controller. The same duct
        // with the patch drawing air OUT of the closed end, the far end open: the link flux across both planes must equal the set flow.
        // (The equilibrium-reset return extracted whatever its carved cells gave.)
        foreach (var (n, id) in new[] { (1, "55g"), (2, "55h"), (4, "55i"), (6, "55j") })
        {
            // 12 flow-throughs of warm-up, not 3: the closed duct's slow density transient reads about 1 % low at 3 for a sink and a source alike.
            var ret = RunDuct(n, linkWise: true, Backend.Cpu, sink: true, warmup: 12);
            // One cell across is below the resolution where the plane reading means much (the supply's 55a reads +0.6 %); it keeps the supply's 2 % band.
            float band = n == 1 ? 0.02f : 0.005f;
            Check($"{id} return grille {n} cell(s) across extracts its set flow within {band * 100:0.#}% (link-wise sink + flux controller)",
                MathF.Abs(ret.Plane1 - 1f) <= band && MathF.Abs(ret.Plane2 - 1f) <= band,
                $"delivered_over_set={ret.Plane1:F4} plane2_over_set={ret.Plane2:F4} scale={ret.Scale:F4} gain={ret.Gain:F4} updates={ret.Updates} " +
                $"links={ret.Links} cells={ret.Cells} link_flux_over_set={ret.LinkRatio:F4} steps={ret.Steps} backend={ret.Backend}");
        }
        {
            var retCorner = RunDuct(2, linkWise: true, Backend.Cpu, corner: true, sink: true, warmup: 12);
            Check("55k corner return grille (2 cells, against side wall and ceiling): controller restores the set flow within 0.5%",
                MathF.Abs(retCorner.Plane1 - 1f) <= 0.005f && MathF.Abs(retCorner.Scale - 1.2f) <= 0.02f,
                $"delivered_over_set={retCorner.Plane1:F4} plane2_over_set={retCorner.Plane2:F4} scale={retCorner.Scale:F4} (expect 1.2) " +
                $"gain={retCorner.Gain:F4} updates={retCorner.Updates} links={retCorner.Links} link_flux_over_set={retCorner.LinkRatio:F4}");
        }

        // RW-01 / RW-04 (batch R): a sealed room whose set supply (0.10 m3/s) and extract (0.05) differ has no pressure anchor once the Return is flux-controlled, so
        // Voxelizer.BalanceSealedPatches scales the extract to the supply (both stay controlled) and writes a room note naming the room. Bounded = the mean lattice density
        // stays within 1 +- 0.01 over 5000 steps (the batch 3c passive release plateaued at 1.18).
        {
            var run = SealedRun(0.10f, 0.05f);
            bool named = run.Note && run.Detail.StartsWith("Room Room: Supply (360 m³/h) and extract (180 m³/h) do not balance in this sealed room; WindFlow balanced them at 360 m³/h.");
            Check("55l sealed room, supply 0.10 / extract 0.05 m3/s: the extract is balanced to the supply (both controlled), the room note names the room, mean density stays within 1 +- 0.01 over 5000 steps",
                run.PassiveCells == 0 && run.ReturnCells > 0 && named && run.MaxDev <= 0.01f,
                $"passive_cells={run.PassiveCells}/{run.ReturnCells} note=\"{run.Detail}\" max|mean_rho-1|={run.MaxDev:F4} series=[{run.Series}] steps={run.Steps} dx={run.Dx * 1000:0}mm backend={run.Backend}");
        }

        if (!LbmSolver.GpuAvailable) { Skip("55e supply inlet GPU = CPU within 1% at every width", "no GPU available"); }
        else
        {
            bool ok = true; var parts = new List<string>();
            for (int w = 0; w < widths.Length; w++)
            {
                var g = RunDuct(widths[w], linkWise: true, Backend.Gpu);
                float d = MathF.Abs(g.Plane1 - cpu[w].Plane1);
                ok &= d <= 0.01f && MathF.Abs(g.Scale - cpu[w].Scale) <= 1e-4f;
                parts.Add($"n{widths[w]}_cpu={cpu[w].Plane1:F4} n{widths[w]}_gpu={g.Plane1:F4} n{widths[w]}_scale_gpu={g.Scale:F4}");
                if (w == widths.Length - 1) parts.Add($"backend=\"{g.Backend}\"");
            }
            Check("55e supply inlet GPU = CPU within 1% at every width (delivered/set and controller scale)", ok, string.Join(" ", parts));
        }

        // RunOptions.SupplyPerturbation: 3 % rms on the 4-cell patch. Mass-neutral over the face, so the delivered flow and
        // the controller scale must match the unperturbed run; host-side and seeded, so the GPU must match the CPU.
        {
            var p = RunDuct(4, linkWise: true, Backend.Cpu, perturb: 0.03f);
            bool ok = MathF.Abs(p.Plane1 - 1f) <= 0.02f && MathF.Abs(p.Plane1 - cpu[2].Plane1) <= 0.005f && MathF.Abs(p.Scale - cpu[2].Scale) <= 1e-3f;
            string gd = "gpu=skipped_no_gpu";
            if (LbmSolver.GpuAvailable)
            {
                var g = RunDuct(4, linkWise: true, Backend.Gpu, perturb: 0.03f);
                ok &= MathF.Abs(g.Plane1 - p.Plane1) <= 0.01f && MathF.Abs(g.Scale - p.Scale) <= 1e-4f;
                gd = $"gpu_delivered_over_set={g.Plane1:F4} gpu_scale={g.Scale:F4}";
            }
            Check("55g supply patch 4 cells, 3% seeded face perturbation: set flow within 2%, within 0.5% of unperturbed, GPU = CPU", ok,
                $"delivered_over_set={p.Plane1:F4} unperturbed={cpu[2].Plane1:F4} scale={p.Scale:F4} unperturbed_scale={cpu[2].Scale:F4} {gd}");
        }

        // SupplyPerturbation is the isotropic Tu: axial and both tangential components carry rms Tu x U. Face-mean tangential
        // velocity ~ 0, per-component rms within 20 % of target, delivered flow unchanged (tangential wall speed is mass-neutral).
        {
            const float tu = 0.19f;
            var p = RunDuct(4, linkWise: true, Backend.Cpu, perturb: tu, window: 4);   // P13a-04: 4 windows, the flux ripple of a fluctuating face averages out
            var f = p.Face ?? [];
            // P13a-04 (rule 3): the face rms is SupplyTuTransfer x Tu now (was Tu, of which the fluid received about half); what must hold is the Tu the fluid receives
            // 1 cell off the face, within 10 % of the request, and the net flux still unchanged to 0.5 %.
            var (dtu, _, _, _) = DeliveredTu(4, tu, Backend.Cpu, [1]);
            bool ok = f.Length == 6 && MathF.Abs(f[1]) <= 0.1f * tu && MathF.Abs(f[2]) <= 0.1f * tu;
            if (ok) for (int c = 0; c < 3; c++) ok &= MathF.Abs(f[3 + c] / (tu * LbmSolver.LbmSession.SupplyTuTransfer(4)) - 1f) <= 0.2f;
            ok &= MathF.Abs(dtu[0] / tu - 1f) <= 0.1f;
            ok &= MathF.Abs(p.Plane1 - cpu[2].Plane1) <= 0.005f && MathF.Abs(p.Scale - cpu[2].Scale) <= 0.01f;
            Check("55h supply patch 4 cells, Tu 0.19 isotropic perturbation: delivered Tu 1 cell off the face within 10% of Tu, tangential face mean ~ 0, flow unchanged within 0.5%", ok,
                f.Length == 6 ? $"delivered_Tu_1cell={dtu[0]:F4} ({dtu[0] / tu:F2}x) mean_t1={f[1]:F4} mean_t2={f[2]:F4} rms_ax={f[3]:F3} rms_t1={f[4]:F3} rms_t2={f[5]:F3} (face = {LbmSolver.LbmSession.SupplyTuTransfer(4):F2} x Tu) delivered_over_set={p.Plane1:F4} unperturbed={cpu[2].Plane1:F4} scale={p.Scale:F4} unperturbed_scale={cpu[2].Scale:F4}" : "no face stats");
        }

        // Per-device Tu (AirSource.TurbulenceIntensity + RunOptions.SupplyTuPerSource, the High Accuracy path): the source's own Tu
        // gives the same isotropic face statistics as the global option; it does nothing with the flag off; the type default (a square
        // 0.2 m patch = grille / duct end = 0.10) applies when the field is null; a face under 2 cells across (l0 < 2 dx) injects nothing.
        {
            var own = RunDuct(4, linkWise: true, Backend.Cpu, srcTu: 0.19f, perSource: true);
            var off = RunDuct(4, linkWise: true, Backend.Cpu, srcTu: 0.19f, perSource: false);
            var def = RunDuct(4, linkWise: true, Backend.Cpu, srcTu: -1f, perSource: true);
            var tiny = RunDuct(1, linkWise: true, Backend.Cpu, srcTu: 0.19f, perSource: true);
            var f = own.Face ?? []; var fo = off.Face ?? []; var fd = def.Face ?? []; var ft = tiny.Face ?? [];
            bool ok = f.Length == 6 && fo.Length == 6 && fd.Length == 6 && ft.Length == 6;
            if (ok)
            {
                for (int c = 0; c < 3; c++) ok &= MathF.Abs(f[3 + c] / (0.19f * LbmSolver.LbmSession.SupplyTuTransfer(4)) - 1f) <= 0.2f;   // P13a-04: face rms = transfer x Tu
                for (int c = 0; c < 3; c++) ok &= fo[3 + c] <= 1e-4f && ft[3 + c] <= 1e-4f;
                for (int c = 0; c < 3; c++) ok &= MathF.Abs(fd[3 + c] / (0.10f * LbmSolver.LbmSession.SupplyTuTransfer(4)) - 1f) <= 0.2f;   // P13a-04
                ok &= MathF.Abs(own.Plane1 - cpu[2].Plane1) <= 0.01f && MathF.Abs(def.Plane1 - cpu[2].Plane1) <= 0.01f;
            }
            Check("55i per-device Tu: own Tu 0.19 gives isotropic face rms, flag off = none, null = type default 0.10, 1-cell face (l0 < 2 dx) = none", ok,
                f.Length == 6 && fo.Length == 6 && fd.Length == 6 && ft.Length == 6 ? $"own_rms={f[3]:F3}/{f[4]:F3}/{f[5]:F3} off_rms={fo[3]:F3} default_rms={fd[3]:F3}/{fd[4]:F3}/{fd[5]:F3} tiny_rms={ft[3]:F3} delivered_own={own.Plane1:F4} delivered_default={def.Plane1:F4} unperturbed={cpu[2].Plane1:F4}" : "no face stats");
        }

        // RP follow-up 3: a supply patch narrower than 4 cells that runs with a Tu delivers less than requested (n = 3 about 0.35-0.5x, n = 2 axial only, FIXLOG Batch RP): a note says so.
        {
            string TuNote(int n)
            {
                var (g, m, _) = Duct(n, linkWise: true, corner: false, srcTu: 0.19f, sink: false);
                using var s = LbmSolver.Start(g, m, Vector2.Zero, new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, SupplyTuPerSource = true });
                return string.Join(" | ", g.Warnings.Where(w => w.Contains("its supply patch is only", StringComparison.Ordinal)));
            }
            string n3 = TuNote(3), n4 = TuNote(4);
            Check("RP-3 a supply patch under 4 cells across with a Tu says the delivered turbulence is below the request; 4 cells gets no note",
                n3.Contains("only 3 cells across", StringComparison.Ordinal) && n3.Contains("below the requested 19 %", StringComparison.Ordinal) && n4.Length == 0, $"3 cells: \"{n3}\" · 4 cells: \"{n4}\"");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL INLET CHECKS PASSED" : $"{failures} INLET CHECK(S) FAILED");
        return failures;
    }

    readonly record struct Sealed(int ReturnCells, int PassiveCells, bool Note, string Detail, float MaxDev, float FirstRise, float LastRise, string Series, int Steps, float Dx, string Backend);

    /// <summary>The "supply" base scene (a closed 8 x 6 x 3 m room, supply and return patches under the ceiling) with the given set flows, voxelised as the app does;
    /// 5000 steps, the mean lattice density over the room's air cells read every 500.</summary>
    static Sealed SealedRun(float supply, float extract)
    {
        var model = Program.BuildSupply();
        model.Sources[0].FlowRate = supply; model.Sources[1].FlowRate = extract;
        model.Sources[0].Center = new Vector3(2f, 2f, 2.6f); model.Sources[1].Center = new Vector3(6f, 4f, 2.6f);   // clear of the roof slab at this cell size
        var grid = Voxelizer.Build(model, 400_000, null, null);
        int ret = grid.FixedCells.Count(f => f.Mode == 1), passive = grid.FixedCells.Count(f => f.Mode == 1 && f.Supply < 0);
        string detail = grid.Warnings.FirstOrDefault(w => w.Contains("do not balance in this sealed room")) ?? "";
        bool note = detail.Length > 0;
        var inRoom = new List<int>();
        for (int z = 0; z < grid.Nz; z++) for (int y = 0; y < grid.Ny; y++) for (int x = 0; x < grid.Nx; x++)
        {
            int i = grid.Index(x, y, z);
            var c = grid.CellCenter(x, y, z);
            if (grid.Cell[i] == VoxelGrid.Air && c.X > 0.3f && c.X < 7.7f && c.Y > 0.3f && c.Y < 5.7f && c.Z > 0.1f && c.Z < 2.9f) inRoom.Add(i);
        }
        using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = Backend.Auto, WindProfileExponent = 0f });
        var rho = new float[grid.Count];
        float maxDev = 0f, prev = 1f, first = 0f, last = 0f; var series = new List<string>();
        for (int block = 0; block < 10; block++)
        {
            session.Step(500);
            session.ReadDensity(rho);
            double sum = 0; foreach (int i in inRoom) sum += rho[i];
            float mean = (float)(sum / inRoom.Count);
            maxDev = MathF.Max(maxDev, float.IsFinite(mean) ? MathF.Abs(mean - 1f) : float.PositiveInfinity);
            if (block == 0) first = mean - prev;
            last = mean - prev; prev = mean;
            series.Add(mean.ToString("F4"));
        }
        return new Sealed(ret, passive, note, detail, maxDev, first, last, string.Join(" ", series), (int)session.Steps, grid.Dx, session.Backend);
    }

    readonly record struct Result(float Plane1, float Plane2, float Scale, float Gain, int Updates, int Links, int Cells,
        float LinkRatio, int Steps, string Backend, float[]? Face = null);

    static Result RunDuct(int n, bool linkWise, Backend backend, bool corner = false, float perturb = 0f, float srcTu = -1f, bool perSource = false, bool sink = false, int warmup = 3, int window = 1)
    {
        var (grid, model, flow) = Duct(n, linkWise, corner, srcTu, sink);
        var options = new RunOptions { Backend = backend, WindProfileExponent = 0f, SupplyPerturbation = perturb, SupplyTuPerSource = perSource };
        using var session = LbmSolver.Start(grid, model, Vector2.Zero, options);
        session.Step(warmup * session.PlannedSteps);
        session.ResetAveraging();
        // Delivered flow = LINK flux across the plane (conserved mass crossing, wall-bound populations excluded),
        // averaged per step over the window. Node-velocity plane sums read 0.935-0.985 with the slip wall model on.
        int[] planes = [Nx / 2, 3 * Nx / 4]; var linkSum = new double[2];
        for (int s = 0, w = window * session.PlannedSteps; s < w; s++)
        {
            session.Step(1);
            var q = session.PlaneLinkFluxX(planes);
            linkSum[0] += q[0]; linkSum[1] += q[1];
        }
        var field = session.ToField();
        float Plane(int p) => (float)(linkSum[p] / (window * session.PlannedSteps) / flow) * (sink ? -1f : 1f);   // a return draws air toward -x
        var sr = field.Supplies.Count > 0 ? field.Supplies[0] : new SupplyResult();
        return new Result(Plane(0), Plane(1), sr.Scale, sr.Gain, sr.Updates, sr.Links, sr.Cells,
            sr.SetFlowRate > 0 ? sr.FlowRate / sr.SetFlowRate : float.NaN, (int)session.Steps, session.Backend, session.SupplyFaceStats());
    }

    /// <summary>`--tu-run n:request[:factor] ...`: delivered Tu at 1, 2, 3, 5 and 9 cells downstream of an n-cell patch (GPU from n = 6, else CPU).</summary>
    internal static int RunTu(string[] specs)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int[] dists = [1, 2, 3, 5, 9]; var base0 = new Dictionary<int, double[]>();
        foreach (var spec in specs)
        {
            var p = spec.Split(':');
            if (p[0] == "flux")   // flux:n:request:windows -- delivered / set at mid-duct, perturbed vs unperturbed, over that many PlannedSteps windows
            {
                int fn = int.Parse(p[1]), win = int.Parse(p[3]);
                var pr = RunDuct(fn, linkWise: true, Backend.Cpu, perturb: float.Parse(p[2], inv), window: win);
                var u0 = RunDuct(fn, linkWise: true, Backend.Cpu, window: win);
                Console.WriteLine($"[FLUX] n={fn} request={p[2]} windows={win} delivered_over_set={pr.Plane1:F4} unperturbed={u0.Plane1:F4} diff={(pr.Plane1 / u0.Plane1 - 1) * 100:+0.00;-0.00}% scale={pr.Scale:F4}/{u0.Scale:F4}");
                continue;
            }
            int n = int.Parse(p[0]); float req = float.Parse(p[1], inv);
            var (tu, mean, face, mom) = DeliveredTu(n, req, Backend.Cpu, dists);
            if (!base0.TryGetValue(n, out var mom0)) base0[n] = mom0 = DeliveredTu(n, 0f, Backend.Cpu, dists).Momentum;
            string rel = string.Join(" ", tu.Select((v, i) => $"d{dists[i]}={v:F4}({v / req:F2}x)"));
            Console.WriteLine($"[TU] n={n} request={req:F2} delivered_Tu_iso {rel} mean_ux=[{string.Join(" ", mean.Select(v => v.ToString("F3")))}] face_rms=[{string.Join(" ", face.Skip(3).Select(v => v.ToString("F3")))}] mean_momentum_over_unperturbed_d1={mom[0] / mom0[0]:F4}_d3={mom[1] / mom0[1]:F4} total_d1={mom[2] / mom0[2]:F4}_d3={mom[3] / mom0[3]:F4}");
        }
        return 0;
    }

    /// <summary>6i (P13a-04): the Tu the fluid receives. Per-step velocity of the interior cells of the n x n patch's footprint (the edge cells
    /// sit against the wall), at <paramref name="dists"/> cells downstream of the patch: isotropic Tu = sqrt((u'^2 + v'^2 + w'^2) / 3) / mean(u),
    /// u' the temporal rms about each cell's mean, averaged over the cells (the P13a harness measure). Also the momentum flux
    /// sum(rho u^2) at the farthest plane over the one at the first, whole duct section, with and without the perturbation.</summary>
    internal static (float[] Tu, float[] Mean, float[] Face, double[] Momentum) DeliveredTu(int n, float request, Backend backend, int[] dists)
    {
        var (grid, model, _) = Duct(n, true, false, -1f, false);
        using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = backend, WindProfileExponent = 0f, SupplyPerturbation = request });
        session.Step(2 * session.PlannedSteps);
        int y0 = (Ny - n) / 2, z0 = (Nz - n) / 2, T = 2 * session.PlannedSteps;
        var cells = new List<int>[dists.Length];
        for (int d = 0; d < dists.Length; d++)
        {
            cells[d] = new List<int>();
            for (int z = z0 + 1; z < z0 + n - 1; z++) for (int y = y0 + 1; y < y0 + n - 1; y++) cells[d].Add(grid.Index(1 + dists[d], y, z));
        }
        var s1 = new double[dists.Length][,]; var s2 = new double[dists.Length][,];
        for (int d = 0; d < dists.Length; d++) { s1[d] = new double[cells[d].Count, 3]; s2[d] = new double[cells[d].Count, 3]; }
        var ux = new float[grid.Count]; var uy = new float[grid.Count]; var uz = new float[grid.Count];
        int[] mp = [2, 4];   // momentum planes: 1 and 3 cells off the face
        var pm1 = new double[mp.Length, Ny * Nz]; var pm2 = new double[mp.Length, Ny * Nz];
        for (int t = 0; t < T; t++)
        {
            session.Step(1);
            session.ReadVelocity(ux, uy, uz);
            for (int m = 0; m < mp.Length; m++)
                for (int z = 1; z < Nz - 1; z++) for (int y = 1; y < Ny - 1; y++) { double u = ux[grid.Index(mp[m], y, z)]; pm1[m, z * Ny + y] += u; pm2[m, z * Ny + y] += u * u; }
            for (int d = 0; d < dists.Length; d++)
                for (int i = 0; i < cells[d].Count; i++)
                {
                    int c = cells[d][i]; double a = ux[c], b = uy[c], g = uz[c];
                    s1[d][i, 0] += a; s1[d][i, 1] += b; s1[d][i, 2] += g; s2[d][i, 0] += a * a; s2[d][i, 1] += b * b; s2[d][i, 2] += g * g;
                }
        }
        var tu = new float[dists.Length]; var mean = new float[dists.Length];
        for (int d = 0; d < dists.Length; d++)
        {
            double mu = 0, r2 = 0; int k = cells[d].Count;
            for (int i = 0; i < k; i++)
            {
                mu += s1[d][i, 0] / T;
                for (int c = 0; c < 3; c++) { double m = s1[d][i, c] / T; r2 += Math.Max(0, s2[d][i, c] / T - m * m) / k; }   // mean over cells of each component's variance
            }
            mu /= k;
            mean[d] = (float)mu; tu[d] = (float)(Math.Sqrt(r2 / 3) / mu);
        }
        // axial momentum flux across the duct section 1 and 3 cells off the face (m4/s2 per unit density): the mean flow's sum(mean(u)^2) and the total sum(mean(u^2))
        var momentum = new double[2 * mp.Length];
        for (int m = 0; m < mp.Length; m++)
            for (int i = 0; i < Ny * Nz; i++) { double mu = pm1[m, i] / T; momentum[m] += mu * mu; momentum[mp.Length + m] += pm2[m, i] / T; }
        return (tu, mean, session.SupplyFaceStats(), momentum);
    }

    /// <summary>The n x n supply patch in the closed-end duct of <see cref="RunDuct"/> and the model that drives it.</summary>
    static (VoxelGrid Grid, BuildingModel Model, float Flow) Duct(int n, bool linkWise, bool corner, float srcTu, bool sink)
    {
        var grid = new VoxelGrid { Nx = Nx, Ny = Ny, Nz = Nz, Dx = Dx, Origin = Vector3.Zero, Cell = new byte[Nx * Ny * Nz] };
        for (int z = 0; z < Nz; z++)
        for (int y = 0; y < Ny; y++)
        for (int x = 0; x < Nx; x++)
        {
            bool inside = x >= 1 && y >= 1 && y <= Ny - 2 && z >= 1 && z <= Nz - 2;   // x = Nx-1 stays open
            grid.Cell[grid.Index(x, y, z)] = inside ? VoxelGrid.Air : VoxelGrid.Solid;
        }
        // n x n patch, flush in the first air column of the closed end, centred on the duct axis (or tucked into
        // the corner against the side wall and the ceiling, where the wall eats a third of the edge links).
        int y0 = corner ? 1 : (Ny - n) / 2, z0 = corner ? Nz - 1 - n : (Nz - n) / 2;
        for (int z = z0; z < z0 + n; z++)
        for (int y = y0; y < y0 + n; y++)
        {
            int idx = grid.Index(1, y, z);
            grid.Cell[idx] = VoxelGrid.Fixed;
            grid.FixedCells.Add(sink ? new FixedCell(idx, new Vector3(-U0, 0, 0), 0, 1) : new FixedCell(idx, new Vector3(U0, 0, 0), linkWise ? 0 : -1));
        }
        float side = n * Dx, flow = U0 * side * side;
        var model = new BuildingModel
        {
            Name = "inlet", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = Nz * Dx, Analysed = true } },
            Sources = { new AirSource { Id = "supply", Name = "Supply", Level = "L1", Kind = sink ? SourceKind.Return : SourceKind.Supply, Device = DeviceKind.Patch,
                Center = new Vector3(1.5f * Dx, Ny * Dx / 2, Nz * Dx / 2), Direction = sink ? -Vector3.UnitX : Vector3.UnitX, Width = side, Height = side, FlowRate = flow,
                TurbulenceIntensity = srcTu >= 0f ? srcTu : null } },
        };
        return (grid, model, flow);
    }
}
