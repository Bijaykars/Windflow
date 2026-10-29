using System.Globalization;
using System.Numerics;
using WindFlow.Core;
using CoreMesh = WindFlow.Core.Mesh;

namespace WindFlow.Validation;

/// <summary>IEA Annex 20 two-dimensional test case (Nielsen 1990; Nielsen, Rong &amp; Olmedo 2010), isothermal
/// case 2D1: L/H = 3, h/H = 0.056 (supply slot under the ceiling, left wall), t/H = 0.16 (outlet at the floor,
/// right wall), Re = u0 h / nu = 5000. Measured: LDA mean u/u0 on the symmetry plane of an H = 89.3 mm,
/// W/H = 1 model (Restivo 1979), along x = H, x = 2H, y = h/2 and y = H - h/2 (y measured down from the ceiling).
/// <para>Our solver is 3D, so this is a room of width W = H with a full-width slot, compared on the centre
/// plane -- which is also the geometry of the measured model.</para>
/// <para>Scale: Voxelizer.BuildDomain clamps Dx to &gt;= 0.1 m, so an H = 3 m room can have at most 1.7 cells
/// across its 0.168 m slot. The case is isothermal and defined by L/H, h/H, t/H and Re, so it is built at the
/// Reynolds-similar scale where the slot is SlotCells cells of 0.1 m (H = 7.1 m for 4 cells) with
/// u0 = Re nu / h, and compared in u/u0 against x/H, y/H -- the same similarity the source uses to transfer its
/// 89.3 mm model measurements to the H = 3 m specification.</para></summary>
public sealed class Annex20TwoD : ValidationCase
{
    public override string Id => "annex20-2d";
    public override string Title => "IEA Annex 20 2D (isothermal, Re 5000)";
    public override string Citation => "Nielsen 1990 (IEA Annex 20 2D test case), LDA data of Restivo 1979";
    public override string Quantities => "u/u0 on x=H, x=2H, y=h/2, y=H-h/2 (centre plane)";

    const float Nu = 15.3e-6f;   // m^2/s at 20 C, the value the specification uses (Nielsen 1990, eq. 3-4)
    const float Re = 5000f;

    public override CaseSetup Build(string dataDir, Dictionary<string, string> args)
    {
        int nh = args.TryGetValue("slot-cells", out var sc) ? int.Parse(sc, CultureInfo.InvariantCulture) : 4;
        if (nh < 4) throw new ArgumentException("slot-cells must be >= 4 (the brief's resolution floor)");
        const float d = 0.1f;                                   // Dx: the Voxelizer's floor, hit on purpose
        int nH = (int)MathF.Round(nh / 0.056f);                  // 4 -> 71
        int nL = 3 * nH, nW = nH;
        int nt = (int)MathF.Round(0.16f * nH);                   // 71 -> 11
        int nP = nH / 2;                                         // outdoor plenum past the outlet wall
        const int tw = 2;                                        // wall thickness, cells
        // --inlet flush (default, how a user places a supply grille in the product): the Patch sits in the first
        // room-air column against the inlet wall, so its lower face borders room air. --inlet duct (diagnostic):
        // the slot is a 3-cell duct through a 3-cell inlet wall and the Patch sits in its middle cell, so the
        // patch borders only duct walls and the duct -- it can no longer exchange mass with the room below it.
        bool duct = args.TryGetValue("inlet", out var inl) && inl == "duct";
        if (args.ContainsKey("inlet") && inl is not ("duct" or "flush")) throw new ArgumentException("--inlet flush|duct");
        int tl = duct ? 3 : tw;                                  // inlet-wall thickness, cells
        // R2-05: the specification's inlet (Nielsen 1990 p.2 eq. 5-7, Fig. 4 p.7) is a measured profile, about 0.92 u0 at the
        // slot edges and 1.08-1.10 u0 mid-slot, with 4 % turbulence intensity. Both are the default (RX-08: what is delivered is not that, see InletText); --inlet-profile uniform
        // and --inlet-tu 0 give the old plain inlet (own folder suffix). l0 = h/10 is not representable: the solver's
        // supply perturbation has its own D/U correlation time.
        bool uniformProfile = args.TryGetValue("inlet-profile", out var ip) && ip == "uniform";
        if (args.ContainsKey("inlet-profile") && ip is not ("uniform" or "spec")) throw new ArgumentException("--inlet-profile spec|uniform");
        float inletTu = args.TryGetValue("inlet-tu", out var itu) ? float.Parse(itu, CultureInfo.InvariantCulture) : 0.04f;

        float H = nH * d, L = nL * d, W = nW * d, h = nh * d, t = nt * d;
        float u0 = Re * Nu / h;

        // Walls: boxes whose faces sit 0.05 Dx inside the solid side of a cell face, so each face marks exactly
        // one cell layer (VoxelizeObstacles marks every cell a face triangle touches, +-0.5 Dx, and leaves a
        // thick box's interior as air) and the room interior is exactly nL x nW x nH cells. The interpolated-wall
        // distance puts the wall 0.05 Dx off the cell face.
        float e = 0.05f * d, o = tw * d - e;                    // o: outer face of a 2-cell wall
        float xEnd = (nL + tw + nP + 1) * d;                     // past the domain box: rasteriser clamps
        float x0 = -(tl * d - e);                                // outer face of the inlet wall
        var obstacles = new List<CoreMesh>
        {
            Box("wall-inlet", new(-(2 * d - e), -o, 0), new(-e, W + o, H + o)),
            Box("wall-outlet", new(L + e, -o, 0), new(L + o, W + o, H + o)),
            Box("wall-south", new(x0, -o, 0), new(xEnd, -e, H + o)),
            Box("wall-north", new(x0, W + e, 0), new(xEnd, W + o, H + o)),
            Box("ceiling", new(x0, -o, H + e), new(xEnd, W + o, H + o)),
        };
        if (duct) obstacles.Add(Box("wall-inlet-outer", new(x0, -o, 0), new(-(2 * d + e), W + o, H + o)));

        var model = new BuildingModel
        {
            Name = "IEA Annex 20 2D test room (Re-similar scale)",
            GroundZ = 0,
            Storeys = { new Storey { Id = "L0", Name = "Room", Elevation = 0, Height = H, Analysed = true } },
            Obstacles = obstacles,
            Rooms = { new Room { Id = "room", Name = "Test room", Level = "L0", ZMin = 0, ZMax = H,
                Loops = { new Vector2[] { new(0, 0), new(L, 0), new(L, W), new(0, W) } } } },
            Openings = { new Opening { Id = "outlet", Name = "Outlet slot", Level = "L0", Kind = OpeningKind.Opening, IsOpen = true,
                Center = new(L + tw * d / 2, W / 2, t / 2), Normal = Vector3.UnitX, Width = W, Height = t, Depth = tw * d } },
            // Patch supply one cell thick, the full width, the top nh cells.
            Sources = { new AirSource { Id = "inlet", Name = "Inlet slot", Level = "L0", Kind = SourceKind.Supply, Device = DeviceKind.Patch,
                Center = new(duct ? -1.5f * d : 0.5f * d, W / 2, H - h / 2), Direction = Vector3.UnitX, Width = W, Height = h, FlowRate = u0 * W * h } },
        };
        // The duct: carved through the whole inlet wall (the outermost cell is the domain ring; that face is then
        // under 35 % air and Voxelizer closes it as a wall, so the duct is closed behind the patch).
        if (duct)
            model.Openings.Add(new Opening { Id = "inlet-duct", Name = "Inlet slot duct", Level = "L0", Kind = OpeningKind.Opening, IsOpen = true,
                Center = new(-1.5f * d, W / 2, H - h / 2), Normal = -Vector3.UnitX, Width = W, Height = h, Depth = tl * d });

        // Domain: the room, its walls, and an outdoor plenum of nP cells past the outlet wall, open at +X.
        // Sizes are n - 0.01 cells so Ceiling(size / Dx) lands on n exactly.
        var min = new Vector3(-tl * d, -tw * d, 0);
        var size = new Vector3((nL + tl + tw + nP - 0.01f) * d, (nW + 2 * tw - 0.01f) * d, (nH + tw - 0.01f) * d);
        var options = new RunOptions
        {
            Backend = Backend.Auto,
            WindProfileExponent = 0,
            TargetCells = (int)(size.X * size.Y * size.Z / (d * d * d) * 2),   // asks for < 0.1 m, clamps to exactly 0.1 m
            Domain = new DomainBox(min, min + size),
            SupplyPerturbation = inletTu,            // R2-05: isotropic inlet Tu, rms fraction of the face velocity (spec 4 %)
        };

        var setup = new CaseSetup
        {
            Model = model, Options = options, Wind = Vector2.Zero,
            TimeScale = H / u0,                  // T = H / u0
            WarmupT = 100, BlockT = 10, MinBlocks = 8, MaxBlocks = 40, ConvergeLag = 3, ConvergeTol = 0.005,
            Quantity = v => v.X / u0,
            // No measurement uncertainty is published with the data; W = 0.05 u0 is our choice (5 % of the supply
            // velocity, below the 0.07-0.2 u0 turbulence rms the source reports everywhere on these lines).
            W = 0.05,
            WNote = "W = 0.05 (u/u0): chosen, not published; the source gives turbulence rms 0.07-0.20 u0 but no measurement uncertainty",
        };
        setup.SliceY = W / 2; setup.SliceMin = new(0, 0); setup.SliceMax = new(L, H); setup.SliceScale = u0; setup.SliceMax01 = 0.5f;
        setup.SliceTitle = "Annex 20 2D: centre-plane time-averaged flow (inlet top left, outlet bottom right)";
        if (nh != 4) { setup.OutSuffix = $"-s{nh}"; setup.TitleSuffix = $" [grid study: {nh} cells across the slot]"; }
        if (duct) { setup.OutSuffix += "-duct"; setup.TitleSuffix += " [diagnostic: patch recessed in a slot duct]"; }
        if (uniformProfile) { setup.OutSuffix += "-uniform"; setup.TitleSuffix += " [diagnostic: uniform inlet profile]"; }
        if (inletTu != 0.04f) { setup.OutSuffix += $"-tu{inletTu * 100:0.#}"; setup.TitleSuffix += $" [diagnostic: inlet Tu {inletTu * 100:0.#} %]"; }
        // Spec inlet shape: 0.92 u0 at the slot edges to 1.09 u0 mid-slot (sine between), sampled at the cell centres and
        // normalised to a mean of 1 so the slot still carries u0 x h x W (the flux controller scales the patch to that
        // flow); what that delivers (peak, edge) is in Info["inlet_peak_over_u0"] / ["inlet_edge_over_u0"] and the Info["inlet"] text (RX-08).
        string InletText(string profile) => (duct ? "Patch recessed 1 cell inside a 3-cell slot duct (diagnostic variant)" : "Patch flush in the first room-air column (product placement)") +
            profile + $", {inletTu * 100:0.#} % requested wall-velocity Tu" + (inletTu > 0 ? ", about half reaches the fluid (P13a-04) until 6i; length scale not set" : "");
        if (!uniformProfile)
            setup.ShapeGrid = g =>
            {
                var raw = new double[g.FixedCells.Count];
                for (int k = 0; k < raw.Length; k++)
                {
                    float eta = Math.Clamp((g.CellCenter(0, 0, g.FixedCells[k].Index / (g.Nx * g.Ny)).Z - (H - h)) / h, 0f, 1f);
                    raw[k] = 0.92 + 0.17 * Math.Sin(Math.PI * eta);
                }
                double mean = raw.Length > 0 ? raw.Average() : 1;
                for (int k = 0; k < raw.Length; k++)
                    g.FixedCells[k] = g.FixedCells[k] with { Velocity = g.FixedCells[k].Velocity * (float)(raw[k] / mean) };
                double peak = raw.Length > 0 ? raw.Max() / mean : 1.0, edge = raw.Length > 0 ? raw.Min() / mean : 1.0;
                setup.Info["inlet_peak_over_u0"] = peak;
                setup.Info["inlet_edge_over_u0"] = edge;
                setup.Info["inlet"] = InletText(string.Create(CultureInfo.InvariantCulture,
                    $", sine profile mean-normalised to u0, delivered {edge:0.000}-{peak:0.000} u0 at {nh} cells (Nielsen 1990 Fig. 4 asks 0.92-1.09)"));
            };
        var info = setup.Info;
        info["scale"] = $"Reynolds-similar: slot {nh} cells of {d} m -> H = {H:0.###} m (spec H = 3.0 m); compared in u/u0 vs x/H, y/H";
        info["H_m"] = H; info["L_m"] = L; info["W_m"] = W; info["h_m"] = h; info["t_m"] = t;
        info["L_over_H"] = (double)nL / nH; info["h_over_H"] = (double)nh / nH; info["t_over_H"] = (double)nt / nH; info["W_over_H"] = 1.0;
        info["spec"] = "L/H = 3.0, h/H = 0.056, t/H = 0.16, Re = 5000 (Nielsen 1990 eq. 1, 3); measured model W/H = 1.0";
        info["u0_mps"] = u0; info["nu_m2ps"] = Nu; info["Re"] = Re;
        info["cells_across_slot"] = nh;
        info["inlet"] = InletText(uniformProfile ? ", uniform profile" : ", sine profile mean-normalised to u0 (delivered values are set when the grid is built)");
        info["plane"] = "centre plane y = W/2 (the measured model's symmetry plane)";
        // Effective lattice Reynolds number from the solver constants (LbmSolver.cs 2026-09-28: ULat = 0.08,
        // NuLatMin = 5e-4, NuPhysAir = 1.5e-5). Not exposed by Core -- see the report's Core API note.
        float dtEst = 0.08f * d / u0, nuLatPhys = 1.5e-5f * dtEst / (d * d);
        float nuLat = MathF.Max(nuLatPhys, 5e-4f);
        info["lattice_nu_molecular"] = nuLatPhys; info["lattice_nu_used"] = nuLat;
        info["Re_effective_before_LES"] = 0.08 * nh / nuLat;

        int expectedFixed = nW * nh, expectedRoom = nL * nW * nH;
        setup.InspectGrid = (g, inf) =>
        {
            int fixedCells = g.FixedCells.Count;
            // Room air count and the outlet throat height, read back from the grid.
            int room = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z);
                if (c.X > 0 && c.X < L && c.Y > 0 && c.Y < W && c.Z > 0 && c.Z < H && g.Cell[g.Index(x, y, z)] != VoxelGrid.Solid) room++;
            }
            int throat = 0, ix = (int)((L + d * 1.5f - g.Origin.X) / d), iy = (int)((W / 2 - g.Origin.Y) / d);
            for (int z = 0; z < g.Nz; z++) if (g.Cell[g.Index(ix, iy, z)] != VoxelGrid.Solid) throat++;
            inf["grid"] = $"{g.Nx} x {g.Ny} x {g.Nz}"; inf["cells"] = g.Count; inf["Dx_m"] = g.Dx;
            inf["fixed_inlet_cells"] = fixedCells; inf["room_air_cells"] = room; inf["outlet_throat_cells_high"] = throat;
            Console.WriteLine($"  inlet cells {fixedCells} (expect {expectedFixed}), room air {room:N0} (expect {expectedRoom:N0}), outlet throat {throat} cells high (expect {nt})");
            if (Math.Abs(g.Dx - d) > 1e-6f) throw new InvalidOperationException($"Dx = {g.Dx}, expected {d}");
            if (fixedCells != expectedFixed || room != expectedRoom || throat != nt)
                throw new InvalidOperationException("grid does not match the intended geometry (see the counts above)");
        };
        setup.CheckField = (f, inf) =>
        {
            // Mass balance of the last block: flux through the outlet throat vs the imposed supply.
            var g = f.Grid;
            int ix = (int)((L + d * 1.5f - g.Origin.X) / d);
            double q = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++)
            {
                int i = g.Index(ix, y, z);
                if (g.Cell[i] != VoxelGrid.Solid) q += f.Ux[i] * d * d;
            }
            double qIn = u0 * W * h;
            inf["supply_m3ps"] = qIn; inf["outlet_m3ps_last_block"] = q; inf["outlet_over_supply"] = q / qIn;
            // The same flux through room cross-sections: in a converged mean every plane carries the supply.
            var planes = new Dictionary<string, double>();
            foreach (float xp in new[] { 0.15f, 0.55f, H / 2, H, 2 * H, L - 0.15f })
            {
                int px = (int)((xp - g.Origin.X) / d);
                double qp = 0;
                for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++)
                {
                    int i = g.Index(px, y, z);
                    if (g.Cell[i] != VoxelGrid.Solid) qp += f.Ux[i] * d * d;
                }
                planes[$"x={xp / H:0.###}H"] = qp / qIn;
            }
            inf["plane_flux_over_supply"] = planes;
            // Room-mean pressure as a lattice density excess (FlowField.P = (rho - 1) * pScale, pScale from LbmSolver:
            // (1/3)(Dx/dt)^2 * 1.2 with dt = ULat Dx / u0 for this run's velocity scale).
            double dtRun = 0.08 * d / u0, pScale = (1.0 / 3.0) * (d / dtRun) * (d / dtRun) * 1.2, pSum = 0; int pN = 0;
            for (int z = 1; z <= nH; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, z); var c = g.CellCenter(x, y, z);
                if (c.X > 0 && c.X < L && g.Cell[i] == VoxelGrid.Air) { pSum += f.P[i]; pN++; }
            }
            inf["room_mean_P_Pa"] = pSum / pN; inf["room_mean_rho_excess_lattice"] = pSum / pN / pScale;
            Console.WriteLine($"  room mean P {pSum / pN:0.####} Pa = lattice rho - 1 of {pSum / pN / pScale:0.#####}");
            Console.WriteLine("  flux / supply: throat " + (q / qIn).ToString("0.000") + ", " + string.Join(", ", planes.Select(p => $"{p.Key} {p.Value:0.000}")));
        };

        // Measured lines. Source y runs DOWN from the ceiling: world z = H - y. Horizontal lines are defined at
        // y = h/2 and y = H - h/2 with the room's own h (the source's 0.028 / 0.972 are these for h/H = 0.056).
        var byLine = ReadCsv(Path.Combine(dataDir, "measured.csv"));
        float yc = W / 2;
        ProfileLine Vertical(string name, float xH)
        {
            var l = new ProfileLine { Name = name, Title = $"Vertical profile {name}", Vertical = true, PosLabel = "height above floor z/H = 1 - y/H",
                ValueLabel = "u / u0", BarLabel = "bars: measured turbulence rms (no measurement uncertainty published)", MeasuredLabel = "measured (Restivo 1979 LDA, via Nielsen 1990)" };
            foreach (var r in byLine[name]) l.Measured.Add(new(1 - r.y, new(xH * H, yc, (float)(H * (1 - r.y))), r.u, r.rms));
            for (int k = 0; k <= 400; k++) { double zz = k / 400.0; l.Dense.Add((zz, new(xH * H, yc, (float)(zz * H)))); }
            return l;
        }
        ProfileLine Horizontal(string name, float zWorld, string where)
        {
            var l = new ProfileLine { Name = name, Title = $"Horizontal profile {name} ({where})", Vertical = false, PosLabel = "x/H",
                ValueLabel = "u / u0", BarLabel = "bars: measured turbulence rms (no measurement uncertainty published)", MeasuredLabel = "measured (Restivo 1979 LDA, via Nielsen 1990)" };
            foreach (var r in byLine[name]) l.Measured.Add(new(r.x, new((float)(r.x * H), yc, zWorld), r.u, r.rms));
            for (int k = 0; k <= 600; k++) { double xx = 3.0 * k / 600; l.Dense.Add((xx, new((float)(xx * H), yc, zWorld))); }
            return l;
        }
        setup.Lines.Add(Vertical("x=H", 1f));
        setup.Lines.Add(Vertical("x=2H", 2f));
        setup.Lines.Add(Horizontal("y=h/2", H - h / 2, "under the ceiling"));
        setup.Lines.Add(Horizontal("y=H-h/2", h / 2, "above the floor"));
        // Region label for the q-miss breakdown: the horizontal lines sit entirely in one region by construction
        // (y=h/2 traverses the ceiling wall jet, y=H-h/2 the floor return); on the two vertical traverses, Pos is
        // z/H (0 = floor, 1 = ceiling), so the top/bottom 20% are the same two regions and the rest is the room's
        // single primary recirculation (Nielsen 1990's known 2D1 flow pattern -- a spatial label, not a solver claim).
        setup.RegionOf = (li, p) => setup.Lines[li].Name switch
        {
            "y=h/2" => "ceiling jet",
            "y=H-h/2" => "floor return",
            _ => p.Pos > 0.8 ? "ceiling jet" : p.Pos < 0.2 ? "floor return" : "recirculation core",
        };
        return setup;
    }

    static Dictionary<string, List<(double x, double y, double u, double rms)>> ReadCsv(string path)
    {
        var res = new Dictionary<string, List<(double, double, double, double)>>();
        bool header = true;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.StartsWith('#') || raw.Trim().Length == 0) continue;
            if (header) { header = false; continue; }
            var p = raw.Split(',');
            double P(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
            if (!res.TryGetValue(p[0], out var list)) res[p[0]] = list = new();
            list.Add((P(1), P(2), P(3), P(4)));
        }
        return res;
    }

    static CoreMesh Box(string id, Vector3 a, Vector3 b)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        int[] idx =
        {
            0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,   // z faces
            0, 1, 4, 1, 5, 4,   2, 6, 3, 3, 6, 7,   // y faces
            0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,   // x faces
        };
        return new CoreMesh { Id = id, Vertices = v, Indices = idx };
    }
}
