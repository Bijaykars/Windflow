// WindFlow end-to-end verification console for the whole-building 3D domain.
// No args: builds the fixed set of scenes below, runs the solver, checks the numbers against
// physically-motivated bounds and prints PASS/FAIL, and (best-effort) renders report images.
// <path.json> [windX windY] [--gpu|--cpu] [--refine]: replays a BuildingModel dumped by the Revit
// add-in and writes its own images. Exit code 1 if any check fails.

using System.IO;
using System.Numerics;
using System.Windows.Media.Imaging;
using WindFlow.Core;
using WindFlow.Report;
using WindFlow.Viz;
using Environment = WindFlow.Core.Environment;   // disambiguate from System.Environment

namespace WindFlow.Sandbox;

static class Program
{
    const float H = 3f;   // storey height for the single-storey scenes

    [STAThread]
    static int Main(string[] args)
    {
        // Body-force HVAC devices and wall treatment have their own scenes and their own harnesses; both
        // also run at the end of the default (no-argument) pass, so one command covers everything.
        // `--compare <baseline.json>` combines with any check mode: run normally, then print the delta.
        string? baseline = null;
        int ci = Array.IndexOf(args, "--compare");
        if (ci >= 0)
        {
            if (ci + 1 >= args.Length) { Console.WriteLine("--compare needs a baseline.json path"); return 2; }
            baseline = Path.GetFullPath(args[ci + 1]);
            args = args.Where((_, i) => i != ci && i != ci + 1).ToArray();
        }

        string repoRoot = FindRepoRoot();
        string outDir = Path.Combine(repoRoot, "out");
        Directory.CreateDirectory(outDir);

        int code;
        if (args.Length == 1 && args[0] == "--devices") code = DeviceChecks.Run();
        else if (args.Length == 1 && args[0] == "--ac-balance") code = DeviceChecks.RunAcBalance();
        else if (args.Length == 1 && args[0] == "--walls") code = WallChecks.Run();
        else if (args.Length == 1 && args[0] == "--nested") code = NestedChecks.Run();
        else if (args.Length == 1 && args[0] == "--inlet") code = InletChecks.Run();
        else if (args.Length == 1 && args[0] == "--warmstart") code = WarmStartChecks.Run();
        else if (args.Length == 1 && args[0] == "--boundary") code = BoundaryChecks.Run();
        else if (args.Length == 1 && args[0] == "--audit") code = AuditChecks.Run();
        else if (args.Length == 1 && args[0] == "--fixes") code = FixChecks.Run();
        else if (args.Length == 1 && args[0] == "--fluxcons") code = FixChecks.RunFluxCons();
        else if (args.Length == 1 && args[0] == "--hold") code = HoldChecks.Run();
        else if (args.Length == 1 && args[0] == "--quiescent")
        {
            var (pass, detail) = Quiescent19d();
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] 19d quiescent sealed room  {detail}");
            code = pass ? 0 : 1;
        }
        else if (args.Length == 1 && args[0] == "--hybrid-gpu")
        {
            if (!LbmSolver.GpuAvailable) { Console.WriteLine("[WAIT] 19f hybrid thermal GPU = CPU  no GPU"); code = 0; }
            else
            {
                var (pass, detail) = HybridGpuCpu19f();
                Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] 19f hybrid thermal GPU = CPU  {detail}");
                var (passAge, detailAge) = FixChecks.AgeGpuCpu();   // 6k: the online age's GPU kernel = its CPU twin
                Console.WriteLine($"[{(passAge ? "PASS" : "FAIL")}] 6k online age GPU = CPU  {detailAge}");
                code = pass && passAge ? 0 : 1;
            }
        }
        else if (args.Length == 1 && args[0] == "--converge-stats")
        {
            var (pass, detail) = ConvergeStats54b();
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] 54b convergence statistics  {detail}");
            code = pass ? 0 : 1;
        }
        else if (args.Length == 1 && args[0] == "--orifice") code = OrificeChecks.Run();
        else if (args.Length >= 2 && args[0] == "--tu-run") code = InletChecks.RunTu(args[1..]);
        else if (args.Length >= 2 && args[0] == "--orifice-run") code = OrificeChecks.RunSpecs(args[1..]);
        else if (args.Length == 1 && args[0] == "--wallmodel") code = WallModelChecks.Run();
        else if (args.Length == 1 && args[0] == "--nested-speed") { NestedChecks.SpeedProbe(); code = 0; }
        else if (args.Length == 1 && args[0] == "--plume19a") { Plume19a(); code = 0; }   // plume-model measurement: 19a at WF_DX (m), WINDFLOW_PLUME_MODEL=1 for on
        else if (args.Length == 0) code = RunAllChecks(outDir);
        else return RunReplay(args, outDir);

        CheckLog.Write(Path.Combine(outDir, "checks.json"));
        if (baseline != null) CheckLog.Compare(baseline);
        return code;
    }

    // ---------------------------------------------------------------------------------------------
    // Geometry helpers
    // ---------------------------------------------------------------------------------------------

    static Mesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] v =
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        };
        int[] idx =
        {
            0, 1, 2,  0, 2, 3,   // bottom (z = min)
            4, 6, 5,  4, 7, 6,   // top (z = max)
            0, 4, 5,  0, 5, 1,   // y = min
            1, 5, 6,  1, 6, 2,   // x = max
            2, 6, 7,  2, 7, 3,   // y = max
            3, 7, 4,  3, 4, 0,   // x = min
        };
        return new Mesh { Id = Guid.NewGuid().ToString("N"), Vertices = v, Indices = idx };
    }

    // Axis-aligned wall box centred on the segment (x0,y0)-(x1,y1); thickness is added perpendicular
    // to the segment (not along it), spanning z in [0, height].
    static Mesh Wall(float x0, float y0, float x1, float y1, float thickness = 0.2f, float height = 3f)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float half = thickness * 0.5f;
        bool vertical = MathF.Abs(x1 - x0) < 1e-6f;
        bool horizontal = MathF.Abs(y1 - y0) < 1e-6f;
        if (vertical) { minX -= half; maxX += half; }
        else if (horizontal) { minY -= half; maxY += half; }
        else { minX -= half; maxX += half; minY -= half; maxY += half; } // diagonal fallback, unused by the scenes below
        return Box(new Vector3(minX, minY, 0), new Vector3(maxX, maxY, height));
    }

    static Opening Window(string id, Vector3 center, Vector3 normal, bool isOpen = true) => new()
    {
        Id = id, Name = id, Kind = OpeningKind.Window, IsOpen = isOpen,
        Center = center, Normal = normal, Width = 1.2f, Height = 1.2f, Depth = 0.2f,
    };

    // Ground slab: z in [-0.2, 0], spanning the footprint plus a margin.
    static void AddGroundSlab(BuildingModel floor, float minX, float minY, float maxX, float maxY, float margin = 1f) =>
        floor.Obstacles.Add(Box(new Vector3(minX - margin, minY - margin, -0.2f), new Vector3(maxX + margin, maxY + margin, 0f)));

    // Roof slab: z in [roofZ, roofZ + 0.2], the footprint only.
    static void AddRoofSlab(BuildingModel floor, float minX, float minY, float maxX, float maxY, float roofZ) =>
        floor.Obstacles.Add(Box(new Vector3(minX, minY, roofZ), new Vector3(maxX, maxY, roofZ + 0.2f)));

    static Vector2[] RectLoop(float x0, float y0, float x1, float y1) =>
        new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

    // ---------------------------------------------------------------------------------------------
    // Scenes
    // ---------------------------------------------------------------------------------------------

    internal static BuildingModel BuildCross(string name, bool westOpen, bool eastOpen)
    {
        var floor = new BuildingModel
        {
            Name = name, GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = H, Analysed = true } },
        };
        floor.Obstacles.Add(Wall(0, 0, 8, 0, 0.2f, H));   // south, y = 0
        floor.Obstacles.Add(Wall(8, 0, 8, 6, 0.2f, H));   // east, x = 8
        floor.Obstacles.Add(Wall(8, 6, 0, 6, 0.2f, H));   // north, y = 6
        floor.Obstacles.Add(Wall(0, 6, 0, 0, 0.2f, H));   // west, x = 0
        AddGroundSlab(floor, 0, 0, 8, 6);
        AddRoofSlab(floor, 0, 0, 8, 6, H);
        floor.Openings.Add(Window("west", new Vector3(0, 3, 1.5f), new Vector3(-1, 0, 0), westOpen));
        floor.Openings.Add(Window("east", new Vector3(8, 3, 1.5f), new Vector3(1, 0, 0), eastOpen));
        floor.Rooms.Add(new Room
        {
            Id = "room", Name = "Room", Level = "L1",
            Loops = { RectLoop(0.1f, 0.1f, 7.9f, 5.9f) },
            ZMin = 0, ZMax = H,
        });
        return floor;
    }

    static BuildingModel BuildTwoRooms(string name, bool doorOpen)
    {
        var floor = new BuildingModel
        {
            Name = name, GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = H, Analysed = true } },
        };
        floor.Obstacles.Add(Wall(0, 0, 10, 0, 0.2f, H));   // south
        floor.Obstacles.Add(Wall(10, 0, 10, 5, 0.2f, H));  // east
        floor.Obstacles.Add(Wall(10, 5, 0, 5, 0.2f, H));   // north
        floor.Obstacles.Add(Wall(0, 5, 0, 0, 0.2f, H));    // west
        floor.Obstacles.Add(Wall(5, 0, 5, 5, 0.2f, H));    // interior wall, x = 5
        AddGroundSlab(floor, 0, 0, 10, 5);
        AddRoofSlab(floor, 0, 0, 10, 5, H);
        floor.Openings.Add(Window("west", new Vector3(0, 2.5f, 1.5f), new Vector3(-1, 0, 0)));
        floor.Openings.Add(Window("east", new Vector3(10, 2.5f, 1.5f), new Vector3(1, 0, 0)));
        floor.Openings.Add(new Opening
        {
            Id = "door", Name = "door", Kind = OpeningKind.Door, IsOpen = doorOpen,
            Center = new Vector3(5, 2.5f, 1.05f), Normal = new Vector3(1, 0, 0),
            Width = 0.9f, Height = 2.1f, Depth = 0.2f,
        });
        floor.Rooms.Add(new Room { Id = "A", Name = "Room A", Level = "L1", Loops = { RectLoop(0.1f, 0.1f, 4.9f, 4.9f) }, ZMin = 0, ZMax = H });
        floor.Rooms.Add(new Room { Id = "B", Name = "Room B", Level = "L1", Loops = { RectLoop(5.1f, 0.1f, 9.9f, 4.9f) }, ZMin = 0, ZMax = H });
        return floor;
    }

    // 8x6 plan, L1 (0-3, analysed) + L2 (3-6, not analysed); full-footprint slab between them.
    static BuildingModel BuildTwoStorey(string name, bool closedAll)
    {
        var floor = new BuildingModel
        {
            Name = name, GroundZ = 0,
            Storeys =
            {
                new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true },
                new Storey { Id = "L2", Name = "Level 2", Elevation = 3, Height = 3, Analysed = false },
            },
        };
        floor.Obstacles.Add(Wall(0, 0, 8, 0, 0.2f, 6f));
        floor.Obstacles.Add(Wall(8, 0, 8, 6, 0.2f, 6f));
        floor.Obstacles.Add(Wall(8, 6, 0, 6, 0.2f, 6f));
        floor.Obstacles.Add(Wall(0, 6, 0, 0, 0.2f, 6f));
        AddGroundSlab(floor, 0, 0, 8, 6);
        floor.Obstacles.Add(Box(new Vector3(0, 0, 3f), new Vector3(8, 6, 3.2f)));   // L1/L2 slab, no hole
        AddRoofSlab(floor, 0, 0, 8, 6, 6f);

        bool l1Open = !closedAll;
        floor.Openings.Add(Window("west-l1", new Vector3(0, 3, 1.5f), new Vector3(-1, 0, 0), l1Open));
        floor.Openings.Add(Window("east-l1", new Vector3(8, 3, 1.5f), new Vector3(1, 0, 0), l1Open));
        // L2 is not analysed: its openings are forced closed, the way the Revit extractor would.
        floor.Openings.Add(Window("west-l2", new Vector3(0, 3, 4.5f), new Vector3(-1, 0, 0), false));
        floor.Openings.Add(Window("east-l2", new Vector3(8, 3, 4.5f), new Vector3(1, 0, 0), false));

        floor.Rooms.Add(new Room { Id = "L1room", Name = "Room L1", Level = "L1", Loops = { RectLoop(0.1f, 0.1f, 7.9f, 5.9f) }, ZMin = 0, ZMax = 3 });
        return floor;
    }

    // twostorey, but L2 is analysed too and a 2x2 m hole connects the storeys at the plan centre.
    static BuildingModel BuildAtrium()
    {
        var floor = new BuildingModel
        {
            Name = "atrium", GroundZ = 0,
            Storeys =
            {
                new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true },
                new Storey { Id = "L2", Name = "Level 2", Elevation = 3, Height = 3, Analysed = true },
            },
        };
        floor.Obstacles.Add(Wall(0, 0, 8, 0, 0.2f, 6f));
        floor.Obstacles.Add(Wall(8, 0, 8, 6, 0.2f, 6f));
        floor.Obstacles.Add(Wall(8, 6, 0, 6, 0.2f, 6f));
        floor.Obstacles.Add(Wall(0, 6, 0, 0, 0.2f, 6f));
        AddGroundSlab(floor, 0, 0, 8, 6);

        // L1/L2 slab with a 2x2 m hole at the plan centre (x in [3,5], y in [2,4]), as 4 boxes around it.
        floor.Obstacles.Add(Box(new Vector3(0, 0, 3f), new Vector3(8, 2, 3.2f)));   // south strip
        floor.Obstacles.Add(Box(new Vector3(0, 4, 3f), new Vector3(8, 6, 3.2f)));   // north strip
        floor.Obstacles.Add(Box(new Vector3(0, 2, 3f), new Vector3(3, 4, 3.2f)));   // west middle strip
        floor.Obstacles.Add(Box(new Vector3(5, 2, 3f), new Vector3(8, 4, 3.2f)));   // east middle strip
        AddRoofSlab(floor, 0, 0, 8, 6, 6f);

        floor.Openings.Add(Window("west-l1", new Vector3(0, 3, 1.5f), new Vector3(-1, 0, 0), true));
        floor.Openings.Add(Window("east-l1", new Vector3(8, 3, 1.5f), new Vector3(1, 0, 0), true));
        floor.Openings.Add(Window("west-l2", new Vector3(0, 3, 4.5f), new Vector3(-1, 0, 0), true));
        floor.Openings.Add(Window("east-l2", new Vector3(8, 3, 4.5f), new Vector3(1, 0, 0), true));

        floor.Rooms.Add(new Room { Id = "L1room", Name = "Room L1", Level = "L1", Loops = { RectLoop(0.1f, 0.1f, 7.9f, 5.9f) }, ZMin = 0, ZMax = 3 });
        floor.Rooms.Add(new Room { Id = "L2room", Name = "Room L2", Level = "L2", Loops = { RectLoop(0.1f, 0.1f, 7.9f, 5.9f) }, ZMin = 3, ZMax = 6 });
        return floor;
    }

    internal static BuildingModel BuildSupply()
    {
        var floor = BuildCross("supply", false, false);
        floor.Sources.Add(new AirSource
        {
            Id = "supply", Name = "Supply", Level = "L1", Kind = SourceKind.Supply,
            Center = new Vector3(2, 2, 2.95f), Direction = new Vector3(0, 0, -1),
            Width = 0.6f, Height = 0.6f, FlowRate = 0.139f,
        });
        floor.Sources.Add(new AirSource
        {
            Id = "return", Name = "Return", Level = "L1", Kind = SourceKind.Return,
            Center = new Vector3(6, 4, 2.95f), Direction = new Vector3(0, 0, 1),
            Width = 0.6f, Height = 0.6f, FlowRate = 0.139f,
        });
        return floor;
    }

    internal static BuildingModel BuildFan()
    {
        var floor = BuildCross("fan", true, true);
        floor.Sources.Add(new AirSource
        {
            Id = "fan", Name = "Fan", Level = "L1", Kind = SourceKind.Fan,
            Center = new Vector3(4, 3, 1.2f), Direction = new Vector3(0, 1, 0),
            Width = 0.4f, Height = 0.4f, FlowRate = 0.2f,
        });
        return floor;
    }

    // Closed room (both windows shut) with only the fan moving air, zero ambient wind: isolates the fan
    // from façade-driven cross flow. Run with wind (0,0).
    static BuildingModel BuildFanNoWind()
    {
        var floor = BuildCross("fan-nowind", false, false);
        floor.Sources.Add(new AirSource
        {
            Id = "fan", Name = "Fan", Level = "L1", Kind = SourceKind.Fan,
            Center = new Vector3(4, 3, 1.2f), Direction = new Vector3(0, 1, 0),
            Width = 0.4f, Height = 0.4f, FlowRate = 0.2f,
        });
        return floor;
    }

    // Two 1x1 m openings on opposite walls at different heights: west low (z=0.5), east high (z=2.5).
    // Thermal stack effect only, no ambient wind.
    static BuildingModel BuildStack()
    {
        var floor = new BuildingModel
        {
            Name = "stack", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = H, Analysed = true } },
        };
        floor.Obstacles.Add(Wall(0, 0, 8, 0, 0.2f, H));
        floor.Obstacles.Add(Wall(8, 0, 8, 6, 0.2f, H));
        floor.Obstacles.Add(Wall(8, 6, 0, 6, 0.2f, H));
        floor.Obstacles.Add(Wall(0, 6, 0, 0, 0.2f, H));
        AddGroundSlab(floor, 0, 0, 8, 6);
        AddRoofSlab(floor, 0, 0, 8, 6, H);
        floor.Openings.Add(new Opening
        {
            Id = "west-low", Name = "west-low", Kind = OpeningKind.Window, IsOpen = true,
            Center = new Vector3(0, 3, 0.5f), Normal = new Vector3(-1, 0, 0), Width = 1f, Height = 1f, Depth = 0.2f,
        });
        floor.Openings.Add(new Opening
        {
            Id = "east-high", Name = "east-high", Kind = OpeningKind.Window, IsOpen = true,
            Center = new Vector3(8, 3, 2.5f), Normal = new Vector3(1, 0, 0), Width = 1f, Height = 1f, Depth = 0.2f,
        });
        floor.Rooms.Add(new Room { Id = "room", Name = "Room", Level = "L1", Loops = { RectLoop(0.1f, 0.1f, 7.9f, 5.9f) }, ZMin = 0, ZMax = H });
        floor.Env = new Environment { IndoorTemperature = 26, OutdoorTemperature = 16 };
        return floor;
    }

    // Closed room, one 500 W equipment box near the floor: tests thermal stratification.
    static BuildingModel BuildHeat()
    {
        var floor = BuildCross("heat", false, false);
        floor.Env = new Environment { IndoorTemperature = 22, OutdoorTemperature = 22 };
        floor.Heat.Add(new HeatSource
        {
            Id = "heater", Name = "heater", Level = "L1", Kind = HeatKind.Equipment, Enabled = true,
            Center = new Vector3(4, 3, 0.25f), Width = 1, Depth = 1, Height = 0.5f, Watts = 500,
        });
        return floor;
    }

    static BuildingModel BuildCo2()
    {
        var floor = BuildCross("co2", true, true);
        floor.Rooms[0].Occupants = 8;
        return floor;
    }

    static BuildingModel BuildCo2Closed()
    {
        var floor = BuildCross("co2-closed", false, false);
        floor.Rooms[0].Occupants = 8;
        return floor;
    }

    static BuildingModel BuildComfort()
    {
        var floor = BuildCross("comfort", true, true);
        floor.Env = new Environment { IndoorTemperature = 26, OutdoorTemperature = 24 };
        floor.Rooms[0].Occupants = 8;
        return floor;
    }

    static BuildingModel BuildAnim() => BuildCross("anim", true, true);

    static BuildingModel BuildTracer()
    {
        var floor = BuildCross("tracer", true, true);
        floor.Contaminants.Add(new ContaminantSource { Id = "tracer", Name = "tracer", Level = "L1", Enabled = true, Center = new Vector3(2, 3, 1.2f), Rate = 1 });
        return floor;
    }

    // Advanced-tier scene + its RunOptions (all use TargetCells=400_000, Backend=Cpu per the brief).
    static (BuildingModel Floor, Vector2 Wind, RunOptions Options) AdvancedScene(string name)
    {
        var opts = new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu };
        switch (name)
        {
            case "stack": opts.Thermal = true; opts.HoldIndoorTemperature = false; return (BuildStack(), new Vector2(0, 0), opts);   // P14-01, rule 3 (RT-04): 18a-18c verify the stack effect driven by the indoor/outdoor dT, free-running (18b's derivation: the room decays toward outdoor)
            case "heat": opts.Thermal = true; opts.HoldIndoorTemperature = false; return (BuildHeat(), new Vector2(0, 0), opts);   // P14-01 (6j): the sealed heated room (19a-19e) is free-running physics (rule 3, test configuration)
            case "co2": opts.AirQuality = true; return (BuildCo2(), new Vector2(3, 0), opts);
            // Same two-room plan as the basic "tworooms" scene (west wind, both windows and the door open),
            // re-run with air quality on so check 32 can compare the two rooms' mean age of air.
            case "tworooms-aq": opts.AirQuality = true; return (BuildTwoRooms("tworooms-aq", true), new Vector2(3, 0), opts);
            case "co2-closed": opts.AirQuality = true; return (BuildCo2Closed(), new Vector2(0, 0), opts);
            case "comfort": opts.Comfort = true; opts.AirQuality = true; opts.Thermal = true; return (BuildComfort(), new Vector2(3, 0), opts);
            case "anim": opts.SnapshotCount = 6; return (BuildAnim(), new Vector2(3, 0), opts);
            case "tracer": opts.AirQuality = true; return (BuildTracer(), new Vector2(3, 0), opts);
            default: throw new ArgumentException($"unknown advanced scene {name}");
        }
    }

    // 8760 hourly rows in standard EPW column order (EnergyPlus Auxiliary Programs manual): 5000 h from
    // 225 deg at 3 m/s, then 3000 h from 45 deg at 6 m/s, then 760 calm hours (wind speed 0).
    static void WriteSyntheticEpw(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("LOCATION,Synthetic Test Site,Test,US,Custom,000000,45.0,-93.0,-6.0,250.0");
        sb.AppendLine("DESIGN CONDITIONS,0");
        sb.AppendLine("TYPICAL/EXTREME PERIODS,0");
        sb.AppendLine("GROUND TEMPERATURES,0");
        sb.AppendLine("HOLIDAYS/DAYLIGHT SAVINGS,No,0,0,0");
        sb.AppendLine("COMMENTS 1,Synthetic WindFlow test fixture");
        sb.AppendLine("COMMENTS 2,5000h 225@3m/s 3000h 45@6m/s 760h calm");
        sb.AppendLine("DATA PERIODS,1,1,Data,Sunday,1/1,12/31");

        int[] daysInMonth = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };   // 2023: not leap, 365*24 = 8760
        int hourIndex = 0;
        for (int month = 1; month <= 12; month++)
        for (int day = 1; day <= daysInMonth[month - 1]; day++)
        for (int hour = 1; hour <= 24; hour++)
        {
            float windDir, windSpd;
            if (hourIndex < 5000) { windDir = 225f; windSpd = 3f; }
            else if (hourIndex < 8000) { windDir = 45f; windSpd = 6f; }
            else { windDir = 0f; windSpd = 0f; }
            hourIndex++;

            sb.AppendLine(string.Join(",", new object[]
            {
                2023, month, day, hour, 0,
                "?9?9?9?9E0?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9?9",
                20.0, 12.0, 50, 101300, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                windDir, windSpd,
                5, 5, 10000, 7700, 0, "999999999", 0, 0, 0, 0, 0.2, 0, 0,
            }));
        }
        File.WriteAllText(path, sb.ToString());
    }

    // Mean of `values` over Air cells whose centre lies within one cell of z (m) AND inside the room's own
    // footprint (InsideRoomLoops) -- NOT every Air cell in the grid at that height. A "heat"/"stack"-shaped
    // scene's grid is >90% exterior wind-tunnel margin at any given z; averaging over the whole plane dilutes
    // a real indoor signal by ~18x (confirmed empirically: a scene with a genuine ~0.3-0.7 K indoor
    // stratification read back as ~0.01 K once the huge, nearly-isothermal exterior was mixed in). Root cause
    // of sandbox check 19a's original failure -- see the implement agent's memory.
    // Plume-model measurement (not a check): 19a's sealed 500 W room on a grounded box domain at Dx WF_DX (default 0.2683299 m, as the 2026-10-19 grid study),
    // 3 filling-box turnovers, then the same averaging as 19-setup. Prints dT(2.5 m - 0.5 m) and the injected-heat ledger against W x t.
    static void Plume19a()
    {
        float ddx = float.Parse(System.Environment.GetEnvironmentVariable("WF_DX") ?? "0.2683299", System.Globalization.CultureInfo.InvariantCulture);
        var (floor, wind, opts) = AdvancedScene("heat");
        opts.ThermalScheme = ThermalScheme.Hybrid; opts.HoldIndoorTemperature = false;
        const float mg = 1f;
        var bx = new DomainBox(new Vector3(-mg, -mg, 0f), new Vector3(8f + mg, 6f + mg, 3f + 0.2f + mg));
        opts.Domain = bx; opts.MinDx = Math.Min(0.1f, ddx);
        opts.TargetCells = (int)((bx.Max.X - bx.Min.X) * (bx.Max.Y - bx.Min.Y) * bx.Max.Z / (ddx * ddx * ddx));
        var grid = Voxelizer.Build(floor, opts.TargetCells!.Value, domain: opts.Domain, minDx: opts.MinDx ?? 0.1f);
        using var session = LbmSolver.Start(grid, floor, wind, opts);
        var room = floor.Rooms[0];
        float watts = floor.Heat.Where(h => h.Enabled).Sum(h => h.Watts), vol = 0f;
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.Cell[i] != VoxelGrid.Air) continue;
            var c = grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny));
            if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
            vol += grid.Dx * grid.Dx * grid.Dx;
        }
        float height = MathF.Max(room.ZMax - room.ZMin, grid.Dx), tOutK = floor.Env.OutdoorTemperature + 273.15f;
        float plumeFlux = 0.156f * MathF.Cbrt(9.81f / tOutK * watts / (1.2f * 1005f)) * MathF.Pow(height, 5f / 3f), fillSeconds = vol / plumeFlux;
        Console.WriteLine($"Plume19a: dx={grid.Dx:F4} grid {grid.Nx}x{grid.Ny}x{grid.Nz} plume={LbmSolver.PlumeModel} planned={session.PlannedSteps} warmup={session.WarmupSteps}");
        foreach (var w in grid.Warnings.Where(w => w.StartsWith("Plume model"))) Console.WriteLine(w);
        session.Step(session.PlannedSteps);
        float dt = (float)(session.SimSeconds / session.Steps);
        float target = 3f * fillSeconds;
        if (session.SimSeconds < target) session.Step((int)MathF.Ceiling((target - (float)session.SimSeconds) / dt));
        session.ResetAveraging();
        session.Step(Math.Max(1, session.PlannedSteps - session.WarmupSteps));
        var field = session.ToField();
        float tHigh = MeanAtHeight(field, 2.5f, field.T, room), tLow = MeanAtHeight(field, 0.5f, field.T, room);
        // ledger: HybridInjectedKelvinCells x rho cp x dx^3 (J) against W x dt x (steps - ramp/2), the heat ramps over max(1, warmup/2) steps
        double rampSteps = Math.Max(1.0, session.WarmupSteps / 2.0), expected = watts * dt * (session.Steps - rampSteps / 2.0);
        double injected = session.HybridInjectedKelvinCells * 1.2 * 1005 * grid.Dx * grid.Dx * grid.Dx;
        Console.WriteLine($"RESULT dx={grid.Dx:F4} plume={LbmSolver.PlumeModel} T(2.5)={tHigh:F2} T(0.5)={tLow:F2} dT={tHigh - tLow:F2} in-band={(tHigh - tLow >= 1.8f && tHigh - tLow <= 8.6f)} " +
            $"SimSeconds={session.SimSeconds:F0} ({session.SimSeconds / fillSeconds:F1} turnovers) steps={session.Steps} injectedJ={injected:F0} expectedJ={expected:F0} ratio={injected / expected:F4}");
    }

    // Check 19d body: check 19's room (the "heat" scene) with the heater removed and a uniform 27 C start,
    // sealed and adiabatic, at rest. Measured 2026-10-05 (CPU, 8000 steps): stale Bouzidi q built 1.32 K top
    // to bottom; Bouzidi wall link without the f0 mass correction 0.83 K (mean speed 0.03 m/s); with the
    // correction 0.009 K (mean speed ~0.006 m/s).
    static (bool Pass, string Detail) Quiescent19d()
    {
        var (floor, wind, opts) = AdvancedScene("heat");
        floor.Heat.Clear();
        floor.Env = new Environment { IndoorTemperature = 27, OutdoorTemperature = 22 };
        var grid = Voxelizer.Build(floor, opts.TargetCells!.Value);
        using var session = LbmSolver.Start(grid, floor, wind, opts);
        var room = floor.Rooms[0];
        const int total = 8000, window = 150;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.Step(total - window);
        session.ResetAveraging();       // average only the last 150 steps
        session.Step(window);
        var f = session.ToField();
        if (f.T.Length == 0) throw new NotImplementedException("field.T empty, thermal not wired yet");
        double su = 0; int n = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.Cell[i] != VoxelGrid.Air) continue;
            var c = grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny));
            if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
            su += MathF.Sqrt(f.Ux[i] * f.Ux[i] + f.Uy[i] * f.Uy[i] + f.Uz[i] * f.Uz[i]); n++;
        }
        float meanU = n > 0 ? (float)(su / n) : float.NaN;
        float tHigh = MeanAtHeight(f, 2.5f, f.T, room), tLow = MeanAtHeight(f, 0.5f, f.T, room);
        float dT = MathF.Abs(tHigh - tLow);
        bool ok = !float.IsNaN(dT) && !float.IsNaN(meanU) && dT <= 0.05f && meanU <= 0.01f;
        return (ok, $"|dT|={dT:F3}K (T2.5={tHigh:F2} T0.5={tLow:F2}) meanSpeed={meanU:F4}m/s steps={session.Steps} " +
            $"backend={session.Backend} wall={sw.Elapsed.TotalSeconds:F0}s");
    }

    // Check 19e body (CPU, ThermalScheme.Hybrid). The hybrid scheme moves T with the LBM's own link mass fluxes, so (a) a uniform T must stay uniform
    // to round-off however the lattice flow moves, and (b) in a sealed adiabatic room sum(rho_new (T - T_out)) must rise by exactly the heat added
    // (the energy is taken above the outdoor T: the lattice mass itself drifts at float round-off, about 4e-7 per simulated second, and that times 22 K
    // is not an energy error). Measured 2026-10-10: see HANDOFF.md. The earlier face-averaged-velocity form grew a 5 K anomaly in (a).
    static (bool Pass, string Detail) Hybrid19e()
    {
        const double uniformTol = 1e-4, energyTol = 1e-3;
        double MaxDev(LbmSolver.LbmSession session, VoxelGrid grid, double t0)
        {
            var t = new float[grid.Count];
            session.ReadTemperature(t);
            double dev = 0;
            for (int i = 0; i < grid.Count; i++) if (grid.Cell[i] != VoxelGrid.Solid) dev = Math.Max(dev, Math.Abs(t[i] - t0));
            return dev;
        }
        // (a) the 19d room: sealed, adiabatic, quiescent, uniform 27 C inside a 22 C world
        var (floor, wind, opts) = AdvancedScene("heat");
        opts.ThermalScheme = ThermalScheme.Hybrid;
        opts.HoldIndoorTemperature = false;   // P14-01 (6j): 19e checks free-running transport and energy, not the hold (rule 3, test configuration)
        floor.Heat.Clear();
        floor.Env = new Environment { IndoorTemperature = 27, OutdoorTemperature = 22 };
        var grid = Voxelizer.Build(floor, opts.TargetCells!.Value);
        double devRoom;
        using (var session = LbmSolver.Start(grid, floor, wind, opts))
        {
            session.Step(3000);
            var t = new float[grid.Count];
            session.ReadTemperature(t);
            var room = floor.Rooms[0];
            devRoom = 0;
            for (int i = 0; i < grid.Count; i++)
            {
                if (grid.Cell[i] != VoxelGrid.Air) continue;
                var c = grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny));
                if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                devRoom = Math.Max(devRoom, Math.Abs(t[i] - 27.0));
            }
        }
        // (b) the cross-ventilation scene in a 3 m/s wind, indoor = outdoor = 22 C: nothing may change T
        var (floorW, windW) = Scene("cross");
        floorW.Env = new Environment { IndoorTemperature = 22, OutdoorTemperature = 22 };
        var optsW = new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu, Thermal = true, ThermalScheme = ThermalScheme.Hybrid, HoldIndoorTemperature = false };   // P14-01 (6j): free-running (rule 3, test configuration)
        var gridW = Voxelizer.Build(floorW, optsW.TargetCells!.Value);
        double devWind; double maxSpeed;
        using (var session = LbmSolver.Start(gridW, floorW, windW, optsW))
        {
            session.Step(1500);
            devWind = MaxDev(session, gridW, 22.0);
            var ux = new float[gridW.Count]; var uy = new float[gridW.Count]; var uz = new float[gridW.Count];
            session.ReadVelocity(ux, uy, uz);
            maxSpeed = 0;
            for (int i = 0; i < gridW.Count; i++) maxSpeed = Math.Max(maxSpeed, Math.Sqrt(ux[i] * ux[i] + uy[i] * uy[i] + uz[i] * uz[i]));
        }
        // (c) the 500 W sealed room, 2500 steps
        var (floorH, windH, optsH) = AdvancedScene("heat");
        optsH.ThermalScheme = ThermalScheme.Hybrid;
        optsH.HoldIndoorTemperature = false;   // P14-01 (6j): the sealed-room energy balance is free-running physics (rule 3, test configuration)
        var gridH = Voxelizer.Build(floorH, optsH.TargetCells!.Value);
        double eErr, injected;
        using (var session = LbmSolver.Start(gridH, floorH, windH, optsH))
        {
            var roomH = floorH.Rooms[0];
            // S1 (6a): the room's cells in LBM terms -- since 6a the LBM also streams (and the heater warms) wall cells that Cell marks Solid (LbmAir),
            // so a Cell-air mask misses their energy (0.32 % of the injected heat here); the energy balance must sum the cells the solver advects.
            // IMP-C (rule 3, measurement window): RoomMask now stops at the ceiling, but the energy balance sums every cell the solver advects in the room's columns, as the
            // heat that numerically crosses the lid is still injected heat; the window is the old column rule (every LBM-fluid cell inside the loops and the storey's z-range).
            var inside = new bool[gridH.Count];
            for (int z = 0; z < gridH.Nz; z++)
            {
                float cz = gridH.Origin.Z + (z + 0.5f) * gridH.Dx;
                if (cz < roomH.ZMin || cz >= roomH.ZMax) continue;
                for (int y = 0; y < gridH.Ny; y++) for (int x = 0; x < gridH.Nx; x++)
                {
                    int i = gridH.Index(x, y, z);
                    if (Voxelizer.LbmFluid(gridH, i)) { var c = gridH.CellCenter(x, y, z); inside[i] = Analysis.InsideLoops(roomH.Loops, new Vector2(c.X, c.Y)); }
                }
            }
            session.Step(2500);
            injected = session.HybridInjectedKelvinCells;
            double e = session.HybridEnergy(i => inside[i]) - 22.0 * session.HybridMassSum(i => inside[i]);
            eErr = injected > 0 ? Math.Abs(e - injected) / injected : double.NaN;
        }
        bool ok = devRoom <= uniformTol && devWind <= uniformTol && eErr <= energyTol;
        return (ok, $"19d room max|T-27|={devRoom:E1} K (limit {uniformTol:E0}) | windy scene (max speed {maxSpeed:F2} m/s) max|T-22|={devWind:E1} K (limit {uniformTol:E0}) | " +
            $"sealed 500 W room energy error {eErr * 100:F3} % of {injected:F0} K-cells (limit {energyTol * 100:F1} %)");
    }

    // Check 19f body (needs a GPU): the GPU Hybrid kernels reproduce the CPU Hybrid in single precision. The same small thermal scenes run N steps
    // on both backends; max|dT| over all non-solid cells and the energy sum(rho_new (T - T_out)) over the air cells must agree. Scenes: the sealed
    // 500 W room (solids, lid, heat source), the cross-ventilation room in a 3 m/s wind with a heater and a 4 K indoor/outdoor split (ring
    // inflow / open cells, openings) and the Supply+Return room with a heater (Supply patch: SupplyBounce links, Fixed cells).
    static (bool Pass, string Detail) HybridGpuCpu19f()
    {
        if (!LbmSolver.GpuAvailable) throw new NotImplementedException("no GPU on this machine");
        const double dtTol = 1e-3, eTol = 1e-4;
        const int cells = 150_000;
        BuildingModel WithHeater(BuildingModel m, float indoor)
        {
            m.Env = new Environment { IndoorTemperature = indoor, OutdoorTemperature = 22 };
            m.Heat.Add(new HeatSource { Id = "heater", Name = "heater", Level = "L1", Kind = HeatKind.Equipment, Enabled = true,
                Center = new Vector3(4, 3, 0.5f), Width = 2, Depth = 2, Height = 1, Watts = 500 });
            return m;
        }
        // the heater ramps in over half the warm-up, so the sealed room needs more steps before its energy balance means anything
        var scenes = new (string Name, Func<BuildingModel> Build, Vector2 Wind, int Steps)[]
        {
            ("sealed 3 kW", () => { var m = BuildHeat(); m.Heat[0].Center = new Vector3(4, 3, 0.5f); m.Heat[0].Width = 2; m.Heat[0].Depth = 2; m.Heat[0].Height = 1; m.Heat[0].Watts = 3000; return m; }, Vector2.Zero, 4000),
            ("cross+heat 3 m/s", () => WithHeater(BuildCross("cross", true, true), 26), new Vector2(3, 0), 800),
            ("supply+heat", () => WithHeater(BuildSupply(), 26), Vector2.Zero, 800),
        };
        bool ok = true; var parts = new List<string>();
        foreach (var (name, build, wind, steps) in scenes)
        {
            (float[] T, VoxelGrid Grid, double E, double Injected) Run(Backend backend)
            {
                var model = build();
                var grid = Voxelizer.Build(model, cells);
                using var s = LbmSolver.Start(grid, model, wind, new RunOptions { TargetCells = cells, Backend = backend, Thermal = true, ThermalScheme = ThermalScheme.Hybrid });
                s.Step(steps);
                var t = new float[grid.Count]; s.ReadTemperature(t);
                double tOut = model.Env.OutdoorTemperature, mass = s.HybridMassSum(_ => true);
                double e = s.HybridEnergy(_ => true) - tOut * mass;
                return (t, grid, e, s.HybridInjectedKelvinCells);
            }
            var cpu = Run(Backend.Cpu); var gpu = Run(Backend.Gpu);
            double maxDt = 0, maxT = 0; int at = -1;
            for (int i = 0; i < cpu.T.Length; i++)
            {
                if (cpu.Grid.Cell[i] == VoxelGrid.Solid) continue;
                double d = Math.Abs(cpu.T[i] - gpu.T[i]);
                if (!(d <= maxDt)) { maxDt = double.IsNaN(d) ? double.PositiveInfinity : d; at = i; }
                maxT = Math.Max(maxT, Math.Abs(cpu.T[i] - 22.0));
            }
            double eDiff = Math.Abs(gpu.E - cpu.E) / Math.Max(Math.Abs(cpu.E), 1e-12);
            ok &= maxDt <= dtTol && eDiff <= eTol;
            parts.Add($"{name} ({steps} steps): max|dT|={maxDt:E2} K (max|T-22|={maxT:F2}) energy cpu={cpu.E:F3} gpu={gpu.E:F3} (injected cpu={cpu.Injected:F1} gpu={gpu.Injected:F1}) diff={eDiff * 100:F4} %");
        }
        return (ok, string.Join(" | ", parts) + $" | {cells} cells, limits {dtTol:E0} K and {eTol * 100:F2} %");
    }

    // Check 54b body (no GPU): the monitor's batch-means statistics and the field accumulator, on synthetic data.
    //  (a) AR(1) rho 0.7 batch means, 2000 trials x 40 batches: the 95% CI (autocorrelation-inflated, over the kept batches) must cover the
    //      true mean 90-98 % of the time.
    //  (b) a start-up transient (5 e^(-t/2) ACH on top of 10 ACH) plus noise (sd 0.4), 200 trials: every trial converges, the mean error over
    //      trials is within 0.2 x tolerance (no transient bias survives), and >= 90 % of the landed means are within tolerance of the truth.
    //  (c) BatchWindowMean over (d, n] against the direct UNWEIGHTED mean of those batch fields, n = 1..20, unequal sample counts (ignored).
    //  (d) miss rate at the stopping point: the full sequential rule (monitor evaluated after every batch, n >= 16 gate) on AR(1) series, mean 10,
    //      tolerance 0.5, rho {0, .5, .7} x sd {.25, .5, 1, 2}, 2000 trials per cell capped at n = 256; a miss is |landed mean - 10| > 0.5;
    //      P(miss | converged) must be <= 0.10 in every cell with >= 50 converged trials (rarer cells are printed only; see MissGrid).
    static (bool Pass, string Detail) ConvergeStats54b()
    {
        static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

        // (a)
        var rng = new Random(54);
        const int trials = 2000, nb = 40;
        const double mu = 10, rho = 0.7;
        int covered = 0;
        for (int t = 0; t < trials; t++)
        {
            var mon = new ConvergenceMonitor();
            double e = Gauss(rng);
            for (int i = 0; i < nb; i++)
            {
                e = rho * e + Math.Sqrt(1 - rho * rho) * Gauss(rng);
                mon.AddBatch([("a", (float)(mu + e))]);
            }
            var r = mon.Evaluate().Rooms[0];
            if (Math.Abs(r.Mean - mu) <= r.HalfWidth) covered++;
        }
        double coverage = covered / (double)trials;

        // (b)
        const int bTrials = 200;
        double sumErr = 0, tolSeen = 0; int unconverged = 0, within = 0, minN = int.MaxValue, maxN = 0;
        for (int t = 0; t < bTrials; t++)
        {
            var mon = new ConvergenceMonitor();
            ConvergenceStatus st;
            int n = 0;
            do
            {
                mon.AddBatch([("a", (float)(10 + 5 * Math.Exp(-n / 2.0) + 0.4 * Gauss(rng)))]);
                n++;
                st = mon.Evaluate();
            } while (st.Reason == ConvergenceStopReason.Running);
            var r = st.Rooms[0];
            if (st.Reason != ConvergenceStopReason.Converged) unconverged++;
            sumErr += r.Mean - 10; tolSeen = r.Tolerance;
            if (Math.Abs(r.Mean - 10) <= r.Tolerance) within++;
            minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
        }
        double bias = sumErr / bTrials, fracWithin = within / (double)bTrials;

        // (c)
        var rg = new Random(5);
        const int cells = 12;
        var raw = new List<(float[][] A, double Inflow, long W)>();
        var acc = new BatchWindowMean();
        double maxDiff = 0; bool nullAtOne = false;
        for (int n = 1; n <= 20; n++)
        {
            float[][] arrs = new float[6][];
            for (int k = 0; k < 6; k++) arrs[k] = Enumerable.Range(0, cells).Select(_ => (float)(rg.NextDouble() * 4 - 2)).ToArray();
            double inflow = rg.NextDouble() * 3; long w = 20 + rg.Next(0, 400);
            raw.Add((arrs, inflow, w));
            var grid = new VoxelGrid { Nx = 3, Ny = 2, Nz = 2, Dx = 0.5f };
            var room = new Room { Id = "r" };
            acc.Add(new FlowField
            {
                Grid = grid, Ux = (float[])arrs[0].Clone(), Uy = (float[])arrs[1].Clone(), Uz = (float[])arrs[2].Clone(), P = (float[])arrs[3].Clone(),
                T = (float[])arrs[4].Clone(), Tu = (float[])arrs[5].Clone(), AverageSamples = w,
                RoomFluxes = { new RoomFlux { Room = room, Inflow = inflow } },
            });
            var mean = acc.Mean;
            if (n == 1) { nullAtOne = mean == null; continue; }
            int d = ConvergenceMonitor.TruncationFor(n);
            double cnt = n - d, sw = 0; var sum = new double[6][]; for (int k = 0; k < 6; k++) sum[k] = new double[cells];
            double sf = 0;
            for (int i = d; i < n; i++)
            {
                sw += raw[i].W; sf += raw[i].Inflow;
                for (int k = 0; k < 6; k++) for (int c = 0; c < cells; c++) sum[k][c] += raw[i].A[k][c];
            }
            float[][] got = [mean!.Ux, mean.Uy, mean.Uz, mean.P, mean.T, mean.Tu];
            for (int k = 0; k < 6; k++) for (int c = 0; c < cells; c++) maxDiff = Math.Max(maxDiff, Math.Abs(got[k][c] - sum[k][c] / cnt));
            maxDiff = Math.Max(maxDiff, Math.Abs(mean.RoomFluxes[0].Inflow - sf / cnt));
            if (mean.AverageSamples != (long)sw) maxDiff = double.PositiveInfinity;     // the true sample total still lands, only the weights are equal
        }

        // (d)
        var (dTable, worstMiss, _) = MissGrid();
        Console.WriteLine(dTable);

        bool pass = coverage >= 0.90 && coverage <= 0.98 && unconverged == 0 && Math.Abs(bias) <= 0.2 * tolSeen && fracWithin >= 0.90 && nullAtOne && maxDiff <= 1e-5 && worstMiss <= 0.10;
        return (pass, $"(a) AR(1) rho=0.7 coverage={coverage:F3} of {trials} trials x {nb} batches (want 0.90-0.98) | " +
            $"(b) transient, {bTrials} trials: {unconverged} unconverged (converged at n={minN}..{maxN}), bias {bias:+0.000;-0.000} ACH (want |bias| <= {0.2 * tolSeen:F3} = 0.2 tol), within tol {fracWithin:F3} (want >= 0.90) | " +
            $"(c) unweighted accumulator over (d,n], n=1..20: max diff {maxDiff:E2} (want <=1e-5), n=1 fallback={nullAtOne} | " +
            $"(d) worst-cell miss rate {worstMiss:F3} (want <= 0.10)");
    }

    // 54b(d) grid. Cells with < 50 converged trials (2.5 % of 2000) are printed, not asserted: the miss estimate there has no power.
    // WorstMiss = worst P(miss | converged) over the asserted cells; StdN = mean n-at-stop over the Standard-like cells (rho 0,.5 x sd .5,1).
    static (string Table, double WorstMiss, double StdN) MissGrid()
    {
        static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
        double[] dRhos = [0, 0.5, 0.7], dSds = [0.25, 0.5, 1, 2];
        const int dTrials = 2000, minStops = 50;
        double worstMiss = 0, stdSum = 0; int stdCells = 0;
        var dTable = new System.Text.StringBuilder($"54b(d) miss rate at the stopping point (mean 10, tol 0.5, cap n=256, {dTrials} trials/cell; cells with < {minStops} stops not asserted)\n  rho    sd   stop-rate  miss-rate (of stopped)  mean n at stop\n");
        for (int ri = 0; ri < dRhos.Length; ri++)
        for (int si = 0; si < dSds.Length; si++)
        {
            double pr = dRhos[ri], ps = dSds[si];
            int stops = 0, misses = 0; long sumSteps = 0;
            int cellSeed = 540000 + ri * 10 + si;
            Parallel.For(0, dTrials, ti =>
            {
                var rd = new Random(cellSeed * 3001 + ti);
                var mon = new ConvergenceMonitor { MaxBatches = 256 };
                double e = Gauss(rd);
                ConvergenceStatus st;
                int steps = 0;
                do
                {
                    e = pr * e + Math.Sqrt(1 - pr * pr) * Gauss(rd);
                    mon.AddBatch([("a", (float)(10 + ps * e))]);
                    steps++;
                    st = mon.Evaluate();
                } while (st.Reason == ConvergenceStopReason.Running);
                if (st.Reason != ConvergenceStopReason.Converged) return;
                System.Threading.Interlocked.Increment(ref stops);
                System.Threading.Interlocked.Add(ref sumSteps, steps);
                if (Math.Abs(st.Rooms[0].Mean - 10) > 0.5) System.Threading.Interlocked.Increment(ref misses);
            });
            double miss = stops > 0 ? misses / (double)stops : 0, meanN = stops > 0 ? sumSteps / (double)stops : double.NaN;
            if (stops >= minStops) worstMiss = Math.Max(worstMiss, miss);
            if (pr <= 0.5 && ps is 0.5 or 1) { stdSum += meanN; stdCells++; }
            dTable.AppendLine($"  {pr,-5:0.0} {ps,5:0.##}  {stops / (double)dTrials,9:F3}  {(stops > 0 ? miss.ToString("F3") : "n/a"),9}  ({misses}/{stops}){(stops < minStops ? " not asserted" : "")}  {meanN,8:F1}");
        }
        return (dTable.ToString().TrimEnd(), worstMiss, stdSum / stdCells);
    }

    static float MeanAtHeight(FlowField field, float z, float[] values, Room room)
    {
        var g = field.Grid;
        if (values.Length != g.Count) return float.NaN;
        double sum = 0; int n = 0;
        float band = MathF.Max(g.Dx, 0.15f);
        for (int zi = 0; zi < g.Nz; zi++)
        {
            float cz = g.Origin.Z + (zi + 0.5f) * g.Dx;
            if (MathF.Abs(cz - z) > band) continue;
            for (int yi = 0; yi < g.Ny; yi++)
            for (int xi = 0; xi < g.Nx; xi++)
            {
                int idx = g.Index(xi, yi, zi);
                if (g.Cell[idx] != VoxelGrid.Air) continue;
                var c = g.CellCenter(xi, yi, zi);
                if (!InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                sum += values[idx]; n++;
            }
        }
        return n > 0 ? (float)(sum / n) : float.NaN;
    }

    // Even-odd point-in-polygon test, mirrors Analysis.InsideLoops (internal to WindFlow.Core, so
    // re-implemented here rather than exposed across the assembly boundary).
    static bool InsideRoomLoops(List<Vector2[]> loops, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in loops)
        {
            bool hit = false;
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[i]; var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    hit = !hit;
            }
            if (hit) inside = !inside;
        }
        return inside;
    }

    // Fraction of occupied-zone (z in [zLo,zHi], inside the room's footprint) Air cells with a finite PMV.
    // Comfort.Compute leaves PMV NaN outside the analysed storeys, so the denominator must be restricted
    // to the room -- the exterior wind-tunnel margin is Air too and would swamp the count otherwise.
    static (int Total, int Finite) CountFinitePmv(FlowField field, ComfortFields cf, Room room, float zLo, float zHi)
    {
        var g = field.Grid;
        int total = 0, finiteCount = 0;
        for (int zi = 0; zi < g.Nz; zi++)
        {
            float cz = g.Origin.Z + (zi + 0.5f) * g.Dx;
            if (cz < zLo || cz > zHi) continue;
            for (int yi = 0; yi < g.Ny; yi++)
            for (int xi = 0; xi < g.Nx; xi++)
            {
                int idx = g.Index(xi, yi, zi);
                if (g.Cell[idx] != VoxelGrid.Air) continue;
                var c = g.CellCenter(xi, yi, zi);
                if (!InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                total++;
                if (idx < cf.Pmv.Length && !float.IsNaN(cf.Pmv[idx])) finiteCount++;
            }
        }
        return (total, finiteCount);
    }

    static (BuildingModel Floor, Vector2 Wind) Scene(string name) => name switch
    {
        "cross" => (BuildCross("cross", true, true), new Vector2(3, 0)),
        "closed" => (BuildCross("closed", false, false), new Vector2(3, 0)),
        "single" => (BuildCross("single", true, false), new Vector2(3, 0)),
        "reversed" => (BuildCross("reversed", true, true), new Vector2(-3, 0)),
        "diagonal" => (BuildCross("diagonal", true, true), new Vector2(2.121f, 2.121f)),
        "tworooms" => (BuildTwoRooms("tworooms", true), new Vector2(3, 0)),
        "tworooms-doorclosed" => (BuildTwoRooms("tworooms-doorclosed", false), new Vector2(3, 0)),
        "twostorey" => (BuildTwoStorey("twostorey", false), new Vector2(3, 0)),
        "twostorey-closed" => (BuildTwoStorey("twostorey-closed", true), new Vector2(3, 0)),
        "supply" => (BuildSupply(), new Vector2(3, 0)),
        "fan" => (BuildFan(), new Vector2(3, 0)),
        "fan-nowind" => (BuildFanNoWind(), new Vector2(0, 0)),
        "atrium" => (BuildAtrium(), new Vector2(3, 0)),
        _ => throw new ArgumentException($"unknown scene {name}"),
    };

    // ---------------------------------------------------------------------------------------------
    // Printing & rendering
    // ---------------------------------------------------------------------------------------------

    static void PrintResult(string name, SimulationResult r)
    {
        var g = r.Field.Grid;
        double mlups = r.Field.Seconds > 0 ? (double)g.Count * r.Field.Steps / r.Field.Seconds / 1e6 : 0;
        Console.WriteLine($"-- {name} --");
        Console.WriteLine($"  grid {g.Nx}x{g.Ny}x{g.Nz}  Dx={g.Dx:F3}  backend={r.Field.Backend}  steps={r.Field.Steps}  solve={r.Field.Seconds:F2}s  MLUPS={mlups:F1}");
        foreach (var o in r.Openings)
            Console.WriteLine($"  opening {o.Opening.Name,-10} FlowRate={o.FlowRate,8:F3} m3/s  MeanSpeed={o.MeanSpeed,7:F3} m/s  " +
                $"CarvedArea={o.CarvedArea:F3}/{o.Opening.Width * o.Opening.Height:F3} m2  dP={o.PressureDrop:F3} Pa  Cd_eff={o.EffectiveCd:F3}");
        foreach (var s in r.Sources)
            Console.WriteLine($"  source  {s.Source.Name,-10} set={s.Source.FlowRate,7:F3}  measured={s.FlowRate,7:F3} m3/s");
        foreach (var room in r.Rooms)
            Console.WriteLine($"  room {room.Room.Name,-10} ACH={room.Ach,8:F2}  MeanSpeed={room.MeanSpeed,7:F3} m/s  Stagnant={room.StagnantFraction * 100,5:F1}%  Inflow={room.Inflow,7:F3} m3/s  InternalExchange={room.InternalExchange,7:F3} m3/s");
        foreach (var w in g.Warnings)
            Console.WriteLine($"  WARNING: {w}");
    }

    // West (or west-lN) opening centre, extended a couple of metres past the east wall, +X.
    static (Vector2 A, Vector2 B) SectionLine(BuildingModel model)
    {
        var candidates = model.Openings.Where(o => o.Name.Contains("west", StringComparison.OrdinalIgnoreCase)).ToList();
        var west = candidates.FirstOrDefault(o => o.Name.Equals("west", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(o => o.Name.Contains("l1", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
        var (min, max) = Voxelizer.PlanBounds(model);
        Vector2 a = west != null ? new Vector2(west.Center.X, west.Center.Y) : new Vector2(min.X, (min.Y + max.Y) * 0.5f);
        Vector2 b = new Vector2(max.X + 2f, a.Y);
        return (a, b);
    }

    // Renderer.Plan/Section may still throw NotImplementedException while WindFlow.Viz is in flight;
    // images are not part of PASS/FAIL, so any rendering failure is reported and swallowed.
    static void RenderScene(string name, SimulationResult r, string outDir)
    {
        try
        {
            var plan = Renderer.Plan(r, new PlanOptions { Z = 1.1f, Streamlines = true, Arrows = false, Contours = false, Title = $"{name} · plan" });
            Renderer.SavePng(plan, Path.Combine(outDir, $"{name}-plan.png"));

            var arrows = Renderer.Plan(r, new PlanOptions { Z = 1.1f, Streamlines = false, Arrows = true, Contours = true, Title = $"{name} · arrows" });
            Renderer.SavePng(arrows, Path.Combine(outDir, $"{name}-arrows.png"));

            var (a, b) = SectionLine(r.Model);
            var section = Renderer.Section(r, a, b, new SectionOptions { ZMin = -0.5f, ZMax = 4.5f, Title = $"{name} · section" });
            Renderer.SavePng(section, Path.Combine(outDir, $"{name}-section.png"));

            Console.WriteLine($"  images: {name}-plan.png {name}-arrows.png {name}-section.png");
        }
        catch (NotImplementedException)
        {
            Console.WriteLine("  renderer not ready");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  renderer error: {ex.Message}");
        }
    }

    // Trilinear scalar sample (mirrors Analysis.SampleVelocity's interpolation), 0 in solid cells.
    static float SampleScalar(FlowField field, Vector3 p, float[] values)
    {
        var g = field.Grid;
        Vector3 lp = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(lp.X), y0 = (int)MathF.Floor(lp.Y), z0 = (int)MathF.Floor(lp.Z);
        float fx = lp.X - x0, fy = lp.Y - y0, fz = lp.Z - z0;

        float At(int x, int y, int z)
        {
            x = Math.Clamp(x, 0, g.Nx - 1);
            y = Math.Clamp(y, 0, g.Ny - 1);
            z = Math.Clamp(z, 0, g.Nz - 1);
            int idx = g.Index(x, y, z);
            return g.Cell[idx] != 0 ? 0f : values[idx];
        }

        float Lerp(float a, float b, float t) => a + (b - a) * t;
        float c00 = Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        float c10 = Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        float c01 = Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        float c11 = Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        float c0 = Lerp(c00, c10, fy);
        float c1 = Lerp(c01, c11, fy);
        return Lerp(c0, c1, fz);
    }

    // ---------------------------------------------------------------------------------------------
    // Replay CLI
    // ---------------------------------------------------------------------------------------------

    static int RunReplay(string[] args, string outDir)
    {
        string path = args[0];
        var backend = Backend.Auto;
        var quality = Quality.Preview;
        var numeric = new List<float>();
        bool thermal = false, airQuality = false, comfort = false;
        int snapshots = 0;
        int? occupants = null;
        float? indoor = null, outdoor = null;
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--gpu") backend = Backend.Gpu;
            else if (a == "--cpu") backend = Backend.Cpu;
            else if (a == "--refine") quality = Quality.Refine;
            else if (a == "--thermal") thermal = true;
            else if (a == "--airquality") airQuality = true;
            else if (a == "--comfort") comfort = true;
            else if (a == "--snapshots" && i + 1 < args.Length && int.TryParse(args[++i], out var sn)) snapshots = sn;
            else if (a == "--occupants" && i + 1 < args.Length && int.TryParse(args[++i], out var oc)) occupants = oc;
            else if (a == "--indoor" && i + 1 < args.Length && float.TryParse(args[++i], out var ind)) indoor = ind;
            else if (a == "--outdoor" && i + 1 < args.Length && float.TryParse(args[++i], out var outd)) outdoor = outd;
            else if (float.TryParse(a, out var v)) numeric.Add(v);
        }
        float windX = numeric.Count > 0 ? numeric[0] : 3f;
        float windY = numeric.Count > 1 ? numeric[1] : 0f;

        var model = BuildingModel.FromJson(File.ReadAllText(path));
        if (indoor.HasValue || outdoor.HasValue)
            model.Env = new Environment
            {
                IndoorTemperature = indoor ?? model.Env.IndoorTemperature,
                OutdoorTemperature = outdoor ?? model.Env.OutdoorTemperature,
                RelativeHumidity = model.Env.RelativeHumidity,
                OutdoorCo2 = model.Env.OutdoorCo2,
            };
        if (occupants.HasValue)
            foreach (var room in model.Rooms) room.Occupants = occupants.Value;

        var wind = new Vector2(windX, windY);
        var options = new RunOptions
        {
            Backend = backend, Quality = quality,
            Thermal = thermal, AirQuality = airQuality, Comfort = comfort, SnapshotCount = snapshots,
        };
        if (quality == Quality.Preview) options.TargetCells = 150_000;

        var result = Simulation.Run(model, wind, options);
        string name = Path.GetFileNameWithoutExtension(path);
        PrintResult(name, result);
        RenderScene($"replay-{name}", result, outDir);
        return 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Differentially heated cubic cavity (de Vahl Davis 1983 / Fusegi et al. 1991): a hand-built
    // VoxelGrid + a minimal BuildingModel passed straight to LbmSolver, bypassing Voxelizer.Build (whose
    // >= 8 m mandatory sky/side margin would swallow a physically-realistic, centimetre-scale cavity).
    // Layout along X (and identically along Y): ring(open) - ext.air - SOLID wall - N interior fluid
    // cells - SOLID wall - ext.air - ring(open); Z: SOLID ground (grid convention) - N interior fluid -
    // SOLID lid. Only X's two solid walls carry a Surface HeatSource (hot/cold); Y's and Z's stay plain
    // solid, i.e. adiabatic (GatherBoundaryG's default bounce-back), matching "adiabatic others".
    // ---------------------------------------------------------------------------------------------

    // Physical constants mirrored from LbmSolver.cs's own (private) NuPhysAir/Pr/GAccel/RhoCp -- kept in
    // lockstep by hand since the benchmark needs the exact same numbers to target a real Ra with real air.
    const float CavityNu = 1.5e-5f, CavityPr = 0.71f, CavityG = 9.81f, CavityRhoCp = 1.2f * 1005f;

    static (VoxelGrid Grid, BuildingModel Model, int HotX, int ColdX, int Y0, int Y1, int Z0, int Z1, float Dx, float CavityH, float THot, float TCold)
        BuildCavity(int n, double ra, float dT, float tCold)
    {
        float tHot = tCold + dT;
        float beta = 1f / (tCold + 273.15f);
        float alpha = CavityNu / CavityPr;
        float cavityH = MathF.Cbrt((float)ra * CavityNu * alpha / (CavityG * beta * dT));
        float dx = cavityH / n;

        int nx = n + 6, ny = n + 6, nz = n + 2;
        int hotX = 2, coldX = 3 + n;
        var grid = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = dx, Origin = Vector3.Zero, Cell = new byte[nx * ny * nz] };
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    bool solid = x == hotX || x == coldX || y == 2 || y == 3 + n || z == 0 || z == nz - 1;
                    grid.Cell[grid.Index(x, y, z)] = solid ? VoxelGrid.Solid : VoxelGrid.Air;
                }

        var model = new BuildingModel
        {
            Name = $"cavity-Ra{ra:E0}", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = n * dx, Analysed = true } },
            Env = new Environment { IndoorTemperature = tCold, OutdoorTemperature = tCold },
        };
        // Dummy footprint so Voxelizer.PlanBounds (called by BuildThermal for the T0 indoor/outdoor split,
        // irrelevant here since Indoor==Outdoor==tCold) doesn't throw on an empty model; never voxelized.
        model.Obstacles.Add(new Mesh
        {
            Id = "dummy",
            Vertices = new[] { new Vector3(0, 0, 0), new Vector3(nx * dx, 0, 0), new Vector3(nx * dx, ny * dx, 0), new Vector3(0, ny * dx, 0) },
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
        });
        // P14-02: each Surface box holds the heated wall cell and the ONE fluid layer next to it (width 2 dx), and spans only the cavity
        // interior in y and z (n dx). The Core fixes a solid face only when its solid cell centre is inside the box, so the adiabatic
        // side walls, floor and lid (outside the box) stay adiabatic. Before: 3 dx wide and unbounded, which held 2-3 layers of those
        // walls at T_hot / T_cold (pure conduction gave Nu 0.78 / 0.65 at n = 24).
        float midY = ny * dx * 0.5f, midZ = nz * dx * 0.5f, side = n * dx;
        model.Heat.Add(new HeatSource { Id = "hot", Name = "hot", Level = "L1", Kind = HeatKind.Surface, Enabled = true,
            Center = new Vector3((hotX + 1.0f) * dx, midY, midZ), Width = 2 * dx, Depth = side, Height = side, SurfaceTemperature = tHot });
        model.Heat.Add(new HeatSource { Id = "cold", Name = "cold", Level = "L1", Kind = HeatKind.Surface, Enabled = true,
            Center = new Vector3(coldX * dx, midY, midZ), Width = 2 * dx, Depth = side, Height = side, SurfaceTemperature = tCold });

        return (grid, model, hotX, coldX, 3, 2 + n, 1, n, dx, cavityH, tHot, tCold);
    }

    /// <summary>Mean hot/cold-wall Nusselt number from the time-averaged T field, via a second-order
    /// one-sided (3-point) finite difference at the wall -- nodes at the wall itself (exact, Dirichlet)
    /// and the cavity's own two nearest fluid layers at dx/2 and 3dx/2 from it, matching the anti-bounce-
    /// back's halfway wall placement (<see cref="LbmSolver"/>'s GatherBoundaryG). Validated against the
    /// pure-conduction limit (linear T profile) where both come out to exactly 1.</summary>
    static (double NuHot, double NuCold, int Samples) CavityNusselt(FlowField field, int hotX, int coldX,
        int y0, int y1, int z0, int z1, float dx, float cavityH, float tHot, float tCold)
    {
        var grid = field.Grid; var t = field.T;
        double sumHot = 0, sumCold = 0; int n = 0; float dT = tHot - tCold;
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                float t1h = t[grid.Index(hotX + 1, y, z)], t2h = t[grid.Index(hotX + 2, y, z)];
                float t1c = t[grid.Index(coldX - 1, y, z)], t2c = t[grid.Index(coldX - 2, y, z)];
                sumHot += (cavityH / dT) * (8.0 * tHot - 9.0 * t1h + t2h) / (3.0 * dx);
                sumCold += (cavityH / dT) * (9.0 * t1c - t2c - 8.0 * tCold) / (3.0 * dx);
                n++;
            }
        return (sumHot / n, sumCold / n, n);
    }

    // ---------------------------------------------------------------------------------------------
    // Checks
    // ---------------------------------------------------------------------------------------------

    static int RunAllChecks(string outDir)
    {
        string[] names =
        {
            "cross", "closed", "single", "reversed", "diagonal",
            "tworooms", "tworooms-doorclosed",
            "twostorey", "twostorey-closed",
            "supply", "fan", "fan-nowind", "atrium",
        };
        // WINDFLOW_CHECK_ONLY=<regex on the check label>: run only the matching checks (the thermal-scheme evaluation: 18/19/22/31 with
        // WINDFLOW_THERMAL_SCHEME set). Skips the basic scenes and every non-matching check, and stops after check 31.
        var only = System.Environment.GetEnvironmentVariable("WINDFLOW_CHECK_ONLY") is { Length: > 0 } onlyRx
            ? new System.Text.RegularExpressions.Regex(onlyRx) : null;
        if (only != null) names = [];
        var results = new Dictionary<string, SimulationResult>();
        var errors = new Dictionary<string, Exception>();

        foreach (var name in names)
        {
            try
            {
                var (floor, wind) = Scene(name);
                var options = new RunOptions { TargetCells = 400_000, Backend = Backend.Cpu };
                var result = Simulation.Run(floor, wind, options);
                results[name] = result;
                PrintResult(name, result);
                RenderScene(name, result, outDir);
            }
            catch (Exception ex)
            {
                errors[name] = ex;
                Console.WriteLine($"-- {name} -- EXCEPTION: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== checks ==");
        bool allPass = true;
        bool Check(string label, bool pass, string detail)
        {
            if (only != null && !only.IsMatch(label)) return true;
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {label}  {detail}");
            CheckLog.Record(pass ? "PASS" : "FAIL", label, detail);
            allPass &= pass;
            return pass;
        }

        // 1. voxel regression: west wall solid away from the window, air above the roof, ground layer solid.
        if (results.TryGetValue("cross", out var cross))
        {
            var g = cross.Field.Grid;
            bool CellSolid(Vector3 p)
            {
                int xi = Math.Clamp((int)MathF.Floor((p.X - g.Origin.X) / g.Dx), 0, g.Nx - 1);
                int yi = Math.Clamp((int)MathF.Floor((p.Y - g.Origin.Y) / g.Dx), 0, g.Ny - 1);
                int zi = Math.Clamp((int)MathF.Floor((p.Z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
                return g.Cell[g.Index(xi, yi, zi)] != 0;
            }
            bool wallSolid = CellSolid(new Vector3(0f, 1.0f, 2.5f));
            bool aboveRoofAir = !CellSolid(new Vector3(4f, 3f, H + 0.2f + 0.5f));
            bool groundSolid = true;
            for (int y = 0; y < g.Ny && groundSolid; y++)
            for (int x = 0; x < g.Nx; x++)
                if (g.Cell[g.Index(x, y, 0)] == 0) { groundSolid = false; break; }
            Check("1 voxel regression (west wall solid, above-roof air, ground layer solid)",
                wallSolid && aboveRoofAir && groundSolid, $"wallSolid={wallSolid} aboveRoofAir={aboveRoofAir} groundSolid={groundSolid}");
        }
        else Check("1 voxel regression", false, "cross scene did not run");

        // 2. cross mass balance. Since 2026-09-28 OpeningFlow.FlowRate is the u.n integral over the carved throat
        // (Analysis.ThroatCells), not a box-mean speed x the nominal area: Qeast moved 1.449 -> 1.393 (-3.9 %).
        // 2a compares two throats measured the same way, so the 15 % band is unchanged (the imbalance fell to
        // 0.6 %). 2c's [0.37, 3.7] is the order-of-magnitude bracket of Cd x A_eff x U x sqrt(dCp) for Cd 0.3-0.8
        // and dCp 0.2-1.6 at U = 3 m/s (0.41-3.1 m3/s, rounded out); a -4 % shift sits well inside it, unchanged.
        if (results.TryGetValue("cross", out var crossR))
        {
            var west = crossR.Openings.First(o => o.Opening.Id == "west");
            var east = crossR.Openings.First(o => o.Opening.Id == "east");
            var room = crossR.Rooms.First();
            double qw = west.FlowRate, qe = east.FlowRate;
            bool balance = Math.Abs(qw + qe) <= 0.15 * Math.Max(Math.Abs(qw), Math.Abs(qe));
            Check("2a cross mass balance |Qw+Qe|<=0.15*max", balance, $"Qwest={qw:F3} Qeast={qe:F3}");
            Check("2b cross Qwest<0, Qeast>0", qw < 0 && qe > 0, $"Qwest={qw:F3} Qeast={qe:F3}");
            bool band = Math.Abs(qe) >= 0.37 && Math.Abs(qe) <= 3.7;
            Check("2c cross |Qeast| in [0.37,3.7]", band, $"|Qeast|={Math.Abs(qe):F3}");
            Check("2d cross room ACH>5", room.Ach > 5, $"ACH={room.Ach:F2}");

            // 2e: a Room whose outline is drawn in the middle of the room's own air (no wall, no opening
            // on it) -- the shape of a detected room on an imported mesh. Re-analysed on the same field, so
            // no extra solve: everything crossing that outline is recirculation and must land in
            // InternalExchange (with a warning), not Inflow; the real room's Inflow must not move.
            var g0 = crossR.Field.Grid;
            var gCopy = new VoxelGrid
            {
                Nx = g0.Nx, Ny = g0.Ny, Nz = g0.Nz, Dx = g0.Dx, Origin = g0.Origin, Cell = g0.Cell,
                Distance = g0.Distance, FixedCells = g0.FixedCells, Devices = g0.Devices, Warnings = new(),
            };
            var fCopy = new FlowField
            {
                Grid = gCopy, Ux = crossR.Field.Ux, Uy = crossR.Field.Uy, Uz = crossR.Field.Uz, P = crossR.Field.P,
                T = crossR.Field.T, Tu = crossR.Field.Tu, Devices = crossR.Field.Devices,
                RoomFluxes = crossR.Field.RoomFluxes,   // "the same field": the real room keeps its solver-measured link flux; the added "mid" room has none and falls back to the face means
            };
            var mCopy = BuildingModel.FromJson(crossR.Model.ToJson());
            mCopy.Rooms.Add(new Room { Id = "mid", Name = "Middle", Level = "L1", Loops = { RectLoop(3f, 2f, 5f, 4f) }, ZMin = 0, ZMax = H });
            var reR = Analysis.Analyze(mCopy, fCopy, crossR.Wind, crossR.Options);
            var mid = reR.Rooms.First(r => r.Room.Id == "mid");
            var same = reR.Rooms.First(r => r.Room.Id == room.Room.Id);
            bool warned = gCopy.Warnings.Exists(w => w.Contains("Room Middle outline crosses open air"));
            Check("2e cross open-air outline: Inflow ~ 0, InternalExchange > 0 with a warning, real room unchanged",
                mid.Inflow <= 0.01f && mid.InternalExchange > 0.05f && warned && MathF.Abs(same.Inflow - room.Inflow) <= 1e-6f,
                $"middle Inflow={mid.Inflow:F4} InternalExchange={mid.InternalExchange:F3} m3/s warned={warned} | " +
                $"room Inflow={room.Inflow:F3} (re-analysed {same.Inflow:F3}) InternalExchange={room.InternalExchange:F4}");
        }
        else Check("2 cross mass balance", false, "cross scene did not run");

        // 3. closed
        if (results.TryGetValue("closed", out var closedR))
        {
            var room = closedR.Rooms.FirstOrDefault();
            if (room != null)
                Check("3 closed room MeanSpeed<0.15 and ACH<1", room.MeanSpeed < 0.15f && room.Ach < 1f,
                    $"MeanSpeed={room.MeanSpeed:F3} ACH={room.Ach:F2}");
            else
                Check("3 closed room MeanSpeed<0.15 and ACH<1", false, "no RoomResult (room has 0 air cells)");
        }
        else Check("3 closed", false, "closed scene did not run");

        // 4. single
        if (results.TryGetValue("single", out var singleR) && results.TryGetValue("cross", out var crossR2))
        {
            float singleInflow = singleR.Rooms.First().Inflow;
            float crossInflow = crossR2.Rooms.First().Inflow;
            Check("4 single Inflow < 0.5*cross Inflow", singleInflow < 0.5f * crossInflow,
                $"single={singleInflow:F3} cross={crossInflow:F3}");
        }
        else Check("4 single", false, "single or cross scene did not run");

        // 5. reversed. The mirrored wind must pass the same flow up to where the openings fall on the cell grid;
        // 20 % unchanged -- both sides are throat integrals now, and the gap fell from 13.7 % to 7.7 %.
        if (results.TryGetValue("reversed", out var reversedR) && results.TryGetValue("cross", out var crossR3))
        {
            float qeRev = Math.Abs(reversedR.Openings.First(o => o.Opening.Id == "east").FlowRate);
            float qeCross = Math.Abs(crossR3.Openings.First(o => o.Opening.Id == "east").FlowRate);
            bool within = Math.Abs(qeRev - qeCross) <= 0.2f * qeCross;
            Check("5 reversed |Qeast| within 20% of cross |Qeast|", within, $"reversed={qeRev:F3} cross={qeCross:F3}");
        }
        else Check("5 reversed", false, "reversed or cross scene did not run");

        // 6. diagonal
        if (results.TryGetValue("diagonal", out var diagR))
            Check("6 diagonal runs, ACH>0", diagR.Rooms.First().Ach > 0, $"ACH={diagR.Rooms.First().Ach:F2}");
        else
            Check("6 diagonal runs, ACH>0", false, errors.TryGetValue("diagonal", out var ex) ? $"threw: {ex.Message}" : "did not run");

        // 7. tworooms
        if (results.TryGetValue("tworooms", out var twoR))
        {
            var door = twoR.Openings.First(o => o.Opening.Id == "door");
            var a = twoR.Rooms.First(r => r.Room.Id == "A");
            var b = twoR.Rooms.First(r => r.Room.Id == "B");
            Check("7a tworooms door FlowRate>0", door.FlowRate > 0, $"door FlowRate={door.FlowRate:F3}");
            Check("7b tworooms B Inflow > 0.2*A Inflow", b.Inflow > 0.2f * a.Inflow, $"A={a.Inflow:F3} B={b.Inflow:F3}");
        }
        else Check("7 tworooms", false, "tworooms scene did not run");

        // 8. tworooms-doorclosed
        if (results.TryGetValue("tworooms-doorclosed", out var closedTwoR) && results.TryGetValue("tworooms", out var twoR2))
        {
            var a0 = twoR2.Rooms.FirstOrDefault(r => r.Room.Id == "A");
            var b0 = twoR2.Rooms.FirstOrDefault(r => r.Room.Id == "B");
            var a1 = closedTwoR.Rooms.FirstOrDefault(r => r.Room.Id == "A");
            var b1 = closedTwoR.Rooms.FirstOrDefault(r => r.Room.Id == "B");
            if (a0 != null && b0 != null && a1 != null && b1 != null)
            {
                bool aOk = a1.Ach < 0.3f * a0.Ach;
                bool bOk = b1.Ach < 0.3f * b0.Ach;
                Check("8 tworooms-doorclosed A & B ACH < 30% of tworooms", aOk && bOk,
                    $"A: {a1.Ach:F2} < 0.3*{a0.Ach:F2}  B: {b1.Ach:F2} < 0.3*{b0.Ach:F2}");
            }
            else
                Check("8 tworooms-doorclosed A & B ACH < 30% of tworooms", false,
                    $"missing RoomResult(s): A0={a0 != null} B0={b0 != null} A1={a1 != null} B1={b1 != null}");
        }
        else Check("8 tworooms-doorclosed", false, "tworooms-doorclosed or tworooms scene did not run");

        // 9. JSON round-trip (tworooms)
        {
            var (floor, _) = Scene("tworooms");
            var json = floor.ToJson();
            var back = BuildingModel.FromJson(json);
            bool countsOk = back.Obstacles.Count == floor.Obstacles.Count
                && back.Openings.Count == floor.Openings.Count
                && back.Rooms.Count == floor.Rooms.Count;
            bool firstVertexOk = floor.Obstacles.Count > 0 && back.Obstacles.Count > 0
                && back.Obstacles[0].Vertices.Length > 0 && floor.Obstacles[0].Vertices.Length > 0
                && back.Obstacles[0].Vertices[0] == floor.Obstacles[0].Vertices[0];
            bool openingsOk = true;
            for (int i = 0; i < floor.Openings.Count && i < back.Openings.Count; i++)
            {
                if (back.Openings[i].Normal != floor.Openings[i].Normal) openingsOk = false;
                if (back.Openings[i].IsOpen != floor.Openings[i].IsOpen) openingsOk = false;
            }
            Check("9 JSON round-trip tworooms (counts, first vertex, opening Normal/IsOpen)", countsOk && firstVertexOk && openingsOk,
                $"counts={countsOk} firstVertex={firstVertexOk} openings={openingsOk}");
        }

        // 10. Effective discharge coefficient of the cross scene's two windows in series (was "orifice consistency",
        // the same numbers expressed as a ratio to Cd = 0.61). Reference: an opening passes Q = Cd A sqrt(2 dp / rho);
        // a sharp-edged opening has Cd ~ 0.6-0.65 at high Re (Etheridge & Sandberg 1996, Building Ventilation;
        // ASHRAE Fundamentals ch. 16 uses 0.65 for large openings). Two openings in series: 1/A_eff^2 = 1/A1^2 + 1/A2^2.
        // dp = the SEALED building's windward-leeward facade difference (the closed scene -- the sealed-body
        // assumption). A = the CARVED throat area (OpeningFlow.CarvedArea): the hole the solver actually has, so this
        // tests the flow physics, not the voxel rounding (which OpeningFlow.EffectiveCd on the nominal area shows).
        // Band [0.37, 0.80]. Lower 0.37 = the old band's floor (0.6 x 0.61), kept: at this grid's Re (the NuLatMin
        // viscosity floor, ~1e2-1e3) a sharp-edged opening sits below the 0.6 asymptote -- 18b measures 0.33 at
        // Re 131. Upper 0.80, tightened from the old 1.10 (1.8 x 0.61): the sealed-body dp OVERSTATES the drop the
        // open openings see (the facade pressures relax toward each other once air flows), which can only pull the
        // apparent Cd down; above 0.8 the solver would be passing more air than a sharp-edged opening can.
        if (results.TryGetValue("closed", out var closedForP) && results.TryGetValue("cross", out var crossR4))
        {
            var field = closedForP.Field;
            float pWest = SampleScalar(field, new Vector3(-0.5f, 3f, 1.5f), field.P);
            float pEast = SampleScalar(field, new Vector3(8.5f, 3f, 1.5f), field.P);
            float dP = pWest - pEast;
            float dCp = dP / (0.5f * 1.2f * 3f * 3f);
            var eastF = crossR4.Openings.First(o => o.Opening.Id == "east");
            var westF = crossR4.Openings.First(o => o.Opening.Id == "west");
            float aEff = SeriesArea(westF.CarvedArea, eastF.CarvedArea);
            float qEastCross = Math.Abs(eastF.FlowRate);
            float cd = dP > 0 && aEff > 0 ? qEastCross / (aEff * MathF.Sqrt(2f * dP / 1.2f)) : float.NaN;
            bool pass = !float.IsNaN(cd) && cd >= 0.37f && cd <= 0.80f;
            Check("10 cross effective Cd of the two windows in series (sealed-body dp, carved area) in [0.37,0.80]", pass,
                $"dP={dP:F3} Pa  dCp={dCp:F3}  A_eff={aEff:F3} m2 (carved {westF.CarvedArea:F3}/{eastF.CarvedArea:F3})  Qeast={qEastCross:F3}  Cd={cd:F3}");

            // 10b: the per-opening number the app shows (OpeningFlow.EffectiveCd: nominal area, local dp across
            // each window). Sharp-edged windows 5.4 cells across in pressure-driven cross flow are the case the
            // warning band is built around, so neither window may trip it (same reference as 10). Floor 0.42, not
            // 0.5, under the (default) calibrated opening model: the model moves a sharp opening from the lattice's
            // 0.72 to 0.61 (x0.85), and the leeward window reads ~0.8x the windward on its local static dp because
            // the cross-room jet stagnates on the wall around it (0.54 / 0.72 resolved, 0.48 / 0.61 calibrated) --
            // see Analysis.CdWarnLowCalibrated.
            bool localOk = westF.EffectiveCd is >= 0.42f and <= 0.8f && eastF.EffectiveCd is >= 0.42f and <= 0.8f;
            Check("10b cross per-opening EffectiveCd in [0.42,0.8] (no Cd warning on plain cross ventilation)", localOk,
                $"west Cd_eff={westF.EffectiveCd:F3} dp={westF.PressureDrop:F3} | east Cd_eff={eastF.EffectiveCd:F3} dp={eastF.PressureDrop:F3} Pa");
        }
        else Check("10 effective Cd", false, "closed or cross scene did not run");

        // 11. twostorey pressure (the bias fix)
        float twoStoreyDCp = float.NaN, twoStoreyDP = float.NaN;
        if (results.TryGetValue("twostorey-closed", out var ts2c))
        {
            var field = ts2c.Field;
            float pW = SampleScalar(field, new Vector3(-0.5f, 3f, 1.5f), field.P);
            float pE = SampleScalar(field, new Vector3(8.5f, 3f, 1.5f), field.P);
            twoStoreyDP = pW - pE;
            twoStoreyDCp = twoStoreyDP / (0.5f * 1.2f * 3f * 3f);
            bool pass = twoStoreyDCp >= 0.5f && twoStoreyDCp <= 1.6f;
            Check("11 twostorey pressure dCp in [0.5,1.6]", pass,
                $"dCp={twoStoreyDCp:F3}  dP={twoStoreyDP:F3} Pa  pWest={pW:F3} Pa  pEast={pE:F3} Pa");
        }
        else Check("11 twostorey pressure", false, "twostorey-closed scene did not run");

        // 12. twostorey flow: the same effective-Cd statement as check 10, on the two-storey building (L1 open, L2
        // shut), same reference and the same [0.37, 0.80] band for the same reasons. At this Dx (319 mm) the carved
        // L1 windows are 1.631 m2 against 1.44 drawn, which is why the area is the carved one: on the nominal area
        // (the old ratio form, 1.355 x 0.61 = 0.83) the voxel rounding would read as orifice physics.
        if (results.TryGetValue("twostorey", out var ts) && !float.IsNaN(twoStoreyDP))
        {
            var eF = ts.Openings.First(o => o.Opening.Id == "east-l1");
            var wF = ts.Openings.First(o => o.Opening.Id == "west-l1");
            float aEff = SeriesArea(wF.CarvedArea, eF.CarvedArea);
            float qEastL1 = Math.Abs(eF.FlowRate);
            float cd = twoStoreyDP > 0 && aEff > 0 ? qEastL1 / (aEff * MathF.Sqrt(2f * twoStoreyDP / 1.2f)) : float.NaN;
            bool flowPass = !float.IsNaN(cd) && cd >= 0.37f && cd <= 0.80f;
            Check("12a twostorey L1 effective Cd in series (sealed-body dp, carved area) in [0.37,0.80]", flowPass,
                $"dP={twoStoreyDP:F3} Pa A_eff={aEff:F3} m2 (carved {wF.CarvedArea:F3}/{eF.CarvedArea:F3}) Qeast={qEastL1:F3} Cd={cd:F3}");

            float qWestL2 = ts.Openings.First(o => o.Opening.Id == "west-l2").FlowRate;
            float qEastL2 = ts.Openings.First(o => o.Opening.Id == "east-l2").FlowRate;
            Check("12b twostorey L2 openings closed FlowRate=0", qWestL2 == 0f && qEastL2 == 0f,
                $"west-l2={qWestL2:F3} east-l2={qEastL2:F3}");

            // Room interior extents (inside the wall envelope), not Voxelizer.PlanBounds: PlanBounds
            // includes the 1 m exterior-air margin around the building, which would count the outside
            // air halo as "inside L2" and always fail this check.
            var g = ts.Field.Grid;
            int airCount = 0;
            for (int z = 0; z < g.Nz; z++)
            {
                float cz = g.Origin.Z + (z + 0.5f) * g.Dx;
                if (cz <= 3.3f || cz >= 5.9f) continue;
                for (int y = 0; y < g.Ny; y++)
                for (int x = 0; x < g.Nx; x++)
                {
                    var c = g.CellCenter(x, y, z);
                    if (c.X < 0.1f || c.X > 7.9f || c.Y < 0.1f || c.Y > 5.9f) continue;
                    if (g.Cell[g.Index(x, y, z)] == VoxelGrid.Air) airCount++;
                }
            }
            Check("12c twostorey L2 sealed (0 Air cells, room interior, centre z in (3.3,5.9))", airCount == 0, $"airCount={airCount}");
        }
        else Check("12 twostorey flow", false, "twostorey or twostorey-closed scene did not run");

        // 13. supply
        if (results.TryGetValue("supply", out var sup))
        {
            var room = sup.Rooms.FirstOrDefault();
            if (room != null)
                Check("13a supply room Inflow within 25% of 0.139", Math.Abs(room.Inflow - 0.139f) <= 0.25f * 0.139f,
                    $"Inflow={room.Inflow:F4}");
            else
                Check("13a supply room Inflow within 25% of 0.139", false, "no RoomResult (room has 0 air cells)");

            bool sourcesOk = true;
            foreach (var sf in sup.Sources)
            {
                float set = sf.Source.FlowRate;
                float measured = Math.Abs(sf.FlowRate);
                bool ok = set <= 0 || Math.Abs(measured - set) <= 0.15f * set;
                sourcesOk &= ok;
                Console.WriteLine($"    source {sf.Source.Name} set={set:F4} measured={measured:F4} ok={ok}");
            }
            Check("13b supply sources within 15% of set", sourcesOk, "see source lines above");

            // 0.139 m3/s into an 8x6x3 m room, bulk speed ~= Q / (6x3 m^2 cross-section) ~= 0.008 m/s;
            // 0.02 was too tight for that estimate, so the bound is 0.005.
            if (room != null)
                Check("13c supply room MeanSpeed>0.005", room.MeanSpeed > 0.005f, $"MeanSpeed={room.MeanSpeed:F4}");
            else
                Check("13c supply room MeanSpeed>0.005", false, "no RoomResult (room has 0 air cells)");
        }
        else Check("13 supply", false, "supply scene did not run");

        // 14. fan-nowind: closed room + fan only, wind (0,0), isolates the fan from cross flow.
        // The solver is being changed to accept zero wind when sources exist; until that lands this
        // scene returns a zero field and these checks are expected to FAIL. The "fan" scene (with wind)
        // is still run above so its images get produced, but it no longer has a check of its own.
        if (results.TryGetValue("fan-nowind", out var fanNoWind))
        {
            var fanSource = fanNoWind.Model.Sources.First(s => s.Id == "fan");
            var fanFlow = fanNoWind.Sources.FirstOrDefault(s => s.Source.Id == "fan");
            bool flowOk = fanFlow != null && Math.Abs(Math.Abs(fanFlow.FlowRate) - 0.2f) <= 0.2f * 0.2f;
            Check("14a fan-nowind measured flow within 20% of 0.2", flowOk,
                fanFlow != null ? $"measured={fanFlow.FlowRate:F3}" : "no SourceFlow for fan");

            var room = fanNoWind.Rooms.FirstOrDefault();
            Check("14b fan-nowind room MeanSpeed>0.01", room != null && room.MeanSpeed > 0.01f,
                room != null ? $"MeanSpeed={room.MeanSpeed:F4}" : "no RoomResult (room has 0 air cells)");

            bool nearStreamline = fanNoWind.Streamlines.Any(s => s.Points.Length > 0 && Vector3.Distance(s.Points[0], fanSource.Center) <= 0.6f);
            Check("14c fan-nowind >=1 streamline starts within 0.6m of fan centre", nearStreamline,
                $"streamlines={fanNoWind.Streamlines.Count}");
        }
        else Check("14 fan-nowind", false, errors.TryGetValue("fan-nowind", out var fnwEx) ? $"threw: {fnwEx.Message}" : "fan-nowind scene did not run");

        // 15. atrium
        if (results.TryGetValue("atrium", out var atr))
        {
            var l2 = atr.Rooms.First(r => r.Room.Level == "L2");
            Check("15a atrium L2 Inflow>0", l2.Inflow > 0, $"L2 Inflow={l2.Inflow:F3}");

            float totalInflow = atr.Rooms.Sum(r => r.Inflow);
            float totalVolume = atr.Rooms.Sum(r => r.Volume);
            float totalAch = totalVolume > 0 ? totalInflow * 3600f / totalVolume : 0f;
            Check("15b atrium total ACH>1", totalAch > 1f, $"totalACH={totalAch:F2}");
        }
        else Check("15 atrium", false, "atrium scene did not run");

        // 16. GPU parity (only if LbmSolver.GpuAvailable). Both backends are measured with the same throat integral,
        // so the carved-area change (-3.9 % on Qeast) cancels; bands unchanged.
        if (LbmSolver.GpuAvailable)
        {
            if (results.TryGetValue("cross", out var cpuResult))
            {
                var (floorGpu, windGpu) = Scene("cross");
                var gpuOptions = new RunOptions { TargetCells = 400_000, Backend = Backend.Gpu };
                var gpuResult = Simulation.Run(floorGpu, windGpu, gpuOptions);

                float qGpu = Math.Abs(gpuResult.Openings.First(o => o.Opening.Id == "east").FlowRate);
                float qCpu = Math.Abs(cpuResult.Openings.First(o => o.Opening.Id == "east").FlowRate);
                bool qOk = qCpu > 0 && Math.Abs(qGpu - qCpu) <= 0.03f * qCpu;
                float achGpu = gpuResult.Rooms.First().Ach;
                float achCpu = cpuResult.Rooms.First().Ach;
                bool achOk = achCpu > 0 && Math.Abs(achGpu - achCpu) <= 0.05f * achCpu;
                double mlupsGpu = gpuResult.Field.Seconds > 0 ? (double)gpuResult.Field.Grid.Count * gpuResult.Field.Steps / gpuResult.Field.Seconds / 1e6 : 0;
                double mlupsCpu = cpuResult.Field.Seconds > 0 ? (double)cpuResult.Field.Grid.Count * cpuResult.Field.Steps / cpuResult.Field.Seconds / 1e6 : 0;
                Check("16 GPU parity |Qeast| within 3%, ACH within 5%", qOk && achOk,
                    $"Qgpu={qGpu:F3} Qcpu={qCpu:F3} ACHgpu={achGpu:F2} ACHcpu={achCpu:F2} " +
                    $"MLUPSgpu={mlupsGpu:F1} MLUPScpu={mlupsCpu:F1} backendGpu={gpuResult.Field.Backend} backendCpu={cpuResult.Field.Backend}");
            }
            else Check("16 GPU parity", false, "cross (CPU) scene did not run");
        }
        else
        {
            Console.WriteLine("[SKIPPED] 16 GPU parity  LbmSolver.GpuAvailable=false");
        }

        // 17. JSON round-trip (atrium)
        {
            var (floor, _) = Scene("atrium");
            var json = floor.ToJson();
            var back = BuildingModel.FromJson(json);
            bool countsOk = back.Obstacles.Count == floor.Obstacles.Count
                && back.Openings.Count == floor.Openings.Count
                && back.Rooms.Count == floor.Rooms.Count
                && back.Sources.Count == floor.Sources.Count;
            bool firstVertexOk = floor.Obstacles.Count > 0 && back.Obstacles.Count > 0
                && back.Obstacles[0].Vertices.Length > 0 && floor.Obstacles[0].Vertices.Length > 0
                && back.Obstacles[0].Vertices[0] == floor.Obstacles[0].Vertices[0];
            bool openingsOk = true;
            for (int i = 0; i < floor.Openings.Count && i < back.Openings.Count; i++)
            {
                if (back.Openings[i].Normal != floor.Openings[i].Normal) openingsOk = false;
                if (back.Openings[i].IsOpen != floor.Openings[i].IsOpen) openingsOk = false;
            }
            bool sourcesOk = true;
            for (int i = 0; i < floor.Sources.Count && i < back.Sources.Count; i++)
            {
                if (back.Sources[i].Direction != floor.Sources[i].Direction) sourcesOk = false;
                if (Math.Abs(back.Sources[i].FlowRate - floor.Sources[i].FlowRate) > 1e-6f) sourcesOk = false;
            }
            Check("17 JSON round-trip atrium (counts, vertex, opening Normal/IsOpen, source Direction/FlowRate)",
                countsOk && firstVertexOk && openingsOk && sourcesOk,
                $"counts={countsOk} firstVertex={firstVertexOk} openings={openingsOk} sources={sourcesOk}");
        }

        // ---------------------------------------------------------------------------------------------
        // Advanced tier: thermal, air quality, comfort, snapshots, iso-surfaces, sweeps, annual, report.
        // Methods owned by the other seven agents may still throw NotImplementedException while their
        // features are in flight; that is reported as [WAIT], not [FAIL], and does not affect exit code.
        // ---------------------------------------------------------------------------------------------

        void Report(string label, Verdict v, string detail)
        {
            if (only != null && !only.IsMatch(label)) return;
            string tag = v switch { Verdict.Pass => "PASS", Verdict.Fail => "FAIL", _ => "WAIT" };
            Console.WriteLine($"[{tag}] {label}  {detail}");
            CheckLog.Record(tag, label, detail);
            if (v == Verdict.Fail) allPass = false;
        }

        void ReportTry(string label, Func<(bool Pass, string Detail)> body)
        {
            if (only != null && !only.IsMatch(label)) return;
            try
            {
                var (pass, detail) = body();
                Report(label, pass ? Verdict.Pass : Verdict.Fail, detail);
            }
            catch (NotImplementedException ex)
            {
                Report(label, Verdict.Wait, $"not implemented yet ({ex.Message})");
            }
            catch (Exception ex)
            {
                Report(label, Verdict.Fail, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        string[] advNames = only != null ? (only.IsMatch("18") || only.IsMatch("19") || only.IsMatch("22") ? ["stack", "heat", "comfort"] : only.IsMatch("32 tworooms") ? ["tworooms-aq"] : []) : ["stack", "heat", "co2", "co2-closed", "tworooms-aq", "comfort", "anim", "tracer"];
        var advResults = new Dictionary<string, SimulationResult>();
        var advWait = new HashSet<string>();
        var advErrors = new Dictionary<string, Exception>();
        var sceneElapsed = new Dictionary<string, double>();

        foreach (var name in advNames)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (floor, wind, opts) = AdvancedScene(name);
                var result = Simulation.Run(floor, wind, opts);
                sw.Stop();
                sceneElapsed[name] = sw.Elapsed.TotalSeconds;
                advResults[name] = result;
                PrintResult(name, result);
                RenderScene(name, result, outDir);
            }
            catch (NotImplementedException ex)
            {
                sw.Stop();
                sceneElapsed[name] = sw.Elapsed.TotalSeconds;
                advWait.Add(name);
                Console.WriteLine($"-- {name} -- WAIT (not implemented: {ex.Message})");
            }
            catch (Exception ex)
            {
                sw.Stop();
                sceneElapsed[name] = sw.Elapsed.TotalSeconds;
                advErrors[name] = ex;
                Console.WriteLine($"-- {name} -- EXCEPTION: {ex.Message}");
            }
        }

        void CheckScene(string label, string sceneName, Func<SimulationResult, (bool, string)> body)
        {
            if (advResults.TryGetValue(sceneName, out var r))
                ReportTry(label, () => body(r));
            else if (advWait.Contains(sceneName))
                Report(label, Verdict.Wait, "scene not implemented yet");
            else
                Report(label, Verdict.Fail, advErrors.TryGetValue(sceneName, out var e) ? $"scene threw: {e.Message}" : "scene did not run");
        }

        Console.WriteLine();
        Console.WriteLine("== advanced checks ==");

        // 18. stack effect. 18a is a sign test; its flows rose ~13 % with the carved-throat integral (the carved
        // stack openings are 1.152 m2 against 1.000 drawn at this Dx) and now agree with 18-setup's own throat flux.
        CheckScene("18a stack west(low)<0, east(high)>0", "stack", r =>
        {
            var west = r.Openings.First(o => o.Opening.Id == "west-low");
            var east = r.Openings.First(o => o.Opening.Id == "east-high");
            return (west.FlowRate < 0 && east.FlowRate > 0, $"Qwest={west.FlowRate:F4} Qeast={east.FlowRate:F4}");
        });
        // 18b (bespoke session, not advResults["stack"]): the stack-effect flow rate against the
        // orifice equation. Investigated in this order, all four numbers printed below:
        //
        // (1) RUN LENGTH. Q reaches a quasi-steady value within ~40 s and then declines slowly, tracking the
        //     decaying indoor dT; running 2x the step budget moves Q by <10% and the ratio to the analytic
        //     estimate by <5%. Not a developing- or decaying-transient artefact.
        // (2) WHAT KEEPS THE ROOM WARM: nothing. There is no heat source in this scene, so indoor T decays
        //     from its initial 26 C toward the 16 C outdoor air as the stack effect itself flushes the room
        //     (26 -> ~23.3 by the step budget, ~22.5 at 2x). The analytic estimate must therefore use the
        //     ACTUAL contemporaneous mean indoor T, not the initial 26 C: that alone moves the reference
        //     from 0.491 to ~0.42 m3/s.
        // (3) THE FORMULA. Two openings in series: Q = Cd * A_eff * sqrt(2*g*dh*dT/Tbar) with
        //     A_eff = A1*A2/sqrt(A1^2+A2^2) = 1/sqrt(2) m2 for two 1 m2 openings, dh = 2.0 m between the
        //     opening centres (z = 0.5 and z = 2.5), Tbar the mean absolute temperature. Checked against the
        //     stratification-resolved form, which replaces dh*dT by the density-deficit integral
        //     int_{z1}^{z2} (T(z) - T_out) dz: the measured indoor profile is very nearly linear
        //     (22.5 at 0.4 m to 24.0 at 2.8 m), so the two agree to <4% -- stratification is NOT the gap.
        //     Cd = 0.6 is the SHARP-ORIFICE HIGH-REYNOLDS ASYMPTOTE, which is the part that does not apply
        //     here, see (4).
        // (4) RESOLUTION AND REYNOLDS NUMBER. The 1 m opening is 1/Dx = 3.7 -> 4 cells across, so it is
        //     voxelized to 4x4 cells = 1.15 m2 against its nominal 1.00 m2 (a 15% over-area; Analysis's
        //     OpeningFlow.FlowRate multiplies the box-mean normal speed by the NOMINAL area and so reads
        //     ~14% below the true throat flux -- that is why this check integrates the flux itself).
        //     Resolution is not the limiter, though: the isothermal orifice check (10) reproduces Cd ~ 0.6
        //     to within 3% at this SAME Dx. What differs is the Reynolds number. The lattice viscosity has a
        //     stability floor NuLatMin, so nu_eff = max(nu_air*dt/Dx^2, NuLatMin)*Dx^2/dt ~ 1.7e-3 m2/s here
        //     = ~113x air; with a 0.23 m/s throat velocity that puts the opening at Re ~ 130 instead of the
        //     ~1.5e4 of real air. Classical sharp-edged-orifice data (Johansen 1930; Lichtarowicz, Duggins &
        //     Markland 1965) give Cd ~ 0.35-0.45 at Re ~ 1e2, reaching 0.6 only above Re ~ 1e4. Check 10 sits
        //     at Re ~ 250 on a 1 Pa wind stagnation head, twice the Reynolds number and four times the head.
        //     So the physically right reference for THIS grid is Cd ~ 0.4, and the check uses that; Cd_eff and
        //     Re are printed so a finer-grid run can show Cd_eff climbing toward 0.6 (nu_eff scales with Dx).
        //
        // A REAL SOLVER DEFECT was found and fixed along the way: the regularized collision dropped the
        // first moment of the forcing term, so the Boussinesq force delivered only (3/2 - 1/(2*tau0)) of
        // its nominal value = 0.503x at the tau0 ~ 0.5015 of a building-scale room (see swOdd in
        // LbmSolver.CollideForced). The buoyancy head was correspondingly ~60% of theory and Q was 1/sqrt(2)
        // low; Qthroat went 0.192 -> 0.261 m3/s when it was fixed, and the measured hydrostatic gradient now
        // matches rho*g*beta*(T - T_out) to 2-4% (asserted below).
        const float CdStackAtGridRe = 0.4f;   // see (4): the Cd this grid's opening Reynolds number supports
        (float QThroat, float MeanIndoorT, float HeadMeasured, float HeadTheory, float CdEff, float Re,
            float ThroatArea, float SimSeconds, string Trend)? stackTrace = null;
        ReportTry("18-setup stack bespoke session (throat flux, buoyancy head, Cd_eff -- feeds 18b)", () =>
        {
            var (floor, wind, opts) = AdvancedScene("stack");
            var grid = Voxelizer.Build(floor, opts.TargetCells!.Value);
            using var session = LbmSolver.Start(grid, floor, wind, opts);
            var ux = new float[grid.Count]; var uy = new float[grid.Count]; var uz = new float[grid.Count];
            var t = new float[grid.Count];
            var room = floor.Rooms[0];
            float dx = grid.Dx;
            int XiAt(float x) => Math.Clamp((int)MathF.Floor((x - grid.Origin.X) / dx), 0, grid.Nx - 1);
            int xiW = XiAt(0f), xiE = XiAt(8f);   // the wall planes themselves, i.e. the opening throats

            // True flux integral over the carved cells of one opening: sum(ux)*Dx^2 on the wall plane.
            // Not Analysis's OpeningFlow.FlowRate, which averages over a box straddling the wall and then
            // multiplies by the NOMINAL area -- see (4).
            (float q, float area) Throat(float[] vx, int xi, float zc)
            {
                float q = 0; int n = 0;
                for (int zi = 0; zi < grid.Nz; zi++)
                for (int yi = 0; yi < grid.Ny; yi++)
                {
                    var c = grid.CellCenter(xi, yi, zi);
                    if (MathF.Abs(c.Y - 3f) > 0.5f + dx || MathF.Abs(c.Z - zc) > 0.5f + dx) continue;
                    int i = grid.Index(xi, yi, zi);
                    if (grid.Cell[i] != VoxelGrid.Air) continue;
                    q += vx[i]; n++;
                }
                return (q * dx * dx, n * dx * dx);
            }
            // room-footprint mean of `src` on layer zi
            float Layer(float[] src, int zi)
            {
                double sum = 0; int n = 0;
                for (int yi = 0; yi < grid.Ny; yi++)
                for (int xi = 0; xi < grid.Nx; xi++)
                {
                    int i = grid.Index(xi, yi, zi);
                    if (grid.Cell[i] != VoxelGrid.Air) continue;
                    var c = grid.CellCenter(xi, yi, zi);
                    if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                    sum += src[i]; n++;
                }
                return n > 0 ? (float)(sum / n) : float.NaN;
            }

            // (1) Q(t) over the run
            int samples = 8, chunk = Math.Max(1, session.PlannedSteps / samples);
            var qeTrace = new List<float>(); var secTrace = new List<float>();
            while (session.Steps < session.PlannedSteps)
            {
                session.Step((int)Math.Min(chunk, session.PlannedSteps - session.Steps));
                session.ReadVelocity(ux, uy, uz);
                qeTrace.Add(Throat(ux, xiE, 2.5f).q); secTrace.Add((float)session.SimSeconds);
            }
            // Measure on the developed state only: throw away the average accumulated while Q was still
            // settling, then average one more framework averaging window (ResetAveraging exists for this).
            session.ResetAveraging();
            session.Step(Math.Max(1, session.PlannedSteps - session.WarmupSteps));
            var field = session.ToField();
            session.ReadTemperature(t);

            var (qw, areaW) = Throat(field.Ux, xiW, 0.5f);
            var (qe, areaE) = Throat(field.Ux, xiE, 2.5f);
            float qThroat = 0.5f * (MathF.Abs(qw) + MathF.Abs(qe));
            float throatArea = 0.5f * (areaW + areaE);

            // (2)/(3) actual indoor dT and the buoyancy head, both from the layer profile between the
            // opening centres; plus the head the solved pressure field actually delivers there.
            const float tOut = 16f;
            double tSum = 0, span = 0; int zLo = -1, zHi = -1;
            for (int zi = 0; zi < grid.Nz; zi++)
            {
                float cz = grid.Origin.Z + (zi + 0.5f) * dx;
                float tl = Layer(t, zi);
                if (cz < 0.5f || cz > 2.5f || float.IsNaN(tl)) continue;
                tSum += tl * dx; span += dx;
                if (zLo < 0) zLo = zi;
                zHi = zi;
            }
            float meanIndoorT = span > 0 ? (float)(tSum / span) : float.NaN;
            float tBar = (meanIndoorT + tOut) / 2f + 273.15f;
            float headTheory = 1.2f * 9.81f * (meanIndoorT - tOut) / tBar * (zHi - zLo) * dx;
            float headMeasured = (zLo >= 0 && zHi > zLo)
                ? (Layer(field.P, zHi) - Layer(field.P, zLo)) - 0f : float.NaN;

            float uIdeal = MathF.Sqrt(2f * 9.81f * 2.0f * (meanIndoorT - tOut) / tBar);
            float aEffRealized = throatArea * throatArea / MathF.Sqrt(2f * throatArea * throatArea);
            float cdEff = qThroat / MathF.Max(aEffRealized * uIdeal, 1e-9f);

            // nu_eff: must mirror LbmSolver's tau0 = 0.5 + 3*max(NuPhysAir*dt/Dx^2, NuLatMin)
            const float nuPhysAir = 1.5e-5f, nuLatMin = 5e-4f;
            float dt = (float)(session.SimSeconds / session.Steps);
            float nuEff = MathF.Max(nuPhysAir * dt / (dx * dx), nuLatMin) * dx * dx / dt;
            float re = qThroat / throatArea * 1.0f / nuEff;

            stackTrace = (qThroat, meanIndoorT, headMeasured, headTheory, cdEff, re, throatArea,
                (float)session.SimSeconds, string.Join(" -> ", qeTrace.Select(q => q.ToString("F3"))));
            return (true, $"Qeast(t)={stackTrace.Value.Trend} over {secTrace[^1]:F0}s  " +
                $"Qthroat(w/e)={qw:F4}/{qe:F4} throatArea={throatArea:F3}m2 (nominal 1.000) " +
                $"meanIndoorT(0.5-2.5m)={meanIndoorT:F2} head measured/theory={headMeasured:F4}/{headTheory:F4}Pa " +
                $"Cd_eff={cdEff:F3} Re={re:F0} nu_eff={nuEff:E2}m2/s (x{nuEff / nuPhysAir:F0} air)");
        });
        ReportTry("18b stack |Q| within 40% of the orifice equation at the ACTUAL dT and grid Cd(Re)", () =>
        {
            if (!stackTrace.HasValue) throw new NotImplementedException("18-setup did not produce a trace");
            var (qThroat, meanIndoorT, headMeasured, headTheory, cdEff, re, throatArea, simSeconds, _) = stackTrace.Value;
            const float tOut = 16f;
            float dT = MathF.Max(meanIndoorT - tOut, 0.01f);
            float tBar = (meanIndoorT + tOut) / 2f + 273.15f;
            float aEffNominal = 1f / MathF.Sqrt(2f);                       // two 1 m2 openings in series, see (3)
            float uIdeal = MathF.Sqrt(2f * 9.81f * 2.0f * dT / tBar);      // dh = 2.0 m between opening centres
            float qEst = CdStackAtGridRe * aEffNominal * uIdeal;
            float qEstHighRe = 0.6f * aEffNominal * uIdeal;                // the Cd = 0.6 asymptote, for reference
            bool within = qThroat >= 0.6f * qEst && qThroat <= 1.4f * qEst;
            // The buoyancy head the solver delivers must match hydrostatics -- this is what the
            // regularized-collision force fix repaired, and a regression here would bring the old 1/sqrt(2)
            // deficit straight back, so fail the check if it drifts more than 20%.
            float headErr = headTheory > 0 ? MathF.Abs(headMeasured - headTheory) / headTheory : float.NaN;
            bool headOk = !float.IsNaN(headErr) && headErr <= 0.20f;
            return (within && headOk,
                $"meanIndoorT={meanIndoorT:F2} dT={dT:F2} Qest(Cd={CdStackAtGridRe:F2})={qEst:F3} Qmeas={qThroat:F3} " +
                $"ratio={qThroat / qEst:F3} | Cd_eff={cdEff:F3} Re={re:F0} (Cd=0.6 asymptote would be " +
                $"{qEstHighRe:F3}, ratio {qThroat / qEstHighRe:F3}) | head measured/theory={headMeasured:F4}/" +
                $"{headTheory:F4}Pa err={headErr * 100:F1}% headOk={headOk} SimSeconds={simSeconds:F0}");
        });
        CheckScene("18c stack room MeanTemperature finite in [16,26]", "stack", r =>
        {
            if (r.Field.T.Length == 0) throw new NotImplementedException("field.T empty, thermal not wired yet");
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.MeanTemperature)) throw new NotImplementedException("MeanTemperature NaN, Analysis.Finish not wired yet");
            bool ok = !float.IsInfinity(room.MeanTemperature) && room.MeanTemperature >= 16f && room.MeanTemperature <= 26f;
            return (ok, $"MeanTemperature={room.MeanTemperature:F2}");
        });

        // 19. heat source stratification. A bespoke LbmSession (not the shared advResults["heat"]) for two
        // reasons: SimulationResult/FlowField do not carry dt, so the physical seconds simulated can only be
        // read off the session; and vertical stratification needs a LONGER run than the framework thermal
        // development budget.
        //
        // Why longer (derivation, measured below): a sealed room with one convective source is the classic
        // FILLING BOX (Baines & Turner, JFM 37, 1969). The plume entrains room air on the way up, spreads
        // under the ceiling and descends as a stratified layer, so the room vertical temperature difference
        // builds over the time the plume takes to recycle the whole room volume -- NOT over the time the bulk
        // mean takes to warm up (which is all LbmSession PlannedSteps extension targets, and all 19b/19c
        // need). For a point source of buoyancy flux B = g*beta*P/(rho*cp) the plume volume flux at height H
        // is Q_p = 0.156 * B^(1/3) * H^(5/3), so one turnover is t_fill = V / Q_p and the stratification
        // keeps growing roughly linearly over the first few turnovers. Traced on this scene (500 W,
        // V = 108 m3, H = 3 m -> Q_p = 0.233 m3/s, t_fill = 464 s): dT(2.5 m - 0.5 m) = 0.11 K at 275 s,
        // 0.17 K at 600 s, 0.30 K at ~1120 s, 0.47 K at 1650 s -- about 2.4 turnovers to reach the 0.3 K
        // this check asks for, so run 3 for margin over the threshold (2.5 measured 0.32 K, only 7% clear).
        // Run TurnoversFor19a turnovers (a physical time, not a tuned step count) and
        // print both, so the number stays auditable instead of fitted.
        const float TurnoversFor19a = 3f;
        (FlowField Field, Room Room, float SimSeconds, float MidSeconds, float FillSeconds, float TotalWatts,
            float IndoorVolume, float MeanT, long Steps, string Backend)? heatTrace = null;
        ReportTry("19-setup heat bespoke session (feeds 19a and 19c)", () =>
        {
            var (floor, wind, opts) = AdvancedScene("heat");
            opts.ThermalScheme = ThermalScheme.Hybrid;   // 19a reference below: the D3Q7 floor puts it at 0.54 K
            opts.HoldIndoorTemperature = false;   // P14-01 (6j): 19a/19c test the free-running filling box (rule 3, test configuration)
            var grid = Voxelizer.Build(floor, opts.TargetCells!.Value);
            using var session = LbmSolver.Start(grid, floor, wind, opts);
            float totalWatts = floor.Heat.Where(h => h.Enabled).Sum(h => h.Watts);
            var room = floor.Rooms[0];
            float vol = 0f;
            for (int i = 0; i < grid.Count; i++)
            {
                if (grid.Cell[i] != VoxelGrid.Air) continue;
                var c = grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny));
                if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                vol += grid.Dx * grid.Dx * grid.Dx;
            }
            // filling-box turnover, see the derivation above
            float height = MathF.Max(room.ZMax - room.ZMin, grid.Dx);
            float tOutK = floor.Env.OutdoorTemperature + 273.15f;
            float buoyancyFlux = 9.81f / tOutK * totalWatts / (1.2f * 1005f);
            float plumeFlux = 0.156f * MathF.Cbrt(buoyancyFlux) * MathF.Pow(height, 5f / 3f);
            float fillSeconds = plumeFlux > 0 ? vol / plumeFlux : float.NaN;
            float targetSeconds = TurnoversFor19a * fillSeconds;

            session.Step(session.PlannedSteps);                      // framework budget first...
            float dt = (float)(session.SimSeconds / session.Steps);  // ...which is what gives us dt
            if (!float.IsNaN(targetSeconds) && session.SimSeconds < targetSeconds)
            {
                // Stepping past PlannedSteps just keeps accumulating into the same time-average (the ramps
                // stay clamped at 1) -- the same trick checks 31a-c use for the slow buoyant cavity.
                int extra = (int)MathF.Ceiling((targetSeconds - (float)session.SimSeconds) / dt);
                session.Step(extra);
            }
            // Average the DEVELOPED state, not the whole build-up (see the comment above).
            session.ResetAveraging();
            session.Step(Math.Max(1, session.PlannedSteps - session.WarmupSteps));
            var field = session.ToField();
            // Volume mean T over the room own air cells. NOT RoomResult.MeanTemperature: that is the
            // occupied-zone (0.1-1.8 m) mean, which a stratified room reports below its volume mean -- and
            // it is filled in by Analysis.Finish, which only Simulation.Run calls, so on a bespoke Analyze
            // it stays NaN (19c read measuredRise=NaN until this was fixed).
            double tSum = 0; int tCount = 0;
            if (field.T.Length == grid.Count)
                for (int i = 0; i < grid.Count; i++)
                {
                    if (grid.Cell[i] != VoxelGrid.Air) continue;
                    var c = grid.CellCenter(i % grid.Nx, i / grid.Nx % grid.Ny, i / (grid.Nx * grid.Ny));
                    if (c.Z < room.ZMin || c.Z > room.ZMax || !InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                    tSum += field.T[i]; tCount++;
                }
            float meanT = tCount > 0 ? (float)(tSum / tCount) : float.NaN;
            // Physical time at the mid-point of the averaging window, which after the ResetAveraging above
            // is the last (PlannedSteps - WarmupSteps) steps of the run -- that is what the time-averaged T
            // of ToField corresponds to for a linear-in-t bulk rise.
            float tMid = dt * (session.Steps - 0.5f * (session.PlannedSteps - session.WarmupSteps));
            heatTrace = (field, room, (float)session.SimSeconds, tMid, fillSeconds, totalWatts, vol, meanT,
                session.Steps, session.Backend);
            return (true, $"steps={session.Steps} (planned {session.PlannedSteps}) SimSeconds={session.SimSeconds:F1} " +
                $"fillingBoxTurnover={fillSeconds:F0}s x{TurnoversFor19a:F1} totalWatts={totalWatts:F0} " +
                $"indoorVolume={vol:F1}m3 roomMeanT={meanT:F2} backend={session.Backend}");
        });
        // 19a moved 0.40 K -> 2.17 K when interpolated (Bouzidi) bounce-back landed, with the thermal lattice
        // untouched. Measured 2026-09-24 by re-running this exact session with VoxelGrid.Distance emptied
        // (= halfway bounce-back), GPU, same steps:
        //   * NOT a thermal defect. The heater is volumetric (Equipment) and every wall here is adiabatic
        //     plain D3Q7 bounce-back, so no anti-bounce-back link exists in this scene and the wall
        //     position cannot move any heat flux. The energy balance is identical to 3 decimals (rise
        //     5.224 K both ways, 19c), so the same heat is in the room, only distributed differently.
        //   * It is the momentum wall. Halfway bounce-back carries a room-wide double roll at 0.10-0.18 m/s
        //     (interior mean |u| 0.106 m/s, ceiling layer 0.18) that stirs the whole filling box, so the
        //     stratification stays at 0.40 K. With the interpolated wall the flow is what a filling box
        //     should be: a plume (0.23 m/s at 1.5 m), a weak ceiling jet (0.05-0.08) and a quiet ambient
        //     (interior mean 0.025 m/s), and the warm layer builds down from the ceiling.
        //   * Neither number is grid-converged. Refining the same scene: Dx 268 mm 0.40 (halfway) / 2.17
        //     (interpolated), 213 mm 0.58 / 1.53, 179 mm 0.67 / diverged. The two wall models close in on
        //     each other from opposite sides, so the resolved answer lies roughly between 0.7 and 1.5 K.
        //     The old 0.40 K was low because halfway walls let that under-resolved circulation mix the room.
        //     2.17 K is the interpolated-wall value at this Dx, not a measured physical stratification (the old
        //     0.3 K floor was a sanity check that both models passed; it is replaced by the reference band below).
        //   * Open: the interpolated wall diverges at 179 mm where halfway runs (lower lattice viscosity);
        //     it is not exercised by any check at the sandbox resolutions.
        // 19a reference (2026-10-10): the sealed 500 W room is an AREA-source filling box (1 m x 1 m x 0.5 m heater, virtual origin 0-1 m
        // below the floor, entrainment alpha_e 0.083-0.13), which gives an ambient dT(2.5 m - 0.5 m) of 2-9 K at t ~ 1420 s
        // (scratch analysis thermal2/fb.py; Baines & Turner 1969 JFM 37:51, Worster & Huppert 1983 JFM 132:457). The old "4-8 K" was a
        // point source. The plume covers ~2 % of the footprint, so the room-footprint mean below is within ~7 % of the ambient-only one.
        // The D3Q7 scheme sits at 0.54 K here (its diffusivity floor, 1100x air, is the filling box's ambient kappa) and fails the band, so this
        // session runs ThermalScheme.Hybrid (2.13 K on the old 2.73 m room), which has been the product default since the GPU port (2026-10-10).
        // RT-11 (P12-02, rule 3): the 3b closed-shell shrink made this room its drawn 3.0 m (11 -> 12 air layers; the volume this check counts
        // is now 133.8 m3, was 108.2; the heater 1 -> 2 layers) and 19a read 1.30 K. The band comes from a stated analytic reference, so it was
        // recomputed for the corrected room BEFORE comparing: audits/2026-10-12/phase1/P14/t3_filling_box.py `transient` (Morton-Taylor-Turner
        // top-hat plume in a Baines-Turner/Germeles filling box, kappa = 0, 100 ambient cells, band-averaged +-0.268 m as sampled here) with
        // H = 3.0 m, V = 133.8 m3 (S = V/H = 44.6 m2), P = 500 W, T_ref 22 C, t = 3 turnovers = 3 V/(0.156 B^(1/3) H^(5/3)) = 1720 s (also
        // 1700, 1749 = the averaging window's mid-point, 1780), top-hat alpha 0.10-0.13 and virtual origin 0-1 m below the floor (the 1 m x 1 m x 0.5 m
        // area heater): 1.79-8.52 K (alpha 0.10 / zv 0: 8.34-8.52; alpha 0.13 / zv 1.0: 1.79-1.80). The band is that envelope, 1.8-8.6 K. The
        // reference hardly moved from the old 2-9 K (which came from the same family at H = 3 m nominal and 1420 s): it already assumed H = 3 m,
        // and the room the solver simulates now matches it.
        ReportTry("19a heat ambient dT(z=2.5 - z=0.5) in [1.8, 8.6] K at t ~ 1750 s (area-source filling box)", () =>
        {
            if (!heatTrace.HasValue) throw new NotImplementedException("19-setup did not produce a trace");
            var (field, room, simSeconds, _, fillSeconds, _, _, _, _, _) = heatTrace.Value;
            if (field.T.Length == 0) throw new NotImplementedException("field.T empty, thermal not wired yet");
            float tHigh = MeanAtHeight(field, 2.5f, field.T, room);
            float tLow = MeanAtHeight(field, 0.5f, field.T, room);
            bool ok = !float.IsNaN(tHigh) && !float.IsNaN(tLow) && (tHigh - tLow) >= 1.8f && (tHigh - tLow) <= 8.6f;
            return (ok, $"T(2.5m)={tHigh:F2} T(0.5m)={tLow:F2} dT={tHigh - tLow:F2} SimSeconds={simSeconds:F1} " +
                $"(= {simSeconds / fillSeconds:F1} filling-box turnovers of {fillSeconds:F0}s)");
        });
        CheckScene("19b heat room MeanTemperature>22", "heat", r =>
        {
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.MeanTemperature)) throw new NotImplementedException("MeanTemperature NaN, Analysis.Finish not wired yet");
            return (room.MeanTemperature > 22f, $"MeanTemperature={room.MeanTemperature:F2}");
        });
        // Sealed-room energy balance. The "heat" scene has both windows closed and no mechanical source,
        // so every one of the 500 W lands in the room air and nothing leaves through an opening: the bulk
        // rise must be P*t/(rho*cp*V) with no outflow term. ToField's T is a TIME AVERAGE over the
        // averaging phase (steps _warmupSteps.._totalSteps), not the final instant, so the matching
        // reference time is the mid-point of that window, not SimSeconds -- with a linear-in-t rise the
        // mean of T over [t0, t1] equals T at (t0+t1)/2. The residual error left after that is real heat
        // that went into the enclosing solids (the D3Q7 walls are adiabatic at the fluid link but the
        // ground/roof slab cells still hold T), so the band is 20%, not a few percent.
        // Sealed-room energy balance. The "heat" scene has both windows closed and no mechanical source, and
        // the D3Q7 solid links are adiabatic (plain bounce-back), so every one of the 500 W stays in the room
        // air: the bulk rise must be P*t/(rho*cp*V) with no outflow term. The T of ToField is a TIME AVERAGE
        // over the averaging phase (WarmupSteps..Steps), not the final instant, so the matching reference
        // time is the mid-point of that window -- for a linear-in-t rise the mean of T over [t0, t1] is T at
        // (t0+t1)/2. The 20% band covers the inlet ramp over the first RampSteps and the heat held by the
        // enclosing slab cells (which carry T but are not part of the room volume).
        ReportTry("19c heat sealed-room energy balance: room meanT rise vs P*t/(rho*cp*V) within 20%", () =>
        {
            if (!heatTrace.HasValue) throw new NotImplementedException("19-setup did not produce a trace");
            var (_, _, simSeconds, tEff, _, totalWatts, volume, meanT, _, _) = heatTrace.Value;
            if (float.IsNaN(meanT)) return (false, "19-setup produced no room mean T (field.T empty?)");
            const float rhoCp = 1.2f * 1005f;
            const float indoorStart = 22f;  // heat scene: Env.IndoorTemperature == Env.OutdoorTemperature == 22
            float measuredRise = meanT - indoorStart;
            float expectedRise = volume > 0 ? totalWatts * tEff / (rhoCp * volume) : float.NaN;
            float err = expectedRise > 0 ? MathF.Abs(measuredRise - expectedRise) / expectedRise : float.NaN;
            bool ok = !float.IsNaN(err) && err <= 0.20f;
            return (ok, $"measuredRise={measuredRise:F3}K expectedRise={expectedRise:F3}K " +
                $"(P*t_mid/(rhoCp*V), P={totalWatts:F0}W t_mid={tEff:F1}s of {simSeconds:F1}s V={volume:F1}m3) err={err * 100:F1}%");
        });
        // 19d: regression for the wall mass correction (LbmSolver.GatherBoundary f0 correction). Standalone
        // as `-- --quiescent` too, since check 19 only runs inside the full sandbox.
        ReportTry("19d quiescent sealed room stays uniform: |meanT(z=2.5)-meanT(z=0.5)|<=0.05K and mean speed<=0.01m/s after 8000 steps",
            Quiescent19d);
        // 19e: the mass-consistent transport of ThermalScheme.Hybrid, forced on here whatever the product default is.
        ReportTry("19e hybrid thermal: uniform T stays uniform to 1e-4 K (19d room, windy scene) and a sealed heated room conserves energy to 0.1 %",
            Hybrid19e);
        ReportTry("19f hybrid thermal GPU = CPU: max|dT| <= 1e-3 K and energy within 0.01 % (sealed heat, cross+heat, supply+heat)",
            HybridGpuCpu19f);

        // 20. co2
        CheckScene("20a co2 MeanCo2-420 within [0.4,2.5]x expected", "co2", r =>
        {
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.MeanCo2)) throw new NotImplementedException("MeanCo2 NaN, not wired yet");
            var east = r.Openings.FirstOrDefault(o => o.Opening.Id == "east");
            float qEast = east != null ? MathF.Abs(east.FlowRate) : float.NaN;
            if (float.IsNaN(qEast) || qEast <= 0) return (false, $"invalid Qeast={qEast}");
            float expected = 8f * 0.0052e-3f / qEast * 1e6f;
            float dC = room.MeanCo2 - 420f;
            bool ok = dC >= 0.4f * expected && dC <= 2.5f * expected;
            return (ok, $"MeanCo2={room.MeanCo2:F2} dC={dC:F2} expected~{expected:F2}");
        });
        CheckScene("20b co2 MeanAge within [0.3,3.0]x nominal (Volume/Inflow)", "co2", r =>
        {
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.MeanAge)) throw new NotImplementedException("MeanAge NaN, not wired yet");
            if (room.Inflow <= 0) return (false, $"Inflow={room.Inflow}");
            float nominal = room.VoxelVolume / room.Inflow;   // age is transport over the voxel control volume
            // Wide band: this is a cross-flow room (jet straight through, dead corners), so short-circuiting can
            // push local mean age well below or above the nominal time, not just the near-1x of well-mixed rooms.
            bool ok = room.MeanAge >= 0.3f * nominal && room.MeanAge <= 3.0f * nominal;
            return (ok, $"MeanAge={room.MeanAge:F1} nominal={nominal:F1}");
        });
        CheckScene("20c co2 AirChangeEffectiveness finite in [0.2,2.5]", "co2", r =>
        {
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.AirChangeEffectiveness)) throw new NotImplementedException("AirChangeEffectiveness NaN, not wired yet");
            // ASHRAE 129: short-circuiting (this jet-through cross-flow room) can drop well below the 1.0 of
            // perfect mixing, while displacement-like pockets push it up; up to 2 is typical, so give headroom.
            bool ok = !float.IsInfinity(room.AirChangeEffectiveness) && room.AirChangeEffectiveness >= 0.2f && room.AirChangeEffectiveness <= 2.5f;
            return (ok, $"ACE={room.AirChangeEffectiveness:F2}");
        });
        // 20d is the unit test behind the 20a fix, and the one property the whole scalar discretization rests
        // on: with no sources anywhere, a field that is uniform at the outdoor value is an EXACT solution of
        // the advection-diffusion equations (every cell's neighbour weights sum to 1), so the solver must
        // return it unchanged. It did not: the continuity-defect correction only restored a_P = sum(a_Q) in
        // net-inflow cells, so in every net-outflow cell a_P was too big and CO2 decayed away from its own
        // boundary value -- which is exactly what 20a measured (229.95 ppm against a 420 ppm outdoor value).
        // Run on the already-solved cross velocity field, whose model has no occupants and no contaminant.
        ScalarFields? crossScalarsCache = null;   // 20d and 20e share one source-free solve on the cross field
        ScalarFields CrossScalars(SimulationResult cr) =>
            crossScalarsCache ??= ScalarSolver.Solve(cr.Model, cr.Field, new RunOptions());

        if (results.TryGetValue("cross", out var crossForScalar))
            ReportTry("20d scalar uniform-field preservation: no sources -> CO2 stays 420+-0.5 ppm (cross)", () =>
            {
                var model = crossForScalar.Model;
                if (model.Rooms.Count == 0) return (false, "cross model has no rooms");
                if (model.Rooms.Any(rm => rm.Occupants > 0) || model.Contaminants.Any(c => c.Enabled))
                    return (false, "cross model is not source-free, test is invalid");
                var scalars = CrossScalars(crossForScalar);
                if (scalars.Co2.Length != crossForScalar.Field.Grid.Count)
                    throw new NotImplementedException("ScalarFields.Co2 empty, not wired yet");
                var g = crossForScalar.Field.Grid;
                var room = model.Rooms[0];
                float lo = float.MaxValue, hi = float.MinValue;
                int cells = 0;
                for (int zi = 0; zi < g.Nz; zi++)
                for (int yi = 0; yi < g.Ny; yi++)
                for (int xi = 0; xi < g.Nx; xi++)
                {
                    int idx = g.Index(xi, yi, zi);
                    if (g.Cell[idx] != VoxelGrid.Air) continue;
                    var c = g.CellCenter(xi, yi, zi);
                    if (c.Z < room.ZMin || c.Z > room.ZMax) continue;
                    if (!InsideRoomLoops(room.Loops, new Vector2(c.X, c.Y))) continue;
                    float v = scalars.Co2[idx];
                    if (float.IsNaN(v)) return (false, $"NaN CO2 in an air cell at {c}");
                    lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); cells++;
                }
                if (cells == 0) return (false, "no air cells inside the room footprint");
                bool ok = lo >= 419.5f && hi <= 420.5f;
                return (ok, $"min={lo:F4} max={hi:F4} ppm over {cells} indoor air cells (outdoor 420)");
            });
        else Report("20d scalar uniform-field preservation", Verdict.Fail, "cross scene did not run");

        // 20e is the identity that decides whether a mean age well above the nominal time is real
        // short-circuiting or a broken age operator. Integrating the age equation over the solved region gives
        // (outward age flux) - (inward age flux) = |region|, for ANY flow pattern, so the flow-weighted mean age
        // of the air leaving the exhaust is exactly tau_n = V/Q -- no matter how the age is distributed inside.
        // (The VOLUME mean age is not bounded by tau_n: it equals tau_n only for perfect mixing and can run
        // several times higher when part of the room is stagnant, which is what the air-exchange efficiency
        // eps = tau_n / (2*<tau>) measures -- printed here, ASHRAE/Sandberg definition, 100% = piston flow,
        // 50% = perfect mixing.) Fluxes are integrated over the opening's own carved throat cells on the wall
        // plane, from the same time-averaged velocity field the ledger and ScalarSolver use -- NOT from
        // OpeningFlow.FlowRate, which multiplies a box-mean speed by the nominal area (see 18-setup).
        if (results.TryGetValue("cross", out var crossForAge))
            ReportTry("20e age identity: flow-weighted exhaust age == nominal time V/Q within 10% (cross)", () =>
            {
                var model = crossForAge.Model;
                var field = crossForAge.Field;
                var g = field.Grid;
                var scalars = CrossScalars(crossForAge);
                if (scalars.Age.Length != g.Count) throw new NotImplementedException("ScalarFields.Age empty, not wired yet");

                // The control volume is the ROOM: its boundary is solid wall everywhere except the two window
                // throats, so the two throat integrals below are its whole surface flux. NOT ScalarSolver's
                // solved region -- Voxelizer.PlanBounds is the bounding box of every obstacle vertex, and the
                // ground slab it takes in extends a metre past the walls, so the solved region also contains a
                // 1 m ring of outdoor air (181.6 m3 against the room's 108.6) whose boundary is the open wind
                // tunnel. The identity over THAT region would have to balance the free stream blowing through
                // the ring, which no pair of window fluxes can. V and <tau> are both accumulated over the same
                // cell set here so they cannot disagree about what "the room" is.
                var room0 = model.Rooms[0];
                double vRoom = 0, ageSum = 0; int ageCells = 0;
                float cellVol = g.Dx * g.Dx * g.Dx;
                for (int zi = 0; zi < g.Nz; zi++)
                for (int yi = 0; yi < g.Ny; yi++)
                for (int xi = 0; xi < g.Nx; xi++)
                {
                    int i = g.Index(xi, yi, zi);
                    if (g.Cell[i] != VoxelGrid.Air) continue;
                    var c = g.CellCenter(xi, yi, zi);
                    if (c.Z < room0.ZMin || c.Z > room0.ZMax) continue;
                    if (!InsideRoomLoops(room0.Loops, new Vector2(c.X, c.Y))) continue;
                    if (float.IsNaN(scalars.Age[i])) continue;
                    vRoom += cellVol;
                    ageSum += scalars.Age[i]; ageCells++;
                }

                // Flux-weighted age across one opening's throat: the wall-plane cells the voxelizer carved for
                // it (Analysis.ThroatCells -- the same set OpeningFlow.FlowRate integrates), split by the sign of
                // the velocity component along the opening's normal.
                (double QOut, double AgeOut, double QIn, double AgeIn, int Cells) Throat(Opening o)
                {
                    var n = Vector3.Normalize(new Vector3(o.Normal.X, o.Normal.Y, 0));
                    double qOut = 0, aOut = 0, qIn = 0, aIn = 0; int cells = 0;
                    foreach (int i in Analysis.ThroatCells(g, o))
                    {
                        float u = field.Ux[i] * n.X + field.Uy[i] * n.Y + field.Uz[i] * n.Z;   // + = leaving the building
                        float a = scalars.Age[i];
                        if (float.IsNaN(a)) continue;
                        cells++;
                        if (u > 0) { qOut += u; aOut += u * a; } else { qIn += -u; aIn += -u * a; }
                    }
                    float faceArea = g.Dx * g.Dx;
                    return (qOut * faceArea, aOut / Math.Max(qOut, 1e-12), qIn * faceArea, aIn / Math.Max(qIn, 1e-12), cells);
                }

                var east = model.Openings.FirstOrDefault(o => o.Id == "east" && o.IsOpen);
                var west = model.Openings.FirstOrDefault(o => o.Id == "west" && o.IsOpen);
                if (east == null || west == null) return (false, "cross scene has no open east/west opening");
                var ex = Throat(east);
                var inl = Throat(west);
                if (ex.Cells == 0 || ex.QOut <= 0) return (false, $"no outward flux at the east throat (cells={ex.Cells})");

                double tauN = vRoom / ex.QOut;
                double err = Math.Abs(ex.AgeOut - tauN) / tauN;
                double meanAge = ageCells > 0 ? ageSum / ageCells : double.NaN;
                double eps = tauN / (2.0 * meanAge);
                // Two-sided form of the same identity: the inlet throat cells sit inside the solved region and
                // carry a small age of their own (outdoor air upwind of the window is solved too, see above),
                // so subtracting the inflow's age flux is the sharper statement -- it should be exactly 1.
                double balance = (ex.QOut * ex.AgeOut - inl.QIn * inl.AgeIn) / vRoom;
                bool ok = err <= 0.10;
                return (ok, $"tau_exhaust={ex.AgeOut:F1}s tau_n=V/Q={tauN:F1}s err={err * 100:F1}% " +
                    $"(V={vRoom:F1}m3 over {ageCells} room air cells, RoomResult.VoxelVolume={crossForAge.Rooms[0].VoxelVolume:F1}m3," +
                    $"Qout={ex.QOut:F4}m3/s over {ex.Cells} east throat cells) " +
                    $"inlet Qin={inl.QIn:F4}m3/s age_in={inl.AgeIn:F1}s -> (Qout*tau_out - Qin*tau_in)/V=" +
                    $"{balance:F3} (ideal 1.000)  " +
                    $"volume-mean age={meanAge:F1}s, air-exchange efficiency eps=tau_n/(2*mean)={eps * 100:F0}% " +
                    $"(50% = perfect mixing; RoomResult has no field for eps, printed only)");
            });
        else Report("20e age identity", Verdict.Fail, "cross scene did not run");

        // 21. co2-closed: sealed room robustness
        CheckScene("21 co2-closed completes<60s, MeanCo2 finite & capped, warnings mention no steady state", "co2-closed", r =>
        {
            double elapsed = sceneElapsed.TryGetValue("co2-closed", out var t) ? t : double.NaN;
            bool timeOk = !double.IsNaN(elapsed) && elapsed < 60.0;
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.MeanCo2)) throw new NotImplementedException("MeanCo2 NaN, not wired yet");
            bool finiteOk = !float.IsInfinity(room.MeanCo2) && room.MeanCo2 <= 50_000f;
            var notes = string.Join(" | ", r.Field.Grid.Warnings.Concat(r.Ledger.Notes));
            bool mentionsSealed = notes.Contains("no steady state", StringComparison.OrdinalIgnoreCase);
            return (timeOk && finiteOk && mentionsSealed, $"elapsed={elapsed:F1}s MeanCo2={room.MeanCo2:F2} mentionsSealed={mentionsSealed} notes=\"{notes}\"");
        });

        // 22. comfort
        CheckScene("22a comfort ComfortFraction & DraughtFraction in [0,1]", "comfort", r =>
        {
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            if (float.IsNaN(room.ComfortFraction) || float.IsNaN(room.DraughtFraction)) throw new NotImplementedException("fractions NaN, not wired yet");
            bool ok = room.ComfortFraction >= 0f && room.ComfortFraction <= 1f && room.DraughtFraction >= 0f && room.DraughtFraction <= 1f;
            return (ok, $"ComfortFraction={room.ComfortFraction:F3} DraughtFraction={room.DraughtFraction:F3}");
        });
        CheckScene("22b comfort >=50% occupied-zone cells finite PMV", "comfort", r =>
        {
            if (r.Comfort.Pmv.Length == 0) throw new NotImplementedException("ComfortFields empty, not wired yet");
            var room = r.Rooms.FirstOrDefault();
            if (room == null) return (false, "no RoomResult (room has 0 air cells)");
            var (total, finite) = CountFinitePmv(r.Field, r.Comfort, room.Room, room.Room.ZMin + 0.1f, room.Room.ZMin + 1.8f);
            if (total == 0) return (false, "no occupied-zone air cells found");
            float frac = (float)finite / total;
            return (frac >= 0.5f, $"finite={finite}/{total} ({frac * 100:F0}%)");
        });
        CheckScene("22c comfort PPD in [5,100] where finite", "comfort", r =>
        {
            if (r.Comfort.Ppd.Length == 0) throw new NotImplementedException("Ppd empty, not wired yet");
            bool allOk = true; int checkedCount = 0;
            foreach (var v in r.Comfort.Ppd)
            {
                if (float.IsNaN(v)) continue;
                checkedCount++;
                if (v < 5f || v > 100f) { allOk = false; break; }
            }
            return (allOk && checkedCount > 0, $"checked={checkedCount} allWithinRange={allOk}");
        });
        CheckScene("22d comfort Probe(4,3,1.1) Room='Room', finite Speed/Temperature/Pmv", "comfort", r =>
        {
            var p = Analysis.Probe(r, new Vector3(4, 3, 1.1f));
            bool ok = p.Room == "Room" && !float.IsNaN(p.Speed) && !float.IsInfinity(p.Speed)
                && !float.IsNaN(p.Temperature) && !float.IsInfinity(p.Temperature)
                && !float.IsNaN(p.Pmv) && !float.IsInfinity(p.Pmv);
            return (ok, $"Room={p.Room} Speed={p.Speed:F3} Temperature={p.Temperature:F2} Pmv={p.Pmv:F2}");
        });

        // 23. ledger (existing cross/supply results from the basic run above). The ledger sums the exterior
        // openings' FlowRate, now throat integrals: 23a's residual fell 2.1 % -> 0.5 %. 10 % is the ledger's own
        // IsPhysical threshold, unchanged.
        if (results.TryGetValue("cross", out var crossForLedger))
        {
            var ledger = crossForLedger.Ledger;
            if (ledger.In == 0 && ledger.Out == 0)
                Report("23a ledger cross IsPhysical & Residual<=0.10", Verdict.Wait, "Ledger empty (Analysis.Finish not wired yet)");
            else
            {
                bool ok = ledger.IsPhysical && ledger.Residual <= 0.10f;
                Report("23a ledger cross IsPhysical & Residual<=0.10", ok ? Verdict.Pass : Verdict.Fail,
                    $"IsPhysical={ledger.IsPhysical} Residual={ledger.Residual:F3} In={ledger.In:F3} Out={ledger.Out:F3}");
            }
        }
        else Report("23a ledger cross", Verdict.Fail, "cross scene did not run");

        if (results.TryGetValue("supply", out var supplyForLedger))
        {
            var ledger = supplyForLedger.Ledger;
            if (ledger.In == 0 && ledger.Out == 0)
                Report("23b ledger supply In~=0.139+window inflow", Verdict.Wait, "Ledger empty (Analysis.Finish not wired yet)");
            else
            {
                float windowInflow = supplyForLedger.Openings.Where(o => o.FlowRate > 0).Sum(o => o.FlowRate);
                float expected = 0.139f + windowInflow;
                bool ok = expected > 0 && Math.Abs(ledger.In - expected) <= 0.25f * expected;
                Report("23b ledger supply In~=0.139+window inflow", ok ? Verdict.Pass : Verdict.Fail,
                    $"Ledger.In={ledger.In:F3} expected~{expected:F3} (0.139+windows={windowInflow:F3})");
            }
        }
        else Report("23b ledger supply", Verdict.Fail, "supply scene did not run");

        // 24. anim: snapshots + GIF
        CheckScene("24a anim 6 snapshots, finite speeds, max within 0.3-3x averaged max", "anim", r =>
        {
            if (r.Field.Snapshots.Count == 0) throw new NotImplementedException("Snapshots empty, SnapshotCount not wired yet");
            if (r.Field.Snapshots.Count != 6) return (false, $"snapshot count={r.Field.Snapshots.Count}, expected 6");
            var g = r.Field.Grid;
            float avgMax = 0f;
            for (int i = 0; i < g.Count; i++)
            {
                if (g.Cell[i] == VoxelGrid.Solid) continue;
                float s = MathF.Sqrt(r.Field.Ux[i] * r.Field.Ux[i] + r.Field.Uy[i] * r.Field.Uy[i] + r.Field.Uz[i] * r.Field.Uz[i]);
                if (s > avgMax) avgMax = s;
            }
            bool ok = true;
            var snapMaxes = new List<float>();
            foreach (var snap in r.Field.Snapshots)
            {
                float mx = 0f; bool finiteOk = true;
                for (int i = 0; i < snap.Ux.Length; i++)
                {
                    float ux = (float)snap.Ux[i], uy = (float)snap.Uy[i], uz = (float)snap.Uz[i];
                    if (float.IsNaN(ux) || float.IsInfinity(ux) || float.IsNaN(uy) || float.IsInfinity(uy) || float.IsNaN(uz) || float.IsInfinity(uz))
                    { finiteOk = false; break; }
                    float s = MathF.Sqrt(ux * ux + uy * uy + uz * uz);
                    if (s > mx) mx = s;
                }
                snapMaxes.Add(mx);
                if (!finiteOk) ok = false;
                if (!(avgMax > 0 && mx >= 0.3f * avgMax && mx <= 3f * avgMax)) ok = false;
            }
            return (ok, $"count={r.Field.Snapshots.Count} avgMax={avgMax:F3} snapMaxes=[{string.Join(",", snapMaxes.Select(v => v.ToString("F2")))}]");
        });
        ReportTry("24b anim Renderer.Frames=6, SaveGif writes >10KB", () =>
        {
            if (!advResults.TryGetValue("anim", out var animR))
            {
                if (advWait.Contains("anim")) throw new NotImplementedException("anim scene not implemented yet");
                return (false, "anim scene did not run");
            }
            var planOptions = new PlanOptions { Z = 1.1f, Title = "anim" };
            var frames = Renderer.Frames(animR, planOptions);
            string gifPath = Path.Combine(outDir, "cross-anim.gif");
            Renderer.SaveGif(frames, gifPath, 150);
            long size = File.Exists(gifPath) ? new FileInfo(gifPath).Length : 0;
            bool ok = frames.Count == 6 && size > 10_240;
            return (ok, $"frameCount={frames.Count} gifSize={size}B path={gifPath}");
        });

        // 25. iso surfaces & vorticity (on the existing cross result)
        if (results.TryGetValue("cross", out var crossForIso))
        {
            ReportTry("25a iso non-empty mesh, vertices in domain, >80% within footprint+2m", () =>
            {
                var g = crossForIso.Field.Grid;
                var speed = new float[g.Count];
                for (int i = 0; i < g.Count; i++)
                    speed[i] = MathF.Sqrt(crossForIso.Field.Ux[i] * crossForIso.Field.Ux[i]
                        + crossForIso.Field.Uy[i] * crossForIso.Field.Uy[i] + crossForIso.Field.Uz[i] * crossForIso.Field.Uz[i]);
                var (minB, maxB) = Voxelizer.PlanBounds(crossForIso.Model);
                // Without a filter, |u|>1 m/s is true almost everywhere in the ambient 3 m/s exterior wind
                // field too, not just around the building -- restrict extraction to the footprint + 2 m so
                // the surface reflects indoor/near-facade flow, matching what "footprint" below measures.
                bool Include(int idx)
                {
                    int x = idx % g.Nx, y = idx / g.Nx % g.Ny, z = idx / (g.Nx * g.Ny);
                    var c = g.CellCenter(x, y, z);
                    return c.X >= minB.X - 2f && c.X <= maxB.X + 2f && c.Y >= minB.Y - 2f && c.Y <= maxB.Y + 2f;
                }
                var mesh = IsoSurface.Extract(g, speed, 1.0f, Include);
                if (mesh.Vertices.Length == 0) return (false, "empty mesh");
                int insideDomain = 0, insideFootprint = 0;
                foreach (var v in mesh.Vertices)
                {
                    bool inDomain = v.X >= g.Origin.X && v.X <= g.Origin.X + g.Nx * g.Dx
                        && v.Y >= g.Origin.Y && v.Y <= g.Origin.Y + g.Ny * g.Dx
                        && v.Z >= g.Origin.Z && v.Z <= g.Origin.Z + g.Nz * g.Dx;
                    if (inDomain) insideDomain++;
                    if (v.X >= minB.X - 2f && v.X <= maxB.X + 2f && v.Y >= minB.Y - 2f && v.Y <= maxB.Y + 2f) insideFootprint++;
                }
                bool domainOk = insideDomain == mesh.Vertices.Length;
                bool footprintOk = insideFootprint > 0.8 * mesh.Vertices.Length;
                return (domainOk && footprintOk,
                    $"vertices={mesh.Vertices.Length} insideDomain={insideDomain} insideFootprint={insideFootprint}");
            });
            ReportTry("25b iso Vorticity.Q finite in air cells, some positive", () =>
            {
                var q = Vorticity.Q(crossForIso.Field);
                var g = crossForIso.Field.Grid;
                bool allFiniteInAir = true; bool anyPositive = false; int airChecked = 0;
                for (int i = 0; i < g.Count; i++)
                {
                    if (g.Cell[i] == VoxelGrid.Solid) continue;
                    if (i >= q.Length || float.IsNaN(q[i]) || float.IsInfinity(q[i])) { allFiniteInAir = false; break; }
                    airChecked++;
                    if (q[i] > 0) anyPositive = true;
                }
                return (allFiniteInAir && anyPositive && airChecked > 0, $"airCellsChecked={airChecked} allFinite={allFiniteInAir} anyPositive={anyPositive}");
            });
        }
        else
        {
            Report("25a iso", Verdict.Fail, "cross scene did not run");
            Report("25b iso Vorticity.Q", Verdict.Fail, "cross scene did not run");
        }

        // 26. opening sweep (fresh cross scene, TargetCells=60_000)
        ReportTry("26 sweep OpeningSweep.Run cross: 4 entries, all-open first, all-closed last, top has Result", () =>
        {
            var (floor, wind) = Scene("cross");
            var sweepOptions = new RunOptions { TargetCells = 60_000, Backend = Backend.Cpu };
            var ids = floor.Openings.Select(o => o.Id).ToList();
            var entries = OpeningSweep.Run(floor, wind, ids, sweepOptions);
            bool countOk = entries.Count == 4;
            bool allOpenFirst = entries.Count > 0 && entries[0].Open.All(b => b);
            bool allClosedLast = entries.Count > 0 && entries[^1].Open.All(b => !b);
            bool topHasResult = entries.Count > 0 && entries[0].Result != null;
            return (countOk && allOpenFirst && allClosedLast && topHasResult,
                $"count={entries.Count} firstOpen=[{(entries.Count > 0 ? string.Join(",", entries[0].Open) : "")}] " +
                $"lastOpen=[{(entries.Count > 0 ? string.Join(",", entries[^1].Open) : "")}] topHasResult={topHasResult}");
        });

        // 27. annual (synthetic EPW -> wind rose -> Annual.Run, cross, TargetCells=60_000, threshold 4 ACH)
        ReportTry("27 annual: EPW written, WindRose 2 bins+calm, Annual.Run VentilatedShare/CalmHours/bestDirection", () =>
        {
            string epwPath = Path.Combine(outDir, "synthetic.epw");
            WriteSyntheticEpw(epwPath);
            bool fileOk = File.Exists(epwPath) && new FileInfo(epwPath).Length > 0;

            var epw = Epw.Load(epwPath);
            bool rowsOk = epw.Hours.Count == 8760;
            var bins = WindRose.Build(epw);
            var nonCalm = bins.Where(b => b.Sector >= 0).ToList();
            var calm = bins.Where(b => b.Sector < 0).ToList();
            bool binsOk = nonCalm.Count == 2 && calm.Count == 1;

            var (floor, _) = Scene("cross");
            var annualOptions = new RunOptions { TargetCells = 60_000, Backend = Backend.Cpu };
            // FROM-direction compass degrees (0=N, 90=E) -> the model-axes vector the wind blows TOWARD
            // (+X = East, +Y = North, matching the west/east wall layout of the built scenes).
            Vector2 WindOf(WindBin b)
            {
                float rad = (b.DirectionDeg + 180f) * MathF.PI / 180f;
                return new Vector2(MathF.Sin(rad), MathF.Cos(rad)) * b.SpeedMid;
            }
            var annual = Annual.Run(floor, bins, WindOf, 4f, annualOptions);
            bool calmHoursOk = annual.CalmHours == 760;
            var roomResult = annual.Rooms.FirstOrDefault();
            bool shareOk = roomResult != null && roomResult.VentilatedShare >= 0f && roomResult.VentilatedShare <= 1f;
            bool bestDirReported = roomResult != null && !float.IsNaN(roomResult.BestDirectionDeg);

            bool pass = fileOk && rowsOk && binsOk && calmHoursOk && shareOk && bestDirReported;
            return (pass, $"fileOk={fileOk} rows={epw.Hours.Count} nonCalmBins={nonCalm.Count} calmBins={calm.Count} " +
                $"CalmHours={annual.CalmHours} VentilatedShare={(roomResult?.VentilatedShare ?? float.NaN):F3} BestDir={(roomResult?.BestDirectionDeg ?? float.NaN):F1}");
        });

        // 28. report PDF (comfort result, two images)
        ReportTry("28 report ReportBuilder.Build comfort (plan speed + plan PMV) -> out/report.pdf >20KB", () =>
        {
            if (!advResults.TryGetValue("comfort", out var comfortR))
            {
                if (advWait.Contains("comfort")) throw new NotImplementedException("comfort scene not implemented yet");
                return (false, "comfort scene did not run");
            }
            var speedImg = Renderer.Plan(comfortR, new PlanOptions { Z = 1.1f, Field = FieldKind.Speed, Title = "comfort - speed" });
            var pmvImg = Renderer.Plan(comfortR, new PlanOptions { Z = 1.1f, Field = FieldKind.Pmv, Title = "comfort - pmv" });
            var images = new (string Title, BitmapSource Image)[] { ("Speed plan", speedImg), ("PMV plan", pmvImg) };
            string pdfPath = Path.Combine(outDir, "report.pdf");
            ReportBuilder.Build(comfortR, images, pdfPath, new ReportOptions { ProjectName = "WindFlow Sandbox", Scenario = "comfort" });
            long size = File.Exists(pdfPath) ? new FileInfo(pdfPath).Length : 0;
            return (size > 20_480, $"path={pdfPath} size={size}B");
        });

        // 29. field renders + difference plan
        ReportTry("29 fields: plan Temperature/Co2/Age/Pmv/DraughtRate + Renderer.Difference(cross,comfort) -> PNGs", () =>
        {
            if (!advResults.TryGetValue("comfort", out var comfortR))
            {
                if (advWait.Contains("comfort")) throw new NotImplementedException("comfort scene not implemented yet");
                return (false, "comfort scene did not run");
            }
            if (!results.TryGetValue("cross", out var crossR))
                return (false, "cross scene did not run");

            var kinds = new (string Name, FieldKind Kind)[]
            {
                ("Temperature", FieldKind.Temperature), ("Co2", FieldKind.Co2), ("Age", FieldKind.Age),
                ("Pmv", FieldKind.Pmv), ("DraughtRate", FieldKind.DraughtRate),
            };
            var saved = new List<string>();
            foreach (var (kindName, kind) in kinds)
            {
                var img = Renderer.Plan(comfortR, new PlanOptions { Z = 1.1f, Field = kind, Title = $"comfort - {kindName}" });
                string path = Path.Combine(outDir, $"comfort-{kindName.ToLowerInvariant()}-plan.png");
                Renderer.SavePng(img, path);
                saved.Add(path);
            }

            var diffImg = Renderer.Difference(crossR, comfortR, new PlanOptions { Z = 1.1f, Title = "cross vs comfort - difference" });
            string diffPath = Path.Combine(outDir, "cross-vs-comfort-difference-plan.png");
            Renderer.SavePng(diffImg, diffPath);
            saved.Add(diffPath);

            bool allExist = saved.All(p => File.Exists(p) && new FileInfo(p).Length > 0);
            return (allExist, $"saved {saved.Count} PNGs: {string.Join(", ", saved.Select(Path.GetFileName))}");
        });

        // 30. live session (LbmSolver.Start/Step/ReadVelocity/ToField/UpdateCells) on the cross scene:
        // chunked stepping must land on the monolithic Solve's answer, and closing the upwind window
        // mid-run must kill the inflow through it without diverging.
        ReportTry("30 live session: chunked Step == Solve (Q within 1%), UpdateCells closes the west window", () =>
        {
            const int cells = 400_000, chunk = 200, afterCloseSteps = 2000;
            var (floor, wind) = Scene("cross");
            var opts = new RunOptions { TargetCells = cells };   // Auto backend: GPU when there is one
            var grid = Voxelizer.Build(floor, cells);

            var mono = LbmSolver.Solve(grid, floor, wind, opts);
            double monoMlups = mono.Seconds > 0 ? (double)grid.Count * mono.Steps / mono.Seconds / 1e6 : 0;
            float qMono = Math.Abs(Analysis.Analyze(floor, mono, wind, opts).Openings.First(o => o.Opening.Id == "east").FlowRate);

            int count = grid.Count;
            var ux = new float[count]; var uy = new float[count]; var uz = new float[count];
            using var session = LbmSolver.Start(grid, floor, wind, opts);

            // Flux (m3/s) through the vertical plane one cell inside the west wall, over the west window's
            // own footprint, straight from the instantaneous velocity -- those cells stay air either way,
            // so this measures the through-flow, not just "the opening cells went solid".
            float WestInflow(VoxelGrid g)
            {
                int xi = Math.Clamp((int)MathF.Floor((0.5f - g.Origin.X) / g.Dx), 0, g.Nx - 1);
                float q = 0f;
                for (int zi = 0; zi < g.Nz; zi++)
                for (int yi = 0; yi < g.Ny; yi++)
                {
                    var c = g.CellCenter(xi, yi, zi);
                    if (MathF.Abs(c.Y - 3f) > 0.6f || MathF.Abs(c.Z - 1.5f) > 0.6f) continue;
                    int idx = g.Index(xi, yi, zi);
                    if (g.Cell[idx] != 0) continue;
                    q += ux[idx];
                }
                return q * g.Dx * g.Dx;
            }

            int reads = 0; double readMs = 0;
            session.ReadVelocity(ux, uy, uz);   // warm up: the GPU read-back kernel JITs on first use
            while (session.Steps < mono.Steps)
            {
                session.Step((int)Math.Min(chunk, mono.Steps - session.Steps));
                var swRead = System.Diagnostics.Stopwatch.StartNew();
                session.ReadVelocity(ux, uy, uz);
                swRead.Stop();
                readMs += swRead.Elapsed.TotalMilliseconds; reads++;
            }
            var liveField = session.ToField();
            double liveMlups = liveField.Seconds > 0 ? (double)count * liveField.Steps / liveField.Seconds / 1e6 : 0;
            float qLive = Math.Abs(Analysis.Analyze(floor, liveField, wind, opts).Openings.First(o => o.Opening.Id == "east").FlowRate);
            bool qOk = qMono > 0 && Math.Abs(qLive - qMono) <= 0.01f * qMono;
            float inflowOpen = WestInflow(grid);

            // Close the west window and keep stepping the same session.
            var closedModel = BuildCross("cross-live-closed", false, true);
            var closedGrid = Voxelizer.Build(closedModel, cells);
            session.UpdateCells(closedGrid);
            for (int done = 0; done < afterCloseSteps; done += chunk)
            {
                session.Step(Math.Min(chunk, afterCloseSteps - done));
                session.ReadVelocity(ux, uy, uz);
            }
            float inflowClosed = WestInflow(closedGrid);
            bool closeOk = inflowOpen > 0.1f && MathF.Abs(inflowClosed) <= 0.15f * inflowOpen;

            // Cost of one ReadVelocity at the app's preview size (1.5 M cells) on the GPU.
            string bigRead = "";
            if (LbmSolver.GpuAvailable)
            {
                var bigGrid = Voxelizer.Build(floor, 1_500_000);
                using var big = LbmSolver.Start(bigGrid, floor, wind, new RunOptions { TargetCells = 1_500_000, Backend = Backend.Gpu });
                var bx = new float[bigGrid.Count]; var by = new float[bigGrid.Count]; var bz = new float[bigGrid.Count];
                big.Step(10);
                big.ReadVelocity(bx, by, bz);   // warm up: kernel JIT + staging buffers
                var swBig = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < 5; k++) big.ReadVelocity(bx, by, bz);
                swBig.Stop();
                bigRead = $" ReadVelocity({bigGrid.Count} cells, GPU)={swBig.Elapsed.TotalMilliseconds / 5:F2}ms";
            }

            return (qOk && closeOk,
                $"Qsolve={qMono:F3} Qsession={qLive:F3} steps={session.Steps} backend={session.Backend} " +
                $"MLUPSchunked={liveMlups:F1} MLUPSmonolithic={monoMlups:F1} " +
                $"westInflow open={inflowOpen:F3} closed={inflowClosed:F3} m3/s  " +
                $"ReadVelocity({count} cells)={readMs / Math.Max(1, reads):F2}ms x{reads}{bigRead} " +
                $"SimSeconds={session.SimSeconds:F1}");
        });

        // 31. Differentially heated cubic cavity (de Vahl Davis 1983 / Fusegi et al. 1991): hot/cold
        // vertical walls (Surface HeatSource, Dirichlet via anti-bounce-back), adiabatic others, no wind,
        // Pr 0.71, real air properties -- Ra reached by solving for cavity size H, not by fudging viscosity.
        //
        // RUN TO A DETECTED STEADY STATE, not to a fixed step count. The framework PlannedSteps budget is
        // tuned for building-scale rooms and is nowhere near enough for a slow buoyant cavity: the cavity
        // starts uniformly at Tcold and its near-stagnant core takes far longer to reach the steady mean
        // than the wall boundary layers take to form, so an early read sees a much larger hot-wall gradient
        // than the steady one. Measured at Ra=1e4, n=48, reading NuHot from successive averaging windows of
        // PlannedSteps each (ResetAveraging between them): 2.77, 2.28, 2.03, 1.91, 1.85, 1.82, 1.81, 1.805,
        // 1.802, 1.801, 1.800, 1.800 ... -- i.e. the previous fixed "PlannedSteps + 3x" (window 4) read 2.03
        // while the converged answer is 1.800, and the core mean T only reaches the expected (Thot+Tcold)/2
        // = 20 C at the plateau. The unconverged read is also why NuCold used to come back at half NuHot.
        // So: step one window at a time until NuHot stops moving (< CavityNuTol between windows) and assert
        // the hot/cold imbalance is small, which fails loudly on a transient instead of passing on one.
        //
        // CONVERGED ACCURACY at n=48: NuHot = 1.800 vs Fusegi 2.05 at Ra=1e4 (-12%) and 3.652 vs 4.34 at
        // Ra=1e5 (-16%), with a residual NuHot/NuCold imbalance of 6.9% that does NOT decay with more steps.
        // That residual is UNDER-RESOLUTION, not a physics error, and it was measured rather than assumed:
        // repeating the Ra=1e4 case at n=96 (1.02 M cells, 83 k steps to the same plateau) gives NuHot =
        // 1.920 and an imbalance of 3.6% -- the imbalance halves with Dx (a first-order wall/corner
        // asymmetry) and Nu rises, extrapolating to 1.96 (Richardson, 2nd order) to 2.04 (1st order) against
        // Fusegi's 2.05. The viscosity floor NuLatMin does not bind in either case (nu_lat = 0.032 at Ra=1e4
        // n=48, 0.065 at n=96, 0.010 at Ra=1e5, all >> 5e-4), so Dx really is the only knob. n=96 costs ~20
        // minutes of CPU, far too much for this harness, so the check stays at n=48 with a 20% band -- with
        // the imbalance guard doing the work the old tight band only pretended to do.
        //
        // HISTORY, because the numbers moved: these checks previously read NuHot = 2.146 (Ra=1e4) and 4.708
        // (Ra=1e5) and "passed" a 10% band against Fusegi. That was two errors cancelling. The regularized
        // collision was dropping the first moment of the forcing term, so the Boussinesq force was only
        // (3/2 - 1/(2*tau0)) of nominal = 0.663x at this cavity's tau0 = 0.597 (see swOdd in
        // LbmSolver.CollideForced); the cavity was really running at Ra = 0.66e4, and the resulting Nu
        // deficit happened to offset the +17% of the unconverged early read. Fixing the force alone moved the
        // unconverged NuHot to 2.404 -- exactly the predicted (1/0.663)^0.28 = 1.12x -- which is the third
        // independent confirmation of that factor (the other two: a sealed still room's hydrostatic gradient,
        // 0.5032x predicted 0.50299 before the fix and exact after it; and the stack scene's buoyancy head).
        (double NuHot, double NuCold, int Samples, long Steps, int Windows, string Backend, float CavityMm) RunCavity(int n, double ra, Backend backend, ThermalScheme? scheme = null)
        {
            const int CavityMaxWindows = 24;      // hard cap; Ra=1e5 needs ~17, Ra=1e4 ~10
            const double CavityNuTol = 0.002;     // relative NuHot change between windows that counts as steady
            var (grid, model, hotX, coldX, y0, y1, z0, z1, dx, cavityH, tHot, tCold) = BuildCavity(n, ra, 20f, 10f);
            // 6e addendum: the cavity resolves its boundary layer and checks the solver against benchmarks, so its walls are Resolved (fixed T)
            var opts = new RunOptions { Backend = backend, Thermal = true, SurfaceExchange = SurfaceExchange.Resolved };
            if (scheme.HasValue) opts.ThermalScheme = scheme.Value;
            using var session = LbmSolver.Start(grid, model, Vector2.Zero, opts);
            int planned = session.PlannedSteps;
            double nuHot = 0, nuCold = 0, prev = double.NaN; int samples = 0, windows = 0;
            for (int w = 0; w < CavityMaxWindows; w++)
            {
                // Each window is its own time-average, so Nu describes THIS interval, not the whole
                // build-up from the cold start (that is what ResetAveraging is for).
                session.ResetAveraging();
                session.Step(planned);
                windows++;
                (nuHot, nuCold, samples) = CavityNusselt(session.ToField(), hotX, coldX, y0, y1, z0, z1, dx, cavityH, tHot, tCold);
                if (!double.IsNaN(prev) && Math.Abs(nuHot - prev) <= CavityNuTol * Math.Abs(nuHot)) break;
                prev = nuHot;
            }
            return (nuHot, nuCold, samples, session.Steps, windows, session.Backend, cavityH * 1000f);
        }

        const int cavityN = 48;
        const double cavityBand = 0.20, cavityImbalanceMax = 0.10;
        (bool, string) CavityCheck(double ra, double target, Backend backend)
        {
            var r = RunCavity(cavityN, ra, backend);
            double err = Math.Abs(r.NuHot - target) / target;
            double imbalance = Math.Abs(r.NuHot - r.NuCold) / (0.5 * (r.NuHot + r.NuCold));
            bool ok = err <= cavityBand && imbalance <= cavityImbalanceMax;
            return (ok, $"NuHot={r.NuHot:F3} NuCold={r.NuCold:F3} target={target} err={err * 100:F1}% " +
                $"(band {cavityBand * 100:F0}%) imbalance={imbalance * 100:F1}% (max {cavityImbalanceMax * 100:F0}%) " +
                $"windows={r.Windows} steps={r.Steps} samples={r.Samples} H={r.CavityMm:F3}mm backend={r.Backend}");
        }
        ReportTry("31a cavity Ra=1e4 hot-wall mean Nu vs Fusegi 3D 2.05 (CPU, steady, within 20% at n=48)",
            () => CavityCheck(1e4, 2.05, Backend.Cpu));
        ReportTry("31b cavity Ra=1e5 hot-wall mean Nu vs Fusegi 3D 4.34 (CPU, steady, within 20% at n=48)",
            () => CavityCheck(1e5, 4.34, Backend.Cpu));
        // 31e. P14-02: the cavity's own geometry in the conduction limit (buoyancy negligible) must give Nu = 1 on both walls:
        // anything else means the heated boxes drive the adiabatic walls too. n = 24 (the n whose old artefact was 0.78 / 0.65).
        ReportTry("31e cavity geometry in (near-)pure conduction (D3Q7 Ra=10, Hybrid Ra=300, CPU, n=24): hot and cold wall Nu = 1.00 within 1% (P14-02)", () =>
        {
            // D3Q7 at Ra = 10 is pure conduction (convective rise of Nu ~1e-5). The Hybrid scheme is explicit and cannot run below Ra ~ 300 at n = 24
            // (6 kappa dt/dx^2 reaches 1), where convection adds about 0.8 % to Nu, still inside the 1 % limit.
            var lbm = RunCavity(24, 10.0, Backend.Cpu, ThermalScheme.Lbm);
            var hyb = RunCavity(24, 300.0, Backend.Cpu, ThermalScheme.Hybrid);
            bool Within(double nu) => Math.Abs(nu - 1.0) <= 0.01;
            return (Within(lbm.NuHot) && Within(lbm.NuCold) && Within(hyb.NuHot) && Within(hyb.NuCold),
                $"D3Q7 Ra=10: NuHot={lbm.NuHot:F4} NuCold={lbm.NuCold:F4} windows={lbm.Windows} | Hybrid Ra=300: NuHot={hyb.NuHot:F4} NuCold={hyb.NuCold:F4} windows={hyb.Windows} | n=24, CPU, samples={lbm.Samples}; before the fix the conduction limit read 0.776 / 0.650 (P14-02, T11)");
        });
        ReportTry("31c cavity Ra=1e4 GPU vs CPU Nu parity (within 10%, same wall BC both backends)", () =>
        {
            if (!LbmSolver.GpuAvailable) throw new NotImplementedException("no GPU on this machine");
            var cpu = RunCavity(cavityN, 1e4, Backend.Cpu);
            var gpu = RunCavity(cavityN, 1e4, Backend.Gpu);
            double err = Math.Abs(gpu.NuHot - cpu.NuHot) / cpu.NuHot;
            return (err <= 0.10, $"NuCpu={cpu.NuHot:F3} NuGpu={gpu.NuHot:F3} err={err * 100:F1}% " +
                $"windowsCpu={cpu.Windows} windowsGpu={gpu.Windows} backendCpu={cpu.Backend} backendGpu={gpu.Backend}");
        });

        // 31d. The same cube at n=160 on the GPU (about 12 min), asserted to 5 % against the 3-D cube benchmark
        // (Tric, Labrosse & Betrouni 2000: hot-wall mean Nu 2.054 / 4.337). The 2-D de Vahl Davis 1983 values
        // (2.243 / 4.519, Pr 0.71) are printed but NOT asserted: this is a 3-D cube with no-slip adiabatic side
        // walls (the thermal solver has no periodic or symmetry y-boundary), so even an exact solution sits
        // 8.4 % / 4.0 % below them. Nu converges slowly with n (Ra 1e4 / 1e5, GPU): n=48 1.806 / 3.818, n=96
        // 1.926 / 4.056, n=128 1.961 / 4.124, n=160 1.974 / 4.168 (-3.9 % / -3.9 %); n=128 is only -4.5 % / -4.9 %,
        // too close to the band (these Nu figures predate P14-02: the cavity's side walls were partly held at T_hot / T_cold, which lowered Nu by
        // 4 % / 6.5 % at n = 160 in the conduction limit; the slow convergence with n was largely that artefact). The c_s^2 and thermal-floor changes of 2026-10-09 leave steady Nu unchanged
        // (n=160 before them: 1.972 / 4.165): they rescale thermal time, not the steady Peclet number.
        // Runs ThermalScheme.Hybrid (the GPU Hybrid port, 2026-10-10); WINDFLOW_31D_SCHEME=lbm runs the D3Q7 scheme through the same assertion.
        ReportTry("31d cavity n=160 GPU hot-wall Nu within 5% of the 3-D cube benchmark (Tric 2000: Ra=1e4 2.054, Ra=1e5 4.337)", () =>
        {
            if (!LbmSolver.GpuAvailable) throw new NotImplementedException("no GPU on this machine");
            var scheme = string.Equals(System.Environment.GetEnvironmentVariable("WINDFLOW_31D_SCHEME"), "lbm", StringComparison.OrdinalIgnoreCase)
                ? ThermalScheme.Lbm : ThermalScheme.Hybrid;
            var a = RunCavity(160, 1e4, Backend.Gpu, scheme);
            var b = RunCavity(160, 1e5, Backend.Gpu, scheme);
            double ea = Math.Abs(a.NuHot - 2.054) / 2.054, eb = Math.Abs(b.NuHot - 4.337) / 4.337;
            return (ea <= 0.05 && eb <= 0.05,
                $"Ra=1e4 NuHot={a.NuHot:F3} err3D={ea * 100:F1}% (vs 2-D 2.243 {(a.NuHot / 2.243 - 1) * 100:+0.0;-0.0}%) | " +
                $"Ra=1e5 NuHot={b.NuHot:F3} err3D={eb * 100:F1}% (vs 2-D 4.519 {(b.NuHot / 4.519 - 1) * 100:+0.0;-0.0}%) | band 5% | scheme={scheme} windows={a.Windows}/{b.Windows} backend={a.Backend}");
        });

        if (only != null && !only.IsMatch("32 tworooms"))     // 32 is the one check past the thermal-scheme block that can be selected (RV-07)
        {
            Console.WriteLine();
            Console.WriteLine(allPass ? "SELECTED CHECKS PASSED" : "SOME SELECTED CHECKS FAILED");
            return allPass ? 0 : 1;
        }

        // 32. tworooms-aq: two rooms in series on one cross-ventilation path (wind from the west, both windows
        // and the connecting door open). Room A is fed by outdoor air at the west window; room B is fed almost
        // entirely by air that has already passed through A, so B's air must be older. Mean age is the whole
        // point of the age-of-air field -- a solver that got the room-to-room ordering backwards would still
        // satisfy every single-room check above.
        CheckScene("32 tworooms-aq downstream MeanAge > upstream, upstream ACE finite in [0.1,2.5], downstream ACE none (RV-03/RV-07)", "tworooms-aq", r =>
        {
            var a = r.Rooms.FirstOrDefault(x => x.Room.Id == "A");   // upstream (west, x 0-5)
            var b = r.Rooms.FirstOrDefault(x => x.Room.Id == "B");   // downstream (east, x 5-10)
            if (a == null || b == null) return (false, "room A or B produced no RoomResult");
            if (float.IsNaN(a.MeanAge) || float.IsNaN(b.MeanAge)) throw new NotImplementedException("MeanAge NaN, not wired yet");
            bool ordered = b.MeanAge > a.MeanAge;
            // Same band as 20c, and for the same reason: these are jet-through rooms whose supply short-circuits
            // the occupied zone, so ACE sits well below the 1.0 of perfect mixing (measured 0.39 upstream, 0.32
            // downstream -- B is additionally fed with air that is already ~120 s old, which ASHRAE 129's
            // nominal time, computed from B's own inflow, does not know about).
            // RV-03: eps_a is on the room-VOLUME mean age now (this jet-through room reads 0.19; 0.39 was on the occupied-zone mean), so the floor is 0.1.
            bool AceOk(RoomResult rr) => !float.IsNaN(rr.AirChangeEffectiveness) && !float.IsInfinity(rr.AirChangeEffectiveness)
                && rr.AirChangeEffectiveness >= 0.1f && rr.AirChangeEffectiveness <= 2.5f;
            // RV-07 / D4: B is fed through the door, so under D4 its air-change efficiency is "—" (NaN) by design; only the room fed from outside has one.
            // RW identity (review of 2b-3c): Sandberg's tau_e = V / Q, so eps_a x 2 x <tau> must equal the nominal time V / (Inflow + InternalExchange) within 15 % (a tau_e bug of 2x fails; the 0.1 floor alone would not).
            float nominalA = a.VoxelVolume / MathF.Max(a.Inflow + a.InternalExchange, 1e-6f);
            float identity = a.AirChangeEffectiveness * 2f * a.VolumeMeanAge / nominalA;
            bool identityOk = MathF.Abs(identity - 1f) <= 0.15f;
            return (ordered && AceOk(a) && float.IsNaN(b.AirChangeEffectiveness) && identityOk,
                $"identity eps_a*2*<tau>/(V/Q) A={identity:F3} (must be within 0.15 of 1) " +
                $"MeanAge A(upstream)={a.MeanAge:F1}s B(downstream)={b.MeanAge:F1}s " +
                $"ACE A={a.AirChangeEffectiveness:F2} B={b.AirChangeEffectiveness:F2} " +
                $"(nominal A={a.VoxelVolume / MathF.Max(a.Inflow, 1e-6f):F1}s B={b.VoxelVolume / MathF.Max(b.Inflow, 1e-6f):F1}s)");
        });

        if (only != null)
        {
            Console.WriteLine();
            Console.WriteLine(allPass ? "SELECTED CHECKS PASSED" : "SOME SELECTED CHECKS FAILED");
            return allPass ? 0 : 1;
        }

        // 53/54. PLAN_V2 2.2 smarter convergence: drives a bespoke LbmSession through Simulation.Converge, the RETIRED
        // loop (batch 5: since 6f the app and headless run AveragedPass; these checks stay as "legacy loop (Validation-free)", nothing shipped calls ConvergeCore) -- cold start: the session's fixed warm-up, extended to the largest room's
        // flow-through time (V/Q); warm start: no fixed warm-up (the seed is a developed flow); then batch means
        // (>=1 flow-through per batch, start-up transient batches dropped) until every room's 95% half-width is
        // under its tolerance, capped or diverged. Check 54 once copied the cold-start warm-up into both runs, so a
        // warm start could never take fewer steps (1650 = 1650).
        (long Steps, ConvergenceStatus Status, int Batches) RunToConvergence(
            LbmSolver.LbmSession session, BuildingModel model, Vector2 wind, RunOptions opts, FlowField? seed = null)
        {
            // Core's retired AutoConverge loop (Simulation.Converge): a legacy-loop test, not what the app runs since 6f.
            var (status, batches) = Simulation.Converge(session, model, wind, opts, seed);
            return (session.Steps, status, batches);
        }

        {
            var floor53 = BuildCross("converge-monitor", true, true);
            var wind53 = new Vector2(3, 0);
            // Auto: a batch is one real flow-through of the room (~7k steps here), too slow for the CPU path.
            var opts53 = new RunOptions { TargetCells = 250_000, Backend = Backend.Auto };
            var grid53 = Voxelizer.Build(floor53, opts53.TargetCells!.Value, null, null, minDx: 0.1f);
            using var session53 = LbmSolver.Start(grid53, floor53, wind53, opts53);
            var (steps53, status53, batches53) = RunToConvergence(session53, floor53, wind53, opts53);
            string intervals = string.Join("; ", status53.Rooms.Select(r =>
                $"{r.RoomId}={r.Mean:F2}+/-{r.HalfWidth:F3}ACH(n={r.BatchCount})"));
            Check("53 legacy loop (Validation-free): convergence monitor stops itself once every room's ACH settles, and reports the interval",
                status53.Reason == ConvergenceStopReason.Converged,
                $"reason={status53.Reason} steps={steps53} batches={batches53} [{intervals}]");
        }

        // 54. Preview-to-Refine warm start: the SAME finer grid solved to convergence from rest (cold) and
        // warm-started from a coarser Preview run's averaged velocity (LbmSolver.Start's warmStart param,
        // trilinear equilibrium seeding) -- warm start must reach the monitor's convergence in fewer steps.
        {
            var floorWs = BuildCross("warm-start", true, true);
            var windWs = new Vector2(3, 0);
            var previewOpts = new RunOptions { TargetCells = 120_000, Backend = Backend.Cpu };
            var previewField = LbmSolver.Solve(Voxelizer.Build(floorWs, 120_000, null, null, minDx: 0.1f), floorWs, windWs, previewOpts);

            var refineOpts = new RunOptions { TargetCells = 400_000, Backend = Backend.Auto };
            var refineGrid = Voxelizer.Build(floorWs, 400_000, null, null, minDx: 0.1f);

            using var coldSession = LbmSolver.Start(refineGrid, floorWs, windWs, refineOpts);
            var (coldSteps, coldStatus, _) = RunToConvergence(coldSession, floorWs, windWs, refineOpts);

            var refineGrid2 = Voxelizer.Build(floorWs, 400_000, null, null, minDx: 0.1f);
            using var warmSession = LbmSolver.Start(refineGrid2, floorWs, windWs, refineOpts, previewField);
            var (warmSteps, warmStatus, _) = RunToConvergence(warmSession, floorWs, windWs, refineOpts, previewField);

            Check("54 legacy loop (Validation-free): Refine warm-started from Preview converges in fewer steps than Refine from rest",
                coldStatus.Reason == ConvergenceStopReason.Converged && warmStatus.Reason == ConvergenceStopReason.Converged
                    && warmSteps < coldSteps,
                $"coldSteps={coldSteps} warmSteps={warmSteps} coldReason={coldStatus.Reason} warmReason={warmStatus.Reason}");
        }

        // 54b. Convergence statistics without a GPU (also `-- --converge-stats`): CI coverage under autocorrelation, start-up transient,
        // and the field accumulator against a direct weighted mean.
        ReportTry("54b batch statistics: AR(1) CI coverage 0.90-0.98, converged mean within tolerance after a transient, accumulator == direct mean over (d,n]",
            ConvergeStats54b);

        // Devices (DeviceChecks.cs) and walls (WallChecks.cs) build their own scenes and print in the same
        // style; they run here so the default pass is the whole story, and separately behind --devices and
        // --walls when only one of them is being worked on.
        Console.WriteLine();
        int deviceFailures = DeviceChecks.Run();
        Console.WriteLine();
        int wallFailures = WallChecks.Run();
        Console.WriteLine();
        int nestedFailures = NestedChecks.Run();
        Console.WriteLine();
        int inletFailures = InletChecks.Run();
        Console.WriteLine();
        int warmFailures = WarmStartChecks.Run();
        Console.WriteLine();
        int boundaryFailures = BoundaryChecks.Run();
        Console.WriteLine();
        int orificeFailures = OrificeChecks.Run();
        int auditFailures = AuditChecks.Run();
        int fixFailures = FixChecks.Run();
        allPass &= deviceFailures == 0 && wallFailures == 0 && nestedFailures == 0 && inletFailures == 0 && warmFailures == 0
            && boundaryFailures == 0 && orificeFailures == 0 && auditFailures == 0 && fixFailures == 0;

        Console.WriteLine();
        Console.WriteLine(allPass ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");
        return allPass ? 0 : 1;
    }

    enum Verdict { Pass, Fail, Wait }

    /// <summary>Effective area of two openings in series: 1/A^2 = 1/A1^2 + 1/A2^2.</summary>
    static float SeriesArea(float a1, float a2) => a1 > 0 && a2 > 0 ? 1f / MathF.Sqrt(1f / (a1 * a1) + 1f / (a2 * a2)) : 0f;

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WindFlow.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
