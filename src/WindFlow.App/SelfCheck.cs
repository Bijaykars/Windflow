using System.Numerics;
using WindFlow.App.Import;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>--selfcheck: the device and import rules that need no window or GPU, checked against the sample
/// house. Writes out/app-selfcheck.txt and returns 1 on any failure.</summary>
public static partial class SelfCheck
{
    /// <summary>RA-18: the STEP-1 measurement seams (env) must not leak into the checks: cleared for the run, restored after; the NEE inflow default is set explicitly.</summary>
    public static int Run()
    {
        string[] seams = ["WINDFLOW_IC_SEED", "WINDFLOW_FIXED_BATCHES", "WINDFLOW_GROUP_AGES", "WINDFLOW_BATCH_LOG", "WINDFLOW_INFLOW",
            "WINDFLOW_NU_FLOOR", "WINDFLOW_SUPPLY_PERTURB", "WINDFLOW_WALL_MODEL", "WINDFLOW_THERMAL_SCHEME", "WINDFLOW_ACCURATE_CAP_S", "WINDFLOW_PLUME_MODEL"];   // WINDFLOW_NO_GPU stays: the runner uses it
        var saved = seams.Select(System.Environment.GetEnvironmentVariable).ToArray();
        bool neeSaved = LbmSolver.NeeInflow, plumeSaved = LbmSolver.PlumeModel;
        var shiftSaved = Voxelizer.OriginShiftCells;   // read once into a static from WINDFLOW_ORIGIN_SHIFT
        try
        {
            foreach (var s in seams) System.Environment.SetEnvironmentVariable(s, null);
            LbmSolver.NeeInflow = true;
            LbmSolver.PlumeModel = false;   // read once from WINDFLOW_PLUME_MODEL; the checks run with the default (off)
            Voxelizer.OriginShiftCells = Vector2.Zero;
            return RunChecks();
        }
        finally
        {
            for (int i = 0; i < seams.Length; i++) System.Environment.SetEnvironmentVariable(seams[i], saved[i]);
            LbmSolver.NeeInflow = neeSaved;
            LbmSolver.PlumeModel = plumeSaved;
            Voxelizer.OriginShiftCells = shiftSaved;
        }
    }

    static int RunChecks()
    {
        var lines = new List<string>();
        int failed = 0;
        void Check(string name, bool ok, string detail)
        {
            lines.Add($"[{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
            if (!ok) failed++;
        }

        // An old worker is held until after invalidation and a newer publication, then released.
        var publication = new PublicationEpoch();
        int oldEpoch = publication.Current, published = 0;
        bool oldAccepted = true;
        using (var ready = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var worker = Task.Run(() =>
            {
                ready.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) return;
                oldAccepted = publication.Publish(oldEpoch, () => published = 1);
            });
            bool started = ready.Wait(TimeSpan.FromSeconds(5));
            int current = publication.Advance();
            publication.Publish(current, () => published = 2);
            release.Set();
            bool finished = worker.Wait(TimeSpan.FromSeconds(5));
            Check("old analysis cannot overwrite a new epoch", started && finished && !oldAccepted && published == 2,
                $"oldAccepted={oldAccepted}, published={published}");
        }

        var model = SampleBuilding.Build();
        var living = model.Rooms.First(r => r.Name == "Living");
        var mid = new Vector3(3.5f, 4f, 0);

        // High Accuracy: finer than Accurate inside a 16 GB card's 60 % budget; Accurate's cell size (and a warning) when the budget
        // allows no more; Preview/Accurate options carry no Tu and no wind turbulence, and Configure is the only thing that adds them.
        {
            var whole = HighAccuracy.Plan(model, 14L << 30, 5_000_000);
            var hi = HighAccuracy.Plan(model, 14L << 30, 5_000_000, "bed1");
            var tight = HighAccuracy.Plan(model, 200L << 20, 5_000_000, "bed1");
            var bed1 = model.Rooms.First(r => r.Id == "bed1");
            bool boxed = hi.FineBox is { } fb && fb.Min.X <= bed1.Loops[0].Min(p => p.X) && fb.Max.X >= bed1.Loops[0].Max(p => p.X) &&
                         fb.Min.Y <= bed1.Loops[0].Min(p => p.Y) && fb.Max.Y >= bed1.Loops[0].Max(p => p.Y) &&
                         fb.Min.Z <= bed1.ZMin && fb.Max.Z >= bed1.ZMax && hi.FineCells > 0 && hi.FineCells < hi.Cells;
            var plain = new RunOptions { Quality = Quality.Refine, TargetCells = 5_000_000 };
            var preview = new RunOptions { Quality = Quality.Preview };
            bool untouched(RunOptions o) => !o.SupplyTuPerSource && o.WindTurbulence == null && o.SupplyPerturbation == 0f &&
                                            o.WallCapSeconds == 0 && o.WarmupFlowThroughs == 1 && !o.AutoConverge && o.WallModel == WallModel.None;
            var configured = new RunOptions();
            HighAccuracy.Configure(configured, model, hi);
            bool tus = HighAccuracy.DefaultSupplyTu(new AirSource { Device = DeviceKind.Diffuser, Kind = SourceKind.Supply }) == 0.20f &&
                       HighAccuracy.DefaultSupplyTu(new AirSource { Device = DeviceKind.Patch, Kind = SourceKind.Supply, Width = 1f, Height = 0.1f }) == 0.04f &&
                       HighAccuracy.DefaultSupplyTu(new AirSource { Device = DeviceKind.Patch, Kind = SourceKind.Supply, Width = 0.3f, Height = 0.3f }) == 0.10f;
            Check("HighAccuracy = Accurate's Dx everywhere (no global Dx/2), a 2:1 fine box around the focus room within budget, none without focus or when memory is short; Preview/Accurate options unchanged (Tu 0, no wind turbulence)",
                whole.FineBox == null && whole.Dx == whole.AccurateDx && hi.Dx == hi.AccurateDx && hi.Cells == whole.Cells &&
                boxed && Math.Abs(hi.FineDx - hi.Dx / 2f) < 1e-6f && hi.Bytes <= hi.BudgetBytes && hi.StepsPerSecond > 0 &&
                tight.FineBox == null && tight.Dx == tight.AccurateDx && tight.Warnings.Count > 0 && untouched(plain) && untouched(preview) &&
                configured.Quality == Quality.HighAccuracy && configured.SupplyTuPerSource && configured.WindTurbulence != null &&
                configured.FocusBox == hi.FineBox && !configured.Thermal &&
                configured.WarmupFlowThroughs >= 2 && configured.WallCapSeconds == 600 && configured.WallModel == WallModel.None && tus,
                $"{hi} | no focus: {whole} | tight budget: {tight}");
        }

        // Floor fan: placed by a ray looking down into the room, it must blow horizontally.
        var pedestal = Devices.PresetFor(DeviceKind.PedestalFan);
        var eye = mid + new Vector3(-6, -4, 8);
        var dir = Vector3.Normalize(new Vector3(mid.X, mid.Y, living.ZMin) - eye);
        var floorHit = Devices.Pick(model, eye, dir, pedestal);
        var floorFan = floorHit is { } fh ? Devices.Create(pedestal, fh.Center, fh.Inward, fh.Room.Level, 1) : null;
        Check("pedestal fan blows horizontally", floorFan != null && MathF.Abs(floorFan.Direction.Z) < 1e-3f,
            $"Direction {floorFan?.Direction}");
        var spun = Devices.FromYaw(90f, 1f);
        Check("FromYaw on a vertical vector gives a horizontal one", MathF.Abs(spun.Z) < 1e-3f && MathF.Abs(spun.X - 1f) < 1e-3f,
            $"FromYaw(90, z=1) = {spun}");

        // Ceiling fan: hung on a downrod, clear of the slab.
        var ceilingPreset = Devices.PresetFor(DeviceKind.CeilingFan);
        var up = Vector3.Normalize(new Vector3(mid.X, mid.Y, living.ZMax) - (mid + new Vector3(-3, -2, 1.2f)));
        var ceilingHit = Devices.Pick(model, mid + new Vector3(-3, -2, 1.2f), up, ceilingPreset);
        float drop = ceilingHit is { } ch ? ch.Room.ZMax - ch.Center.Z : float.NaN;
        Check("new ceiling fan hangs 0.35 m under the ceiling", MathF.Abs(drop - Devices.CeilingFanDrop) < 1e-3f,
            $"drop {drop:0.###} m");

        // PLAN_V2 4.5: each preset snaps to its own surface. Ceiling fans and cassettes on the ceiling, split AC and
        // exhaust on a wall, pedestal fans on the floor; and a wall pick lands on the wall facing into the room.
        var surfaces = new (DeviceKind Kind, MountSurface Want)[]
        {
            (DeviceKind.CeilingFan, MountSurface.Ceiling), (DeviceKind.CassetteAc, MountSurface.Ceiling),
            (DeviceKind.SplitAc, MountSurface.Wall), (DeviceKind.ExhaustFan, MountSurface.Wall),
            (DeviceKind.PedestalFan, MountSurface.Floor),
        };
        var wrongSurface = surfaces.Where(s => Devices.PresetFor(s.Kind).Surface != s.Want).Select(s => s.Kind).ToList();
        var splitPreset = Devices.PresetFor(DeviceKind.SplitAc);
        var wallEye = new Vector3(living.Loops[0].Average(p => p.X), living.Loops[0].Average(p => p.Y), living.ZMin + 1.5f);
        var wallHit = Devices.Pick(model, wallEye, Vector3.UnitX, splitPreset);
        bool onWall = wallHit is { } wh && MathF.Abs(wh.Inward.Z) < 1e-3f && Vector3.Dot(wh.Inward, Vector3.UnitX) < -0.99f &&
                      MathF.Abs(wh.Center.Z - (wh.Room.ZMin + splitPreset.MountHeight)) < 1e-3f;
        Check("surface snap per preset: fan/cassette -> ceiling, split AC/exhaust -> wall, pedestal -> floor",
            wrongSurface.Count == 0 && onWall && Devices.Rotatable(DeviceKind.PedestalFan) &&
            !Devices.Rotatable(DeviceKind.CeilingFan) && !Devices.Rotatable(DeviceKind.SplitAc),
            (wrongSurface.Count == 0 ? "all 5 kinds on their surface" : "wrong: " + string.Join(", ", wrongSurface)) +
            $" · split AC ray east from the Living centre hits {(wallHit is { } w2 ? $"{w2.Center} facing {w2.Inward}" : "nothing")}");

        // PLAN_V2 3.3: a fan disc under 8 cells across needs refining. The 1.2 m fan: 4.7 cells at 255 mm (refine),
        // exactly 8 at 150 mm (OK); the split AC's 120 mm slot needs 15 mm cells; no grid yet is not a verdict.
        var fan12 = Devices.Create(ceilingPreset, mid, -Vector3.UnitZ, "L1", 1);
        var split = Devices.Create(splitPreset, mid, Vector3.UnitX, "L1", 1);
        bool[] verdicts =
        [
            Devices.NeedsRefine(fan12, 0.255f), !Devices.NeedsRefine(fan12, 0.15f), !Devices.NeedsRefine(fan12, 0.1f),
            Devices.NeedsRefine(split, 0.1f), !Devices.NeedsRefine(split, 0.015f), !Devices.NeedsRefine(fan12, 0f),
        ];
        Check("refine-needed threshold: fan disc < 8 cells across (1.2 m fan: 255 mm refine, 150 mm OK)",
            verdicts.All(v => v) && Devices.Resolution(fan12, 0.255f).Label == "refine needed" &&
            Devices.Resolution(fan12, 0.15f).Label == "resolution OK",
            $"1.2 m fan {Devices.CellsAcross(fan12, 0.255f):0.0} cells at 255 mm, {Devices.CellsAcross(fan12, 0.15f):0.0} at 150 mm · " +
            $"split AC slot {Devices.ResolvedSpan(split) * 1000:0} mm · verdicts {string.Join(",", verdicts)}");

        // One-click box for "refine needed": the 1.2 m fan at 255 mm gets a nested box (127.5 mm cells, 9.4 across,
        // OK) whose faces clear the outlet by 1.5 widths + the blend band + one fine cell; the 120 mm split-AC slot
        // stays under 8 cells even in that box, so it is "too small to resolve", not a red chip a click cannot fix.
        {
            float coarse = 0.255f, fine = 0.5f * coarse, band = (Devices.NestedSpongeCells + 1) * fine;
            var box = Devices.DeviceBox([fan12], band, fine, model.GroundZ);
            float boxDx = Devices.BoxDx(coarse, nested: true, 0, box);
            var (a, b) = BlendBand.Extent(fan12);
            float clear = BlendBand.Nearest(box, a, b, bottom: box.Min.Z > model.GroundZ + 1e-3f, top: true)?.Dist ?? -1f;
            float want = Devices.BoxMarginWidths * Devices.BoxWidth(fan12) + band + fine;
            var two = Devices.DeviceBox([fan12, split], band, fine, model.GroundZ);
            bool ok = Devices.NeedsRefine(fan12, coarse) && !Devices.NeedsRefine(fan12, boxDx) &&
                      MathF.Abs(clear - want) < 1e-3f && !BlendBand.Inside(box, (a, b), band, box.Min.Z > model.GroundZ + 1e-3f, true) &&
                      Devices.NeedsRefine(split, boxDx) && Devices.TooSmallWhy(split, boxDx, false).Contains("120 mm outlet") &&
                      two.Min.X <= box.Min.X && two.Max.X >= box.Max.X &&
                      Devices.BoxDx(coarse, nested: false, 1_500_000, box) is > 0.09f and < 0.26f;
            Check("device box: 1.2 m fan boxed at 128 mm resolves, clear of the blend band; 120 mm AC slot too small even boxed",
                ok, $"box {box.Min} .. {box.Max} · fan {Devices.CellsAcross(fan12, boxDx):0.0} cells at {boxDx * 1000:0} mm · " +
                    $"outlet to nearest face {clear:0.00} m (want {want:0.00}) · AC {Devices.CellsAcross(split, boxDx):0.0} cells · " +
                    $"v1 box would be {Devices.BoxDx(coarse, nested: false, 1_500_000, box) * 1000:0} mm");
        }

        // Extract placement: inside an open exterior opening only.
        var window = model.Openings.First(o => o.Kind == OpeningKind.Window && o.IsOpen);
        var door = model.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door && !Devices.IsExterior(model, o));
        var along = Vector3.Normalize(new Vector3(-window.Normal.Y, window.Normal.X, 0));
        Check("exhaust in an open exterior window is accepted", Devices.InOpenExteriorOpening(model, window.Center),
            window.Name);
        Check("exhaust 0.5 m past the window edge is refused",
            !Devices.InOpenExteriorOpening(model, window.Center + along * (window.Width * 0.5f + 0.5f)), window.Name);
        Check("exhaust in an interior door is refused", door != null && !Devices.InOpenExteriorOpening(model, door.Center),
            door?.Name ?? "(no interior door found)");
        window.IsOpen = false;
        Check("exhaust in a CLOSED window is refused", !Devices.InOpenExteriorOpening(model, window.Center), window.Name);
        window.IsOpen = true;

        // Ids: add, add, remove the first, add again: all distinct, none reused while still taken.
        var diffuser = Devices.PresetFor(DeviceKind.Diffuser);
        var ids = new List<string>();
        AirSource Add()
        {
            var s = Devices.Create(diffuser, mid, -Vector3.UnitZ, "L1", Devices.NextOrdinal(model, diffuser.Kind));
            model.Sources.Add(s);
            ids.Add(s.Id);
            return s;
        }
        var first = Add();
        Add();
        model.Sources.Remove(first);
        Add();
        var live = model.Sources.Where(s => s.Device == DeviceKind.Diffuser).Select(s => s.Id).ToList();
        Check("device ids stay unique after add/add/remove/add", live.Distinct().Count() == live.Count,
            $"issued {string.Join(", ", ids)} · in model {string.Join(", ", live)}");

        // Import: a coordinate past float range is refused with a message, not voxelised into NaN.
        string obj = Path.Combine(Path.GetTempPath(), "windflow-selfcheck-inf.obj");
        File.WriteAllText(obj, "v 1e39 0 0\nv 0 1 0\nv 0 0 1\nv 1 1 1\nf 1 2 3\nf 2 3 4\n");
        string message;
        bool refused;
        try { MeshImport.Load(obj); refused = false; message = "loaded without complaint"; }
        catch (InvalidDataException e) { refused = true; message = e.Message; }
        catch (Exception e) { refused = false; message = $"{e.GetType().Name}: {e.Message}"; }
        Check("OBJ with v 1e39 is rejected with a message", refused, message);

        // Snapshot: a deep copy for the solver thread, obstacles shared.
        var snap = LiveSolver.Snapshot(model);
        snap.Openings[0].IsOpen = !snap.Openings[0].IsOpen;
        Check("model snapshot is independent of the original",
            snap.Openings[0].IsOpen != model.Openings[0].IsOpen && ReferenceEquals(snap.Obstacles, model.Obstacles) &&
            snap.Sources.Count == model.Sources.Count, $"{snap.Sources.Count} sources, obstacles shared");

        // Room detection: two rooms joined by a doorway about two cells wide. The one-cell inset cuts the neck;
        // both halves must come back as rooms, with a note saying the region split.
        var neck = new BuildingModel
        {
            Name = "neck test",
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } },
        };
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(-1, -1, -0.2f), new Vector3(11, 6, 0)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.25f, -0.25f, 3), new Vector3(10.25f, 5.25f, 3.2f)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.25f, -0.25f, 0), new Vector3(10.25f, 0, 3)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.25f, 5, 0), new Vector3(10.25f, 5.25f, 3)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.25f, 0, 0), new Vector3(0, 5, 3)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(10, 0, 0), new Vector3(10.25f, 5, 3)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(4.9f, 0, 0), new Vector3(5.1f, 2.22f, 3)));
        neck.Obstacles.Add(SampleBuilding.Box(new Vector3(4.9f, 2.78f, 0), new Vector3(5.1f, 5, 3)));
        var detected = RoomDetect.Detect(neck, System.Environment.ProcessorCount - 1);
        Check("a room pair joined by a narrow doorway gives two rooms", detected.Rooms.Count == 2,
            $"{detected.Rooms.Count} rooms at {detected.Dx * 1000:0} mm · {string.Join(" | ", detected.Notes)}");

        // S3-27: a 20 x 20 m single-storey ring (0.2 m walls, roof 3.0-3.2 m) around an open 10 x 10 m courtyard, with one window in the
        // courtyard-side wall. The courtyard must come back as an inner loop (area ~260 m2, not 348), outside the volume, and the window
        // must be found with the courtyard (open sky) as its outdoor side.
        var ring = new BuildingModel
        {
            Name = "ring courtyard",
            Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } },
        };
        Vector3 V(float x, float y, float z) => new(x, y, z);
        ring.Obstacles.Add(SampleBuilding.Box(V(-1, -1, -0.2f), V(21, 21, 0)));
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 0, 0), V(20, 0.2f, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 19.8f, 0), V(20, 20, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 0, 0), V(0.2f, 20, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(19.8f, 0, 0), V(20, 20, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(5, 14.8f, 0), V(15, 15, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(5, 5, 0), V(5.2f, 15, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(14.8f, 5, 0), V(15, 15, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(5, 5, 0), V(9, 5.2f, 3.2f)));           // south courtyard wall with a window: x 9-11, z 0.9-2.1
        ring.Obstacles.Add(SampleBuilding.Box(V(11, 5, 0), V(15, 5.2f, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(9, 5, 0), V(11, 5.2f, 0.9f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(9, 5, 2.1f), V(11, 5.2f, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 0, 3), V(20, 5, 3.2f)));             // roof ring, open over the courtyard
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 15, 3), V(20, 20, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(0, 5, 3), V(5, 15, 3.2f)));
        ring.Obstacles.Add(SampleBuilding.Box(V(15, 5, 3), V(20, 15, 3.2f)));
        static bool EvenOdd(List<Vector2[]> loops, Vector2 q)
        {
            bool inside = false;
            foreach (var l in loops)
                for (int i = 0, j = l.Length - 1; i < l.Length; j = i++)
                    if ((l[i].Y > q.Y) != (l[j].Y > q.Y) && q.X < (l[j].X - l[i].X) * (q.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) inside = !inside;
            return inside;
        }
        var ringDet = RoomDetect.Detect(ring, System.Environment.ProcessorCount - 1);
        var ringRoom = ringDet.Rooms.Count == 1 ? ringDet.Rooms[0] : null;
        float ringArea = ringRoom == null ? 0 : RoomNames.FloorArea(ringRoom);
        float ringVol = ringRoom == null ? 0 : ringRoom.DetectedVolume / 3f;
        bool courtyardOut = ringRoom != null && !EvenOdd(ringRoom.Loops, new Vector2(10, 10)) &&
                            EvenOdd(ringRoom.Loops, new Vector2(10, 2.5f));
        var ringWin = ringDet.Openings.Where(o => o.Kind == OpeningKind.Window).ToList();
        // IMP-A D6 (FIX_RULES rule 3): DetectedVolume is the 3D air up to the roof, so its reference is the geometric one: the interior is 19.6 x 19.6 m (20 m minus two 0.2 m walls)
        // less the courtyard block as built, 10 x 10 m outside (walls at 5-5.2 and 14.8-15; IMP-A's 10.4 counted those walls twice) = 384.16 - 100 = 284.16 m2, x 3.0 m to the roof underside. Tolerance unchanged: +-5 %.
        // IMP-B D1 (rule 3): the loop area reference moves with the cell size, 260 -> 276.5 m2: the outer loop is the air inset by InsetM (2 cells of 100 mm here), (19.6 - 2 x 0.2)^2 = 368.64, less the
        // courtyard loop at its own edge, 9.6^2 = 92.16 (the old 260 came from an inset of about 0.42 m at the old 0.42 m cells). Tolerance unchanged: +-5 %.
        Check("a ring around an open courtyard: one room, the courtyard an inner loop (loop area 276.5 m2 +-5 %, volume/3 284.16 m2 +-5 %, courtyard outside the mask)",
            ringRoom != null && ringRoom.Loops.Count == 2 && courtyardOut && MathF.Abs(ringArea - 276.5f) < 276.5f * 0.05f && MathF.Abs(ringVol - 284.16f) < 284.16f * 0.05f,
            $"{ringDet.Rooms.Count} rooms, {ringRoom?.Loops.Count} loops, loop area {ringArea:0.#} m2, volume/3 {ringVol:0.#} m2, courtyard outside {courtyardOut}");
        Check("a window in the courtyard wall is detected with the courtyard as its outdoor side",
            ringWin.Count == 1 && ringWin[0].Normal.Y > 0.9f,   // the name no longer carries the side; the normal does
            string.Join(" | ", ringDet.Openings.Select(o => $"{o.Name} n=({o.Normal.X:0},{o.Normal.Y:0})")));

        // ---- IMP-B: detection quality (D1, D4, D8, D9, OF-1 consistency) on small synthetic buildings, CPU only ------------------------------------------
        {
            BuildingModel Building(float w, float d, float h, params Mesh[] more)
            {
                var m = new BuildingModel { Name = "imp-b", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = h, Analysed = true } } };
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(-1, -1, -0.2f), new Vector3(w + 1, d + 1, 0)));
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, h), new Vector3(w, d, h + 0.2f)));
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, 0), new Vector3(w, 0.2f, h)));
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(0, d - 0.2f, 0), new Vector3(w, d, h)));
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0.2f, 0), new Vector3(0.2f, d - 0.2f, h)));
                m.Obstacles.Add(SampleBuilding.Box(new Vector3(w - 0.2f, 0.2f, 0), new Vector3(w, d - 0.2f, h)));
                m.Obstacles.AddRange(more);
                return m;
            }
            Mesh B(float x0, float y0, float z0, float x1, float y1, float z1) => SampleBuilding.Box(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));

            // D1 + D4: a 30 x 16 m building (480 m2, so 100 mm cells) with a 1.0 m doorway running floor to ceiling in the partition at x = 20 (no head), and a 2.2 x 2.0 m closet
            // with a door that has a head. The closet and both doorways are found, and the doorway splits the building into two rooms.
            var hall = Building(30, 16, 3,
                B(19.9f, 0.2f, 0, 20.1f, 7f, 3), B(19.9f, 8f, 0, 20.1f, 15.8f, 3),
                B(27.4f, 0.2f, 0, 27.6f, 2.4f, 3), B(27.4f, 2.2f, 0, 28.3f, 2.4f, 3), B(29.2f, 2.2f, 0, 29.8f, 2.4f, 3), B(28.3f, 2.2f, 2.1f, 29.2f, 2.4f, 3));
            var hallDet = RoomDetect.Detect(hall, System.Environment.ProcessorCount - 1);
            Check("IMP-B D1/D4: a 480 m2 building is detected on 100 mm cells; a 4.4 m2 closet and a floor-to-ceiling doorway (no head) are found, three rooms",
                MathF.Abs(hallDet.Dx - RoomDetect.StandardDx) < 1e-6f && hallDet.Rooms.Count == 3 &&
                hallDet.Openings.Any(o => MathF.Abs(o.Center.X - 20f) < 0.2f && MathF.Abs(o.Center.Y - 7.5f) < 0.2f && o.Kind == OpeningKind.Door && MathF.Abs(o.Width - 1f) < 0.1f) &&
                hallDet.Openings.Any(o => MathF.Abs(o.Center.X - 28.75f) < 0.2f && MathF.Abs(o.Center.Y - 2.3f) < 0.2f && o.Kind == OpeningKind.Door),
                $"{hallDet.Dx * 1000:0} mm, {hallDet.Rooms.Count} rooms [{string.Join(", ", hallDet.Rooms.Select(r => RoomNames.FloorArea(r).ToString("0.#")))} m2] · {string.Join(" | ", hallDet.Openings.Select(o => $"{o.Name} {o.Width:0.00}x{o.Height:0.00} at ({o.Center.X:0.0},{o.Center.Y:0.0})"))}");

            // D8a: a 0.8 m wide gap from the floor to 2.4 m, crossed by two thin rails that leave three 0.8 m casements, is windows, not one door.
            var stack = Building(8, 5, 3, B(0.2f, 0, 0, 3f, 0.25f, 3), B(3.8f, 0, 0, 7.8f, 0.25f, 3), B(3f, 0, 2.4f, 3.8f, 0.25f, 3), B(3f, 0, 0.82f, 3.8f, 0.25f, 0.83f), B(3f, 0, 1.62f, 3.8f, 0.25f, 1.63f));
            var stackDet = RoomDetect.Detect(stack, System.Environment.ProcessorCount - 1);
            Check("IMP-B D8a: a floor-to-2.4 m gap split by two thin rails into 0.8 m casements is one window, not a door",
                stackDet.Openings.Count == 1 && stackDet.Openings[0].Kind == OpeningKind.Window && stackDet.Openings[0].Height > 2.3f,
                string.Join(" | ", stackDet.Openings.Select(o => $"{o.Name} {o.Kind} {o.Width:0.00}x{o.Height:0.00}")));

            // D8b: a window whose glazing sits mid-wall: two gaps, one each side of the pane, are one opening.
            var twoLayer = Building(8, 5, 3, B(0.2f, 0, 0, 3f, 0.25f, 3), B(4f, 0, 0, 7.8f, 0.25f, 3), B(3f, 0, 0, 4f, 0.25f, 0.9f), B(3f, 0, 2.1f, 4f, 0.25f, 3), B(3f, 0.12f, 0.9f, 4f, 0.13f, 2.1f));
            var twoDet = RoomDetect.Detect(twoLayer, System.Environment.ProcessorCount - 1);
            Check("IMP-B D8b: a window with a pane in the middle of a 0.25 m wall is one opening, not two",
                twoDet.Openings.Count == 1 && twoDet.Openings[0].Kind == OpeningKind.Window,
                string.Join(" | ", twoDet.Openings.Select(o => $"{o.Name} {o.Kind} {o.Width:0.00}x{o.Height:0.00} depth {o.Depth:0.00}")));

            // D9: a mono-pitch roof (2.5 m at the front wall, 4.5 m at the back) with gable walls whose tops follow it: no opening, however tall the band is.
            Mesh Slanted(float x0, float x1, float y0, float y1, float zFront, float zBack, float thickness, bool solidBelow)
            {
                var m = SampleBuilding.Box(new Vector3(x0, y0, 0), new Vector3(x1, y1, zFront + thickness));
                for (int i = 0; i < m.Vertices.Length; i++)
                {
                    var v = m.Vertices[i];
                    float top = v.Y >= y1 - 1e-4f ? zBack : zFront;
                    m.Vertices[i] = new Vector3(v.X, v.Y, v.Z <= 1e-4f && solidBelow ? 0 : v.Z <= 1e-4f ? top : top + thickness);
                }
                return m;
            }
            var mono = new BuildingModel { Name = "mono-pitch", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } } };
            mono.Obstacles.Add(B(-1, -1, -0.2f, 11, 7, 0));
            mono.Obstacles.Add(B(0, 0, 0, 10, 0.2f, 2.5f));
            mono.Obstacles.Add(B(0, 5.8f, 0, 10, 6, 4.5f));
            mono.Obstacles.Add(Slanted(0, 0.2f, 0.2f, 5.8f, 2.5f, 4.5f, 0f, true));
            mono.Obstacles.Add(Slanted(9.8f, 10, 0.2f, 5.8f, 2.5f, 4.5f, 0f, true));
            mono.Obstacles.Add(Slanted(-0.3f, 10.3f, -0.3f, 6.3f, 2.5f, 4.5f, 0.2f, false));
            var monoDet = RoomDetect.Detect(mono, System.Environment.ProcessorCount - 1);
            Check("IMP-B D9: a sloped roof and its gable walls are not a wall with a hole: no door, window or opening (nothing over 0.5 m; seam slivers read as 0.1-0.15 m vents) under a mono-pitch roof",
                monoDet.Openings.All(o => MathF.Max(o.Width, o.Height) < 0.5f),
                $"{monoDet.Rooms.Count} rooms, openings: {string.Join(" | ", monoDet.Openings.Select(o => $"{o.Name} {o.Width:0.00}x{o.Height:0.00}"))}");
        }

        // IMP-B OF-1 consistency: a room's cells are its outline plus the band of wall-gap air (Analysis.RoomMasks) for the viewer's room mask and the public building rate as for Finish.
        // A 3 m cube of air in a solid shell, a room outline one cell inside the walls: the band is the ring the outline leaves, 100 of 180 cells. Age 360 s inside the outline, 720 s in the ring.
        {
            var g = new VoxelGrid { Nx = 10, Ny = 10, Nz = 8, Dx = 0.5f, Origin = Vector3.Zero, Cell = new byte[800] };
            for (int z = 0; z < 8; z++) for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++)
                g.Cell[g.Index(x, y, z)] = x is >= 2 and <= 7 && y is >= 2 and <= 7 && z is >= 2 and <= 6 ? VoxelGrid.Air : VoxelGrid.Solid;
            var room = new Room { Id = "r", Name = "R", Level = "L1", ZMin = 1f, ZMax = 3.5f };
            room.Loops.Add([new Vector2(1.5f, 1.5f), new Vector2(3.5f, 1.5f), new Vector2(3.5f, 3.5f), new Vector2(1.5f, 3.5f)]);
            var oneRoom = new BuildingModel { Rooms = { room } };
            int banded = Analysis.RoomMasks(g, oneRoom, out var outlineCells)[0].Count(b => b);
            int viewer = FieldVolume.RoomMask(oneRoom, g).Count(b => b);
            var age = new float[g.Count];
            for (int z = 0; z < 8; z++) for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++)
                age[g.Index(x, y, z)] = x is >= 3 and <= 6 && y is >= 3 and <= 6 ? 360f : 720f;
            float rate = Analysis.BuildingFreshAch(g, oneRoom, age), want = 3600f / ((80 * 360f + 100 * 720f) / 180f);
            Check("IMP-B OF-1: the viewer's room mask and the public building fresh-air rate count the room's band (outline 80 cells + ring 100), as Finish does",
                banded == 180 && outlineCells[0] == 80 && viewer == 180 && MathF.Abs(rate - want) < 0.01f,
                $"outline {outlineCells[0]}, banded {banded}, viewer mask {viewer} cells · rate {rate:0.000} ACH (want {want:0.000}; the outline alone reads 10.000)");
        }

        // IMP-C 3: detection stores what each side of an opening faces. A 6 x 4 m building, a partition at x = 5 with a headed doorway leaves a 0.7 m closet strip (2.5 m2, under the 3 m2
        // room floor, so covered air in no room); a sideWindow in the south wall faces the open air. Door: room + indoor air; sideWindow: room + outdoors.
        {
            var m = new BuildingModel { Name = "imp-c", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } } };
            Mesh B(float x0, float y0, float z0, float x1, float y1, float z1) => SampleBuilding.Box(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
            m.Obstacles.Add(B(-1, -1, -0.2f, 7, 5, 0)); m.Obstacles.Add(B(0, 0, 3, 6, 4, 3.2f));
            m.Obstacles.Add(B(0, 3.8f, 0, 6, 4, 3)); m.Obstacles.Add(B(0, 0.2f, 0, 0.2f, 3.8f, 3)); m.Obstacles.Add(B(5.8f, 0.2f, 0, 6, 3.8f, 3));
            m.Obstacles.Add(B(0, 0, 0, 1.5f, 0.2f, 3)); m.Obstacles.Add(B(2.5f, 0, 0, 6, 0.2f, 3)); m.Obstacles.Add(B(1.5f, 0, 0, 2.5f, 0.2f, 0.9f)); m.Obstacles.Add(B(1.5f, 0, 2.1f, 2.5f, 0.2f, 3));
            m.Obstacles.Add(B(4.9f, 0.2f, 0, 5.1f, 1f, 3)); m.Obstacles.Add(B(4.9f, 1.9f, 0, 5.1f, 3.8f, 3)); m.Obstacles.Add(B(4.9f, 1f, 2.1f, 5.1f, 1.9f, 3));
            var det = RoomDetect.Detect(m, System.Environment.ProcessorCount - 1);
            var sideDoor = det.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door); var sideWindow = det.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Window);
            Check("IMP-C 3: detection stores each side of an opening: a door from a room into covered air in no room is room + indoor, a window onto open air is room + outdoors",
                det.Rooms.Count == 1 && sideDoor != null && sideWindow != null &&
                (sideDoor.SideA, sideDoor.SideB) is (OpeningSide.Room, OpeningSide.Indoor) or (OpeningSide.Indoor, OpeningSide.Room) &&
                (sideWindow.SideA, sideWindow.SideB) is (OpeningSide.Room, OpeningSide.Outdoors) or (OpeningSide.Outdoors, OpeningSide.Room),
                $"{det.Rooms.Count} rooms · " + string.Join(" | ", det.Openings.Select(o => $"{o.Name} {o.SideA}/{o.SideB}")));
        }

        // IMP-C 4: the resolution guard counts rooms under 3 cells across and open openings under 4 at the grid's cell size, and names the cell budget that resolves them.
        {
            var m = new BuildingModel();
            var thin = new Room { Id = "t", Name = "Thin", ZMin = 0, ZMax = 3 }; thin.Loops.Add([new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 4), new Vector2(0, 4)]);
            var fat = new Room { Id = "f", Name = "Fat", ZMin = 0, ZMax = 3 }; fat.Loops.Add([new Vector2(2, 0), new Vector2(5, 0), new Vector2(5, 3), new Vector2(2, 3)]);
            m.Rooms.Add(thin); m.Rooms.Add(fat);
            m.Openings.Add(new Opening { Id = "s", Name = "Small", Width = 0.6f, Height = 0.6f, Depth = 0.2f });
            m.Openings.Add(new Opening { Id = "b", Name = "Big", Width = 1.2f, Height = 1.2f, Depth = 0.2f });
            var gg = new VoxelGrid { Nx = 40, Ny = 40, Nz = 62, Dx = 0.25f, Cell = new byte[40 * 40 * 62] };   // 99 200 cells
            var notes = Envelope.ResolutionGuard(gg, m, out var small, cellLimit: 1_000_000_000);
            double wantM = gg.Count * Math.Pow(0.25 / (0.5 / 3), 3) / 1e6;   // IMP-C2: the 0.5 m room wants 3 cells (0.1667 m); the 0.6 m opening no longer sets the budget
            var capped = Envelope.ResolutionGuard(gg, m, out _, cellLimit: 150_000);   // same fixture, a plan that holds only 150 k cells: 3.4x asked, over the limit
            var tiny = new BuildingModel(); var sliver = new Room { Id = "s", Name = "Sliver", ZMin = 0, ZMax = 3 }; sliver.Loops.Add([new Vector2(0, 0), new Vector2(0.1f, 0), new Vector2(0.1f, 4), new Vector2(0, 4)]); tiny.Rooms.Add(sliver);
            var tooFine = Envelope.ResolutionGuard(gg, tiny, out _, cellLimit: 1_000_000_000);   // 0.1 m room at 250 mm: 7.5 ratio, 422x the grid, over 4x
            Check("IMP-C 4: the guard flags a 0.5 m room (not a 3 m one), counts the 0.6 m opening, and suggests a budget from the rooms only, unless it is over 4x the grid or the plan's limit",
                small.Count == 1 && small.Contains(thin) && notes.Count == 1 && notes[0].StartsWith("1 room is too small for this cell size", StringComparison.Ordinal) &&
                notes[0].Contains($"about {wantM:0.#} M cells", StringComparison.Ordinal) && notes[0].EndsWith("1 opening is narrower than 4 cells.", StringComparison.Ordinal) &&
                capped[0].Contains("too detailed to resolve every room", StringComparison.Ordinal) && !capped[0].Contains(" M cells", StringComparison.Ordinal) &&
                tooFine[0].Contains("too detailed to resolve every room", StringComparison.Ordinal) && !tooFine[0].Contains(" M cells", StringComparison.Ordinal),
                (notes.Count > 0 ? notes[0] : "no note") + " | capped: " + (capped.Count > 0 ? capped[0] : "no note"));
        }

        // IMP-C 5: detection memory is capped at 150 M cells: a tall 19.9 m plan would need 233 M cells at 50 mm, so it coarsens to 100 mm and says so; a 60 x 60 x 40 m block to the 150 mm cap.
        {
            BuildingModel Tall(float w, float d, float h) { var t = new BuildingModel(); t.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, 0), new Vector3(w, d, h))); return t; }
            var tall = Tall(19.9f, 19.9f, 60f); var block = Tall(60f, 60f, 40f);
            var tallDet = RoomDetect.Detect(tall, System.Environment.ProcessorCount - 1);
            Check("IMP-C 5: fine detection over 150 M cells coarsens 50 -> 100 -> 150 mm with a note",
                RoomDetect.DetectionDx(tall) == RoomDetect.StandardDx && RoomDetect.DetectionDx(block) == RoomDetect.CoarsestDx &&
                Voxelizer.DetectionCells(tall, RoomDetect.FineDx) > RoomDetect.MaxDetectionCells && tallDet.Dx == RoomDetect.StandardDx &&
                tallDet.Notes.Any(n => n.StartsWith("Detection cells coarsened from 50 to 100 mm", StringComparison.Ordinal)),
                $"tall {RoomDetect.DetectionDx(tall) * 1000:0} mm ({Voxelizer.DetectionCells(tall, RoomDetect.FineDx) / 1e6:0} M at 50 mm), block {RoomDetect.DetectionDx(block) * 1000:0} mm · {tallDet.Notes.FirstOrDefault(n => n.StartsWith("Detection cells"))}");
        }

        // P17-07: a GPU failure restarts the run ONCE on the CPU and clears the cached device. Policy first (AveragedPass.TryGpuRestart, what the headless run and the
        // app both call): the first fault restarts and clears the cache, a second on the CPU does not; then a real run with a fake accelerator exception thrown from the
        // GPU stepping loop (LbmSolver.GpuStepTestHook) finishes on the CPU with the message in its notes, and the options get their backend back for the next run.
        {
            bool gpuHere = LbmSolver.GpuAvailable;
            var fo = new RunOptions();
            bool gfirst = AveragedPass.TryGpuRestart(fo, true, new LbmSolver.GpuFaultException(new InvalidOperationException("fake: CUDA_ERROR_LAUNCH_FAILED")), out var lostNote);   // RW-02: only a fault raised by a GPU call restarts
            bool gcleared = !LbmSolver.GpuDeviceCached, onCpu = fo.Backend == Backend.Cpu;
            bool gsecond = AveragedPass.TryGpuRestart(fo, true, new LbmSolver.GpuFaultException(new InvalidOperationException("fake again")), out _);
            bool divergence = AveragedPass.TryGpuRestart(new RunOptions(), true, new InvalidOperationException("Airflow solver diverged"), out _);
            bool oom = AveragedPass.TryGpuRestart(new RunOptions(), true, new LbmSolver.GpuOutOfMemoryException("out of memory"), out _);
            bool back = !gpuHere || LbmSolver.GpuAvailable;   // the next run creates the device again
            Check("a GPU failure restarts once on the CPU, clears the cached device and says so; divergence and out-of-memory are not restarts",
                gfirst && gcleared && onCpu && !gsecond && !divergence && !oom && back && lostNote.StartsWith("The GPU stopped responding (") && lostNote.EndsWith("Restarting this run on the CPU."),
                $"first {gfirst}, cache cleared {gcleared}, on CPU {onCpu}, second restart {gsecond}, divergence restart {divergence}, OOM restart {oom}, device back {back} · {lostNote}");

            // RW-02: a fault is decided by origin. A model with no obstacles (ArgumentException from the voxeliser) and an exception from the analysis (NullReferenceException) are
            // not GPU faults: no restart, the device stays cached, the options keep their backend, the run's own message surfaces.
            {
                bool cachedBefore = LbmSolver.GpuDeviceCached;
                var o2 = new RunOptions { Quality = Quality.Preview, TargetCells = 20_000 };
                string msg2 = ""; Exception? thrown2 = null;
                try { Simulation.Run(new BuildingModel(), new Vector2(3, 0), o2); }
                catch (Exception e) { thrown2 = e; msg2 = e.Message; }
                var o3 = new RunOptions();
                bool nre = AveragedPass.TryGpuRestart(o3, true, new NullReferenceException("fake: analysis bug"), out string nreNote);
                bool arg = AveragedPass.TryGpuRestart(o3, true, new ArgumentException("Nothing to simulate in this building"), out string argNote);
                Check("RW-02: a model with no obstacles and an exception from the analysis are not restarted on the CPU: no \"GPU stopped responding\", the device stays cached, the run's own message surfaces",
                    thrown2 is ArgumentException && msg2.Contains("Nothing to simulate", StringComparison.Ordinal) && o2.GpuLossNote == null && o2.Backend == Backend.Auto && !nre && !arg && nreNote.Length == 0 && argNote.Length == 0 &&
                    o3.Backend == Backend.Auto && o3.GpuLossNote == null && LbmSolver.GpuDeviceCached == cachedBefore,
                    $"empty model: {thrown2?.GetType().Name}: {msg2} · NullReferenceException restart {nre} · ArgumentException restart {arg} · device cached {LbmSolver.GpuDeviceCached} (was {cachedBefore})");
            }

            if (!gpuHere) Check("a fake accelerator exception in the GPU stepping loop: the run finishes on the CPU (needs a GPU)", true, "no GPU on this machine: not run");
            else
            {
                int calls = 0;
                LbmSolver.GpuStepTestHook = () => { if (++calls == 3) throw new InvalidOperationException("fake: CUDA_ERROR_ILLEGAL_ADDRESS"); };
                // RU-05: a cap inside the settle lands nothing now, so this run is a fixed-length one (the policy is the check, not the pass); RU-12: the restart takes the CPU budget
                var ro = new RunOptions { Quality = Quality.Preview, TargetCells = 40_000, Backend = Backend.Auto, CpuReplan = o => o.TargetCells = 20_000 };
                SimulationResult? rr = null; string err = "";
                try { rr = Simulation.Run(SampleBuilding.Build(), new Vector2(3, 0), ro); }
                catch (Exception e) { err = e.Message; }
                finally { LbmSolver.GpuStepTestHook = null; }
                bool noted = rr?.Field.Grid.Warnings.Any(w => w.Contains("Restarting this run on the CPU")) == true;
                Check("a fake accelerator exception in the GPU stepping loop: the run finishes on the CPU, once, with the message in its notes",
                    rr != null && rr.Field.Backend.StartsWith("CPU") && calls == 3 && noted && ro.Backend == Backend.Auto && ro.GpuLossNote == null,
                    rr == null ? err : $"{rr.Field.Backend} · hook calls {calls} · note {noted} · options back to {ro.Backend} · {rr.Convergence?.Status.Reason}");
                // RU-12: the restart re-planned the cell budget for the CPU (20k, not the GPU-sized 40k) and the options keep the GPU budget for the next run
                Check("RU-12: a CPU restart after a GPU fault uses the CPU cell budget, not the GPU-sized grid; the options get their budget back",
                    rr != null && rr.Field.Grid.Count < 30_000 && ro.TargetCells == 40_000,
                    rr == null ? err : $"restarted grid {rr.Field.Grid.Count} cells (CPU budget 20000, GPU budget 40000) · options.TargetCells back to {ro.TargetCells}");

                // RU-11: the app on the GPU; an edit (a Config change) restarts the session and the GPU cannot open it, in the edit's Restart and again in the recovery Restart.
                // The run falls back to the CPU once (Loop -> AveragedPass.TryGpuRestart) instead of ending as "the solver could not restart".
                var ls11 = new LiveSolver(new WindFlow.App.Viewport.ParticleSystem());
                bool fell11 = false; string why11 = "";
                try
                {
                    var house11 = SampleBuilding.Build();
                    bool WaitFor11(Func<bool> f) { var until = DateTime.UtcNow.AddSeconds(180); while (!f() && DateTime.UtcNow < until) Thread.Sleep(50); return f(); }
                    ls11.Start(house11, new Vector2(4, 0));
                    bool up11 = WaitFor11(() => ls11.State == LiveSolver.Mode.Running && ls11.Backend.Length > 0);
                    string before11 = ls11.Backend;
                    LbmSolver.GpuInitTestHook = () => throw new InvalidOperationException("fake: CUDA_ERROR_LAUNCH_FAILED");
                    ls11.Reconfigure(false, house11);
                    fell11 = up11 && !before11.StartsWith("CPU") &&
                             WaitFor11(() => ls11.GpuNote.Contains("Restarting this run on the CPU") && ls11.Backend.StartsWith("CPU") && ls11.State == LiveSolver.Mode.Running);
                    why11 = $"before \"{before11}\" · after \"{ls11.Backend}\" · state {ls11.State} · note \"{ls11.GpuNote}\" · error \"{ls11.Error}\"";
                }
                finally { LbmSolver.GpuInitTestHook = null; ls11.Stop(); }
                Check("RU-11: a GPU fault inside an app edit's Restart falls back to the CPU once (GPU note, running on the CPU), not \"could not restart\"", fell11, why11);
            }
        }

        // Room names (PLAN_V2 4.1). Detected rooms carry the detector's "Room N" placeholder: both become
        // "Room N", west one first on the same row.
        var detNames = RoomNames.Compute(new BuildingModel { Storeys = neck.Storeys, Rooms = detected.Rooms });
        Check("unnamed mesh rooms are named \"Room N\"",
            detNames.Count == 2 && detNames[0].Name == "Room 1" && detNames[1].Name == "Room 2",
            string.Join(" | ", detNames.Select(x => $"{x.Room.Id} -> {x.Name}")));

        // Duplicates: the sample house with its Kitchen renamed "Hall" and Bedroom 2 renamed "Bedroom 1".
        var dup = SampleBuilding.Build();
        dup.Rooms.First(r => r.Id == "kitchen").Name = "Hall";
        dup.Rooms.First(r => r.Id == "bed2").Name = "Bedroom 1";
        var dupNames = RoomNames.Compute(dup);
        var byId = dupNames.ToDictionary(x => x.Room.Id, x => x.Name);
        Check("room display names are unique; duplicates get a direction + area suffix",
            dupNames.Select(x => x.Name).Distinct().Count() == dupNames.Count &&
            byId["hall"] == "Hall (north-east, 15 m²)" && byId["kitchen"] == "Hall (south-east, 20 m²)" &&
            byId["bed1"].StartsWith("Bedroom 1 (west, ") && byId["bed2"].StartsWith("Bedroom 1 (south-east, ") &&
            byId["living"] == "Living" && byId["bath"] == "Bathroom",
            string.Join(" | ", dupNames.Select(x => x.Name)));

        // RENAME-01: a rename must not leak into a different load of a model with the same name and room ids
        // (sample reload, file open); only a live-link re-send of the document keeps it.
        var renMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["hall"] = "Hall Renamed" };
        string renDoc = "Sample house";
        var renFresh = SampleBuilding.Build();
        MainWindow.CarryRenames(renMap, ref renDoc, renFresh, liveResend: false);
        var renResend = SampleBuilding.Build();
        renMap["hall"] = "Hall Renamed";
        MainWindow.CarryRenames(renMap, ref renDoc, renResend, liveResend: true);
        Check("RENAME-01: a rename does not leak into a freshly loaded model with the same room ids (live re-send keeps it)",
            renFresh.Rooms.First(r => r.Id == "hall").Name == SampleBuilding.Build().Rooms.First(r => r.Id == "hall").Name &&
            renResend.Rooms.First(r => r.Id == "hall").Name == "Hall Renamed",
            $"fresh \"{renFresh.Rooms.First(r => r.Id == "hall").Name}\" · resend \"{renResend.Rooms.First(r => r.Id == "hall").Name}\"");

        // RENAME-02: a double-click whose second press falls on the list while the first press's repaint replaced the
        // rows (so no row handler runs) still renames the row just pressed; a handled, slow, far or single press does not.
        bool lostYes = MainWindow.LostDoubleClick(2, 1100, 3, 1000, 500, 0);
        bool lostNo = !MainWindow.LostDoubleClick(1, 1100, 3, 1000, 500, 0) && !MainWindow.LostDoubleClick(2, 1600, 3, 1000, 500, 0) &&
                      !MainWindow.LostDoubleClick(2, 1100, 3, 1000, 500, 40) && !MainWindow.LostDoubleClick(2, 1100, -1, 1000, 500, 0);
        Check("RENAME-02: a double-click's second press that misses the repainted rows still starts the rename; single, slow or far presses do not",
            lostYes && lostNo, $"lost second press {lostYes} · single/slow/far/no-row rejected {lostNo}");

        // Stable order: by storey, then north-to-south, west-to-east; independent of list order, and a 1 cm
        // live-link wobble does not reshuffle it.
        var shuffled = SampleBuilding.Build();
        shuffled.Rooms.Reverse();
        foreach (var loop in shuffled.Rooms.SelectMany(r => r.Loops))
            for (int i = 0; i < loop.Length; i++) loop[i] += new Vector2(0.01f, -0.01f);
        var order1 = RoomNames.Compute(SampleBuilding.Build()).Select(x => x.Room.Id).ToList();
        var order2 = RoomNames.Compute(shuffled).Select(x => x.Room.Id).ToList();
        Check("room order is stable (storey, then position), whatever the input order",
            order1.SequenceEqual(order2) && order1.SequenceEqual(["hall", "living", "kitchen", "landing", "bath", "bed1", "bed2"]),
            $"{string.Join(",", order1)} vs shuffled {string.Join(",", order2)}");

        // Particle auto-adjust with fake fps samples at a 30 fps cap: 3 s low steps down, 10 s at the cap steps
        // up, a step up that is immediately taken back doubles the next wait, and the visual ceiling holds.
        var auto = new ParticleAuto(ParticleAuto.VisualCeiling);
        int start = auto.Count, down = 0, upTo = 0, second = 0;
        string why = "";
        int Feed(double seconds, float fps)
        {
            int changed = 0;
            for (double t = 0; t < seconds; t += 0.15)
                if (auto.Update(0.15, fps, 30, out var r) is > 0 and var c) { changed = c; why = r; }
            return changed;
        }
        Feed(2.5, 20);                              // 2.5 s low: not yet
        bool heldEarly = auto.Count == start;
        down = Feed(1.0, 20);                       // past 3 s: one step down
        upTo = Feed(10.5, 30);                      // 10 s of headroom: back up to the ceiling
        Feed(3.2, 22);                              // taken back again...
        second = Feed(12, 30);                      // ...so 10 s is no longer enough (20 s now)
        bool backedOff = second == 0;
        Feed(10, 30);
        bool ceiling = auto.Count == start;
        Feed(60, 30);                               // never above the visual ceiling
        Check("particle auto-adjust: down after 3 s under 90 % of the cap, up after 10 s, backs off, capped",
            heldEarly && down == ParticleAuto.Steps[Array.IndexOf(ParticleAuto.Steps, start) - 1] && upTo == start &&
            backedOff && ceiling && auto.Count == start,
            $"start {start} · down {down} · up {upTo} · retry after 12 s {second} · final {auto.Count} · last reason \"{why}\"");

        // Startup tier table: an injected low probe (what an iGPU through OpenCL would read) picks the low row.
        var low = StartupTier.Pick(80, 0.5, "injected", "selfcheck");
        var high = StartupTier.Pick(2000, 16, "injected", "selfcheck");
        Check("startup tier: 80 MLUPS / 0.5 GB -> low, 2000 MLUPS / 16 GB -> high",
            low.Name == "low" && high.Name == "high" && high.Particles <= ParticleAuto.VisualCeiling,
            $"{low.Name} {low.TargetCells} cells {low.Particles} particles {low.FrameCap} fps · {high.Name} {high.TargetCells} {high.Particles} {high.FrameCap}");

        // Preview, then Accurate (Accurate.cs): the tracker the solver thread drives, fed by hand.
        {
            var t = new AccurateTracker();
            var cancelledIn = new List<string>();
            foreach (var stop in new[] { AccuratePhase.Settling, AccuratePhase.Averaging, AccuratePhase.Finishing, AccuratePhase.Accurate })
            {
                t.Begin();
                if (stop != AccuratePhase.Settling) t.Update(600, 400, 1000, 300, 5);
                if (stop is AccuratePhase.Finishing or AccuratePhase.Accurate) t.Finishing(5);
                if (stop == AccuratePhase.Accurate) t.Done();
                int before = t.Cycle;
                bool reached = t.Phase == stop;
                t.Edit();
                if (reached && t.Phase == AccuratePhase.Preview && t.Progress == 0 && t.Cycle == before + 1 && !t.Running &&
                    double.IsNaN(t.EtaSeconds)) cancelledIn.Add(stop.ToString());
            }
            Check("Accurate: an edit cancels the pass in every phase and restarts the cycle from Preview",
                cancelledIn.Count == 4, $"cancelled from {string.Join(", ", cancelledIn)}");

            // SETTLING-01: "settling…" only for Preview numbers, while the pass runs, after an edit; Edit sets the flag, a landed pass clears it.
            int settlingOn = 0;
            for (int bits = 0; bits < 8; bits++)
                if (AccurateTracker.ShowSettling((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0)) settlingOn++;
            var sf = new AccurateTracker();
            sf.Edit(); bool afterEdit = sf.EditedSinceAccurate;
            sf.Begin(); sf.Update(600, 400, 1000, 300, 5); sf.Finishing(5); sf.Done();
            Check("SETTLING-01: settling shows only for Preview stats + Accurate running + edited since Accurate; Edit sets, Done clears",
                settlingOn == 1 && AccurateTracker.ShowSettling(true, true, true) && afterEdit && !sf.EditedSinceAccurate,
                $"{settlingOn} of 8 combinations, afterEdit {afterEdit}, afterDone {sf.EditedSinceAccurate}");

            // RA-20: an unrecoverable solver error during a pass drops the Accurate phase (Fail), so the rows stop reading "settling…".
            var sx = new AccurateTracker();
            sx.Edit(); sx.Begin(); sx.Fail();
            Check("RA-20: a solver error mid-pass leaves the Accurate phase (not Running, label Preview)",
                !sx.Running && sx.ResultLabel == "Preview", $"running {sx.Running}, label {sx.ResultLabel}");

            // Progress under a messy feed: the step count stalls and the plan grows mid-pass (a slower rate, a
            // longer average), which would pull a naive fraction backwards.
            var p = new AccurateTracker();
            p.Begin();
            var seen = new List<double>();
            (long Steps, long Total, double Rate)[] feed =
                [(0, 1000, 0), (100, 1000, 300), (400, 1000, 280), (400, 1600, 150), (500, 1600, 150), (900, 1600, 290), (1600, 1600, 300)];
            foreach (var (steps, total, rate) in feed) { p.Update(steps, 300, total, rate, 4); seen.Add(p.Progress); }
            p.Finishing(4); seen.Add(p.Progress);
            p.Update(10, 300, 1600, 300, 4); seen.Add(p.Progress);     // a stale reading after Finishing: ignored
            p.Done(); seen.Add(p.Progress);
            bool monotone = seen.Zip(seen.Skip(1), (a, b) => b >= a).All(x => x);
            Check("Accurate: progress never decreases inside a pass, ends at 100 %",
                monotone && seen[^1] == 1 && seen[0] == 0,
                string.Join(" ", seen.Select(v => $"{v * 100:0.#}")));

            // Labels follow the state: only a finished pass is "Accurate"; the ETA is "…" whenever it is unknown.
            var phases = Enum.GetValues<AccuratePhase>();
            var l = new AccurateTracker();
            var labels = new List<string>();
            l.Begin(); labels.Add($"{l.Phase}={l.ResultLabel}");
            l.Update(500, 300, 1000, 300, 4); labels.Add($"{l.Phase}={l.ResultLabel}");
            l.Finishing(4); labels.Add($"{l.Phase}={l.ResultLabel}");
            l.Done(); labels.Add($"{l.Phase}={l.ResultLabel}");
            l.Edit(); labels.Add($"{l.Phase}={l.ResultLabel}");
            l.Off("box"); labels.Add($"{l.Phase}={l.ResultLabel}");
            bool labelOk = labels.SequenceEqual(["Settling=Preview", "Averaging=Preview", "Finishing=Preview", "Accurate=Accurate",
                                                 "Preview=Preview", "Off=Preview"]);
            string settle = AccurateTracker.StageText(AccuratePhase.Settling, 0.1, double.NaN, "");
            string avg = AccurateTracker.StageText(AccuratePhase.Averaging, 0.64, 38, "");
            string done = AccurateTracker.StageText(AccuratePhase.Accurate, 1, 0, "");
            // S6-03 / S6-07: the pass strip says "Averaged result" and never ticks by phase alone (the tick comes from ResultStatus.StripText, only when Settled).
            bool words = settle == "Averaged result · letting the air settle… · …" &&
                         avg == "Averaged result · averaging · 64 % · about 40 s left" && done == "Averaged result" &&
                         AccurateTracker.Left(double.NaN) == "…" && AccurateTracker.Left(-1) == "…" &&
                         AccurateTracker.Left(125) == "about 3 min left";
            Check("Accurate: Preview/Accurate labels follow the state; ETA shows … when unknown",
                labelOk && words, $"{string.Join(" ", labels)} · \"{settle}\" · \"{avg}\" · \"{done}\"");

            // The budget: the tier row as is on a 16 GB card, clamped on a 2 GB one next to a 1.5 M Preview grid.
            var big = AccurateBudget.Resolve(0, high.AccurateCells, 16, 1_500_000, false);
            var small = AccurateBudget.Resolve(0, high.AccurateCells, 1, 1_500_000, false);
            var typed = AccurateBudget.Resolve(100_000_000, high.AccurateCells, 16, 1_500_000, false);
            Check("Accurate grid: tier budget (high 5M), clamped to what VRAM holds beside the Preview grid (1 GB card, 100M typed)",
                big == (5_000_000, false) && small.Clamped && small.Cells < 5_000_000 && typed.Clamped &&
                typed.Cells == AccurateBudget.VramCap(16, 1_500_000, false),
                $"16 GB {AccurateBudget.Fmt(big.Cells)} · 1 GB {AccurateBudget.Fmt(small.Cells)} (clamped {small.Clamped}) · typed 100M on 16 GB -> " +
                $"{AccurateBudget.Fmt(typed.Cells)} · low tier {AccurateBudget.Fmt(low.AccurateCells)}");

            // RQ-04: the typed floor is 1.5 x the Preview TARGET (1.5 M -> 2.25 M), not the Full grid's cell count (~15 M); the VRAM cap still wins.
            int t4 = AccurateBudget.Typed(4_000_000, 1_500_000, 20_000_000, out string n4), t1 = AccurateBudget.Typed(1_000_000, 1_500_000, 20_000_000, out string n1), tc = AccurateBudget.Typed(1_000_000, 1_500_000, 2_000_000, out string nc);
            Check("Accurate budget box: with a Full wind grid (~15M cells) a typed 4M is kept (floor is 1.5 x the 1.5M Preview target); 1M is raised to 2.25M; the VRAM cap overrides the floor",
                t4 == 4_000_000 && n4 == "" && t1 == 2_250_000 && n1.StartsWith("raised to 2.", StringComparison.Ordinal) && tc == 2_000_000 && nc.StartsWith("clamped to 2M", StringComparison.Ordinal),
                $"4M -> {AccurateBudget.Fmt(t4)} · 1M -> {AccurateBudget.Fmt(t1)} \"{n1}\" · 1M under a 2M cap -> {AccurateBudget.Fmt(tc)} \"{nc}\"");
        }

        // Nested box blend band: an opening 0.3 m in from the -X face, and a door straddling the top face, are in a
        // 1.15 m band; one in the middle is not; Grow clears both (the bottom stands on the ground: no band there).
        var bandBox = new DomainBox(new Vector3(0, 0, 0), new Vector3(10, 10, 4));
        (Vector3, Vector3) nearFace = (new Vector3(0.2f, 4, 1), new Vector3(0.4f, 5, 2)),
                           straddle = (new Vector3(6, 4, 3.5f), new Vector3(7, 4.3f, 5.5f)),
                           middle = (new Vector3(4.8f, 4.8f, 1), new Vector3(5.2f, 5.2f, 2));
        bool flagged = BlendBand.Inside(bandBox, nearFace, 1.15f, false, true) && BlendBand.Inside(bandBox, straddle, 1.15f, false, true) &&
                       !BlendBand.Inside(bandBox, middle, 1.15f, false, true);
        var grown = BlendBand.Grow(bandBox, [nearFace, straddle, middle], 1.15f, 0.13f, false, true, 0f);
        bool cleared = !BlendBand.Inside(grown, nearFace, 1.15f, false, true) && !BlendBand.Inside(grown, straddle, 1.15f, false, true) &&
                       grown.Min.Z == 0f;
        Check("blend band: near-face and straddling openings flagged, middle not; Grow box clears both",
            flagged && cleared, $"flagged {flagged} · grown {grown.Min} -> {grown.Max} · cleared {cleared}");

        // Basic's air-age column: pending, minutes with a decimal under 10 min, whole minutes, and a closed room.
        string[] ages = [MainWindow.AgeText(float.NaN), MainWindow.AgeText(30), MainWindow.AgeText(169),
                         MainWindow.AgeText(900), MainWindow.AgeText(86400)];
        Check("air age column: NaN -> …, 30 s -> 0.5 min, 169 s -> 2.8 min, 900 s -> 15 min, a day -> > 1 h",
            ages.SequenceEqual(["…", "0.5 min", "2.8 min", "15 min", "> 1 h"]), string.Join(" | ", ages));

        // PLAN_V2 4.2: openings for a plain mesh. The sample house written as a glTF with every opening cut as a real
        // hole, imported back, and the detected openings matched against the sample's own: count, centre and size
        // each within one cell, class right. CPU only (Assimp + the voxeliser), so it runs here, not as a bench.
        var trip = OpeningRoundTrip(null);
        Check("mesh openings round trip: count and centre within one cell, size within 2 cm (S3-02), class right", trip.Ok, trip.Summary);
        lines.AddRange(trip.Table);
        // The merge rule: a mullion down the middle of the Living south window, inside one cell, must not
        // split it into two openings. IMP-B D1 (rule 3): the cell is now 50 mm, so the mullion is 60 mm only when that fits in one cell, else 0.8 of a cell (40 mm); it stays inside one cell.
        var mullionX = trip.Origin.X + (MathF.Floor((3.5f - trip.Origin.X) / trip.Dx) + 0.5f) * trip.Dx;
        float mullionHalf = MathF.Min(0.03f, 0.4f * trip.Dx);
        var mullion = OpeningRoundTrip([(new Vector3(mullionX - mullionHalf, -0.125f, 0.85f), new Vector3(mullionX + mullionHalf, 0.125f, 2.15f))]);
        var livingSouth = mullion.Table.FirstOrDefault(l => l.Contains("Living south window")) ?? "";
        Check("mesh openings: a window split by a one-cell mullion is one opening", mullion.Ok && mullion.Joined >= 1,
            $"mullion at x = {mullionX:0.000} m · {mullion.Summary} · joined {mullion.Joined} · {livingSouth.Trim()}");

        // IMP-A D3: an opening's "(A to B)" names the rooms it connects, by the rooms' own names (Room.Name, which the JSON, the solve text
        // and the GUI show). Each detected opening of the sample-house round trip: the rooms found a step either side of it along its normal
        // are the "Room N" the name cites. D10/leak net: the sample house gets no unlisted-gap or leak note.
        {
            static bool InRoom(Room r, float px, float py)
            {
                bool inside = false;
                foreach (var loop in r.Loops)
                    for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                        if ((loop[i].Y > py) != (loop[j].Y > py) && px < (loop[j].X - loop[i].X) * (py - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X) inside = !inside;
                return inside;
            }
            int checkedNames = 0, interior = 0, exterior = 0; var badNames = new List<string>();
            foreach (var o in trip.Detected.Openings)
            {
                // the rooms on each side of the opening along its normal (same storey), by geometry: no name involved
                var sides = new List<string>();
                foreach (int sgn in new[] { -1, 1 })
                    for (float d = 0.2f; d <= 2.0f; d += 0.15f)
                    {
                        var hit = trip.Detected.Rooms.FirstOrDefault(r => r.Level == o.Level && InRoom(r, o.Center.X + sgn * o.Normal.X * d, o.Center.Y + sgn * o.Normal.Y * d));
                        if (hit != null) { sides.Add(hit.Id); break; }
                    }
                checkedNames++;
                if (sides.Count == 2 && sides[0] != sides[1]) interior++;
                else if (sides.Count == 1) exterior++;
                else badNames.Add($"{o.Name}: rooms on its two sides [{string.Join(",", sides)}]");
            }
            Check("IMP-A D3: every detected opening sits on the boundary of two different detected rooms or of one room and the outside (rooms found on each side by geometry)",
                checkedNames > 0 && badNames.Count == 0, $"{checkedNames} openings checked ({interior} between two rooms, {exterior} room to outside) · {(badNames.Count == 0 ? "all agree" : string.Join(" | ", badNames))}");
            Check("IMP-A D10 / leak net: the sample house import has no unlisted-gap or open-to-outside note",
                !trip.Detected.Notes.Any(n => n.Contains("not recognised as openings", StringComparison.Ordinal)), string.Join(" | ", trip.Detected.Notes));
        }

        string d10Note = "Room 6: 99 % of its air exchange passes through gaps that are not recognised as openings; total exchange excludes them.";
        Check("IMP-A D10: the unlisted-gap note has its own plain sentence", Notes.Plain(d10Note) != Notes.Plain("zzz unknown note") && Notes.Plain(d10Note).StartsWith("Room 6 ", StringComparison.Ordinal), Notes.Plain(d10Note));

        // IMP-A D7: repeated detection notes become one with a count and where; distinct ones stay.
        {
            var d7 = new List<string> { "Level 1: 3 rooms at z = 1.30 m (200 covered cells of 300 air, 5 regions)" };
            for (int i = 0; i < 40; i++) d7.Add($"Level {1 + i % 3}: a {2.5 + i * 0.1:0.#} m² region is at most two cells ({0.5 + i * 0.01:0.##} m) wide and vanished when inset from its walls");
            var d7r = RoomDetect.AggregateNotes(d7);
            Check("IMP-A D7: 40 repeated detection notes aggregate to one with the count; a distinct note stays",
                d7r.Count == 2 && d7r[1].Contains("(40 times", StringComparison.Ordinal) && d7r[0] == d7[0], string.Join(" | ", d7r));
        }

        // IMP-A D2/D12/D5: storeys ignore terrain-like ground beyond the walls (and say so), an OBJ's up axis is tried both ways.
        {
            var house = SampleBuilding.Build();
            var plain = new ImportSettings { YUpToZUp = false };
            var base2 = MeshImport.GuessStoreys(house.Obstacles, plain);
            var terrain = new WindFlow.Core.Mesh
            {
                Id = "terrain", Vertices = [new(30, -50, 0.5f), new(100, -50, 0.5f), new(100, 50, 0.5f), new(30, 50, 0.5f)],
                Indices = [0, 1, 2, 0, 2, 3],
            };
            var withSite = new ImportSettings { YUpToZUp = false };
            var site2 = MeshImport.GuessStoreys(house.Obstacles.Append(terrain).ToList(), withSite);
            Check("IMP-A D2/D12: a large terrain surface beside the building does not change the storeys, and the import notes say it was ignored",
                base2.Count > 0 && base2.SequenceEqual(site2) && withSite.Notes.Any(n => n.StartsWith("ground/site surface ignored", StringComparison.Ordinal)),
                $"{string.Join(" | ", base2.Select(s => $"{s.Elevation:0.00}+{s.Height:0.00}"))} vs {string.Join(" | ", site2.Select(s => $"{s.Elevation:0.00}+{s.Height:0.00}"))} · {string.Join(" | ", withSite.Notes)}");

            ImportedGeometry AsObj(Func<Vector3, Vector3> map)
            {
                var ms = house.Obstacles.Select(m => new WindFlow.Core.Mesh { Id = m.Id, Vertices = m.Vertices.Select(map).ToArray(), Indices = m.Indices }).ToList();
                var all = ms.SelectMany(m => m.Vertices).ToList();
                return new ImportedGeometry("fixture.obj", ms, all.Aggregate(Vector3.Min), all.Aggregate(Vector3.Max), 0, 0f);
            }
            bool yFile = MeshImport.GuessYUp(AsObj(v => new Vector3(v.X, v.Z, -v.Y)), 1f, out _);     // a Y-up file
            bool zFile = MeshImport.GuessYUp(AsObj(v => v), 1f, out string zWhy);                       // a Z-up file (IfcConvert)
            string objPath = Path.Combine(Path.GetTempPath(), $"windflow-selfcheck-d5-{System.Environment.ProcessId}.obj");
            GltfWriter.WriteTestHouseObj(objPath);
            bool sampleObj = MeshImport.GuessYUp(MeshImport.Load(objPath), 1f, out _);
            File.Delete(objPath);
            Check("IMP-A D5: an OBJ in Y-up stays Y-up (also the sample house .obj export), an OBJ in Z-up is read as Z-up",
                yFile && !zFile && sampleObj, $"Y-up file → {(yFile ? "Y-up" : "Z-up")} · Z-up file → {(zFile ? "Y-up" : "Z-up")} ({zWhy}) · sample .obj → {(sampleObj ? "Y-up" : "Z-up")}");
        }

        // Notes: repeated analysis and air-age passes on an unchanged model must not grow the notes list. The live
        // grid is shared by every pass and Core/ScalarSolver append to its Warnings; this is the app's own path
        // (LiveSolver.SolveAge + AnalyseFields + Notes.Gather), four age solves interleaved with analysis-only passes,
        // on a still field (no fresh air anywhere, so the air-age "no steady state" note fires every solve).
        {
            var nm = SampleBuilding.Build();
            var grid = Voxelizer.Build(nm, 40_000, System.Environment.ProcessorCount, null);
            int n = grid.Count;
            var field = new FlowField { Grid = grid, Ux = new float[n], Uy = new float[n], Uz = new float[n], P = new float[n] };
            var o = new RunOptions();
            var w = SolverRun.WindVector(270, 4);
            var counts = new List<int>();
            var warnCounts = new List<int>();
            ScalarFields? age = null;
            for (int pass = 0; pass < 8; pass++)
            {
                if (pass % 2 == 0) age = LiveSolver.SolveAge(nm, field, o);
                var r = LiveSolver.AnalyseFields(nm, field, null, w, o, out _, age);
                counts.Add(Notes.Gather([], r).Count);
                warnCounts.Add(grid.Warnings.Count);
            }
            Check("notes: repeated analysis + air-age passes on an unchanged model do not grow the notes list",
                counts[0] > 0 && counts.All(c => c == counts[0]) && warnCounts.All(c => c == warnCounts[0]),
                $"{n / 1000}k cells · notes per pass {string.Join(",", counts)} · grid warnings {string.Join(",", warnCounts)}");
        }

        // Batch 5: every note written since the plain-words table (RY-05 gap, opening Cd low and +-X, stratification, device-room hold, GPU fallback and loss, a vanished patch, a narrow supply
        // patch, skipped non-finite samples) has its own Basic sentence, none falls to the generic line; the vanished-patch note comes from a real build (a 1 cm patch on cell faces).
        {
            string b5generic = Notes.Plain("zzz unknown note");
            var b5opening = new Opening { Id = "w", Name = "W1", Width = 0.4f, Height = 0.4f, Depth = 0.1f };
            var b5model = SampleBuilding.Build();
            var b5g0 = Voxelizer.Build(b5model, 40_000, System.Environment.ProcessorCount, null);
            b5model.Sources.Add(new AirSource { Id = "tiny", Name = "Tiny supply", Level = "L1", Kind = SourceKind.Supply, Device = DeviceKind.Patch, Direction = -Vector3.UnitZ, Width = 0.01f, Height = 0.01f, FlowRate = 0.1f,
                Center = new Vector3(b5g0.Origin.X + 8 * b5g0.Dx, b5g0.Origin.Y + 8 * b5g0.Dx, b5g0.Origin.Z + 8 * b5g0.Dx) });
            var b5g = Voxelizer.Build(b5model, 40_000, System.Environment.ProcessorCount, null);
            string b5vanished = b5g.Warnings.FirstOrDefault(w => w.Contains("its patch is smaller than one cell", StringComparison.Ordinal)) ?? "";
            string[] b5notes =
            [
                "Closet connects only through a gap narrower than about 2 cells at this cell size; its age of air cannot be computed and shows as sealed. Use a finer quality level or widen the gap.",
                "Opening W1: the lattice resolves this opening at Cd 0.58, below the requested 0.61; flow here may be about 5 % low",
                "Opening W1: " + (Analysis.OpeningCdScatterNote(b5opening, 0.1f) ?? "(no scatter note)"),
                "Living: room air held at 22 °C on average; occupied zone 24.1 °C (stratification)",
                "Bedroom is conditioned by its Split AC 1; the ideal hold is off there.",
                "GPU not available (no GPU device found); running on the CPU — this is much slower",
                "CUDA unavailable (no CUDA devices found); using OpenCL.",
                "The GPU stopped responding (fake). Restarting this run on the CPU.",
                b5vanished,
                "Source Supply 1: its supply patch is only 3 cells across (under 4), so the turbulence it delivers is below the requested 19 %; use a finer quality level or a larger patch.",
                FlowFieldMean.NonFiniteTag + "2 non-finite (NaN or infinite) samples were left out of the time average; the mean is biased or missing in 2 cells",
            ];
            var b5bad = b5notes.Where(n => n.Length == 0 || Notes.Plain(n) == b5generic || Notes.Plain(n).Contains("Expert", StringComparison.Ordinal)).ToList();
            string b5storey = MeshImport.StoreyHeightNote([new StoreyPlan(0, 2.0f), new StoreyPlan(3, 2.9f), new StoreyPlan(6, 7.5f)]);
            Check("batch 5: each new note has a plain sentence (RY-05 gap, Cd low, +-X, stratification, device hold, GPU x3, vanished patch, narrow Tu patch, skipped samples); the storey-height hint names heights outside 2.2-6 m only",
                b5bad.Count == 0 && b5g.Dx == b5g0.Dx && b5storey.Contains("2.0, 7.5 m", StringComparison.Ordinal) && !b5storey.Contains("2.9", StringComparison.Ordinal) && MeshImport.StoreyHeightNote([new StoreyPlan(0, 2.7f)]).Length == 0,
                b5bad.Count == 0 ? $"{b5notes.Length} notes mapped · \"{b5vanished}\" · \"{b5storey.Trim()}\"" : "no plain sentence for: " + string.Join(" | ", b5bad.Select(n => n.Length == 0 ? "(vanished-patch note missing)" : n[..Math.Min(60, n.Length)])));
        }

        // Batch 5 (6l disclosure): with a fine box on, room ACH and opening flows are the whole-building run's, not the box's. A fine field over the whole domain at a finer Dx
        // (every room inside it) with a different flow must leave every room's Ach / Inflow and every opening's FlowRate exactly as the coarse-only analysis has them.
        {
            var b5m = SampleBuilding.Build();
            var b5c = Voxelizer.Build(b5m, 40_000, System.Environment.ProcessorCount, null);
            var b5f = Voxelizer.Build(b5m, 160_000, System.Environment.ProcessorCount, null);
            FlowField b5Uniform(VoxelGrid g, float ux) { int c = g.Count; var f = new FlowField { Grid = g, Ux = Enumerable.Repeat(ux, c).ToArray(), Uy = new float[c], Uz = new float[c], P = new float[c] }; return f; }
            var b5o = new RunOptions();
            var b5w = SolverRun.WindVector(270, 4);
            var b5coarse = LiveSolver.AnalyseFields(b5m, b5Uniform(b5c, 0.1f), null, b5w, b5o, out _);
            var b5with = LiveSolver.AnalyseFields(b5m, b5Uniform(b5c, 0.1f), b5Uniform(b5f, 0.4f), b5w, b5o, out var b5box);
            bool b5rooms = b5coarse.Rooms.Count == b5with.Rooms.Count && b5coarse.Rooms.Zip(b5with.Rooms).All(p => p.First.Ach == p.Second.Ach && p.First.Inflow == p.Second.Inflow && !p.Second.FromFineBox);
            bool b5open = b5coarse.Openings.Count == b5with.Openings.Count && b5coarse.Openings.Zip(b5with.Openings).All(p => p.First.FlowRate == p.Second.FlowRate);
            bool b5diff = b5box != null && b5box.Openings.Zip(b5coarse.Openings).Any(p => MathF.Abs(p.First.FlowRate - p.Second.FlowRate) > 1e-3f);   // the test is not vacuous: the box's own numbers are different
            Check("6l disclosure: with a fine box, room ACH and opening flows stay the whole-building run's (the box's own numbers differ, and are not shown as results)",
                b5rooms && b5open && b5diff, $"{b5with.Rooms.Count} rooms · {b5with.Openings.Count} openings · rooms same {b5rooms} · openings same {b5open} · box differs {b5diff}");

            // RW-08: a device the box answers for loses the coarse ledger's "moved N % of its set flow" note; another device's note stays
            var rw8 = new SimulationResult();
            rw8.Ledger.Notes.Add("Device Fan A moved 40% of its set flow (0.1 of 0.25 m3/s): too coarse a grid, or too little room around it");
            rw8.Ledger.Notes.Add("Device Fan B moved 50% of its set flow (0.1 of 0.2 m3/s): too coarse a grid, or too little room around it");
            Analysis.DropDeviceMovedNote(rw8, new AirSource { Id = "a", Name = "Fan A" });
            Check("RW-08: the coarse 'Device X moved N % of its set flow' note is dropped for a device the fine box answers for; other devices' notes stay",
                rw8.Ledger.Notes.Count == 1 && rw8.Ledger.Notes[0].StartsWith("Device Fan B moved ", StringComparison.Ordinal), string.Join(" | ", rw8.Ledger.Notes));
        }

        // Convergence-driven Accurate progress (Accurate.UpdateConverging, fed by Core's ConvergenceMonitor): the bar never
        // goes back through a messy feed, the ETA is "…" until a CI exists and never exceeds what the cap leaves.
        {
            var mon = new ConvergenceMonitor();
            var rng = new Random(7);
            var t = new AccurateTracker();
            t.Begin();
            var bar = new List<double>();
            bool etaHonest = true, sawUnknown = false, sawKnown = false;
            long steps = 0;
            const long settle = 300;
            for (; steps < settle; steps += 100)
            {
                t.UpdateConverging(steps, settle, 0, 0, mon.MinBatches, double.NaN, double.NaN, 600, double.NaN);
                bar.Add(t.Progress);
                etaHonest &= double.IsNaN(t.EtaSeconds);
            }
            int batches = 0;
            for (double capLeft = 600; batches < 40; batches++, capLeft -= 30)
            {
                // A drifting start (first batches climb), then noise around 10 ACH: a wide CI first, a narrow one later.
                float ach = 10f + 4f * MathF.Exp(-batches / 2f) + (float)(rng.NextDouble() - 0.5) * 1.2f;
                mon.AddBatch([("a", ach), ("b", ach * 0.5f)]);
                var st = mon.Evaluate();
                double worst = st.Rooms.Count == 0 ? double.NaN : st.Rooms.Max(r => r.Tolerance > 0 ? r.StopHalfWidth / r.Tolerance : double.PositiveInfinity);   // P16-08: the width the stop rule tests
                t.UpdateConverging(steps, settle, mon.BatchCount, mon.TransientBatches, mon.MinBatches, worst, 30, capLeft, 4);
                bar.Add(t.Progress);
                double eta = t.EtaSeconds;
                if (double.IsNaN(eta)) sawUnknown = true;
                else { sawKnown = true; etaHonest &= eta >= 0 && eta <= Math.Max(0, capLeft) + 4 + 1e-6; }
                if (st.Reason != ConvergenceStopReason.Running) break;
            }
            t.Finishing(4); bar.Add(t.Progress);
            t.Done(); bar.Add(t.Progress);
            bool mono = bar.Zip(bar.Skip(1), (a, b) => b >= a).All(x => x);
            double f0 = AccurateTracker.ConvergedFraction(0, 0, double.NaN, 5, out _), f1 = AccurateTracker.ConvergedFraction(12, 2, 0.8, 5, out double need1),
                   f2 = AccurateTracker.ConvergedFraction(8, 2, 3.0, 5, out double need2);
            bool frac = f0 == 0 && f1 == 0.75 && need1 == 16 && f2 > 0 && f2 < 1 && need2 > 8;
            string text = AccurateTracker.StageText(AccuratePhase.Averaging, 0.5, double.NaN, "", false, "batch 4 · worst room ±9 % (aim 5 %)");
            bool words = text == "Averaged result · averaging · 50 % · … · batch 4 · worst room ±9 % (aim 5 %)";   // S6-07 rename
            Check("Accurate: convergence-driven progress never decreases; ETA is … until a CI exists and never passes the cap",
                mono && etaHonest && sawUnknown && sawKnown && frac && words && bar[^1] == 1,
                $"{batches + 1} batches · {bar.Count} samples · {string.Join(" ", bar.Where((_, i) => i % 3 == 0).Select(v => $"{v * 100:0}"))} % · " +
                $"fraction(12 batches, CI met)={f1:0.##} · fraction(8, ratio 3)={f2:0.##} of {need2:0.#} needed · {text}");
        }

        // S3-08: the one numeric-box parser: '.' or ',' is the decimal mark, two separators, NaN/infinity and out-of-range refused.
        {
            bool ok = NumInput.TryParse("4,5", 0, 40, out double a) && a == 4.5 && NumInput.TryParse("4.5", 0, 40, out double b) && b == 4.5 &&
                      NumInput.TryParse(" 22,5 ", -30, 60, out double c) && c == 22.5;
            bool bad = !NumInput.TryParse("1,2,3", out _) && !NumInput.TryParse("1,000.5", out _) && !NumInput.TryParse("NaN", out _) &&
                       !NumInput.TryParse("Infinity", out _) && !NumInput.TryParse("-∞", out _) && !NumInput.TryParse("", out _) &&
                       !NumInput.TryParse("abc", out _) && !NumInput.TryParse("45", 0, 40, out _) && !NumInput.TryParse("-0,1", 0, 40, out _);
            bool wrap = NumInput.WrapDegrees(-90) == 270 && NumInput.WrapDegrees(450) == 90 && NumInput.WrapDegrees(360) == 0 && NumInput.WrapDegrees(270) == 270;
            Check("numeric boxes: \"4,5\" and \"4.5\" read 4.5, \"1,2,3\" / NaN / infinity / 45 in a 0-40 field are refused, bearings wrap to [0, 360)",
                ok && bad && wrap, $"ok {ok}, refused {bad}, wrap {wrap}");
        }

        // P13b-05: the report prints the solved wind as the compass bearing it comes FROM, the way the Wind boxes take it.
        {
            string w270 = WindFlow.Report.ReportWind.Text(SolverRun.WindVector(270, 4));
            string w0 = WindFlow.Report.ReportWind.Text(SolverRun.WindVector(0, 2.5));
            string w225 = WindFlow.Report.ReportWind.Text(SolverRun.WindVector(225, 3));
            string w90 = WindFlow.Report.ReportWind.Text(SolverRun.WindVector(90, 1));
            Check("report wind: WindVector(270, 4) prints \"4.0 m/s from 270° (W)\" (was \"0°\"), N/SW/E likewise",
                w270 == "4.0 m/s from 270° (W)" && w0 == "2.5 m/s from 0° (N)" && w225 == "3.0 m/s from 225° (SW)" && w90 == "1.0 m/s from 90° (E)",
                $"{w270} · {w0} · {w225} · {w90}");
        }

        // P13a-02: the inspector's "turb" field acts only on link-wise supply patches (Revit air terminals), never on a palette device.
        {
            var palette = Enum.GetValues<DeviceKind>().Where(k => k != DeviceKind.Patch).ToList();
            bool paletteHidden = palette.Count > 0 && palette.All(k => !Devices.TurbulenceApplies(new AirSource { Device = k, Kind = SourceKind.Supply }));
            bool patchShown = Devices.TurbulenceApplies(new AirSource { Device = DeviceKind.Patch, Kind = SourceKind.Supply });
            bool returnHidden = !Devices.TurbulenceApplies(new AirSource { Device = DeviceKind.Patch, Kind = SourceKind.Return });
            Check("device inspector: the turb field shows for a Revit air-terminal supply patch only, not for the " + palette.Count + " palette device kinds",
                paletteHidden && patchShown && returnHidden, $"palette hidden {paletteHidden}, patch supply shown {patchShown}, patch return hidden {returnHidden}");
        }

        // S5-05 / S5-01: one disclaimer wording (app About section, README, PDF footer), and the licence notices sit next to the exe.
        {
            const string wording = "WindFlow is a design-stage airflow tool. Its results are estimates with stated uncertainty, not a substitute for a ventilation engineer or a code-compliance calculation.";
            string readme = Path.Combine(Options.RepoRoot(), "README.md");
            bool inReadme = File.Exists(readme) && File.ReadAllText(readme).Contains(wording);
            string notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            bool shipped = File.Exists(notices) && File.ReadAllText(notices).Contains("Microsoft.Extensions.DependencyModel");
            Check("disclaimer wording is one string (README carries it; the About section and PDF footer read WindFlow.Report.Disclaimer.Text); THIRD-PARTY-NOTICES.txt ships beside the exe",
                WindFlow.Report.Disclaimer.Text == wording && inReadme && shipped, $"README {inReadme}, notices beside exe {shipped} ({notices})");
        }

        // Opening Cd (Expert): the presets, the parser, and the write to Opening.DischargeCoefficient that Core's calibrated
        // opening model reads; a Cd edit is flow-changing (UiTable row) and survives the solver's model snapshot.
        {
            var cm = SampleBuilding.Build();
            var win = cm.Openings.First(o => o.Kind == OpeningKind.Window);
            var cdDoor = cm.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door);
            bool presets = OpeningCd.Presets.Select(p => p.Cd).ToArray() is { Length: 4 } cds && cds[0] == 0.61f && cds[1] == 0.65f && cds[2] == 0.4f && cds[3] == 0.3f &&
                           string.Join(",", OpeningCd.Presets.Where(p => p.Typical).Select(p => p.Name)) == "open door,top-hung window,louvre";
            bool parse = OpeningCd.TryParse("Cd 0.4", out float a) && a == 0.4f && OpeningCd.TryParse("0,3", out float b) && b == 0.3f &&
                         !OpeningCd.TryParse("0", out _) && !OpeningCd.TryParse("1.5", out _) && !OpeningCd.TryParse("abc", out _) &&
                         !OpeningCd.TryParse("NaN", out _);
            bool before = OpeningCd.Effective(win) == 0.61f && win.DischargeCoefficient == null;
            bool changed = OpeningCd.Apply(win, 0.4f) && win.DischargeCoefficient == 0.4f && OpeningCd.Name(0.4f) == "(top-hung window, typical)";
            var cdSnap = LiveSolver.Snapshot(cm);
            var snapWin = cdSnap.Openings.First(o => o.Id == win.Id);
            bool carried = snapWin.DischargeCoefficient == 0.4f && OpeningCd.Effective(snapWin) == 0.4f;
            bool same = !OpeningCd.Apply(win, 0.4f);                                   // no change, no restart
            bool back = OpeningCd.Apply(win, 0.61f) && win.DischargeCoefficient == null && OpeningCd.Name(0.61f) == "(sharp-edged opening)";
            bool custom = OpeningCd.Name(0.5f) == "(custom)";
            bool flow = UiTable.Rules.Any(r => r.Name == "OpeningModelRow" && r.ExpertOnly && r.ChangesFlow) &&
                        UiTable.FlowControls.Contains("OpeningModelToggle") && UiTable.ExpertControls.Contains("OpeningModelToggle");
            bool note = Notes.Plain(Notes.CalibratedTechnical) == "Windows and doors use a calibrated discharge model (Cd 0.61) because the grid is coarse";
            bool vent = !OpeningCd.Editable(new Opening { Kind = OpeningKind.Opening });
            Check("opening Cd: presets 0.61/0.65/0.4/0.3, typical labels, edit writes DischargeCoefficient and survives the snapshot, Basic note text",
                presets && parse && before && changed && carried && same && back && custom && flow && note && vent && cdDoor != null,
                $"presets {presets} · parse {parse} · write {changed} · snapshot {carried} · unchanged edit ignored {same} · back to default {back} · " +
                $"flow-changing row {flow} · Basic note {note} · vent not editable {vent}");
        }

        // Delete / add an opening by hand (OpeningEdit, the code the Openings section runs). Delete: the opening leaves the model and the analysis, its hole is solid
        // (also where the mesh itself has the hole), and Restore puts it back. Add: a window on the sample house's south wall carves its cells through the wall,
        // reads as exterior (ledger count), and survives a save/load round trip.
        {
            const int cells = 300_000;
            (VoxelGrid Grid, List<int> Footprint) Voxel(BuildingModel m, Opening o)
            {
                var vg = Voxelizer.Build(m, cells, System.Environment.ProcessorCount, null);
                var fp = new List<int>();
                for (int k = 0; k < vg.Nz; k++) for (int j = 0; j < vg.Ny; j++) for (int i = 0; i < vg.Nx; i++)
                {
                    var c = vg.CellCenter(i, j, k) - o.Center;   // inside the box, a half cell clear of its edge, and within the wall's own thickness
                    if (MathF.Abs(c.X) <= o.Width * 0.5f - 0.5f * vg.Dx && MathF.Abs(c.Z) <= o.Height * 0.5f - 0.5f * vg.Dx && MathF.Abs(c.Y) <= MathF.Min(o.Depth * 0.5f, 0.1f))
                        fp.Add(vg.Index(i, j, k));
                }
                return (vg, fp);
            }
            SimulationResult Analysed(BuildingModel m, VoxelGrid vg)
            {
                int n = vg.Count;
                var f = new FlowField { Grid = vg, Ux = new float[n], Uy = new float[n], Uz = new float[n], P = new float[n] };
                var res = Analysis.Analyze(m, f, Vector2.Zero, new RunOptions());
                Analysis.Finish(res);
                return res;
            }
            int Exterior(SimulationResult r) =>
                r.Ledger.Notes.Select(n => System.Text.RegularExpressions.Regex.Match(n, @"^(\d+) exterior opening")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault();

            // ---- delete: an open window of the sample house
            var dm = SampleBuilding.Build();
            var dwin = dm.Openings.First(o => o.Id == "w-liv-s");
            var (dg0, dfp) = Voxel(dm, dwin);
            bool openBefore = dfp.Count > 0 && dfp.All(i => dg0.Cell[i] == VoxelGrid.Air);
            var dRes0 = Analysed(dm, dg0);
            int nOpen = dm.Openings.Count, nObs = dm.Obstacles.Count;
            var deleted = OpeningEdit.Delete(dm, dwin);
            var (dg1, _) = Voxel(dm, dwin);
            var dRes1 = Analysed(dm, dg1);
            bool gone = !dm.Openings.Contains(dwin) && dm.Openings.Count == nOpen - 1 && dRes0.Openings.Any(f => f.Opening.Id == "w-liv-s") &&
                        dRes1.Openings.All(f => f.Opening.Id != "w-liv-s") && dRes1.Openings.Count == nOpen - 1 && Exterior(dRes1) == Exterior(dRes0) - 1;
            bool wall = dfp.All(i => dg1.Cell[i] == VoxelGrid.Solid);
            OpeningEdit.Restore(dm, deleted);
            var (dg2, _) = Voxel(dm, dwin);
            bool undone = dm.Openings.Count == nOpen && dm.Openings.IndexOf(dwin) == deleted.Index && dm.Obstacles.Count == nObs && dfp.All(i => dg2.Cell[i] == VoxelGrid.Air);

            // ---- delete over a hole in the mesh itself (an imported model): sealed as a closed opening is
            var hm = new BuildingModel { Name = "hole", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } } };
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1, -1, -0.2f), new Vector3(11, 6, 0)));
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 3), new Vector3(10.2f, 5.2f, 3.2f)));
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 0), new Vector3(4f, 0, 3)));        // south wall in two pieces: a 1 m x 3 m gap
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(5f, -0.2f, 0), new Vector3(10.2f, 0, 3)));
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 5, 0), new Vector3(10.2f, 5.2f, 3)));
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 0, 0), new Vector3(0, 5, 3)));
            hm.Obstacles.Add(SampleBuilding.Box(new Vector3(10, 0, 0), new Vector3(10.2f, 5, 3)));
            var hole = new Opening { Id = "det-hole", Name = "Window 1", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(4.5f, -0.1f, 1.5f), Normal = -Vector3.UnitY, Width = 1f, Height = 1.5f, Depth = 0.2f };
            hm.Openings.Add(hole);
            var (hg0, hfp) = Voxel(hm, hole);
            bool holeOpen = hfp.Count > 0 && hfp.All(i => hg0.Cell[i] == VoxelGrid.Air);
            OpeningEdit.Delete(hm, hole);
            var (hg1, _) = Voxel(hm, hole);
            bool holeSealed = hfp.All(i => hg1.Cell[i] == VoxelGrid.Solid) && hm.Openings.Count == 0;
            Check("delete opening: gone from the model and from the analysis (and one exterior opening fewer in the ledger); its cells are solid wall, also over a real hole in a mesh; Restore puts it back at its index with the cells open again",
                openBefore && gone && wall && undone && holeOpen && holeSealed,
                $"open before {openBefore} · removed {gone} · cells solid {wall} · restored {undone} · mesh hole open {holeOpen}, sealed after delete {holeSealed} · footprint {dfp.Count} cells");

            // ---- add: click the south wall of the sample house at x 5.4, 1.5 m up, from outside
            var am = SampleBuilding.Build();
            var hit = OpeningEdit.PickWall(am, new Vector3(5.4f, -10f, 1.5f), Vector3.UnitY);
            bool picked = hit is { } h0 && MathF.Abs(h0.Point.Y + 0.125f) < 1e-3f && h0.Normal == -Vector3.UnitY;
            bool roofRefused = OpeningEdit.PickWall(am, new Vector3(5f, 4f, 20f), -Vector3.UnitZ) == null;   // a roof is not a wall
            var ext0 = Analysed(am, Voxelizer.Build(am, cells, System.Environment.ProcessorCount, null));
            var added = hit is { } h1 ? OpeningEdit.Add(am, h1, out _) : null;
            bool shape = added != null && added.Kind == OpeningKind.Window && added.IsOpen && added.DischargeCoefficient == null && OpeningEdit.IsUser(added) &&
                         MathF.Abs(added.Width - 1.2f) < 1e-3f && MathF.Abs(added.Height - 1.2f) < 1e-3f && MathF.Abs(OpeningEdit.Sill(am, added) - 0.9f) < 1e-3f &&
                         MathF.Abs(added.Depth - 0.25f) < 0.02f && Vector3.Distance(added.Center, new Vector3(5.4f, 0f, 1.5f)) < 0.02f && added.Normal == -Vector3.UnitY;
            var aprobe = added ?? new Opening();
            var (ag0, afp) = Voxel(SampleBuilding.Build(), aprobe);                  // the same cells before the add: wall
            var (ag1, _) = Voxel(am, aprobe);
            bool carved = shape && afp.Count > 0 && afp.All(i => ag0.Cell[i] == VoxelGrid.Solid && ag1.Cell[i] == VoxelGrid.Air);
            bool beside = shape && ag1.Cell[ag1.Index((int)((5.4f + 0.9f - ag1.Origin.X) / ag1.Dx), (int)((0f - ag1.Origin.Y) / ag1.Dx), (int)((1.5f - ag1.Origin.Z) / ag1.Dx))] == VoxelGrid.Solid;   // 0.3 m past the edge: still wall
            var ext1 = Analysed(am, ag1);
            bool exterior = shape && Devices.IsExterior(am, added!) && Exterior(ext1) == Exterior(ext0) + 1 && ext1.Openings.Any(f => f.Opening.Id == added!.Id);
            var back = BuildingModel.FromJson(am.ToJson());
            var again = back.Openings.FirstOrDefault(o => o.Id == aprobe.Id);
            bool roundTrip = again != null && again.Kind == OpeningKind.Window && again.IsOpen && Vector3.Distance(again.Center, aprobe.Center) < 1e-5f &&
                        again.Normal == aprobe.Normal && again.Width == aprobe.Width && again.Height == aprobe.Height && again.Depth == aprobe.Depth;
            var (bg1, _) = Voxel(back, aprobe);
            bool sameCells = roundTrip && afp.All(i => bg1.Cell[i] == VoxelGrid.Air);
            // edits: sill 0.5, height 1.0 (the sill stays), width 9 refused
            bool edits = shape && OpeningEdit.Set(am, added!, "Sill", 0.5f) && OpeningEdit.Set(am, added!, "Height", 1.0f) &&
                         MathF.Abs(OpeningEdit.Sill(am, added!) - 0.5f) < 1e-3f && MathF.Abs(added!.Center.Z - 1.0f) < 1e-3f && !OpeningEdit.Set(am, added!, "Width", 9f);
            Check("add opening: a click on the sample house's south wall gives a 1.2 x 1.2 m window, sill 0.9 m, as thick as the wall, centred on the click, outward normal; it carves the wall cells (not the wall beside it), counts as one more exterior opening, survives save/load; size edits keep the sill; a roof click is refused",
                picked && roofRefused && shape && carved && beside && exterior && sameCells && edits,
                $"picked {picked} · roof refused {roofRefused} · shape {shape} · carved {carved} ({afp.Count} cells) · wall beside kept {beside} · exterior {exterior} (ledger {Exterior(ext0)} -> {Exterior(ext1)}) · round roundTrip {roundTrip}/{sameCells} · edits {edits}");

            // ---- review fixes: the fill solid goes into a NEW list (a snapshot enumerating the old one cannot throw); size edits are bounded by the free wall and the storey; Sill reads from the
            // opening's own storey; merge compares floor heights, not Level strings.
            {
                var fm = SampleBuilding.Build();
                var fwin = fm.Openings.First(o => o.Id == "w-liv-s");
                var listBefore = fm.Obstacles;
                int nBeforeList = listBefore.Count;
                var fdel = OpeningEdit.Delete(fm, fwin);
                bool listReplaced = !ReferenceEquals(fm.Obstacles, listBefore) && listBefore.Count == nBeforeList && fm.Obstacles.Count == nBeforeList + 2;   // the fill and its source box
                var listAfterDelete = fm.Obstacles;
                OpeningEdit.Restore(fm, fdel);
                bool listRestored = !ReferenceEquals(fm.Obstacles, listAfterDelete) && listAfterDelete.Count == nBeforeList + 2 && fm.Obstacles.Count == nBeforeList && fm.Obstacles.All(m => !ReferenceEquals(m, fdel.Fill));

                var sm = SampleBuilding.Build();
                var sHit = OpeningEdit.PickWall(sm, new Vector3(5.4f, -10f, 1.5f), Vector3.UnitY);
                var sw = sHit is { } sh ? OpeningEdit.Add(sm, sh, out _) : null;
                float head = (sm.Storeys.FirstOrDefault(st => st.Id == sw?.Level) is { Height: > 0 } sst ? sst.Height : 3f) - 0.2f;
                bool headOk = sw != null && !OpeningEdit.Set(sm, sw, "Height", head) && OpeningEdit.Set(sm, sw, "Height", head - 0.9f - 0.01f)        // sill 0.9 + height must stay under the head room
                              && !OpeningEdit.Set(sm, sw, "Sill", 3f) && OpeningEdit.Set(sm, sw, "Sill", 0.5f);
                bool widthOk = false;
                if (sw != null)
                {
                    sw.Center = sw.Center with { X = 1.0f };                                     // close to the west end of the south wall
                    float maxW = OpeningEdit.MaxWidth(sm, sw);
                    widthOk = maxW > 1f && maxW < 2.3f && OpeningEdit.Set(sm, sw, "Width", 1.5f) && !OpeningEdit.Set(sm, sw, "Width", 3f);
                }
                var two = new BuildingModel { Name = "two", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3 }, new Storey { Id = "L2", Name = "Level 2", Elevation = 3.0f, Height = 3 } } };
                var lowOpening = new Opening { Id = "o1", Level = "L1", Center = new Vector3(0, 0, 3.05f), Height = 0.2f };        // lowest edge 2.95 m: still L1, 0.05 m under the L2 floor
                bool ownStorey = MathF.Abs(OpeningEdit.Sill(two, lowOpening) - 2.95f) < 1e-4f && MathF.Abs(OpeningEdit.Sill(two, new Opening { Id = "o2", Level = "Level 2", Center = new Vector3(0, 0, 4.0f), Height = 1f }) - 0.5f) < 1e-4f;
                var ra = new Room { Id = "a", Name = "A", Level = "L1", ZMin = 0, ZMax = 3 };
                var rb = new Room { Id = "b", Name = "B", Level = "Level 1", ZMin = 0, ZMax = 3 };
                var rc = new Room { Id = "c", Name = "C", Level = "L2", ZMin = 3, ZMax = 6 };
                bool mergeLevels = RoomEdit.CannotMerge([ra, rb]) == null && RoomEdit.CannotMerge([ra, rc]) != null;
                Check("review fixes: delete/restore replace model.Obstacles (the old list a snapshot holds is untouched); Height/Sill keep the head under the storey height, Width stays within the free wall; Sill is read from the opening's own storey; rooms of one floor merge whatever their Level strings",
                    listReplaced && listRestored && headOk && widthOk && ownStorey && mergeLevels,
                    $"list replaced {listReplaced} · restored {listRestored} · head room {headOk} · width bound {widthOk} · own-storey sill {ownStorey} · merge by floor {mergeLevels}");
            }

            // ---- rooms: delete, merge, add (RoomEdit). Delete: the room leaves the model and the analysis, Restore brings it back at its index. Merge: Living + Kitchen (both L1)
            // become one room (first's name and Id, both outlines, summed volume and people), its mask is the union, it survives save/load, Unmerge restores; a storey mix is refused.
            // Add: the Kitchen deleted, a click inside where it was recreates a room whose outline area is within 5 % of the drawn one; a click in an existing room or in the open air creates nothing.
            {
                var rm = SampleBuilding.Build();
                int nRooms = rm.Rooms.Count;
                var kitchen = rm.Rooms.First(r => r.Id == "kitchen");
                var rg0 = Voxelizer.Build(rm, cells, System.Environment.ProcessorCount, null);
                bool inBefore = Analysed(rm, rg0).Rooms.Any(r => r.Room.Id == "kitchen");
                var kdoor = rm.Openings.First(o => o.Id == "d-liv-kit");
                int extBefore = Exterior(Analysed(rm, rg0));
                var rdel = RoomEdit.Delete(rm, kitchen);
                var rg1 = Voxelizer.Build(rm, cells, System.Environment.ProcessorCount, null);
                bool goneRoom = !rm.Rooms.Contains(kitchen) && rm.Rooms.Count == nRooms - 1 && Analysed(rm, rg1).Rooms.All(r => r.Room.Id != "kitchen") &&
                                rm.Openings.Any(o => o.Id == "d-liv-kit") && kdoor.SideA != OpeningSide.Outdoors && kdoor.SideB != OpeningSide.Outdoors && kdoor.SideA != OpeningSide.Unknown && Exterior(Analysed(rm, rg1)) == extBefore;   // the door onto the deleted room's air stays interior (indoor, in no room)
                RoomEdit.Restore(rm, rdel);
                bool undoneRoom = kdoor.SideA == OpeningSide.Unknown && kdoor.SideB == OpeningSide.Unknown && rm.Rooms.Count == nRooms && rm.Rooms.IndexOf(kitchen) == rdel.Index &&
                                  Analysed(rm, Voxelizer.Build(rm, cells, System.Environment.ProcessorCount, null)).Rooms.Any(r => r.Room.Id == "kitchen");
                Check("delete room: the Kitchen is gone from the model and the analysis (its openings stay); Restore puts it back at its index and the analysis lists it again",
                    inBefore && goneRoom && undoneRoom, $"in analysis before {inBefore} · removed {goneRoom} · restored {undoneRoom}");

                // ---- merge
                var mm = SampleBuilding.Build();
                var liv = mm.Rooms.First(r => r.Id == "living");
                var kit = mm.Rooms.First(r => r.Id == "kitchen");
                var bed1 = mm.Rooms.First(r => r.Id == "bed1");
                float volSum = RoomNames.FloorArea(liv) * (liv.ZMax - liv.ZMin) + RoomNames.FloorArea(kit) * (kit.ZMax - kit.ZMin);
                int people = liv.Occupants + kit.Occupants, nBefore = mm.Rooms.Count;
                var mg0 = Voxelizer.Build(mm, cells, System.Environment.ProcessorCount, null);
                var masks0 = Analysis.RoomMasks(mg0, mm, out _);
                bool[] mLiving = masks0[mm.Rooms.IndexOf(liv)], mKit = masks0[mm.Rooms.IndexOf(kit)];
                bool refusedStoreys = RoomEdit.CannotMerge([liv, bed1]) != null && RoomEdit.Merge(mm, [liv, bed1], out _) == null && mm.Rooms.Count == nBefore;
                var merged = RoomEdit.Merge(mm, [liv, kit], out string whyNot);
                bool shaped = merged != null && mm.Rooms.Count == nBefore - 1 && merged.Result.Id == "living" && merged.Result.Name == "Living" &&
                              merged.Result.Loops.Count == liv.Loops.Count + kit.Loops.Count && merged.Result.Occupants == people && !mm.Rooms.Contains(kit) &&
                              mm.Rooms.IndexOf(merged.Result) == merged.Sources[0].Index;
                var mg1 = Voxelizer.Build(mm, cells, System.Environment.ProcessorCount, null);
                var mres = Analysed(mm, mg1);
                var mergedRes = mres.Rooms.FirstOrDefault(r => r.Room.Id == "living");
                bool volume = mergedRes != null && MathF.Abs(mergedRes.Volume - volSum) < 0.005f * volSum && mres.Rooms.All(r => r.Room.Id != "kitchen");
                var masks1 = Analysis.RoomMasks(mg1, mm, out _);
                var mMerged = masks1[mm.Rooms.IndexOf(merged?.Result ?? liv)];
                int unionCells = 0, missing = 0, extra = 0;
                for (int i = 0; i < mMerged.Length; i++)
                {
                    bool u = mLiving[i] || mKit[i];
                    if (u) unionCells++;
                    if (u && !mMerged[i]) missing++;
                    if (!u && mMerged[i]) extra++;
                }
                bool union = unionCells > 0 && missing == 0 && extra <= unionCells / 50;      // the union of the two outlines (plus the band round the pair; the shared wall line adds a few cells)
                var back2 = BuildingModel.FromJson(mm.ToJson());
                var backRoom = back2.Rooms.FirstOrDefault(r => r.Id == "living");
                bool roundTripRoom = backRoom != null && backRoom.Loops.Count == 2 && backRoom.Occupants == people && back2.Rooms.Count == nBefore - 1 &&
                                     MathF.Abs(RoomNames.FloorArea(backRoom) * (backRoom.ZMax - backRoom.ZMin) - volSum) < 0.005f * volSum;
                RoomEdit.Unmerge(mm, merged!);
                bool unmerged = mm.Rooms.Count == nBefore && mm.Rooms.IndexOf(liv) == merged!.Sources[0].Index && mm.Rooms.IndexOf(kit) == merged.Sources[1].Index;
                Check("merge rooms: Living + Kitchen become one room (first's name and Id, both outlines, volume = sum, people = sum), its mask is the union of the two, it round-trips save/load, Unmerge restores both; rooms on two storeys are refused",
                    refusedStoreys && shaped && volume && union && roundTripRoom && unmerged,
                    $"storeys refused {refusedStoreys} · shape {shaped} {whyNot} · volume {mergedRes?.Volume:0.0} vs {volSum:0.0} m3 {volume} · mask union {unionCells} cells, missing {missing}, extra {extra} {union} · round trip {roundTripRoom} · unmerge {unmerged}");

                // ---- add: the Kitchen (L1, x 7.1-11.85, y 0.15-4.4) deleted, then a tilted click at (9.5, 2.3, 1.1) from the south, as a camera above the street would make it
                var xm = SampleBuilding.Build();
                var xk = xm.Rooms.First(r => r.Id == "kitchen");
                float area0 = RoomNames.FloorArea(xk);
                RoomEdit.Delete(xm, xk);
                var detRooms = RoomDetect.Detect(xm);
                var target = new Vector3(9.5f, 2.3f, 1.1f);
                var eyeS = new Vector3(9.5f, -9f, 4.5f);
                var ray = Vector3.Normalize(target - eyeS);
                var made = RoomEdit.Create(xm, detRooms, eyeS, ray, out string whyAdd);
                float area1 = made == null ? 0 : RoomNames.FloorArea(made);
                // The importer insets every outline 0.25 m off the wall faces (RoomDetect.InsetM); the sample's drawn rooms sit on the faces. So the outline AREA is smaller (reported), and the room
                // the solver sees is compared as it counts it: the volume (the un-eroded air, as ACH divides by it) and the mask (outline plus the OF-1 band), each within 5 % of the drawn Kitchen's.
                var gDrawn = Voxelizer.Build(SampleBuilding.Build(), cells, System.Environment.ProcessorCount, null);
                var sampleDrawn = SampleBuilding.Build();
                int drawnCells = Analysis.RoomMasks(gDrawn, sampleDrawn, out _)[sampleDrawn.Rooms.FindIndex(r => r.Id == "kitchen")].Count(b => b);
                var gAdded = Voxelizer.Build(xm, cells, System.Environment.ProcessorCount, null);
                int addedCells = made == null ? 0 : Analysis.RoomMasks(gAdded, xm, out _)[xm.Rooms.IndexOf(made)].Count(b => b);
                float volDrawn = area0 * (xk.ZMax - xk.ZMin);
                bool addOk = made != null && made.Level == "L1" && xm.Rooms.Contains(made) && made.Name.StartsWith("Room ", StringComparison.Ordinal) && made.Id.StartsWith(RoomEdit.UserPrefix, StringComparison.Ordinal) &&
                             MathF.Abs(made.DetectedVolume - volDrawn) <= 0.05f * volDrawn && drawnCells > 0 && MathF.Abs(addedCells - drawnCells) <= 0.05f * drawnCells;
                int afterAdd = xm.Rooms.Count;
                var again2 = RoomEdit.Create(xm, detRooms, eyeS, ray, out string whyDup);                                                       // now inside the new room
                var outside = RoomEdit.Create(xm, detRooms, new Vector3(-6f, -9f, 4.5f), Vector3.Normalize(new Vector3(-6f, 2.3f, 1.1f) - new Vector3(-6f, -9f, 4.5f)), out string whyOut);   // open air west of the house
                bool refused2 = again2 == null && whyDup.Contains("already inside", StringComparison.Ordinal) && outside == null && whyOut.Contains("not an enclosed space", StringComparison.Ordinal) && xm.Rooms.Count == afterAdd;
                var xback = BuildingModel.FromJson(xm.ToJson());
                bool savedRoom = made != null && xback.Rooms.FirstOrDefault(r => r.Id == made.Id) is { } sr && MathF.Abs(RoomNames.FloorArea(sr) - area1) < 1e-3f && sr.VoxelDerived;
                Check("add room: a click inside the deleted Kitchen recreates a room (same detection as the importer) whose volume and mask cells are within 5 % of the drawn one (its outline is inset 0.25 m, the importer's convention), named \"Room N\", saved with the model; a click inside an existing room or in open air creates nothing",
                    addOk && refused2 && savedRoom, $"outline {area0:0.0} m2 drawn -> {area1:0.0} m2 detected ({(area0 > 0 ? (area1 / area0 - 1) * 100 : 0):+0.0;-0.0} %) · volume {made?.DetectedVolume:0.0} vs {volDrawn:0.0} m3 · mask {addedCells} vs {drawnCells} cells · {made?.Name} {whyAdd} · refused {refused2} ({whyDup} / {whyOut}) · saved {savedRoom}");

                // ---- spaces without a room (the list under the rooms) and the forgiving click: the Kitchen deleted, the detector run on the edited model, RoomEdit.Candidates lists the Kitchen's space
                // (area within 5 % of what the same detector gave for the Kitchen before it was deleted), CreateFrom makes it a room, and a click ray through it at floor + 0.2 m, mid-height and
                // ceiling - 0.2 m resolves to it (so do rays from the street that only reach it at one of those heights); a ray into an existing room or the open air resolves to nothing, with the reason.
                {
                    var cm = SampleBuilding.Build();
                    var kPoint = new Vector2(9.5f, 2.3f);
                    var detBefore = RoomDetect.Detect(cm);
                    var origSpace = detBefore.Rooms.FirstOrDefault(r => r.Level == "L1" && Devices.InLoops(r, kPoint.X, kPoint.Y));
                    float origArea = origSpace == null ? 0 : RoomNames.FloorArea(origSpace);
                    var candNone = RoomEdit.Candidates(cm, detBefore);                                    // every space has its room: nothing to offer
                    RoomEdit.Delete(cm, cm.Rooms.First(r => r.Id == "kitchen"));
                    var detAfter = RoomDetect.Detect(cm);
                    var cands = RoomEdit.Candidates(cm, detAfter);
                    var space = cands.FirstOrDefault(r => r.Level == "L1" && Devices.InLoops(r, kPoint.X, kPoint.Y));
                    float spaceArea = space == null ? 0 : RoomNames.FloorArea(space);
                    bool listed = space != null && origArea > 0 && MathF.Abs(spaceArea - origArea) <= 0.05f * origArea && candNone.Count == 0;
                    var eyeC = new Vector3(9.5f, -9f, 4.5f);
                    float zf = 0.2f, zm = 1.5f, zc = 3f - 0.2f;                                              // L1 is 3 m high: the three heights the resolver tests
                    Room? Ray(Vector3 eye, Vector3 target, out string why) => RoomEdit.Resolve(cm, cands, eye, Vector3.Normalize(target - eye), out why);
                    bool atFloor = ReferenceEquals(Ray(eyeC, new Vector3(9.5f, 2.3f, zf), out _), space);
                    bool atMid = ReferenceEquals(Ray(eyeC, new Vector3(9.5f, 2.3f, zm), out _), space);
                    bool atCeil = ReferenceEquals(Ray(eyeC, new Vector3(9.5f, 2.3f, zc), out _), space);
                    bool fromAbove = ReferenceEquals(Ray(new Vector3(9.5f, 2.3f, 20f), new Vector3(9.5f, 2.3f, 1f), out _), space);               // straight down through the roof
                    Ray(eyeC, new Vector3(3f, 2f, 1.1f), out string whyRoom);                                                                      // into the Living room: not a candidate
                    Ray(new Vector3(-6f, -9f, 4.5f), new Vector3(-6f, 2.3f, 1.1f), out string whyAir);                                             // open air west of the house
                    bool reasons = whyRoom.Contains("already a room", StringComparison.Ordinal) && whyAir.Contains("outside the building", StringComparison.Ordinal);
                    var madeRoom = space == null ? null : RoomEdit.CreateFrom(cm, space, space.Level);
                    bool created = madeRoom != null && cm.Rooms.Contains(madeRoom) && madeRoom.Level == "L1" && madeRoom.Id.StartsWith(RoomEdit.UserPrefix, StringComparison.Ordinal) &&
                                   MathF.Abs(RoomNames.FloorArea(madeRoom) - spaceArea) < 1e-3f && !RoomEdit.Candidates(cm, detAfter).Any(r => Devices.InLoops(r, kPoint.X, kPoint.Y));
                    Check("spaces without a room: after deleting the Kitchen the candidate list holds its space (area within 5 % of the detector's own Kitchen), nothing is offered while every space has a room; Add makes it a room; a click ray through it at floor + 0.2 m, mid-height and ceiling - 0.2 m, or straight down through the roof, resolves to it; a ray into an existing room or the open air says why",
                        listed && atFloor && atMid && atCeil && fromAbove && reasons && created,
                        $"candidates {cands.Count} (none while all rooms exist: {candNone.Count == 0}) · space {spaceArea:0.0} m2 vs {origArea:0.0} · rays floor/mid/ceiling/above {atFloor}/{atMid}/{atCeil}/{fromAbove} · \"{whyRoom}\" / \"{whyAir}\" · created {created}");
                }

                // ---- put a window back where one was deleted: Delete leaves a fill solid; a new opening over it carves through it (the fill becomes the boxes the opening does not cover, cells open),
                // on a drawn model (the sample's Living window) and over a real hole in a mesh; Restore of the deleted opening after other edits brings back the very same opening (size, type, Cd) with
                // its fill and every piece of it gone.
                {
                    static bool Overlaps(Mesh m, Opening o)      // positive-volume overlap of a mesh's extent with the opening's box, in the opening's own frame
                    {
                        var n = Vector3.Normalize(new Vector3(o.Normal.X, o.Normal.Y, 0));
                        var t = new Vector3(-n.Y, n.X, 0);
                        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                        foreach (var v in m.Vertices) { var d = v - o.Center; var l = new Vector3(Vector3.Dot(d, n), Vector3.Dot(d, t), d.Z); lo = Vector3.Min(lo, l); hi = Vector3.Max(hi, l); }
                        return lo.Y < o.Width * 0.5f - 0.01f && hi.Y > -o.Width * 0.5f + 0.01f && lo.Z < o.Height * 0.5f - 0.01f && hi.Z > -o.Height * 0.5f + 0.01f && lo.X < o.Depth * 0.5f && hi.X > -o.Depth * 0.5f;
                    }
                    int FillCount(BuildingModel m, string id) => m.Obstacles.Count(x => x.Id == OpeningEdit.FillPrefix + id || x.Id.StartsWith(OpeningEdit.FillPrefix + id + "~", StringComparison.Ordinal));

                    var rm2 = SampleBuilding.Build();
                    var orig = rm2.Openings.First(o => o.Id == "w-liv-s");
                    orig.DischargeCoefficient = 0.45f;
                    float oW = orig.Width, oH = orig.Height, oD = orig.Depth;
                    var oKind = orig.Kind;
                    var oCentre = orig.Center;
                    var (rgA, rfp) = Voxel(rm2, orig);
                    var gone1 = OpeningEdit.Delete(rm2, orig);
                    int fillBefore = FillCount(rm2, orig.Id);
                    var spot = OpeningEdit.PickWall(rm2, new Vector3(oCentre.X, -10f, oCentre.Z), Vector3.UnitY);          // the click lands on the fill's face
                    var over = spot is { } sp ? OpeningEdit.Add(rm2, sp, out string whyOver) : null;
                    bool onFill = spot is { } sp2 && MathF.Abs(sp2.Point.Y - (oCentre.Y - oD * 0.5f)) < 0.3f;
                    var (rgB, rfp1) = over == null ? (rgA, new List<int>()) : Voxel(rm2, over);
                    int piecesNow = FillCount(rm2, orig.Id);
                    bool anyOverlap = over != null && rm2.Obstacles.Any(x => x.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && Overlaps(x, over));
                    bool cellsAir = over != null && rfp1.All(i => rgB.Cell[i] == VoxelGrid.Air);
                    bool carvedDrawn = over != null && onFill && fillBefore == 1 && rfp1.Count > 0 && rfp1.All(i => rgB.Cell[i] == VoxelGrid.Air) &&
                                       !rm2.Obstacles.Any(x => x.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && Overlaps(x, over)) && FillCount(rm2, orig.Id) is > 0 and <= 4;
                    // an unrelated later edit, then Restore
                    var other = rm2.Openings.First(o => o.Id == "d-liv-kit");
                    OpeningEdit.Delete(rm2, other);
                    var otherAdd = OpeningEdit.PickWall(rm2, new Vector3(9.5f, -10f, 1.5f), Vector3.UnitY) is { } sp3 ? OpeningEdit.Add(rm2, sp3, out _) : null;
                    if (otherAdd != null) OpeningEdit.Set(rm2, otherAdd, "Width", 0.8f);
                    OpeningEdit.Restore(rm2, gone1);
                    var (rg2, _) = Voxel(rm2, orig);
                    bool exact = rm2.Openings.Contains(orig) && orig.Width == oW && orig.Height == oH && orig.Depth == oD && orig.Kind == oKind && orig.Center == oCentre && orig.DischargeCoefficient == 0.45f &&
                                 FillCount(rm2, orig.Id) == 0 && rm2.Openings.Count(o => o.Id == orig.Id) == 1;
                    bool reopened = rfp.Count > 0 && rfp.All(i => rg2.Cell[i] == VoxelGrid.Air);
                    // over a real hole in a mesh (the imported case): the Delete test's wall in two pieces with a 1 m gap
                    var hm2 = new BuildingModel { Name = "hole2", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } } };
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(-1, -1, -0.2f), new Vector3(11, 6, 0)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 3), new Vector3(10.2f, 5.2f, 3.2f)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 0), new Vector3(4f, 0, 3)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(5f, -0.2f, 0), new Vector3(10.2f, 0, 3)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 5, 0), new Vector3(10.2f, 5.2f, 3)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 0, 0), new Vector3(0, 5, 3)));
                    hm2.Obstacles.Add(SampleBuilding.Box(new Vector3(10, 0, 0), new Vector3(10.2f, 5, 3)));
                    var hole2 = new Opening { Id = "det-hole", Name = "Window 1", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(4.5f, -0.1f, 1.5f), Normal = -Vector3.UnitY, Width = 1f, Height = 1.5f, Depth = 0.2f };
                    hm2.Openings.Add(hole2);
                    var hdel = OpeningEdit.Delete(hm2, hole2);
                    var hSpot = OpeningEdit.PickWall(hm2, new Vector3(4.5f, -10f, 1.5f), Vector3.UnitY);
                    var hAdd = hSpot is { } hs ? OpeningEdit.Add(hm2, hs, out string whyHole) : null;
                    var (hg2, hfp2) = hAdd == null ? (null!, new List<int>()) : Voxel(hm2, hAdd);
                    bool carvedMesh = hAdd != null && hfp2.Count > 0 && hfp2.All(i => hg2.Cell[i] == VoxelGrid.Air) &&
                                      !hm2.Obstacles.Any(x => x.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && Overlaps(x, hAdd));
                    string hdiag = $"hole cells {hfp2.Count} air {hAdd != null && hfp2.All(i => hg2.Cell[i] == VoxelGrid.Air)} overlap {hAdd != null && hm2.Obstacles.Any(x => x.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && Overlaps(x, hAdd))} spot {hSpot is not null}";
                    OpeningEdit.Restore(hm2, hdel);
                    bool meshRestored = hm2.Openings.Contains(hole2) && FillCount(hm2, hole2.Id) == 0;
                    // RA-14: delete -> re-add over the fill -> resize: the old cut-out goes back to solid first, so nothing of the old rectangle outside the new one stays open; Restore still brings back the original state
                    {
                        var qm = SampleBuilding.Build();
                        var qo = qm.Openings.First(o => o.Id == "w-liv-s");
                        var qc = qo.Center;
                        var qn = qo.Normal; var qt = new Vector3(-qn.Y, qn.X, 0);
                        float qW = qo.Width, qH = qo.Height;
                        var qDel = OpeningEdit.Delete(qm, qo);
                        var qSpot = OpeningEdit.PickWall(qm, new Vector3(qc.X, -10f, qc.Z), Vector3.UnitY);
                        var qAdd = qSpot is { } qs ? OpeningEdit.Add(qm, qs, out _) : null;
                        bool shrunk = qAdd != null && OpeningEdit.Set(qm, qAdd, "Width", MathF.Max(0.4f, qAdd.Width * 0.5f));
                        float nW = qAdd?.Width ?? 0, nH = qAdd?.Height ?? 0;
                        var nc = qAdd?.Center ?? qc;
                        bool Solid(Vector3 pt) => qm.Obstacles.Any(m => m.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && m.Vertices.Length > 0 &&
                            pt.X >= m.Vertices.Min(v => v.X) - 1e-4f && pt.X <= m.Vertices.Max(v => v.X) + 1e-4f && pt.Y >= m.Vertices.Min(v => v.Y) - 1e-4f && pt.Y <= m.Vertices.Max(v => v.Y) + 1e-4f &&
                            pt.Z >= m.Vertices.Min(v => v.Z) - 1e-4f && pt.Z <= m.Vertices.Max(v => v.Z) + 1e-4f);
                        int holes = 0, probes = 0; string wrong = "";
                        for (float u = -qW * 0.5f + 0.05f; u < qW * 0.5f; u += 0.1f)
                            for (float w = -qH * 0.5f + 0.05f; w < qH * 0.5f; w += 0.1f)
                            {
                                var pt = qc + qt * u + new Vector3(0, 0, w);
                                var rel = pt - nc;
                                float ex = MathF.Abs(Vector3.Dot(rel, qt)) - nW * 0.5f, ez = MathF.Abs(rel.Z) - nH * 0.5f;
                                if (MathF.Abs(ex) < 0.02f && ez < 0.02f || MathF.Abs(ez) < 0.02f && ex < 0.02f) continue;       // on the new rectangle's edge: a float tie, not a verdict
                                bool inNew = ex < 0 && ez < 0;
                                probes++;
                                if (inNew ? Solid(pt) : !Solid(pt)) { holes++; if (holes <= 4) wrong += $" [u {u:0.00} w {w:0.00} inNew {inNew}]"; }
                            }
                        OpeningEdit.Restore(qm, qDel);
                        bool backExact = qm.Openings.Contains(qo) && qo.Width == qW && qo.Height == qH && qo.Center == qc && !qm.Obstacles.Any(m => m.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal));
                        Check("RA-14: resizing a re-added opening restores the fill first, then carves the new rectangle: the old rectangle minus the new one is solid, the new one is open; Restore of the deleted opening then brings back the original state",
                            shrunk && probes > 0 && holes == 0 && backExact,
                            $"old {qW:0.##} x {qH:0.##} m, re-added {qAdd?.Width:0.##} (resized to {nW:0.##}); {holes} of {probes} probe points wrong{wrong} · new centre {nc - qc} · restored exactly {backExact}");
                    }
                    // RA-14 (m3): two openings re-added on one fill, one resized: merging the fill back must not close the other one's hole
                    {
                        var sm = SampleBuilding.Build();
                        var so = sm.Openings.First(o => o.Id == "w-liv-s");
                        var sc = so.Center; var snT = new Vector3(-so.Normal.Y, so.Normal.X, 0); float sW = so.Width;
                        OpeningEdit.Delete(sm, so);
                        Opening? Readd(float off) => OpeningEdit.PickWall(sm, new Vector3(sc.X + snT.X * off, -10f, sc.Z), Vector3.UnitY) is { } h ? OpeningEdit.Add(sm, h, out _) : null;
                        var sa = Readd(-0.25f * sW); var sb = Readd(0.25f * sW);
                        bool sizedOk = sa != null && sb != null && OpeningEdit.Set(sm, sa, "Width", 0.4f) && OpeningEdit.Set(sm, sb, "Width", 0.4f) && OpeningEdit.Set(sm, sa, "Width", 0.5f);
                        bool SolidAt(Vector3 pt) => sm.Obstacles.Any(m => m.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && m.Vertices.Length > 0 &&
                            pt.X >= m.Vertices.Min(v => v.X) - 1e-4f && pt.X <= m.Vertices.Max(v => v.X) + 1e-4f && pt.Y >= m.Vertices.Min(v => v.Y) - 1e-4f && pt.Y <= m.Vertices.Max(v => v.Y) + 1e-4f &&
                            pt.Z >= m.Vertices.Min(v => v.Z) - 1e-4f && pt.Z <= m.Vertices.Max(v => v.Z) + 1e-4f);
                        bool bothOpen = sizedOk && !SolidAt(sa!.Center) && !SolidAt(sb!.Center);
                        Check("RA-14: two openings re-added on one fill, resizing one leaves the other's hole open",
                            bothOpen, $"re-added {(sa != null && sb != null)}, resized {sizedOk}; hole centres solid: first {(sa != null && SolidAt(sa.Center))}, second {(sb != null && SolidAt(sb.Center))} (want False, False)");
                    }
                    // RC-02 / RC-09: the fill is rebuilt from its ORIGINAL box minus every listed opening, so no sequence of re-add / resize / delete / Restore leaves an unlisted hole or a fill over a listed opening.
                    {
                        static bool FillAt(BuildingModel mm, Vector3 pt) => mm.Obstacles.Any(m => m.Id.StartsWith(OpeningEdit.FillPrefix, StringComparison.Ordinal) && m.Vertices.Length > 0 &&
                            pt.X >= m.Vertices.Min(v => v.X) - 1e-4f && pt.X <= m.Vertices.Max(v => v.X) + 1e-4f && pt.Y >= m.Vertices.Min(v => v.Y) - 1e-4f && pt.Y <= m.Vertices.Max(v => v.Y) + 1e-4f &&
                            pt.Z >= m.Vertices.Min(v => v.Z) - 1e-4f && pt.Z <= m.Vertices.Max(v => v.Z) + 1e-4f);
                        // over the rectangle (centre c, along-wall unit t, W x H): a point inside a listed opening must be open, any other point of it must be filled
                        static (int Wrong, int Probes) HoleCheck(BuildingModel mm, Vector3 c, Vector3 t, float W, float H)
                        {
                            var nn = new Vector3(t.Y, -t.X, 0);
                            int wrong = 0, probes = 0;
                            for (float u = -W * 0.5f + 0.05f; u < W * 0.5f; u += 0.05f)
                                for (float w = -H * 0.5f + 0.05f; w < H * 0.5f; w += 0.05f)
                                {
                                    var pt = c + t * u + new Vector3(0, 0, w);
                                    bool tie = false, inListed = false;
                                    foreach (var o in mm.Openings)
                                    {
                                        var rel = pt - o.Center;
                                        if (MathF.Abs(Vector3.Dot(rel, nn)) > 0.5f) continue;
                                        float ex = MathF.Abs(Vector3.Dot(rel, t)) - o.Width * 0.5f, ez = MathF.Abs(rel.Z) - o.Height * 0.5f;
                                        if (MathF.Abs(ex) < 0.02f && ez < 0.02f || MathF.Abs(ez) < 0.02f && ex < 0.02f) tie = true;
                                        if (ex < 0 && ez < 0) inListed = true;
                                    }
                                    if (tie) continue;
                                    probes++;
                                    if (inListed == FillAt(mm, pt)) wrong++;
                                }
                            return (wrong, probes);
                        }
                        // trace 1: an imported-style wall with a real 1.0 x 0.8 m hole, sill 1.6 m; delete it, re-add on its fill (1.2 x 1.2 m at 0.9-2.1 m), save/load, Height 0.5
                        var rcm = new BuildingModel { Name = "hole3", Storeys = { new Storey { Id = "L1", Name = "Level 1", Elevation = 0, Height = 3, Analysed = true } } };
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1, -1, -0.2f), new Vector3(11, 6, 0)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 3), new Vector3(10.2f, 5.2f, 3.2f)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 0), new Vector3(10.2f, 0, 1.6f)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 2.4f), new Vector3(10.2f, 0, 3)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, -0.2f, 1.6f), new Vector3(4f, 0, 2.4f)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(5f, -0.2f, 1.6f), new Vector3(10.2f, 0, 2.4f)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 5, 0), new Vector3(10.2f, 5.2f, 3)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.2f, 0, 0), new Vector3(0, 5, 3)));
                        rcm.Obstacles.Add(SampleBuilding.Box(new Vector3(10, 0, 0), new Vector3(10.2f, 5, 3)));
                        var rcHole = new Opening { Id = "det-hole3", Name = "Window 1", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(4.5f, -0.1f, 2.0f), Normal = -Vector3.UnitY, Width = 1f, Height = 0.8f, Depth = 0.2f };
                        rcm.Openings.Add(rcHole);
                        var rcT = new Vector3(1, 0, 0);
                        OpeningEdit.Delete(rcm, rcHole);
                        var rcAdd = OpeningEdit.PickWall(rcm, new Vector3(4.5f, -10f, 1.5f), Vector3.UnitY) is { } rcHit ? OpeningEdit.Add(rcm, rcHit, out _) : null;
                        bool t1shape = rcAdd != null && MathF.Abs(rcAdd.Width - 1.2f) < 1e-3f && MathF.Abs(rcAdd.Height - 1.2f) < 1e-3f && MathF.Abs(OpeningEdit.Sill(rcm, rcAdd) - 0.9f) < 1e-3f;
                        var (t1wrongPre, _) = rcAdd != null ? HoleCheck(rcm, rcHole.Center, rcT, 1f, 0.8f) : (-1, 0);
                        var rcBack = BuildingModel.FromJson(rcm.ToJson());          // (d) save / load keeps the fill's original box
                        var rcAdd2 = rcBack.Openings.FirstOrDefault(o => o.Id == rcAdd?.Id);
                        bool t1valid = ModelValidation.Validate(rcBack) == null;
                        bool t1set = rcAdd2 != null && OpeningEdit.Set(rcBack, rcAdd2, "Height", 0.5f);
                        var (t1wrong, t1probes) = t1set ? HoleCheck(rcBack, rcHole.Center, rcT, 1f, 0.8f) : (-1, 0);
                        // trace 2: the sample Living window deleted, re-added on its fill, Sill 0 -> Width 1.5 -> Width 0.5
                        var rsm = SampleBuilding.Build();
                        var rso = rsm.Openings.First(o => o.Id == "w-liv-s");
                        var rsc = rso.Center; var rst = new Vector3(-rso.Normal.Y, rso.Normal.X, 0); float rsW = rso.Width, rsH = rso.Height;
                        OpeningEdit.Delete(rsm, rso);
                        var rsAdd = OpeningEdit.PickWall(rsm, new Vector3(rsc.X, -10f, rsc.Z), Vector3.UnitY) is { } rsHit ? OpeningEdit.Add(rsm, rsHit, out _) : null;
                        bool t2set = rsAdd != null && OpeningEdit.Set(rsm, rsAdd, "Sill", 0f) && OpeningEdit.Set(rsm, rsAdd, "Width", 1.5f) && OpeningEdit.Set(rsm, rsAdd, "Width", 0.5f);
                        var (t2wrong, t2probes) = t2set ? HoleCheck(rsm, rsc, rst, rsW, rsH) : (-1, 0);
                        // (c) a deleted sibling restored: delete A, re-add X on the fill of A, delete X (its fill overlaps the rectangle of A), Restore A: the rectangle of A is open and no fill covers it
                        var rpm = SampleBuilding.Build();
                        var rpa = rpm.Openings.First(o => o.Id == "w-liv-s");
                        var rpc = rpa.Center; var rpt = new Vector3(-rpa.Normal.Y, rpa.Normal.X, 0);
                        var rpDelA = OpeningEdit.Delete(rpm, rpa);
                        var rpX = OpeningEdit.PickWall(rpm, new Vector3(rpc.X, -10f, rpc.Z), Vector3.UnitY) is { } rpHit ? OpeningEdit.Add(rpm, rpHit, out _) : null;
                        if (rpX != null) OpeningEdit.Set(rpm, rpX, "Width", 0.6f);
                        if (rpX != null) OpeningEdit.Delete(rpm, rpX);
                        OpeningEdit.Restore(rpm, rpDelA);
                        var (t3wrong, t3probes) = rpX != null ? HoleCheck(rpm, rpc, rpt, rpa.Width, rpa.Height) : (-1, 0);
                        Check("RC-02 / RC-09: a fill is rebuilt from its original box minus every listed opening: (a) delete a 1.0 x 0.8 m hole (sill 1.6), re-add on its fill (1.2 x 1.2 m at 0.9-2.1 m), save/load, Height 0.5 leaves no open area outside the listed opening; (b) Sill 0 -> Width 1.5 -> Width 0.5 leaves no hole; (c) Restore of a deleted sibling leaves its rectangle open with no fill over it; (d) the model written by the app still validates",
                            t1shape && t1valid && t1set && t1wrongPre == 0 && t1wrong == 0 && t1probes > 0 && t2set && t2wrong == 0 && t2probes > 0 && rpX != null && t3wrong == 0 && t3probes > 0,
                            $"(a) re-added shape {t1shape} ({rcAdd?.Width:0.##} x {rcAdd?.Height:0.##}), after save/load valid {t1valid}, Height 0.5 {t1set}: {t1wrong} of {t1probes} wrong (before the reload {t1wrongPre}) · (b) edits {t2set}: {t2wrong} of {t2probes} wrong · (c) {t3wrong} of {t3probes} wrong (0 = rectangle open, nothing over it)");
                    }
                    Check("re-add over a deleted window: a new opening on the fill of a deleted one carves through it (the fill becomes the boxes it does not cover; cells open), on the drawn sample and over a real hole in a mesh; Restore after other edits (a delete, an add, a resize) brings back the same opening with its size, type, Cd and centre, every piece of its fill gone, its cells open",
                        carvedDrawn && exact && reopened && carvedMesh && meshRestored,
                        $"drawn: pieces {piecesNow} overlap {anyOverlap} air {cellsAir} fillBefore {fillBefore} · click on the fill {onFill}, new window {over?.Width:0.##} x {over?.Height:0.##} m over the {oW:0.##} x {oH:0.##} m fill, carved {carvedDrawn} ({rfp1.Count} cells), pieces left after restore {FillCount(rm2, orig.Id)} · restored exactly {exact}, cells open {reopened} · mesh hole: {hdiag} carved {carvedMesh} ({hAdd?.Width:0.##} m wide), restored {meshRestored}");
                }
            }
        }

        // Live model replacement (CLAUDE_FIX_PROMPT #8): LiveSolver itself, CPU backend, small grid. Start on A, swap in a
        // B with a bigger plan (so a different grid), and every field published under B's epoch or later must be B's.
        {
            var modelA = SampleBuilding.Build();
            var modelB = SampleBuilding.Build();
            modelB.Obstacles.Add(SampleBuilding.Box(new Vector3(14, 0, 0), new Vector3(30, 2, 0.5f)));
            var ls = new LiveSolver(new WindFlow.App.Viewport.ParticleSystem()) { RecordPublications = true };
            int Logged() { lock (ls.PublicationLog) return ls.PublicationLog.Count; }
            bool WaitFor(Func<bool> done) { var until = DateTime.UtcNow.AddSeconds(90); while (!done() && DateTime.UtcNow < until) Thread.Sleep(20); return done(); }
            bool gotA = false, gotB = false, ordered = false, noneStale = false;
            int cellsA = 0, cellsB = 0, epochB = -1, afterB = 0;
            try
            {
                ls.UseCpuForTest(30_000);
                ls.Start(modelA, new Vector2(4, 0));
                gotA = WaitFor(() => Logged() > 0);
                if (gotA) lock (ls.PublicationLog) cellsA = ls.PublicationLog[0].Grid.Count;
                ls.GeometryChanged(modelB);
                gotB = WaitFor(() => { lock (ls.PublicationLog) return ls.PublicationLog.Any(e => e.Grid.Count != cellsA); });
                if (gotB)
                {
                    int mark = Logged();
                    WaitFor(() => Logged() >= mark + 3);      // a few more after the swap: none may be A's
                }
                ls.Stop();
                List<(int Epoch, VoxelGrid Grid)> log;
                lock (ls.PublicationLog) log = [.. ls.PublicationLog];
                if (gotB)
                {
                    var firstB = log.First(e => e.Grid.Count != cellsA);
                    epochB = firstB.Epoch; cellsB = firstB.Grid.Count;
                    var after = log.Where(e => e.Epoch >= epochB).ToList();
                    afterB = after.Count;
                    noneStale = after.All(e => e.Grid.Count == cellsB);
                    ordered = log.Zip(log.Skip(1), (a, b) => b.Epoch >= a.Epoch).All(x => x) &&
                              log.Where(e => e.Epoch < epochB).All(e => e.Grid.Count == cellsA);
                }
            }
            finally { ls.Stop(); }
            Check("live model replacement: after B's epoch starts, every published field is B's grid and none is A's",
                gotA && gotB && ordered && noneStale && epochB > 0 && cellsA != cellsB && afterB >= 2,
                $"A {cellsA} cells, B {cellsB} cells · B's first epoch {epochB} · {afterB} publications at/after it · epochs non-decreasing and A before B: {ordered} · none stale {noneStale}");
        }

        // RZ-02: Domain Full + Accurate lands an Accurate result. The skip test compares the Accurate budget with the Preview budget (both set Dx on the
        // Compact domain), not with the ~10x bigger Full Preview grid, which used to switch the pass off "for GPU memory". CPU, tiny budgets, 3 s cap.
        {
            var ls = new LiveSolver(new WindFlow.App.Viewport.ParticleSystem()) { DomainModeWanted = DomainMode.Full, AccurateCellsTyped = 16_000, AccurateCapForTest = 40 };   // RU-05: a cap inside the settle lands nothing, so the cap must outlast the 16k-cell settle (was 3 s)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            SimulationResult? acc = null; int previewCells = 0; string off = "";
            try
            {
                ls.UseCpuForTest(8_000);
                ls.Start(SampleBuilding.Build(), new Vector2(4, 0));
                var until = DateTime.UtcNow.AddSeconds(300);
                while (ls.AccurateStats == null && ls.Accurate.Phase != AccuratePhase.Off && DateTime.UtcNow < until)
                {
                    if (previewCells == 0) previewCells = ls.Cells;
                    Thread.Sleep(100);
                }
                acc = ls.AccurateStats; off = ls.Accurate.OffWhy;
            }
            finally { ls.Stop(); }
            var ag = acc?.Field.Grid;
            float accBlock = ag == null ? float.NaN : Envelope.Blockage(ag, new Vector2(4, 0)) ?? float.NaN;
            Check("RZ-02: Domain Full + Accurate lands an Accurate result on a Full grid (the pass is not switched off for a Full Preview grid ~10x its budget)",
                acc != null && accBlock <= Envelope.BlockageLimit && previewCells > 8_000 * 1.5,
                acc == null ? $"no Accurate result after {clock.Elapsed.TotalSeconds:0} s · phase off: '{off}' · preview {previewCells} cells"
                            : $"preview {previewCells} cells · Accurate {ag!.Count} cells ({ag.Nx}x{ag.Ny}x{ag.Nz}, Dx {ag.Dx:0.###} m), blockage {accBlock * 100:0.0} % · {clock.Elapsed.TotalSeconds:0} s");
        }

        // Batch 3a S4-01: 20 rapid Start/Stop cycles (Start while the previous thread is still opening its session, the case that used to
        // fall through an 8 s join): one solver loop alive at a time, none left after the last Stop. WINDFLOW_STRESS_GPU=1 runs it on the GPU.
        {
            var ls = new LiveSolver(new WindFlow.App.Viewport.ParticleSystem());
            bool gpuStress = System.Environment.GetEnvironmentVariable("WINDFLOW_STRESS_GPU") == "1";
            int[] pauses = [0, 20, 60, 120, 250];
            LiveSolver.LoopsPeak = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int cycles = 0;
            try
            {
                if (!gpuStress) ls.UseCpuForTest(30_000);
                var house = SampleBuilding.Build();
                for (; cycles < 20; cycles++)
                {
                    ls.Start(house, new Vector2(4, 0));
                    Thread.Sleep(pauses[cycles % pauses.Length]);
                    if (cycles % 3 == 0) ls.Stop();      // a mix of Stop-then-Start and Start-on-Start
                }
            }
            finally { ls.Stop(); }
            int peak = LiveSolver.LoopsPeak, alive = LiveSolver.LoopsAlive;
            Check("live solver: 20 rapid Start/Stop cycles never run two solver threads and leave none behind",
                cycles == 20 && peak == 1 && alive == 0 && !ls.Active,
                $"{cycles} cycles in {clock.Elapsed.TotalSeconds:0.0} s ({(gpuStress ? "GPU" : "CPU 30k cells")}) · most loops alive at once {peak} · alive after the last Stop {alive}");

            // The old failure itself: the first thread cannot be interrupted for 8.5 s (kernel JIT, a long voxelisation). Start() used to give up
            // the join at 8 s and start a second thread beside it; now it waits, so the two never overlap.
            var held = new LiveSolver(new WindFlow.App.Viewport.ParticleSystem());
            held.UseCpuForTest(30_000);
            LiveSolver.LoopsPeak = 0;
            bool holdOnce = true;
            held.OpenSessionHook = () => { if (holdOnce) { holdOnce = false; Thread.Sleep(8500); } };
            var waited = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var house = SampleBuilding.Build();
                held.Start(house, new Vector2(4, 0));
                Thread.Sleep(150);
                held.Start(house, new Vector2(4, 0));        // the thread above is still held: this must wait for it
                double startWait = waited.Elapsed.TotalSeconds;
                held.Stop();
                Check("live solver: Start while the old thread cannot be interrupted for 8.5 s waits for it (no second thread after 8 s)",
                    LiveSolver.LoopsPeak == 1 && LiveSolver.LoopsAlive == 0 && startWait > 8.0,
                    $"second Start returned after {startWait:0.0} s · most loops alive at once {LiveSolver.LoopsPeak} · alive after Stop {LiveSolver.LoopsAlive}");
            }
            finally { held.Stop(); }
        }

        // Room isolate is view only: the box the renderer clips to is a pure function of the model, and a published
        // field is byte-identical before and after it is computed. The box must hold the room's footprint and its
        // openings (so inflow stays visible) and leave a neighbour's centre outside.
        {
            var sample = SampleBuilding.Build();
            var grid = Voxelizer.Build(sample, 40_000, System.Environment.ProcessorCount, null);
            int n = grid.Count;
            var ux = new float[n]; var uy = new float[n]; var uz = new float[n];
            var rnd = new Random(7);
            for (int i = 0; i < n; i++) { ux[i] = (float)rnd.NextDouble(); uy[i] = (float)rnd.NextDouble() * 0.5f; uz[i] = (float)rnd.NextDouble() * 0.1f; }
            var volume = WindFlow.App.Viewport.FieldVolume.Build(grid, sample, ux, uy, uz, null, null, true);
            static string Hash(WindFlow.App.Viewport.FieldVolume v)
            {
                var h = new HashCode();
                foreach (var t in v.Texels) h.Add(t);
                foreach (var o in v.Occupancy) h.Add(o);
                foreach (var s in v.Spawn) h.Add(s);
                h.Add(v.SpeedMax); h.Add(v.Nx); h.Add(v.Ny); h.Add(v.Nz); h.Add(v.HouseMin); h.Add(v.HouseMax);
                return h.ToHashCode().ToString("x8");
            }
            string before = Hash(volume);
            int hallIndex = RoomIsolation.Find(sample, "Hall");
            var hall = sample.Rooms[hallIndex];
            var box = RoomIsolation.Box(hall);
            string after = Hash(volume);
            bool Inside(Vector3 p) => box is { } b && p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z;
            var hallOpenings = sample.Openings.Where(o => o.Id is "w-hall-e" or "d-kit-hall").ToList();
            bool openingsIn = hallOpenings.Count == 2 && hallOpenings.All(o => Inside(o.Center));
            var loop = hall.Loops[0];
            bool footprintIn = loop.All(p => Inside(new Vector3(p.X, p.Y, hall.ZMin)) && Inside(new Vector3(p.X, p.Y, hall.ZMax)));
            var livingRoom = sample.Rooms.First(r => r.Id == "living");
            var lc = livingRoom.Loops[0].Aggregate(Vector2.Zero, (a, p) => a + p) / livingRoom.Loops[0].Length;
            bool neighbourOut = !Inside(new Vector3(lc.X, lc.Y, livingRoom.ZMin + 1.1f));
            Check("room isolate is view only: published field identical before and after, box holds the Hall and its openings, not the Living room",
                before == after && box != null && openingsIn && footprintIn && neighbourOut && hallIndex == 2,
                $"field hash {before} -> {after} · box {box?.Min} {box?.Max} · openings in {openingsIn} · footprint in {footprintIn} · living centre out {neighbourOut}");
        }

        // High Accuracy in-app: Standard options are the live options untouched; High adds Tu / approach turbulence, and a box when a focus is given.
        {
            var hm = SampleBuilding.Build();
            var liveOpts = new RunOptions { Backend = Backend.Cpu, TargetCells = 12345 };
            var std = AccurateRecipe.Options(AccurateMode.Standard, liveOpts, hm, null);
            var plan = HighAccuracy.Plan(hm, 0, 5_000_000, hm.Rooms.First(r => r.Name.StartsWith("Bedroom 1")).Name);
            var hi = AccurateRecipe.Options(AccurateMode.High, liveOpts, hm, plan.FineBox);
            var hiNoFocus = AccurateRecipe.Options(AccurateMode.High, liveOpts, hm, null);
            bool stdSame = !ReferenceEquals(std, liveOpts) && std.OnlineAge && std.OnlineAgePerRoom && !liveOpts.OnlineAge && std.TargetCells == liveOpts.TargetCells && !std.SupplyTuPerSource && std.WindTurbulence == null && std.FocusBox == null;
            bool highAdds = plan.FineBox != null && hi.SupplyTuPerSource && hi.WindTurbulence != null && hi.FocusBox == plan.FineBox &&
                            hiNoFocus.SupplyTuPerSource && hiNoFocus.WindTurbulence != null && hiNoFocus.FocusBox == null &&
                            hi.TargetCells == liveOpts.TargetCells && hi.Quality == liveOpts.Quality && !liveOpts.SupplyTuPerSource && liveOpts.WindTurbulence == null;
            Check("High Accuracy (app): Standard Accurate options = the live ones plus the online age (per-room landing); High adds Tu + wind turbulence, and a box only with a focus room",
                stdSame && highAdds, $"standard same {stdSame} · high adds {highAdds} · plan {plan.FocusRoomName} {(plan.FineBox is { } b ? $"{b.Size.X:0.#}x{b.Size.Y:0.#}x{b.Size.Z:0.#} m" : "no box")}");
        }

        // Honest status: the one mapping the pass strip, the room rows and the report cover share. A run that stopped on its time
        // budget (Capped, or still Running when the wall cap cut it) must never read "Settled"; only Converged does.
        {
            static WindFlow.Core.ConvergenceReport Report(WindFlow.Core.ConvergenceStopReason reason, int batches = 40, int kept = 24) =>
                new()
                {
                    Batches = batches,
                    Status = new WindFlow.Core.ConvergenceStatus
                    {
                        Reason = reason, Converged = reason == WindFlow.Core.ConvergenceStopReason.Converged,
                        Rooms = { new WindFlow.Core.RoomConvergence { RoomId = "r1", BatchCount = kept, HalfWidth = 0.6f } },
                    },
                };
            string settled = WindFlow.Report.ResultStatus.Text(Report(WindFlow.Core.ConvergenceStopReason.Converged));
            string capped = WindFlow.Report.ResultStatus.Text(Report(WindFlow.Core.ConvergenceStopReason.Capped));
            string running = WindFlow.Report.ResultStatus.Text(Report(WindFlow.Core.ConvergenceStopReason.Running));
            string preview = WindFlow.Report.ResultStatus.Text(null);
            bool ok = settled == WindFlow.Report.ResultStatus.Settled && capped == WindFlow.Report.ResultStatus.TimeLimited &&
                      running == WindFlow.Report.ResultStatus.TimeLimited && preview == WindFlow.Report.ResultStatus.Preview &&
                      !capped.Contains("Settled", StringComparison.OrdinalIgnoreCase) && !running.Contains("Settled", StringComparison.OrdinalIgnoreCase) &&
                      !WindFlow.Report.ResultStatus.ReportLine(Report(WindFlow.Core.ConvergenceStopReason.Capped)).Contains("settled:", StringComparison.Ordinal);
            Check("Accurate status: Converged -> Settled; Capped or cut while Running -> time limit reached, never Settled; no pass -> Preview",
                ok, $"settled \"{settled}\" · capped \"{capped}\" · running \"{running}\" · none \"{preview}\"");

            string cell = WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, provisional: false);
            string prov = WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, provisional: true);
            string none = WindFlow.Report.ResultStatus.AchCell(119.4f, float.PositiveInfinity, provisional: false, "†");
            Check("ACH cell: Accurate \"13.3 ±0.6\"; Preview keeps \"~13.3\" with no range; no finite half-width -> plain value with its mark",
                cell == "13.3 ±0.6" && prov == "~13.3" && none == "119.4†", $"\"{cell}\" · \"{prov}\" · \"{none}\"");

            // A run too short for a range (under MinTotalBatches batches in all, or under MinBatches kept): mean with "± —", its own
            // status line, and never when it converged or is a Preview.
            var shortAll = Report(WindFlow.Core.ConvergenceStopReason.Capped, batches: 6, kept: 2);
            var shortKept = Report(WindFlow.Core.ConvergenceStopReason.Capped, batches: 20, kept: 4);
            var longCapped = Report(WindFlow.Core.ConvergenceStopReason.Capped);
            bool tooShortOk = WindFlow.Report.ResultStatus.TooShort(shortAll, "r1") && WindFlow.Report.ResultStatus.TooShort(shortKept, "r1") &&
                              !WindFlow.Report.ResultStatus.TooShort(longCapped, "r1") && !WindFlow.Report.ResultStatus.TooShort(null, "r1") &&
                              !WindFlow.Report.ResultStatus.TooShort(Report(WindFlow.Core.ConvergenceStopReason.Converged, batches: 20, kept: 10), "r1") &&
                              WindFlow.Report.ResultStatus.Text(shortAll) == WindFlow.Report.ResultStatus.TooShortText &&
                              WindFlow.Report.ResultStatus.Text(shortKept) == WindFlow.Report.ResultStatus.TooShortText &&
                              WindFlow.Report.ResultStatus.Text(longCapped) == WindFlow.Report.ResultStatus.TimeLimited &&
                              WindFlow.Report.ResultStatus.ReportLine(shortAll).Contains("too short for ranges", StringComparison.Ordinal);
            string shortCell = WindFlow.Report.ResultStatus.AchCell(155.4f, 355.3f, provisional: false, tooShort: true);
            string shortPrev = WindFlow.Report.ResultStatus.AchCell(155.4f, 355.3f, provisional: true, tooShort: true);
            Check("Too-short run: under 16 batches in all or 5 kept -> \"± —\" and the too-short status; a long capped run keeps its range",
                tooShortOk && shortCell == "155.4 ± —" && shortPrev == "~155.4", $"\"{shortCell}\" · \"{shortPrev}\" · \"{WindFlow.Report.ResultStatus.Text(shortAll)}\"");
        }

        // Batch 3c, S6-03 / P16-01 / P16-03: the tick, the green chip and "settled" hang on ResultStatus.IsSettled alone; a pass cut by its time budget (Capped,
        // still Running, too short) never reads settled or ticked. A Settled room shows the 99 % width its stop rule tested, a capped one the 95 % range, and a
        // room whose batches still drift is marked.
        {
            static WindFlow.Core.ConvergenceReport Rep(WindFlow.Core.ConvergenceStopReason reason, int batches, WindFlow.Core.RoomConvergence room) =>
                new() { Batches = batches, Status = new WindFlow.Core.ConvergenceStatus
                    { Reason = reason, Converged = reason == WindFlow.Core.ConvergenceStopReason.Converged, Rooms = { room } } };
            var okRoom = new WindFlow.Core.RoomConvergence { RoomId = "r1", BatchCount = 24, HalfWidth = 0.6f, StopHalfWidth = 0.9f, Tolerance = 1f, Converged = true, Stationary = true };
            var driftRoom = new WindFlow.Core.RoomConvergence { RoomId = "r1", BatchCount = 24, HalfWidth = 0.6f, StopHalfWidth = 0.9f, Tolerance = 0.5f, Converged = false, Stationary = false };
            var shortRoom = new WindFlow.Core.RoomConvergence { RoomId = "r1", BatchCount = 2, HalfWidth = 5f, StopHalfWidth = 9f, Tolerance = 0.5f, Converged = false, Stationary = false };
            var settledRep = Rep(WindFlow.Core.ConvergenceStopReason.Converged, 40, okRoom);
            var cappedRep = Rep(WindFlow.Core.ConvergenceStopReason.Capped, 40, driftRoom);
            var runningRep = Rep(WindFlow.Core.ConvergenceStopReason.Running, 40, driftRoom);
            var shortRep = Rep(WindFlow.Core.ConvergenceStopReason.Capped, 6, shortRoom);
            bool settledOk = WindFlow.Report.ResultStatus.IsSettled(settledRep) && WindFlow.Report.ResultStatus.StripText(settledRep, 5.1).Contains('✓') &&
                             WindFlow.Report.ResultStatus.TagText(settledRep) == "SETTLED" && WindFlow.Report.ResultStatus.ResultName(settledRep).Contains("settled");
            bool cutOk = true; var cutDetail = new List<string>();
            foreach (var cut in new[] { cappedRep, runningRep, shortRep })
            {
                string strip = WindFlow.Report.ResultStatus.StripText(cut, 5.1), name = WindFlow.Report.ResultStatus.ResultName(cut);
                cutOk &= !WindFlow.Report.ResultStatus.IsSettled(cut) && !strip.Contains('✓') && !strip.Contains("settled", StringComparison.OrdinalIgnoreCase) &&
                         WindFlow.Report.ResultStatus.TagText(cut) == "TIME-LIMITED" && !name.Contains("settled", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("time limit reached") && strip.Contains("Time limit reached");
                cutDetail.Add($"\"{strip}\"");
            }
            cutOk &= !WindFlow.Report.ResultStatus.IsSettled(null) && AccurateTracker.StageText(AccuratePhase.Accurate, 1, 0, "") == "Averaged result";
            Check("S6-03: the tick, \"settled\" and the green chip need every room Settled; a time-limited, running-cut or too-short pass reads \"time limit reached\" in the strip, tag and trust head",
                settledOk && cutOk, $"settled \"{WindFlow.Report.ResultStatus.StripText(settledRep, 5.1)}\" · tag {WindFlow.Report.ResultStatus.TagText(settledRep)} · cut: {string.Join(" | ", cutDetail)}");

            var rs = WindFlow.Report.ResultStatus.Shown(okRoom);
            var rc = WindFlow.Report.ResultStatus.Shown(driftRoom);
            string tipSettled = WindFlow.Report.ResultStatus.RangeTip(okRoom, rs), tipCapped = WindFlow.Report.ResultStatus.RangeTip(driftRoom, rc);
            bool widthOk = rs == 0.9f && rc == 0.6f && tipSettled.Contains("99 %") && !tipSettled.Contains("95 %") && !tipSettled.Contains("drifting") &&
                           tipCapped.Contains("95 %") && !tipCapped.Contains("99 %") && tipCapped.Contains("Still drifting") && tipCapped.Contains("aim was ±0.5");
            bool driftOk = WindFlow.Report.ResultStatus.Drifting(cappedRep, "r1") && !WindFlow.Report.ResultStatus.Drifting(settledRep, "r1") &&
                           !WindFlow.Report.ResultStatus.Drifting(shortRep, "r1") && !WindFlow.Report.ResultStatus.Drifting(null, "r1") &&
                           WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, false, "", false, true) == "13.3 ±0.6 ⚠" &&
                           WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, false) == "13.3 ±0.6" && WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, true, "", false, true) == "~13.3";
            // The monitor itself stores the tested width: the 99 % t-value is the larger one for the same data.
            var mon = new WindFlow.Core.ConvergenceMonitor(); var rngM = new Random(7);
            for (int b = 0; b < 40; b++) mon.AddBatch([("a", 10f + (float)(rngM.NextDouble() - 0.5))]);
            var monRoom = mon.Evaluate().Rooms[0];
            // RA-03: a Standard report (steady age, no fresh-air ±) still marks a non-stationary room: the cell mark, the tooltip sentence and the PDF cell note.
            var stdDrift = Rep(WindFlow.Core.ConvergenceStopReason.Capped, 40, driftRoom);   // AgeOnline false = Standard
            bool stdDrifting = WindFlow.Report.ResultStatus.Drifting(stdDrift, "r1") && !WindFlow.Report.ResultStatus.FreshHasRange(stdDrift);
            string stdCell = WindFlow.Report.ResultStatus.AchCell(13.3f, float.NaN, false, "", false, stdDrifting);
            string stdPdf = "13.3" + WindFlow.Report.ResultStatus.PdfDriftSuffix(stdDrift, "r1");
            Check("RA-03: a Standard result (no ±) with a non-stationary room still shows the drift mark in the cell, the warning in the tooltip text and \"(drifting)\" in the PDF row",
                stdDrifting && stdCell == "13.3 ⚠" && WindFlow.Report.ResultStatus.AchCell(13.3f, float.NaN, false) == "13.3" &&
                WindFlow.Report.ResultStatus.DriftTip.Contains("Still drifting") && stdPdf == "13.3 (drifting)" && WindFlow.Report.ResultStatus.PdfDriftSuffix(settledRep, "r1") == "",
                $"cell \"{stdCell}\" · pdf \"{stdPdf}\" · tip \"{WindFlow.Report.ResultStatus.DriftTip}\"");

            Check("P16-01 / P16-03: a Settled room shows the 99 % width its stop rule tested (tooltip says so), a capped one the 95 % range; a drifting room carries a mark and the warning",
                widthOk && driftOk && monRoom.StopHalfWidth > monRoom.HalfWidth && float.IsFinite(monRoom.StopHalfWidth),
                $"settled ± {rs:0.0} / capped ± {rc:0.0} · monitor hw95 {monRoom.HalfWidth:0.000} < hw99 {monRoom.StopHalfWidth:0.000} · drifting cell \"{WindFlow.Report.ResultStatus.AchCell(13.3f, 0.6f, false, "", false, true)}\"");
        }

        // 6f (S3-11): the app's Accurate pass and the headless --solve run ONE driver (AveragedPass). A fake session feeds the same batch series through the headless
        // loop (AveragedPass.Run: 100-step bursts, one clock) and through the app's loop (37-step bursts, a manual clock, batches delivered through a publication
        // queue the way LiveSolver's epoch gate does): the stop decision, batch count, kept batches and the landed mean flow must agree, and with the ConvergenceMonitor
        // fed the same series on its own. A non-finite batch stops both at the same batch as Diverged.
        {
            var m6f = new BuildingModel();
            static double Series6f(int k) => 10 + 0.3 * Math.Sin(2.3 * k);                    // pull 0 is the settle-end probe; batch b is pull b
            static double Bad6f(int k) => k == 7 ? double.NaN : Series6f(k);
            static PassSettings Cfg6f(Func<Action<Action>>? publisher = null) => new()
            {
                CapSeconds = 1e9, AnalyseInline = true, Publisher = publisher, Probe = _ => 20f,
                Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux[0])], []),
            };
            PassOutcome Headless6f(Func<int, double> series) =>
                AveragedPass.Run(new FakePassSession(series), m6f, Vector2.Zero, new RunOptions(), Cfg6f(), solveScalars: false);
            PassOutcome App6f(Func<int, double> series)
            {
                var pending = new Queue<Action>();
                var fake = new FakePassSession(series);
                var pass = new AveragedPass(fake, m6f, Vector2.Zero, new RunOptions(), Cfg6f(() => a => pending.Enqueue(a)));
                double clock = 0; int guard = 0;
                while (!pass.Done && guard++ < 200000)
                {
                    fake.Step(37, default);
                    while (pending.Count > 0) pending.Dequeue()();
                    clock += 0.5;
                    pass.Tick(clock);
                }
                return pass.Land();
            }
            var hl = Headless6f(Series6f); var ap = App6f(Series6f);
            var hd = Headless6f(Bad6f); var ad = App6f(Bad6f);
            // the monitor alone on the same numbers: the first batch count at which it says Converged, and the mean of the batches it keeps
            var refMon = new WindFlow.Core.ConvergenceMonitor(); int refN = 0;
            while (refN < 200)
            {
                refMon.AddBatch([("r", (float)Series6f(++refN))]);
                if (refMon.Evaluate().Reason != WindFlow.Core.ConvergenceStopReason.Running) break;
            }
            int refD = WindFlow.Core.ConvergenceMonitor.TruncationFor(refN);
            double refMean = Enumerable.Range(refD + 1, refN - refD).Average(Series6f);
            bool same = hl.Report.Status.Reason == ap.Report.Status.Reason && hl.Report.Batches == ap.Report.Batches && hl.Kept == ap.Kept &&
                        hl.Report.BatchSteps == ap.Report.BatchSteps && Math.Abs(hl.Field.Ux[0] - ap.Field.Ux[0]) < 1e-6f && hl.Landed == ap.Landed;
            bool vsMonitor = hl.Report.Status.Reason == WindFlow.Core.ConvergenceStopReason.Converged && hl.Report.Batches == refN && hl.Kept == refN - refD &&
                             Math.Abs(hl.Field.Ux[0] - refMean) < 1e-4;
            bool diverged = hd.Report.Status.Reason == WindFlow.Core.ConvergenceStopReason.Diverged && ad.Report.Status.Reason == WindFlow.Core.ConvergenceStopReason.Diverged &&
                            hd.Report.Batches == 7 && ad.Report.Batches == 7;
            Check("6f: the app's Accurate loop and the headless --solve run one driver: the same batch series gives the same stop decision, batch count, kept batches and landing, and the monitor's own",
                same && vsMonitor && diverged,
                $"headless {hl.Report.Status.Reason} after {hl.Report.Batches} batches (kept {hl.Kept}, landed {hl.Field.Ux[0]:0.0000}) · app {ap.Report.Status.Reason} after {ap.Report.Batches} (kept {ap.Kept}, landed {ap.Field.Ux[0]:0.0000}) · " +
                $"monitor alone {refN} batches, mean {refMean:0.0000} · non-finite batch: headless {hd.Report.Status.Reason} at {hd.Report.Batches}, app {ad.Report.Status.Reason} at {ad.Report.Batches}");
        }

        // RU-01 (cancel inside the settle-end steady age solve), RU-02 (the cap is real elapsed time), RU-03 (scalar notes once), RU-05 (a cap inside the settle lands nothing).
        {
            // RU-01: the sample house on a small grid, a still field; the token is cancelled the moment the solve starts (the pass says "computing air age..." first). The pass must
            // leave the solve within a second: the token reaches ScalarSolver (every sweep checks it).
            var house = SampleBuilding.Build();
            var g01 = Voxelizer.Build(house, 60_000, System.Environment.ProcessorCount, null);
            int n01 = g01.Count;
            var field01 = new FlowField { Grid = g01, Ux = new float[n01], Uy = new float[n01], Uz = new float[n01], P = new float[n01], AverageSamples = 100 };
            using var cts01 = new CancellationTokenSource();
            long cancelAt = 0; var said = new List<string>();
            var cfg01 = new PassSettings { CapSeconds = 1e9, Probe = _ => 20f, SettleFactor = 1, OnStatus = t => { said.Add(t); if (t == AveragedPass.ComputingAgeStatus) { cancelAt = System.Diagnostics.Stopwatch.GetTimestamp(); cts01.Cancel(); } } };
            var sess01 = new GridPassSession(field01);
            var pass01 = new AveragedPass(sess01, house, new Vector2(4, 0), new RunOptions { OnlineAge = true }, cfg01);
            sess01.Step((int)pass01.SettleSteps, default);
            bool cancelled01 = false;
            try { pass01.Tick(1, 0, cts01.Token); } catch (OperationCanceledException) { cancelled01 = true; }
            double leftMs = cancelAt == 0 ? double.NaN : (System.Diagnostics.Stopwatch.GetTimestamp() - cancelAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Check("RU-01: cancelling during the settle-end steady age solve returns within 1 s, after the pass said it was computing the air age",
                cancelled01 && leftMs < 1000 && said.Contains(AveragedPass.ComputingAgeStatus),
                $"cancelled {cancelled01} · left the solve {leftMs:0} ms after the cancel · status calls [{string.Join(" | ", said)}] · {n01} cells");

            // RU-03: two solves on one grid leave each scalar note once (the second one strips the first's)
            var o03 = new RunOptions();
            ScalarSolver.Solve(house, field01, o03, ageOnly: true);
            ScalarSolver.Solve(house, field01, o03, ageOnly: true);
            int notes03 = g01.Warnings.Count(w => w.StartsWith("Age of air has no steady state", StringComparison.Ordinal));
            Check("RU-03: the steady age solved twice on one grid leaves its 'no steady state' note once", notes03 == 1, $"{notes03} copies of the note after two solves");

            // RU-02: a fake session whose 100-step bursts take 2.5 s (the old stall rule counted none of that time): the cap (6 s) ends the pass at the third tick; the
            // pass lands its unfinished window. Hard ceiling: 3 x the cap on the raw clock ends a pass whose user pauses keep the cap clock low.
            var m02 = new BuildingModel();
            var slow = new FakePassSession(k => 10 + 0.3 * Math.Sin(2.3 * k)) { Warmup = 10, StepSleepMs = 2500 };
            var clock02 = System.Diagnostics.Stopwatch.StartNew();
            var out02 = AveragedPass.Run(slow, m02, Vector2.Zero, new RunOptions(), new PassSettings { CapSeconds = 6, AnalyseInline = true, Probe = _ => 20f, Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux[0])], []) }, solveScalars: false);
            double secs02 = clock02.Elapsed.TotalSeconds;
            var calm = new FakePassSession(k => 10 + 0.3 * Math.Sin(2.3 * k)) { Warmup = 10 };
            var ceil = new AveragedPass(calm, m02, Vector2.Zero, new RunOptions(), new PassSettings { CapSeconds = 10, AnalyseInline = true, Probe = _ => 20f, Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux[0])], []) });
            calm.Step(20, default);
            ceil.Tick(1);                       // the settle ends: averaging
            calm.Step(100, default);
            ceil.Tick(31, 25);                  // 6 s of stepping (under the cap) but 31 s on the raw clock: 3 x the cap
            var noisy = new FakePassSession(k => 10 + 12 * (((k * 7919) % 13) / 13.0 - 0.5)) { Warmup = 10 };
            // RW-01: fast ticks and a short cap: the pass runs past 60 batches (no batch-count stop) and ends at the cap, as a time limit.
            var pass60 = new AveragedPass(noisy, m02, Vector2.Zero, new RunOptions(), new PassSettings { CapSeconds = 8, AnalyseInline = true, Probe = _ => 20f, Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux[0])], []) });
            double clock60 = 0; int guard60 = 0, past60 = 0;
            while (!pass60.Done && guard60++ < 100000) { noisy.Step(37, default); pass60.Tick(clock60 += 0.001); if (pass60.BatchCount > 60 && past60 == 0) past60 = guard60; }
            Check("RU-02/RW-01: a fake session whose bursts take 2.5 s ends at the 6 s cap (not never); 3 x the cap on the raw clock ends a pass; fast ticks with a short cap pass 60 batches without stopping and end at the cap with WallCapHit",
                out02.Report.Status.Reason == ConvergenceStopReason.Capped && out02.Report.WallCapHit && secs02 is > 6 and < 13 &&
                ceil.Done && ceil.Reason == ConvergenceStopReason.Capped && pass60.Done && pass60.Reason == ConvergenceStopReason.Capped && pass60.BatchCount > 60 && past60 > 0 && pass60.Land().Report.WallCapHit,
                $"cap run {out02.Report.Status.Reason} after {secs02:0.0} s (cap 6) · raw-clock ceiling done {ceil.Done} ({ceil.SteppingSeconds:0} s stepping at 31 s) · noisy series {pass60.Reason} at {pass60.BatchCount} batches (passed 60 at tick {past60}, batch {pass60.BatchSteps} steps)");

            // RU-05: the cap runs out inside the settle: no flow is landed, in the app's loop and in Run (which says so with an exception that is not a GPU fault)
            var early = new FakePassSession(k => 10) { Warmup = 1000 };
            var pass05 = new AveragedPass(early, m02, Vector2.Zero, new RunOptions(), new PassSettings { CapSeconds = 5, AnalyseInline = true, Probe = _ => 20f });
            early.Step(100, default); pass05.Tick(3); bool after3 = pass05.Done;
            early.Step(100, default); pass05.Tick(6);
            string thrown05 = "";
            try { AveragedPass.Run(new FakePassSession(k => 10) { Warmup = 1000, StepSleepMs = 600 }, m02, Vector2.Zero, new RunOptions(), new PassSettings { CapSeconds = 1 }, solveScalars: false); }
            catch (PassTimeLimitException e) { thrown05 = e.Message; }
            Check("RU-05: a cap that runs out during the settle lands no flow field and says so (app loop and headless run); it is not a GPU fault",
                !after3 && pass05.Done && pass05.TimeLimitBeforeAveraging && thrown05 == AveragedPass.NoAveragingNote && !LbmSolver.IsGpuFault(new PassTimeLimitException("x")),
                $"app loop: done at 3 s {after3}, at 6 s {pass05.Done}, before averaging {pass05.TimeLimitBeforeAveraging} · headless: \"{thrown05}\"");
        }

        // 6k (P14-06): a real averaged pass (headless route: Simulation.Run with AutoConverge -> AveragedPass on a session) on a small closed room with a supply and a return, CPU: the pass
        // starts the online age at the end of its settle, lands its time mean, and says so; the room's FreshAch is 3600 / its volume-mean age and carries a range from the per-batch ages.
        {
            var m6k = ClosedRoomModel(out var dom6k);
            var o6k = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, AutoConverge = true, WallCapSeconds = 90, TargetCells = 1500, AgeOfAir = true, OnlineAge = true,   // AGE-FIX (rule 3): the landed default is now the steady age; this check keeps testing the opt-in online age
                Domain = dom6k };
            var clock6k = System.Diagnostics.Stopwatch.StartNew();
            var r6k = Simulation.Run(m6k, Vector2.Zero, o6k);
            var rr6k = r6k.Rooms.FirstOrDefault();
            var rc6k = r6k.Convergence?.Status.Rooms.FirstOrDefault();
            int finite6k = r6k.Scalars.Age.Count(float.IsFinite);
            bool pass6k = r6k.Convergence is { AgeOnline: true } && r6k.Convergence.AgeLabel == AveragedPass.AgeOnlineLabel && finite6k > 100 && rr6k != null &&
                          float.IsFinite(rr6k.VolumeMeanAge) && MathF.Abs(rr6k.FreshAch - 3600f / rr6k.VolumeMeanAge) < 1e-3f * rr6k.FreshAch && rc6k != null && float.IsFinite(rc6k.FreshHalfWidth) &&
                          WindFlow.Report.ResultStatus.AgeLine(r6k.Convergence) == AveragedPass.AgeOnlineLabel;
            Check("6k: an averaged pass on a small closed room runs the online age, lands its time mean and says so (age: online), with a fresh-air range from the per-batch ages",
                pass6k, r6k.Convergence == null ? "no convergence report" :
                    $"{r6k.Convergence.Status.Reason} after {r6k.Convergence.Batches} batches · \"{r6k.Convergence.AgeLabel}\" · {finite6k} age cells · volume-mean age {rr6k?.VolumeMeanAge:0.0} s -> fresh-air ACH {rr6k?.FreshAch:0.00} ± {rc6k?.FreshHalfWidth:0.00} · {clock6k.Elapsed.TotalSeconds:0} s");

            // RU-08: the same room warm-started from that result on ANOTHER grid (the app's Preview -> Accurate seed): the age warm start waits for a probe window of the session's own
            // grid, so the pass still runs the online age ("age: online"), not the steady fallback
            var o08 = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, AutoConverge = true, WallCapSeconds = 60, TargetCells = 1800, AgeOfAir = true, OnlineAge = true, WarmStart = r6k.Field, Domain = dom6k };   // AGE-FIX (rule 3): opt-in online age, as above
            var clock08 = System.Diagnostics.Stopwatch.StartNew();
            var r08 = Simulation.Run(m6k, Vector2.Zero, o08);
            Check("RU-08: a warm-seeded pass (seed on another grid) runs the online age and labels it 'age: online'",
                r08.Convergence is { AgeOnline: true } && r08.Convergence.AgeLabel == AveragedPass.AgeOnlineLabel && r08.Field.Grid.Count != r6k.Field.Grid.Count,
                r08.Convergence == null ? "no convergence report" : $"\"{r08.Convergence.AgeLabel}\" · seed grid {r6k.Field.Grid.Count} cells, pass grid {r08.Field.Grid.Count} · {r08.Convergence.Status.Reason} after {r08.Convergence.Batches} batches · {clock08.Elapsed.TotalSeconds:0} s");

            // AGE-FIX: the same room with the default options lands the steady age: its label, no fresh-air range (value only), and the one disclosure note.
            var oAf = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, AutoConverge = true, WallCapSeconds = 60, TargetCells = 1500, AgeOfAir = true, Domain = dom6k };
            var rAf = Simulation.Run(m6k, Vector2.Zero, oAf);
            var rrAf = rAf.Rooms.FirstOrDefault();
            bool passAf = rAf.Convergence is { AgeOnline: false } && rAf.Convergence.AgeLabel == AveragedPass.AgeSteadyLabel && rrAf != null && float.IsFinite(rrAf.FreshAch) &&
                          float.IsNaN(WindFlow.Report.ResultStatus.FreshHalfWidth(rAf.Convergence, rrAf)) && !WindFlow.Report.ResultStatus.FreshHasRange(rAf.Convergence) &&
                          WindFlow.Report.ResultStatus.AgeNote(rAf.Convergence) == AveragedPass.AgeSteadyNote + " " + AveragedPass.GridSensitivityNote && /* rule 3: the one note now also carries the grid-sensitivity sentence (queue W2) */ AveragedPass.AgeSteadyNote.Contains("by about 30 % either way per room",StringComparison.Ordinal) && !AveragedPass.AgeSteadyNote.Contains("conservative", StringComparison.Ordinal);
            Check("AGE-FIX: the default averaged pass lands the steady age (age: steady on mean flow), shows no fresh-air ±, and carries the disclosure note",
                passAf, rAf.Convergence == null ? "no convergence report" : $"\"{rAf.Convergence.AgeLabel}\" · fresh-air ACH {rrAf?.FreshAch:0.00} · fresh ± {(rrAf == null ? float.NaN : WindFlow.Report.ResultStatus.FreshHalfWidth(rAf.Convergence, rrAf))} · note \"{WindFlow.Report.ResultStatus.AgeNote(rAf.Convergence)}\"");

            // AGE-HIGH: High asks for the online age (Standard too, per room, since the per-room batch; rAf below has it off); the online pass (r6k, the High landing) carries the steady age per room and for the building as the
            // conservative estimate and the gusty-wind note; the Standard pass (rAf) has neither.
            var hiO = AccurateRecipe.Options(AccurateMode.High, new RunOptions(), m6k, null);
            bool passAh = hiO.OnlineAge && AccurateRecipe.Options(AccurateMode.Standard, new RunOptions(), m6k, null).OnlineAge &&   /* Standard now asks for the online age too (per room) */
                          WindFlow.Report.ResultStatus.HasConservative(r6k.Convergence) && rr6k != null && float.IsFinite(rr6k.ConservativeFreshAch) && rr6k.ConservativeFreshAch > 0 &&
                          float.IsFinite(r6k.Ledger.ConservativeFreshAch) && r6k.Scalars.SteadyAge.Length == r6k.Scalars.Age.Length &&
                          WindFlow.Report.ResultStatus.AgeNote(r6k.Convergence) == AveragedPass.AgeGustyNote + " " + AveragedPass.GridSensitivityNote &&
                          !WindFlow.Report.ResultStatus.HasConservative(rAf.Convergence) && rrAf != null && float.IsNaN(rrAf.ConservativeFreshAch) && float.IsNaN(rAf.Ledger.ConservativeFreshAch);
            Check("AGE-HIGH: High lands the online age as the fresh-air ACH and carries the steady conservative estimate per room and for the building with the gusty-wind note; Standard is unchanged (steady, no conservative value)",
                passAh, $"High: online {rr6k?.FreshAch:0.00} ACH, conservative {rr6k?.ConservativeFreshAch:0.00} ACH (building {r6k.Ledger.FreshAch:0.00} / {r6k.Ledger.ConservativeFreshAch:0.00}) · Standard conservative {rrAf?.ConservativeFreshAch}");
        }

        // Batch 3c, C4: each way a result can sit outside the validated envelope has a note. Sample house on the Preview-size grid (Dx ~ 255 mm).
        {
            var house = SampleBuilding.Build();
            var grid = Voxelizer.Build(house, 1_500_000, System.Environment.ProcessorCount, null);
            var steady = new Vector2(4f, 0f);                       // wind from 270, blowing toward +X
            string Joined(IEnumerable<string> l) => string.Join(" || ", l);
            var narrow = Envelope.NarrowOpenings(grid, house.Openings).ToList();
            var preview = Envelope.PreviewOpenings(grid, house.Openings).ToList();
            bool cells = narrow.Any(n => n.StartsWith("Opening Hall window is only")) && narrow.Any(n => n.StartsWith("Opening Living to kitchen is only")) &&
                         !narrow.Any(n => n.Contains("Living south window")) &&
                         preview.Any(n => n.StartsWith("Opening Kitchen window")) && preview.Any(n => n.Contains("Living south window")) && !preview.Any(n => n.Contains("Hall window")) &&
                         preview.All(n => n.Contains("size quantised to the grid: ±1 cell row; use Standard or High for this opening's flow"));
            Check("C4: an open opening under 4 cells across gets a note (Hall window, interior doors); one 4-6 cells across in Preview says its size is quantised (+-1 cell row)",
                cells, $"Dx {grid.Dx * 1000:0} mm · narrow {narrow.Count}: {Joined(narrow.Take(2))} · preview {preview.Count}: {Joined(preview.Take(1))}");

            // Throat empty / blocked (P12-05): a 0.5 m wall at 0.2 m cells with the opening's Depth set to 0.1 m is left blocked; Depth = wall thickness is open.
            BuildingModel ThickWall(float depth, Vector3 centre, float thickness = 0.5f)
            {
                var wm = new BuildingModel { Name = "thick", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
                wm.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, 0), new Vector3(thickness, 6f, 3f)));
                wm.Openings.Add(new Opening { Id = "w", Name = "w", Kind = OpeningKind.Window, IsOpen = true, Center = centre, Normal = -Vector3.UnitX, Width = 1.0f, Height = 1.0f, Depth = depth });
                return wm;
            }
            // thin, centred: the carve leaves an enclosed pocket that the flood fill turns solid (empty throat); thin, off the mid-plane: reaches one face only (a solid layer left).
            var mThin = ThickWall(0.1f, new Vector3(0.25f, 3f, 1.5f)); var mSide = ThickWall(0.1f, new Vector3(0.4f, 3f, 1.5f));
            var mFull = ThickWall(0.5f, new Vector3(0.25f, 3f, 1.5f)); var mFar = ThickWall(0.5f, new Vector3(500f, 500f, 1.5f));
            var gThin = Voxelizer.Build(mThin, 500_000, System.Environment.ProcessorCount, null);
            var gSide = Voxelizer.Build(mSide, 500_000, System.Environment.ProcessorCount, null);
            var gFull = Voxelizer.Build(mFull, 500_000, System.Environment.ProcessorCount, null);
            var blocked = Envelope.BlockedOpenings(gThin, mThin.Openings).ToList();
            var layer = Envelope.BlockedOpenings(gSide, mSide.Openings).ToList();
            var open = Envelope.BlockedOpenings(gFull, mFull.Openings).ToList();
            var empty = Envelope.BlockedOpenings(gFull, mFar.Openings).ToList();
            var houseBlocked = Envelope.BlockedOpenings(grid, house.Openings).ToList();
            // P12-05 (batch 4): the thin-Depth and off-mid-plane openings are now carved fully through, so they no longer get the blocked note (they did: 1 and 1).
            Check("C4 / P12-05: an open opening with a thin Depth (enclosed pocket) or off the wall's mid-plane is carved through and gets no blocked note; one with no cell in its throat still gets a note; a through opening and the sample house get none",
                blocked.Count == 0 && layer.Count == 0 && open.Count == 0 && empty.Count == 1 && empty[0].Contains("no open cells in its throat") && houseBlocked.Count == 0,
                $"Dx {gThin.Dx * 1000:0} mm · thin: {Joined(blocked)} · off mid-plane: {Joined(layer)} · full: {open.Count} · far: {Joined(empty)} · sample house: {houseBlocked.Count}");

            // RX-10d / RX-05: a 1.2 m wall with the opening 0.15 m from one face has 1.05 m of wall on its far side, past the 1 m carve-through cap, so a layer is left and the note blames the right cause.
            var mThick = ThickWall(0.1f, new Vector3(0.15f, 3f, 1.5f), 1.2f);
            var gThick = Voxelizer.Build(mThick, 500_000, System.Environment.ProcessorCount, null);
            var thick = Envelope.BlockedOpenings(gThick, mThick.Openings).ToList();
            Check("C4 / RX-10d: an opening in a 1.2 m wall (over the 1 m carve-through cap) is left blocked, and the note says the wall is over 1 m thick, end to end from the geometry",
                thick.Count == 1 && thick[0].Contains("a solid layer still blocks it") && thick[0].Contains("over 1 m thick"), $"Dx {gThick.Dx * 1000:0} mm · {thick.Count} note: {Joined(thick)}");

            var steadyNotes = Envelope.GustDrivenRooms(grid, house, steady, new RunOptions()).ToList();
            var gustyNotes = Envelope.GustDrivenRooms(grid, house, steady, new RunOptions { WindTurbulence = new WindTurbulence { LengthScale = 10f, RefIu = 0.2f } }).ToList();
            bool gust = steadyNotes.Any(n => n.StartsWith("Room Living: gust-driven") && n.Contains("parallel to the wind")) && steadyNotes.Any(n => n.StartsWith("Room Kitchen: gust-driven") && n.Contains("one facade")) &&
                        !steadyNotes.Any(n => n.Contains("Bedroom 1")) && !steadyNotes.Any(n => n.Contains("Landing")) && gustyNotes.Count == 0 &&
                        Envelope.GustDrivenRooms(grid, house, Vector2.Zero, new RunOptions()).Count == 0;
            Check("C4: under steady wind a room whose outside openings are all on one facade or all parallel to the wind is flagged gust-driven (Living, Kitchen); not with gusts, still air, or a through-flow room",
                gust, $"{steadyNotes.Count} flagged: {string.Join(", ", steadyNotes.Select(n => n[5..n.IndexOf(':')]))} · with gusts {gustyNotes.Count}");

            var ww = new RunOptions { WallModel = WallModel.WernerWengle };
            bool wall = Envelope.WallModelOn(ww) && !Envelope.WallModelOn(new RunOptions { WallModel = WallModel.WernerWengle, Thermal = true }) && !Envelope.WallModelOn(new RunOptions { WallModel = WallModel.None });
            float? blockage = Envelope.Blockage(grid, steady), blockageN = Envelope.Blockage(grid, new Vector2(0f, 4f));
            bool block = blockage is > 0.06f and < 0.12f && blockageN is > 0.08f and < 0.15f && Envelope.Blockage(new VoxelGrid { Nx = 8, Ny = 8, Nz = 8, Dx = 1 }, steady) == null &&
                         Envelope.BlockageNote(blockage!.Value).Contains("3 %") && Envelope.BudgetClampNote(8_000_000, 5_200_000, 0.17f).Contains("graphics memory");
            var applied = new VoxelGrid { Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz, Dx = grid.Dx, Origin = grid.Origin, Cell = grid.Cell, BuildingSize = grid.BuildingSize };
            Envelope.Apply(applied, house, steady, ww); int once = applied.Warnings.Count;
            Envelope.Apply(applied, house, steady, ww);
            bool twice = applied.Warnings.Count == once && applied.Warnings.Contains(Envelope.WallModelNote) && applied.Warnings.Any(w => w.StartsWith("Wind blockage")) &&
                         applied.Warnings.Any(w => w.StartsWith("Room Living: gust-driven"));
            bool plain = new[] { narrow[0], preview[0], "Opening w: a solid layer still blocks it at 200 mm cells (the wall here is over 1 m thick, or the opening lies outside its wall), so no air passes through it.", empty[0], steadyNotes[0], Envelope.WallModelNote, Envelope.BlockageNote(0.087f), Envelope.BudgetClampNote(8_000_000, 5_200_000, 0.17f), Voxelizer.SealedBalanceNote("Bathroom", 0.10f, 0.05f, 0.10f) }
                .All(n => Notes.Plain(n) != Notes.Plain("zzz unmatched"));
            Check("C4: wall model on (isothermal only), domain blockage over 3 % (sample house 10.5 % W wind, 13.2 % S wind), the graphics-memory clamp; Apply is idempotent and every note has a plain sentence",
                wall && block && twice && plain,
                $"blockage W wind {blockage * 100:0.0} % · S wind {blockageN * 100:0.0} % · {once} notes after Apply, {applied.Warnings.Count} after the second · plain: {Notes.Plain(Envelope.BlockageNote(0.087f))}");

            // 6c (P13b-03): the Full wind domain. W wind: inflow (west) and side faces >= 5 H, outflow (east) >= 10 H + the sponge, top >= 5 H, blockage
            // <= 3 %; the VRAM guard steps down to downstream 5 H, then side/top at the blockage limit, then Compact (never a coarser Dx); an oblique wind
            // and the built Full grid also stay <= 3 %.
            float H = grid.BuildingSize.Z, fdx = grid.Dx, sponge = LbmSolver.WindSpongeCells * fdx;
            var p0 = Voxelizer.PlanFull(house, steady, fdx, 0);
            var p1 = Voxelizer.PlanFull(house, steady, fdx, p0.Cells - 1);
            var p2 = Voxelizer.PlanFull(house, steady, fdx, p1.Cells - 1);
            var p3 = Voxelizer.PlanFull(house, steady, fdx, 1000);
            var sw = new Vector2(2.83f, 2.83f);
            var pSw = Voxelizer.PlanFull(house, sw, fdx, 0);
            // RZ-08: SW wind (inflow faces west and south, no tangential face) stepped down: blockage still <= 3 %, upstream still >= 5 H.
            var pSw1 = Voxelizer.PlanFull(house, sw, fdx, pSw.Cells - 1);
            var pSw2 = Voxelizer.PlanFull(house, sw, fdx, pSw1.Cells - 1);
            bool SwOk(Voxelizer.FullDomainPlan p) => p.StepDown == 3 || (p.Blockage <= Envelope.BlockageLimit && p.West >= 5 * H - 1e-3f && p.South >= 5 * H - 1e-3f);
            bool swSteps = pSw1.StepDown == 1 && pSw2.StepDown == 2 && SwOk(pSw1) && SwOk(pSw2);
            // RZ-07: the note never names an internal path, and from a cut-back Full plan it does not point at Full.
            string nC = Envelope.BlockageNote(0.105f), nF = Envelope.BlockageNote(0.05f, fromFull: true);
            bool notes = nC.Contains("8-33 %") && nC.Contains("Domain: Full (Settings, Expert)") && !nC.Contains("fixes/") && !nF.Contains("higher than in the Full") && nF.StartsWith("Wind blockage");
            var fullGrid = Voxelizer.Build(house, 40_000, System.Environment.ProcessorCount, null, mode: DomainMode.Full, wind: steady);
            float fullBlock = Envelope.Blockage(fullGrid, steady) ?? 1f;
            bool full0 = p0.StepDown == 0 && p0.West >= 5 * H && p0.South >= 5 * H && p0.North >= 5 * H && p0.East >= 10 * H + sponge - 1e-3f && p0.Top >= 5 * H && p0.Blockage <= Envelope.BlockageLimit;
            bool steps = p1.StepDown == 1 && MathF.Abs(p1.East - (5 * H + sponge)) < 1e-3f && p1.Note.Contains("10 H to 5 H") &&
                         p2.StepDown == 2 && p2.Blockage <= Envelope.BlockageLimit && p2.North < p1.North &&
                         p3.StepDown == 3 && p3.Note.Contains("Compact") && p3.Blockage > Envelope.BlockageLimit && p3.Dx == fdx;
            bool fullOk = full0 && steps && swSteps && notes && pSw.StepDown == 0 && pSw.Blockage <= Envelope.BlockageLimit && fullGrid.BuildingSize.Z > 0 && fullBlock <= Envelope.BlockageLimit &&
                          Notes.Plain(p1.Note) != Notes.Plain("zzz unmatched");
            Check("6c: Full wind domain (COST 732): margins 5 H / 10 H + sponge / 5 H top for a W wind, blockage <= 3 % (also SW at steps 0-2 and the built grid; notes RZ-07); VRAM step-down 5 H downstream, then side/top at the blockage limit, then Compact at the same Dx",
                fullOk, $"H {H:0.0} m · full W {p0.West:0.#} E {p0.East:0.#} S {p0.South:0.#} N {p0.North:0.#} top {p0.Top:0.#} m, {p0.Cells / 1e6:0.#}M cells, blockage {p0.Blockage * 100:0.0} % · " +
                        $"steps {p1.StepDown}/{p2.StepDown}/{p3.StepDown} (blockage {p2.Blockage * 100:0.0} %, Compact {p3.Blockage * 100:0.0} %) · SW {pSw.Blockage * 100:0.0} %, steps {pSw1.StepDown}/{pSw2.StepDown} at {pSw1.Blockage * 100:0.0}/{pSw2.Blockage * 100:0.0} %, W {pSw2.West:0.#} S {pSw2.South:0.#} m · notes {notes} · built grid {fullGrid.Nx}x{fullGrid.Ny}x{fullGrid.Nz} {fullBlock * 100:0.0} %");
        }

        // Batch R (RW-01 / RW-04 / RW-05 / RW-02): a sealed room whose set supply and extract differ has no pressure anchor, so Voxelizer.BalanceSealedPatches keeps BOTH
        // controlled and scales the extract to the supply total (no tolerance: 0.5 % off is balanced too); with only one side the patches go to 0; each case writes a room note naming the room.
        // A Diffuser in a sealed room brings in no outdoor air. The age solve reads the same effective flows.
        {
            BuildingModel SealedRoom(float supply, float extract, bool window, bool diffuser = false)
            {
                var sm = new BuildingModel { Name = "sealed", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.1f, -0.1f, -0.2f), new Vector3(8.1f, 6.1f, 0f)));
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.1f, -0.1f, 0f), new Vector3(8.1f, 0.1f, 3f)));
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(7.9f, -0.1f, 0f), new Vector3(8.1f, 6.1f, 3f)));
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.1f, 5.9f, 0f), new Vector3(8.1f, 6.1f, 3f)));
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.1f, -0.1f, 0f), new Vector3(0.1f, 6.1f, 3f)));
                sm.Obstacles.Add(SampleBuilding.Box(new Vector3(-0.1f, -0.1f, 3f), new Vector3(8.1f, 6.1f, 3.2f)));
                var rm = new Room { Id = "room", Name = "Bathroom", Level = "L1", ZMin = 0, ZMax = 3 };
                rm.Loops.Add([new Vector2(0.15f, 0.15f), new Vector2(7.85f, 0.15f), new Vector2(7.85f, 5.85f), new Vector2(0.15f, 5.85f)]);
                sm.Rooms.Add(rm);
                sm.Sources.Add(new AirSource { Id = "supply", Name = "Supply", Level = "L1", Kind = SourceKind.Supply, Device = DeviceKind.Patch, Center = new Vector3(2f, 2f, 2.6f), Direction = -Vector3.UnitZ, Width = 0.6f, Height = 0.6f, FlowRate = supply });
                sm.Sources.Add(new AirSource { Id = "return", Name = "Return", Level = "L1", Kind = SourceKind.Return, Device = DeviceKind.Patch, Center = new Vector3(6f, 4f, 2.6f), Direction = Vector3.UnitZ, Width = 0.6f, Height = 0.6f, FlowRate = extract });
                if (diffuser) sm.Sources.Add(new AirSource { Id = "diff", Name = "Diffuser", Level = "L1", Kind = SourceKind.Supply, Device = DeviceKind.Diffuser, Center = new Vector3(4f, 3f, 2.6f), Direction = -Vector3.UnitZ, Width = 0.6f, Height = 0.6f, FlowRate = 0.1f });
                if (window) sm.Openings.Add(new Opening { Id = "w", Name = "w", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(0f, 3f, 1.5f), Normal = -Vector3.UnitX, Width = 1.2f, Height = 1.2f, Depth = 0.2f });
                return sm;
            }
            VoxelGrid Probe(float supply, float extract, bool window, bool diffuser = false, int cells = 150_000) =>
                Voxelizer.Build(SealedRoom(supply, extract, window, diffuser), cells, System.Environment.ProcessorCount, null);
            string NoteOf(VoxelGrid g) => g.Warnings.FirstOrDefault(w => w.Contains("do not balance in this sealed room")) ?? "";
            float MeanSpeed(VoxelGrid g, byte mode) { var v = g.FixedCells.Where(f => f.Mode == mode).ToList(); return v.Count == 0 ? 0f : v.Average(f => f.Velocity.Length()); }

            var unbal = Probe(0.10f, 0.05f, false);
            var bal = Probe(0.10f, 0.10f, false);
            var near = Probe(0.10f, 0.0995f, false);
            var vented = Probe(0.10f, 0.05f, true);
            var onlyS = Probe(0.10f, 0f, false);
            var diff = Probe(0.10f, 0.10f, false, diffuser: true);
            var diffVented = Probe(0.10f, 0.10f, true, diffuser: true);
            int Controlled(VoxelGrid g, byte mode) => g.FixedCells.Count(f => f.Mode == mode && f.Supply >= 0);
            string unbalNote = NoteOf(unbal);
            bool noteOk = unbalNote.StartsWith("Room Bathroom: Supply (360 m³/h) and extract (180 m³/h) do not balance in this sealed room; WindFlow balanced them at 360 m³/h. " +
                "Real rooms need a leakage path or transfer opening — add one for a realistic result.", StringComparison.Ordinal);
            bool ok = unbal.FixedCells.Any(f => f.Mode == 1) && Controlled(unbal, 1) == unbal.FixedCells.Count(f => f.Mode == 1) && Controlled(unbal, 0) == unbal.FixedCells.Count(f => f.Mode == 0) &&
                      MathF.Abs(unbal.PatchFlow(1, 0.05f) - 0.10f) < 1e-6f && MathF.Abs(MeanSpeed(unbal, 1) / MeanSpeed(bal, 1) - 1f) < 0.1f && noteOk &&
                      bal.PatchFlowOverride.Count == 0 && NoteOf(bal) == "" && vented.PatchFlowOverride.Count == 0 && NoteOf(vented) == "" &&
                      MathF.Abs(near.PatchFlow(1, 0.0995f) - 0.10f) < 1e-6f && NoteOf(near).Contains("balanced them at 360 m³/h");
            Check("sealed room, set supply 0.10 and extract 0.05 m3/s (RW-01/04): both patches stay controlled, the extract runs at the supply (0.10, even 0.5 % off is balanced), the note names the room; balanced or vented has no override and no note",
                ok, $"extract override {unbal.PatchFlow(1, 0.05f):0.####} m3/s, return wall speed x{MeanSpeed(unbal, 1) / MeanSpeed(bal, 1):0.00} of the balanced scene · returns controlled {Controlled(unbal, 1)}/{unbal.FixedCells.Count(f => f.Mode == 1)} · note \"{unbalNote}\" · 0.5 % off override {near.PatchFlow(1, 0.0995f):0.####}");
            bool zeroOk = onlyS.FixedCells.Count == 0 && onlyS.PatchFlow(0, 0.10f) == 0f && NoteOf(onlyS).Contains("Supply (360 m³/h) and extract (0 m³/h)") && NoteOf(onlyS).Contains("balanced them at 0 m³/h") &&
                          onlyS.Cell.Count(c => c == VoxelGrid.Fixed) == 0;
            Check("sealed room with supply but no extract (RW-01): both go to 0 (no patch cells left), and the note says so",
                zeroOk, $"patch cells {onlyS.FixedCells.Count} · supply flow {onlyS.PatchFlow(0, 0.10f)} m3/s · note \"{NoteOf(onlyS)}\"");
            bool diffOk = diff.NoFreshAir.Contains(2) && diffVented.NoFreshAir.Count == 0 && diff.Warnings.Any(w => w.StartsWith("Room Bathroom:") && w.Contains("supplies no outdoor air in this sealed room"));
            Check("a Diffuser in a sealed room brings in no outdoor air, in a vented room it does (RW-05)", diffOk,
                $"sealed: no-fresh-air sources [{string.Join(",", diff.NoFreshAir)}] · vented: [{string.Join(",", diffVented.NoFreshAir)}]");

            // RY-06: six 30 m3/h grilles against one 180 m3/h extract differ only by float rounding: still scaled (any s != r), but no note.
            {
                var rm6 = SealedRoom(30f / 3600f, 180f / 3600f, false);
                for (int k = 1; k < 6; k++)
                    rm6.Sources.Add(new AirSource { Id = "s" + k, Name = "Supply " + k, Level = "L1", Kind = SourceKind.Supply, Device = DeviceKind.Patch, Center = new Vector3(1.5f + 1.5f * (k % 3), k < 3 ? 1.5f : 4.5f, 2.6f), Direction = -Vector3.UnitZ, Width = 0.6f, Height = 0.6f, FlowRate = 30f / 3600f });
                rm6.Sources[0].Center = new Vector3(1.5f, 3f, 2.6f);
                var g6 = Voxelizer.Build(rm6, 150_000, System.Environment.ProcessorCount, null);
                float s6 = rm6.Sources.Where(x => x.Kind == SourceKind.Supply).Sum(x => x.FlowRate), r6 = rm6.Sources[1].FlowRate;
                Check("RY-06: 6 x 30 m3/h supply against a 180 m3/h extract in a sealed room writes no balance note (rounding only), and the Return stays controlled",
                    NoteOf(g6) == "" && g6.FixedCells.Count(f => f.Mode == 1 && f.Supply >= 0) == g6.FixedCells.Count(f => f.Mode == 1) && g6.FixedCells.Any(f => f.Mode == 1),
                    $"supply sum {s6:R} vs extract {r6:R} (differ: {s6 != r6}) · note \"{NoteOf(g6)}\"");
            }

            // RW-02: the age solve prescribes the same effective flows. The LBM's patch velocities as the field (the Return's already scaled), no LBM run.
            var sg = Probe(0.10f, 0.05f, false, cells: 60_000);
            int sn = sg.Count;
            var sf = new FlowField { Grid = sg, Ux = new float[sn], Uy = new float[sn], Uz = new float[sn], P = new float[sn] };
            foreach (var fc in sg.FixedCells) { sf.Ux[fc.Index] = fc.Velocity.X; sf.Uy[fc.Index] = fc.Velocity.Y; sf.Uz[fc.Index] = fc.Velocity.Z; }
            var sealedAge = ScalarSolver.Solve(SealedRoom(0.10f, 0.05f, false), sf, new RunOptions(), ageOnly: true).Age;
            int sealMid = sg.Index((int)((4f - sg.Origin.X) / sg.Dx), (int)((3f - sg.Origin.Y) / sg.Dx), (int)((1.5f - sg.Origin.Z) / sg.Dx));   // the room centre
            bool solved = !sg.Warnings.Contains(ScalarSolver.AgeNotConvergedNote) && float.IsFinite(sealedAge[sealMid]) && sealedAge[sealMid] > 0f && sealedAge[sealMid] < ScalarSolver.AgeCapSeconds * 0.999f;
            Check("RW-02: the age solve of the unbalanced sealed room converges (no 'not converged' note) and reads a finite age", solved,
                $"room-centre age {sealedAge[sealMid]:0} s (room volume / supply ~ {8f * 6f * 3f / 0.10f:0} s) · notes: {string.Join(" | ", sg.Warnings.Where(w => w.StartsWith("Age")))}");
        }

        // RW-03: sealing a stale room must not raise a configuration's rank. 2 rooms of 12 x 8 x 6 cells (0.5 m): A is fresh (300 s), B 1000 s with the window open or at the 24 h cap when it is sealed.
        {
            const float dx = 0.5f;
            var g = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero, Cell = new byte[16 * 12 * 10] };
            Room Rm(string id, float x0, float x1) { var r = new Room { Id = id, Name = id, Level = "L1", ZMin = 1, ZMax = 4 }; r.Loops.Add([new Vector2(x0, 1.05f), new Vector2(x1, 1.05f), new Vector2(x1, 4.95f), new Vector2(x0, 4.95f)]); return r; }
            var rooms = new[] { Rm("Kitchen", 1.05f, 3.95f), Rm("Bathroom", 4.05f, 6.95f) };
            float[] AgeWith(float bathroom) { var a = new float[g.Count]; for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++) a[g.Index(x, y, z)] = g.CellCenter(x, y, z).X < 4f ? 300f : bathroom; return a; }
            var freshModel = new BuildingModel { Rooms = [.. rooms] };   // IMP-B: the public rate takes the model (merged room masks)
            float openRate = Analysis.BuildingFreshAch(g, freshModel, AgeWith(1000f), out float openSealed, out var openRooms);
            float shutRate = Analysis.BuildingFreshAch(g, freshModel, AgeWith(ScalarSolver.AgeCapSeconds), out float shutSealed, out var shutRooms);
            var openEntry = new SweepEntry { TotalAch = openRate, SealedVolume = openSealed, SealedRooms = openRooms };
            var shutEntry = new SweepEntry { TotalAch = shutRate, SealedVolume = shutSealed, SealedRooms = shutRooms };
            var sorted = new List<SweepEntry> { shutEntry, openEntry }; sorted.Sort(OpeningSweep.Rank);
            string label = shutEntry.VolumeText;
            Check("RW-03: sealing a stale room raises the building rate but never its rank; the label names the sealed room",
                shutRate > openRate && openSealed == 0f && shutSealed > 0f && ReferenceEquals(sorted[0], openEntry) && OpeningSweep.Rank(openEntry, shutEntry) < 0 &&
                label.Contains("Bathroom") && label.Contains("sealed") && openEntry.VolumeText == "all indoor volume",
                $"window open {openRate:0.0} ACH sealed {openSealed:0.#} m3 · shut {shutRate:0.0} ACH sealed {shutSealed:0.#} m3 · label: {label}");
        }

        // RW-08: the outdoors envelope is per storey. A 2-storey block with a one-storey extension: the air above the extension's roof (at upper-storey height) is outdoors.
        {
            const float dx = 0.5f;
            Room Rm(string id, string level, float x0, float x1, float z0, float z1) { var r = new Room { Id = id, Name = id, Level = level, ZMin = z0, ZMax = z1 }; r.Loops.Add([new Vector2(x0, 1.05f), new Vector2(x1, 1.05f), new Vector2(x1, 4.95f), new Vector2(x0, 4.95f)]); return r; }
            var g = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero, Cell = new byte[16 * 12 * 10] };
            // RX-01: Room.Level holds the storey Id in a mesh model ("L1" = Id) but the storey NAME in a Revit model (Id = the ElementId); both give one envelope.
            float[] EnvAge(bool revitIds)
            {
                var m = new BuildingModel { Name = "envelope" };
                m.Storeys.AddRange([new Storey { Id = revitIds ? "1001" : "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true }, new Storey { Id = revitIds ? "1002" : "L2", Name = "L2", Elevation = 3, Height = 3, Analysed = true }]);
                m.Rooms.AddRange([Rm("Block", "L1", 1.05f, 3.95f, 0, 3), Rm("Extension", "L1", 4.05f, 6.95f, 0, 3), Rm("Upper", "L2", 1.05f, 3.95f, 3, 6)]);
                var ux = new float[g.Count]; Array.Fill(ux, 0.1f);
                return ScalarSolver.Solve(m, new FlowField { Grid = g, Ux = ux, Uy = new float[g.Count], Uz = new float[g.Count], P = new float[g.Count] }, new RunOptions(), ageOnly: true).Age;
            }
            var age = EnvAge(false); var ageByName = EnvAge(true);
            bool sameEnvelope = true; for (int i = 0; i < age.Length; i++) if (!(age[i] == ageByName[i] || (float.IsNaN(age[i]) && float.IsNaN(ageByName[i])))) { sameEnvelope = false; break; }
            int At(float x, float y, float z) => g.Index((int)(x / dx), (int)(y / dx), (int)(z / dx));
            float over = age[At(5.25f, 3.25f, 4.25f)], upper = age[At(2.25f, 3.25f, 4.25f)], ext = age[At(5.25f, 3.25f, 1.25f)];
            Check("RW-08: air above a lower roof next to a taller block is outdoors (no age); the upper storey over its own plan and the extension itself are indoors",
                float.IsNaN(over) && float.IsFinite(upper) && float.IsFinite(ext), $"above the extension {over} (NaN = outdoors) · upper storey {upper:0.0} s · extension {ext:0.0} s");
            Check("RX-01: Room.Level holding the storey name (Revit: Storey.Id is the ElementId) gives the same envelope and age field as Level = Storey.Id", sameEnvelope,
                $"{age.Length} cells compared · above the extension by name {ageByName[At(5.25f, 3.25f, 4.25f)]} · upper storey by name {ageByName[At(2.25f, 3.25f, 4.25f)]:0.0} s");
        }

        // RW-09: a glTF whose nodes reuse one mesh expands to a copy per node (PreTransformVertices); the guard counts the expanded vertices, not the accessor once.
        {
            string guardDir = Path.Combine(Path.GetTempPath(), $"windflow-guard-{System.Environment.ProcessId}"); Directory.CreateDirectory(guardDir);
            try
            {
                string Gltf(int nodes) => "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[" + string.Join(",", Enumerable.Range(0, nodes)) + "]}],\"nodes\":[" +
                    string.Join(",", Enumerable.Repeat("{\"mesh\":0}", nodes)) + "],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                    "\"accessors\":[{\"count\":100000,\"componentType\":5126,\"type\":\"VEC3\"},{\"count\":300000,\"componentType\":5125,\"type\":\"SCALAR\"}]}";
                string bad = Path.Combine(guardDir, "reuse.gltf"), good = Path.Combine(guardDir, "once.gltf");
                File.WriteAllText(bad, Gltf(10_000)); File.WriteAllText(good, Gltf(10));
                string? guardMsg = null; try { WindFlow.App.Import.ImportGuard.Check(bad); } catch (InvalidDataException ex) { guardMsg = ex.Message; }
                bool goodPasses = true; try { WindFlow.App.Import.ImportGuard.Check(good); } catch (InvalidDataException) { goodPasses = false; }
                Check("RW-09: a glTF with 10 000 nodes reusing one 100 000-vertex mesh is refused before Assimp bakes 1e9 vertices; 10 nodes pass",
                    guardMsg != null && guardMsg.Contains("reuse meshes") && goodPasses, $"{new FileInfo(bad).Length / 1024} kB file · refused: {guardMsg ?? "NOT refused"} · 10 nodes pass {goodPasses}");

                // RX-06 / RX-10: the walk starts at scenes[scene].nodes (a root listed 10 000 times is baked 10 000 times), refuses a node that is its own ancestor, and a non-number index is "malformed".
                string Refused(string json, string file)
                {
                    string f = Path.Combine(guardDir, file); File.WriteAllText(f, json);
                    try { WindFlow.App.Import.ImportGuard.Check(f); return "NOT refused"; } catch (InvalidDataException ex) { return ex.Message; }
                }
                const string Head = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,";
                const string Mesh1 = "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],\"accessors\":[{\"count\":100000,\"componentType\":5126,\"type\":\"VEC3\"},{\"count\":300000,\"componentType\":5125,\"type\":\"SCALAR\"}]}";
                string repeated = Refused(Head + "\"scenes\":[{\"nodes\":[" + string.Join(",", Enumerable.Repeat("0", 10_000)) + "]}],\"nodes\":[{\"mesh\":0}]," + Mesh1, "repeated-root.gltf");
                string cycle = Refused(Head + "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0,\"children\":[0]}]," + Mesh1, "cycle.gltf");
                string cycle2 = Refused(Head + "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"children\":[1]},{\"children\":[0]}]," + Mesh1, "cycle2.gltf");
                string nullMesh = Refused(Head + "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":null}]," + Mesh1, "null-mesh.gltf");
                string strPos = Refused(Head + "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":\"0\"}}]}],\"accessors\":[]}", "string-position.gltf");
                string twice = Refused(Head + "\"scenes\":[{\"nodes\":[0,0]}],\"nodes\":[{\"mesh\":0}]," + Mesh1, "twice.gltf");   // 2 x 100 000 vertices: under the cap, passes
                File.WriteAllBytes(Path.Combine(guardDir, "long.bin"), new byte[16]);
                string longBin = Refused("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"long.bin\",\"byteLength\":4}]}", "long-bin.gltf");
                string shortBin = Refused("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"long.bin\",\"byteLength\":32}]}", "short-bin.gltf");
                Check("RX-06 / RX-10: a scene listing one node 10 000 times and a node that is its own ancestor (self, two-node loop) are refused; a null or string index is 'malformed', not a .NET message; a node listed twice passes; an external .bin longer than byteLength passes, a shorter one is refused",
                    repeated.Contains("reuse meshes") && cycle.Contains("loops back") && cycle2.Contains("loops back") && nullMesh.Contains("malformed") && strPos.Contains("malformed") && twice == "NOT refused" && longBin == "NOT refused" && shortBin.Contains("declares 32 bytes"),
                    $"repeated: {repeated} | cycle: {cycle} | two-node loop: {cycle2} | null mesh: {nullMesh} | string POSITION: {strPos} | listed twice: {twice} | long bin: {longBin} | short bin: {shortBin}");
            }
            finally { try { Directory.Delete(guardDir, true); } catch (IOException) { } }
        }

        // Batch 2 (D1-D4): ACH redefined as fresh-air ACH from the age of air. A synthetic chain outdoor -> room A -> door -> room B in
        // a uniform 0.1 m/s stream (16 x 12 x 10 cells of 0.5 m, no walls; the rooms float clear of the domain edge, which counts as a connection), solved by nothing: the fields are written, Analysis reads them.
        {
            const float dx = 0.5f, tau = 600f;
            var g = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero };
            int n = g.Count;
            g.Cell = new byte[n];
            Room MakeRoom(string id, float x0, float x1)
            {
                var rm = new Room { Id = id, Name = id, Level = "L1", ZMin = 1, ZMax = 4 };
                rm.Loops.Add([new Vector2(x0, 1.05f), new Vector2(x1, 1.05f), new Vector2(x1, 4.95f), new Vector2(x0, 4.95f)]);
                return rm;
            }
            Opening MakeOpening(string id, float x, float normalX, OpeningKind kind) => new()
            {
                Id = id, Name = id, Kind = kind, IsOpen = true, Center = new Vector3(x, 3f, 2.2f), Normal = new Vector3(normalX, 0, 0),
                Width = 1.0f, Height = 1.2f, Depth = 0.2f,
            };
            var chain = new BuildingModel { Name = "chain" };
            chain.Rooms.AddRange([MakeRoom("A", 1.05f, 3.95f), MakeRoom("B", 4.05f, 6.95f)]);
            chain.Openings.AddRange([MakeOpening("w", 1.0f, -1f, OpeningKind.Window), MakeOpening("d", 4.0f, 1f, OpeningKind.Door),
                                     MakeOpening("e", 7.0f, 1f, OpeningKind.Window)]);
            SimulationResult Solved(float? age)
            {
                var ux = new float[n]; Array.Fill(ux, 0.1f);
                var f = new FlowField { Grid = g, Ux = ux, Uy = new float[n], Uz = new float[n], P = new float[n] };
                var res = Analysis.Analyze(chain, f, Vector2.Zero, new RunOptions());
                if (age is float a) { var ag = new float[n]; Array.Fill(ag, a); res.Scalars = new ScalarFields { Age = ag }; }
                Analysis.Finish(res);
                return res;
            }
            var withAge = Solved(tau);
            var noAge = Solved(null);
            var ra = withAge.Rooms.First(r => r.Room.Id == "A"); var rb = withAge.Rooms.First(r => r.Room.Id == "B");

            // (a) a uniform age field gives FreshAch = 3600 / age in every room
            Check("fresh-air ACH: a uniform age of 600 s gives 3600 / 600 = 6.0 ACH in every room",
                MathF.Abs(ra.FreshAch - 6f) < 1e-3f && MathF.Abs(rb.FreshAch - 6f) < 1e-3f, $"A {ra.FreshAch:0.000}, B {rb.FreshAch:0.000}");

            // (b) P15-07: the building rate is 3600 / the volume-weighted mean age over all room cells (synthetic age 300 s in A, 1500 s in B),
            // not the sum of the rooms' inflows and not the mean of the rooms' rates; a room at the 24 h cap (no air path) is left out of the mean
            SimulationResult SolvedAges(float ageA, float ageB)
            {
                var ux = new float[n]; Array.Fill(ux, 0.1f);
                var f = new FlowField { Grid = g, Ux = ux, Uy = new float[n], Uz = new float[n], P = new float[n] };
                var res = Analysis.Analyze(chain, f, Vector2.Zero, new RunOptions());
                var ag = new float[n]; Array.Fill(ag, float.NaN);
                for (int i = 0; i < n; i++) ag[i] = g.CellCenter(i % g.Nx, i / g.Nx % g.Ny, i / (g.Nx * g.Ny)).X < 4f ? ageA : ageB;
                res.Scalars = new ScalarFields { Age = ag };
                Analysis.Finish(res);
                return res;
            }
            var two = SolvedAges(300f, 1500f);
            float expectMean = (300f * two.Rooms[0].VoxelVolume + 1500f * two.Rooms[1].VoxelVolume) / (two.Rooms[0].VoxelVolume + two.Rooms[1].VoxelVolume);
            float meanOfRates = (two.Rooms[0].FreshAch + two.Rooms[1].FreshAch) / 2f;
            var capped = SolvedAges(300f, ScalarSolver.AgeCapSeconds);
            Check("building fresh-air rate (sweep, annual) = 3600 / volume-weighted mean age: 4.0 ACH for 300 s and 1500 s rooms of equal volume; a capped room is left out",
                MathF.Abs(two.Ledger.FreshAch - 3600f / expectMean) < 1e-3f && MathF.Abs(two.Ledger.FreshAch - 4f) < 0.01f && meanOfRates > 1.5f * two.Ledger.FreshAch &&
                MathF.Abs(capped.Ledger.FreshAch - 12f) < 1e-3f && float.IsNaN(noAge.Ledger.FreshAch),
                $"mean age {expectMean:0} s -> {two.Ledger.FreshAch:0.000} ACH (mean of the rooms' rates would be {meanOfRates:0.00}); capped room left out -> {capped.Ledger.FreshAch:0.000}; no age -> {noAge.Ledger.FreshAch}");

            // (c) air-change efficiency needs a room fed >= 90 % of ALL its incoming air straight from outside: B is fed through the door, and in this field
            // (uniform stream, no walls) A takes most of its air across plain-air faces (RV-08), so neither has one. The positive case is under RV-02/03 below.
            Check("air-change efficiency: \"—\" (NaN) on a room fed through a door and on one whose air mostly crosses plain-air faces (RV-08)",
                float.IsNaN(rb.AirChangeEffectiveness) && float.IsNaN(ra.AirChangeEffectiveness) && rb.OutdoorInflow < 0.1f * rb.Inflow &&
                ra.OutdoorInflow < 0.9f * (ra.Inflow + ra.InternalExchange),
                $"A {ra.AirChangeEffectiveness:0.000} (outdoor {ra.OutdoorInflow:0.000} of {ra.Inflow:0.000} + plain-air {ra.InternalExchange:0.000} m3/s), B {rb.AirChangeEffectiveness:0.000} (outdoor {rb.OutdoorInflow:0.000} of {rb.Inflow:0.000})");

            // (d) no age: "—", a neutral (pending) dot, no comfort word, never the total exchange standing in; the age cap reads "sealed"
            var pendingRoom = noAge.Rooms.First(r => r.Room.Id == "A");
            var sealedRoom = new RoomResult { MeanAge = ScalarSolver.AgeCapSeconds, FreshAch = 3600f / ScalarSolver.AgeCapSeconds };
            Check("missing air age: \"—\", pending dot (no verdict), no \"stuffy\", total exchange still shown; the 24 h cap reads \"sealed\"",
                float.IsNaN(pendingRoom.FreshAch) && pendingRoom.Ach > 0f && Standards.FreshText(pendingRoom) == "—" &&
                Standards.Verdict(pendingRoom, AchIntent.AirQuality) == null && Standards.ComfortFlag(pendingRoom, 0f, AchIntent.AirQuality).Flag == "" &&
                Standards.Verdict(ra, AchIntent.AirQuality) == AchBand.Good && sealedRoom.AgeSealed && Standards.FreshText(sealedRoom) == "sealed",
                $"fresh {pendingRoom.FreshAch}, total {pendingRoom.Ach:0.0} ACH, text \"{Standards.FreshText(pendingRoom)}\", verdict {Standards.Verdict(pendingRoom, AchIntent.AirQuality)?.ToString() ?? "pending"}, sealed \"{Standards.FreshText(sealedRoom)}\"");

            // D2: the total exchange counts plain-air faces too, so it is at least the inflow through openings (here the walls are missing)
            Check("total exchange includes plain-air faces: Ach x volume / 3600 = Inflow + InternalExchange",
                ra.InternalExchange > 0f && MathF.Abs(ra.Ach * ra.Volume / 3600f - (ra.Inflow + ra.InternalExchange)) < 1e-3f,
                $"A: inflow {ra.Inflow:0.000} + plain-air {ra.InternalExchange:0.000} m3/s over {ra.Volume:0.0} m3 = {ra.Ach:0.0} ACH");
        }

        // Batch 2c (RV-02, RV-03, RV-04, RV-05). A synthetic chain outdoor - A - door - B (16 x 12 x 10 cells of 0.5 m, no walls) with an age field
        // that depends on x and height and a flow that moves only through the openings' upper layer, so the weights and the means are not uniform.
        {
            const float dx = 0.5f;
            var g = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero };
            int n = g.Count;
            g.Cell = new byte[n];
            Room MakeRoom(string id, float x0, float x1)
            {
                var rm = new Room { Id = id, Name = id, Level = "L1", ZMin = 1, ZMax = 4 };
                rm.Loops.Add([new Vector2(x0, 1.05f), new Vector2(x1, 1.05f), new Vector2(x1, 4.95f), new Vector2(x0, 4.95f)]);
                return rm;
            }
            Opening MakeOpening(string id, float x, float normalX, OpeningKind kind) => new()
            {
                Id = id, Name = id, Kind = kind, IsOpen = true, Center = new Vector3(x, 3f, 2.2f), Normal = new Vector3(normalX, 0, 0), Width = 1.0f, Height = 1.2f, Depth = 0.2f,
            };
            var chain = new BuildingModel { Name = "chain2c" };
            chain.Rooms.AddRange([MakeRoom("A", 1.05f, 3.95f), MakeRoom("B", 4.05f, 6.95f)]);
            chain.Openings.AddRange([MakeOpening("w", 1.0f, -1f, OpeningKind.Window), MakeOpening("d", 4.0f, 1f, OpeningKind.Door), MakeOpening("e", 7.0f, 1f, OpeningKind.Window)]);
            // a return grille in A (one Fixed cell, 0.1 m3/s out) at the cell centred (2.25, 4.25, 3.25)
            int grille = g.Index(4, 8, 6);
            g.Cell[grille] = VoxelGrid.Fixed;
            chain.Sources.Add(new AirSource { Id = "ret", Name = "ret", Kind = SourceKind.Return, Device = DeviceKind.Patch, Enabled = true, Center = g.CellCenter(4, 8, 6),
                Direction = Vector3.UnitY, Width = 0.5f, Height = 0.5f, FlowRate = 0.1f });
            float AgeAt(float x, float zc) => 10f * x + 100f * (zc - 1f);
            var ux = new float[n]; var uy = new float[n]; var age = new float[n];
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, z); var c = g.CellCenter(x, y, z);
                age[i] = AgeAt(c.X, c.Z);
                if (MathF.Abs(c.Z - 3.25f) < 1e-3f && c.Y > 2f && c.Y < 4f) ux[i] = 0.2f;     // the upper layer of the openings' boxes only
            }
            uy[grille] = 0.4f; age[grille] = 40f;                                          // 0.4 m/s x 0.25 m2 = 0.1 m3/s at age 40 s
            var field = new FlowField { Grid = g, Ux = ux, Uy = uy, Uz = new float[n], P = new float[n] };
            var res = Analysis.Analyze(chain, field, Vector2.Zero, new RunOptions());
            res.Scalars = new ScalarFields { Age = age };
            Analysis.Finish(res);
            var ra = res.Rooms.First(r => r.Room.Id == "A");
            // expected, worked out here from the definitions: room-volume mean over A's air cells; exit age weighted by m3/s over the door's upper layer and the grille
            double vs = 0; int vn = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z); int i = g.Index(x, y, z);
                if (c.X > 1.05f && c.X < 3.95f && c.Y > 1.05f && c.Y < 4.95f && c.Z >= 1f && c.Z < 4f && g.Cell[i] == VoxelGrid.Air) { vs += age[i]; vn++; }
            }
            double meanVol = vs / vn;
            double doorQ = 4 * dx * dx * 0.2, gAge = AgeAt(3.75f, 3.25f);
            double tauE = (doorQ * gAge + 0.1 * 40.0) / (doorQ + 0.1);
            double eps = tauE / (2 * meanVol);
            double occ = 0; int on = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z); int i = g.Index(x, y, z);
                if (c.X > 1.05f && c.X < 3.95f && c.Y > 1.05f && c.Y < 4.95f && c.Z >= 1.1f && c.Z <= 2.8f && g.Cell[i] == VoxelGrid.Air) { occ += age[i]; on++; }
            }
            Check("RV-03: fresh-air ACH and eps_a use the room-VOLUME mean age; the occupied-zone age stays its own number",
                MathF.Abs(ra.VolumeMeanAge - (float)meanVol) < 0.5f && MathF.Abs(ra.FreshAch - (float)(3600 / meanVol)) < 0.01f &&
                MathF.Abs(ra.MeanAge - (float)(occ / on)) < 5f && MathF.Abs(ra.MeanAge - ra.VolumeMeanAge) > 5f,
                $"volume mean {ra.VolumeMeanAge:0.0} s (expected {meanVol:0.0}) -> {ra.FreshAch:0.00} ACH; occupied zone {ra.MeanAge:0.0} s (expected about {occ / on:0.0})");
            var uniformAge = new float[n]; Array.Fill(uniformAge, 600f);
            var resU = Analysis.Analyze(chain, field, Vector2.Zero, new RunOptions());
            resU.Scalars = new ScalarFields { Age = uniformAge };
            Analysis.Finish(resU);
            var rU = resU.Rooms.First(r => r.Room.Id == "A");
            Check("air-change efficiency: 0.5 on a uniform-age room fed from outside (door faces and a grille as exits)",
                MathF.Abs(rU.AirChangeEffectiveness - 0.5f) < 1e-3f && rU.OutdoorInflow >= 0.9f * (rU.Inflow + rU.InternalExchange),
                $"A {rU.AirChangeEffectiveness:0.000} (outdoor {rU.OutdoorInflow:0.000} of {rU.Inflow:0.000} + plain-air {rU.InternalExchange:0.000} m3/s)");
            // RA-05: one capped cell in room A (1 of ~288 = 0.35 %) with 600 s elsewhere: the capped cell is left out of the mean (6.0 ACH, not 4.0) and the share is disclosed
            var cappedAge = new float[n]; Array.Fill(cappedAge, 600f);
            int capCell = -1, roomCells = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z); int i = g.Index(x, y, z);
                if (c.X > 1.05f && c.X < 3.95f && c.Y > 1.05f && c.Y < 4.95f && c.Z >= 1f && c.Z < 4f && g.Cell[i] == VoxelGrid.Air) { roomCells++; if (capCell < 0) capCell = i; }
            }
            cappedAge[capCell] = ScalarSolver.AgeCapSeconds;
            var resC = Analysis.Analyze(chain, field, Vector2.Zero, new RunOptions());
            resC.Scalars = new ScalarFields { Age = cappedAge };
            Analysis.Finish(resC);
            var rC = resC.Rooms.First(r => r.Room.Id == "A");
            string? capNote = g.Warnings.FirstOrDefault(w => w.StartsWith("A: ", StringComparison.Ordinal) && w.EndsWith("is left out of the mean", StringComparison.Ordinal));
            Check("RA-05: cells at the 24 h age cap are left out of the room-volume mean (fresh-air ACH from the rest), and a capped share over 0.1 % of the room is disclosed with its percentage",
                MathF.Abs(rC.FreshAch - 6f) < 0.01f && MathF.Abs(rC.VolumeMeanAge - 600f) < 0.5f && capNote != null && capNote.Contains($"{100.0 / roomCells:0.#} %"),
                $"1 of {roomCells} cells capped: {rC.FreshAch:0.00} ACH (expected 6.00; with the cell in the mean {3600.0 / ((600.0 * (roomCells - 1) + ScalarSolver.AgeCapSeconds) / roomCells):0.00}) · note: {capNote ?? "none"}");
            // RA-05 (M2): the same capped cell on a one-room model: the room and the building fresh-air rate agree (both leave it out)
            var roomOnly = new BuildingModel { Name = "ra05-one-room" };
            roomOnly.Storeys.AddRange(chain.Storeys); roomOnly.Rooms.Add(chain.Rooms.First(r => r.Id == "A"));
            float bldC = Analysis.BuildingFreshAch(g, roomOnly, cappedAge);
            // RA-05 (M1): half of the room capped is a real stagnant zone: it stays in the mean (low ACH) and the note does not say "left out"
            var halfAge = new float[n]; Array.Fill(halfAge, 600f);
            int halfK = 0;
            for (int z = 0; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                var c = g.CellCenter(x, y, z); int i = g.Index(x, y, z);
                if (c.X > 1.05f && c.X < 3.95f && c.Y > 1.05f && c.Y < 4.95f && c.Z >= 1f && c.Z < 4f && g.Cell[i] == VoxelGrid.Air && (halfK++ & 1) == 0) halfAge[i] = ScalarSolver.AgeCapSeconds;
            }
            var resH = Analysis.Analyze(chain, field, Vector2.Zero, new RunOptions());
            resH.Scalars = new ScalarFields { Age = halfAge };
            Analysis.Finish(resH);
            var rH = resH.Rooms.First(r => r.Room.Id == "A");
            string? halfNote = g.Warnings.FirstOrDefault(w => w.StartsWith("A: ", StringComparison.Ordinal) && w.Contains("is stagnant (age at the 24 h cap)", StringComparison.Ordinal));
            Check("RA-05: a capped share over 1 % of the room stays in the mean (low fresh-air ACH, noted without 'left out'); on a one-room model the building rate agrees with the room's",
                rH.FreshAch < 0.2f && rH.VolumeMeanAge > 30000f && halfNote != null && !halfNote.Contains("left out", StringComparison.Ordinal) && MathF.Abs(bldC - rC.FreshAch) < 0.01f,
                $"half capped: {rH.FreshAch:0.000} ACH, mean age {rH.VolumeMeanAge:0} s · note: {halfNote ?? "none"} · one-room 0.35 %: building {bldC:0.00} vs room {rC.FreshAch:0.00} ACH");
            Check("RV-02: the exit age weights every exit by m3/s: door faces (m/s x Dx^2) and a return grille (m3/s) together; eps_a <= 1",
                !float.IsNaN(ra.AirChangeEffectiveness) && MathF.Abs(ra.AirChangeEffectiveness - (float)eps) < 0.01f && ra.AirChangeEffectiveness <= 1f,
                $"eps_a {ra.AirChangeEffectiveness:0.000} (expected {eps:0.000}: door {doorQ:0.00} m3/s at {gAge:0.0} s + grille 0.10 m3/s at 40 s -> exit age {tauE:0.0} s over 2 x {meanVol:0.0}); with the old m/s weights it would read {(0.8 * gAge + 0.1 * 40) / 0.9 / (2 * meanVol):0.000}");

            // RV-04 / RV-05: the age SOLVER on a uniform 0.1 m/s stream west to east through the same chain.
            var model2 = new BuildingModel { Name = "chain2c-solve" };
            model2.Storeys.AddRange([new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true }, new Storey { Id = "L2", Name = "L2", Elevation = 3, Height = 3, Analysed = false }]);
            model2.Rooms.AddRange(chain.Rooms); model2.Openings.AddRange(chain.Openings);
            model2.Obstacles.Add(new WindFlow.Core.Mesh { Id = "context", Vertices = [new Vector3(14, 0, 0), new Vector3(15, 0, 0), new Vector3(14, 1, 3)], Indices = [0, 1, 2] });   // a neighbouring building far east
            var stream = new float[n]; Array.Fill(stream, 0.1f);
            ScalarFields SolveAge(BuildingModel m, VoxelGrid grid)
            {
                var f = new FlowField { Grid = grid, Ux = stream, Uy = new float[n], Uz = new float[n], P = new float[n] };
                return ScalarSolver.Solve(m, f, new RunOptions(), ageOnly: true);
            }
            int At(float x, float y, float z) => g.Index((int)(x / dx), (int)(y / dx), (int)(z / dx));
            var g2 = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero, Cell = new byte[n] };
            var baseAge = SolveAge(model2, g2).Age;
            Check("RV-05: age 0 applies outside the analysed building's envelope: a context building (and everything east of the rooms' wall) is outdoors (no age), the unanalysed storey above is indoors",
                float.IsNaN(baseAge[At(7.75f, 3.25f, 2.25f)]) && float.IsFinite(baseAge[At(2.25f, 3.25f, 4.75f)]) && float.IsFinite(baseAge[At(5.25f, 3.25f, 2.25f)]) && baseAge[At(5.25f, 3.25f, 2.25f)] > 20f,
                $"age east of the rooms {baseAge[At(7.75f, 3.25f, 2.25f)]} (NaN = outdoors) · upper storey {baseAge[At(2.25f, 3.25f, 4.75f)]:0.0} s · room B {baseAge[At(5.25f, 3.25f, 2.25f)]:0.0} s");

            AirSource Dev(DeviceKind d, SourceKind k) => new() { Id = "dev", Name = "dev", Kind = k, Device = d, Enabled = true, FlowRate = 0.1f, Center = g.CellCenter(10, 6, 4) };
            float DownstreamAge(DeviceKind d, SourceKind k, out float building)
            {
                var m = new BuildingModel { Name = model2.Name, Storeys = model2.Storeys, Rooms = model2.Rooms, Openings = model2.Openings, Obstacles = model2.Obstacles };
                m.Sources.Add(Dev(d, k));
                var gg = new VoxelGrid { Nx = 16, Ny = 12, Nz = 10, Dx = dx, Origin = Vector3.Zero, Cell = new byte[n] };
                var outlet = new List<int>();                                         // the whole cross-section of room B at x = 5.25
                for (int zz = 2; zz <= 7; zz++) for (int yy = 2; yy <= 9; yy++) outlet.Add(gg.Index(10, yy, zz));
                gg.Devices.Add(new DeviceRegion { SourceIndex = 0, SourceId = "dev", Kind = d, Cells = [.. outlet], OutletCount = outlet.Count, FlowRate = 0.1f });
                var ag = SolveAge(m, gg).Age;
                building = Analysis.BuildingFreshAch(gg, m, ag);
                return ag[gg.Index(11, 6, 4)];                                    // the cell right downstream of the device outlet
            }
            float dAge = DownstreamAge(DeviceKind.Diffuser, SourceKind.Supply, out float dRate);
            float fAge = DownstreamAge(DeviceKind.CeilingFan, SourceKind.Fan, out float fRate);
            float eAge = DownstreamAge(DeviceKind.ExhaustFan, SourceKind.Return, out float eRate);
            float baseRate = Analysis.BuildingFreshAch(g2, model2, baseAge);
            Check("RV-04: a Diffuser is outdoor air (its outlet cell is age 0 and the building rate counts it); a fan and an exhaust are not",
                dAge < 25f && fAge > 40f && eAge > 40f && dRate > baseRate * 1.05f && MathF.Abs(fRate - baseRate) < 0.01f * baseRate && MathF.Abs(eRate - baseRate) < 0.01f * baseRate,
                $"age one cell downstream: diffuser {dAge:0.0} s, fan {fAge:0.0} s, exhaust {eAge:0.0} s · building rate: none {baseRate:0.00}, diffuser {dRate:0.00}, fan {fRate:0.00}, exhaust {eRate:0.00} ACH");
        }

        // Batch 3a S4-02: save -> open round trip of an edited model through the file the app writes (devices, openings, Cd overrides,
        // occupants, names, environment), and the dirty test the unsaved-changes prompt uses.
        {
            var edited = SampleBuilding.Build();
            string cleanPrint = UserFiles.Fingerprint(edited);
            edited.Sources.Add(new AirSource { Id = "dev-x", Name = "My fan", Kind = SourceKind.Fan, Device = DeviceKind.CeilingFan, Center = new Vector3(4, 4, 2.5f),
                Direction = -Vector3.UnitZ, Width = 1f, Height = 1f, Radius = 0.6f });
            edited.Openings[0].IsOpen = !edited.Openings[0].IsOpen;
            OpeningCd.Apply(edited.Openings[1], 0.4f);
            edited.Rooms[0].Occupants = 7;
            edited.Rooms[1].Name = "Study";
            edited.Env.OutdoorTemperature = 12.5f;
            bool dirty = UserFiles.Fingerprint(edited) != cleanPrint;
            string file = Path.Combine(Path.GetTempPath(), $"windflow-selfcheck-{System.Environment.ProcessId}.windflow.json");
            try
            {
                UserFiles.SaveModel(edited, file);
                var back = UserFiles.OpenModel(file);
                bool same = UserFiles.Fingerprint(back) == UserFiles.Fingerprint(edited);
                bool fields = back.Sources.Any(x => x.Id == "dev-x" && x.Device == DeviceKind.CeilingFan && x.Radius == 0.6f) &&
                              back.Openings[0].IsOpen == edited.Openings[0].IsOpen && back.Openings[1].DischargeCoefficient == 0.4f &&
                              back.Rooms[0].Occupants == 7 && back.Rooms[1].Name == "Study" && back.Env.OutdoorTemperature == 12.5f &&
                              back.Obstacles.Count == edited.Obstacles.Count;
                Check("save model: a file written by Save model... reads back equal (devices, openings, Cd override, occupants, names, environment); an edit reads as unsaved",
                    dirty && same && fields && UserFiles.Fingerprint(back) != cleanPrint,
                    $"{new FileInfo(file).Length / 1024} kB · edited vs clean differ {dirty} · round trip equal {same} · fields {fields}");
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
        }

        // RA-11: a model opened from a file is validated like the live link's (the auditor's two cases, plus the sample still loads)
        {
            string ra11Dir = Path.Combine(Path.GetTempPath(), $"windflow-ra11-{System.Environment.ProcessId}");
            Directory.CreateDirectory(ra11Dir);
            try
            {
                var good = SampleBuilding.Build();
                string goodFile = Path.Combine(ra11Dir, "good.windflow.json"); UserFiles.SaveModel(good, goodFile);
                var badIdx = SampleBuilding.Build(); badIdx.Obstacles[0].Indices[0] = 99999;
                string idxFile = Path.Combine(ra11Dir, "bad-index.windflow.json"); UserFiles.SaveModel(badIdx, idxFile);
                string nanFile = Path.Combine(ra11Dir, "nan-width.windflow.json");
                File.WriteAllText(nanFile, System.Text.RegularExpressions.Regex.Replace(good.ToJson(), "(\"Id\":\"w-liv-s\".*?\"Width\":)[^,]+", "$1\"NaN\"", System.Text.RegularExpressions.RegexOptions.Singleline));
                string? Refuse(string f) { try { UserFiles.OpenModel(f); return null; } catch (InvalidDataException e) { return e.Message; } }
                string? mIdx = Refuse(idxFile), mNan = Refuse(nanFile);
                bool tooBig = false; try { ModelValidation.Load(goodFile, 1024); } catch (InvalidDataException) { tooBig = true; }
                bool loads = UserFiles.OpenModel(goodFile).Obstacles.Count == good.Obstacles.Count;
                Check("RA-11: a model file with an out-of-range mesh index, or a NaN opening width, or over the size limit is refused with a reason before use; the sample model still loads",
                    mIdx != null && mIdx.Contains("index") && mNan != null && mNan.Contains("w-liv-s") && tooBig && loads,
                    $"index case: {mIdx ?? "NOT refused"} · NaN width: {mNan ?? "NOT refused"} · over limit refused {tooBig} · sample loads {loads}");
            }
            finally { try { Directory.Delete(ra11Dir, true); } catch (IOException) { } }
        }

        // RC-04: plausibility bounds: the recheck's crafted files (huge or null fields that passed the RA-11 finite test and crashed or hung --solve) are each refused with a reason; the sample still validates
        {
            var crafted = new (string Name, Action<BuildingModel> Craft)[]
            {
                ("vertex z 3e38", m => m.Obstacles[0].Vertices[0] = m.Obstacles[0].Vertices[0] with { Z = 3e38f }),
                ("vertex x/y 3e38", m => m.Obstacles[0].Vertices[0] = m.Obstacles[0].Vertices[0] with { X = 3e38f, Y = 3e38f }),
                ("vertex z 3e9", m => m.Obstacles[0].Vertices[0] = m.Obstacles[0].Vertices[0] with { Z = 3e9f }),
                ("room Id null", m => m.Rooms[0].Id = null!),
                ("room Name null", m => m.Rooms[0].Name = null!),
                ("GroundZ -3e38", m => m.GroundZ = -3e38f),
                ("window 1e9 m wide", m => m.Openings[0].Width = 1e9f),
                ("opening Kind 99", m => m.Openings[0].Kind = (OpeningKind)99),
                ("device ReturnCenter +Infinity", m => { if (m.Sources.Count > 0) m.Sources[0].ReturnCenter = new Vector3(float.PositiveInfinity, float.NaN, float.NaN); else m.GroundZ = float.PositiveInfinity; }),
            };
            var reasons = crafted.Select(c => { var m = SampleBuilding.Build(); c.Craft(m); return (c.Name, Why: ModelValidation.Validate(m)); }).ToList();
            bool allRefused = reasons.All(r => !string.IsNullOrEmpty(r.Why));
            bool sampleOk = ModelValidation.Validate(SampleBuilding.Build()) == null;
            var unsetOk = SampleBuilding.Build(); unsetOk.Heat.Add(new HeatSource { Id = "h", Name = "h", Kind = HeatKind.Surface });   // SurfaceTemperature NaN = not set
            Check("RC-04: a model with a vertex z of 3e38 or 3e9, an x/y extent of 3e38, a null room Id or Name, GroundZ -3e38, a 1e9 m window, an undefined enum or an infinite unset-field is refused with a reason; the sample and a NaN \"unset\" field still validate",
                allRefused && sampleOk && ModelValidation.Validate(unsetOk) == null,
                string.Join(" · ", reasons.Select(r => $"{r.Name}: {r.Why ?? "NOT refused"}")) + $" · sample valid {sampleOk}");
        }

        // Batch 3a S4-04: the folder the PDF / video / model dialogs start in is Documents\WindFlow, created on demand, never the working or install folder
        {
            string docs = Path.Combine(Path.GetTempPath(), $"windflow-selfcheck-docs-{System.Environment.ProcessId}");
            string blocker = Path.Combine(Path.GetTempPath(), $"windflow-selfcheck-blocker-{System.Environment.ProcessId}");
            try
            {
                string made = UserFiles.DefaultFolder(docs);
                File.WriteAllText(blocker, "x");            // a FILE where the Documents folder should be: creating WindFlow under it fails
                string fallback = UserFiles.DefaultFolder(blocker);
                string real = UserFiles.DefaultFolder();
                string myDocs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
                Check("default save folder: Documents/WindFlow, created; an unwritable Documents falls back instead of throwing",
                    made == Path.Combine(docs, "WindFlow") && Directory.Exists(made) && Directory.Exists(fallback) &&
                    (myDocs.Length == 0 || real == Path.Combine(myDocs, "WindFlow")),
                    $"{made} · fallback {fallback} · real {real}");
            }
            finally
            {
                try { Directory.Delete(docs, true); File.Delete(blocker); } catch (IOException) { }
            }
        }

        // Batch 3a S4-03: a 1080p display at 150 % has a 1280 DIP work area. The window's minimum (read from MainWindow.axaml) must fit it, and a saved
        // 2560 DIP window is pulled inside it, so the 328 DIP results column is never off screen.
        {
            string axaml = File.ReadAllText(Path.Combine(Options.RepoRoot(), "src", "WindFlow.App", "MainWindow.axaml"));
            var m = System.Text.RegularExpressions.Regex.Match(axaml, @"MinWidth=""(\d+)""");
            double xamlMin = m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
            var big = WindowFit.Fit(2560, 1400, 3000, 900, 0, 0, 1264, 680);              // 1280 x 720 DIP work area less frame and title bar
            var small = WindowFit.Fit(1680, 940, 0, 0, 0, 0, 1000, 700);                   // a 1000 DIP screen: the minimum shrinks below the default
            Check("window fit: MinWidth in the XAML <= 1280 DIP; a saved 2560 x 1400 window at x = 3000 lands inside a 1264 x 680 work area; results column fits",
                xamlMin <= 1280 && xamlMin == WindowFit.MinWidth && big.W == 1264 && big.H == 680 && big.X == 0 && big.Y == 0 && big.W - 328 > 800 &&
                small.W == 1000 && small.MinW == 1000,
                $"XAML MinWidth {xamlMin} · big -> {big.W}x{big.H} at ({big.X}, {big.Y}), min {big.MinW} · small -> {small.W} wide, min {small.MinW}");
        }

        // Batch 3a S4-05: the LiveLink server pipe is for this user on this machine. A client opening it through the SMB path (127.0.0.1, which a remote
        // machine uses) is refused while the same-user local client still connects. A pipe with the old default security is the control: if the SMB path
        // does not even reach THAT one here (SMB server off), the refusal proves nothing and the case says so.
        {
            string pn = "WindFlow.SelfCheck." + Guid.NewGuid().ToString("N")[..8];
            bool Reaches(string host, string pipeName)
            {
                try { using var c = new System.IO.Pipes.NamedPipeClientStream(host, pipeName, System.IO.Pipes.PipeDirection.InOut); c.Connect(2500); return c.IsConnected; }
                catch (Exception) { return false; }
            }
            bool controlSmb;
            using (var ctl = new System.IO.Pipes.NamedPipeServerStream(pn + ".ctl", System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                _ = ctl.WaitForConnectionAsync();
                controlSmb = Reaches("127.0.0.1", pn + ".ctl");
            }
            bool remote, local;
            using (var srv = LiveLinkServer.CreateServer(pn))
            {
                _ = srv.WaitForConnectionAsync();
                remote = Reaches("127.0.0.1", pn);                      // the SMB path: must be refused
                local = false;
                try
                {
                    using var c = new System.IO.Pipes.NamedPipeClientStream(".", pn, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    c.Connect(2500); local = c.IsConnected;
                }
                catch (Exception) { }
            }
            Check("LiveLink pipe: this user on this machine only: the SMB path is refused, the local same-user client (CurrentUserOnly) connects",
                !remote && local, $"SMB path (127.0.0.1) to the old default-security pipe {(controlSmb ? "connects" : "does not connect here, so the refusal below proves nothing")} · to the new one {(remote ? "CONNECTS" : "refused")} · local client {(local ? "connected" : "FAILED")}");
        }

        // Batch 3a S4-06: what the Revit exporter drops or guesses becomes a warning in the model (the LiveLink payload is the model JSON), shown with
        // the app's notes. The add-in cannot run here: the wording (ExtractionNotes, used by RevitExtractor), the JSON carry and the plain-language
        // mapping are what is tested.
        {
            var m = SampleBuilding.Build();
            m.Warnings.Add(ExtractionNotes.DroppedGeometry(["Basic Wall (id 3141)", "Floor (id 2718)", "Roof (id 1618)", "Wall (id 1", "Wall (id 2", "Wall (id 3", "Wall (id 4)"]));
            m.Warnings.Add(ExtractionNotes.DefaultedTerminals(["Return Grille: 600x600"]));
            var back = BuildingModel.FromJson(m.ToJson());
            string plain1 = Notes.Plain(back.Warnings[0]), plain2 = Notes.Plain(back.Warnings[1]);
            Check("Revit warnings: dropped elements and defaulted terminals are in the model JSON and read as plain notes",
                back.Warnings.Count == 2 && back.Warnings[0].StartsWith("7 model elements had no usable geometry and were left out", StringComparison.Ordinal) &&
                back.Warnings[0].Contains("and 2 more", StringComparison.Ordinal) && back.Warnings[1].StartsWith("1 air terminal had no readable duct connector, so it was assumed", StringComparison.Ordinal) &&
                plain1.StartsWith("7 elements from Revit had no geometry", StringComparison.Ordinal) && plain2.StartsWith("1 air terminal from Revit was assumed", StringComparison.Ordinal) &&
                !Notes.Plain("something else").StartsWith("7", StringComparison.Ordinal),
                $"\"{back.Warnings[0]}\" → \"{plain1}\" · \"{back.Warnings[1]}\" → \"{plain2}\"");
        }

        // P13b-06: EPW speeds are met-station values (z0 0.05 m, 10 m); a z0 0.3 m site gets EN 1991-1-4 k_r(z0) ln(10/z0) ratios:
        // k_r = 0.19 (0.3/0.05)^0.07 = 0.21538, 0.21538 ln(33.33) = 0.75528 against 0.19 ln(200) = 1.00668, so 0.75026.
        {
            float f03 = WindRose.SiteSpeedFactor(0.3f), f005 = WindRose.SiteSpeedFactor(0.05f);
            float fApp = WindRose.SiteSpeedFactor(HighAccuracy.Z0FromExponent(new RunOptions().WindProfileExponent));
            Check("EPW terrain: a z0 0.3 m site from a 0.05 m station at 10 m is 0.7503 of the station speed (EN 1991-1-4), the station's own terrain 1.000, the app's default exponent is the z0 0.3 case",
                MathF.Abs(f03 - 0.75026f) < 5e-5f && MathF.Abs(f005 - 1f) < 1e-6f && MathF.Abs(fApp - f03) < 1e-6f,
                $"factor {f03:0.00000} · 4 m/s at the station = {4 * f03:0.000} m/s at the site · station terrain {f005:0.00000} · default exponent {fApp:0.00000}");
        }

        // P13b-04 / S3-09: true north 30 deg clockwise of model +Y. A wind FROM true west (270) blows toward true east (bearing 90), which
        // is model bearing 90 + 30 = 120: (sin 120, cos 120) x 4 m/s = (3.4641, -2.0000). North 0 is the old vector (4, 0). The
        // report words it back as "from 270 (W)", and an old file with no NorthDeg loads as 0 while 30 round-trips.
        {
            var w30 = SolverRun.WindVector(270, 4, 30);
            var w0 = SolverRun.WindVector(270, 4, 0);
            var wRose = SolverRun.WindVector(90, 2, -45);        // an EPW sector from true east, north 45 deg anticlockwise: model bearing 90 - 45 = 45, travel toward 225
            var nm = SampleBuilding.Build(); nm.NorthDeg = 30;
            var oldJson = System.Text.Json.Nodes.JsonNode.Parse(nm.ToJson())!.AsObject(); oldJson.Remove("NorthDeg");
            string said = WindFlow.Report.ReportWind.Text(w30, 30), saidModel = WindFlow.Report.ReportWind.Text(w30);
            Check("true north: north 30 deg + wind from 270 gives the model-axis vector (3.464, -2.000); north 0 is unchanged; the report and the file carry it",
                MathF.Abs(w30.X - 3.4641016f) < 1e-4f && MathF.Abs(w30.Y + 2f) < 1e-4f && MathF.Abs(w0.X - 4f) < 1e-4f && MathF.Abs(w0.Y) < 1e-4f &&
                MathF.Abs(wRose.X + 1.4142136f) < 1e-4f && MathF.Abs(wRose.Y + 1.4142136f) < 1e-4f &&
                said.Contains("from 270°", StringComparison.Ordinal) && saidModel.Contains("from 300°", StringComparison.Ordinal) &&
                BuildingModel.FromJson(nm.ToJson()).NorthDeg == 30f && BuildingModel.FromJson(oldJson.ToJsonString()).NorthDeg == 0f &&
                LiveSolver.Snapshot(nm).NorthDeg == 30f,
                $"north 30, from 270, 4 m/s -> ({w30.X:0.0000}, {w30.Y:0.0000}) · north 0 -> ({w0.X:0.0000}, {w0.Y:0.0000}) · rose from 90, north -45 -> ({wRose.X:0.0000}, {wRose.Y:0.0000}) · report \"{said}\" (model words it \"{saidModel}\")");
        }

        // ---- Batch 4 ------------------------------------------------------------------------------------------------------------------------

        // S3-03 / S3-04 / S3-05: the detection budget is the same on every machine; the detector's notes reach the import note; the unit comes from the
        // file when it declares one (glTF metres, FBX UnitScaleFactor) and the size guess reads the HORIZONTAL extent.
        {
            // IMP-B D1 (FIX_RULES rule 3): the S3-03 intent (the same detection on every machine) now reads as a fixed cell SIZE, not a cell budget:
            // 50 mm under 400 m2 of plan, else 100 mm, never above the 150 mm 3D cap, whatever the machine or the building's volume.
            var small = new BuildingModel(); small.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, 0), new Vector3(19, 19, 3)));
            var big = new BuildingModel(); big.Obstacles.Add(SampleBuilding.Box(new Vector3(0, 0, 0), new Vector3(21, 21, 3)));
            Check("S3-03 / IMP-B D1: room/opening detection uses a fixed cell size (50 mm under 400 m2 of plan, else 100 mm), not a cell budget",
                RoomDetect.DetectionDx(small) == RoomDetect.FineDx && RoomDetect.DetectionDx(big) == RoomDetect.StandardDx && RoomDetect.StandardDx <= 0.15f,
                $"{RoomDetect.DetectionDx(small) * 1000:0} mm at 361 m2, {RoomDetect.DetectionDx(big) * 1000:0} mm at 441 m2");

            string withNotes = MainWindow.ImportNote(10, 2, "3 rooms detected at 255 mm", 4, " Detection notes: a 91.8 m² region splits into 8 parts | 0 openings.");
            string noNotes = MainWindow.ImportNote(10, 2, "3 rooms detected at 255 mm", 4, "");
            Check("S3-04: the detector's notes are appended to the import note (and absent when there are none)",
                withNotes.Contains("Detection notes: a 91.8 m² region splits into 8 parts", StringComparison.Ordinal) && !noNotes.Contains("Detection notes", StringComparison.Ordinal),
                $"\"{withNotes[^70..]}\"");

            string unitDir = Path.Combine(Path.GetTempPath(), $"windflow-units-{System.Environment.ProcessId}"); Directory.CreateDirectory(unitDir);
            try
            {
                string Fbx(string scale) => "; FBX 7.4.0 project file\nFBXHeaderExtension:  {\n\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n}\nGlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n" +
                    "\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1\n\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2\n\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1\n" +
                    "\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0\n\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1\n\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\"," + scale + "\n" +
                    "\t\tP: \"OriginalUnitScaleFactor\", \"double\", \"Number\", \"\"," + scale + "\n\t}\n}\nDefinitions:  {\n\tVersion: 100\n\tCount: 2\n\tObjectType: \"Model\" {\n\t\tCount: 1\n\t}\n\tObjectType: \"Geometry\" {\n\t\tCount: 1\n\t}\n}\n" +
                    "Objects:  {\n\tGeometry: 1000, \"Geometry::tri\", \"Mesh\" {\n\t\tVertices: *9 {\n\t\t\ta: 0,0,0,1000,0,0,0,600,300\n\t\t}\n\t\tPolygonVertexIndex: *3 {\n\t\t\ta: 0,1,-3\n\t\t}\n\t\tGeometryVersion: 124\n\t}\n" +
                    "\tModel: 2000, \"Model::tri\", \"Mesh\" {\n\t\tVersion: 232\n\t\tProperties70:  {\n\t\t}\n\t\tShading: T\n\t\tCulling: \"CullingOff\"\n\t}\n}\nConnections:  {\n\tC: \"OO\",2000,0\n\tC: \"OO\",1000,2000\n}\n";
                var declared = new List<string>(); bool allRight = true;
                foreach (var (scale, wantMetres, wantIdx) in new[] { ("1.0", 0.01f, 1), ("100.0", 1f, 0), ("0.1", 0.001f, 2) })
                {
                    string p = Path.Combine(unitDir, $"u{scale}.fbx"); File.WriteAllText(p, Fbx(scale));
                    var gf = MeshImport.Load(p);
                    int idx = MeshImport.GuessUnit(gf);
                    declared.Add($"scale {scale} -> {gf.DeclaredMetres} m/unit, unit {MeshImport.Units[idx].Label}");
                    allRight &= MathF.Abs(gf.DeclaredMetres - wantMetres) < 1e-6f && idx == wantIdx && MeshImport.DeclaredUnit(gf) == wantIdx;
                }
                string gp = Path.Combine(unitDir, "house.gltf"); GltfWriter.Write(SampleBuilding.Build(), gp);
                var gg = MeshImport.Load(gp);
                string op = Path.Combine(unitDir, "t.obj"); File.WriteAllText(op, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
                var go = MeshImport.Load(op);
                Check("S3-05: an FBX's UnitScaleFactor (1 = cm, 100 = m, 0.1 = mm) and a glTF's metres set the unit; an OBJ declares none",
                    allRight && MathF.Abs(gg.DeclaredMetres - 1f) < 1e-6f && go.DeclaredMetres == 0f, string.Join(" · ", declared) + $" · glTF {gg.DeclaredMetres} · OBJ {go.DeclaredMetres}");
            }
            finally { try { Directory.Delete(unitDir, true); } catch (IOException) { } }

            // A 60 x 60 m footprint, 2000 high (a tower in centimetres? no -- a 20 m mast read in cm): the tall axis must not decide the unit.
            int yUpTower = MeshImport.GuessUnit(Vector3.Zero, new Vector3(60, 2000, 60), yUp: true), zUpTower = MeshImport.GuessUnit(Vector3.Zero, new Vector3(60, 60, 2000), yUp: false);
            int yUpSite = MeshImport.GuessUnit(Vector3.Zero, new Vector3(6000, 300, 4000), yUp: true);
            Check("S3-05: the size guess reads the horizontal extent (Y-up: X and Z; Z-up: X and Y): a 60 m footprint with a 2000 high mast stays metres, a 6000 wide site is mm",
                yUpTower == 0 && zUpTower == 0 && yUpSite == 2, $"Y-up tower {MeshImport.Units[yUpTower].Label} · Z-up tower {MeshImport.Units[zUpTower].Label} · 6000 wide site {MeshImport.Units[yUpSite].Label}");
        }

        // P15-05: a Revit room's upper limit below its storey's top is raised to the storey top; a higher one, or an unmeasured storey height, is left alone.
        {
            float lowTop = ExtractionNotes.RoomTop(2.44f, 0f, 0f, 3.2f, [0f, 3.2f]), highTop = ExtractionNotes.RoomTop(3.5f, 0f, 0f, 3.2f, [0f, 3.2f]), unmeasured = ExtractionNotes.RoomTop(2.44f, 0f, 0f, 0f, [0f]), upper = ExtractionNotes.RoomTop(5.44f, 3f, 3f, 3.2f, [0f, 3f, 6.2f]);
            float mezz = ExtractionNotes.RoomTop(2.44f, 0f, 0f, 6f, [0f, 3f, 6f]), mezzOwn = ExtractionNotes.RoomTop(2.44f, 0f, 0f, 6f, [0f, 6f]), mezzBelow = ExtractionNotes.RoomTop(3.5f, 0f, 0f, 6f, [0f, 3f, 6f]), landing = ExtractionNotes.RoomTop(2.44f, 0f, 0f, 3.2f, [0f, 1.6f, 3.2f]), mezzAt = ExtractionNotes.RoomTop(3.0f, 0f, 0f, 6f, [0f, 3f, 6f]), mezzUnder = ExtractionNotes.RoomTop(2.997f, 0f, 0f, 6f, [0f, 3f, 6f]);
            string note = ExtractionNotes.RaisedRoomTops(["Living", "Hall"]), plain = Notes.Plain(note);
            Check("P15-05: Revit room top 2.44 m under a 3.2 m storey becomes 3.2 m; 3.5 m stays; no storey height leaves it; the note reads as a plain sentence",
                MathF.Abs(lowTop - 3.2f) < 1e-6f && MathF.Abs(highTop - 3.5f) < 1e-6f && MathF.Abs(unmeasured - 2.44f) < 1e-6f && MathF.Abs(upper - 6.2f) < 1e-6f &&
                plain.StartsWith("2 rooms from Revit had a ceiling limit lower than the storey", StringComparison.Ordinal) && plain.Contains("next level above"),
                $"2.44 -> {lowTop:0.00} · 3.5 -> {highTop:0.00} · no height -> {unmeasured:0.00} · upper storey 5.44 -> {upper:0.00} · \"{plain}\"");
            Check("RX-07: a room is raised to the lowest level above its base, story or not: a mezzanine level at 3 m under a 6 m storey caps a 2.44 m room at 3 m; with no mezzanine it goes to 6 m; a level the room already spans does not cap it (RY-02: 3.5 m past a 3 m mezzanine goes to 6 m; a 1.6 m landing under a 2.44 m room in a 3.2 m storey goes to 3.2 m; RQ-01: a limit at or a few mm under the mezzanine level stays at it)",
                MathF.Abs(mezz - 3f) < 1e-6f && MathF.Abs(mezzOwn - 6f) < 1e-6f && MathF.Abs(mezzBelow - 6f) < 1e-6f && MathF.Abs(landing - 3.2f) < 1e-6f && MathF.Abs(mezzAt - 3f) < 1e-6f && MathF.Abs(mezzUnder - 3f) < 1e-6f,   // RY-02: mezzBelow was 3.5 (the raise cancelled)
                $"mezzanine at 3 m: 2.44 -> {mezz:0.00} · none: 2.44 -> {mezzOwn:0.00} · 3.5 -> {mezzBelow:0.00} · landing at 1.6: 2.44 -> {landing:0.00} · limit at the mezzanine 3.0 -> {mezzAt:0.00} · 2.997 -> {mezzUnder:0.00}");
        }

        // 6d (P13a-03): the opening-Cd placement note fires wherever the rig's scatter X(n, t) exceeds 5 %, at any cell size, and names X.
        {
            Opening Win(float n, float t) => new() { Width = n * 0.1f, Height = n * 0.1f, Depth = t * 0.1f, IsOpen = true };
            string? n8 = Analysis.OpeningCdScatterNote(Win(8, 1), 0.1f), n16 = Analysis.OpeningCdScatterNote(Win(16, 2), 0.1f), n16Thin = Analysis.OpeningCdScatterNote(Win(16, 1), 0.1f);
            // RP-01 (rule 3): X re-measured on the generic phase triples: 8 cells / wall 1 is 7.7 % (+-8 %), 16 / 1 is 5.1 % (+-5 %), 16 / 2 is 1.6 % (none).
            Check("6d: the opening-Cd uncertainty note fires at 8 cells across in a 1-cell wall (X 7.7 %, +-8 %) and at 16 cells in a 1-cell wall (5.1 %); a 16-cell opening in a 2-cell wall (1.6 %) gets none",
                n8 != null && n8.Contains("±8 %", StringComparison.Ordinal) && n8.Contains("a finer quality level reduces it", StringComparison.Ordinal)
                && n16Thin != null && n16Thin.Contains("±5 %", StringComparison.Ordinal) && n16 == null,
                $"8/1: {n8} · 16/1: {n16Thin} · 16/2: {n16 ?? "none"}");
        }

        // P12-03: the sample house's stair void sits at x 7.1-8.9 (over the Hall, inside the Landing), so the L1/L2 slab is whole over the Living room (x < 7).
        {
            var house = SampleBuilding.Build();
            var vg = Voxelizer.Build(house, 1_500_000, System.Environment.ProcessorCount, null);
            bool SlabAt(float x, float y)
            {
                int ix = (int)((x - vg.Origin.X) / vg.Dx), iy = (int)((y - vg.Origin.Y) / vg.Dx);
                for (float z = 2.9f; z <= 3.3f; z += vg.Dx / 2) if (vg.Cell[vg.Index(ix, iy, (int)((z - vg.Origin.Z) / vg.Dx))] == VoxelGrid.Solid) return true;
                return false;
            }
            bool livingSolid = SlabAt(6.4f, 5.6f) && SlabAt(6.4f, 7.0f), hallVoid = !SlabAt(8.0f, 5.6f) && !SlabAt(8.0f, 7.0f), hallSolid = SlabAt(9.6f, 6.2f);
            Check("P12-03: the L1/L2 slab is solid over the Living room (x 6.4), open over the Hall's stair void (x 8.0) and solid east of it (x 9.6)",
                livingSolid && hallVoid && hallSolid, $"Dx {vg.Dx * 1000:0} mm · slab at x 6.4: {livingSolid} · void at x 8.0: {hallVoid} · slab at x 9.6: {hallSolid}");
        }

        // P12-05: an open opening is carved fully through its wall even with a short Depth (the curtain-wall fallback 0.1 m) and a centre 0.8 Dx off the wall mid-plane.
        {
            var wm = new BuildingModel { Name = "wall", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
            wm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f)));
            wm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 0f, 0f), new Vector3(3.25f, 6f, 3f)));
            wm.Openings.Add(new Opening { Id = "o", Name = "o", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(3.125f + 0.1f, 3f, 1.5f), Normal = Vector3.UnitX, Width = 1.0f, Height = 1.2f, Depth = 0.1f });
            var dom = new DomainBox(new Vector3(0f, 0f, 0f), new Vector3(6f, 6f, 3f));
            var cg = Voxelizer.Build(wm, 75_000, 1, dom, sealUnreachable: false, minDx: 0.1f);
            int through = 0, blocked = 0, wallKept = 0;
            for (float y = 2.75f; y <= 3.25f; y += cg.Dx)
            for (float z = 1.1f; z <= 1.9f; z += cg.Dx)
            {
                bool open = true;
                for (float x = 2.9f; x <= 3.4f; x += cg.Dx / 2)
                    if (cg.Cell[cg.Index((int)((x - cg.Origin.X) / cg.Dx), (int)((y - cg.Origin.Y) / cg.Dx), (int)((z - cg.Origin.Z) / cg.Dx))] == VoxelGrid.Solid) open = false;
                if (open) through++; else blocked++;
            }
            for (float x = 3.05f; x <= 3.2f; x += cg.Dx / 2)
                if (cg.Cell[cg.Index((int)((x - cg.Origin.X) / cg.Dx), (int)((1.0f - cg.Origin.Y) / cg.Dx), (int)((1.5f - cg.Origin.Z) / cg.Dx))] == VoxelGrid.Solid) wallKept++;
            Check("P12-05: a window with Depth 0.1 m centred 0.1 m off a 0.25 m wall is open all the way through (no solid layer left in its footprint); the wall beside it stays",
                blocked == 0 && through > 0 && wallKept > 0, $"Dx {cg.Dx * 1000:0} mm · {through} footprint columns open through, {blocked} blocked · wall cells kept 1 m to the side: {wallKept}");
        }

        // RX-05: furniture is voxelized after the carve-through, so a 0.6 m box against a window stays solid while the window is still carved (and a centre in air starts the run at the wall).
        {
            var wm = new BuildingModel { Name = "wall", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
            wm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f)));
            wm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 0f, 0f), new Vector3(3.25f, 6f, 3f)));
            var wardrobe = SampleBuilding.Box(new Vector3(3.25f, 2.3f, 0.4f), new Vector3(3.85f, 3.7f, 2.6f)); wardrobe.Furniture = true;
            wm.Obstacles.Add(wardrobe);
            wm.Openings.Add(new Opening { Id = "o", Name = "o", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(3.125f, 3f, 1.5f), Normal = Vector3.UnitX, Width = 1.0f, Height = 1.2f, Depth = 0.25f });
            var dom = new DomainBox(new Vector3(0f, 0f, 0f), new Vector3(6f, 6f, 3f));
            var fg = Voxelizer.Build(wm, 75_000, 1, dom, sealUnreachable: false, minDx: 0.1f);
            bool SolidAt(float x, float y, float z) => fg.Cell[fg.Index((int)((x - fg.Origin.X) / fg.Dx), (int)((y - fg.Origin.Y) / fg.Dx), (int)((z - fg.Origin.Z) / fg.Dx))] == VoxelGrid.Solid;
            bool furnitureKept = SolidAt(3.45f, 3f, 1.5f) && SolidAt(3.6f, 3f, 1.0f) && SolidAt(3.7f, 2.7f, 1.8f);
            bool windowCarved = true; for (float x = 3.05f; x <= 3.2f; x += fg.Dx / 2) if (SolidAt(x, 3f, 1.5f)) windowCarved = false;
            bool wallKept = SolidAt(3.125f, 1.0f, 1.5f);
            Check("RX-05: a 0.6 m furniture box against a window stays solid; the window is still carved through its wall; the wall beside it stays",
                furnitureKept && windowCarved && wallKept, $"Dx {fg.Dx * 1000:0} mm · furniture solid {furnitureKept} · window carved {windowCarved} · wall kept {wallKept}");

            // A window whose centre lies in air, 0.15 m outside the wall face: the run starts at the nearest wall solid along the normal.
            var wo = new BuildingModel { Name = "wall", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
            wo.Obstacles.Add(SampleBuilding.Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f)));
            wo.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 0f, 0f), new Vector3(3.25f, 6f, 3f)));
            wo.Openings.Add(new Opening { Id = "o", Name = "o", Kind = OpeningKind.Window, IsOpen = true, Center = new Vector3(3.4f, 3f, 1.5f), Normal = Vector3.UnitX, Width = 1.0f, Height = 1.2f, Depth = 0.1f });
            var og = Voxelizer.Build(wo, 75_000, 1, dom, sealUnreachable: false, minDx: 0.1f);
            bool OSolid(float x, float y, float z) => og.Cell[og.Index((int)((x - og.Origin.X) / og.Dx), (int)((y - og.Origin.Y) / og.Dx), (int)((z - og.Origin.Z) / og.Dx))] == VoxelGrid.Solid;
            bool oCarved = true; for (float x = 3.05f; x <= 3.2f; x += og.Dx / 2) if (OSolid(x, 3f, 1.5f)) oCarved = false;
            Check("RX-05: an opening whose centre lies in air 0.15 m outside its wall is still carved through that wall; the wall 1 m to the side stays", oCarved && OSolid(3.125f, 1.0f, 1.5f),
                $"Dx {og.Dx * 1000:0} mm · carved {oCarved} · wall beside kept {OSolid(3.125f, 1.0f, 1.5f)}");
        }

        // RX-05 addendum: a door whose hole is already cut (centre in air) in a 0.2 m wall, facing a corridor wall 1.0 m / 0.6 m away: the carve-through never crosses the air into the corridor wall.
        {
            foreach (float gap in new[] { 1.0f, 0.6f })
            {
                var dm = new BuildingModel { Name = "door", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } };
                dm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f)));
                dm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 0f, 0f), new Vector3(3.2f, 2.55f, 3f)));
                dm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 3.45f, 0f), new Vector3(3.2f, 6f, 3f)));
                dm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.0f, 2.55f, 2.1f), new Vector3(3.2f, 3.45f, 3f)));
                dm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.2f + gap, 0f, 0f), new Vector3(3.4f + gap, 6f, 3f)));
                dm.Openings.Add(new Opening { Id = "d", Name = "d", Kind = OpeningKind.Door, IsOpen = true, Center = new Vector3(3.1f, 3f, 1.05f), Normal = Vector3.UnitX, Width = 0.9f, Height = 2.1f, Depth = 0.2f });
                var dg = Voxelizer.Build(dm, 75_000, 1, new DomainBox(new Vector3(0f, 0f, 0f), new Vector3(6f, 6f, 3f)), sealUnreachable: false, minDx: 0.1f);
                bool S(float x, float y, float z) => dg.Cell[dg.Index((int)((x - dg.Origin.X) / dg.Dx), (int)((y - dg.Origin.Y) / dg.Dx), (int)((z - dg.Origin.Z) / dg.Dx))] == VoxelGrid.Solid;
                int corridorSolid = 0, corridorTotal = 0, doorOpen = 0, doorTotal = 0;
                for (float y = 2.7f; y <= 3.3f; y += 0.1f)
                for (float z = 0.3f; z <= 1.9f; z += 0.2f)
                {
                    corridorTotal++; if (S(3.3f + gap, y, z)) corridorSolid++;
                    doorTotal++; if (!S(3.1f, y, z)) doorOpen++;
                }
                Check($"RX-05: a door whose hole is already cut, in a 0.2 m wall facing a corridor wall {gap:0.0} m away, stays open and leaves the corridor wall fully solid",
                    corridorSolid == corridorTotal && doorOpen == doorTotal, $"Dx {dg.Dx * 1000:0} mm · corridor wall solid {corridorSolid}/{corridorTotal} · door cells open {doorOpen}/{doorTotal}");
            }
        }

        // RY-03 / RY-04 / RY-05: thin walls (0.12 m) at Dx 0.25 m (6 x 6 x 3 m domain, 6912 cells), so no cell centre lies in them. Rows are the link rows through x = 2.875 / 3.125.
        {
            Opening OpeningAt(float cx, float width, float height, float depth, bool open) =>
                new() { Id = "o", Name = "o", Kind = OpeningKind.Window, IsOpen = open, Center = new Vector3(cx, 3f, 1.05f), Normal = Vector3.UnitX, Width = width, Height = height, Depth = depth };
            BuildingModel Thin() { var tm = new BuildingModel { Name = "thin", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3, Analysed = true } } }; tm.Obstacles.Add(SampleBuilding.Box(new Vector3(-1f, -1f, -0.2f), new Vector3(7f, 7f, 0f))); return tm; }
            var thinBox = new DomainBox(new Vector3(0f, 0f, 0f), new Vector3(6f, 6f, 3f));
            // Cut links through x between cells 11 and 12 (pulled into 12) over the rows with y in [y0, y1] and z in [z0, z1].
            (int Rows, int Open) Crossing(VoxelGrid tg, float y0, float y1, float z0, float z1, int ix = 12)
            {
                int rows = 0, open = 0;
                for (int iz = 0; iz < tg.Nz; iz++) for (int iy = 0; iy < tg.Ny; iy++)
                {
                    var c = tg.CellCenter(ix, iy, iz);
                    if (c.Y < y0 || c.Y > y1 || c.Z < z0 || c.Z > z1) continue;
                    bool Fluid(int i) => tg.Cell[i] != VoxelGrid.Solid || tg.LbmAir.Length == tg.Count && tg.LbmAir[i];
                    if (!Fluid(tg.Index(ix - 1, iy, iz)) || !Fluid(tg.Index(ix, iy, iz))) continue;   // inside a closed volume: no link
                    rows++; if (tg.WallLinks[1 * tg.Count + tg.Index(ix, iy, iz)] == 0) open++;
                }
                return (rows, open);
            }

            // RY-03: a hole 0.9 m wide in a 0.12 m wall (y 2.55-3.45), closed by a window 0.4 Dx (0.1 m) narrower per side: the crack rows y 2.625 and 3.375 stay cut.
            var cm = Thin();
            cm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.99f, 0f, 0f), new Vector3(3.11f, 2.55f, 3f)));
            cm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.99f, 3.45f, 0f), new Vector3(3.11f, 6f, 3f)));
            cm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.99f, 2.55f, 1.8f), new Vector3(3.11f, 3.45f, 3f)));
            cm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.99f, 2.55f, 0f), new Vector3(3.11f, 3.45f, 0.3f)));
            cm.Openings.Add(OpeningAt(3.05f, 0.7f, 1.5f, 0.12f, false));
            var closedG = Voxelizer.Build(cm, 6912, 1, thinBox, sealUnreachable: false, minDx: 0.1f);
            var crack = Crossing(closedG, 2.5f, 3.5f, 0.35f, 1.75f);
            Check("RY-03: a closed window 0.4 Dx narrower than its hole per side (a sub-cell wall) has every link through the hole cut, crack rows included",
                MathF.Abs(closedG.Dx - 0.25f) < 1e-3f && crack.Rows >= 12 && crack.Open == 0, $"Dx {closedG.Dx * 1000:0} mm · link rows in the hole {crack.Rows} · open (leaking) {crack.Open}");

            // RY-04: a 0.12 m partition (x 2.94-3.06, no cell centre inside) with an open door of Depth 0.12 whose centre is 0.2 m off the mid-plane: the far face is at 0.26 m.
            var pm = Thin();
            pm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.94f, 0f, 0f), new Vector3(3.06f, 6f, 3f)));
            pm.Openings.Add(OpeningAt(3.2f, 0.9f, 2.1f, 0.12f, true));
            var partG = Voxelizer.Build(pm, 6912, 1, thinBox, sealUnreachable: false, minDx: 0.1f);
            var doorX = Crossing(partG, 2.7f, 3.3f, 0.2f, 1.9f); var sideX = Crossing(partG, 0.8f, 1.2f, 0.2f, 1.9f);
            Check("RY-04: an open door in a partition thinner than a cell, centre 0.2 m off its mid-plane, is open (no link cut across its footprint); the partition beside it stays cut",
                MathF.Abs(partG.Dx - 0.25f) < 1e-3f && doorX.Rows >= 8 && doorX.Open == doorX.Rows && sideX.Rows > 0 && sideX.Open == 0,
                $"Dx {partG.Dx * 1000:0} mm · door rows {doorX.Rows}, open {doorX.Open} · partition rows {sideX.Rows}, open {sideX.Open}");

            // RQ-02: the same door with a 0.1 m second leaf 0.2 m on its open side (x 3.40-3.50, inside the span): the stretch goes toward the door's own wall only, so the leaf keeps its links (cells 13 -> 14) cut.
            var lm = Thin();
            lm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.94f, 0f, 0f), new Vector3(3.06f, 6f, 3f)));
            lm.Obstacles.Add(SampleBuilding.Box(new Vector3(3.40f, 0f, 0f), new Vector3(3.50f, 6f, 3f)));
            lm.Openings.Add(OpeningAt(3.2f, 0.9f, 2.1f, 0.12f, true));
            var leafG = Voxelizer.Build(lm, 6912, 1, thinBox, sealUnreachable: false, minDx: 0.1f);
            var ownX = Crossing(leafG, 2.7f, 3.3f, 0.2f, 1.9f); var leafX = Crossing(leafG, 2.7f, 3.3f, 0.2f, 1.9f, ix: 14);
            Check("RQ-02: a door whose centre is in air, its own thin wall on one side and a second 0.1 m leaf within the span on the other, opens through its own wall and leaves the leaf cut",
                MathF.Abs(leafG.Dx - 0.25f) < 1e-3f && ownX.Rows >= 8 && ownX.Open == ownX.Rows && leafX.Rows >= 8 && leafX.Open == 0,
                $"Dx {leafG.Dx * 1000:0} mm · own wall rows {ownX.Rows}, open {ownX.Open} · leaf rows {leafX.Rows}, open {leafX.Open}");

            // RP-05: the same door with furniture nearer than its wall (x 3.22-3.45, Mesh.Furniture; the wall face is 0.14 m away, the furniture 0.02 m): the stretch skips furniture,
            // so the door still opens through its own wall, and the furniture cell it carved through stays Solid.
            var fm = Thin();
            fm.Obstacles.Add(SampleBuilding.Box(new Vector3(2.94f, 0f, 0f), new Vector3(3.06f, 6f, 3f)));
            var furniture = SampleBuilding.Box(new Vector3(3.22f, 2.4f, 0f), new Vector3(3.45f, 3.6f, 1.0f)); furniture.Furniture = true;
            fm.Obstacles.Add(furniture);
            fm.Openings.Add(OpeningAt(3.2f, 0.9f, 2.1f, 0.12f, true));
            var furG = Voxelizer.Build(fm, 6912, 1, thinBox, sealUnreachable: false, minDx: 0.1f);
            var ownFur = Crossing(furG, 2.7f, 3.3f, 0.2f, 1.9f);
            bool furSolid = furG.Cell[furG.Index(13, 12, 1)] == VoxelGrid.Solid;
            Check("RP-05: a door whose centre is in air, its own thin wall 0.14 m away and furniture 0.02 m away on the other side, opens through its own wall and leaves the furniture solid",
                MathF.Abs(furG.Dx - 0.25f) < 1e-3f && ownFur.Rows >= 8 && ownFur.Open == ownFur.Rows && furSolid,
                $"Dx {furG.Dx * 1000:0} mm · own wall rows {ownFur.Rows}, open {ownFur.Open} · furniture cell solid {furSolid}");

            // RY-05: a closet behind a 0.1 m partition with a 0.4 m gap (x 1.55-1.95): the nearer-end rule closes the gap's south row in Cell, the LBM keeps it open.
            BuildingModel Closet(bool gap, bool wide = false)
            {
                var km = Thin();
                if (gap) { km.Obstacles.Add(SampleBuilding.Box(new Vector3(1f, 2.675f, 0f), new Vector3(1.55f, 2.775f, 2.5f))); km.Obstacles.Add(SampleBuilding.Box(new Vector3(1.95f, 2.675f, 0f), new Vector3(3f, 2.775f, 2.5f))); }
                else km.Obstacles.Add(SampleBuilding.Box(new Vector3(1f, 2.675f, 0f), new Vector3(3f, 2.775f, 2.5f)));
                km.Obstacles.Add(SampleBuilding.Box(new Vector3(0.8f, 2.675f, 0f), new Vector3(1.1f, 5.5f, 2.8f)));
                km.Obstacles.Add(SampleBuilding.Box(new Vector3(2.7f, 2.675f, 0f), new Vector3(3f, 5.5f, 2.8f)));
                km.Obstacles.Add(SampleBuilding.Box(new Vector3(0.8f, 5.2f, 0f), new Vector3(3f, 5.5f, 2.8f)));
                km.Obstacles.Add(SampleBuilding.Box(new Vector3(0.8f, 2.675f, 2.5f), new Vector3(3f, 5.5f, 2.8f)));
                var kr = new Room { Id = "closet", Name = "Closet", Level = "L1", ZMin = 0, ZMax = 2.5f };
                if (wide) kr.Loops.Add([new Vector2(0.2f, 0.2f), new Vector2(5.8f, 0.2f), new Vector2(5.8f, 5.15f), new Vector2(0.2f, 5.15f)]);   // RQ-03: one big ventilated room holding the pocket
                else kr.Loops.Add([new Vector2(1.15f, 2.85f), new Vector2(2.65f, 2.85f), new Vector2(2.65f, 5.15f), new Vector2(1.15f, 5.15f)]);
                km.Rooms.Add(kr);
                return km;
            }
            const string gapNote = "Closet connects only through a gap narrower than about 2 cells at this cell size; its age of air cannot be computed and shows as sealed. Use a finer quality level or widen the gap.";
            var gapG = Voxelizer.Build(Closet(true), 6912, 1, thinBox);
            var shutG = Voxelizer.Build(Closet(false), 6912, 1, thinBox);
            var wideG = Voxelizer.Build(Closet(true, wide: true), 6912, 1, thinBox);   // RQ-03: the same pocket is a small share of a ventilated room
            var houseG = Voxelizer.Build(SampleBuilding.Build(), Simulation.TargetCells(Quality.Preview, true));
            int houseNotes = houseG.Warnings.Count(w => w.Contains("connects only through a gap narrower", StringComparison.Ordinal));
            Check("RY-05: a closet behind a partition with a 0.4 m gap that closes in Cell but not in the LBM gets the 'connects only through a gap' note naming it; with no gap, as a small pocket of a big ventilated room (RQ-03), and in the sample house, there is none",
                MathF.Abs(gapG.Dx - 0.25f) < 1e-3f && gapG.Warnings.Contains(gapNote) && !shutG.Warnings.Any(w => w.Contains("connects only through a gap narrower", StringComparison.Ordinal)) && !wideG.Warnings.Any(w => w.Contains("connects only through a gap narrower", StringComparison.Ordinal)) && houseNotes == 0,
                $"Dx {gapG.Dx * 1000:0} mm · gap notes [{string.Join(" | ", gapG.Warnings.Where(w => w.Contains("gap narrower", StringComparison.Ordinal)))}] · no-gap notes {shutG.Warnings.Count(w => w.Contains("gap narrower", StringComparison.Ordinal))} · pocket in a big ventilated room {wideG.Warnings.Count(w => w.Contains("gap narrower", StringComparison.Ordinal))} · sample house at Preview ({houseG.Dx * 1000:0} mm) {houseNotes}");
        }

        // P17-04 / P17-05 / P17-06: the cell-count guard, one bytes-per-cell figure, and failure wording that names the cause.
        {
            string? refusedBig = null;
            try { Voxelizer.Build(SampleBuilding.Build(), int.MaxValue, 1, new DomainBox(new Vector3(-200, -200, 0), new Vector3(200, 200, 100))); } catch (InvalidOperationException ex) { refusedBig = ex.Message; }
            bool capOk = AccurateBudget.VramCap(0, 1_500_000, false) == LbmSolver.MaxCells && AccurateBudget.VramCap(4096, 1_500_000, false) <= LbmSolver.MaxCells && LbmSolver.MaxCells == Math.Min(int.MaxValue / 20, Array.MaxLength / 20);   // RX-09a: the 20 x count flag array must also fit Array.MaxLength
            Check("P17-04: a grid past int.MaxValue / 20 cells (107 M) is refused by the voxelizer before it allocates, in plain words; the VRAM cap never exceeds it",
                refusedBig != null && refusedBig.Contains("million cells", StringComparison.Ordinal) && capOk, $"refused: {refusedBig ?? "NOT refused"} · cap without VRAM info {AccurateBudget.VramCap(0, 1_500_000, false):N0}");
            Check("P17-05: one bytes-per-cell figure (238 isothermal, 298 thermal) in Core, the App's Accurate budget and HighAccuracy",
                LbmSolver.GpuBytesPerCell == 238 && LbmSolver.GpuBytesPerCellThermal == 298 && AccurateBudget.BytesPerCell == 238 && AccurateBudget.ThermalBytesPerCell == 298 &&   // 221 / 281 (273 + 8: the 6j held-room plane and gather, P14-01) + 17: the 6k online age (P14-06)
                HighAccuracy.BytesPerCell == 238 && HighAccuracy.BytesPerCell + HighAccuracy.ThermalExtraBytesPerCell == 298,
                $"Core {LbmSolver.GpuBytesPerCell}/{LbmSolver.GpuBytesPerCellThermal} · AccurateBudget {AccurateBudget.BytesPerCell}/{AccurateBudget.ThermalBytesPerCell} · HighAccuracy {HighAccuracy.BytesPerCell}+{HighAccuracy.ThermalExtraBytesPerCell}");
            string div = LiveSolver.AccurateFailureText(new InvalidOperationException("Airflow solver diverged; try Refine quality or a lower wind speed")),
                   oom = LiveSolver.AccurateFailureText(new OutOfMemoryException("Out of memory"));
            // Analysis: +-Infinity (not only NaN) in any field, and a non-finite temperature, make the result non-physical.
            var pg = new VoxelGrid { Nx = 8, Ny = 8, Nz = 6, Dx = 0.5f, Origin = Vector3.Zero }; pg.Cell = new byte[pg.Count];
            var pm = new BuildingModel { Name = "p" }; var pr = new Room { Id = "r", Name = "r", Level = "L1", ZMin = 0.5f, ZMax = 2.5f };
            pr.Loops.Add([new Vector2(1, 1), new Vector2(3, 1), new Vector2(3, 3), new Vector2(1, 3)]); pm.Rooms.Add(pr);
            bool Physical(Action<FlowField> poison)
            {
                int pn = pg.Count; var pf = new FlowField { Grid = pg, Ux = new float[pn], Uy = new float[pn], Uz = new float[pn], P = new float[pn] };
                poison(pf);
                var res = Analysis.Analyze(pm, pf, Vector2.Zero, new RunOptions()); Analysis.Finish(res);
                return res.Ledger.IsPhysical;
            }
            bool clean = Physical(_ => { }), infP = Physical(f => f.P[pg.Index(2, 2, 2)] = float.PositiveInfinity),
                 nanU = Physical(f => f.Ux[pg.Index(2, 2, 2)] = float.NaN),
                 infT = Physical(f => { f.T = new float[pg.Count]; f.T[pg.Index(5, 5, 3)] = float.NegativeInfinity; });
            Check("P17-06: a dropped averaged pass says \"diverged\" only for the solver's own divergence; any Inf/NaN in the velocity, pressure or temperature makes the result non-physical",
                div.Contains("diverged", StringComparison.Ordinal) && !oom.Contains("diverged", StringComparison.Ordinal) && oom.Contains("Out of memory", StringComparison.Ordinal) && clean && !infP && !nanU && !infT,
                $"\"{div}\" · \"{oom}\" · physical: clean {clean}, Inf in P {infP}, NaN in Ux {nanU}, -Inf in T {infT}");
        }

        // Batch R follow-ups: (i) the sealed-room note says what a Diffuser moves; (ii) the annual building mean names its sealed rooms; (iii) a glTF buffer's byteLength must match its data.
        {
            string noteWithDiffuser = Voxelizer.SealedBalanceNote("Bathroom", 0.10f, 0.05f, 0.05f, 0.10f), plainNote = Voxelizer.SealedBalanceNote("Bathroom", 0.10f, 0.05f, 0.05f);
            Check("(i) sealed-room balance note names a Diffuser's flow and says it adds no air (a body-force device creates no mass, so it cannot anchor an extract)",
                noteWithDiffuser.Contains("A Diffuser in this room moves 360 m³/h but adds no air", StringComparison.Ordinal) && !plainNote.Contains("Diffuser", StringComparison.Ordinal) &&
                Notes.Plain(noteWithDiffuser) == Notes.Plain(plainNote), $"\"{noteWithDiffuser[^95..]}\"");

            var l1 = new MassLedger { SealedVolume = 30f, SealedRooms = ["Bath"] }; var l2 = new MassLedger { SealedVolume = 45f, SealedRooms = ["Bath", "Store"] }; var l3 = new MassLedger();
            var (sr, sv) = Annual.SealedAcross([l1, l2, l3]);
            string label = new AnnualResult { SealedRooms = sr, SealedVolume = sv }.VolumeText, none = new AnnualResult().VolumeText;
            Check("(ii) the annual study's building mean names the sealed rooms it leaves out (union over the bins, the largest sealed volume), as RW-03's labels do",
                sr.SequenceEqual(["Bath", "Store"]) && sv == 45f && label.Contains("sealed rooms Bath, Store", StringComparison.Ordinal) && none == "all indoor volume", $"\"{label}\" · none sealed: \"{none}\"");

            string gdir = Path.Combine(Path.GetTempPath(), $"windflow-buf-{System.Environment.ProcessId}"); Directory.CreateDirectory(gdir);
            try
            {
                string Json(string? uri, int len) => "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{" + (uri != null ? "\"uri\":\"" + uri + "\"," : "") + "\"byteLength\":" + len + "}]}";
                string? Refuses(string path) { try { ImportGuard.Check(path); return null; } catch (InvalidDataException ex) { return ex.Message; } }
                string Write(string name, string text) { string p = Path.Combine(gdir, name); File.WriteAllText(p, text); return p; }
                File.WriteAllBytes(Path.Combine(gdir, "m12.bin"), new byte[12]); File.WriteAllBytes(Path.Combine(gdir, "m40.bin"), new byte[40]);
                string data12 = "data:application/octet-stream;base64," + Convert.ToBase64String(new byte[12]);
                byte[] Glb(string json, int binLen)
                {
                    var jb = System.Text.Encoding.UTF8.GetBytes(json); int pad = (4 - jb.Length % 4) % 4; Array.Resize(ref jb, jb.Length + pad); for (int i = jb.Length - pad; i < jb.Length; i++) jb[i] = 0x20;
                    using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
                    bw.Write(0x46546C67u); bw.Write(2u); bw.Write((uint)(12 + 8 + jb.Length + 8 + binLen));
                    bw.Write((uint)jb.Length); bw.Write(0x4E4F534Au); bw.Write(jb);
                    bw.Write((uint)binLen); bw.Write(0x004E4942u); bw.Write(new byte[binLen]);
                    return ms.ToArray();
                }
                string? e1 = Refuses(Write("d12.gltf", Json(data12, 12))), e2 = Refuses(Write("d36.gltf", Json(data12, 36))),
                        e3 = Refuses(Write("x12.gltf", Json("m12.bin", 12))), e4 = Refuses(Write("x100.gltf", Json("m12.bin", 100))), e5 = Refuses(Write("x40.gltf", Json("m40.bin", 12))),
                        e6 = Refuses(Write("miss.gltf", Json("nothere.bin", 12)));
                string g1 = Path.Combine(gdir, "g12.glb"), g2 = Path.Combine(gdir, "g40.glb");
                File.WriteAllBytes(g1, Glb(Json(null, 12), 12)); File.WriteAllBytes(g2, Glb(Json(null, 40), 12));
                string? e7 = Refuses(g1), e8 = Refuses(g2);
                Check("(iii) ImportGuard compares each glTF buffer's byteLength with its data: base64 data URI, external .bin, GLB BIN chunk; a mismatch is refused, a missing file is left to Assimp",
                    e1 == null && e2 != null && e3 == null && e4 != null && e5 == null && e6 == null && e7 == null && e8 != null,   // RX-10b: an external .bin longer than byteLength (40 vs 12) is fine now
                    $"data ok {e1 == null} · data 36 vs 12 refused {e2 != null} · .bin ok {e3 == null} · .bin 100 vs 12 refused {e4 != null} · .bin 40 vs 12 refused {e5 != null} (must not be) · missing .bin passes {e6 == null} · GLB ok {e7 == null} · GLB 40 vs 12 refused {e8 != null} · \"{e4}\"");
            }
            finally { try { Directory.Delete(gdir, true); } catch (IOException) { } }
        }

        // Accuracy step 1 (C1.3a/b/c + the inflow switch): the 6b NEE inflow is the default and WINDFLOW_INFLOW=EQ opts into the equilibrium one (RA-04); the IC-seed, fixed-batch and group-noise seams.
        {
            var houseA = SampleBuilding.Build();
            var windA = new Vector2(4f, 0f);
            // NEE default: the wind ring classifies its inflow cells BcInflow; with the EQ switch they are BcInflowEq (state restored)
            var gridA = Voxelizer.Build(houseA, 20_000, System.Environment.ProcessorCount, null).LbmView();
            bool neeBefore = LbmSolver.NeeInflow;
            (int Nee, int Eq) byDefault, withEq;
            try { LbmSolver.NeeInflow = true; byDefault = LbmSolver.CountRingInflowClasses(gridA, windA); LbmSolver.NeeInflow = false; withEq = LbmSolver.CountRingInflowClasses(gridA, windA); }
            finally { LbmSolver.NeeInflow = neeBefore; }
            Check("NEE default: the wind ring's inflow cells are the 6b non-equilibrium inflow (BcInflow) by default and feq(1, u) (BcInflowEq) with WINDFLOW_INFLOW=EQ",
                byDefault.Nee > 0 && byDefault.Eq == 0 && withEq.Eq > 0 && withEq.Nee == 0 && withEq.Eq == byDefault.Nee,
                $"default: {byDefault.Eq} Eq / {byDefault.Nee} NEE inflow cells · EQ switch: {withEq.Eq} Eq / {withEq.Nee} NEE");

            // C1.3a: IC seed. Two tiny cold CPU runs of the same seed land bitwise-equal velocity fields; seed 3 vs 4 differ; seed 0 is the unseeded run.
            FlowField ColdRun(int? seed)
            {
                var gI = Voxelizer.Build(houseA, 20_000, System.Environment.ProcessorCount, null);
                var oI = new RunOptions { Backend = Backend.Cpu };
                if (seed is int sd) oI.IcSeed = sd;
                using var sI = LbmSolver.Start(gI, houseA, windA, oI);
                sI.ResetAveraging(true);   // averaging starts after the warm-up by default; start it now so the short run has a mean
                sI.Step(30, default);
                return sI.ToField();
            }
            static bool SameU(FlowField a, FlowField b) => a.Ux.AsSpan().SequenceEqual(b.Ux) && a.Uy.AsSpan().SequenceEqual(b.Uy) && a.Uz.AsSpan().SequenceEqual(b.Uz);
            var seed3a = ColdRun(3); var seed3b = ColdRun(3); var seed4 = ColdRun(4); var seed0 = ColdRun(0); var unseeded = ColdRun(null);
            Check("C1.3a: WINDFLOW_IC_SEED: equal seeds give bitwise-equal landed velocity fields, seed 3 and 4 differ, seed 0 equals the unseeded run bitwise",
                SameU(seed3a, seed3b) && !SameU(seed3a, seed4) && SameU(seed0, unseeded) && !SameU(seed3a, unseeded),
                $"3=3 {SameU(seed3a, seed3b)} · 3!=4 {!SameU(seed3a, seed4)} · 0==unseeded {SameU(seed0, unseeded)} · 3!=unseeded {!SameU(seed3a, unseeded)} · {seed3a.Grid.Count} cells");

            // C1.3b: WINDFLOW_FIXED_BATCHES=1,3: batches 1..4 run, batch 1 is discarded, the pass stops right after batch 4 with reason Fixed and keeps exactly 3; the landed field is the mean of batches 2-4.
            var fix = new FakePassSession(k => 10 + k) { Warmup = 10 };
            var outFix = AveragedPass.Run(fix, new BuildingModel(), Vector2.Zero, new RunOptions(),
                new PassSettings { CapSeconds = 1e9, AnalyseInline = true, Probe = _ => 20f, FixedBatches = (1, 3), Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux[0])], []) }, solveScalars: false);
            var envFix = PassSettings.ParseFixedBatches("2,5");
            Check("C1.3b: WINDFLOW_FIXED_BATCHES=1,3 stops with reason Fixed after exactly 4 batches and keeps exactly 3 (mean of batches 2-4, no power-of-two truncation)",
                outFix.Report.Status.Reason == ConvergenceStopReason.Fixed && outFix.Report.Batches == 4 && outFix.Kept == 3 && MathF.Abs(outFix.Field.Ux[0] - 13f) < 1e-4f &&
                outFix.Report.Status.Rooms.Count == 1 && outFix.Report.Status.Rooms[0].BatchCount == 3 && envFix == (2, 5) && PassSettings.ParseFixedBatches("3") == null,
                $"{outFix.Report.Status.Reason} after {outFix.Report.Batches} batches, kept {outFix.Kept}, landed Ux {outFix.Field.Ux[0]:0.####} (want 13) · monitor kept {outFix.Report.Status.Rooms.FirstOrDefault()?.BatchCount} · \"{outFix.Landed}\"");

            // C1.3c: WINDFLOW_GROUP_AGES=4 on a frozen flow (every pull is the same developed field, so every group mean is that field): the group fresh-air values equal the full-sample
            // ones within 1e-4 relative and sigma is 0; the landed field and age are bitwise those of the same pass without the seam.
            var gC = Voxelizer.Build(houseA, 30_000, System.Environment.ProcessorCount, null);
            FlowField frozen;
            using (var sC = LbmSolver.Start(gC, houseA, windA, new RunOptions { Backend = Backend.Cpu })) { sC.ResetAveraging(true); sC.Step(150, default); frozen = sC.ToField(); }
            var optC = new RunOptions { Backend = Backend.Cpu, AgeOfAir = true };
            PassOutcome FrozenPass(int groupK) => AveragedPass.Run(new FrozenPassSession(frozen), houseA, windA, optC,
                new PassSettings { CapSeconds = 1e9, AnalyseInline = true, Probe = _ => 20f, FixedBatches = (1, 8), GroupAges = groupK,
                    Analyse = (f, fine) => new PassBatch(f, fine, houseA.Rooms.Select(r => (r.Id, 5f)).ToList(), []) });
            var withGroups = FrozenPass(4); var noGroups = FrozenPass(0);
            var refRes = Analysis.Analyze(houseA, withGroups.Field, windA, optC);
            refRes.Scalars = ScalarSolver.Solve(houseA, withGroups.Field, optC, ageOnly: true); Analysis.Finish(refRes);
            var rowsC = withGroups.Report.GroupSigma;
            bool groupsOk = rowsC.Count == houseA.Rooms.Count + 1 && withGroups.Report.GroupK == 4;
            string worstC = "";
            foreach (var row in rowsC)
            {
                double want = row.Name == "Building" ? refRes.Ledger.FreshAch : refRes.Rooms.First(r => (r.Room.Name.Length > 0 ? r.Room.Name : r.Room.Id) == row.Name).FreshAch;
                bool fin = double.IsFinite(want);
                bool ok = fin ? Math.Abs(row.Fresh - want) <= 1e-4 * Math.Abs(want) && row.FreshSigma <= 1e-4 * Math.Abs(want) : double.IsNaN(row.Fresh);
                if (row.Name != "Building") ok &= row.TotalSigma <= 1e-4 * Math.Abs(row.Total) && Math.Abs(row.Total - 5.0) < 1e-4;
                groupsOk &= ok;
                worstC += $"{row.Line(4)} [want {want:0.00}{(ok ? "" : " MISMATCH")}]; ";
            }
            bool bitwise = withGroups.Field.Ux.AsSpan().SequenceEqual(noGroups.Field.Ux) && withGroups.Field.Uy.AsSpan().SequenceEqual(noGroups.Field.Uy) &&
                           withGroups.Field.Uz.AsSpan().SequenceEqual(noGroups.Field.Uz) && withGroups.Scalars.Age.AsSpan().SequenceEqual(noGroups.Scalars.Age) &&
                           withGroups.Report.Batches == noGroups.Report.Batches && noGroups.Report.GroupSigma.Count == 0;
            Check("C1.3c: WINDFLOW_GROUP_AGES=4 on a frozen flow: the four group fresh-air values (rooms and building) equal the full-sample ones within 1e-4 and sigma is 0; the landed field and age are bitwise unchanged by the seam",
                groupsOk && bitwise, $"{withGroups.Report.GroupSigmaNote} · bitwise landed {bitwise} · {worstC}");
        }

        // PLAN_V2 4.3/4.4 + Lock: the same UiTable rows the window applies, read against MainWindow.axaml.
        foreach (var (name, ok, detail) in UiTableChecks()) Check(name, ok, detail);

        // Feature checks live in SelfCheck.<Feature>.cs (partial class): each adds one line here, Checks_<Feature>(feature); and its file fills the list.
        var feature = new List<(string Name, bool Ok, string Detail)>();
        // FEATURE CHECKS BEGIN
        Checks_Paired(feature);
        Checks_Import(feature);
        Checks_Portal(feature);
        Checks_Age(feature);
        Checks_Plume(feature);
        // FEATURE CHECKS END
        foreach (var f in feature) Check(f.Name, f.Ok, f.Detail);
        // Ultra planning: pure arithmetic (the Dx rule, the memory cap, the no-open-opening fallback, the labels).
        {
            float D(float? n, float std, float fit) => Ultra.ChooseDx(n, std, fit, out _);
            var house = SampleBuilding.Build();
            var allClosed = BuildingModel.FromJson(house.ToJson()); foreach (var op in allClosed.Openings) op.IsOpen = false;
            float? narrowestHouse = Ultra.NarrowestOpen(house), narrowestClosed = Ultra.NarrowestOpen(allClosed);
            float expectNarrow = house.Openings.Where(x => x.IsOpen).Select(x => MathF.Min(x.Width, x.Height)).Min();
            Ultra.ChooseDx(0.8f, 0.2f, 0.01f, out float tA);
            long cap16 = Ultra.CellCap(16L * 1024 * 1024 * 1024, 0);
            float fitDx = Ultra.FinestDxWithin(dx => (long)(1e3 / (dx * dx * dx)), 20_000_000);
            bool okPlan =
                MathF.Abs(D(0.8f, 0.2f, 0.01f) - 0.05f) < 1e-6f && MathF.Abs(tA - 0.05f) < 1e-6f &&          // target = narrowest / 16
                MathF.Abs(D(0.2f, 0.2f, 0.01f) - Ultra.MinDx) < 1e-6f && Ultra.MinDx == 0.025f &&              // the 25 mm floor
                MathF.Abs(D(0.8f, 0.2f, 0.08f) - 0.08f) < 1e-6f &&                                              // memory: never finer than the GPU holds
                MathF.Abs(D(3.0f, 0.1f, 0.01f) - 0.1f) < 1e-6f &&                                               // never coarser than Standard's Dx
                MathF.Abs(D(null, 0.2f, 0.01f) - 0.1f) < 1e-6f && MathF.Abs(D(null, 0.04f, 0.01f) - Ultra.MinDx) < 1e-6f &&   // no open openings: Standard / 2, clamped the same
                narrowestClosed == null && narrowestHouse is float nh && MathF.Abs(nh - expectNarrow) < 1e-6f &&
                cap16 > 38_000_000 && cap16 < 46_000_000 && Ultra.CellCap(16L * 1024 * 1024 * 1024, 1_000_000) < cap16 &&
                fitDx > 0 && (long)(1e3 / (fitDx * fitDx * fitDx)) <= 20_000_000 && (long)(1e3 / (MathF.BitDecrement(fitDx) * MathF.BitDecrement(fitDx) * MathF.BitDecrement(fitDx))) > 19_900_000 &&
                Ultra.ResultLabel(0.0371f) == "Ultra: finest grid this GPU fits (Dx 37.1 mm), long run; accuracy not separately validated." &&
                Ultra.CapHourChoices.SequenceEqual([6, 12, 20, 24]) && Ultra.DefaultCapHours == 20;
            var demo = Ultra.Plan(house, new Vector2(4f, 0f), 5_000_000, 16L * 1024 * 1024 * 1024, 0, 2950, true, "NVIDIA GeForce RTX 5070 Ti", 20);
            okPlan &= demo.Feasible && demo.Dx >= Ultra.MinDx && demo.Dx <= demo.StandardDx * 1.0001f && demo.Cells <= demo.CellCap && demo.Lines().Any(x => x.Contains("separately validated", StringComparison.Ordinal)) && demo.Lines().Any(x => x.Contains("measured 2,950 MLUPS", StringComparison.Ordinal));
            long gib = 1L << 30, v16 = 16 * gib;
            long capFree14 = Ultra.CellCap(v16, 1_000_000, 14 * gib), capFree15 = Ultra.CellCap(v16, 0, 15 * gib + gib / 2), capUnknown = Ultra.CellCap(v16, 0, 0);
            var freePlan = Ultra.Plan(house, new Vector2(4f, 0f), 5_000_000, v16, 0, 2950, true, "NVIDIA GeForce RTX 5070 Ti", 20, freeVramBytes: 14 * gib);
            okPlan &= capFree14 == (long)((14.0 * gib - Ultra.HeadroomBytes) / LbmSolver.GpuBytesPerCell)                       // free VRAM - 1.5 GiB, the Preview session already counted in "free"
                   && capFree15 == (long)(0.85 * v16 / LbmSolver.GpuBytesPerCell)                                             // never more than 0.85 of the card
                   && capUnknown == Ultra.CellCap(v16, 0) && capUnknown == (long)(0.6 * v16 / LbmSolver.GpuBytesPerCell)      // no free figure: the 0.6 x total rule
                   && capFree14 > capUnknown
                   && Ultra.OomRetryCells(54_000_000) == 45_900_000 && Math.Abs(Math.Pow(Ultra.OomRetryDx(0.0833f) / 0.0833f, -3) - 0.85) < 1e-3   // the out-of-memory retry: 85 % of the cells
                   && freePlan.Cells <= freePlan.CellCap && freePlan.FreeKnown && freePlan.ShortLines().Any(x => x.Contains("keeps 1.5 GB free for Windows and other apps", StringComparison.Ordinal))
                   && demo.ShortLines().Any(x => x.Contains("keeps 40 % free", StringComparison.Ordinal))
                   && Ultra.GridSentence == "At 16 cells across, the measured opening flow is within about 1-3 % of its target, so Ultra does not go finer."
                   && (LbmSolver.CellLimit(true, false, true) >= LbmSolver.CellLimit(true, false) || !LbmSolver.GpuAvailable);   // the 0.88 guard is Ultra's alone (equal when there is no GPU to ask)
            Check("Ultra planning: Dx = narrowest open opening / 16, floor 25 mm, never finer than the GPU memory holds, never coarser than Standard's, no open openings = Standard / 2; cell cap from VRAM; labels",
                okPlan, $"narrowest {narrowestHouse:0.00} m, 16 GiB cap {cap16 / 1e6:0.#}M cells, finest Dx within 20M cells {fitDx:0.0000} | sample house on a 16 GB card: " + string.Join(" | ", demo.Lines().Take(5)));
        }

        // Ultra checkpoint round trip on a tiny CPU grid: run an averaged pass to a batch boundary, write the checkpoint, load it into a fresh session and pass,
        // step both the same number of steps and compare the populations, the online age and the landed batch means.
        {
            var m = SampleBuilding.Build(); var w = new Vector2(4f, 0f);
            var o = new RunOptions { Backend = Backend.Cpu, Quality = Quality.Refine, OnlineAge = true, OnlineAgePerRoom = true, Ultra = new UltraSettings() };
            var g = Voxelizer.Build(m, 12_000, 2, null);
            PassSettings Cfg() => new() { CapSeconds = 1e12, AnalyseInline = true, Probe = _ => 20f, BatchScale = 0.1f,
                Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux.Average())], f.Age.Length > 0 ? [("r", f.Age.Where(float.IsFinite).DefaultIfEmpty(0f).Average())] : []) };
            using var sa = LbmSolver.Start(g, m, w, o); using var sb2 = LbmSolver.Start(g, m, w, o);
            var pa = new AveragedPass(new LbmPassSession(sa), m, w, o, Cfg()); var pb = new AveragedPass(new LbmPassSession(sb2), m, w, o, Cfg());
            void Advance(LbmSolver.LbmSession s, AveragedPass p) { s.Step((int)Math.Clamp(p.StepsToEvent, 1, 100), default); p.Tick(s.Steps * 1e-3); }
            int guard = 0;
            while (guard++ < 400_000 && !(pa.BatchCount >= 3 && pa.Averaging && pa.TryQuiesce())) Advance(sa, pa);
            // the real file path: three checkpoints (only two stay), the newest read back, a flipped byte makes it invalid
            string ckDir = Path.Combine(Path.GetTempPath(), "wf-ultra-selfcheck-" + Guid.NewGuid().ToString("N")), ckHash = "selfcheckhash";
            long sizeBytes = 0; bool fileOk;
            try
            {
                UltraManifest? last = null;
                for (int n = 1; n <= 3; n++) last = UltraCheckpoint.Write(ckDir, n, new UltraManifest { Hash = ckHash, HashInputs = new() { ["k"] = "v" }, Step = sa.Steps, SimSeconds = sa.SimSeconds, WallUsedSeconds = 12.5, Batches = pa.BatchCount, Phase = "averaging", RunId = "selfcheck" }, bw => { sa.WriteState(bw); pa.WriteState(bw); });
                sizeBytes = last!.DataBytes;
                bool kept2 = Directory.GetFiles(ckDir, "ckpt-*.bin").Length == 2 && Directory.GetFiles(ckDir, "*.tmp").Length == 0;
                var found = UltraCheckpoint.FindValid(ckDir, ckHash, out _);
                bool mismatch = UltraCheckpoint.FindValid(ckDir, "otherhash", out string whyHash) == null && whyHash.Contains("different model", StringComparison.Ordinal);
                using (var br = UltraCheckpoint.Open(ckDir, found!, ckHash, out var fsIn)) using (fsIn) { sb2.ReadState(br); pb.ReadState(br, g); }
                using (var fsBad = new FileStream(Path.Combine(ckDir, last.DataFile), FileMode.Open, FileAccess.Write)) { fsBad.Position = sizeBytes / 2; fsBad.WriteByte(0x5A); }
                fileOk = kept2 && found is { Sequence: 3 } && mismatch && UltraCheckpoint.FindValid(ckDir, ckHash, out _) is { Sequence: 2 };   // the corrupt newest is skipped, the older one is still valid
            }
            finally { UltraCheckpoint.Delete(ckDir); }
            long stepAtCkpt = sa.Steps; int batchesAtCkpt = pa.BatchCount;
            long more = 2 * pa.BatchSteps + 37;
            for (long k = 0; k < more; k += 0) { long t = Math.Min(100, Math.Min(more - k, Math.Max(1, Math.Min(pa.StepsToEvent, pb.StepsToEvent)))); sa.Step((int)t, default); pa.Tick(sa.Steps * 1e-3); sb2.Step((int)t, default); pb.Tick(sb2.Steps * 1e-3); k += t; }
            static double Rel(float[] x, float[] y)
            {
                if (x.Length != y.Length) return double.PositiveInfinity;
                double d = 0, mx = 1e-30;
                for (int i = 0; i < x.Length; i++) { if (float.IsNaN(x[i]) && float.IsNaN(y[i])) continue; d = Math.Max(d, Math.Abs((double)x[i] - y[i])); mx = Math.Max(mx, Math.Abs(x[i])); }
                return d / mx;
            }
            double rPop = Rel(sa.PopulationsNow(), sb2.PopulationsNow()), rAge = Rel(sa.AgeFieldNow(), sb2.AgeFieldNow());
            var la = pa.Land(); var lb = pb.Land();
            double rUx = Rel(la.Field.Ux, lb.Field.Ux), rAgeMean = Rel(la.Field.Age, lb.Field.Age), rP = Rel(la.Field.P, lb.Field.P);
            bool same = pa.BatchCount == pb.BatchCount && sa.Steps == sb2.Steps && la.Report.Batches == lb.Report.Batches && la.Kept == lb.Kept;
            double worst = new[] { rPop, rAge, rUx, rAgeMean, rP }.Max();
            Check("Ultra checkpoint round trip: a pass written at a batch boundary and loaded into a fresh session and pass matches the uninterrupted one after more steps (populations, online age, landed batch means within 1e-6 relative); two checkpoints kept, atomic temp rename, hash mismatch and a corrupt file refused",
                fileOk && fileOk && same && worst <= 1e-6 && sa.OnlineAgeOn && pa.BatchCount > batchesAtCkpt,
                $"checkpoint at step {stepAtCkpt} after {batchesAtCkpt} batches ({sizeBytes / 1e6:0.0} MB), compared at step {sa.Steps} ({pa.BatchCount} batches): max relative difference populations {rPop:E2}, age {rAge:E2}, landed Ux {rUx:E2}, P {rP:E2}, age mean {rAgeMean:E2}{(worst == 0 ? " (exactly 0)" : "")}");
        }

        // Ultra checkpoint safety: Delete never throws, the hash leaves the grid out and the manifest carries it, Start over deletes the saved run at once,
        // and a session whose resume failed refuses to write (the good checkpoint is neither overwritten nor pruned).
        {
            string root = Path.Combine(Path.GetTempPath(), "wf-ultra-safety-" + Guid.NewGuid().ToString("N"));
            bool ok = true; string detail = "";
            try
            {
                bool delOk = true;
                string lockDir = Path.Combine(root, "locked"); Directory.CreateDirectory(lockDir);
                string lockFile = Path.Combine(lockDir, "x.bin"); File.WriteAllText(lockFile, "x");
                using (var held = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { UltraCheckpoint.Delete(lockDir); UltraCheckpoint.Delete(Path.Combine(root, "no", "such")); UltraCheckpoint.Delete(""); UltraCheckpoint.Delete("Z:\\no\\such\\folder"); } catch (Exception) { delOk = false; }
                }
                var m = SampleBuilding.Build(); var w = new Vector2(4f, 0f);
                var oa = new RunOptions { Backend = Backend.Cpu, Quality = Quality.Refine, OnlineAge = true, OnlineAgePerRoom = true, TargetCells = 1_000_000, DomainMode = DomainMode.Full, MinDx = 0.025f };
                var ob = new RunOptions { Backend = Backend.Cpu, Quality = Quality.Refine, OnlineAge = true, OnlineAgePerRoom = true, TargetCells = 12_000, DomainMode = DomainMode.Compact };
                bool hashNoGrid = UltraCheckpoint.HashOf(m, w, oa).Hash == UltraCheckpoint.HashOf(m, w, ob).Hash && UltraCheckpoint.HashOf(m, w, oa).Hash != UltraCheckpoint.HashOf(m, new Vector2(3f, 0f), oa).Hash
                                  && UltraCheckpoint.BuildId.Length > 32;
                PassSettings Cfg() => new() { CapSeconds = 1e12, AnalyseInline = true, Probe = _ => 20f, BatchScale = 0.1f, Analyse = (f, fine) => new PassBatch(f, fine, [("r", f.Ux.Average())], []) };
                var g = Voxelizer.Build(m, 12_000, 2, null);
                RunOptions Opts(UltraSettings u) { var o = new RunOptions { Backend = Backend.Cpu, Quality = Quality.Refine, OnlineAge = true, OnlineAgePerRoom = true, Ultra = u }; return o; }
                var u1 = new UltraSettings { CheckpointRoot = root, RunId = "r1" };
                using var s1 = LbmSolver.Start(g, m, w, Opts(u1));
                var p1 = new AveragedPass(new LbmPassSession(s1), m, w, Opts(u1), Cfg());
                var run1 = new UltraRun(g, m, w, Opts(u1), u1, s1, _ => { });
                bool wrote = run1.TryWrite(s1, p1, 5, "selfcheck");
                var saved = UltraCheckpoint.FindValid(run1.Dir, run1.Hash, out _);
                bool manifestGrid = saved != null && saved.Nx == g.Nx && saved.Ny == g.Ny && saved.Nz == g.Nz && saved.Dx == g.Dx && saved.OriginX == g.Origin.X && saved.RunId == "r1" && saved.Cells == g.Count;
                // a failed resume (the rebuilt grid is not the saved one) refuses to write and leaves the good checkpoint alone
                var g2 = Voxelizer.Build(m, 60_000, 2, null);   // a different grid from g (12k cells)
                var u3 = new UltraSettings { CheckpointRoot = root, RunId = "r3", Decision = UltraDecision.Resume };
                using var s3 = LbmSolver.Start(g2, m, w, Opts(u3));
                var p3 = new AveragedPass(new LbmPassSession(s3), m, w, Opts(u3), Cfg());
                var run3 = new UltraRun(g2, m, w, Opts(u3), u3, s3, _ => { });
                int before = Directory.GetFiles(run3.Dir, "ckpt-*.json").Length;
                bool threw = false;
                try { run3.TryResume(s3, p3, g2); } catch (UltraStoppedException) { threw = true; }
                bool writeRefused = threw && g2.Count != g.Count && !run3.Enabled && !run3.TryWrite(s3, p3, 1, "after failed resume") && Directory.GetFiles(run3.Dir, "ckpt-*.json").Length == before && UltraCheckpoint.FindValid(run3.Dir, run3.Hash, out _) != null;
                // Start over deletes the saved run immediately
                var u2 = new UltraSettings { CheckpointRoot = root, RunId = "r2", Decision = UltraDecision.StartOver };
                using var s2 = LbmSolver.Start(g, m, w, Opts(u2));
                var p2 = new AveragedPass(new LbmPassSession(s2), m, w, Opts(u2), Cfg());
                var run2 = new UltraRun(g, m, w, Opts(u2), u2, s2, _ => { });
                bool startOver = run2.TryResume(s2, p2, g) == 0 && !Directory.Exists(run2.Dir);
                // cleanup: an old folder goes, a fresh one and a folder with no checkpoints stay
                string oldDir = Path.Combine(root, "old"), freshDir = Path.Combine(root, "fresh"), otherDir = Path.Combine(root, "other");
                foreach (var d in new[] { oldDir, freshDir, otherDir }) Directory.CreateDirectory(d);
                var mj = System.Text.Json.JsonSerializer.Serialize(new UltraManifest { BuildId = UltraCheckpoint.BuildId });
                File.WriteAllText(Path.Combine(oldDir, "ckpt-1.json"), mj); File.SetLastWriteTimeUtc(Path.Combine(oldDir, "ckpt-1.json"), DateTime.UtcNow.AddDays(-20));
                File.WriteAllText(Path.Combine(freshDir, "ckpt-1.json"), mj); File.WriteAllText(Path.Combine(otherDir, "notes.txt"), "keep");
                UltraCheckpoint.Cleanup(root, _ => { });
                bool cleanup = !Directory.Exists(oldDir) && Directory.Exists(freshDir) && File.Exists(Path.Combine(otherDir, "notes.txt"));
                ok = delOk && hashNoGrid && wrote && manifestGrid && writeRefused && startOver && cleanup;
                detail = $"delete never throws {delOk} · hash ignores grid {hashNoGrid} · wrote {wrote} · manifest grid {manifestGrid} · failed resume refuses to write {writeRefused} · start over deletes {startOver} · old-run cleanup {cleanup}";
            }
            finally { UltraCheckpoint.Delete(root); }
            Check("Ultra checkpoint safety: Delete never throws, the hash leaves the grid out and the manifest restores it, Start over deletes at once, a failed resume refuses to write, old runs are cleaned up", ok, detail);
        }

        lines.Add(failed == 0 ? "ALL SELF CHECKS PASSED" : $"{failed} SELF CHECK(S) FAILED");
        string dir2 = Path.Combine(Options.RepoRoot(), "out");
        Directory.CreateDirectory(dir2);
        File.WriteAllLines(Path.Combine(dir2, "app-selfcheck.txt"), lines);
        foreach (var l in lines) { Console.WriteLine(l); Log.Info("selfcheck " + l); }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The PLAN_V2 4.2 round trip: the sample house as a mesh with its openings as holes
    /// (<see cref="GltfWriter.WithHoles"/>), written as glTF, imported through <see cref="MeshImport"/> exactly as the
    /// dialog would (guessed units, axes and storeys), then <see cref="RoomDetect"/> at Preview cells (1.5 M, about
    /// 255 mm). Every sample opening must be matched by the nearest detected one: centre within one cell on every
    /// axis, width and height within one cell, same kind; and the counts must agree. The table is one line per
    /// expected opening, for the check output and the shot sidecar.</summary>
    public static (bool Ok, string Summary, List<string> Table, float Dx, Vector3 Origin, int Joined, DetectedRooms Detected)
        OpeningRoundTrip(IEnumerable<(Vector3 Min, Vector3 Max)>? extraSolids)
    {
        var sample = SampleBuilding.Build();
        var holed = GltfWriter.WithHoles(sample, extraSolids);
        string path = Path.Combine(Path.GetTempPath(), $"windflow-selfcheck-holes-{System.Environment.ProcessId}.gltf");
        GltfWriter.Write(holed, path);
        var g = MeshImport.Load(path);
        File.Delete(path);
        var settings = new ImportSettings
        {
            MetresPerUnit = MeshImport.Units[MeshImport.GuessUnit(g)].Metres,
            YUpToZUp = MeshImport.DefaultYUp(g.FileName),
        };
        settings.Storeys = MeshImport.GuessStoreys(g.Meshes, settings);
        var built = MeshImport.Build(g, settings);
        // MeshImport drops the model onto z = 0: the sample's ground slab bottom (-0.2 m) moves up by 0.2 m.
        float zShift = -holed.Obstacles.SelectMany(m => m.Vertices).Min(v => v.Z);
        var det = RoomDetect.Detect(built, System.Environment.ProcessorCount - 1);
        float dx = det.Dx;

        var table = new List<string> { $"  {"expected",-26} {"kind",-7} {"centre (m)",-20} {"W x H (m)",-11} | {"detected",-40} {"kind",-7} {"centre (m)",-20} {"W x H (m)",-11} | verdict" };
        var free = det.Openings.ToList();
        int good = 0;
        static string Kind(OpeningKind k) => k == OpeningKind.Opening ? "vent/op" : k.ToString().ToLowerInvariant();
        static string C(Vector3 c) => $"({c.X:0.00}, {c.Y:0.00}, {c.Z:0.00})";
        foreach (var o in sample.Openings)
        {
            var want = o.Center + new Vector3(0, 0, zShift);
            var hit = free.OrderBy(d => Vector3.DistanceSquared(d.Center, want)).FirstOrDefault();
            string verdict;
            if (hit == null) verdict = "MISSING";
            else
            {
                free.Remove(hit);
                var off = Vector3.Abs(hit.Center - want);
                var wrong = new List<string>();
                if (off.X > dx || off.Y > dx || off.Z > dx) wrong.Add($"centre off {off.X:0.00}/{off.Y:0.00}/{off.Z:0.00}");
                // S3-02: sizes are measured between the mesh's reveal planes, so they match the hole to float noise (was: within one cell)
                if (MathF.Abs(hit.Width - o.Width) > 0.02f) wrong.Add("width");
                if (MathF.Abs(hit.Height - o.Height) > 0.02f) wrong.Add("height");
                if (hit.Kind != o.Kind) wrong.Add("kind");
                verdict = wrong.Count == 0 ? "ok" : "WRONG " + string.Join(", ", wrong);
                if (wrong.Count == 0) good++;
            }
            table.Add($"  {o.Name,-26} {Kind(o.Kind),-7} {C(want),-20} {o.Width,4:0.00} x {o.Height,4:0.00} | " +
                      (hit == null ? $"{"-",-40} {"",-7} {"",-20} {"",-11}"
                                   : $"{hit.Name,-40} {Kind(hit.Kind),-7} {C(hit.Center),-20} {hit.Width,4:0.00} x {hit.Height,4:0.00}") +
                      $" | {verdict}");
        }
        foreach (var extra in free)
            table.Add($"  {"(none)",-26} {"",-7} {"",-20} {"",-11} | {extra.Name,-40} {Kind(extra.Kind),-7} {C(extra.Center),-20} " +
                      $"{extra.Width,4:0.00} x {extra.Height,4:0.00} | EXTRA");
        int joined = det.Notes.Where(n => n.Contains("joined across")).Sum(n =>
            int.TryParse(System.Text.RegularExpressions.Regex.Match(n, @"(\d+) joined across").Groups[1].Value, out int j) ? j : 0);
        bool ok = good == sample.Openings.Count && det.Openings.Count == sample.Openings.Count;
        string summary = $"{good}/{sample.Openings.Count} matched, {det.Openings.Count} detected, {det.Rooms.Count} rooms at {dx * 1000:0} mm · " +
                         string.Join(" | ", det.Notes.Where(n => n.Contains("opening") || n.Contains("dropped")));
        return (ok, summary, table, dx, det.Origin, joined, det);
    }

    /// <summary>Three rules over MainWindow.axaml (the source file, found from the repo root) and
    /// <see cref="UiTable"/>: every Expert-only control is hidden in Basic by a table row on it or on a container
    /// around it; nothing interactive shows in Basic unless <see cref="UiTable.BasicControls"/> allows it; every
    /// flow control is disabled while locked, and no view control is. Item templates are skipped (their rows follow
    /// the mode through bindings).</summary>
    static List<(string Name, bool Ok, string Detail)> UiTableChecks()
    {
        var results = new List<(string, bool, string)>();
        string file = Path.Combine(Options.RepoRoot(), "src", "WindFlow.App", "MainWindow.axaml");
        if (!File.Exists(file))
        {
            results.Add(("UiTable: MainWindow.axaml found", false, file));
            return results;
        }
        var doc = System.Xml.Linq.XDocument.Load(file);
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string? Id(System.Xml.Linq.XElement e) =>
            (string?)e.Attribute(x + "Name") ?? (string?)e.Attribute("Content") ?? (string?)e.Attribute("Header");
        var all = doc.Descendants().Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "DataTemplate")).ToList();
        var byId = all.Where(e => Id(e) != null).GroupBy(e => Id(e)!).ToDictionary(g => g.Key, g => g.ToList());
        var expertRows = UiTable.Rules.Where(r => r.ExpertOnly).Select(r => r.Name).ToHashSet();
        var flowRows = UiTable.Rules.Where(r => r.ChangesFlow).Select(r => r.Name).ToHashSet();
        bool Under(System.Xml.Linq.XElement e, HashSet<string> rows) =>
            e.AncestorsAndSelf().Any(a => (string?)a.Attribute(x + "Name") is { } n && rows.Contains(n));

        var missing = UiTable.Rules.Where(r => !byId.TryGetValue(r.Name, out var l) || l.Count != 1).Select(r => r.Name).ToList();
        results.Add(("UiTable: every row names exactly one control in MainWindow.axaml", missing.Count == 0,
            missing.Count == 0 ? $"{UiTable.Rules.Length} rows" : "missing or duplicated: " + string.Join(", ", missing)));

        // Basic: every Expert control hidden (Visible(rule, expert: false) is false for a row on it or around it).
        var shownInBasic = UiTable.ExpertControls.Where(n => !byId.ContainsKey(n) || !Under(byId[n][0], expertRows)).ToList();
        results.Add(("Basic hides every Expert-only control (UiTable)", shownInBasic.Count == 0 &&
                     UiTable.Rules.Where(r => r.ExpertOnly).All(r => !UiTable.Visible(r, expert: false) && UiTable.Visible(r, expert: true)),
            shownInBasic.Count == 0 ? $"{UiTable.ExpertControls.Length} controls hidden: {string.Join(", ", UiTable.ExpertControls)}"
                                    : "visible in Basic or not found: " + string.Join(", ", shownInBasic)));

        string[] interactive = ["Button", "ToggleButton", "TextBox", "Slider", "CheckBox", "ComboBox", "RadioButton", "MenuItem"];
        var extras = all.Where(e => interactive.Contains(e.Name.LocalName) && !Under(e, expertRows))
                        .Select(e => Id(e) ?? $"<unnamed {e.Name.LocalName}>")
                        .Where(id => !UiTable.BasicControls.Contains(id)).Distinct().ToList();
        int basicCount = all.Count(e => interactive.Contains(e.Name.LocalName) && !Under(e, expertRows));
        results.Add(("Basic shows nothing interactive outside UiTable.BasicControls", extras.Count == 0,
            extras.Count == 0 ? $"{basicCount} Basic controls, all listed" : "not allowed in Basic: " + string.Join(", ", extras)));

        // Lock: every flow control disabled, no view control disabled.
        var live = UiTable.FlowControls.Where(n => !byId.ContainsKey(n) || !Under(byId[n][0], flowRows)).ToList();
        var dead = UiTable.ViewControls.Where(n => !byId.ContainsKey(n) || Under(byId[n][0], flowRows)).ToList();
        results.Add(("Lock disables every flow-changing control and no view control (UiTable)",
            live.Count == 0 && dead.Count == 0 &&
            UiTable.Rules.Where(r => r.ChangesFlow).All(r => !UiTable.Enabled(r, locked: true) && UiTable.Enabled(r, locked: false)),
            (live.Count == 0 ? $"{UiTable.FlowControls.Length} flow controls disabled" : "still enabled or not found: " + string.Join(", ", live)) +
            " · " + (dead.Count == 0 ? $"{UiTable.ViewControls.Length} view controls live" : "view controls disabled or not found: " + string.Join(", ", dead))));
        return results;
    }

    /// <summary>RU-01 check: a session that only counts steps and hands back one prebuilt field (a real grid, for the steady age solve).</summary>
    sealed class GridPassSession(FlowField field) : IPassSession
    {
        long steps;
        public int WarmupSteps => 10;
        public long Steps => steps;
        public float TimeStepSeconds => 0.01f;
        public void Step(int n, CancellationToken ct) => steps += n;
        public void ResetAveraging() { }
        public bool StartOnlineAge(float[] frozen, out string note) { note = "fake"; return false; }
        public (FlowField, FlowField?) Pull() => (field, null);
    }

    /// <summary>C1.3c check: a session that only counts steps and hands back a fresh copy of one developed field at every pull (a frozen flow).</summary>
    sealed class FrozenPassSession(FlowField field) : IPassSession
    {
        long steps;
        public int WarmupSteps => 10;
        public long Steps => steps;
        public float TimeStepSeconds => 0.01f;
        public void Step(int n, CancellationToken ct) => steps += n;
        public void ResetAveraging() { }
        public bool StartOnlineAge(float[] frozen, out string note) { note = "fake"; return false; }
        public (FlowField, FlowField?) Pull() => (FlowFieldMean.Clone(field), null);
    }

    /// <summary>6f check: a session that only counts steps and hands back a 2 x 2 x 2 field whose first cell carries the pull's number from <c>series</c>
    /// (pull 0 is the settle-end probe, pull b the end of batch b), so the pass's decisions can be checked on a known series.</summary>
    sealed class FakePassSession(Func<int, double> series) : IPassSession
    {
        long steps; int pulls;
        public int Warmup = 1000, StepSleepMs;   // RU-02: a burst that takes seconds
        public int WarmupSteps => Warmup;
        public long Steps => steps;
        public float TimeStepSeconds => 0.01f;
        public void Step(int n, CancellationToken ct) { if (StepSleepMs > 0) Thread.Sleep(StepSleepMs); steps += n; }
        public void ResetAveraging() { }
        public bool StartOnlineAge(float[] frozen, out string note) { note = "fake"; return false; }
        public (FlowField, FlowField?) Pull()
        {
            var grid = new VoxelGrid { Nx = 2, Ny = 2, Nz = 2, Dx = 1f, Cell = new byte[8] };
            var f = new FlowField { Grid = grid, Ux = new float[8], Uy = new float[8], Uz = new float[8], P = new float[8], AverageSamples = 100 };
            f.Ux[0] = (float)series(pulls++);
            return (f, null);
        }
    }
}
