using System.Numerics;
using WindFlow.Core;
using WindFlow.Report;

namespace WindFlow.App;

public static partial class SelfCheck
{
    /// <summary>Standard lands the online age per room (settled rooms) and the steady age for the rest, marked; the fresh-air label sentence.</summary>
    static void Checks_Age(List<(string Name, bool Ok, string Detail)> feature)
    {
        static RoomConvergence Age(bool converged, int batches = 6, float mean = 400f) => new() { BatchCount = batches, Mean = mean, HalfWidth = 5f, Converged = converged };
        bool pick = AveragedPass.OnlineAgeSettled(Age(true)) && !AveragedPass.OnlineAgeSettled(Age(false)) && !AveragedPass.OnlineAgeSettled(Age(true, batches: 1)) &&
                    !AveragedPass.OnlineAgeSettled(Age(true, mean: 0f)) && !AveragedPass.OnlineAgeSettled(null);
        feature.Add(("AGE-PER-ROOM: Standard lands a room's online age only when that room's age settled (enough batches, a mean, a range); anything else lands the steady age",
            pick, $"settled {AveragedPass.OnlineAgeSettled(Age(true))} · not settled {AveragedPass.OnlineAgeSettled(Age(false))} · 1 batch {AveragedPass.OnlineAgeSettled(Age(true, batches: 1))} · none {AveragedPass.OnlineAgeSettled(null)}"));

        var online = new RoomConvergence { RoomId = "a", Converged = true, FreshHalfWidth = 0.4f };
        var steady = new RoomConvergence { RoomId = "b", Converged = false, AgeSteady = true };
        var rep = new ConvergenceReport { AgeOnline = true, AgePerRoom = true, AgeLabel = "x", Status = new ConvergenceStatus { Rooms = { online, steady } } };
        RoomResult Room(string id) => new() { Room = new Room { Id = id, Name = id }, FreshAch = 5f };
        string note = ResultStatus.AgeNote(rep);
        bool marker = ConvergenceReport.RoomStatus(steady).Contains("steady age", StringComparison.Ordinal) && !ConvergenceReport.RoomStatus(online).Contains("steady", StringComparison.Ordinal) &&
                      ResultStatus.AgeSteadyRoom(rep, "b") && !ResultStatus.AgeSteadyRoom(rep, "a");
        bool range = float.IsFinite(ResultStatus.FreshHalfWidth(rep, Room("a"))) && float.IsNaN(ResultStatus.FreshHalfWidth(rep, Room("b"))) &&
                     ResultStatus.FreshHasRange(rep, "a") && !ResultStatus.FreshHasRange(rep, "b") && !ResultStatus.HasConservative(rep);
        bool notes = note.StartsWith(AveragedPass.AgeStandardNote, StringComparison.Ordinal) && note.Contains(AveragedPass.AgeSteadyNote, StringComparison.Ordinal) && note.Contains(AveragedPass.AgeStandardMixedNote, StringComparison.Ordinal) &&
                     !ResultStatus.AgeNote(new ConvergenceReport { AgeOnline = true, AgePerRoom = true, Status = new ConvergenceStatus { Rooms = { online } } }).Contains(AveragedPass.AgeSteadyNote, StringComparison.Ordinal);
        feature.Add(("AGE-PER-ROOM: a steady-age room carries the 'steady age' marker, no fresh-air ± and no steady-vs-online value; the Standard note names both estimators (steady note only when some room used it)",
            marker && range && notes, $"marker {marker} · range {range} · notes {notes}"));

        // Standard: the online age must not change the stop or a room's Settled status (status as without it). A real pass on the small closed room (CPU), once with the online age
        // per room (Standard) and once without it, with the fixed-batch option path (PassSettings.FixedBatches, not the env var): the pass's batch boundaries do not depend on
        // thread timing, and the stop is the fixed one in both. Identical stop reason, batch count and per-room Settled status; the Standard run carries the per-room age marking.
        {
            var model = ClosedRoomModel(out var dom);
            PassOutcome Pass(bool online)
            {
                var o = new RunOptions { Backend = Backend.Cpu, WindProfileExponent = 0f, AutoConverge = true, TargetCells = 1500, AgeOfAir = true, OnlineAge = online, OnlineAgePerRoom = online, Domain = dom };
                using var session = LbmSolver.Start(Voxelizer.Build(model, 1500, null, dom), model, Vector2.Zero, o, null);
                return AveragedPass.Run(new LbmPassSession(session), model, Vector2.Zero, o, new PassSettings { CapSeconds = 1e9, AnalyseInline = true, FixedBatches = (1, 3), GroupAges = 0 });
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var on = Pass(true); var off = Pass(false);
            var ron = on.Report.Status; var roff = off.Report.Status;
            bool same = ron.Reason == roff.Reason && on.Report.Batches == off.Report.Batches && ron.Rooms.Count == roff.Rooms.Count && ron.Rooms.Count > 0 &&
                        ron.Rooms.All(r => roff.Rooms.Any(x => x.RoomId == r.RoomId && x.Converged == r.Converged));
            feature.Add(("AGE-PER-ROOM: with the online age per room (Standard) a real averaged pass on a closed room stops with the same reason, batch count and per-room Settled status as without the online age",
                same && on.Report.AgePerRoom && !off.Report.AgePerRoom,
                $"on: {ron.Reason}, {on.Report.Batches} batches, settled [{string.Join(",", ron.Rooms.Select(r => r.Converged))}], per-room {on.Report.AgePerRoom} · off: {roff.Reason}, {off.Report.Batches} batches, settled [{string.Join(",", roff.Rooms.Select(r => r.Converged))}] · fixed batches (1,3) · {clock.Elapsed.TotalSeconds:0} s"));

            // High's plain online age keeps the gate (GateByAge); an unsettled age unsettles the room and keeps the pass running.
            var flow = new ConvergenceStatus { Converged = true, Reason = ConvergenceStopReason.Converged, Rooms = { new RoomConvergence { RoomId = "a", Converged = true, Stationary = true } } };
            var unsettledAge = new ConvergenceStatus { Rooms = { new RoomConvergence { RoomId = "a", Converged = false, Stationary = false, BatchCount = 6, Mean = 300f, HalfWidth = 40f, StopHalfWidth = 60f, Tolerance = 15f } } };
            AveragedPass.GateByAge(flow, unsettledAge);
            feature.Add(("AGE-PER-ROOM: High's age gate unsettles a room whose online age has not settled (Standard never calls it)",
                flow.Rooms[0].Converged == false && flow.Reason == ConvergenceStopReason.Running, $"High: room settled {flow.Rooms[0].Converged}, {flow.Reason}"));
        }

        // Landing rules on crafted inputs: no room status at all means every room steady (nothing landed online); a steady room's age never overwrites a cell an online-landed room also covers.
        {
            var anyOnline = new ConvergenceStatus { Rooms = { new RoomConvergence { RoomId = "a" }, new RoomConvergence { RoomId = "b", AgeSteady = true } } };
            var allSteady = new ConvergenceStatus { Rooms = { new RoomConvergence { RoomId = "a", AgeSteady = true } } };
            bool empty = !AveragedPass.AnyOnlineRoom(null) && !AveragedPass.AnyOnlineRoom(new ConvergenceStatus()) && !AveragedPass.AnyOnlineRoom(allSteady) && AveragedPass.AnyOnlineRoom(anyOnline);
            feature.Add(("AGE-PER-ROOM: under Standard an empty or missing room status means every room steady, one online room is enough to land the online age", empty,
                $"null {AveragedPass.AnyOnlineRoom(null)} · empty {AveragedPass.AnyOnlineRoom(new ConvergenceStatus())} · all steady {AveragedPass.AnyOnlineRoom(allSteady)} · one online {AveragedPass.AnyOnlineRoom(anyOnline)}"));

            // cells 0..4: online room A covers 0,1; steady room B covers 1,2,3 (cell 3's steady value is NaN); cell 4 is in neither
            var merged = new float[] { 1, 1, 1, 1, 1 };
            AveragedPass.MergeSteadyRooms(merged, [9, 9, 9, float.NaN, 9], [(false, [true, true, false, false, false]), (true, [false, true, true, true, false])]);
            feature.Add(("AGE-PER-ROOM: a steady room's age overwrites only its own cells that no online-landed room also covers (overlap keeps the online age; NaN steady keeps it too)",
                merged.SequenceEqual(new float[] { 1, 1, 9, 1, 1 }), $"merged [{string.Join(",", merged)}] want [1,1,9,1,1]"));
        }

        // A steady-landed room keeps its raw online value for the comparison column (landed value untouched); sealed / no-cell rooms stay out of the "N of M rooms" count.
        {
            var model = ClosedRoomModel(out var dom);
            var grid = Voxelizer.Build(model, 1500, null, dom);
            int n = grid.Count;
            var landed = new float[n]; var raw = new float[n]; Array.Fill(landed, 600f); Array.Fill(raw, 900f);
            var res = Analysis.Analyze(model, new FlowField { Grid = grid, Ux = new float[n], Uy = new float[n], Uz = new float[n], P = new float[n] }, Vector2.Zero, new RunOptions());
            res.Scalars = new ScalarFields { Age = landed, OnlineAge = raw };
            Analysis.Finish(res);
            var rr = res.Rooms.First();
            bool rawOk = MathF.Abs(rr.VolumeMeanAge - 600f) < 0.5f && MathF.Abs(rr.OnlineAgeRaw - 900f) < 0.5f && MathF.Abs(rr.OnlineMeanAgeRaw - 900f) < 0.5f;
            var rooms = new List<RoomConvergence> { new() { RoomId = "a" }, new() { RoomId = "b", AgeSteady = true }, new() { RoomId = "sealed" } };
            var sealedSeries = new RoomConvergence { RoomId = "sealed", BatchCount = 8, Mean = ScalarSolver.AgeCapSeconds };
            var ids = new[] { sealedSeries, Age(true) }.Where(AveragedPass.AgeSeriesSealed).Select(x => x.RoomId).ToHashSet();
            var (nOn, nAll) = AveragedPass.OnlineRoomCount(rooms, ids);
            bool count = nOn == 1 && nAll == 2 && AveragedPass.OnlineRoomCount(rooms, new HashSet<string>()) == (2, 3);
            feature.Add(("AGE-PER-ROOM: a steady-age room reports its raw online volume and occupied-zone age beside the landed value; sealed rooms are left out of the \"N of M rooms\" count",
                rawOk && count, $"landed {rr.VolumeMeanAge:0.0} s · raw online {rr.OnlineAgeRaw:0.0} s / occupied {rr.OnlineMeanAgeRaw:0.0} s (want 600 / 900 / 900) · count {nOn} of {nAll} (want 1 of 2)"));
        }

        string m = AveragedPass.FreshAchMeaning;
        feature.Add(("AGE label: fresh-air ACH = 3600 / mean age equals an air-change rate only when fully mixed (mean-age equivalent otherwise, with citations)",
            m.Contains("fully mixed", StringComparison.Ordinal) && m.Contains("mean-age equivalent", StringComparison.Ordinal) && m.Contains("Etheridge & Sandberg 1996", StringComparison.Ordinal) && m.Contains("Hurraß 2026", StringComparison.Ordinal), m));
    }

    /// <summary>The 6k check's small closed room (3 x 3 x 2.4 m, one supply, one return, one room "room") and the domain box around it.</summary>
    static BuildingModel ClosedRoomModel(out DomainBox domain)
    {
        const float L = 3f, H = 2.4f, T = 0.2f;
        var m = new BuildingModel { Name = "age-room", GroundZ = 0, Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = H, Analysed = true } } };
        foreach (var (a, b) in new[] { (new Vector3(-T, -T, -T), new Vector3(L + T, L + T, 0)), (new Vector3(-T, -T, H), new Vector3(L + T, L + T, H + T)),
            (new Vector3(-T, -T, 0), new Vector3(0, L + T, H)), (new Vector3(L, -T, 0), new Vector3(L + T, L + T, H)),
            (new Vector3(0, -T, 0), new Vector3(L, 0, H)), (new Vector3(0, L, 0), new Vector3(L, L + T, H)) })
            m.Obstacles.Add(SampleBuilding.Box(a, b));
        m.Rooms.Add(new Room { Id = "room", Name = "Room", Level = "L1", ZMin = 0, ZMax = H,
            Loops = { new[] { new Vector2(0.05f, 0.05f), new Vector2(L - 0.05f, 0.05f), new Vector2(L - 0.05f, L - 0.05f), new Vector2(0.05f, L - 0.05f) } } });
        m.Sources.Add(new AirSource { Id = "sup", Name = "Supply", Level = "L1", Kind = SourceKind.Supply, Center = new Vector3(0.75f, 0.75f, H - 0.1f), Direction = new Vector3(0, 0, -1), Width = 0.5f, Height = 0.5f, FlowRate = 0.2f });
        m.Sources.Add(new AirSource { Id = "ret", Name = "Return", Level = "L1", Kind = SourceKind.Return, Center = new Vector3(2.25f, 2.25f, H - 0.1f), Direction = new Vector3(0, 0, 1), Width = 0.5f, Height = 0.5f, FlowRate = 0.2f });
        domain = new DomainBox(new Vector3(-T, -T, -T), new Vector3(L + T, L + T, H + T));
        return m;
    }
}
