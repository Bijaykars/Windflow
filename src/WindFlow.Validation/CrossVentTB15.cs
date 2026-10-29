using System.Globalization;
using System.Numerics;
using WindFlow.Core;
using CoreMesh = WindFlow.Core.Mesh;

namespace WindFlow.Validation;

/// <summary>Wind-driven cross-ventilation of a generic isolated building (Tominaga &amp; Blocken 2015, Building and
/// Environment 92, 452-461, unsheltered case). Wind-tunnel model W x D x H = 0.20 x 0.20 x 0.16 m, one opening
/// 0.092 x 0.036 m centred in the windward and in the leeward facade (centre at z = 0.08 m), wind normal to the
/// openings, power-law approach flow u/UH = (z/H)^0.25, UH = 4.3 m/s (building Re ~ 45,000). Measured: split-fibre
/// mean u/UH on 7 verticals of the vertical centre section (Fig. 9a) and along the opening centreline (Fig. 7),
/// both digitised; tracer-gas flow rate Q/(UH A_inlet) = 0.25 +- 7 %.
/// <para>Built the way an architect's model runs: a BuildingModel of wall/roof meshes with two open Window openings,
/// a DomainBox on COST 732 / AIJ best-practice margins (default; task F2), the power-law wind inflow. Scale: the
/// opening is 0.036 m high, so at the 0.1 m Dx floor the model is built geometrically similar at H = N cells
/// (N = 20 default: H = 2.0 m, openings 4 cells high) and compared as u/UH against x/D, z/H. Sharp-edged, so
/// Reynolds-independent above a building Re of ~1.1e4 -- but see the Re_effective note: the solver's viscosity
/// floor sets it here. Flow rate checked against the opening-flux band 0.40-0.47 (Karava PIV to Hwang &amp; Gorle
/// 2022 LES, see SOURCE.md), not the 0.25 tracer-gas air-exchange value (kept as a separate metric).</para></summary>
public sealed class CrossVentTB15 : ValidationCase
{
    public override string Id => "cross-vent";
    public override string Title => "Cross-ventilation, generic isolated building (Tominaga-Blocken 2015)";
    public override string Citation => "Tominaga & Blocken 2015, Build. Environ. 92:452-461 (wind tunnel, split-fibre + tracer gas)";
    public override string Quantities => "u/UH on 7 verticals of the centre section (Fig. 9a); Q/(UH A) vs opening flux 0.40-0.47";

    // Model-scale geometry (Fig. 1) and results (Section 2.2, 4.1).
    const double Hm = 0.16, Wm = 0.20, OpenW = 0.092, OpenH = 0.036, AreaOverH2 = OpenW * OpenH / (Hm * Hm);
    // QMeasured: tracer-gas AIR EXCHANGE rate (Tominaga-Blocken 2015 Section 4.1) -- NOT the opening flux the
    // solver's Q_in_over_UH_A measures (u.n over the throat); kept as a separate, secondary metric.
    // QFluxLow/QFluxHigh: opening-flux Q/(UH A) references for the SAME configuration (Tominaga-Blocken 2015
    // Section 4.1: "This value [0.25] was 37% smaller than that for the same configuration reported in Karava
    // et al. [PIV]" -> Karava = 0.25/(1-0.37) = 0.397, rounded 0.40; that Karava PIV number is computed the same
    // way as our own metric, U_opening * A). QFluxHigh: Hwang & Gorle 2022 (arXiv 2204.00786) LES of the same
    // Karava geometry, Table 2 mean Qu,ins/(Uref A) over coarse/base/fine meshes = 0.470/0.452/0.462 ("close (3%)
    // agreement" with PIV); the corresponding pressure-based Qp,avg/(Uref A) = 0.445/0.440/0.445 with Cd = 0.61.
    // Geometry: Karava's 1:200 PIV model is the SAME plan/opening layout as this paper's unsheltered case
    // (Tominaga-Blocken built their 1:100 model "so that the results could be compared" with Karava, Section 2.1);
    // Hwang & Gorle's LES uses Karava's geometry directly (their Section 2).
    // R2-02: the measured flow-rate reference is NOT settled between the primary sources. Q/(UH A_inlet) = 0.25 in Tominaga &
    // Blocken 2015 Sect. 4.1 and in TB16 (JWEIA 155, Table 1, configuration E); van Hooff, Blocken & Tominaga 2017 (B&E 114,
    // Table 3, p.157) list "Experiments [65] 0.50", and [65] is TB15 itself. WindFlow's 0.454 is +82 % against 0.25, -9 % against
    // 0.50, inside the Karava/Hwang-Gorle opening-flux band 0.40-0.47. None is picked silently: VALIDATION.md and SOURCE.md show
    // all three; the authors have not been asked.
    const double QMeasured = 0.25, QMeasuredRel = 0.07, ReExp = 45000, QFluxLow = 0.40, QFluxHigh = 0.47;
    const float NuAir = 1.5e-5f;

    public override CaseSetup Build(string dataDir, Dictionary<string, string> args)
    {
        int n = args.TryGetValue("h-cells", out var hc) ? int.Parse(hc, CultureInfo.InvariantCulture) : 20;
        if (n < 20 || n % 20 != 0) throw new ArgumentException("--h-cells must be a multiple of 20 (20 = openings 4 cells high)");
        // Diagnostic variants (out/validation/cross-vent-diag/<name>/, kept out of the VALIDATION.md table):
        // --sealed 1 (openings shut: sealed-body Cp), --domain auto (Voxelizer.Build's own 4 H margin), --open-h <cells>.
        // Default domain is now "guide" (COST 732 / AIJ best-practice margins; see diagnosis.md: 4 H -> guideline
        // takes Q/(UH A) from 0.585 to 0.552 and blockage from 3.0% to 1.9%).
        string? diag = args.TryGetValue("diag", out var dg) ? dg : null;
        bool sealedBody = args.TryGetValue("sealed", out var sbs) && sbs == "1";
        string domainKind = args.TryGetValue("domain", out var dk) ? dk : "guide";
        if (domainKind is not ("auto" or "guide")) throw new ArgumentException("--domain must be auto or guide");
        if (diag == null && (sealedBody || domainKind != "guide" || args.ContainsKey("open-h")))
            throw new ArgumentException("--sealed/--domain/--open-h are diagnostic variants: give them a --diag <name>");
        const float d = 0.1f;                        // Dx: the Voxelizer floor, hit on purpose
        int nW = n * 5 / 4;                          // W = D = 1.25 H
        int oh = args.TryGetValue("open-h", out var ohs) ? int.Parse(ohs, CultureInfo.InvariantCulture)
                                                          : n / 5;   // opening height, cells (0.200 H; the model has 0.225 H)
        int ow = (int)Math.Round(AreaOverH2 * n * n / oh);   // width chosen to keep the opening AREA (0.129 H^2)
        if ((ow - nW) % 2 != 0) ow++;                // same parity as nW so the carve is symmetric about W/2
        float H = n * d, W = nW * d;
        // Opening centre: z = H/2 (a cell face). An odd opening height cannot straddle a face, so it moves up half a cell.
        float zc = H / 2 + (oh % 2 == 1 ? d / 2 : 0);
        float UH = (float)(ReExp * NuAir / H);        // nominal building Re = 45,000 (the solver's floor sets the real one)
        float U10 = UH * MathF.Pow(10f / H, 0.25f);   // RunOptions profile is u = U10 (z / 10 m)^a

        var model = MakeBuilding("Tominaga-Blocken 2015 cross-ventilated building (geometrically similar, H = " + n + " cells)",
            n, nW, ow, oh, zc, zc, sealedBody, d);

        // Domain. N = 20: Voxelizer.Build's own automatic margin (8 m = 4 H each side, sky to 9 m = 4.5 H), the
        // product path. Finer N: a DomainBox with the SAME margins in building units, so the grid study changes
        // only the resolution (the automatic margin is fixed in metres and would shrink to 2 H).
        var options = new RunOptions { Backend = Backend.Auto, WindProfileExponent = 0.25f };
        // --wind-tu measured: approach-flow turbulence from Tominaga & Blocken 2015 eq. 2 (Fig. 4), k/UH^2 = 0.033 exp(-0.32 z/H),
        // split into components by the standard ABL ratios sigma_u : sigma_v : sigma_w = 1 : 0.8 : 0.5 (ASSUMPTION: the paper gives
        // k only). Length scale L = H (ASSUMPTION: no eps or L is published). z below 0.25 H is held at its 0.25 H value
        // (the solver's mean profile floors there).
        bool windTu = args.TryGetValue("wind-tu", out var wtu) ? wtu == "measured" ? true
            : throw new ArgumentException("--wind-tu must be measured") : false;
        if (windTu)
        {
            options.WindTurbulence = MeasuredTurbulence(H);
        }
        float side = 8f * n / 20, top = 9f * n / 20;
        float ext = W + 2 * side;
        options.TargetCells = (int)Math.Min(int.MaxValue, 2.0 * ext * ext * top / (d * d * d));   // asks for < 0.1 m; clamps to 0.1
        bool guide = domainKind == "guide";
        bool auto = n == 20 && !guide;
        if (guide)
        {
            // Best-practice margins (Franke et al. 2007 COST 732; Tominaga et al. 2008 AIJ): 5 H upstream, 5 H to
            // the sides, top 5 H above the roof, 15 H downstream.
            BoxDomain(options, W, d, 5 * H, 5 * H, 15 * H, 6 * H);
        }
        else if (!auto)
        {
            var min = new Vector3(-side, -side, 0);
            options.Domain = new DomainBox(min, new Vector3(W + side - 0.01f * d, W + side - 0.01f * d, top - 0.01f * d));
        }

        var setup = new CaseSetup
        {
            Model = model, Options = options, Wind = new Vector2(U10, 0),   // wind from -X onto the windward facade
            TimeScale = H / UH,
            WarmupT = 40, BlockT = 5, MinBlocks = 8, MaxBlocks = 40, ConvergeLag = 3, ConvergeTol = 0.01,
            Quantity = v => v.X / UH,
            // Digitisation uncertainty ~0.025 UH (Fig. 7 vs Fig. 9a at the 7 common points); no measurement
            // uncertainty is published for the velocities. W = 0.05 UH: our choice, 2x the digitisation error.
            // R2-01 / R2-09: q depends strongly on it (recorded smirnov-windtu run: W 0 -> q 0.365, W 0.025 -> 0.540, W 0.05 -> 0.667,
            // the pass line is 41.6 of 63 hits and W 0.05 gives 42); the facility quotes about +-10 % velocity uncertainty
            // (Tominaga & Blocken 2016 Sect. 2.2, same probe), which is not an absolute W.
            W = 0.05,
            WNote = "W = 0.05 (u/UH): chosen, not published (2x the ~0.025 UH digitisation uncertainty of Fig. 7 vs Fig. 9a; the facility quotes about 10 % velocity uncertainty, TB16 Sect. 2.2). q depends strongly on it: on the recorded run W 0 gives q 0.365, W 0.025 gives 0.540, W 0.05 gives 0.667 against the 0.66 line (42 of 63 hits, 41.6 needed)",
        };
        if (diag != null) { setup.OutSuffix = $"-diag/{diag}"; setup.TitleSuffix = $" [diagnostic {diag}]"; }
        else if (n != 20) { setup.OutSuffix = $"-h{n}"; setup.TitleSuffix = $" [grid study: H = {n} cells]"; }
        if (windTu) { setup.OutSuffix += "-windtu"; setup.TitleSuffix += " [measured approach-flow turbulence, synthetic inflow, L = H assumed]"; }
        // RZ-03: a wind-tunnel building case runs the product's wind path, so its box gets the product's outer ring by default;
        // --ring box keeps the user-box ring (the pre-6b equilibrium ring) for comparison.
        string ring = args.TryGetValue("ring", out var rg) ? rg : "product";
        if (ring is not ("product" or "box")) throw new ArgumentException("--ring must be product or box");
        setup.ProductRing = ring == "product";
        if (!setup.ProductRing) { setup.OutSuffix += "-boxring"; setup.TitleSuffix += " [user-box ring]"; }
        setup.SliceY = W / 2; setup.SliceMin = new(-1.5f * H, 0); setup.SliceMax = new(W + 2.5f * H, 2.5f * H);
        setup.SliceScale = UH; setup.SliceMax01 = 1.2f; setup.SliceUnit = "UH";
        setup.SliceTitle = "Cross-ventilation: centre-section time-averaged flow, wind from the left";

        var info = setup.Info;
        info["scale"] = $"geometrically similar: H = {n} cells of {d} m = {H:0.##} m (model 0.16 m, x{H / Hm:0.#}); compared as u/UH vs x/D, z/H";
        info["H_m"] = H; info["W_m"] = W; info["D_m"] = W;
        info["opening_cells"] = $"{ow} wide x {oh} high (model 0.092 x 0.036 m = {OpenW / Wm * nW:0.#} x {OpenH / Hm * n:0.##} cells)";
        info["opening_area_over_H2"] = (double)ow * oh / (n * n); info["opening_area_over_H2_model"] = AreaOverH2;
        info["opening_h_over_H"] = (double)oh / n; info["opening_w_over_W"] = (double)ow / nW;
        info["wall_thickness_over_H"] = 1.0 / n;
        info["UH_mps"] = (double)UH; info["U10_mps"] = (double)U10; info["profile"] = "power law a = 0.25 (Tominaga-Blocken eq. 1), U10 = UH (10 m / H)^0.25";
        info["Re_nominal"] = ReExp;
        info["domain"] = guide ? "DomainBox, best-practice margins (COST 732 / AIJ): 5 H upstream, 15 H downstream, 5 H lateral, top 5 H above the roof"
                       : auto ? "Voxelizer.Build automatic whole-building margin: 8 m = 4 H upstream/downstream/lateral, top at 9 m = 4.5 H"
                              : $"DomainBox with the N = 20 margins in building units: {side} m = 4 H each side, top at {top} m = 4.5 H";
        info["diag_domain"] = domainKind; info["diag_sealed"] = sealedBody; info["h_cells"] = n; info["open_h_cells"] = oh; info["open_w_cells"] = ow; info["open_centre_z_over_H"] = zc / H;

        int expectRoom = (nW - 2) * (nW - 2) * (n - 1);
        // Pressure-diagnostic cell sets, filled once the grid exists (InspectGrid runs before any block scalar).
        List<int> roomCells = new(), refCells = new(), inflowCells = new();
        var facade = new Dictionary<string, List<int>>();
        setup.InspectGrid = (g, inf) =>
        {
            int room = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z);
                if (c.X > d && c.X < W - d && c.Y > d && c.Y < W - d && c.Z > 0 && c.Z < H - d && g.Cell[g.Index(x, y, z)] != VoxelGrid.Solid)
                { room++; roomCells.Add(g.Index(x, y, z)); }
            }
            // Air cells of the x-layer containing world x whose centres satisfy |y - yc| <= hy and |z - zc| <= hz.
            List<int> Layer(float wx, float yc, float hy, float zc, float hz)
            {
                var l = new List<int>();
                int ix = (int)MathF.Floor((wx - g.Origin.X) / g.Dx);
                for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++)
                {
                    var c = g.CellCenter(ix, y, z);
                    int i = g.Index(ix, y, z);
                    if (g.Cell[i] != VoxelGrid.Solid && MathF.Abs(c.Y - yc) <= hy && MathF.Abs(c.Z - zc) <= hz) l.Add(i);
                }
                return l;
            }
            float tol = 1e-3f * d, hc = 0.5f * d + tol;   // "centre": the 1-4 cells touching the opening centre
            foreach (var (key, x0, x1) in new[] { ("ww", -0.5f * d, -1.5f * d), ("lw", W + 0.5f * d, W + 1.5f * d) })
            {
                facade[key + "_centre"] = Layer(x0, W / 2, hc, zc, hc).Concat(Layer(x1, W / 2, hc, zc, hc)).ToList();
                // Patch: the opening's own footprint, 1 and 2 cells outside the facade.
                facade[key + "_patch"] = Layer(x0, W / 2, ow * d / 2 - tol, zc, oh * d / 2 - tol).Concat(Layer(x1, W / 2, ow * d / 2 - tol, zc, oh * d / 2 - tol)).ToList();
            }
            // Approach static pressure: 5 x 5 cells at x = -3 H, centred on y = W/2, z = H. Also the same window
            // 2 cells in from the inflow face, to show how much the reference itself depends on where it is read.
            refCells.AddRange(Layer(-3 * H, W / 2, 2.5f * d, H, 2.5f * d));
            inflowCells.AddRange(Layer(g.Origin.X + 2.5f * d, W / 2, 2.5f * d, H, 2.5f * d));
            inf["pressure_cells"] = $"room {roomCells.Count}, ref {refCells.Count}, inflow {inflowCells.Count}, " +
                string.Join(", ", facade.Select(kv => $"{kv.Key} {kv.Value.Count}"));
            Console.WriteLine($"  pressure cells: {inf["pressure_cells"]}");
            if (refCells.Count == 0 || facade.Values.Any(v => v.Count == 0)) throw new InvalidOperationException("empty pressure-diagnostic cell set");
            int open0 = Analysis.ThroatCells(g, model.Openings[0]).Count, open1 = Analysis.ThroatCells(g, model.Openings[1]).Count;
            inf["grid"] = $"{g.Nx} x {g.Ny} x {g.Nz}"; inf["cells"] = g.Count; inf["Dx_m"] = g.Dx;
            inf["origin"] = $"{g.Origin.X:0.###}, {g.Origin.Y:0.###}, {g.Origin.Z:0.###}";
            inf["room_air_cells"] = room; inf["windward_throat_cells"] = open0; inf["leeward_throat_cells"] = open1;
            inf["blockage"] = (double)W * H / ((g.Ny - 2) * g.Dx * (g.Nz - 1) * g.Dx);
            // lattice_nu_molecular / lattice_nu_used / Re_effective_before_LES are filled in CheckField, once the
            // actual LbmSession lattice parameters (Runner.Run writes solver_dt_s / solver_nu_lat_used /
            // solver_velocity_scale into Info right after LbmSolver.Start) are known.
            int expectThroat = sealedBody ? 0 : ow * oh;
            Console.WriteLine($"  room air {room:N0} (expect {expectRoom:N0}), throat cells {open0} / {open1} (expect {expectThroat}), origin {inf["origin"]}");
            if (Math.Abs(g.Dx - d) > 1e-6f) throw new InvalidOperationException($"Dx = {g.Dx}, expected {d}");
            if (room != expectRoom || open0 != expectThroat || open1 != expectThroat)
                throw new InvalidOperationException("grid does not match the intended geometry (see the counts above)");
        };

        // Ventilation flow: u.n over each opening's carved throat (Analysis.ThroatCells, the product's opening flow).
        double aDrawn = ow * oh * d * d;
        double Flow(FlowField f, Opening o)
        {
            double q = 0;
            foreach (int i in Analysis.ThroatCells(f.Grid, o)) q += f.Ux[i];
            return q * d * d;
        }
        setup.BlockScalars.Add(("Q_in_over_UH_A", f => Flow(f, model.Openings[0]) / (UH * aDrawn)));
        setup.BlockScalars.Add(("Q_out_over_UH_A", f => Flow(f, model.Openings[1]) / (UH * aDrawn)));
        // Mean vertical velocity over the windward throat cells (the facade downwash through the opening), w/UH.
        setup.BlockScalars.Add(("w_over_UH_ww_throat", f => { var t = Analysis.ThroatCells(f.Grid, model.Openings[0]); double s = 0; foreach (int i in t) s += f.Uz[i]; return s / Math.Max(1, t.Count) / UH; }));
        // Approach flow 3 H upstream of the windward facade (about 1 H downstream of the inflow face): how the
        // imposed profile has developed over the smooth ground (the measured UH is at the empty turntable).
        foreach (float zh in new[] { 0.5f, 1f, 2f })
            setup.BlockScalars.Add(($"approach_u_over_UH_z{zh:0.#}H", f => Sampler.Velocity(f, new(-3 * H, W / 2, zh * H)).X / UH));
        // Pressure coefficients: (P - P_ref) / (0.5 rho UH^2), rho = 1.2 (FlowField.P's own density), P_ref the
        // approach static pressure (refCells). Linear in P, so the block mean of Cp is Cp of the block-mean field.
        double qRef = 0.5 * 1.2 * UH * UH;
        static double Mean(FlowField f, List<int> l) { double s = 0; foreach (int i in l) s += f.P[i]; return s / l.Count; }
        double Cp(FlowField f, List<int> l) => (Mean(f, l) - Mean(f, refCells)) / qRef;
        foreach (var key in new[] { "ww_centre", "ww_patch", "lw_centre", "lw_patch" })
            setup.BlockScalars.Add(($"Cp_{key}", f => Cp(f, facade[key])));
        setup.BlockScalars.Add(("Cp_room", f => Cp(f, roomCells)));
        setup.BlockScalars.Add(("Cp_inflow_face", f => Cp(f, inflowCells)));
        // Derived from the block means: each opening's pressure drop (outside patch vs room mean) and the Cd it
        // implies, Q = Cd A sqrt(2 dp / rho)  <=>  Q/(UH A) = Cd sqrt(dCp); in series (equal areas) the two drops
        // add, Q/(UH A) = Cd_series sqrt(dCp_total / 2).
        setup.CheckField = (_, inf) =>
        {
            double V(string k) => (double)inf[k];
            // Effective lattice Reynolds number from the LbmSession's own reported lattice parameters (Runner.Run
            // wrote solver_dt_s / solver_nu_lat_used / solver_velocity_scale into Info after LbmSolver.Start).
            // VelocityScale is lattice units per m/s (a single dt/dx conversion factor for the whole flow), so
            // UH's lattice velocity is UH * VelocityScale regardless of where in the profile it is evaluated.
            double nuLatUsed = V("solver_nu_lat_used");
            inf["lattice_nu_molecular"] = NuAir * V("solver_dt_s") / (d * d);
            inf["lattice_nu_used"] = nuLatUsed;
            inf["Re_effective_before_LES"] = UH * V("solver_velocity_scale") * n / nuLatUsed;
            double qi = V("Q_in_over_UH_A"), qo = Math.Abs(V("Q_out_over_UH_A"));
            double dIn = V("Cp_ww_patch") - V("Cp_room"), dOut = V("Cp_room") - V("Cp_lw_patch"), dTot = V("Cp_ww_patch") - V("Cp_lw_patch");
            inf["dCp_centre"] = V("Cp_ww_centre") - V("Cp_lw_centre");
            inf["dCp_patch"] = dTot; inf["dCp_windward_opening"] = dIn; inf["dCp_leeward_opening"] = dOut;
            inf["Cd_windward"] = sealedBody || dIn <= 0 ? double.NaN : qi / Math.Sqrt(dIn);
            inf["Cd_leeward"] = sealedBody || dOut <= 0 ? double.NaN : qo / Math.Sqrt(dOut);
            inf["Cd_series"] = sealedBody || dTot <= 0 ? double.NaN : qi / Math.Sqrt(dTot / 2);
            Console.WriteLine($"  Cp ww centre/patch {V("Cp_ww_centre"):0.000}/{V("Cp_ww_patch"):0.000}, lw {V("Cp_lw_centre"):0.000}/{V("Cp_lw_patch"):0.000}, " +
                $"room {V("Cp_room"):0.000}, inflow face {V("Cp_inflow_face"):0.000}; Cd in/out/series {inf["Cd_windward"]:0.000}/{inf["Cd_leeward"]:0.000}/{inf["Cd_series"]:0.000}; Q/(UH A) {qi:0.000}");
        };
        setup.FlowSummary = inf =>
        {
            // Primary check: opening flux Q/(UH A) against the Karava PIV (0.40) - Hwang & Gorle 2022 LES (0.47)
            // band for the same geometry, +-10% (see the QFluxLow/QFluxHigh comment above Build). The 0.25
            // tracer-gas value measures air exchange, a different quantity (net through-flow with recirculation
            // removed), and is reported separately, not used for pass/fail.
            double q = (double)inf["Q_in_over_UH_A"];
            double bandLo = QFluxLow * 0.90, bandHi = QFluxHigh * 1.10;
            bool within = q >= bandLo && q <= bandHi;
            double nearest = q < QFluxLow ? QFluxLow : q > QFluxHigh ? QFluxHigh : q;
            double err = q / nearest - 1;
            double errTracer = q / QMeasured - 1;
            return $"Q/(UH A) {q:0.000} (+-{(double)inf["Q_in_over_UH_A_stderr"]:0.000}) vs opening-flux references {QFluxLow:0.00} " +
                   $"(Karava PIV) - {QFluxHigh:0.00} (Hwang & Gorle 2022 LES) +-10% [{bandLo:0.00}, {bandHi:0.00}]: " +
                   $"{err * 100:+0;-0}% from the nearer bound -> {(within ? "within" : "outside")}. " +
                   $"Air exchange (tracer, a different quantity, Tominaga-Blocken 2015 Section 4.1): {q:0.000} vs {QMeasured} " +
                   $"+-{QMeasuredRel * 100:0}% ({errTracer * 100:+0;-0}%, not scored)";
        };
        setup.Note = run =>
        {
            var r = run; var st = setup.Info;
            string G(string k, string fmt = "0.###") => st.TryGetValue(k, out var v) && v is double x ? x.ToString(fmt, CultureInfo.InvariantCulture) : "?";
            return $"- **{Title}{setup.TitleSuffix}** (run {DateTime.Now:yyyy-MM-dd HH:mm}, {r.Backend}): {st["scale"]}. Openings {st["opening_cells"]}: " +
                $"area/H^2 {G("opening_area_over_H2", "0.0000")} (model {AreaOverH2:0.0000}), h/H {G("opening_h_over_H")} (model 0.225), walls 1 cell = {G("wall_thickness_over_H")} H. " +
                $"Grid {st["grid"]}, {st["domain"]}, blockage {G("blockage", "0.0%")}. Wind: power law a = 0.25 with UH = {G("UH_mps")} m/s at z = H " +
                $"(nominal Re 45,000); the solver floor (NuLatMin) makes the effective Re about {G("Re_effective_before_LES", "0")} before the Smagorinsky term, " +
                $"below the ~1.1e4 Reynolds-independence threshold; {(windTu ? "the inflow carries synthetic turbulence from the measured k/UH^2 (~0.03 at z = H; L = H assumed)" : "the inflow is steady (no turbulence; measured k/UH^2 ~0.03 at z = H)")} and the ground is smooth. " +
                $"Approach flow 3 H upstream, u/UH at z = 0.5/1/2 H: {G("approach_u_over_UH_z0.5H", "0.00")}/{G("approach_u_over_UH_z1H", "0.00")}/{G("approach_u_over_UH_z2H", "0.00")} " +
                $"(eq. 1: 0.84/1.00/1.19). Flow: Q_in/(UH A) {G("Q_in_over_UH_A")}, Q_out {G("Q_out_over_UH_A")} (A = drawn opening area). " +
                $"Run: warm-up {setup.WarmupT} H/UH, {r.Blocks} blocks of {setup.BlockT} H/UH ({r.SimSeconds / setup.TimeScale:0} H/UH, {r.TotalSteps} steps), " +
                $"{(r.Converged ? "converged" : "NOT converged")} (RMS change of the cumulative mean over the last {setup.ConvergeLag} blocks < {setup.ConvergeTol} UH); " +
                $"largest block-mean standard error {r.MaxStdErr:0.###} UH. Measured values are digitised (see SOURCE.md). {setup.WNote}. Plots: `out/validation/{Id}{setup.OutSuffix}/*.png`.";
        };

        // Measured lines (digitised, see SOURCE.md). World: x = (x/D) W from the windward face, y = W/2, z = (z/H) H.
        var rows = File.ReadAllLines(Path.Combine(dataDir, "measured.csv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("fig,"))
            .Select(l => l.Split(','))
            .Select(p => (fig: p[0], x: double.Parse(p[1], CultureInfo.InvariantCulture), z: double.Parse(p[2], CultureInfo.InvariantCulture), u: double.Parse(p[3], CultureInfo.InvariantCulture)))
            .ToList();
        float yc = W / 2;
        const double bar = 0.025;
        const string src = "measured (Tominaga-Blocken 2015, digitised)";
        foreach (var grp in rows.Where(r => r.fig == "9a").GroupBy(r => r.x).OrderBy(g => g.Key))
        {
            double xD = grp.Key;
            var l = new ProfileLine { Name = $"x/D={xD:0.###}", Title = $"Vertical profile x/D = {xD:0.###} (centre section)", Vertical = true,
                PosLabel = "z/H", ValueLabel = "u / UH", BarLabel = "bars: digitisation uncertainty +-0.025 UH (no measurement uncertainty published)", MeasuredLabel = src };
            foreach (var r in grp.OrderBy(r => r.z)) l.Measured.Add(new(r.z, new((float)(xD * W), yc, (float)(r.z * H)), r.u, bar));
            for (int k = 0; k <= 200; k++) { double zz = k / 200.0; l.Dense.Add((zz, new((float)(xD * W), yc, (float)(zz * H)))); }
            setup.Lines.Add(l);
        }
        var cl = new ProfileLine { Name = "centreline", Title = "Centreline z = H/2 through both openings (Fig. 7; same points as Fig. 9a at z/H = 0.5)",
            Vertical = false, InOverall = false, PosLabel = "x/D (windward facade at 0, leeward at 1)", ValueLabel = "u / UH",
            BarLabel = "bars: digitisation uncertainty +-0.025 UH; this line is not in the overall metrics", MeasuredLabel = src + ", Fig. 7" };
        foreach (var r in rows.Where(r => r.fig == "7").OrderBy(r => r.x)) cl.Measured.Add(new(r.x, new((float)(r.x * W), yc, H / 2), r.u, bar));
        for (int k = 0; k <= 300; k++) { double xx = -0.75 + 2.5 * k / 300; cl.Dense.Add((xx, new((float)(xx * W), yc, H / 2))); }
        setup.Lines.Add(cl);
        if (setup.Lines.Count != 8 || setup.Lines.Take(7).Any(l => l.Measured.Count != 9) || cl.Measured.Count != 7)
            throw new InvalidOperationException("measured.csv: expected 7 x 9 Fig. 9a points and 7 Fig. 7 points");
        return setup;
    }

    /// <summary>The one-cell-wall isolated building of both TB15 and TB16 (shared with <see cref="TracerTB16"/>): walls and roof one cell thick, two
    /// open windows (windward at x = Dx/2, leeward at x = W - Dx/2) of ow x oh cells centred at heights zcIn / zcOut, wind along +X.</summary>
    internal static BuildingModel MakeBuilding(string name, int n, int nW, int ow, int oh, float zcIn, float zcOut, bool sealedBody, float d)
    {
        float H = n * d, W = nW * d, e = 0.05f * d;
        // One-cell walls and roof: box faces 0.05 Dx inside the cell they occupy, so each face marks that cell only.
        var obstacles = new List<CoreMesh>
        {
            Box("wall-windward", new(e, e, e), new(d - e, W - e, H - e)),
            Box("wall-leeward", new(W - d + e, e, e), new(W - e, W - e, H - e)),
            Box("wall-south", new(e, e, e), new(W - e, d - e, H - e)),
            Box("wall-north", new(e, W - d + e, e), new(W - e, W - e, H - e)),
            Box("roof", new(e, e, H - d + e), new(W - e, W - e, H - e)),
            // Base plate: lies entirely inside the solid ground layer (z in -Dx..0), so it adds no solid; its
            // corners put PlanBounds' minimum at exactly (0, 0), which aligns the building to the cell faces.
            Box("base-plate", new(0, 0, -d + e), new(W, W, -e)),
        };
        Opening Win(string id, float x, float zc, Vector3 normal) => new()
        {
            Id = id, Name = id, Level = "L0", Kind = OpeningKind.Window, IsOpen = !sealedBody,
            Center = new(x, W / 2, zc), Normal = normal, Width = ow * d, Height = oh * d, Depth = d,
        };
        return new BuildingModel
        {
            Name = name,
            GroundZ = 0,
            Storeys = { new Storey { Id = "L0", Name = "Room", Elevation = 0, Height = H, Analysed = true } },
            Obstacles = obstacles,
            Rooms = { new Room { Id = "room", Name = "Test room", Level = "L0", ZMin = 0, ZMax = H - d,
                Loops = { new Vector2[] { new(d, d), new(W - d, d), new(W - d, W - d), new(d, W - d) } } } },
            Openings = { Win("windward", d / 2, zcIn, -Vector3.UnitX), Win("leeward", W - d / 2, zcOut, Vector3.UnitX) },
        };
    }

    /// <summary>--wind-tu measured: approach-flow turbulence from Tominaga &amp; Blocken 2015 eq. 2 (Fig. 4), k/UH^2 = 0.033 exp(-0.32 z/H), split by the
    /// ABL ratios sigma_u : sigma_v : sigma_w = 1 : 0.8 : 0.5 (ASSUMPTION: the paper gives k only), L = H (ASSUMPTION), z below 0.25 H held at its 0.25 H value.</summary>
    internal static WindTurbulence MeasuredTurbulence(float H)
    {
        const float rv = 0.8f, rw = 0.5f;
        return new WindTurbulence
        {
            LengthScale = H, TimeRefHeight = H, IvRatio = rv, IwRatio = rw,
            Iu = z =>
            {
                float zh = MathF.Max(z / H, 0.25f);
                float k = 0.033f * MathF.Exp(-0.32f * zh);
                return MathF.Sqrt(2f * k / (1f + rv * rv + rw * rw)) / MathF.Pow(zh, 0.25f);
            },
        };
    }

    /// <summary>A DomainBox with the building at plan (0..W, 0..W): up / lat / down / sky margins (metres) and the matching target cell count.</summary>
    internal static void BoxDomain(RunOptions options, float W, float d, float up, float lat, float down, float sky)
    {
        options.Domain = new DomainBox(new Vector3(-up, -lat, 0), new Vector3(W + down - 0.01f * d, W + lat - 0.01f * d, sky - 0.01f * d));
        options.TargetCells = (int)Math.Min(int.MaxValue, 2.0 * (W + up + down) * (W + 2 * lat) * sky / (d * d * d));
    }

    static CoreMesh Box(string id, Vector3 a, Vector3 b)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        int[] idx =
        {
            0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,
            0, 1, 4, 1, 5, 4,   2, 6, 3, 3, 6, 7,
            0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,
        };
        return new CoreMesh { Id = id, Vertices = v, Indices = idx };
    }
}
