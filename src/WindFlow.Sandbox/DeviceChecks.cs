// Body-force HVAC devices (AirSource.Device != Patch): verification harness.
// Run it with:  dotnet run -c Release --project src/WindFlow.Sandbox -- --devices
//
// Every device here is a region of ordinary AIR cells carrying a Guo body force toward a target velocity
// (see VoxelGrid.DeviceRegion and LbmSolver's DeviceForce), never a Fixed-velocity patch, so the first
// thing each scene checks is that the device moved air without creating any.
//
// Scenes (a) and (b) are hand-built sealed rooms passed straight to LbmSolver.Start, the same trick the
// cavity benchmark (Program.cs, checks 31a-c) uses: Voxelizer.Build always pads the domain with >= 8 m of
// sky and side margin, which at a whole-building cell budget leaves a 4 m room ~16 cells across - far too
// coarse to resolve a 0.7 m fan disc. Scenes (c) and (d) go through the full Simulation.Run pipeline
// because that is what they are testing.

using System.Numerics;
using WindFlow.Core;
using Environment = WindFlow.Core.Environment;   // disambiguate from System.Environment

namespace WindFlow.Sandbox;

static class DeviceChecks
{
    const float RhoCp = 1.2f * 1005f;   // J/(m3 K), air at ~20 C -- mirrors LbmSolver's own constant

    /// <summary>Runs every device check and returns the number of failures (0 = all passed).</summary>
    /// <summary>`--ac-balance`: check 41g alone (a few minutes).</summary>
    public static int RunAcBalance()
    {
        int failures = 0;
        SplitAcSteadyBalance((label, pass, detail) =>
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {label}  {detail}");
            CheckLog.Record(pass ? "PASS" : "FAIL", label, detail);
            if (!pass) failures++;
            return pass;
        });
        return failures;
    }

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

        Console.WriteLine("== device checks ==");
        CeilingFanChecks(Check, Skip);
        SplitAcChecks(Check);
        SplitAcSteadyBalance(Check);
        SplitAcGpuParity(Check, Skip);
        SplitAcReturnWarning(Check);
        PatchRegressionCheck(Check);
        ExhaustFanCheck(Check);
        LedgerChecks(Check);
        OtherKindsChecks(Check);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL DEVICE CHECKS PASSED" : $"{failures} DEVICE CHECK(S) FAILED");
        return failures;
    }

    // ---------------------------------------------------------------------------------------------
    // 40. Ceiling fan: actuator disc in a sealed 4 x 4 x 3 m room, no wind, no heat.
    // ---------------------------------------------------------------------------------------------

    static void CeilingFanChecks(Func<string, bool, string, bool> check, Action<string, string> skip)
    {
        const float lx = 4f, ly = 4f, lz = 3f, dx = 0.1f, flow = 1.2f, radius = 0.7f, swirl = 0.3f;
        var axis = new Vector3(lx * 0.5f, ly * 0.5f, 2.7f);

        (FlowField Field, float Dt, VoxelGrid Grid, long Steps, string Backend, BuildingModel Model) RunFan(Backend backend)
        {
            var (grid, model) = SealedRoom(lx, ly, lz, dx, "ceiling-fan");
            model.Sources.Add(new AirSource
            {
                Id = "fan", Name = "Ceiling fan", Level = "L1", Kind = SourceKind.Fan,
                Device = DeviceKind.CeilingFan, Center = axis, Direction = -Vector3.UnitZ,
                Radius = radius, Swirl = swirl, FlowRate = flow,
            });
            Voxelizer.ApplyDevices(grid, model.Sources);

            using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = backend });
            // Three planned runs to develop the recirculation (a room-scale loop needs several turnovers),
            // then average only the developed tail -- same pattern as the cavity benchmark.
            session.Step(3 * session.PlannedSteps);
            session.ResetAveraging();
            session.Step(session.PlannedSteps);
            var field = session.ToField();
            return (field, (float)(session.SimSeconds / Math.Max(1, session.Steps)), grid, session.Steps, session.Backend, model);
        }

        var cpu = RunFan(Backend.Cpu);
        var g = cpu.Grid;
        var dev = cpu.Field.Devices[0];
        float discFlux = DiscFlux(cpu.Field, axis, radius);
        float mass = MassResidual(cpu.Field, cpu.Dt);

        check("40a ceiling-fan sealed room conserves mass (|mean rho - 1| <= 1e-3)",
            MathF.Abs(mass) <= 1e-3f,
            $"massResidual={mass:E2} cells={g.Nx}x{g.Ny}x{g.Nz} Dx={g.Dx * 1000:F0}mm steps={cpu.Steps} backend={cpu.Backend}");

        check("40b ceiling-fan axial flow at the disc within 25% of 1.200 m3/s",
            MathF.Abs(discFlux - flow) <= 0.25f * flow,
            $"discPlane={discFlux:F3} m3/s deviceOutlet={dev.FlowRate:F3} set={dev.SetFlowRate:F3} " +
            $"discCells={dev.Cells} ({2f * radius / dx:F0} across)");

        float wallUp = WallReturn(cpu.Field, axis, radius + 0.6f, 0.5f, lz - 0.8f);
        check("40c ceiling-fan return flow rises along the walls (mean uz > 0 outside the disc)",
            wallUp > 0f, $"meanUz(walls)={wallUp:F3} m/s");

        float occupied = LayerMeanSpeed(cpu.Field, 1.1f);
        check("40d ceiling-fan mean speed at 1.1 m is in 0.05-1.50 m/s",
            occupied >= 0.05f && occupied <= 1.5f, $"meanSpeed(1.1m)={occupied:F3} m/s");

        // 40f: this disc hangs 0.3 m under the ceiling, which meets the metric rule (0.25 m = 3 cells at
        // 100 mm; measured 3 clear cells), so its intake must NOT be recorded short. The Analysis half then
        // marks the intake short by hand: while the fan delivers its flow Analysis must still NOT call it
        // choked; the same field with the fan's measured flow cut to 30 % must, exactly once.
        var region = g.Devices[0];
        bool metricOk = !region.IntakeShort;
        region.IntakeShort = true;
        Analysis.Analyze(cpu.Model, cpu.Field, Vector2.Zero, new RunOptions());
        bool quietWhenDelivering = !cpu.Field.Grid.Warnings.Exists(w => w.Contains("choked"));
        float realFlow = dev.FlowRate;
        dev.FlowRate = 0.3f * dev.SetFlowRate;
        Analysis.Analyze(cpu.Model, cpu.Field, Vector2.Zero, new RunOptions());
        bool warnsWhenShort = cpu.Field.Grid.Warnings.Count(w => w.Contains("choked")) == 1;
        Analysis.Analyze(cpu.Model, cpu.Field, Vector2.Zero, new RunOptions());
        bool noStacking = cpu.Field.Grid.Warnings.Count(w => w.Contains("choked")) == 1;
        dev.FlowRate = realFlow;
        region.IntakeShort = !metricOk;
        check("40f fan intake: 0.3 m clears the 0.25 m rule; short clearance alone is not 'choked'; short + < 60% of set flow is, once",
            metricOk && quietWhenDelivering && warnsWhenShort && noStacking,
            $"clearance={region.IntakeClearance:F2} m short={!metricOk} delivered={realFlow / dev.SetFlowRate * 100:F0}% " +
            $"quiet={quietWhenDelivering} warnsAt30%={warnsWhenShort} noStacking={noStacking}");

        if (!LbmSolver.GpuAvailable) { skip("40e ceiling-fan GPU = CPU within 1%", "no GPU available"); return; }
        var gpu = RunFan(Backend.Gpu);
        float gpuFlux = DiscFlux(gpu.Field, axis, radius);
        float err = MathF.Abs(gpuFlux - discFlux) / MathF.Max(MathF.Abs(discFlux), 1e-6f);
        check("40e ceiling-fan GPU = CPU within 1%", err <= 0.01f,
            $"cpu={discFlux:F4} gpu={gpuFlux:F4} err={err * 100:F2}% backend={gpu.Backend}");
    }

    // ---------------------------------------------------------------------------------------------
    // 41. Split AC: cooled jet + linked return in the same sealed room, thermal on.
    // ---------------------------------------------------------------------------------------------

    static void SplitAcChecks(Func<string, bool, string, bool> check)
    {
        const float lx = 4f, ly = 4f, lz = 3f, dx = 0.1f;
        const float flow = 0.15f, supply = 14f, indoor = 26f, seconds = 60f;

        var (grid, model) = SealedRoom(lx, ly, lz, dx, "split-ac");
        model.Env = new Environment { IndoorTemperature = indoor, OutdoorTemperature = indoor };
        model.Sources.Add(new AirSource
        {
            Id = "ac", Name = "Split AC", Level = "L1", Kind = SourceKind.Fan,   // recirculates: no outdoor air crosses the envelope
            // Outlet top at 2.5 m under a 3 m ceiling: room for the default return's 0.5 m separation.
            Device = DeviceKind.SplitAc, Center = new Vector3(0.25f, ly * 0.5f, 2.4f), Direction = Vector3.UnitX,
            Width = 0.8f, Height = 0.2f, FlowRate = flow, SupplyTemperature = supply, LouvreAngle = 30f,
        });
        Voxelizer.ApplyDevices(grid, model.Sources);

        using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = Backend.Cpu, Thermal = true });
        var t = new float[grid.Count];
        session.Step(session.WarmupSteps);
        float tStart = MeanAirTemperature(session, grid, t);
        double secondsStart = session.SimSeconds;

        session.ResetAveraging();
        float dt = (float)(session.SimSeconds / Math.Max(1, session.Steps));
        session.Step((int)MathF.Ceiling((seconds - (float)secondsStart) / dt));
        float tEnd = MeanAirTemperature(session, grid, t);
        double secondsEnd = session.SimSeconds;

        var field = session.ToField();
        var dev = field.Devices[0];
        float roomVolume = AirCells(grid) * dx * dx * dx;
        float bulkWatts = RhoCp * roomVolume * (tStart - tEnd) / (float)Math.Max(1e-6, secondsEnd - secondsStart);
        // The unit's own duty, from the two numbers it reports: what it moves and how far it cools it.
        float nameplate = RhoCp * MathF.Abs(dev.FlowRate) * (dev.ReturnTemperature - supply);
        float err = MathF.Abs(dev.HeatRemovedWatts - nameplate) / MathF.Max(nameplate, 1e-6f);
        float ideal = RhoCp * flow * (tEnd - supply);
        float mass = MassResidual(field, dt);

        check("41a split-ac room mean temperature falls (>= 0.2 K over the run)",
            tStart - tEnd >= 0.2f,
            $"T {tStart:F2} -> {tEnd:F2} C over {secondsEnd - secondsStart:F0} s (supply {supply:F0} C, " +
            $"{dev.Cells} cells, steps={session.Steps})");

        // Duty and return temperature have to tell the same story: Q == rho*cp*V*(T_return - T_supply)
        // using the very numbers the result reports. They can only disagree if the sink is metered
        // somewhere other than the unit's own stream, which is exactly the bug this replaced.
        check("41b split-ac duty is consistent with its own V and T_return (within 15%)",
            err <= 0.15f,
            $"Q={dev.HeatRemovedWatts:F0} W vs rho*cp*{MathF.Abs(dev.FlowRate):F3}*({dev.ReturnTemperature:F2}-{supply:F0})" +
            $"={nameplate:F0} W err={err * 100:F1}% | room {tEnd:F2} C, so an ideal unit ingesting room air " +
            $"would do {ideal:F0} W; the gap is this return short-circuiting on its own jet");

        // The room is sealed and adiabatic, so everything the unit takes out has to show up as the air's own
        // enthalpy falling: rho*cp*V_room*dT/dt. This is the check that the reported duty is real.
        float balance = MathF.Abs(dev.HeatRemovedWatts - bulkWatts) / MathF.Max(bulkWatts, 1e-6f);
        check("41c split-ac duty balances the room's enthalpy loss within 20%",
            balance <= 0.20f,
            $"Q={dev.HeatRemovedWatts:F0} W bulk=rho*cp*{roomVolume:F1}m3*{(tStart - tEnd) / (float)(secondsEnd - secondsStart):F4}K/s" +
            $"={bulkWatts:F0} W err={balance * 100:F1}%");

        check("41d split-ac mass residual <= 5% (the supply/return pair moves no net mass)",
            MathF.Abs(mass) <= 0.05f, $"massResidual={mass:E2} (sealed room: no opening carries flow)");

        // Where the intake actually reads. A return grille is a point in a room that is NOT uniform, so the
        // number to hold it against is the air at its own height, not the room mean -- but in a sealed room
        // with no heat source it must not come out above the room mean either.
        var region = grid.Devices[0];
        var (outletC, returnC) = DeviceCentroids(grid, region);
        float returnZ = returnC.Z;
        var own = new HashSet<int>(region.Cells);
        float layerT = LayerMeanT(field, returnZ, own);
        float roomT = RoomMeanT(field);

        // 41f bound, grid-aware. The gather is fixed (the default return sits on the wall above the outlet,
        // pulls down the wall, and is read one cell into the room outside the jet, DeviceRegion.GatherCells),
        // so what is left is a real short-circuit of the SIMULATED flow: the coarse cells between the outlet
        // and the grille mix a share of the cold discharge into the air the intake draws, so T_return sits
        // below the air at its height by roughly (mixed share) x (T_room - T_supply), and that share scales
        // with how many cells of mixing lie between the two. Measured at Dx 0.1 m with a 12 K supply-room
        // difference: 2.29 K at 0.3 m of separation, 1.22 K at 0.4 m, 0.61 K at 0.5 m (the default), 0.01 K
        // at 0.7 m; 0.74 K at Dx 0.05 m for 0.3 m. 15 % of |T_supply - T_room| is the accepted coarse-grid
        // ceiling at the default placement (a flat 0.5 K is grid-limited here); finer grids tighten it.
        float bound = MathF.Max(0.5f, 0.15f * MathF.Abs(supply - roomT));
        float dLayer = MathF.Abs(dev.ReturnTemperature - layerT);
        check("41f split-ac ReturnTemperature vs air at the intake's own height (<= max(0.5 K, 0.15 |T_supply - T_room|))",
            dLayer <= bound,
            $"|dT|={dLayer:F2} K bound={bound:F2} K  Treturn={dev.ReturnTemperature:F2} C at z={returnZ:F2} m, air in that layer " +
            $"(device cells excluded)={layerT:F2} C, room mean={roomT:F2} C, separation {Vector3.Distance(outletC, returnC):F3} m " +
            $"(vertical {returnC.Z - outletC.Z:F3} m), gather cells={region.GatherCells.Length} | profile " + Profile(field, lz));

    }

    /// <summary>41g: steady-state energy balance of a split AC in a sealed adiabatic room with a KNOWN heat load: at steady state the unit alone
    /// removes the heater's watts, so T_return = T_supply + Q / (rho cp V_supply) whatever the room does (mixed or stratified). The earlier 41g
    /// asserted "return &lt;= room mean + 0.5 K", which is an assumption of mixing ventilation, not a law: a cold jet over warm air is stably
    /// stratified and the high return then legitimately reads above the mean, so the room mean is only printed. The scene is a small room (3 x 3 x
    /// 2.6 m, flow-through V/Q = 156 s) so it reaches steady state in a few minutes of CPU. Steady = the time-averaged return T of two successive
    /// flow-throughs differs by less than 0.05 K (the first one starts at the end of the heater ramp-in). Not a 41 scene: that room has no load,
    /// so its steady state is T_return = T_supply, reached only after ~25 min of simulated time.</summary>
    static void SplitAcSteadyBalance(Func<string, bool, string, bool> check)
    {
        const float lx = 3f, ly = 3f, lz = 2.6f, dx = 0.1f, flow = 0.15f, supply = 14f, indoor = 22f, watts = 600f;
        const double driftTol = 0.05, tol = 0.3; const int maxThroughs = 14;

        var (grid, model) = SealedRoom(lx, ly, lz, dx, "split-ac-balance");
        model.Env = new Environment { IndoorTemperature = indoor, OutdoorTemperature = indoor };
        model.Sources.Add(new AirSource
        {
            Id = "ac", Name = "Split AC", Level = "L1", Kind = SourceKind.Fan,
            Device = DeviceKind.SplitAc, Center = new Vector3(0.25f, ly * 0.5f, 1.9f), Direction = Vector3.UnitX,
            Width = 0.8f, Height = 0.2f, FlowRate = flow, SupplyTemperature = supply, LouvreAngle = 30f,
        });
        model.Heat.Add(new HeatSource
        {
            Id = "heater", Name = "heater", Level = "L1", Kind = HeatKind.Equipment, Enabled = true,
            Center = new Vector3(2.2f, 1.5f, 0.3f), Width = 0.5f, Depth = 0.5f, Height = 0.5f, Watts = watts,
        });
        Voxelizer.ApplyDevices(grid, model.Sources);

        using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = Backend.Cpu, Thermal = true, ThermalScheme = ThermalScheme.Hybrid });
        float volume = AirCells(grid) * dx * dx * dx;
        session.Step(session.WarmupSteps);
        float dt = (float)(session.SimSeconds / Math.Max(1, session.Steps));
        int chunk = (int)MathF.Ceiling(volume / flow / dt);

        double prev = double.NaN, ret = double.NaN; int throughs = 0; bool steady = false;
        var trace = new List<string>();
        FlowField? field = null;
        while (throughs < maxThroughs && !steady)
        {
            session.ResetAveraging();
            session.Step(chunk);
            field = session.ToField();
            ret = field.Devices[0].ReturnTemperature;
            throughs++;
            trace.Add(ret.ToString("F2"));
            steady = !double.IsNaN(prev) && Math.Abs(ret - prev) < driftTol;
            prev = ret;
        }
        var dev = field!.Devices[0];
        double expected = supply + watts / (RhoCp * flow);   // the unit's set (nameplate) flow, known a priori: the flow its heat sink acts on
        double expectedMeasured = supply + watts / (RhoCp * Math.Abs(dev.FlowRate));
        check("41g split-ac steady state: time-averaged T_return = T_supply + Q/(rho cp V) within 0.3 K (known load, sealed adiabatic room)",
            steady && Math.Abs(ret - expected) <= tol,
            $"Treturn={ret:F2} C expected={expected:F2} C (Q={watts:F0} W, V={flow} m3/s set flow; {expectedMeasured:F2} C at the {Math.Abs(dev.FlowRate):F3} m3/s measured through the outlet cells, printed only) " +
            $"err={ret - expected:+0.00;-0.00} K steady={steady} after {throughs} flow-throughs of {chunk * dt:F0} s (Treturn per flow-through: {string.Join(" ", trace)}) | " +
            $"diagnostic, not asserted: room mean={RoomMeanT(field):F2} C, duty={dev.HeatRemovedWatts:F0} W | profile " + Profile(field, lz) + $" | scheme={session.Backend}");
    }

    /// <summary>Centroids of a device's outlet cells and of its return cells.</summary>
    static (Vector3 Outlet, Vector3 Return) DeviceCentroids(VoxelGrid grid, DeviceRegion region)
    {
        Vector3 Mean(int from, int to)
        {
            var sum = Vector3.Zero;
            for (int i = from; i < to; i++)
                sum += grid.CellCenter(region.Cells[i] % grid.Nx, region.Cells[i] / grid.Nx % grid.Ny,
                    region.Cells[i] / (grid.Nx * grid.Ny));
            return sum / Math.Max(1, to - from);
        }
        return (Mean(0, region.OutletCount), Mean(region.OutletCount, region.Cells.Length));
    }

    /// <summary>41h: a return grille placed by hand 0.2 m above the outlet gets the Core short-circuit
    /// warning; the default placement in the 41 scene does not. Voxelization only, no solve.</summary>
    static void SplitAcReturnWarning(Func<string, bool, string, bool> check)
    {
        (bool Warned, float Gap, string Line) Build(Vector3 returnCenter)
        {
            var (grid, model) = SealedRoom(4f, 4f, 3f, 0.1f, "split-ac-return");
            var center = new Vector3(0.25f, 2f, 2.4f);
            model.Sources.Add(new AirSource
            {
                Id = "ac", Name = "Split AC", Level = "L1", Kind = SourceKind.Fan,
                Device = DeviceKind.SplitAc, Center = center, Direction = Vector3.UnitX,
                Width = 0.8f, Height = 0.2f, FlowRate = 0.15f, SupplyTemperature = 14f, LouvreAngle = 30f,
                ReturnCenter = returnCenter,
            });
            Voxelizer.ApplyDevices(grid, model.Sources);
            var (o, r) = DeviceCentroids(grid, grid.Devices[0]);
            string? line = grid.Warnings.Find(w => w.Contains("re-ingests its own cold jet"));
            return (line != null, Vector3.Distance(o, r), line ?? "");
        }

        var user = Build(new Vector3(0.25f, 2f, 2.6f));
        var auto = Build(new Vector3(float.NaN, float.NaN, float.NaN));
        check("41h split-ac return 0.2 m above the outlet warns; the default placement does not",
            user.Warned && !auto.Warned,
            $"user gap={user.Gap:F3} m warned={user.Warned}, default gap={auto.Gap:F3} m warned={auto.Warned} | \"{user.Line}\"");
    }

    /// <summary>41e: the thermal step kernel carries the device force and the supply-temperature sink on
    /// the GPU too (they ride inside its combined cellData buffer, see StepKernelThermal), so the same
    /// scene must come out the same on both backends. This is the only harness coverage the thermal GPU
    /// kernel has.</summary>
    static void SplitAcGpuParity(Func<string, bool, string, bool> check, Action<string, string> skip)
    {
        const string label = "41e split-ac thermal GPU = CPU (mean T within 0.05 K, duty within 2%)";
        if (!LbmSolver.GpuAvailable) { skip(label, "no GPU available"); return; }

        (float MeanT, float Watts, string Backend) RunAc(Backend backend)
        {
            var (grid, model) = SealedRoom(4f, 4f, 3f, 0.1f, "split-ac-parity");
            model.Env = new Environment { IndoorTemperature = 26f, OutdoorTemperature = 26f };
            model.Sources.Add(new AirSource
            {
                Id = "ac", Name = "Split AC", Level = "L1", Kind = SourceKind.Fan,   // recirculates: no outdoor air crosses the envelope
                Device = DeviceKind.SplitAc, Center = new Vector3(0.25f, 2f, 2.6f), Direction = Vector3.UnitX,
                Width = 0.8f, Height = 0.2f, FlowRate = 0.15f, SupplyTemperature = 14f, LouvreAngle = 30f,
            });
            Voxelizer.ApplyDevices(grid, model.Sources);
            using var session = LbmSolver.Start(grid, model, Vector2.Zero,
                new RunOptions { Backend = backend, Thermal = true });
            session.Step(session.WarmupSteps + 300);   // the average only accumulates past the warm-up
            var field = session.ToField();
            var scratch = new float[grid.Count];
            return (MeanAirTemperature(session, grid, scratch), field.Devices[0].HeatRemovedWatts, session.Backend);
        }

        var cpu = RunAc(Backend.Cpu);
        var gpu = RunAc(Backend.Gpu);
        float dT = MathF.Abs(cpu.MeanT - gpu.MeanT);
        float dQ = MathF.Abs(cpu.Watts - gpu.Watts) / MathF.Max(MathF.Abs(cpu.Watts), 1e-6f);
        check(label, dT <= 0.05f && dQ <= 0.02f,
            $"meanT cpu={cpu.MeanT:F3} gpu={gpu.MeanT:F3} (dT={dT:F4} K) Q cpu={cpu.Watts:F0} gpu={gpu.Watts:F0} " +
            $"(err={dQ * 100:F2}%) backend={gpu.Backend}");
    }

    // ---------------------------------------------------------------------------------------------
    // 44. The remaining device kinds: they must build a region, move roughly the air they are set to,
    //     and leave the mass ledger alone. One sealed room, three devices at once.
    // ---------------------------------------------------------------------------------------------

    static void OtherKindsChecks(Func<string, bool, string, bool> check)
    {
        const float lx = 5f, ly = 5f, lz = 3f, dx = 0.1f;
        var (grid, model) = SealedRoom(lx, ly, lz, dx, "device-kinds");
        model.Sources.Add(new AirSource
        {
            Id = "pedestal", Name = "Pedestal fan", Level = "L1", Kind = SourceKind.Fan,
            Device = DeviceKind.PedestalFan, Center = new Vector3(2.5f, 1.0f, 1.2f), Direction = Vector3.UnitY,
            Radius = 0.25f, Swirl = 0.1f, FlowRate = 0.2f,
        });
        model.Sources.Add(new AirSource
        {
            Id = "cassette", Name = "Cassette", Level = "L1", Kind = SourceKind.Fan,
            Device = DeviceKind.CassetteAc, Center = new Vector3(2.5f, 3.5f, 2.9f), Direction = -Vector3.UnitZ,
            Width = 1.0f, Height = 1.0f, FlowRate = 0.3f, LouvreAngle = 35f,
        });
        model.Sources.Add(new AirSource
        {
            Id = "diffuser", Name = "Diffuser", Level = "L1", Kind = SourceKind.Supply,
            Device = DeviceKind.Diffuser, Center = new Vector3(0.15f, 2.5f, 2.0f), Direction = Vector3.UnitX,
            Width = 0.4f, Height = 0.4f, FlowRate = 0.2f, LouvreAngle = 40f,
        });
        Voxelizer.ApplyDevices(grid, model.Sources);

        using var session = LbmSolver.Start(grid, model, Vector2.Zero, new RunOptions { Backend = Backend.Cpu });
        session.Step(2 * session.PlannedSteps);
        session.ResetAveraging();
        session.Step(session.PlannedSteps);
        var field = session.ToField();
        float dt = (float)(session.SimSeconds / Math.Max(1, session.Steps));

        bool built = field.Devices.Count == 3 && field.Devices.TrueForAll(d => d.Cells > 0);
        bool moved = field.Devices.TrueForAll(d => MathF.Abs(d.FlowRate - d.SetFlowRate) <= 0.4f * d.SetFlowRate);
        bool finite = !float.IsNaN(field.Ux[grid.Index(grid.Nx / 2, grid.Ny / 2, grid.Nz / 2)]);
        float mass = MassResidual(field, dt);
        check("44a pedestal-fan + cassette + diffuser each move their set flow within 40%",
            built && moved && finite,
            string.Join(", ", field.Devices.ConvertAll(d => $"{d.Kind} {d.FlowRate:F3}/{d.SetFlowRate:F3} m3/s ({d.Cells} cells)")));
        check("44b three devices together still conserve mass (|mean rho - 1| <= 1e-3)",
            MathF.Abs(mass) <= 1e-3f, $"massResidual={mass:E2} steps={session.Steps}");

        // A hood with nothing open in front of it cannot remove air, and has to say so rather than
        // pretending: the warning is the documented behaviour, not a failure.
        var (sealedGrid, sealedModel) = SealedRoom(3f, 3f, 3f, 0.15f, "hood-sealed");
        sealedModel.Sources.Add(new AirSource
        {
            Id = "hood", Name = "Hood", Level = "L1", Kind = SourceKind.Return,
            Device = DeviceKind.Hood, Center = new Vector3(1.5f, 1.5f, 2.6f), Direction = Vector3.UnitZ,
            Width = 0.9f, Height = 0.9f, Radius = 0.45f, FlowRate = 0.25f,
        });
        Voxelizer.ApplyDevices(sealedGrid, sealedModel.Sources);
        bool warned = sealedGrid.Warnings.Exists(w => w.Contains("cannot remove air"));
        check("44c hood with no opening in front of it warns and still builds a capture region",
            warned && sealedGrid.Devices.Count == 1 && sealedGrid.Devices[0].Cells.Length > 0,
            $"cells={(sealedGrid.Devices.Count > 0 ? sealedGrid.Devices[0].Cells.Length : 0)} " +
            $"warnings=[{string.Join("; ", sealedGrid.Warnings)}]");
    }

    // ---------------------------------------------------------------------------------------------
    // 42. Regression: the original fixed-velocity Patch source is untouched by any of the above.
    //     Same scene, cell budget and wind as Program.cs's "fan-nowind" (check 14a).
    // ---------------------------------------------------------------------------------------------

    static void PatchRegressionCheck(Func<string, bool, string, bool> check)
    {
        var floor = ClosedRoom("fan-nowind");
        floor.Sources.Add(new AirSource
        {
            Id = "fan", Name = "Fan", Level = "L1", Kind = SourceKind.Fan,
            Center = new Vector3(4, 3, 1.2f), Direction = new Vector3(0, 1, 0),
            Width = 0.4f, Height = 0.4f, FlowRate = 0.2f,
        });

        var result = Simulation.Run(floor, Vector2.Zero, new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu });
        var flow = result.Sources.FirstOrDefault(s => s.Source.Id == "fan");
        float measured = flow?.FlowRate ?? 0f;
        check("42 fan-nowind Patch source still measures 0.200 m3/s within 20%",
            flow != null && MathF.Abs(MathF.Abs(measured) - 0.2f) <= 0.04f,
            $"measured={measured:F3} m3/s fixedCells={result.Field.Grid.FixedCells.Count} devices={result.Field.Devices.Count}");
    }

    // ---------------------------------------------------------------------------------------------
    // 43. Exhaust fan in a duct through an exterior wall, one open window on the opposite wall:
    //     what the fan pushes out has to come back in through the window.
    // ---------------------------------------------------------------------------------------------

    static void ExhaustFanCheck(Func<string, bool, string, bool> check)
    {
        const float flow = 0.2f;
        var floor = ClosedRoom("exhaust");
        floor.Openings[0].IsOpen = true;    // west window
        floor.Openings.Add(new Opening
        {
            // Same size as the window it draws through: Analysis.AnalyzeOpening measures a box-mean speed
            // over the CARVED cells and multiplies by the NOMINAL area, so two openings of different sizes
            // carry different biases and the ledger cannot balance them against each other.
            Id = "duct", Name = "duct", Kind = OpeningKind.Opening, IsOpen = true,
            Center = new Vector3(8, 3, 2.5f), Normal = Vector3.UnitX, Width = 1.2f, Height = 1.2f, Depth = 0.2f,
        });
        floor.Sources.Add(new AirSource
        {
            Id = "extract", Name = "Extract fan", Level = "L1", Kind = SourceKind.Return,
            Device = DeviceKind.ExhaustFan, Center = new Vector3(8, 3, 2.5f), Direction = Vector3.UnitX,
            // 1.0 m, inside the 1.2 m duct: the device region used to be 1.6 m, wider than its hole, so it also drove air
            // into the wall around the duct mouth. The device meter counted that flux, and interpolated bounce-back
            // (which then leaked mass) absorbed it, so the old window/plane/device agreement was two errors cancelling.
            Width = 1.0f, Height = 1.0f, FlowRate = flow,
        });

        var result = Simulation.Run(floor, Vector2.Zero, new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu });
        var field = result.Field;
        var g = field.Grid;
        var dev = field.Devices.Count > 0 ? field.Devices[0] : null;
        var ledger = result.Ledger;

        // Net flux east-ward through a vertical plane inside the room, 1 m in from the west wall: in a
        // steady state that has to equal what the extract removes. Integrated here rather than read off
        // Analysis.OpeningFlow, which multiplies a mean speed by the NOMINAL opening area.
        float through = PlaneFlux(field, 1.0f, 0.3f, 5.7f, 0.15f, 2.85f);
        var west = result.Openings.FirstOrDefault(o => o.Opening.Id == "west");

        check("43 exhaust-fan inflow through the window within 25% of 0.200 m3/s",
            MathF.Abs(through - flow) <= 0.25f * flow,
            $"planeFlux={through:F3} m3/s extractMeasured={dev?.FlowRate ?? 0f:F3} set={flow:F3} " +
            $"westOpening={(west != null ? west.FlowRate.ToString("F3") : "n/a")} " +
            $"Dx={g.Dx * 1000:F0}mm cells={dev?.Cells ?? 0}" +
            (g.Warnings.Count > 0 ? $" warnings=[{string.Join("; ", g.Warnings)}]" : ""));

        // The extract has to appear in the ledger's Out exactly once. It sits in a duct that is itself an
        // exterior opening, so the opening carries it and the device must NOT be added on top.
        var sf = result.Sources.FirstOrDefault(x => x.Source.Id == "extract");
        // Band against the DEVICE's own measured flow, not the nameplate: the ledger reads the opening with
        // Analysis.AnalyzeOpening (box-mean speed x nominal area, a known ~14 % under-read), so mixing the
        // two biases would only make this a check of that. Doubling would show up as ~2x either way.
        float measured = dev?.FlowRate ?? 0f;
        // 43b band (10 %, the ledger's IsPhysical threshold) unchanged by the carved-throat opening flow: the
        // residual rose 0.1 % -> 5.5 % because the window side is now a cell-centred u.n sum over the throat
        // (0.158 m3/s) while the device side is the solver's own flux (0.167 out); a cell-centred sum across a plane
        // next to walls reads a few % below the lattice's link flux (the mid-room plane in 43 reads 0.171). The old
        // 0.1 % was the box-mean x nominal-area estimate landing on the device flux by coincidence.
        check("43b exhaust-fan counted once in the mass ledger (Out ~ its measured flow, residual <= 10%)",
            MathF.Abs(ledger.Out - measured) <= 0.25f * measured && ledger.Residual <= 0.10f && ledger.IsPhysical,
            $"In={ledger.In:F3} Out={ledger.Out:F3} (device measured {measured:F3}, set {flow:F3}) " +
            $"residual={ledger.Residual * 100:F1}% physical={ledger.IsPhysical} " +
            $"SourceFlow={sf?.FlowRate ?? 0f:F3} m3/s | {string.Join(" | ", ledger.Notes)}");
    }

    // ---------------------------------------------------------------------------------------------
    // 45. Devices in the room and building balances: a device that brings outdoor air in must show up in
    //     RoomResult.Inflow and in the ledger's In; one that only stirs the room must show up in neither.
    // ---------------------------------------------------------------------------------------------

    static void LedgerChecks(Func<string, bool, string, bool> check)
    {
        const float supplyFlow = 0.2f, fanFlow = 1.5f;

        // 45a: a supply diffuser sitting in a duct through the east wall, west window open as the relief.
        var floor = ClosedRoom("device-supply");
        floor.Openings[0].IsOpen = true;    // west window
        floor.Openings.Add(new Opening
        {
            Id = "duct", Name = "duct", Kind = OpeningKind.Opening, IsOpen = true,
            Center = new Vector3(8, 3, 2.5f), Normal = Vector3.UnitX, Width = 0.8f, Height = 0.8f, Depth = 0.2f,
        });
        floor.Sources.Add(new AirSource
        {
            Id = "supply", Name = "Supply diffuser", Level = "L1", Kind = SourceKind.Supply,
            Device = DeviceKind.Diffuser, Center = new Vector3(8, 3, 2.5f), Direction = -Vector3.UnitX,
            Width = 1.2f, Height = 1.2f, FlowRate = supplyFlow, LouvreAngle = 20f,
        });
        var supply = Simulation.Run(floor, Vector2.Zero, new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu });
        var room = supply.Rooms.FirstOrDefault();
        var supplyLedger = supply.Ledger;
        var supplyFlowResult = supply.Sources.FirstOrDefault(s => s.Source.Id == "supply");

        // 45a: same ledger reasoning as 43b; its residual moved 2.6 % -> 5.6 % with the throat integral, band unchanged.
        check("45a supply device: room Inflow and ledger In both ~ 0.200 m3/s (within 30%), counted once",
            room != null && MathF.Abs(room.Inflow - supplyFlow) <= 0.3f * supplyFlow
                && MathF.Abs(supplyLedger.In - supplyFlow) <= 0.3f * supplyFlow && supplyLedger.Residual <= 0.10f,
            $"roomInflow={room?.Inflow ?? 0f:F3} ledgerIn={supplyLedger.In:F3} Out={supplyLedger.Out:F3} " +
            $"residual={supplyLedger.Residual * 100:F1}% device={supplyFlowResult?.FlowRate ?? 0f:F3} m3/s " +
            $"ACH={room?.Ach ?? 0f:F2}");

        // 45c: a ceiling fan in a closed room moves air without any of it crossing the envelope.
        var closed = ClosedRoom("device-fan");
        closed.Sources.Add(new AirSource
        {
            Id = "ceilingfan", Name = "Ceiling fan", Level = "L1", Kind = SourceKind.Fan,
            // 2.2 m, not right under the 3 m ceiling: at this scene's 268 mm the disc needs ~3 cells of
            // intake clearance or it chokes (44 % of set at 1.5 cells; the voxelizer warns below 3).
            Device = DeviceKind.CeilingFan, Center = new Vector3(4, 3, 2.2f), Direction = -Vector3.UnitZ,
            Radius = 1.0f, Swirl = 0.3f, FlowRate = fanFlow,
        });
        var fan = Simulation.Run(closed, Vector2.Zero, new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu });
        var fanRoom = fan.Rooms.FirstOrDefault();
        var fanSource = fan.Sources.FirstOrDefault(s => s.Source.Id == "ceilingfan");
        bool ledgerClean = fan.Ledger.In < 1e-3f && fan.Ledger.Out < 1e-3f && fan.Ledger.IsPhysical;
        bool reported = fanSource != null && MathF.Abs(fanSource.FlowRate - fanFlow) <= 0.25f * fanFlow;

        check("45b recirculating fan: nothing in the ledger, still reported in SourceFlow",
            ledgerClean && reported && fanRoom != null && fanRoom.MeanSpeed > 0.05f,
            $"ledgerIn={fan.Ledger.In:F4} Out={fan.Ledger.Out:F4} physical={fan.Ledger.IsPhysical} " +
            $"SourceFlow={fanSource?.FlowRate ?? 0f:F3} of {fanFlow:F3} m3/s roomMeanSpeed={fanRoom?.MeanSpeed ?? 0f:F3} m/s " +
            $"roomInflow={fanRoom?.Inflow ?? 0f:F4} m3/s (must stay ~0: the fan creates no air)");
    }

    // ---------------------------------------------------------------------------------------------
    // Scenes
    // ---------------------------------------------------------------------------------------------

    /// <summary>A sealed box of air with a one-cell solid shell (and layer 0 solid, the grid convention),
    /// hand-built so the whole cell budget lands in the room. Interior cell (1,1,1) starts at the origin,
    /// so the room occupies [0,lx] x [0,ly] x [0,lz]. The matching BuildingModel carries a storey, the
    /// environment and a dummy footprint triangle, which is all LbmSolver's thermal setup needs
    /// (Voxelizer.PlanBounds throws on an empty model).</summary>
    static (VoxelGrid Grid, BuildingModel Model) SealedRoom(float lx, float ly, float lz, float dx, string name)
    {
        int nx = (int)MathF.Round(lx / dx) + 2, ny = (int)MathF.Round(ly / dx) + 2, nz = (int)MathF.Round(lz / dx) + 2;
        var grid = new VoxelGrid
        {
            Nx = nx, Ny = ny, Nz = nz, Dx = dx,
            Origin = new Vector3(-dx, -dx, -dx),
            Cell = new byte[nx * ny * nz],
        };
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            bool solid = x == 0 || x == nx - 1 || y == 0 || y == ny - 1 || z == 0 || z == nz - 1;
            grid.Cell[grid.Index(x, y, z)] = solid ? VoxelGrid.Solid : VoxelGrid.Air;
        }

        var model = new BuildingModel
        {
            Name = name, GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = lz, Analysed = true } },
        };
        model.Obstacles.Add(new Mesh
        {
            Id = "footprint",
            Vertices = new[] { new Vector3(0, 0, 0), new Vector3(lx, 0, 0), new Vector3(lx, ly, 0), new Vector3(0, ly, 0) },
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
        });
        model.Rooms.Add(new Room
        {
            Id = "room", Name = "Room", Level = "L1", ZMin = 0, ZMax = lz,
            Loops = { new[] { new Vector2(0.05f, 0.05f), new Vector2(lx - 0.05f, 0.05f),
                              new Vector2(lx - 0.05f, ly - 0.05f), new Vector2(0.05f, ly - 0.05f) } },
        });
        return (grid, model);
    }

    /// <summary>The 8 x 6 m single-room box Program.cs's scenes use, both windows shut. Kept local (the
    /// harness's own builders are private to Program) and deliberately identical in geometry and cell
    /// budget, so check 42 reproduces check 14a's number.</summary>
    static BuildingModel ClosedRoom(string name)
    {
        const float h = 3f;
        var floor = new BuildingModel
        {
            Name = name, GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = h, Analysed = true } },
        };
        floor.Obstacles.Add(Wall(0, 0, 8, 0, h));
        floor.Obstacles.Add(Wall(8, 0, 8, 6, h));
        floor.Obstacles.Add(Wall(8, 6, 0, 6, h));
        floor.Obstacles.Add(Wall(0, 6, 0, 0, h));
        floor.Obstacles.Add(Box(new Vector3(-1, -1, -0.2f), new Vector3(9, 7, 0f)));      // ground slab
        floor.Obstacles.Add(Box(new Vector3(0, 0, h), new Vector3(8, 6, h + 0.2f)));      // roof slab
        floor.Openings.Add(WindowAt("west", new Vector3(0, 3, 1.5f), new Vector3(-1, 0, 0)));
        floor.Openings.Add(WindowAt("east", new Vector3(8, 3, 1.5f), new Vector3(1, 0, 0)));
        floor.Rooms.Add(new Room
        {
            Id = "room", Name = "Room", Level = "L1", ZMin = 0, ZMax = h,
            Loops = { new[] { new Vector2(0.1f, 0.1f), new Vector2(7.9f, 0.1f), new Vector2(7.9f, 5.9f), new Vector2(0.1f, 5.9f) } },
        });
        return floor;
    }

    static Opening WindowAt(string id, Vector3 center, Vector3 normal) => new()
    {
        Id = id, Name = id, Kind = OpeningKind.Window, IsOpen = false,
        Center = center, Normal = normal, Width = 1.2f, Height = 1.2f, Depth = 0.2f,
    };

    static Mesh Wall(float x0, float y0, float x1, float y1, float height, float thickness = 0.2f)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float half = thickness * 0.5f;
        if (MathF.Abs(x1 - x0) < 1e-6f) { minX -= half; maxX += half; }
        else { minY -= half; maxY += half; }
        return Box(new Vector3(minX, minY, 0), new Vector3(maxX, maxY, height));
    }

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

    // ---------------------------------------------------------------------------------------------
    // Measurements
    // ---------------------------------------------------------------------------------------------

    /// <summary>Mean density departure from 1 over the air cells, i.e. how much mass the run gained or
    /// lost. field.P is (mean rho - 1) * pScale with pScale = (1/3)(Dx/dt)^2 * rho_air, and dt comes from
    /// the session (SimSeconds / Steps), so this inverts back to the lattice's own mass ledger. A Fixed
    /// patch shows up here; a body force cannot.</summary>
    static float MassResidual(FlowField field, float dt)
    {
        var g = field.Grid;
        float pScale = (1f / 3f) * (g.Dx / dt) * (g.Dx / dt) * 1.2f;
        double sum = 0; int n = 0;
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Cell[i] != VoxelGrid.Air) continue;
            sum += field.P[i]; n++;
        }
        return n == 0 ? 0f : (float)(sum / n) / pScale;
    }

    /// <summary>Volume flow downward through the horizontal plane at the fan's own height, integrated over
    /// the disc's footprint - an independent measurement of what the actuator disc achieved (the solver's
    /// own DeviceResult.FlowRate integrates over the driven cells instead).</summary>
    static float DiscFlux(FlowField field, Vector3 axis, float radius)
    {
        var g = field.Grid;
        int z = Math.Clamp((int)MathF.Floor((axis.Z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
        double sum = 0;
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, z);
            if (g.Cell[idx] != VoxelGrid.Air) continue;
            var c = g.CellCenter(x, y, z);
            float dxs = c.X - axis.X, dys = c.Y - axis.Y;
            if (dxs * dxs + dys * dys > radius * radius) continue;
            sum += -field.Uz[idx] * g.Dx * g.Dx;
        }
        return (float)sum;
    }

    /// <summary>Mean vertical velocity outside the fan's wake (radius >= rMin) between two heights: the
    /// recirculation that comes back up the walls.</summary>
    static float WallReturn(FlowField field, Vector3 axis, float rMin, float zLo, float zHi)
    {
        var g = field.Grid;
        double sum = 0; int n = 0;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, z);
            if (g.Cell[idx] != VoxelGrid.Air) continue;
            var c = g.CellCenter(x, y, z);
            if (c.Z < zLo || c.Z > zHi) continue;
            float dxs = c.X - axis.X, dys = c.Y - axis.Y;
            if (dxs * dxs + dys * dys < rMin * rMin) continue;
            sum += field.Uz[idx]; n++;
        }
        return n == 0 ? 0f : (float)(sum / n);
    }

    static float LayerMeanSpeed(FlowField field, float z)
    {
        var g = field.Grid;
        int zi = Math.Clamp((int)MathF.Floor((z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
        double sum = 0; int n = 0;
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, zi);
            if (g.Cell[idx] != VoxelGrid.Air) continue;
            sum += new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]).Length(); n++;
        }
        return n == 0 ? 0f : (float)(sum / n);
    }

    /// <summary>Net +X volume flux through the vertical plane at world x, restricted to the room's own
    /// y/z window so the padded exterior domain does not join in.</summary>
    static float PlaneFlux(FlowField field, float x, float yLo, float yHi, float zLo, float zHi)
    {
        var g = field.Grid;
        int xi = Math.Clamp((int)MathF.Floor((x - g.Origin.X) / g.Dx), 0, g.Nx - 1);
        double sum = 0;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        {
            int idx = g.Index(xi, y, z);
            if (g.Cell[idx] != VoxelGrid.Air) continue;
            var c = g.CellCenter(xi, y, z);
            if (c.Y < yLo || c.Y > yHi || c.Z < zLo || c.Z > zHi) continue;
            sum += field.Ux[idx] * g.Dx * g.Dx;
        }
        return (float)sum;
    }

    static float MeanAirTemperature(LbmSolver.LbmSession session, VoxelGrid grid, float[] scratch)
    {
        session.ReadTemperature(scratch);
        double sum = 0; int n = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.Cell[i] != VoxelGrid.Air) continue;
            sum += scratch[i]; n++;
        }
        return n == 0 ? float.NaN : (float)(sum / n);
    }

    /// <summary>Horizontal mean T over the air cells of the layer containing world height z, skipping the
    /// cells the device itself drives (so the measurement is independent of what the device does to them).</summary>
    static float LayerMeanT(FlowField field, float z, HashSet<int> skip)
    {
        var g = field.Grid;
        int zi = Math.Clamp((int)MathF.Floor((z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
        double sum = 0; int n = 0;
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, zi);
            if (g.Cell[idx] != VoxelGrid.Air || skip.Contains(idx)) continue;
            sum += field.T[idx]; n++;
        }
        return n == 0 ? float.NaN : (float)(sum / n);
    }

    static float RoomMeanT(FlowField field)
    {
        var g = field.Grid;
        double sum = 0; int n = 0;
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Cell[i] != VoxelGrid.Air) continue;
            sum += field.T[i]; n++;
        }
        return n == 0 ? float.NaN : (float)(sum / n);
    }

    /// <summary>Vertical temperature profile of the room, as evidence for where an intake is reading.</summary>
    static string Profile(FlowField field, float height)
    {
        var parts = new List<string>();
        for (float z = 0.25f; z < height; z += 0.5f)
            parts.Add($"{z:F2}m={LayerMeanT(field, z, new HashSet<int>()):F2}");
        return string.Join(" ", parts);
    }

    static int AirCells(VoxelGrid grid)
    {
        int n = 0;
        for (int i = 0; i < grid.Count; i++) if (grid.Cell[i] == VoxelGrid.Air) n++;
        return n;
    }
}
