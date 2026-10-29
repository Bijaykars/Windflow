using System.Numerics;
using System.Reflection;
using ILGPU.Runtime;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static partial class FixChecks
{
    static VoxelGrid Cube(int n = 8) => new() { Nx = n, Ny = n, Nz = n, Dx = Dx, Cell = new byte[n * n * n] };
    static Room RoomOf(float lo, float hi, float zLo = 0, float zHi = 1) => new() { Id = "r", ZMin = zLo, ZMax = zHi, Loops = { new Vector2[] { new(lo, lo), new(hi, lo), new(hi, hi), new(lo, hi) } } };
    static IEnumerable<Backend> Backends => LbmSolver.GpuAvailable ? new[] { Backend.Cpu, Backend.Gpu } : new[] { Backend.Cpu };

    static FlowField Uniform(VoxelGrid g, Vector3 u)
    {
        var f = new FlowField { Grid = g, Ux = new float[g.Count], Uy = new float[g.Count], Uz = new float[g.Count], P = new float[g.Count] };
        Array.Fill(f.Ux, u.X); Array.Fill(f.Uy, u.Y); Array.Fill(f.Uz, u.Z);
        return f;
    }

    static void Seed(LbmSolver.LbmSession s, VoxelGrid g, float rho, Vector3 velocity)
    {
        int[] cx = [0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0];
        int[] cy = [0, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 0, 0, 1, -1, 1, -1];
        int[] cz = [0, 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, -1, 1, 1, -1, -1, 1];
        velocity *= s.VelocityScale; var f = new float[g.Count * 19];
        for (int i = 0; i < g.Count; i++) for (int q = 0; q < 19; q++)
        {
            float w = q == 0 ? 1f / 3 : q <= 6 ? 1f / 18 : 1f / 36; float cu = cx[q] * velocity.X + cy[q] * velocity.Y + cz[q] * velocity.Z;
            f[s.NestGpu ? q * g.Count + i : 19 * i + q] = w * rho * (1 + 3 * cu + 4.5f * cu * cu - 1.5f * velocity.LengthSquared());
        }
        if (s.NestGpu) { s.NestDCur!.View.CopyFromCPU(f); s.NestDNxt!.View.CopyFromCPU(f); }
        else { f.CopyTo(s.NestCur, 0); f.CopyTo(s.NestNxt, 0); }
    }

    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    static void SolverChecks()
    {
        RoomFluxFaceChecks();
        AchVolumeChecks();
        OrificeHeadChecks();
        OpenZFaceChecks();
        WallLinkChannelChecks();
        ReopenChecks();
        LiveUpdateChecks();
        ConvergeLeadChecks();
        MeanAliasingChecks();
        SurfaceBoxCheck();
    }

    // ---- RX-04: a Surface box that holds no face warns and is left out of the dT scaling ----------------------

    static void SurfaceBoxCheck()
    {
        // 8^3 cells of 0.125 m, a two-cell slab on top (z 0.75..1.0 m). Box A spans the air layer under the slab only (the natural "chilled ceiling" drawing); box B reaches into the slab.
        var g = Cube();
        for (int z = 6; z < 8; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) g.Cell[g.Index(x, y, z)] = VoxelGrid.Solid;
        var thermal = typeof(LbmSolver).GetMethod("BuildThermal", BindingFlags.NonPublic | BindingFlags.Static)!;
        (HashSet<HeatSource> Unheld, int Fixed, List<string> Notes) Probe(float centreZ, float height)
        {
            var gg = Cube(); Array.Copy(g.Cell, gg.Cell, g.Cell.Length);
            var m = new BuildingModel();
            m.Obstacles.Add(new Mesh { Id = "dummy", Vertices = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], Indices = [0, 1, 2, 0, 2, 3] });   // PlanBounds needs a footprint
            var h = new HeatSource { Id = "s", Name = "ceiling", Kind = HeatKind.Surface, Center = new Vector3(0.5f, 0.5f, centreZ), Width = 1f, Depth = 1f, Height = height, SurfaceTemperature = 18f };
            m.Heat.Add(h);
            var r = (System.Runtime.CompilerServices.ITuple)thermal.Invoke(null, [gg, m, false])!;
            return ((HashSet<HeatSource>)r[5]!, ((int[])r[2]!).Count(b => b != 0), gg.Warnings.Where(w => w.StartsWith("Surface ")).ToList());
        }
        var air = Probe(0.6875f, 0.125f); var wall = Probe(0.75f, 0.25f);
        Check("RX-04: a Surface box on the air side of the slab fixes no face, warns, and is left out of the dT scaling; a box reaching into the slab fixes faces without a warning",
            air.Fixed == 0 && air.Unheld.Count == 1 && air.Notes.Count == 1 && air.Notes[0].Contains("nothing is held at 18") && wall.Fixed > 0 && wall.Unheld.Count == 0 && wall.Notes.Count == 0,
            $"air-side box: {air.Fixed} cells fixed, unheld {air.Unheld.Count}, note: {(air.Notes.Count > 0 ? air.Notes[0] : "none")} | slab box: {wall.Fixed} cells fixed, notes {wall.Notes.Count}");
    }

    // ---- #12 + net flux: room flux through faces of the domain ------------------------------------------------

    static void RoomFluxFaceChecks()
    {
        // Domain-sized 1 m cube room, uniform +-1 m/s on each axis. Z faces only count where the grid says they are
        // open (OpenZMin/Max); a solid ground or reflecting lid is not exchange.
        var bad = new List<string>();
        foreach (var (axis, dir) in new[] { ("x", Vector3.UnitX), ("y", Vector3.UnitY), ("z", Vector3.UnitZ) })
        foreach (float sign in new[] { 1f, -1f })
        {
            var g = Cube(); g.OpenZMin = g.OpenZMax = true;
            var r = Analysis.Analyze(new BuildingModel { Rooms = { RoomOf(0, 1) } }, Uniform(g, dir * sign), Vector2.UnitX, new());
            var room = r.Rooms.Single();
            bool ok = Math.Abs(room.Inflow - 1) < 1e-5 && Math.Abs(room.Outflow - 1) < 1e-5 && Math.Abs(room.NetFlux) < 1e-5 && Math.Abs(room.Ach - 3600) < .05;
            if (!ok) bad.Add($"{(sign > 0 ? "+" : "-")}{axis}: in={room.Inflow} out={room.Outflow} ach={room.Ach}");
        }
        Check("F12a domain-sized room: +/-1 m/s on each axis gives Inflow = Outflow = 1 m3/s (ACH 3600) and zero net flux", bad.Count == 0, string.Join("; ", bad));

        var gc = Cube();   // closed Z faces: reflecting ground/lid are not exchange
        var rc = Analysis.Analyze(new BuildingModel { Rooms = { RoomOf(0, 1) } }, Uniform(gc, Vector3.UnitZ), Vector2.UnitX, new()).Rooms.Single();
        Check("F12b closed Z faces (no OpenZ) contribute no exchange", rc.Inflow == 0 && rc.Outflow == 0, $"in={rc.Inflow} out={rc.Outflow}");

        // Partial crop: a room touching only the x = 0 domain face (0.25 m2 of it), clear of the other faces.
        // +X flow enters through that face (Inflow) and leaves through plain air at x = 0.5, which is not tracked;
        // -X flow enters through the plain-air face (InternalExchange, never Inflow) and leaves through the domain face.
        var gp = Cube();
        var cropRoom = new Room { Id = "r", ZMin = .25f, ZMax = .75f, Loops = { new Vector2[] { new(0, .25f), new(.5f, .25f), new(.5f, .75f), new(0, .75f) } } };
        var fwd = Analysis.Analyze(new BuildingModel { Rooms = { cropRoom } }, Uniform(gp, Vector3.UnitX), Vector2.UnitX, new()).Rooms.Single();
        var back = Analysis.Analyze(new BuildingModel { Rooms = { cropRoom } }, Uniform(gp, -Vector3.UnitX), Vector2.UnitX, new()).Rooms.Single();
        Check("F12c cropped room: +X enters through the domain face (Inflow .25), -X enters through plain air (InternalExchange .25, not Inflow) and leaves through the domain face (Outflow .25)",
            Math.Abs(fwd.Inflow - .25) < 1e-5 && fwd.InternalExchange < 1e-6 && fwd.Outflow < 1e-6 && back.Inflow < 1e-6 && Math.Abs(back.InternalExchange - .25) < 1e-5 && Math.Abs(back.Outflow - .25) < 1e-5,
            $"+x: in={fwd.Inflow} exch={fwd.InternalExchange} out={fwd.Outflow} | -x: in={back.Inflow} exch={back.InternalExchange} out={back.Outflow}");
        var balanced = Analysis.Analyze(new BuildingModel { Rooms = { RoomOf(0, 1) } }, Uniform(Cube(), Vector3.UnitX), Vector2.UnitX, new()); Analysis.Finish(balanced);
        // An imbalanced room (+X flow in the left half, none in the right): net flux 1, noted in the ledger.
        var gi = Cube(); var fi = Uniform(gi, Vector3.Zero);
        for (int z = 0; z < gi.Nz; z++) for (int y = 0; y < gi.Ny; y++) for (int x = 0; x < 4; x++) fi.Ux[gi.Index(x, y, z)] = 1;
        var imb = Analysis.Analyze(new BuildingModel { Rooms = { RoomOf(0, 1) } }, fi, Vector2.UnitX, new()); Analysis.Finish(imb);
        var ir = imb.Rooms.Single();
        Check("F12d mass ledger carries the per-room net balance (balanced room: worst residual 0, imbalanced room noted as exchange not supply)",
            balanced.Ledger.WorstRoomResidual < 1e-5f && ir.Inflow > .99f && ir.Outflow < 1e-3f && ir.NetFlux > .99f && imb.Ledger.WorstRoomResidual > .9f && imb.Ledger.Notes.Any(n => n.Contains("not fresh-air supply")),
            $"balanced residual={balanced.Ledger.WorstRoomResidual} | imbalanced in={ir.Inflow} out={ir.Outflow} net={ir.NetFlux} residual={imb.Ledger.WorstRoomResidual} notes={imb.Ledger.Notes.Count}");
    }

    // ---- ACH denominator: nominal room volume, voxel volume reported beside it ---------------------------------

    static void AchVolumeChecks()
    {
        // 1 m cube room, uniform +X 1 m/s: Inflow = 1 m3/s through the x = 0 face. A 2x2x2-cell solid block standing on the floor,
        // clear of every side face, leaves that flux alone but takes 8 cells out of the voxel volume, as thin-partition solid
        // does in a real house.
        VoxelGrid Blocked(int z0 = 0)
        {
            var g = Cube();
            for (int z = z0; z <= z0 + 1; z++) for (int y = 3; y <= 4; y++) for (int x = 3; x <= 4; x++) g.Cell[g.Index(x, y, z)] = VoxelGrid.Solid;   // IMP-C (rule 3): standing on the floor, not hung mid-room: a column now stops at the first solid above its air (a ceiling), so a hung block would cut the air above it out of the room
            return g;
        }
        RoomResult Solve(Room room, int z0 = 0) => Analysis.Analyze(new BuildingModel { Rooms = { room } }, Uniform(Blocked(z0), Vector3.UnitX), Vector2.UnitX, new()).Rooms.Single();

        float voxel = 1f - 8f * Dx * Dx * Dx;
        var r = Solve(RoomOf(0, 1));
        Check("ACH1 room with a nominal footprint and height: ACH = Q / V_nominal, voxel volume reported beside it (ACH differs from Q / V_voxel)",
            r.VolumeIsNominal && r.VolumeNote.Length == 0 && Math.Abs(r.NominalVolume - 1f) < 1e-5 && Math.Abs(r.Volume - 1f) < 1e-5
            && Math.Abs(r.VoxelVolume - voxel) < 1e-5 && Math.Abs(r.Inflow - 1f) < 1e-5
            && Math.Abs(r.Ach - r.Inflow * 3600f / r.NominalVolume) < .05f && Math.Abs(r.Ach - r.Inflow * 3600f / r.VoxelVolume) > 10f,
            $"Q={r.Inflow} V_nominal={r.NominalVolume} V_voxel={r.VoxelVolume} (expected {voxel}) ACH={r.Ach} (Q/V_voxel would be {r.Inflow * 3600f / r.VoxelVolume})");

        // RA-07: the same block hung at mid-height (z cells 3..4, air under and over it) must not cost the room the air above it: same voxel volume and inflow as the block on the floor.
        var rh = Solve(RoomOf(0, 1), 3);
        Check("ACH1b RA-07: a block hung mid-room keeps the air above it in the room (voxel volume 1 - 8 cells, inflow 1 m3/s, as the block on the floor)",
            Math.Abs(rh.VoxelVolume - voxel) < 1e-5 && Math.Abs(rh.Inflow - 1f) < 1e-5 && Math.Abs(rh.Ach - r.Ach) < .05f,
            $"V_voxel={rh.VoxelVolume} (want {voxel}) Q={rh.Inflow} ACH={rh.Ach} (floor block {r.Ach})");

        var det = RoomOf(0, 1); det.Id = "det-L1-1";
        var rd = Solve(det);
        var crop = RoomOf(0, 2);
        var rc = Solve(crop);
        bool Fallback(RoomResult x) => !x.VolumeIsNominal && float.IsNaN(x.NominalVolume) && x.VolumeNote.Length > 0
            && Math.Abs(x.Volume - x.VoxelVolume) < 1e-6f && Math.Abs(x.Ach - x.Inflow * 3600f / x.VoxelVolume) < .05f;
        Check("ACH2 no nominal volume (voxel-detected room, room cropped by the domain): falls back to the voxel volume and says so",
            Fallback(rd) && rd.Inflow > 0 && Fallback(rc),
            $"detected: {rd.VolumeNote} | cropped: {rc.VolumeNote} | ACH {rd.Ach}/{rc.Ach}");

        // A loop inside a loop is a hole: 1 m x 1 m footprint minus a 0.5 m x 0.5 m column = 0.75 m2, x 1 m height.
        var holed = RoomOf(0, 1);
        holed.Loops.Add(new Vector2[] { new(.25f, .25f), new(.75f, .25f), new(.75f, .75f), new(.25f, .75f) });
        var rhole = Solve(holed);
        Check("ACH3 nominal volume honours holes in the footprint (even-odd)", Math.Abs(rhole.NominalVolume - 0.75f) < 1e-5f, $"V_nominal={rhole.NominalVolume}");
    }

    // ---- #5 total head ---------------------------------------------------------------------------------------

    static void OrificeHeadChecks()
    {
        var g = Cube(); var bad = new List<string>();
        foreach (var u in new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(.6f, .8f, 0), new Vector3(.3f, -.4f, .5f) })
        {
            double head = OrificeChecks.UpstreamTotalPressure(Uniform(g, u), g, 3, 1.2f), want = .5 * 1.2 * u.LengthSquared();
            if (Math.Abs(head - want) > 1e-5) bad.Add($"{u}: {head} vs {want}");
        }
        Check("F5a upstream total head uses all three velocity components (P=0, u=(0,1,0) reads 0.6 Pa, not 0)", bad.Count == 0 &&
            Math.Abs(OrificeChecks.UpstreamTotalPressure(Uniform(g, Vector3.UnitY), g, 3, 1.2f) - .6) < 1e-6, string.Join("; ", bad));
        // A velocity rotated between axes keeps the head: |u| is rotation invariant, the old Ux-only form was not.
        double a = OrificeChecks.UpstreamTotalPressure(Uniform(g, new(.8f, .6f, 0)), g, 3, 1.2f), b = OrificeChecks.UpstreamTotalPressure(Uniform(g, new(0, .8f, .6f)), g, 3, 1.2f);
        Check("F5b head is invariant when the same velocity is rotated between axes", Math.Abs(a - b) < 1e-6 && Math.Abs(a - .6) < 1e-6, $"xy={a} yz={b}");
    }

    // ---- #11 open Z faces ------------------------------------------------------------------------------------

    static void OpenZFaceChecks()
    {
        // One step of a seeded uniform 1 m/s flow in a box whose six faces are all open: the flow must survive the
        // step at the cell next to each face (a reflecting face would zero its normal component). Z faces are the
        // ones the old CPU/GPU gather reflected.
        var dirs = new (string Name, Vector3 U, int[] Probe)[]
        {
            ("+x", Vector3.UnitX, [0, 3, 3]), ("-x", -Vector3.UnitX, [7, 3, 3]), ("+y", Vector3.UnitY, [3, 0, 3]), ("-y", -Vector3.UnitY, [3, 7, 3]),
            ("+z", Vector3.UnitZ, [3, 3, 0]), ("-z", -Vector3.UnitZ, [3, 3, 7]),
        };
        var results = new Dictionary<Backend, float[]>();
        foreach (var backend in Backends)
        {
            var vals = new List<float>();
            foreach (var (name, u, p) in dirs)
            {
                var g = Cube(); g.OpenZMin = g.OpenZMax = true;
                using var s = new LbmSolver.LbmSession(g, new BuildingModel { Rooms = { RoomOf(0, 1) } }, Vector2.Zero, new() { Backend = backend }, uMaxOverride: 1);
                Seed(s, g, 1, u); s.ResetAveraging(startNow: true); s.Step(1);
                var f = s.ToField(); int i = g.Index(p[0], p[1], p[2]);
                vals.Add(Vector3.Dot(new(f.Ux[i], f.Uy[i], f.Uz[i]), u));
            }
            results[backend] = vals.ToArray();
            Check($"F11a all six faces open ({backend}): seeded 1 m/s survives at the probe cell beside each face",
                vals.All(v => v > .9f), string.Join(" ", dirs.Select((d, k) => $"{d.Name}={vals[k]:F3}")));
        }
        if (results.Count == 2)
        {
            float worst = results[Backend.Cpu].Zip(results[Backend.Gpu], (c, g) => Math.Abs(c - g)).Max();
            Check("F11b open faces CPU = GPU on all six directions", worst < 1e-3f, $"max |cpu-gpu|={worst:E2}");
        }

        // Partly blocked bottom face: the left half of the ground layer is solid, the right half open. Only the open
        // columns exchange.
        foreach (var backend in Backends)
        {
            var g = Cube(); g.OpenZMin = g.OpenZMax = true;
            for (int z = 0; z < 2; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 4; x++) g.Cell[g.Index(x, y, z)] = VoxelGrid.Solid;
            using var s = new LbmSolver.LbmSession(g, new BuildingModel { Rooms = { RoomOf(0, 1, 0, 1) } }, Vector2.Zero, new() { Backend = backend }, uMaxOverride: 1);
            Seed(s, g, 1, Vector3.UnitZ); s.ResetAveraging(startNow: true); s.Step(1);
            var f = s.ToField(); var room = f.RoomFluxes.Single();
            float open = f.Uz[g.Index(6, 3, 2)], blocked = f.Uz[g.Index(2, 3, 2)];
            Check($"F11c partly blocked bottom face ({backend}): open half passes the flow, blocked half does not, room inflow = open area",
                open > .9f && Math.Abs(blocked) < 1e-6f && Math.Abs(room.Inflow - 4 * 8 * Dx * Dx) < .05 * 4 * 8 * Dx * Dx, $"open={open:F3} blocked={blocked:F3} inflow={room.Inflow:F4} expect {4 * 8 * Dx * Dx:F4}");
        }

        // Thermal path: same open vs closed Z contrast with temperature carried (Dirichlet outdoor air at an inflow face).
        foreach (var backend in Backends)
        {
            (float Uz, float T) Run(bool openZ)
            {
                var g = Cube(); g.OpenZMin = g.OpenZMax = openZ;
                var m = new BuildingModel { Rooms = { RoomOf(0, 1) }, Env = new() { OutdoorTemperature = 5, IndoorTemperature = 5 } };
                using var s = new LbmSolver.LbmSession(g, m, Vector2.Zero, new() { Backend = backend, Thermal = true }, uMaxOverride: 1);
                Seed(s, g, 1, Vector3.UnitZ); s.ResetAveraging(startNow: true); s.Step(1);
                var f = s.ToField(); int i = g.Index(3, 3, 0); return (f.Uz[i], f.T[i]);
            }
            var open = Run(true); var closed = Run(false);
            Check($"F11d thermal session ({backend}): open Z face keeps the vertical flow and finite outdoor T, closed Z face reflects it",
                open.Uz > .9f && closed.Uz < .5f * open.Uz && float.IsFinite(open.T) && Math.Abs(open.T - 5) < 1, $"open Uz={open.Uz:F3} T={open.T:F2} | closed Uz={closed.Uz:F3}");
        }
    }

    // ---- #2 wall links on a channel --------------------------------------------------------------------------

    const int CNx = 48, CNy = 8, CNz = 4;
    const float CDx = 0.005f, CUin = 0.024f;

    static VoxelGrid LinkChannel(float inset, bool links)
    {
        var g = new VoxelGrid { Nx = CNx, Ny = CNy, Nz = CNz, Dx = CDx, Origin = Vector3.Zero, Cell = new byte[CNx * CNy * CNz] };
        for (int z = 0; z < CNz; z++) for (int y = 0; y < CNy; y++) for (int x = 0; x < CNx; x++) g.Cell[g.Index(x, y, z)] = y == 0 || y == CNy - 1 ? VoxelGrid.Solid : VoxelGrid.Air;
        if (!links) return g;
        int[] cx = [0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0];
        int[] cy = [0, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 0, 0, 1, -1, 1, -1];
        int[] cz = [0, 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, -1, 1, 1, -1, -1, 1];
        g.WallLinks = new byte[g.Count * 19];
        // Link q arrives at fluid cell i from solid cell i - c_q; the plane sits (0.5 - inset) of a cell from the fluid
        // centre along any such link (the y component of every link is +-1 cell).
        byte enc = (byte)(1 + (int)MathF.Round(254 * (0.5f - inset)));
        for (int z = 0; z < CNz; z++) for (int y = 1; y < CNy - 1; y++) for (int x = 0; x < CNx; x++)
            for (int q = 1; q < 19; q++)
            {
                int sx = x - cx[q], sy = y - cy[q], sz = z - cz[q];
                if (sx < 0 || sx >= CNx || sy < 0 || sy >= CNy || sz < 0 || sz >= CNz || g.Cell[g.Index(sx, sy, sz)] != VoxelGrid.Solid) continue;
                g.WallLinks[q * g.Count + g.Index(x, y, z)] = enc;
            }
        return g;
    }

    static (float Width, float[] Profile, string Backend) RunChannel(Backend backend, float inset, bool links)
    {
        var grid = LinkChannel(inset, links);
        var model = new BuildingModel { Name = "channel", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = CNz * CDx, Analysed = true } } };
        using var session = LbmSolver.Start(grid, model, new Vector2(CUin, 0f), new RunOptions { Backend = backend, WindProfileExponent = 0f });
        session.Step(4 * session.PlannedSteps); session.ResetAveraging(); session.Step(session.PlannedSteps);
        var field = session.ToField();
        var y = new double[CNy - 2]; var u = new double[CNy - 2]; double centre = (CNy - 1) * 0.5; int xs = 3 * CNx / 4;
        for (int j = 1; j <= CNy - 2; j++)
        {
            double sum = 0; for (int z = 0; z < CNz; z++) sum += field.Ux[grid.Index(xs, j, z)];
            y[j - 1] = j + 0.5 - centre; u[j - 1] = sum / CNz;
        }
        var (a, b, c) = WallChecks.FitQuadratic(y, u); double disc = b * b - 4 * a * c;
        float width = a >= 0 || disc <= 0 ? float.NaN : (float)Math.Abs((-b - Math.Sqrt(disc)) / (2 * a) - (-b + Math.Sqrt(disc)) / (2 * a));
        return (width, u.Select(v => (float)v).ToArray(), session.Backend);
    }

    static void WallLinkChannelChecks()
    {
        var detail = new List<string>(); bool ok = true, beatsHalfway = true;
        var cpuProfiles = new Dictionary<float, float[]>();
        foreach (float inset in new[] { -.3f, .1f, .3f, .45f })
        {
            float trueWidth = CNy - 2 - 2 * inset;   // cells
            var link = RunChannel(Backend.Cpu, inset, true); var half = RunChannel(Backend.Cpu, inset, false);
            float err = Math.Abs(link.Width - trueWidth) / trueWidth, errHalf = Math.Abs(half.Width - trueWidth) / trueWidth;
            ok &= err <= .05f; if (inset is .1f or .3f or .45f) beatsHalfway &= err < errHalf;
            cpuProfiles[inset] = link.Profile;
            detail.Add($"inset{inset:F2}: width={link.Width:F2} true={trueWidth:F2} err={err * 100:F1}% halfway_err={errHalf * 100:F1}%");
        }
        Check("F2a Poiseuille with WallLinks at sub-cell wall offsets: fitted width within 5% of true (CPU)", ok, string.Join(" | ", detail));
        Check("F2b WallLinks beat halfway bounce-back at every wall offset >= 0.1 cell", beatsHalfway);
        if (LbmSolver.GpuAvailable)
        {
            var worst = 0f; var det = new List<string>();
            foreach (float inset in new[] { -.3f, .1f, .3f, .45f })
            {
                var gpu = RunChannel(Backend.Gpu, inset, true); var cpu = cpuProfiles[inset];
                float peak = cpu.Max(), d = cpu.Zip(gpu.Profile, (a, b) => Math.Abs(a - b)).Max() / peak; worst = Math.Max(worst, d);
                det.Add($"inset{inset:F2}: max|cpu-gpu|/peak={d * 100:F2}%");
            }
            Check("F2c WallLinks channel profile GPU = CPU within 1% of peak at every offset", worst <= .01f, string.Join(" | ", det));
        }
    }

    // ---- #2 opening close / reopen ---------------------------------------------------------------------------

    static void ReopenChecks()
    {
        BuildingModel Wall(bool open) => new()
        {
            GroundZ = -10, Obstacles = { Box(new(2, .125f, .125f), new(2.5f, 4.875f, 4.875f)) },
            Openings = { new() { Id = "w", Center = new(2.25f, 2.5f, 2.5f), Normal = Vector3.UnitX, Width = 1, Height = 1, Depth = .5f, IsOpen = open } },
        };
        var mClosed = Wall(false); var mOpen = Wall(true);
        var gClosed = Build(mClosed); var gOpen = Build(mOpen);
        var results = new Dictionary<Backend, double[]>();
        foreach (var backend in Backends)
        {
            using var s = LbmSolver.Start(gClosed, mClosed, new Vector2(1, 0), new RunOptions { Backend = backend, WindProfileExponent = 0 });
            double Down()
            {
                s.ResetAveraging(startNow: true); s.Step(400);
                var f = s.ToField(); double sum = 0; int n = 0;
                for (int z = 12; z < 28; z++) for (int y = 12; y < 28; y++) { sum += f.Ux[gOpen.Index(26, y, z)]; n++; }
                return sum / n;
            }
            double closed1 = Down();
            s.UpdateCells(gOpen, mOpen); double reopened = Down();
            s.UpdateCells(gClosed, mClosed); double closed2 = Down();
            results[backend] = [closed1, reopened, closed2];
            // Everything finite; reopening admits flow far beyond the closed leakage; closing again shuts it off again.
            Check($"F2d opening closed -> reopened -> closed on one session ({backend}): downstream flow follows the geometry",
                double.IsFinite(closed1 + reopened + closed2) && reopened > 5 * Math.Abs(closed1) + .02 && closed2 < .5 * reopened,
                $"closed={closed1:F4} reopened={reopened:F4} closedAgain={closed2:F4} m/s");
        }
        if (results.Count == 2)
        {
            var c = results[Backend.Cpu]; var g = results[Backend.Gpu]; double d = Math.Abs(c[1] - g[1]) / Math.Abs(c[1]);
            Check("F2e reopened flow CPU = GPU within 5%", d <= .05, $"cpu={c[1]:F4} gpu={g[1]:F4}");
        }
    }

    // ---- #7 live updates -------------------------------------------------------------------------------------

    static void LiveUpdateChecks()
    {
        const int pa = 2 + 8 * (3 + 8 * 3), pb = 2 + 8 * (3 + 8 * 5);   // (2,3,3) and (2,3,5)
        AirSource Src(string id, float q) => new() { Id = id, Kind = SourceKind.Supply, Direction = Vector3.UnitX, Width = .125f, Height = .125f, FlowRate = q };
        VoxelGrid PatchGrid(params (int Cell, float Q, int Index)[] patches)
        {
            var g = Cube();
            foreach (var (cell, q, index) in patches) { g.Cell[cell] = VoxelGrid.Fixed; g.FixedCells.Add(new(cell, new(q / (Dx * Dx), 0, 0), index)); }
            return g;
        }
        LbmSolver.LbmSession Session(VoxelGrid g, BuildingModel m) => new(g, m, Vector2.Zero, new() { Backend = Backend.Cpu }, uMaxOverride: 1);
        string Targets(FlowField f) => string.Join(",", f.Supplies.OrderBy(x => x.SourceIndex).Select(x => $"{x.SourceIndex}:{x.SetFlowRate * 1000:F2}"));

        var model = new BuildingModel { Sources = { Src("a", .001f), Src("b", .002f) } };
        using (var s = Session(PatchGrid((pa, .001f, 0), (pb, .002f, 1)), model))
        {
            s.Step(60);
            var edited = BuildingModel.FromJson(model.ToJson()); edited.Sources[0].FlowRate = .0015f;
            s.UpdateCells(PatchGrid((pa, .0015f, 0), (pb, .002f, 1)), edited);
            var f = s.ToField();
            Check("F7a supply flow edit reaches the controller target (and the new model is adopted)",
                f.Supplies.Count == 2 && Math.Abs(f.Supplies.Single(x => x.SourceIndex == 0).SetFlowRate - .0015f) < 1e-7 && Math.Abs(f.Supplies.Single(x => x.SourceIndex == 1).SetFlowRate - .002f) < 1e-7, $"targets (L/s) {Targets(f)}");
            var reordered = BuildingModel.FromJson(edited.ToJson()); reordered.Sources.Reverse();
            s.UpdateCells(PatchGrid((pa, .0015f, 1), (pb, .002f, 0)), reordered);
            f = s.ToField();
            Check("F7b source reorder: targets follow the source identity, not the list position",
                f.Supplies.Count == 2 && Math.Abs(f.Supplies.Single(x => x.SourceIndex == 0).SetFlowRate - .002f) < 1e-7 && Math.Abs(f.Supplies.Single(x => x.SourceIndex == 1).SetFlowRate - .0015f) < 1e-7, $"targets (L/s) {Targets(f)}");
            var removed = BuildingModel.FromJson(reordered.ToJson()); removed.Sources.RemoveAt(1);
            s.UpdateCells(PatchGrid((pb, .002f, 0)), removed);
            s.Step(40); f = s.ToField();
            Check("F7c source removal: one loop left, indexed 0, with the survivor's target; still runs",
                f.Supplies.Count == 1 && f.Supplies[0].SourceIndex == 0 && Math.Abs(f.Supplies[0].SetFlowRate - .002f) < 1e-7 && f.Ux.All(float.IsFinite), $"targets (L/s) {Targets(f)}");
        }

        // A source edit that outgrows the session's lattice speed scale is refused (restart), not silently kept.
        using (var s = new LbmSolver.LbmSession(PatchGrid((pa, .001f, 0)), new BuildingModel { Sources = { Src("a", .001f) } }, Vector2.Zero, new() { Backend = Backend.Cpu }))
        {
            string msg = "";
            try { s.UpdateCells(PatchGrid((pa, .002f, 0)), new BuildingModel { Sources = { Src("a", .002f) } }); } catch (InvalidOperationException ex) { msg = ex.Message; }
            Check("F7d a source speed beyond the lattice scale raises the restart guard", msg.Contains("restart"), msg);
        }

        // Opening Cd and dimension edits: read the calibrated throat loss back from the device-force buffer.
        var wallModel = new BuildingModel
        {
            GroundZ = -10, Obstacles = { Box(new(2, .125f, .125f), new(2.5f, 4.875f, 4.875f)) },
            Openings = { new() { Id = "w", Center = new(2.25f, 2.5f, 2.5f), Normal = Vector3.UnitX, Width = 1, Height = 1, Depth = .5f } },
        };
        var wg = Build(wallModel);
        using (var s = LbmSolver.Start(wg, wallModel, new Vector2(1, 0), new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0 }))
        {
            float Loss(VoxelGrid g, BuildingModel m, out int cells)
            {
                var dev = Field<float[]>(s, "_devData"); int count = g.Count; var throat = Analysis.ThroatCells(g, m.Openings[0]);
                cells = 0; for (int i = 0; i < count; i++) if (dev.Length > 3 * count + i && dev[3 * count + i] < 0) cells++;
                return throat.Count > 0 ? dev[3 * count + throat[0]] : 0;
            }
            float want(BuildingModel m, VoxelGrid g) => -0.5f * LbmSolver.OpeningLossK(m.Openings[0], g.Dx, s.ViscosityFloor);
            float before = Loss(wg, wallModel, out _);
            var cdEdit = BuildingModel.FromJson(wallModel.ToJson()); cdEdit.Openings[0].DischargeCoefficient = .4f;
            s.UpdateCells(wg, cdEdit);
            float after = Loss(wg, cdEdit, out int n1);
            Check("F7e opening Cd edit: calibrated throat loss is rebuilt from the new model", Math.Abs(before - want(wallModel, wg)) < 1e-6 && Math.Abs(after - want(cdEdit, wg)) < 1e-6 && after < before,
                $"loss default={before:F4} cd0.4={after:F4} expect {want(cdEdit, wg):F4}");
            var wide = BuildingModel.FromJson(cdEdit.ToJson()); wide.Openings[0].Width = 1.5f; var wg2 = Build(wide);
            s.UpdateCells(wg2, wide);
            float lossWide = Loss(wg2, wide, out int n2);
            Check("F7f opening dimension edit: loss cells follow the new carved throat", n2 == Analysis.ThroatCells(wg2, wide.Openings[0]).Count * 4 / 4 + (n2 - Analysis.ThroatCells(wg2, wide.Openings[0]).Count) && n2 > n1 && Math.Abs(lossWide - want(wide, wg2)) < 1e-6,
                $"loss cells {n1} -> {n2} (throat {Analysis.ThroatCells(wg2, wide.Openings[0]).Count})");
        }

        // Thermal: any model object change needs a restart (buoyancy scale and heat terms are built from it).
        var tm = new BuildingModel { Rooms = { RoomOf(0, 1) }, Env = new() { OutdoorTemperature = 5, IndoorTemperature = 25 } };
        using (var s = new LbmSolver.LbmSession(Cube(), tm, new Vector2(1, 0), new() { Backend = Backend.Cpu, Thermal = true }, uMaxOverride: 1))
        {
            string msg = "";
            var tm2 = BuildingModel.FromJson(tm.ToJson()); tm2.Env.IndoorTemperature = 30;
            try { s.UpdateCells(Cube(), tm2); } catch (InvalidOperationException ex) { msg = ex.Message; }
            bool sameOk = true; try { s.UpdateCells(Cube(), tm); s.UpdateCells(Cube()); } catch (Exception) { sameOk = false; }
            Check("F7g thermal edit forces a restart; same-model geometry updates still go through", msg.Contains("restart") && sameOk, msg);
        }

        // Thermal, new Air cells (a hole opened in the grid on the same model) start at the NEW grid's T0 on both backends.
        // The GPU reset kernel used to read the device T0 before it was refreshed: 0 degC at every cell that had been solid.
        var tw = new BuildingModel
        {
            GroundZ = -10, Obstacles = { Box(new(2, .125f, .125f), new(2.5f, 4.875f, 4.875f)) },
            Openings = { new() { Id = "w", Center = new(2.25f, 2.5f, 2.5f), Normal = Vector3.UnitX, Width = 1, Height = 1, Depth = .5f, IsOpen = false } },
            Env = new() { OutdoorTemperature = 30, IndoorTemperature = 10 },
        };
        var twOpen = BuildingModel.FromJson(tw.ToJson()); twOpen.Openings[0].IsOpen = true;
        var tgClosed = Build(tw); var tgOpen = Build(twOpen);
        foreach (var backend in Backends)
        {
            using var s = LbmSolver.Start(tgClosed, tw, Vector2.Zero, new RunOptions { Backend = backend, Thermal = true });
            s.Step(20);
            s.UpdateCells(tgOpen);
            s.ResetAveraging(startNow: true); s.Step(1);
            var f = s.ToField(); int hole = tgOpen.Index(17, 20, 20);
            Check($"F7i thermal UpdateCells: a newly opened cell starts at the new T0 ({backend})",
                tgClosed.IsSolid(hole) && !tgOpen.IsSolid(hole) && Math.Abs(f.T[hole] - 30f) < 1.5f, $"T at the new cell {f.T[hole]:F2} degC (its T0 is the outdoor 30)");
        }

        NestedUpdateCheck();
    }

    static void NestedUpdateCheck()
    {
        var model = Program.BuildCross("fix-nest", true, true); var wind = new Vector2(3, 0);
        var opts = new RunOptions { Backend = Backend.Cpu };
        var cg = Voxelizer.Build(model, 50_000);
        using var coarse = LbmSolver.Start(cg, model, wind, opts);
        var box = new DomainBox(cg.Origin + new Vector3(0, 0, cg.Dx), cg.Origin + new Vector3(cg.Nx, cg.Ny, cg.Nz) * cg.Dx);
        using var nest = LbmSolver.StartNested(coarse, box, opts, driveCoarse: true);
        nest.Step(3);
        var edited = BuildingModel.FromJson(model.ToJson()); edited.Openings.First(o => o.Id == "east").DischargeCoefficient = .35f;
        var newCoarse = Voxelizer.Build(edited, 50_000); var newFine = nest.BuildGrid(edited);
        nest.UpdateCells(newFine, newCoarse, edited);
        nest.Step(3);
        float Loss(LbmSolver.LbmSession s, VoxelGrid g)
        {
            var dev = Field<float[]>(s, "_devData"); var o = edited.Openings.First(x => x.Id == "east");
            int c = Analysis.ThroatCells(g, o)[0]; return dev[3 * g.Count + c];
        }
        var fine = Field<LbmSolver.LbmSession>(nest, "_fine");
        var east = edited.Openings.First(o => o.Id == "east");
        float wantC = -0.5f * LbmSolver.OpeningLossK(east, newCoarse.Dx, coarse.ViscosityFloor), wantF = -0.5f * LbmSolver.OpeningLossK(east, newFine.Dx, fine.ViscosityFloor);
        Check("F7h nested update adopts the edited model in both the coarse and fine sessions (Cd read back from each)",
            ReferenceEquals(coarse.NestModel, edited) && ReferenceEquals(fine.NestModel, edited) && Math.Abs(Loss(coarse, newCoarse) - wantC) < 1e-6 && Math.Abs(Loss(fine, newFine) - wantF) < 1e-6,
            $"coarse loss={Loss(coarse, newCoarse):F4} expect {wantC:F4}; fine loss={Loss(fine, newFine):F4} expect {wantF:F4}");
    }

    // ---- #9 warm-start lead ----------------------------------------------------------------------------------

    static void ConvergeLeadChecks()
    {
        var g = Cube(); var m = new BuildingModel { Rooms = { RoomOf(.25f, .75f, .25f, .75f) } }; var wind = new Vector2(1, 0);
        var opts = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0 };
        using var s = LbmSolver.Start(g, m, wind, opts, Uniform(g, new(1, 0, 0)));
        // The session gates averaging at its own warm-up step: a plain reset leaves the gate in place...
        int shorter = Math.Max(1, s.WarmupSteps / 4);
        s.ResetAveraging(); s.Step(shorter);
        long gated = s.ToField().AverageSamples;
        // ...so the batch loop must (and does) restart with startNow, which moves the gate to the current step.
        s.ResetAveraging(startNow: true); s.Step(shorter);
        long immediate = s.ToField().AverageSamples;
        Check("F9a a seeded session's plain ResetAveraging keeps the warm-up gate; startNow collects at once (the batch loop uses startNow)",
            gated == 0 && immediate == shorter, $"warmup={s.WarmupSteps} steps={shorter} gated_samples={gated} startNow_samples={immediate}");

        using var s2 = LbmSolver.Start(g, m, wind, opts, Uniform(g, new(1, 0, 0)));
        // A synchronous progress sink reads the session right after each batch (Progress<T> would post later).
        var perBatch = new List<long>();
        var (status, batches) = Simulation.Converge(s2, m, wind, opts, Uniform(g, new(1, 0, 0)), new SyncProgress(() => perBatch.Add(s2.ToField().AverageSamples)));
        var f = s2.ToField();
        Check("F9b legacy loop (Validation-free): Converge on a seeded session whose batches are shorter than the warm-up: EVERY batch, the first included, holds a real average",
            batches >= 1 && perBatch.Count == batches && perBatch.All(n => n > 0 && n < s2.WarmupSteps) && f.AverageSamples > 0 && f.Ux.Max() > .3f && f.P.All(float.IsFinite) && status.Rooms[0].BatchCount >= 1,
            $"batches={batches} samples_per_batch=[{string.Join(",", perBatch)}] warmup_steps={s2.WarmupSteps} maxUx={f.Ux.Max():F3}");
    }

    sealed class SyncProgress(Action onReport) : IProgress<double> { public void Report(double value) => onReport(); }

    // ---- ValidationCase running mean (FlowFieldMean) ---------------------------------------------------------

    static void MeanAliasingChecks()
    {
        // Runner.Run keeps the first block's field as the accumulator and folds later blocks in with FlowFieldMean.Add.
        // ToField returns fresh arrays and fresh RoomFlux objects each call, so the accumulator aliases nothing the
        // session owns; the operand must be left untouched and the result must be the sample-weighted mean.
        var g = Cube(4);
        FlowField Block(float u, long samples, double inflow)
        {
            var f = Uniform(g, new(u, 0, 0)); f.AverageSamples = samples;
            f.RoomFluxes.Add(new() { Room = RoomOf(0, 1), Inflow = inflow, Outflow = inflow, InternalExchange = inflow });
            return f;
        }
        var a = Block(1, 10, 1); var b = Block(3, 10, 3); var c = Block(8, 10, 8);
        var bBefore = b.Ux.ToArray(); double bInflow = b.RoomFluxes[0].Inflow;
        FlowFieldMean.Add(a, b); FlowFieldMean.Add(a, c);
        bool untouched = b.Ux.SequenceEqual(bBefore) && b.RoomFluxes[0].Inflow == bInflow && b.AverageSamples == 10;
        bool mean = Math.Abs(a.Ux[0] - 4) < 1e-5 && a.AverageSamples == 30 && Math.Abs(a.RoomFluxes[0].Inflow - 4) < 1e-9 && Math.Abs(a.RoomFluxes[0].Outflow - 4) < 1e-9 && !ReferenceEquals(a.RoomFluxes[0], b.RoomFluxes[0]);
        Check("F6a running mean of equal blocks: operand untouched, result = arithmetic mean (field, inflow, outflow), no shared RoomFlux", untouched && mean,
            $"Ux={a.Ux[0]} samples={a.AverageSamples} inflow={a.RoomFluxes[0].Inflow} operandUntouched={untouched}");

        // RT-09: a non-finite sample is skipped (the running value stays, the other cells blend as usual), counted, and flagged on the grid's warnings.
        var nanA = Block(1, 10, 1); var nanB = Block(3, 10, 3);
        nanB.Ux[2] = float.NaN; nanB.Uy[5] = float.PositiveInfinity;
        FlowFieldMean.Add(nanA, nanB);
        bool kept = nanA.Ux[2] == 1f && nanA.Uy[5] == 0f && MathF.Abs(nanA.Ux[0] - 2f) < 1e-5f && nanA.Ux.All(float.IsFinite) && nanA.Uy.All(float.IsFinite);
        bool flagged = nanA.NonFiniteSkipped == 2 && nanA.Grid.Warnings.Any(w => w.StartsWith(FlowFieldMean.NonFiniteTag, StringComparison.Ordinal) && w.Contains("2 non-finite", StringComparison.Ordinal));
        Check("RT-09 FlowFieldMean skips non-finite samples (NaN and Inf), keeps the running value there, counts them and flags the result", kept && flagged,
            $"Ux[2]={nanA.Ux[2]} Uy[5]={nanA.Uy[5]} Ux[0]={nanA.Ux[0]} skipped={nanA.NonFiniteSkipped} warning={(nanA.Grid.Warnings.Count > 0 ? nanA.Grid.Warnings[^1] : "none")}");

        // RW-03: each cell is divided by its own valid-sample count: samples 1, NaN, 3 give 2.0 (not 1.67); NaN, 1, 3 give 2.0 and the window's seed NaN is counted and flagged.
        var rwA = Block(1, 1, 1); var rwA2 = Block(3, 1, 3); rwA2.Ux[2] = float.NaN; var rwA3 = Block(3, 1, 3);
        FlowFieldMean.Add(rwA, rwA2); FlowFieldMean.Add(rwA, rwA3);
        var rwB = Block(1, 1, 1); rwB.Ux[2] = float.NaN; FlowFieldMean.Seed(rwB, 1);
        FlowFieldMean.Add(rwB, Block(1, 1, 1)); FlowFieldMean.Add(rwB, Block(3, 1, 3));
        bool rwFlag = rwB.NonFiniteSkipped == 1 && rwB.Grid.Warnings.Any(w => w.StartsWith(FlowFieldMean.NonFiniteTag, StringComparison.Ordinal) && w.Contains("biased or missing in 1 cells", StringComparison.Ordinal));
        Check("RW-03 FlowFieldMean divides each cell by its own valid count: samples 1, NaN, 3 give 2.0; NaN, 1, 3 give 2.0 and raise the flag; other cells unchanged",
            MathF.Abs(rwA.Ux[2] - 2f) < 1e-6f && MathF.Abs(rwB.Ux[2] - 2f) < 1e-6f && MathF.Abs(rwA.Ux[0] - 7f / 3f) < 1e-5f && rwFlag,
            $"1,NaN,3 -> {rwA.Ux[2]} · NaN,1,3 -> {rwB.Ux[2]} · flag {rwFlag} · other cell {rwA.Ux[0]}");
    }
}
