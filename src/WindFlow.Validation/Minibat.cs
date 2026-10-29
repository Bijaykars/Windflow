using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindFlow.Core;
using CoreMesh = WindFlow.Core.Mesh;
using Vec3 = System.Numerics.Vector3;
using Vec2 = System.Numerics.Vector2;
using WPoint = System.Windows.Point;

namespace WindFlow.Validation;

/// <summary>MINIBAT full-scale test room, isothermal case (Kuznik, Rusaouen &amp; Brau 2008, Build. Simul. 1:72-82;
/// data: Gresse, Merlier &amp; Kuznik 2020, Data in Brief 29:105382, Mendeley doi:10.17632/hp2cx64vw6, CC BY 4.0).
/// Room 3.10 x 3.10 x 2.50 m; a round 0.12 m nozzle centred at (1.55, 0.57, 2.32) m blows an axisymmetric jet along +y
/// under the ceiling from a housing on the south facade (y = 0); U0 = 1.67 m/s (68 m3/h), Re 13,360; extraction box on
/// the floor. Measured: three-hot-wire mean velocity on the median plane M1 (x = 1.55 m) and the transversal planes
/// T1-T4 (y = 0.60/0.90/1.20/1.50 m), all in the jet zone z = 2.04-2.47 m. Compared as |U|/U0 at every position whose
/// measured |U| is at least the probe's 0.1 m/s floor.
/// <para>Built at full scale through the product path: wall/ceiling/housing meshes, a Room, the supply as a Patch
/// AirSource (square, --nozzle-cells N cells of 0.1/N m, so always 0.10 x 0.10 m; momentum flux matched to the round
/// nozzle), the outlet as an open Opening, and RunOptions.Domain + RunOptions.MinDx for a Dx below the 0.1 m floor.</para></summary>
public sealed class Minibat : ValidationCase
{
    public override string Id => "minibat-iso";
    public override string Title => "MINIBAT isothermal ceiling jet (Kuznik 2008)";
    public override string Citation => "Kuznik, Rusaouen & Brau 2008, Build. Simul. 1:72-82; data Gresse, Merlier & Kuznik 2020, Data in Brief 29:105382 (hot-wire)";
    public override string Quantities => "speed/U0 on M1 (x=1.55 m) and T1-T4 (y=0.60-1.50 m), jet zone";

    // Geometry and conditions. Sources (see data/validation/minibat-iso/SOURCE.md): K08 = Kuznik 2008, DiB = Gresse 2020.
    const float Lx = 3.10f, Ly = 3.10f, H = 2.50f;              // K08 p.73, DiB p.3
    const float X0 = 1.55f, Y0 = 0.57f, Z0 = 2.32f, Dn = 0.12f;  // nozzle centre + exit plane, diameter: K08 Fig. 2, DiB Fig. 2
    const float U0 = 1.67f;                                      // DiB Table 3; = 68 m3/h (K08 Table 1) through the 0.12 m nozzle
    const double QMeasured = 68.0 / 3600;                        // m3/s, K08 Table 1 (flowmeters +-0.5 m3/h, DiB p.5)
    // Housing around the diffuser (box on the south facade under the ceiling): NOT dimensioned in either paper. Read off
    // the DiB Fig. 1 render: about 1.0 m wide centred on the nozzle, down to z ~2.1 m, front face = the nozzle exit plane.
    const float HousX0 = 1.05f, HousX1 = 2.05f, HousZ0 = 2.10f;
    // Extraction box 0.20 (x) x 0.25 (y) x 0.10 m (DiB Fig. 3) on the floor against the y = 3.10 m wall; its x position
    // is read off DiB Fig. 1 / K08 Fig. 2 (about x = 1.9 m). Open face (0.20 x 0.10) faces the room (-y).
    const float OutX0 = 1.80f, OutX1 = 2.00f, OutDy = 0.25f, OutH = 0.10f;
    const float Nu = 1.5e-5f;                                   // Re = U0 D / nu = 13,360 (K08 Table 1)

    public override CaseSetup Build(string dataDir, Dictionary<string, string> args)
    {
        int n = args.TryGetValue("nozzle-cells", out var nc) ? int.Parse(nc, CultureInfo.InvariantCulture) : 4;
        if (n < 4 || n > 8) throw new ArgumentException("--nozzle-cells must be 4 to 8 (Dx = 0.1/N m: 25, 20, 16.7, 14.3, 12.5 mm)");
        // Measured points (measured.csv, converted from the xlsx by convert.py; see SOURCE.md).
        var all = File.ReadAllLines(Path.Combine(dataDir, "measured.csv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("plane,"))
            .Select(l => l.Split(','))
            .Select(p => new Pt(p[0], new Vec3(F(p[1]), F(p[2]), F(p[3])), MathF.Sqrt(F(p[4]) * F(p[4]) + F(p[5]) * F(p[5]) + F(p[6]) * F(p[6])) / U0, p[13], F(p[5])))
            .ToList();
        var stat = CountStatuses(all.Select(p => p.Status));
        if (all.Count != 2463 || stat != (1419, 515, 529))
            throw new InvalidOperationException($"measured.csv: expected 2463 rows = 1419 ok + 515 below0.1 + 529 masked, got {all.Count} = {stat.Ok} + {stat.Below} + {stat.Masked} (re-run convert.py?)");
        // --inlet measured (default, a SQUARE 0.10 x 0.10 m patch): the patch carries the jet momentum flux measured
        // on T1, 3 cm past the exit (sum of vy|vy| dA over the 2 x 2 cm cells, masked cells = 0) -- K08 p.77 imposed
        // measured inlet values too. --inlet table (diagnostic, same square patch): the tabulated U0 = 1.67 m/s
        // through the 0.12 m nozzle (68 m3/h). The two disagree by 2.3x in momentum flux: the measured exit core is
        // ~3.0 m/s (see SOURCE.md, "Data issue"). --inlet disk-table / disk-momentum (task F2, diagnostic, NOT the
        // default): a staircase DISK of D = 0.12 m (the true nozzle diameter, not a square) on the grid, same two
        // target choices -- see SOURCE.md "MINIBAT inlet: disk option" for the geometry this approximates.
        string inlet = args.TryGetValue("inlet", out var il) ? il : "measured";
        if (inlet is not ("measured" or "table" or "disk-table" or "disk-momentum"))
            throw new ArgumentException("--inlet measured|table|disk-table|disk-momentum");
        bool disk = inlet.StartsWith("disk", StringComparison.Ordinal);
        double mT1 = all.Where(p => p.Plane == "T1").Sum(p => (double)p.Vy * Math.Abs(p.Vy)) * 0.02 * 0.02;
        double qT1 = all.Where(p => p.Plane == "T1").Sum(p => (double)p.Vy) * 0.02 * 0.02;
        float d = 0.1f / n;                                      // Dx: 3.10 and 2.50 m are whole numbers of cells
        int C(float v) => (int)MathF.Round(v / d);               // metres -> nearest cell face index
        int nL = C(Lx), nH = C(H);
        const int tw = 2;                                        // wall thickness, cells
        int nP = C(0.30f);                                       // outdoor plenum past the extract wall, cells
        float e = 0.05f * d, o = tw * d - e;
        float yEnd = (nL + tw + nP + 1) * d;                     // past the domain box: rasteriser clamps

        // Nozzle: --inlet measured/table build a square Patch of n x n cells (0.10 x 0.10 m) centred on (X0, Z0)
        // snapped to cells, in the first air cell in front of the housing face. Momentum flux matched to the round
        // nozzle (U^2 A kept): the far jet decays with sqrt(momentum flux), so this keeps the measured decay
        // comparable in u/U0. --inlet disk-* build a candidate square of side Dn (0.12 m) here; InspectGrid below
        // trims it to a staircase disk of diameter Dn once the grid exists, and corrects FlowRate to the exact
        // target on the disk's actual (rasterised) area -- see the InspectGrid comment.
        float side = disk ? Dn : n * d, aNoz = MathF.PI * Dn * Dn / 4, aPatch = side * side;
        float uSet, qSet;
        switch (inlet)
        {
            case "table": uSet = U0 * MathF.Sqrt(aNoz / aPatch); qSet = uSet * aPatch; break;
            case "disk-table": qSet = (float)QMeasured; uSet = qSet / aNoz; break;                    // candidate; InspectGrid refines
            case "disk-momentum": uSet = (float)Math.Sqrt(mT1 / aNoz); qSet = uSet * aNoz; break;      // candidate; InspectGrid refines
            default: uSet = (float)Math.Sqrt(mT1 / aPatch); qSet = uSet * aPatch; break;               // "measured"
        }
        int hx0 = C(HousX0), hx1 = C(HousX1), hy1 = C(Y0), hz0 = C(HousZ0);
        int nzx0 = C(X0 - side / 2), nzz0 = C(Z0 - side / 2);
        var nozzleC = disk ? new Vec3(X0, (hy1 + 0.5f) * d, Z0) : new Vec3((nzx0 + n / 2f) * d, (hy1 + 0.5f) * d, (nzz0 + n / 2f) * d);
        int ox0 = C(OutX0), ox1 = C(OutX1), oy0 = C(Ly - OutDy), ozh = C(OutH);

        var obstacles = new List<CoreMesh>
        {
            Box("wall-south", new(-o, -o, 0), new(Lx + o, -e, H + o)),
            Box("wall-north", new(-o, Ly + e, 0), new(Lx + o, Ly + o, H + o)),
            Box("wall-east", new(-o, -o, 0), new(-e, yEnd, H + o)),          // x = 0 (east, K08/DiB "east facade 1.55 m")
            Box("wall-west", new(Lx + e, -o, 0), new(Lx + o, yEnd, H + o)),
            Box("ceiling", new(-o, -o, H + e), new(Lx + o, yEnd, H + o)),
        };
        // Housing: one slab per cell layer (a thick box's interior would stay air -- the rasteriser marks faces only).
        for (int k = hz0; k < nH; k++)
            obstacles.Add(Box($"housing-{k}", new(hx0 * d + e, e, k * d + e), new(hx1 * d - e, hy1 * d - e, (k + 1) * d - e)));
        // Extraction box: 1-cell lid above the 0.10 m opening and 1-cell side walls, open to the room at y = 2.85 m and
        // to the extract wall opening behind it.
        obstacles.Add(Box("extract-lid", new((ox0 - 1) * d + e, oy0 * d + e, ozh * d + e), new((ox1 + 1) * d - e, Ly - e, (ozh + 1) * d - e)));
        obstacles.Add(Box("extract-side-e", new((ox0 - 1) * d + e, oy0 * d + e, e), new(ox0 * d - e, Ly - e, (ozh + 1) * d - e)));
        obstacles.Add(Box("extract-side-w", new(ox1 * d + e, oy0 * d + e, e), new((ox1 + 1) * d - e, Ly - e, (ozh + 1) * d - e)));

        var outlet = new Opening
        {
            Id = "extract", Name = "Extraction (box opening through the wall)", Level = "L0", Kind = OpeningKind.Opening, IsOpen = true,
            Center = new((ox0 + ox1) * d / 2, Ly + tw * d / 2, ozh * d / 2), Normal = Vec3.UnitY,
            Width = (ox1 - ox0) * d, Height = ozh * d, Depth = tw * d,
        };
        var nozzleSource = new AirSource { Id = "nozzle", Name = disk ? "Axisymmetric nozzle (staircase disk)" : "Axisymmetric nozzle (square patch)",
            Level = "L0", Kind = SourceKind.Supply, Device = DeviceKind.Patch, Center = nozzleC, Direction = Vec3.UnitY, Width = side, Height = side, FlowRate = qSet };
        var model = new BuildingModel
        {
            Name = "MINIBAT test cell (CETHIL, INSA Lyon)",
            GroundZ = 0,
            Storeys = { new Storey { Id = "L0", Name = "Cell", Elevation = 0, Height = H, Analysed = true } },
            Obstacles = obstacles,
            Rooms = { new Room { Id = "room", Name = "MINIBAT cell", Level = "L0", ZMin = 0, ZMax = H,
                Loops = { new Vec2[] { new(0, 0), new(Lx, 0), new(Lx, Ly), new(0, Ly) } } } },
            Openings = { outlet },
            Sources = { nozzleSource },
        };

        // Domain: room + walls + a plenum of nP cells behind the extract wall, open at +y (the only open face).
        var min = new Vec3(-tw * d, -tw * d, 0);
        var size = new Vec3((nL + 2 * tw - 0.01f) * d, (nL + 2 * tw + nP - 0.01f) * d, (nH + tw - 0.01f) * d);
        var options = new RunOptions
        {
            Backend = Backend.Auto,
            WindProfileExponent = 0,
            MinDx = d,
            TargetCells = (int)(size.X * size.Y * size.Z / (d * d * d) * 2),   // asks for < Dx; clamps to exactly MinDx
            Domain = new DomainBox(min, min + size),
        };

        const double W = 0.05 / U0;   // the published mean-velocity uncertainty, 0.05 m/s (K08 p.74, DiB p.5), in U/U0
        var setup = new CaseSetup
        {
            Model = model, Options = options, Wind = Vec2.Zero,
            TimeScale = Ly / U0,                   // T = room length / U0 = 1.86 s
            WarmupT = 60, BlockT = 10, MinBlocks = 6, MaxBlocks = 30, ConvergeLag = 3, ConvergeTol = 0.01,
            Quantity = v => v.Length() / U0,
            W = W,
            WNote = $"W = 0.05 m/s = {W:0.000} U0: the published hot-wire mean-velocity uncertainty",
        };
        if (n != 4) { setup.OutSuffix = $"-n{n}"; setup.TitleSuffix = $" [grid study: {n} cells across the nozzle]"; }
        if (inlet != "measured") { setup.OutSuffix += "-" + inlet; setup.TitleSuffix += inlet switch
        {
            "table" => " [diagnostic: tabulated U0 = 1.67 m/s inlet]",
            "disk-table" => " [diagnostic: staircase disk inlet, tabulated 68 m3/h]",
            _ => " [diagnostic: staircase disk inlet, measured T1 momentum flux]",
        }; }

        var info = setup.Info;
        info["scale"] = $"full scale, Dx = {d * 1000:0.#} mm via RunOptions.MinDx; nozzle {n} x {n} cells";
        info["room_m"] = $"{Lx} x {Ly} x {H}";
        info["nozzle"] = disk
            ? $"round D = {Dn} m at ({X0}, {Y0}, {Z0}) m -> staircase disk, candidate box {side:0.###} x {side:0.###} m centred ({nozzleC.X:0.###}, {nozzleC.Y:0.###}, {nozzleC.Z:0.###}) m, blowing +y (trimmed to a disk in InspectGrid)"
            : $"round D = {Dn} m at ({X0}, {Y0}, {Z0}) m -> square patch {side:0.###} x {side:0.###} m centred ({nozzleC.X:0.###}, {nozzleC.Y:0.###}, {nozzleC.Z:0.###}) m, blowing +y";
        info["nozzle_area_ratio_patch_over_round"] = aPatch / aNoz;
        info["U0_mps"] = U0; info["U_set_mps"] = uSet; info["Q_set_m3ps"] = qSet; info["Q_table_m3ps"] = QMeasured;
        info["Q_set_over_table"] = qSet / QMeasured;
        info["inlet"] = inlet switch
        {
            "table" => "tabulated: U0 = 1.67 m/s through the 0.12 m nozzle, momentum flux kept on the square patch",
            "disk-table" => "tabulated: Q = 68 m3/h (the flowmeter reading, K08 Table 1) through a staircase disk D = 0.12 m",
            "disk-momentum" => "measured: momentum flux of the T1 plane (3 cm past the exit) through a staircase disk D = 0.12 m",
            _ => "measured: momentum flux of the T1 plane (3 cm past the exit) on the square patch",
        };
        info["T1_momentum_flux_m4ps2"] = mT1; info["T1_flow_m3ps"] = qT1;
        info["table_momentum_flux_m4ps2"] = U0 * U0 * aNoz; info["T1_over_table_momentum"] = mT1 / (U0 * U0 * aNoz);
        info["housing_m"] = $"x {hx0 * d:0.###}-{hx1 * d:0.###}, y 0-{hy1 * d:0.###}, z {hz0 * d:0.###}-{H} (estimated from DiB Fig. 1)";
        info["extract_box_m"] = $"x {ox0 * d:0.###}-{ox1 * d:0.###}, y {oy0 * d:0.###}-{Ly}, z 0-{ozh * d:0.###} open face -y (position estimated from DiB Fig. 1)";
        info["cells_across_nozzle"] = n;
        info["nu_m2ps"] = Nu; info["Re_nominal"] = U0 * Dn / Nu;
        // lattice_nu_molecular / lattice_nu_used / Re_effective_before_LES are filled in CheckField, once the
        // actual LbmSession lattice parameters (Runner.Run writes solver_dt_s / solver_nu_lat_used /
        // solver_velocity_scale into Info right after LbmSolver.Start) are known.

        int expectRoom = nL * nL * nH - (hx1 - hx0) * hy1 * (nH - hz0) - (ox1 - ox0 + 2) * (nL - oy0) - 2 * (nL - oy0) * ozh;
        int expectThroat = (ox1 - ox0) * ozh;
        setup.InspectGrid = (g, inf) =>
        {
            int room = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z);
                if (c.X > 0 && c.X < Lx && c.Y > 0 && c.Y < Ly && c.Z > 0 && c.Z < H && g.Cell[g.Index(x, y, z)] != VoxelGrid.Solid) room++;
            }
            // Fixed vs Air cells don't change the room-air count above (neither is Solid), so this check holds
            // whether the nozzle footprint is the square candidate or (after the disk trim just below) the disk.
            int fixedBefore = g.FixedCells.Count;
            Console.WriteLine($"  nozzle candidate cells {fixedBefore} (expect {(int)MathF.Round(side / d) * (int)MathF.Round(side / d)}), room air {room:N0} (expect {expectRoom:N0})");
            if (!disk && fixedBefore != n * n)
                throw new InvalidOperationException("grid does not match the intended geometry (nozzle patch cell count, see above)");
            if (room != expectRoom)
                throw new InvalidOperationException("grid does not match the intended geometry (room air cell count, see above)");

            if (disk)
            {
                // --inlet disk-*: trim the square candidate Patch down to a staircase disk of diameter Dn, by
                // reverting the corner cells (outside radius Dn/2 of the true nozzle centre X0, Z0) from Fixed
                // back to Air. The Supply flux controller (LbmSolver.BuildSupplyLoops) reads its target directly
                // from model.Sources[Supply].FlowRate at session start (not from cell count or initial velocity),
                // so it will scale to deliver exactly FlowRate through whatever cells remain -- trimming here,
                // before LbmSolver.Start builds the boundary list, is enough; no per-cell velocity surgery needed.
                float r = Dn / 2f;
                var keep = new List<FixedCell>(g.FixedCells.Count);
                int removed = 0;
                foreach (var fc in g.FixedCells)
                {
                    if (fc.Supply != 0) { keep.Add(fc); continue; }   // not the nozzle source: leave alone
                    int x = fc.Index % g.Nx, y = fc.Index / g.Nx % g.Ny, z = fc.Index / (g.Nx * g.Ny);
                    var c = g.CellCenter(x, y, z);
                    float dx = c.X - X0, dz = c.Z - Z0;
                    if (dx * dx + dz * dz <= r * r) { keep.Add(fc); continue; }
                    g.Cell[fc.Index] = VoxelGrid.Air; removed++;
                }
                g.FixedCells.Clear(); g.FixedCells.AddRange(keep);
                int diskCells = keep.Count(k => k.Supply == 0);
                double diskArea = diskCells * (double)d * d, analyticArea = MathF.PI * r * r;
                if (diskCells == 0) throw new InvalidOperationException("disk trim removed every nozzle cell -- Dn/2 too small for this Dx");
                // Exact target now that the true (rasterised) disk area is known -- the qSet/uSet computed above
                // were only a placeholder candidate. Mutating the captured qSet/uSet locals (not just
                // nozzleSource.FlowRate) means every later closure in this method (BlockScalars, FlowSummary,
                // Note) that reads qSet/uSet also sees the corrected value, since C# closures share one storage
                // cell for a local captured by several lambdas in the same method.
                qSet = inlet == "disk-table" ? (float)QMeasured : (float)Math.Sqrt(mT1 * diskArea);
                uSet = qSet / (float)diskArea;
                nozzleSource.FlowRate = qSet;
                inf["U_set_mps"] = (double)uSet; inf["Q_set_m3ps"] = (double)qSet; inf["Q_set_over_table"] = qSet / QMeasured;
                inf["disk_cells"] = diskCells; inf["disk_area_m2"] = diskArea; inf["disk_area_over_analytic"] = diskArea / analyticArea;
                Console.WriteLine($"  disk trim: kept {diskCells} of {fixedBefore} candidate cells (removed {removed}), " +
                    $"area {diskArea:0.00000} m2 ({diskArea / analyticArea:0.00}x the analytic circle), FlowRate corrected to {qSet:0.00000} m3/s");
            }

            int fixedCells = g.FixedCells.Count, throat = Analysis.ThroatCells(g, outlet).Count;
            inf["grid"] = $"{g.Nx} x {g.Ny} x {g.Nz}"; inf["cells"] = g.Count; inf["Dx_m"] = g.Dx;
            inf["room_air_cells"] = room; inf["fixed_nozzle_cells"] = fixedCells; inf["extract_throat_cells"] = throat;
            Console.WriteLine($"  nozzle cells (final) {fixedCells}, extract throat {throat} (expect {expectThroat})");
            if (Math.Abs(g.Dx - d) > 1e-6f) throw new InvalidOperationException($"Dx = {g.Dx}, expected {d} (is RunOptions.MinDx reaching Voxelizer.Build?)");
            if (throat != expectThroat)
                throw new InvalidOperationException("grid does not match the intended geometry (extract throat cell count, see above)");
        };

        // Delivered flow, per block: the extract throat, room cross-sections downstream of the housing, and the
        // jet flux through the nozzle footprint one cell downstream of the patch.
        double NetY(FlowField f, float yp)
        {
            var g = f.Grid; int py = (int)((yp - g.Origin.Y) / g.Dx); double q = 0;
            for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, py, z); var c = g.CellCenter(x, py, z);
                if (c.X > 0 && c.X < Lx && c.Z > 0 && c.Z < H && g.Cell[i] != VoxelGrid.Solid) q += f.Uy[i];
            }
            return q * g.Dx * g.Dx;
        }
        // Normalise by the source's FINAL FlowRate (nozzleSource.FlowRate): for --inlet disk-*, InspectGrid corrects
        // this from the candidate qSet to the exact target once the true (rasterised) disk area is known.
        setup.BlockScalars.Add(("extract_over_set", f => Analysis.ThroatCells(f.Grid, outlet).Sum(i => (double)f.Uy[i]) * d * d / nozzleSource.FlowRate));
        foreach (float yp in new[] { 1.0f, 2.0f, 2.7f })
            setup.BlockScalars.Add(($"room_plane_y{yp:0.0}_over_set", f => NetY(f, yp) / nozzleSource.FlowRate));
        int nSide = (int)MathF.Round(side / d);   // = n for the square patch; the disk's candidate bounding box otherwise
        setup.BlockScalars.Add(("jet_exit_flux_over_set", f =>
        {
            var g = f.Grid; double q = 0;
            int px = (int)MathF.Round((nzx0 * d - g.Origin.X) / d), py = (int)MathF.Round(((hy1 + 1) * d - g.Origin.Y) / d),
                pz = (int)MathF.Round((nzz0 * d - g.Origin.Z) / d);
            for (int k = 0; k < nSide; k++) for (int ii = 0; ii < nSide; ii++) q += f.Uy[g.Index(px + ii, py, pz + k)];
            return q * d * d / nozzleSource.FlowRate;
        }));
        setup.CheckField = (f, inf) =>
        {
            // Effective lattice Reynolds number from the LbmSession's own reported lattice parameters (see
            // CrossVentTB15.CheckField for the same pattern). VelocityScale is lattice units per m/s.
            double nuLatUsed = (double)inf["solver_nu_lat_used"];
            inf["lattice_nu_molecular"] = Nu * (double)inf["solver_dt_s"] / (d * d);
            inf["lattice_nu_used"] = nuLatUsed;
            inf["Re_effective_before_LES"] = uSet * (double)inf["solver_velocity_scale"] * n / nuLatUsed;
            inf["scored_mean_pred_over_U0"] = all.Where(p => p.Status == "ok").Average(p => Sampler.Velocity(f,p.W).Length()/U0);
            foreach (float ys in JetStations)
            {
                double peak = 0;
                for (float z = 2.0f; z < H - 0.01f; z += 0.005f) peak = Math.Max(peak, Sampler.Velocity(f,new(X0,ys,z)).Length()/U0);
                inf[$"jet_umax_over_U0_y{ys:0.0}"] = peak;
            }
        };

        // Jet maximum per station, measured (M1, scored points), for the decay comparison (K08 Fig. 6).
        foreach (float ys in JetStations)
        {
            var near = all.Where(p => p.Plane == "M1" && p.Status == "ok" && MathF.Abs(p.W.Y - ys) < 0.026f).ToList();
            info[$"jet_umax_over_U0_measured_y{ys:0.0}"] = near.Count > 0 ? near.Max(p => p.Obs) : double.NaN;
        }
        // Our own prediction at EVERY measured position (scored or not) and the dense M1 map, block-averaged, for the
        // plane figures. Evaluated as a block scalar so it sees each block-mean field; it returns the block's mean
        // speed of each block over scored positions (explicitly labelled block speed), while the figures
        // and scored mean use the magnitude of the accumulated mean vector.
        var acc = new Vec3[all.Count]; int accN = 0;
        float md = d;                                           // M1 map spacing
        int mNy = (int)MathF.Round(Ly / md), mNz = (int)MathF.Round(H / md);
        var m1 = new Vec3[mNy, mNz];
        setup.BlockScalars.Add(("block_scored_mean_speed_over_U0", f =>
        {
            for (int k = 0; k < all.Count; k++) acc[k] += Sampler.Velocity(f, all[k].W);
            for (int j = 0; j < mNy; j++) for (int k = 0; k < mNz; k++)
            {
                var v = Sampler.Velocity(f, new(X0, (j + 0.5f) * md, (k + 0.5f) * md));
                m1[j, k] += new Vec3(v.Y, 0, v.Z);             // Plot.Slice draws |(X, Z)|: in-plane (v, w) of the y-z plane
            }
            accN++;
            return Enumerable.Range(0, all.Count).Where(k => all[k].Status == "ok").Average(k => Sampler.Velocity(f, all[k].W).Length() / U0);
        }));

        string[] planes = ["M1", "T1", "T2", "T3", "T4"];
        foreach (var pl in planes)
        {
            var pts = all.Where(p => p.Plane == pl && p.Status == "ok").ToList();
            var line = new ProfileLine
            {
                Name = pl, Title = pl == "M1" ? "Median plane M1 (x = 1.55 m)" : $"Transversal plane {pl} (y = {pts[0].W.Y:0.00} m)",
                Vertical = true, PosLabel = "z (m)", ValueLabel = "|U| / U0",
                BarLabel = "bars: hot-wire mean-velocity uncertainty +-0.05 m/s", MeasuredLabel = "measured (three-hot-wire, Gresse et al. 2020 data)",
            };
            foreach (var p in pts) line.Measured.Add(new(p.W.Z, p.W, p.Obs, W));
            setup.Lines.Add(line);
        }
        // Region label for the q-miss breakdown: all planes traverse height z within the ceiling jet zone
        // (K08/DiB), so the split is by height alone -- within 0.10 m of the ceiling (nozzle housing/wall
        // boundary layer), within 0.10 m of the nozzle centreline z0 = 2.32 m (jet core, near the velocity
        // maximum), else the jet's lower shear layer where it entrains room air (a spatial label, not a
        // solver claim).
        setup.RegionOf = (_, p) => p.World.Z > H - 0.10f ? "near-ceiling boundary layer"
            : MathF.Abs(p.World.Z - Z0) <= 0.10f ? "jet core"
            : "jet lower shear layer";

        string repo = Path.GetFullPath(Path.Combine(dataDir, "..", "..", ".."));
        setup.FlowSummary = inf =>
        {
            double ex = (double)inf["extract_over_set"];
            return $"delivered/set {ex:0.000} (extract); set {qSet * 3600:0} m3/h ({inlet} inlet) vs 68 m3/h tabulated and {qT1 * 3600:0} m3/h measured on T1";
        };
        setup.Note = run =>
        {
            // Plane figures (replacing the harness's per-line profile PNGs, which cannot show a 2D plane) and the M1 map.
            string outDir = Path.Combine(repo, "out", "validation", Id + setup.OutSuffix);
            for (int k = 0; k < acc.Length; k++) acc[k] /= Math.Max(accN, 1);
            for (int li = 0; li < planes.Length; li++)
                PlaneFigure(Path.Combine(outDir, $"{planes[li]}.png"), setup.Lines[li], all.Select((p, k) => (p, pred: (double)acc[k].Length() / U0)).Where(t => t.p.Plane == planes[li]).ToList(),
                    run.PredMeasured[li], W, $"{Title}{setup.TitleSuffix}. Dx {d * 1000:0.#} mm, {run.Blocks} averaging blocks.");
            for (int j = 0; j < mNy; j++) for (int k = 0; k < mNz; k++) m1[j, k] /= Math.Max(accN, 1);
            Plot.Slice(Path.Combine(outDir, "field.png"), m1, U0, 1.0f, $"MINIBAT median plane M1 (x = 1.55 m), time-averaged{setup.TitleSuffix}",
                $"horizontal: y = 0 (south facade, nozzle housing top left) to 3.1 m; vertical: z = 0 to 2.5 m; mean of {run.Blocks} blocks; Dx {d * 1000:0.#} mm", "U0");

            var st = setup.Info;
            string G(string key, string fmt = "0.###") => st.TryGetValue(key, out var v) && v is double x ? x.ToString(fmt, CultureInfo.InvariantCulture) : "?";
            string decay = string.Join(", ", JetStations.Select(ys => $"y={ys:0.0}: {G($"jet_umax_over_U0_y{ys:0.0}", "0.00")} vs {G($"jet_umax_over_U0_measured_y{ys:0.0}", "0.00")}"));
            return $"- **{Title}{setup.TitleSuffix}** (run {DateTime.Now:yyyy-MM-dd HH:mm}, {run.Backend}): {st["scale"]}, grid {st["grid"]}. Room {st["room_m"]} m; " +
                $"{st["nozzle"]}; inlet {st["inlet"]}: set U {uSet:0.###} m/s, set Q {qSet * 3600:0.#} m3/h (tabulated U0 {U0} m/s = 68 m3/h; T1 measures {qT1 * 3600:0} m3/h and {G("T1_over_table_momentum", "0.00")}x the tabulated momentum flux); |U| is normalised by the tabulated U0. " +
                $"Housing {st["housing_m"]}; extract box {st["extract_box_m"]}, drained through the wall into a plenum open at +y; zero wind. " +
                $"**Delivered/set flow: extract {G("extract_over_set")}, room planes y = 1.0/2.0/2.7 m {G("room_plane_y1.0_over_set")}/{G("room_plane_y2.0_over_set")}/{G("room_plane_y2.7_over_set")}, " +
                $"jet flux one cell past the patch {G("jet_exit_flux_over_set")}.** Jet max |U|/U0 on M1, WindFlow vs measured: {decay}. " +
                $"Scored: the {stat.Ok} positions with measured |U| >= 0.1 m/s (probe floor); {stat.Below} below it and {stat.Masked} masked (all-zero) positions are not scored. " +
                $"Effective lattice Re about {G("Re_effective_before_LES", "0")} before the Smagorinsky term (NuLatMin floor), vs 13,360. " +
                $"Run: warm-up {setup.WarmupT} T (T = Ly/U0 = {setup.TimeScale:0.00} s), {run.Blocks} blocks of {setup.BlockT} T ({run.SimSeconds:0} s, {run.TotalSteps} steps), " +
                $"{(run.Converged ? "converged" : "NOT converged")} (RMS change over the last {setup.ConvergeLag} blocks < {setup.ConvergeTol} U0); largest block-mean standard error {run.MaxStdErr:0.###} U0. " +
                $"{setup.WNote}. Plots: `out/validation/{Id}{setup.OutSuffix}/*.png`.";
        };
        return setup;
    }

    static readonly float[] JetStations = [0.6f, 1.0f, 1.5f, 2.0f, 2.5f];
    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Rows per status: scored (ok), nonzero but under the probe's 0.1 m/s floor (below0.1), all-zero (masked).</summary>
    public static (int Ok, int Below, int Masked) CountStatuses(IEnumerable<string> statuses)
    {
        int ok = 0, below = 0, masked = 0;
        foreach (var s in statuses) { if (s == "ok") ok++; else if (s == "below0.1") below++; else if (s == "masked") masked++; }
        return (ok, below, masked);
    }

    sealed record Pt(string Plane, Vec3 W, double Obs, string Status, float Vy);

    // ---- plane figure: measured map | WindFlow map at the same positions | scatter ---------------------------------

    static void PlaneFigure(string path, ProfileLine line, List<(Pt p, double pred)> pts, double[] predScored, double w, string subtitle)
    {
        bool median = line.Name == "M1";
        Func<Vec3, double> hx = median ? v => v.Y : v => v.X;
        double h0 = pts.Min(t => hx(t.p.W)), h1 = pts.Max(t => hx(t.p.W)), z0 = pts.Min(t => t.p.W.Z), z1 = pts.Max(t => t.p.W.Z);
        double cw = median ? 0.05 : 0.02, ch = 0.02;              // sampling steps (DiB Table 5)
        h0 -= cw / 2; h1 += cw / 2; z0 -= ch / 2; z1 += ch / 2;
        const double vMax = 1.0;
        const int Hpx = 620, top = 70, mapH = 380;
        double mapW = median ? 560 : 330, gap = 60, x0a = 60, x0b = x0a + mapW + gap, x0c = x0b + mapW + gap + 20, sc = mapH;
        int Wpx = Math.Max(1500, (int)(x0c + sc + 200));   // room for the scatter's axis captions

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Wpx, Hpx));
            Text(dc, $"{line.Title}: measured vs WindFlow, |U| / U0", Wpx / 2.0, 10, 18, center: true, bold: true);
            Text(dc, subtitle, Wpx / 2.0, 36, 12, center: true);
            var grey = new SolidColorBrush(Color.FromRgb(215, 215, 215));
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(120, 120, 120)), 1);
            void Map(double x0, bool measured, string title)
            {
                Rect R(Pt p) => new(x0 + (hx(p.W) - cw / 2 - h0) / (h1 - h0) * mapW, top + (z1 - p.W.Z - ch / 2) / (z1 - z0) * mapH, cw / (h1 - h0) * mapW, ch / (z1 - z0) * mapH);
                foreach (var (p, pred) in pts)
                {
                    var r = R(p);
                    if (measured && p.Status == "masked") { dc.DrawRectangle(grey, null, r); continue; }
                    var (cr, cg, cb) = Colormap.Speed((float)Math.Clamp((measured ? p.Obs : pred) / vMax, 0, 1));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(cr, cg, cb)), null, r);
                    if (p.Status == "below0.1") dc.DrawLine(edge, r.TopLeft, r.BottomRight);
                }
                dc.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(x0, top, mapW, mapH));
                Text(dc, title, x0 + mapW / 2, top + mapH + 26, 13, center: true, bold: true);
                Text(dc, $"{(median ? "y" : "x")} = {h0 + cw / 2:0.00} .. {h1 - cw / 2:0.00} m (left to right)", x0 + mapW / 2, top + mapH + 6, 12, center: true);
                Text(dc, $"z {z1 - ch / 2:0.00}", x0 - 4, top - 2, 11, right: true);
                Text(dc, $"z {z0 + ch / 2:0.00}", x0 - 4, top + mapH - 14, 11, right: true);
            }
            Map(x0a, true, "measured");
            Map(x0b, false, "WindFlow (block-averaged)");

            // Scatter of the scored points: predicted vs measured, 1:1 and the factor-of-2 band.
            double sMax = Math.Max(0.2, Math.Ceiling(Math.Max(line.Measured.Max(m => m.Obs), predScored.Where(double.IsFinite).DefaultIfEmpty(0).Max()) * 5) / 5);
            WPoint S(double ob, double pr) => new(x0c + ob / sMax * sc, top + sc - pr / sMax * sc);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(x0c, top, sc, sc));
            var guide = new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(new Pen(Brushes.Black, 1), S(0, 0), S(sMax, sMax));
            dc.DrawLine(guide, S(0, 0), S(sMax / 2, sMax)); dc.DrawLine(guide, S(0, 0), S(sMax, sMax / 2));
            var dot = new SolidColorBrush(Color.FromArgb(150, 30, 70, 160));
            for (int k = 0; k < line.Measured.Count; k++)
                if (double.IsFinite(predScored[k])) dc.DrawEllipse(dot, null, S(line.Measured[k].Obs, predScored[k]), 2.5, 2.5);
            Text(dc, "0", x0c, top + sc + 4, 11, center: true);
            Text(dc, sMax.ToString("0.0", CultureInfo.InvariantCulture), x0c + sc, top + sc + 4, 11, center: true);
            Text(dc, sMax.ToString("0.0", CultureInfo.InvariantCulture), x0c - 4, top - 6, 11, right: true);
            Text(dc, "measured |U|/U0 (scored points)", x0c + sc / 2, top + sc + 20, 12, center: true);
            Text(dc, "WindFlow |U|/U0 (vertical); solid 1:1, dashed factor 2", x0c + sc / 2, top + sc + 36, 12, center: true);

            // Colour bar and legend.
            double by = Hpx - 70;
            for (int i = 0; i < 200; i++)
            {
                var (cr, cg, cb) = Colormap.Speed(i / 199f);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(cr, cg, cb)), null, new Rect(x0a + i * 2, by, 2, 14));
            }
            Text(dc, "0", x0a, by + 16, 11, center: true); Text(dc, $"{vMax:0.0} U0 (saturates)", x0a + 400, by + 16, 11, center: true);
            Text(dc, $"grey: masked (all-zero in the file, no measurement); diagonal stroke: measured |U| < 0.1 m/s (probe floor, not scored). U0 = {U0} m/s; scored points: {line.Measured.Count}; W = {w:0.000} U0.",
                x0a + 440, by, 12);
        }
        var rtb = new RenderTargetBitmap(Wpx, Hpx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void Text(DrawingContext dc, string s, double x, double y, double size, bool center = false, bool right = false, bool bold = false)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0);
        dc.DrawText(ft, new WPoint(x + (center ? -ft.Width / 2 : right ? -ft.Width : 0), y));
    }

    static CoreMesh Box(string id, Vec3 a, Vec3 b)
    {
        var v = new Vec3[8];
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
