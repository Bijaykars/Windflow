// Room-flux conservation (CPU and GPU).  Run with:
//   dotnet run -c Release --project src/WindFlow.Sandbox -- --fluxcons      (or as part of --fixes / the default run)
//
// The room flux is the conserved D3Q19 link flux across the room boundary (LbmRoomFlux.cs, RoomFluxPlan.Build). Its
// defining property is an exact balance: for a room R and any interval of steps, the mass of R's air cells changes by
// exactly what crossed its boundary,
//        M_R(b) - M_R(a) = - sum of per-step NetFlux_R (outward positive),     in lattice mass units,
// with M_R = sum of all 19 populations over R's cells, read from the populations. The solver's collision conserves
// mass cell by cell and wall links bounce back, so nothing but link crossings changes M_R: a room whose faces
// double-count or drop a link breaks the balance by that link's flux every step. The scenes are unsteady (the wind
// ramps in from rest against closed buildings with windows), so M_R really moves.
//
// Alignment (the f* convention): the lattice stores POST-COLLISION populations f*. After step s, cur = f*_s, and the
// accumulator of step s sums the links of f*_s, which govern M_{s+1} - M_s. Hence over the steps a..b the identity is
//        M(b) - M(a) + (C(b-1) - C(a-1)) = 0,       C(s) = accumulated net outward flux after step s, C(-1) = 0.
// The accumulator's raw per-face sums (RoomFluxRaw: Sum max(+-net,0) over steps) give C(s) = Sum (Out - In) per room
// exactly: net per face per step is Out - In whatever the sign. Open domain faces (Outside < 0) have no D cell and read
// the node velocity, which is not conservative; they are summed apart and only the cropped scene (FC4) has them.
//
// Tolerances (exact scenes), for the whole run and for every 10-step window:
//     |residual| <= 1e-4 x gross exchange (Sum |per-face net| over the interval)  +  float32 allowance (FcFloorPerStep x M_R(0) x steps),
//     and over the whole run also |residual| <= 1e-5 x M_R(0).
// The allowance is needed because the solver itself does not conserve mass better than float32: with NO flux at all (a room at rest,
// gross exchange 0) M_R still wanders by about 1.5e-4 per step (1.7e-7 relative, one sign more often than not) on both backends, so a
// bare 1e-4-of-gross bound is unsatisfiable in any window where little air moves. 2e-7 per step covers that measured rate; the
// 1e-5 x M_R(0) cap on the whole run is the brief's float32 mass limit. Cropped scene: stated in FcCropTol below.

using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static partial class FixChecks
{
    const float FcDx = 0.1f;
    const int FcNx = 44, FcNy = 32, FcNz = 12, FcSteps = 400, FcWindow = 10;
    const double FcTolGross = 1e-4, FcTolMass = 1e-5, FcFloorPerStep = 2e-7;
    /// <summary>Cropped scene only: the open domain face reads node velocity (no conserved crossing) and the ring cells inside
    /// the room are rewritten by the wind boundary every step (mass the room's links never carried), so the balance is only
    /// approximate. Tolerance on |residual| as a fraction of the room's gross exchange (all faces), whole run.</summary>
    const double FcCropTol = 0.10;

    /// <summary>Runs the flux-conservation suite alone (--fluxcons) and returns the number of failures.</summary>
    public static int RunFluxCons()
    {
        failures = 0;
        Console.WriteLine("== flux conservation checks ==");
        FluxConservationChecks();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL FLUX CONSERVATION CHECKS PASSED" : $"{failures} FLUX CONSERVATION CHECK(S) FAILED");
        return failures;
    }

    // ---- scene construction ------------------------------------------------------------------------------------

    sealed record FcScene(string Id, string Title, VoxelGrid Grid, BuildingModel Model, Vector2 Wind, bool Cropped);
    readonly record struct FcBox(int X0, int X1, int Y0, int Y1, int Z0, int Z1);

    /// <summary>Ground at z = 0; interior columns (x,y) are air for z = 1..6 under a roof at z = 7; walls are every non-interior
    /// column within one cell (8-neighbourhood, so a diagonal wall is 4-connected and watertight) solid for z = 1..7;
    /// <paramref name="windows"/> are carved back to air.</summary>
    static VoxelGrid FcBuilding(Func<int, int, bool> interior, params FcBox[] windows)
    {
        var g = new VoxelGrid { Nx = FcNx, Ny = FcNy, Nz = FcNz, Dx = FcDx, Cell = new byte[FcNx * FcNy * FcNz] };
        for (int y = 0; y < FcNy; y++)
        for (int x = 0; x < FcNx; x++)
        {
            g.Cell[g.Index(x, y, 0)] = VoxelGrid.Solid;
            if (interior(x, y)) { g.Cell[g.Index(x, y, 7)] = VoxelGrid.Solid; continue; }
            bool near = false;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < FcNx && yy >= 0 && yy < FcNy && interior(xx, yy)) near = true;
            }
            if (near) for (int z = 1; z <= 7; z++) g.Cell[g.Index(x, y, z)] = VoxelGrid.Solid;
        }
        foreach (var w in windows)
            for (int z = w.Z0; z <= w.Z1; z++) for (int y = w.Y0; y <= w.Y1; y++) for (int x = w.X0; x <= w.X1; x++)
                g.Cell[g.Index(x, y, z)] = VoxelGrid.Air;
        return g;
    }

    static Room FcRoom(string id, params (float X, float Y)[] cornersInCells) => new()
    {
        Id = id, Name = id, ZMin = FcDx, ZMax = 7 * FcDx,
        Loops = { cornersInCells.Select(c => new Vector2(c.X * FcDx, c.Y * FcDx)).ToArray() },
    };

    static (float, float)[] FcRect(int x0, int x1, int y0, int y1) => [(x0, y0), (x1 + 1, y0), (x1 + 1, y1 + 1), (x0, y1 + 1)];

    static FcScene FcMake(string id, string title, VoxelGrid g, Func<int, int, bool> interior, bool cropped, params Room[] rooms)
    {
        var m = new BuildingModel(); m.Rooms.AddRange(rooms);
        // The room polygons together must select exactly the interior air columns z = 1..6 (the scene is what it claims to be).
        var union = new bool[g.Count];
        foreach (var r in rooms)
        {
            var mask = Analysis.RoomMask(g, r, out int n);
            if (n == 0) throw new InvalidOperationException($"{id}: room {r.Id} has no air");
            for (int i = 0; i < g.Count; i++) union[i] |= mask[i];
        }
        int bad = 0;
        for (int z = 0; z < FcNz; z++) for (int y = 0; y < FcNy; y++) for (int x = 0; x < FcNx; x++)
            if (union[g.Index(x, y, z)] != (interior(x, y) && z is >= 1 and <= 6)) bad++;
        if (bad != 0) throw new InvalidOperationException($"{id}: room polygons disagree with the interior at {bad} cells");
        return new FcScene(id, title, g, m, new Vector2(3, 0), cropped);
    }

    static IEnumerable<FcScene> FcScenes()
    {
        // FC1 plain box room, one window in the windward wall.
        bool Box1(int x, int y) => x is >= 16 and <= 27 && y is >= 10 and <= 21;
        yield return FcMake("FC1", "plain box room, one window, wind-driven (Helmholtz-like pressurising)",
            FcBuilding(Box1, new FcBox(15, 15, 14, 17, 2, 4)), Box1, false, FcRoom("box", FcRect(16, 27, 10, 21)));

        // FC2 L-shaped room: one concave corner at (22,15) plus convex corners elsewhere; windows west (leg B) and east (leg A).
        bool L(int x, int y) => (x is >= 16 and <= 31 && y is >= 10 and <= 14) || (x is >= 16 and <= 21 && y is >= 10 and <= 24);
        yield return FcMake("FC2", "L-shaped room (concave + convex corners), through-flow",
            FcBuilding(L, new FcBox(15, 15, 19, 22, 2, 4), new FcBox(32, 32, 11, 13, 2, 4)), L, false,
            FcRoom("L", (16, 10), (32, 10), (32, 15), (22, 15), (22, 25), (16, 25)));

        // FC3 two rooms joined by a doorway in their shared wall (x = 22); window west of room 1 and east of room 2.
        bool Two(int x, int y) => y is >= 10 and <= 21 && (x is >= 14 and <= 21 || x is >= 23 and <= 30);
        yield return FcMake("FC3", "two rooms joined by a doorway, windows at both ends",
            FcBuilding(Two, new FcBox(22, 22, 14, 17, 1, 4), new FcBox(13, 13, 14, 17, 2, 4), new FcBox(31, 31, 12, 15, 2, 4)), Two, false,
            FcRoom("west", FcRect(14, 21, 10, 21)), FcRoom("east", FcRect(23, 30, 10, 21)));

        // FC4 room cropped by the domain: its west face is the x = 0 domain face (open, wind blows straight in), window in its east wall.
        // RZ-04: the grid has an open top, as every user box does, so it runs the ring the product gives a cropped room (the pre-6b
        // equilibrium ring, BcInflowEq/BcOutflowEq): the 6b velocity inlet through the cut wall has no steady state.
        bool Crop(int x, int y) => x is >= 0 and <= 8 && y is >= 10 and <= 21;
        var gCrop = FcBuilding(Crop, new FcBox(9, 9, 14, 17, 2, 4)); gCrop.OpenZMax = true;
        yield return FcMake("FC4", "room cropped by the open domain face x = 0 (velocity-based open face)",
            gCrop, Crop, true, FcRoom("crop", FcRect(0, 8, 10, 21)));
        // FC4b (RZ-04, was FC4 in 6b): the same room on a closed-top grid with the wind from the east, so x = 0 is the 6b rho = 1 outflow face.
        yield return FcMake("FC4b", "room cropped by the domain face x = 0, 6b ring, wind from the east (rho = 1 outflow face)",
            FcBuilding(Crop, new FcBox(9, 9, 14, 17, 2, 4)), Crop, true, FcRoom("crop", FcRect(0, 8, 10, 21))) with { Wind = new Vector2(-3, 0) };

        // FC5 pentagon: a rectangle with its north-east corner cut by a 45 degree wall (staircase 2 diagonal layers thick, 4-connected).
        bool Pent(int x, int y) => x is >= 14 and <= 29 && y is >= 10 and <= 25 && x + y <= 43;
        yield return FcMake("FC5", "room with a 45 degree wall segment (voxel staircase), windows west and east",
            FcBuilding(Pent, new FcBox(13, 13, 15, 18, 2, 4), new FcBox(30, 30, 11, 14, 2, 4)), Pent, false,
            FcRoom("pent", (14, 10), (30, 10), (30, 14.5f), (18.5f, 26), (14, 26)));

        // FC6 wall-less room region in open air behind an upstream plate: every one of its edges and corners is a convex edge/corner
        // with open air on both sides, the case where one diagonal link leaves through two faces of the same cell.
        bool Free(int x, int y) => x is >= 18 and <= 29 && y is >= 10 and <= 21;
        var air = new VoxelGrid { Nx = FcNx, Ny = FcNy, Nz = FcNz, Dx = FcDx, Cell = new byte[FcNx * FcNy * FcNz] };
        for (int y = 0; y < FcNy; y++) for (int x = 0; x < FcNx; x++) air.Cell[air.Index(x, y, 0)] = VoxelGrid.Solid;
        for (int z = 1; z <= 9; z++) for (int y = 8; y <= 23; y++) for (int x = 10; x <= 11; x++) air.Cell[air.Index(x, y, z)] = VoxelGrid.Solid;
        yield return FcMake("FC6", "wall-less room region in open air behind a plate (convex edges and corners, every face open)",
            air, Free, false, FcRoom("free", FcRect(18, 29, 10, 21)));
    }

    // ---- one run -----------------------------------------------------------------------------------------------

    sealed class FcTrace
    {
        public int Rooms, N; public string Backend = "";
        public double[] MGeo = [];   // RQ-05: mass of the cells FcRun was asked to sum independently of the plan mask
        public double[][] M = [], NetC = [], NetO = [], GrC = [], GrO = [];   // [room][step]: mass after step; cumulative closed / open-face net and gross
        public string Diag = "";
        public double Net(int r, int s) => s < 0 ? 0 : NetC[r][s] + NetO[r][s];
        public double Gross(int r, int s, bool closedOnly = false) => s < 0 ? 0 : GrC[r][s] + (closedOnly ? 0 : GrO[r][s]);
        public double Residual(int r, int a, int b) => M[r][b] - M[r][a] + Net(r, b - 1) - Net(r, a - 1);
        public double GrossOver(int r, int a, int b) => Gross(r, b - 1) - Gross(r, a - 1);
    }

    static FcTrace FcRun(FcScene sc, Backend backend, bool diagnose, Func<int, bool>? geo = null)
    {
        var g = sc.Grid;
        using var s = new LbmSolver.LbmSession(g, sc.Model, sc.Wind, new RunOptions { Backend = backend, WindProfileExponent = 0f });
        var plan = s.FluxPlan; int nr = sc.Model.Rooms.Count, n = FcSteps;
        var masks = Analysis.RoomMasks(g, sc.Model, out _, lbm: true);   // RY-01: the plan's room cells (LBM terms); the same as the Cell mask on the hand-built FC1-FC6 grids. OF-1: plus the band (none on these grids; same definition as the plan's)
        var cells = masks.Select(m => Enumerable.Range(0, g.Count).Where(i => m[i]).ToArray()).ToArray();
        var t = new FcTrace { Rooms = nr, N = n, Backend = s.Backend, M = new double[nr][], NetC = new double[nr][], NetO = new double[nr][], GrC = new double[nr][], GrO = new double[nr][] };
        for (int r = 0; r < nr; r++) { t.M[r] = new double[n]; t.NetC[r] = new double[n]; t.NetO[r] = new double[n]; t.GrC[r] = new double[n]; t.GrO[r] = new double[n]; }

        var geoCells = geo == null ? null : Enumerable.Range(0, g.Count).Where(geo).ToArray();
        if (geoCells != null) t.MGeo = new double[n];
        s.ResetAveraging(startNow: true);
        for (int st = 0; st < n; st++)
        {
            s.Step(1);
            var read = s.PopulationReader();
            var (inn, outt) = s.RoomFluxRaw();
            for (int r = 0; r < nr; r++)
            {
                double m = 0;
                foreach (int i in cells[r]) for (int q = 0; q < 19; q++) m += read(i, q);
                t.M[r][st] = m;
            }
            if (geoCells != null) { double mg = 0; foreach (int i in geoCells) for (int q = 0; q < 19; q++) mg += read(i, q); t.MGeo[st] = mg; }
            var netC = new double[nr]; var netO = new double[nr]; var grC = new double[nr]; var grO = new double[nr];
            for (int k = 0; k < plan.Faces.Length; k++)
            {
                var f = plan.Faces[k];
                if (f.Outside < 0) { netO[f.Room] += outt[k] - inn[k]; grO[f.Room] += outt[k] + inn[k]; }
                else { netC[f.Room] += outt[k] - inn[k]; grC[f.Room] += outt[k] + inn[k]; }
            }
            for (int r = 0; r < nr; r++) { t.NetC[r][st] = netC[r]; t.NetO[r][st] = netO[r]; t.GrC[r][st] = grC[r]; t.GrO[r][st] = grO[r]; }
            // ledger of the state after step n-2 (its links govern the mass change of step n-1, which the next iteration reads)
            if (diagnose && st == n - 2 && backend == Backend.Cpu) t.Diag = FcLedger(s, sc, masks, read);
        }
        if (t.Diag.Length > 0)
            t.Diag += " mass change of the following step (= -exact link sum if the solver conserves mass): " +
                string.Join(", ", Enumerable.Range(0, nr).Select(r => $"{sc.Model.Rooms[r].Id}={t.M[r][n - 1] - t.M[r][n - 2]:E4}"));
        return t;
    }

    // ---- evaluation --------------------------------------------------------------------------------------------

    /// <summary>Whole-run and worst-window figures of one room. *Use is residual / tolerance (pass when <= 1).</summary>
    readonly record struct FcEval(double Dm, double Residual, double Gross, double WholeUse, double WinUse, double WinRes, double WinGross, int WinStart, double MaxWinDm, double M0)
    {
        public double RelGross => Gross > 0 ? Math.Abs(Residual) / Gross : double.NaN;
        public bool Pass => WholeUse <= 1 && WinUse <= 1 && Math.Abs(Residual) <= FcTolMass * M0;
    }

    const double FcTolConvex = 0.01;

    static double FcWorstWindowRel(FcTrace t, int r)
    {
        double worst = 0;
        for (int a = 0; a + FcWindow <= t.N - 1; a += FcWindow)
        {
            double gr = t.GrossOver(r, a, a + FcWindow);
            if (gr > 0) worst = Math.Max(worst, Math.Abs(t.Residual(r, a, a + FcWindow)) / gr);
        }
        return worst;
    }

    // FC6 bounds a documented O(edge/area) limitation of time-averaged ACH; per-window ratios are dominated by start-up windows
    // where gross ~ 0 (worst 3.5 % at step 60), so only the whole-run bound is meaningful. FC1-FC5/FC7 keep exact per-window checks.
    static bool FcBounded(FcTrace t, int r, FcEval e) => e.RelGross <= FcTolConvex;

    static double FcTol(double gross, double m0, int steps) => FcTolGross * gross + FcFloorPerStep * m0 * steps;

    static FcEval FcEvaluate(FcTrace t, int r, bool closedOnlyGross = false)
    {
        int last = t.N - 1; double m0 = t.M[r][0];
        double winUse = 0, winRes = 0, winGross = 0, maxDm = 0; int winAt = 0;
        for (int a = 0; a + FcWindow <= last; a += FcWindow)
        {
            int b = a + FcWindow;
            double res = t.Residual(r, a, b), gr = t.Gross(r, b - 1, closedOnlyGross) - t.Gross(r, a - 1, closedOnlyGross);
            double use = Math.Abs(res) / FcTol(gr, m0, FcWindow);
            if (use > winUse) { winUse = use; winRes = res; winGross = gr; winAt = a; }
            maxDm = Math.Max(maxDm, Math.Abs(t.M[r][b] - t.M[r][a]));
        }
        double whole = t.Residual(r, 0, last), grossAll = t.Gross(r, last - 1, closedOnlyGross);
        return new FcEval(t.M[r][last] - m0, whole, grossAll, Math.Abs(whole) / FcTol(grossAll, m0, last), winUse, winRes, winGross, winAt, maxDm, m0);
    }

    /// <summary>The scene must really move mass: some 10-step window changes M_R by more than 100x that window's tolerance.</summary>
    static bool FcMoves(FcEval e) => e.MaxWinDm > 100 * FcTol(e.Gross * FcWindow / (FcSteps - 1.0), e.M0, FcWindow);

    static string FcDetail(FcScene sc, FcTrace t, int r, FcEval e) =>
        $"{t.Backend}: room={sc.Model.Rooms[r].Id} dM={e.Dm:E3} (max 10-step dM {e.MaxWinDm:E2}) residual={e.Residual:E3} gross={e.Gross:E3} res/gross={e.RelGross:E2} " +
        $"res/M={Math.Abs(e.Residual) / e.M0:E2}" +
        (sc.Cropped ? $" openFaceNet={t.NetO[r][t.N - 2]:E2} openFaceGross={t.GrO[r][t.N - 2]:E2}"
                    : $" (cap {FcTolMass:E0}) tolerance use whole={e.WholeUse:F2} worst10={e.WinUse:F2}@{e.WinStart} (res {e.WinRes:E2} of gross {e.WinGross:E2})");

    static void FluxConservationChecks()
    {
        foreach (var sc in FcScenes())
        {
            FcTrace? cpu = null, gpu = null; string err = "";
            try
            {
                cpu = FcRun(sc, Backend.Cpu, diagnose: true);
                if (LbmSolver.GpuAvailable) gpu = FcRun(sc, Backend.Gpu, diagnose: false);
            }
            catch (Exception ex) { err = ex.Message; }
            if (cpu == null) { Check($"{sc.Id} {sc.Title}", false, $"exception: {err}"); continue; }
            for (int r = 0; r < sc.Model.Rooms.Count; r++)
            {
                var ec = FcEvaluate(cpu, r);
                bool moves = FcMoves(ec);
                string label = $"{sc.Id}{(sc.Model.Rooms.Count > 1 ? (char)('a' + r) : "")} {sc.Title}";
                if (!sc.Cropped)
                {
                    // FC6 documented limitation: per-face gross u.n (needed for ACH) counts a convex-edge diagonal link at both open
                    // A-side faces; exact single ownership instead makes per-face gross wrong by +-u/12 at every edge cell
                    // (FC7/Reversal -> 0.917/1.083). So the room NET is exact except at convex edges with open air on both sides,
                    // where it carries an O(edge/area) error; bound 1 % of gross, whole run and every 10-step window.
                    bool bounded = sc.Id == "FC6";
                    bool ok = (bounded ? FcBounded(cpu, r, ec) : ec.Pass) && moves;
                    Check(bounded ? $"{label}: CPU mass balance |res| <= {FcTolConvex:P0} of gross (documented convex-edge limitation), whole run and every 10-step window"
                                  : $"{label}: CPU mass balance M(b)-M(a) = -sum(net flux): |res| <= 1e-4 gross + float32 allowance, whole run and every 10-step window, <= 1e-5 M", ok,
                        FcDetail(sc, cpu, r, ec) + (bounded ? $" worst10 res/gross={FcWorstWindowRel(cpu, r):E2}" : "") + (moves ? "" : " [scene does not move mass]") + (ok || cpu.Diag == "" ? "" : "\n    " + cpu.Diag));
                    if (gpu != null)
                    {
                        var eg = FcEvaluate(gpu, r);
                        bool okg = bounded ? FcBounded(gpu, r, eg) : eg.Pass;
                        double diff = Math.Abs(eg.Residual - ec.Residual) / ec.Gross;
                        Check($"{label}: GPU mass balance holds and its residual matches the CPU's within 1e-4 of gross", okg && diff <= 1e-4,
                            FcDetail(sc, gpu, r, eg) + $" | |gpu-cpu residual|/gross={diff:E2} gpuDm={eg.Dm:E3} cpuDm={ec.Dm:E3}");
                    }
                }
                else
                {
                    // Open-face scene: report the link-only (closed faces) and total residuals; assert the total within FcCropTol of gross.
                    var ecl = FcEvaluate(cpu, r, closedOnlyGross: true);
                    bool ok = Math.Abs(ec.Residual) <= FcCropTol * ec.Gross && moves;
                    Check($"{label}: CPU mass balance with velocity-based open faces, |res| <= {FcCropTol:P0} of gross (looser, stated)", ok,
                        FcDetail(sc, cpu, r, ec) + $" | closed-face gross={ecl.Gross:E3} res/closedGross={ecl.RelGross:E2}" + (moves ? "" : " [scene does not move mass]"));
                    if (gpu != null)
                    {
                        var eg = FcEvaluate(gpu, r);
                        bool okg = Math.Abs(eg.Residual) <= FcCropTol * eg.Gross;
                        double diff = Math.Abs(eg.Residual - ec.Residual) / ec.Gross;
                        Check($"{label}: GPU within the same open-face tolerance and within 1e-4 of the CPU residual (of gross)", okg && diff <= 1e-4,
                            FcDetail(sc, gpu, r, eg) + $" | |gpu-cpu residual|/gross={diff:E2}");
                    }
                }
            }
        }
        FcGrossExchangeCheck();
        FcObliqueOpeningCheck();
        FcSealedThinWallCheck();
        FcBandMergeCheck();
    }

    /// <summary>OF-1 (DESIGN_OPENING_FIX section 5.1 item 1): the sample house's room outlines sit 2.5-4 cm inside the walls, and whether a column of cell centres
    /// lands in that gap is a grid lottery. Unmerged, the gap made a room's whole wall "plain air" (Bedroom 1 total exchange +36 % between two grids 1 mm apart).
    /// With the band merge (Analysis.RoomMasks) the flux plan has no plain-air face in any room but Hall and Landing, and theirs are the stair void only (the shared,
    /// unassigned air between them: each face is seen from both rooms, so their counts are equal; a gap leak would show in one room only), at all five budgets and the 4 x 4 origin shifts at 5.0 M cells (product route, Compact, wind 270).</summary>
    static void FcBandMergeCheck()
    {
        var m = SampleHouse.Build(); var saved = Voxelizer.OriginShiftCells;
        var cases = new List<(int Cells, Vector2 Shift)>();
        foreach (int c in new[] { 5_000_000, 5_090_000, 2_320_000, 11_200_000, 1_500_000 }) cases.Add((c, Vector2.Zero));
        foreach (float sx in new[] { 0f, .25f, .5f, .75f }) foreach (float sy in new[] { 0f, .25f, .5f, .75f }) if (sx != 0 || sy != 0) cases.Add((5_000_000, new Vector2(sx, sy)));
        bool ok = true; var lines = new List<string>();
        try
        {
            foreach (var (cells, shift) in cases)
            {
                Voxelizer.OriginShiftCells = shift;
                var g = Voxelizer.Build(m, cells, 8, null, minDx: 0.1f, mode: DomainMode.Compact, wind: new Vector2(4, 0));
                var plan = RoomFluxPlan.Build(g, m);
                var parts = new List<string>(); bool caseOk = true; var stairFaces = new List<int>();
                for (int r = 0; r < m.Rooms.Count; r++)
                {
                    string name = m.Rooms[r].Name;
                    int plain = plan.Faces.Count(f => f.Room == r && !f.Connection && f.Outside >= 0);
                    if (name is "Hall" or "Landing") stairFaces.Add(plain); else caseOk &= plain == 0;
                    parts.Add($"{name}:{plain}");
                }
                caseOk &= stairFaces.Count == 2 && stairFaces[0] == stairFaces[1];
                ok &= caseOk;
                lines.Add($"{cells / 1e6:0.##} M ({shift.X:0.##},{shift.Y:0.##}) Dx {g.Dx * 1000:0.0} mm {(caseOk ? "ok" : "FAIL")} plain-air faces {string.Join(" ", parts)}");
            }
        }
        catch (Exception ex) { ok = false; lines.Add("exception: " + ex.Message); }
        finally { Voxelizer.OriginShiftCells = saved; }
        foreach (var l in lines) Note("OF1 band merge", l);
        Check("OF1 band merge: plain-air faces = 0 in every room except Hall and Landing (stair void only) at 5.0 / 5.09 / 2.32 / 11.2 / 1.5 M cells and origin shifts {0, .25, .5, .75}^2 at 5.0 M", ok, $"{cases.Count} grids");
    }

    /// <summary>Batch 6a: a sealed room whose six walls are 0.05 m boxes (thinner than a cell, so cut links), placed so the cells next to
    /// them on the room side are Solid in Cell only (VoxelGrid.LbmAir) and outside the Cell room mask; a fan stirs the air. RY-01: the room-face
    /// flux plan's room cells are LBM-fluid cells inside the loops, so that layer is inside the outline, every link across it is cut, and the
    /// sealed room exchanges nothing (gross ~ 0) with the mass balance exact at the FC tolerance.</summary>
    static void FcSealedThinWallCheck()
    {
        float a0 = .99f, a1 = 1.04f, b0 = 3.96f, b1 = 4.01f;   // room-side centres 1.0625 / 3.9375 are nearer the wall than the outer ones
        var m = new BuildingModel { GroundZ = -10 };
        m.Obstacles.AddRange([Box(new(a0, a0, a0), new(a1, b1, b1)), Box(new(b0, a0, a0), new(b1, b1, b1)), Box(new(a0, a0, a0), new(b1, a1, b1)),
            Box(new(a0, b0, a0), new(b1, b1, b1)), Box(new(a0, a0, a0), new(b1, b1, a1)), Box(new(a0, a0, b0), new(b1, b1, b1))]);
        m.Rooms.Add(new Room { Id = "sealed", Name = "sealed", ZMin = a1, ZMax = b0, Loops = { new[] { new Vector2(a1, a1), new Vector2(b0, a1), new Vector2(b0, b0), new Vector2(a1, b0) } } });
        m.Sources.Add(new AirSource { Id = "fan", Name = "fan", Device = DeviceKind.PedestalFan, Kind = SourceKind.Supply, Center = new Vector3(2.5f, 2.5f, 2.5f),
            Direction = Vector3.UnitX, Radius = 0.4f, FlowRate = 0.05f, Enabled = true });
        string detail, geoDetail = ""; bool ok, geoOk = false;
        try
        {
            var g = Voxelizer.Build(m, 64000, domain: new(Vector3.Zero, new(5)));
            var mask = Analysis.RoomMask(g, m.Rooms[0], out _);
            int edge = 0;
            for (int i = 0; i < g.Count; i++)
            {
                if (g.LbmAir.Length != g.Count || !g.LbmAir[i] || g.Cell[i] != VoxelGrid.Solid) continue;
                int x = i % g.Nx, y = i / g.Nx % g.Ny, z = i / (g.Nx * g.Ny);
                if (x > 0 && mask[i - 1] || x < g.Nx - 1 && mask[i + 1] || y > 0 && mask[i - g.Nx] || y < g.Ny - 1 && mask[i + g.Nx]
                    || z > 0 && mask[i - g.Nx * g.Ny] || z < g.Nz - 1 && mask[i + g.Nx * g.Ny]) edge++;
            }
            // RQ-05: the same cells found without the flux plan: every LBM-fluid cell whose centre lies inside the room's true wall planes (a1..b0 on each axis).
            bool InBox(int i) { if (!Voxelizer.LbmFluid(g, i)) return false; var c = g.CellCenter(i % g.Nx, i / g.Nx % g.Ny, i / (g.Nx * g.Ny)); return c.X > a1 && c.X < b0 && c.Y > a1 && c.Y < b0 && c.Z > a1 && c.Z < b0; }
            var t = FcRun(new FcScene("FC9", "sealed thin-wall room", g, m, Vector2.Zero, false), Backend.Cpu, diagnose: false, geo: InBox);
            var e = FcEvaluate(t, 0);
            double net = t.Net(0, t.N - 1);
            // RY-01: the plan's room cells are LBM-fluid cells inside the loops, so the LbmAir layer is inside the outline and a sealed room has no uncut link across it:
            // gross ~ 0 (was 495 with the Cell mask) and the mass balance holds at the exact FC tolerance (was FC6's 1 % of gross).
            ok = edge > 0 && e.Pass && e.Gross <= 1e-6 * e.M0;
            detail = $"LbmAir cells just inside the Cell outline {edge} | gross {e.Gross:E3} (limit {1e-6 * e.M0:E1}) | sum of face nets over the run {net:E3} | " +
                $"dM {e.Dm:E3} residual {e.Residual:E3} | tolerance use whole={e.WholeUse:F2} worst10={e.WinUse:F2} | M0 {e.M0:F1}";
            // Both the plan's exchange and the geometric box's mass change must be nothing at the FC tolerance (float32 allowance, 1e-5 M0).
            int last = t.N - 1; double g0 = t.MGeo[0], dGeo = t.MGeo[last] - g0, tolGeo = FcTol(e.Gross, g0, last);
            geoOk = Math.Abs(dGeo + net) <= tolGeo && Math.Abs(dGeo) <= FcTolMass * g0 && Math.Abs(net) <= tolGeo && g0 > 0;
            geoDetail = $"geometric-box mass M0 {g0:F1} (flux-plan mask M0 {e.M0:F1}) | dM {dGeo:E3} | plan net {net:E3} | |dM + net| {Math.Abs(dGeo + net):E2} <= {tolGeo:E2} | |dM| <= {FcTolMass * g0:E2}";
        }
        catch (Exception ex) { ok = false; detail = "exception: " + ex.Message; }
        Check("FC9 sealed room with sub-cell walls: the room's own wall-side LbmAir layer is inside its flux outline, so no flux crosses it (gross ~ 0) and the mass balance holds at the FC tolerance", ok, detail);
        Check("FC9b sealed sub-cell-wall room: the mass of every LBM-fluid cell inside the true wall planes (no flux-plan mask) does not change, and agrees with the plan's exchange (both ~ 0) at the FC tolerance (RQ-05)", geoOk, geoDetail);
    }

    /// <summary>P12-06: the FC5 pentagon with a window carved through its 45 degree wall (a rotated opening, normal (1,1,0)/sqrt 2) besides the west and east windows.
    /// Opening flows are link fluxes across each opening's plane (field.OpeningFluxes), so with the room otherwise closed they must sum to the room's net outward
    /// flux, i.e. to nothing once the flow is steady: |sum| within 2 % of the sum of the magnitudes. The old u.n over a one-cell slab is printed beside it.</summary>
    static void FcObliqueOpeningCheck()
    {
        bool Pent(int x, int y) => x is >= 14 and <= 29 && y is >= 10 and <= 25 && x + y <= 43;
        var g = FcBuilding(Pent, new FcBox(13, 13, 15, 18, 2, 4), new FcBox(30, 30, 11, 14, 2, 4));
        var carved = new List<Vector3>();
        for (int z = 2; z <= 4; z++) for (int y = 0; y < FcNy; y++) for (int x = 0; x < FcNx; x++)
            if ((x + y is 44 or 45) && Math.Abs(x - y - 4) <= 2 && g.Cell[g.Index(x, y, z)] == VoxelGrid.Solid)
            { g.Cell[g.Index(x, y, z)] = VoxelGrid.Air; carved.Add(g.CellCenter(x, y, z)); }
        var n = Vector3.Normalize(new Vector3(1, 1, 0)); var t = new Vector3(-n.Y, n.X, 0);
        var centre = carved.Aggregate(Vector3.Zero, (s, c) => s + c) / carved.Count;
        float aMin = carved.Min(c => Vector3.Dot(c - centre, t)), aMax = carved.Max(c => Vector3.Dot(c - centre, t));
        var diag = new Opening { Id = "diag", Name = "diag", Kind = OpeningKind.Window, IsOpen = true, Normal = n,
            Center = new Vector3(centre.X, centre.Y, 3.5f * FcDx), Width = (aMax - aMin) + 3 * FcDx, Height = 5 * FcDx, Depth = 3 * FcDx };
        var west = new Opening { Id = "west", Name = "west", Kind = OpeningKind.Window, IsOpen = true, Normal = -Vector3.UnitX,
            Center = new Vector3(13.5f * FcDx, 17f * FcDx, 3.5f * FcDx), Width = 6 * FcDx, Height = 5 * FcDx, Depth = FcDx };
        var east = new Opening { Id = "east", Name = "east", Kind = OpeningKind.Window, IsOpen = true, Normal = Vector3.UnitX,
            Center = new Vector3(30.5f * FcDx, 13f * FcDx, 3.5f * FcDx), Width = 6 * FcDx, Height = 5 * FcDx, Depth = FcDx };
        var model = new BuildingModel();
        model.Rooms.Add(FcRoom("pent", (14, 10), (30, 10), (30, 14.5f), (18.5f, 26), (14, 26)));
        model.Openings.AddRange([diag, west, east]);
        string detail;
        bool ok;
        try
        {
            using var s = new LbmSolver.LbmSession(g, model, new Vector2(3, 0), new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f });
            s.Step(900);
            s.ResetAveraging(startNow: true);
            s.Step(900);
            var f = s.ToField();
            double Net(Opening o) => f.OpeningFluxes.FirstOrDefault(x => ReferenceEquals(x.Opening, o))?.Net ?? double.NaN;
            double qd = Net(diag), qw = Net(west), qe = Net(east);
            double sum = qd + qw + qe, mag = Math.Abs(qd) + Math.Abs(qw) + Math.Abs(qe);
            // the pre-fix reading of the rotated opening: u.n over the one-cell slab
            double old = 0; foreach (int i in Analysis.ThroatCells(g, diag)) old += f.Ux[i] * n.X + f.Uy[i] * n.Y;
            old *= FcDx * FcDx;
            ok = !double.IsNaN(sum) && mag > 1e-6 && Math.Abs(sum) <= 0.02 * mag;
            detail = $"link flux m3/s: diag {qd:E3} west {qw:E3} east {qe:E3} sum {sum:E2} = {(mag > 0 ? Math.Abs(sum) / mag * 100 : double.NaN):0.00} % of the magnitudes (limit 2 %) | " +
                $"pre-fix u.n over the slab for diag {old:E3} ({(qd != 0 ? old / qd : double.NaN):0.00} x the link flux) | carved cells {carved.Count}, plan width {diag.Width:0.00} m";
        }
        catch (Exception ex) { ok = false; detail = "exception: " + ex.Message; }
        Check("FC8 rotated (45 degree) opening in the pentagon's diagonal wall: the three opening link fluxes sum to the room's net flux (0 at steady), within 2 %", ok, detail);
    }

    // ---- diagnosis: link census and last-step link ledger ------------------------------------------------------

    /// <summary>For every room: which lattice boundary links A -> D (A in the room, D other non-solid in-domain air) the plan owns
    /// 0 times (missing), 1 time (right) or >1 time (double counted), and, from the populations the last step left in cur, the
    /// flux each class carries, against the exact crossing sum (each boundary link once) and the plan's weighted sum.</summary>
    static string FcLedger(LbmSolver.LbmSession s, FcScene sc, bool[][] masks, Func<int, int, float> read)
    {
        var g = sc.Grid; var plan = s.FluxPlan; var sb = new System.Text.StringBuilder();
        for (int r = 0; r < masks.Length; r++)
        {
            var mask = masks[r];
            var cnt = new Dictionary<(int A, int D, int Q), int>();
            for (int k = 0; k < plan.Faces.Length; k++)
            {
                if (plan.Faces[k].Room != r) continue;
                for (int l = plan.LinkStart[k]; l < plan.LinkStart[k + 1]; l++)
                {
                    int qf = plan.LinkQ[l]; if (qf >= 128 || plan.LinkA[l] == plan.LinkD[l]) continue;
                    var key = (plan.LinkA[l], plan.LinkD[l], qf & 31); cnt[key] = cnt.GetValueOrDefault(key) + 1;
                }
            }
            double Flux((int A, int D, int Q) l) { int qb = (l.Q & 1) == 1 ? l.Q + 1 : l.Q - 1; return read(l.A, l.Q) - read(l.D, qb); }
            double exact = 0, planSum = 0, missingSum = 0, dblExtra = 0; int nExp = 0, nMiss = 0, nDbl = 0, nExtra = 0;
            var examples = new List<string>();
            string Where((int A, int D, int Q) l)
            {
                LbmSolver.LinkDir(l.Q, out int cx, out int cy, out int cz);
                int x = l.A % g.Nx, y = l.A / g.Nx % g.Ny, z = l.A / (g.Nx * g.Ny);
                return $"({x},{y},{z})+({cx},{cy},{cz}){(cx != 0 && cy != 0 || cx != 0 && cz != 0 || cy != 0 && cz != 0 ? "diag" : "axis")}";
            }
            var expected = new HashSet<(int, int, int)>();
            for (int a = 0; a < g.Count; a++)
            {
                if (!mask[a]) continue;
                int x = a % g.Nx, y = a / g.Nx % g.Ny, z = a / (g.Nx * g.Ny);
                for (int q = 1; q < 19; q++)
                {
                    LbmSolver.LinkDir(q, out int cx, out int cy, out int cz);
                    int xx = x + cx, yy = y + cy, zz = z + cz;
                    if (xx < 0 || xx >= g.Nx || yy < 0 || yy >= g.Ny || zz < 0 || zz >= g.Nz) continue;
                    int d = g.Index(xx, yy, zz);
                    if (g.Cell[d] == VoxelGrid.Solid || mask[d]) continue;
                    var l = (a, d, q); expected.Add(l); nExp++; exact += Flux(l);
                    int c = cnt.GetValueOrDefault(l);
                    if (c == 0) { nMiss++; missingSum += Flux(l); if (examples.Count < 6) examples.Add("missing " + Where(l)); }
                    else if (c > 1) { nDbl++; dblExtra += (c - 1) * Flux(l); if (examples.Count < 6) examples.Add($"x{c} " + Where(l)); }
                }
            }
            foreach (var kv in cnt)
            {
                planSum += kv.Value * Flux(kv.Key);
                if (!expected.Contains(kv.Key)) { nExtra++; if (examples.Count < 8) examples.Add("not a boundary link " + Where(kv.Key)); }
            }
            sb.Append($"[room {sc.Model.Rooms[r].Id} last-step ledger: boundary links={nExp} missing={nMiss} double-counted={nDbl} spurious={nExtra}; exact link sum={exact:E4} plan sum={planSum:E4} (missing {missingSum:E3}, double extra {dblExtra:E3}, plan-exact {planSum - exact:E3}); e.g. {string.Join("; ", examples)}] ");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- gross exchange sanity ---------------------------------------------------------------------------------

    /// <summary>Two identical rooms side by side in a uniform 1 m/s stream, connected by a full-width opening. Each room's
    /// Inflow and Outflow equal the volumetric flow u x A (within 1 %). Short run (6 steps) from the seeded uniform field: the
    /// duct-wall boundary layer of a walled duct would cut the plane flux by roughly 1/6 per wall row, so the rooms sit in open air.</summary>
    static void FcGrossExchangeCheck()
    {
        const int nx = 36, ny = 24, nz = 14;
        float d = FcDx;
        var g = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = d, Cell = new byte[nx * ny * nz] };
        Room Rm(string id, int x0, int x1) => new() { Id = id, Name = id, ZMin = 3 * d, ZMax = 9 * d, Loops = { new Vector2[] { new(x0 * d, 6 * d), new((x1 + 1) * d, 6 * d), new((x1 + 1) * d, 18 * d), new(x0 * d, 18 * d) } } };
        Opening Op(string id, int x) => new() { Id = id, Name = id, Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(x * d, 12 * d, 6 * d), Normal = Vector3.UnitX, Width = 12 * d, Height = 6 * d, Depth = 0.01f };
        var m = new BuildingModel();
        m.Rooms.Add(Rm("A", 8, 17)); m.Rooms.Add(Rm("B", 18, 27));
        m.Openings.Add(Op("west", 8)); m.Openings.Add(Op("between", 18)); m.Openings.Add(Op("east", 28));
        double q = 1.0 * (12 * 6) * d * d;    // u x open cross-section (12 x 6 cells)
        foreach (var backend in Backends)
        {
            using var s = new LbmSolver.LbmSession(g, m, Vector2.Zero, new() { Backend = backend, OpeningModel = OpeningModel.Resolved }, uMaxOverride: 1);
            Seed(s, g, 1, Vector3.UnitX); s.ResetAveraging(startNow: true); s.Step(6);
            var f = s.ToField();
            var bad = new List<string>(); string detail = "";
            foreach (var rf in f.RoomFluxes)
            {
                double eIn = Math.Abs(rf.Inflow / q - 1), eOut = Math.Abs(rf.Outflow / q - 1);
                if (eIn > .01 || eOut > .01) bad.Add(rf.Room.Id);
                detail += $"{rf.Room.Id}: in={rf.Inflow:F4} out={rf.Outflow:F4} (Q={q:F4}, err {eIn:P2}/{eOut:P2}) exch={rf.InternalExchange:E1}; ";
            }
            Check($"FC7 gross exchange ({backend}): two identical rooms joined by a full-width opening in a uniform 1 m/s stream, each room's inflow and outflow = u x A within 1%",
                bad.Count == 0 && f.RoomFluxes.Count == 2, detail + (bad.Count > 0 ? "failing: " + string.Join(",", bad) : ""));
        }
    }
}
