using System.Linq;
using System.Numerics;

namespace WindFlow.Core;

public static partial class Analysis
{
    public const float StagnantSpeed = 0.1f;   // m/s

    public static void Finish(SimulationResult r)
    {
        var field = r.Field;
        var g = field.Grid;
        int n = g.Count;
        bool hasT = field.T.Length == n;
        bool hasCo2 = r.Scalars.Co2.Length == n;
        bool hasAge = r.Scalars.Age.Length == n;
        bool hasRaw = hasAge && r.Scalars.OnlineAge.Length == n;
        bool hasComfort = r.Comfort.Pmv.Length == n;
        bool[]? connection = null;
        // OF-1: every room-cell average below (occupied zone, comfort, age) is over the room's outline plus its band (RoomMasks)
        var masks = r.Model.Rooms.Count > 0 && r.Rooms.Count > 0 ? RoomMasks(g, r.Model, out _) : [];

        foreach (var rr in r.Rooms)
        {
            var room = rr.Room;
            int rIdx = r.Model.Rooms.IndexOf(room);
            var merged = rIdx >= 0 && rIdx < masks.Length ? masks[rIdx] : RoomMask(g, room, out _);
            float zLo = room.ZMin + 0.1f, zHi = room.ZMin + 1.8f;

            double tSum = 0, co2Sum = 0, ageSum = 0, rawSum = 0;
            int tCount = 0, co2Count = 0, ageCount = 0, rawCount = 0, comfortTotal = 0, comfortCount = 0, draughtCount = 0;

            for (int z = 0; z < g.Nz; z++)
            {
                float cz = g.Origin.Z + (z + 0.5f) * g.Dx;
                if (cz < zLo || cz > zHi) continue;
                for (int y = 0; y < g.Ny; y++)
                for (int x = 0; x < g.Nx; x++)
                {
                    int idx = g.Index(x, y, z);
                    if (g.Cell[idx] != VoxelGrid.Air) continue;   // occupied-zone stats: air cells only, not Fixed
                    var c = g.CellCenter(x, y, z);
                    if (!merged[idx] && !InsideLoops(room.Loops, new Vector2(c.X, c.Y))) continue;

                    if (hasT) { tSum += field.T[idx]; tCount++; }
                    if (hasCo2 && !float.IsNaN(r.Scalars.Co2[idx])) { co2Sum += r.Scalars.Co2[idx]; co2Count++; }
                    if (hasAge && !float.IsNaN(r.Scalars.Age[idx])) { ageSum += r.Scalars.Age[idx]; ageCount++; }
                    if (hasRaw && !float.IsNaN(r.Scalars.OnlineAge[idx])) { rawSum += r.Scalars.OnlineAge[idx]; rawCount++; }
                    if (hasComfort)
                    {
                        comfortTotal++;
                        if (MathF.Abs(r.Comfort.Pmv[idx]) <= 0.5f) comfortCount++;
                        if (r.Comfort.Dr[idx] > 20f) draughtCount++;
                    }
                }
            }

            if (tCount > 0) rr.MeanTemperature = (float)(tSum / tCount);
            var hold = field.RoomHolds.FirstOrDefault(h => ReferenceEquals(h.Room, room) || (room.Id.Length > 0 && h.Room.Id == room.Id));
            if (hold != null) rr.HoldDutyWatts = (float)hold.DutyWatts;
            string roomName = room.Name.Length > 0 ? room.Name : room.Id, heldTag = $"{roomName}: room air held at ";
            g.Warnings.RemoveAll(w => w.StartsWith(heldTag, StringComparison.Ordinal));
            if (hold != null && tCount > 0 && MathF.Abs(rr.MeanTemperature - hold.SetPoint) > 1f)   // S2: the hold is on the volume mean
                g.Warnings.Add($"{heldTag}{hold.SetPoint:0.#} °C on average; occupied zone {rr.MeanTemperature:0.#} °C (stratification)");
            if (co2Count > 0) rr.MeanCo2 = (float)(co2Sum / co2Count);
            if (ageCount > 0) rr.MeanAge = (float)(ageSum / ageCount);      // occupied zone: the "Age (occupied zone)" column
            if (rawCount > 0) rr.OnlineMeanAgeRaw = (float)(rawSum / rawCount);
            if (hasAge)
            {
                // RV-03: the room-VOLUME mean age (all heights, air cells), which is what D1 and ISO 16000-8 define; the occupied-zone mean
                // above is a different number (a displacement room reads younger at 0.1-1.8 m) and would put eps_a above 1.
                var roomMask = merged;
                (double sum, double cappedSum, long count, long capped) Tally(float[] age)
                {
                    double s = 0, cs = 0; long c = 0, cp = 0;
                    for (int i = 0; i < roomMask.Length; i++)
                        if (roomMask[i] && g.Cell[i] == VoxelGrid.Air && !float.IsNaN(age[i]))
                        {
                            s += age[i]; c++;
                            if (age[i] >= ScalarSolver.AgeCapSeconds * 0.999f) { cp++; cs += age[i]; }
                        }
                    return (s, cs, c, cp);
                }
                var (vSum, cappedSum, vCount, capped) = Tally(r.Scalars.Age);
                if (hasRaw)
                {
                    var (rs, rcs, rc, rcp) = Tally(r.Scalars.OnlineAge);   // same capped-cell rule as the landed value
                    if (DropCapped(rcp, rc)) { rs -= rcs; rc -= rcp; }
                    if (rc > 0) rr.OnlineAgeRaw = (float)(rs / rc);
                }
                // RA-05: cells at the age cap are stagnant pockets, not air that was ever ventilated; up to 1 % of the room they are left out of the room-volume mean (one capped pocket would drag eps_a and the fresh-air ACH down),
                // above that they stay in (a real stagnant zone is part of the room's air) and the note says so. Disclosed from 0.1 % of the room. A room entirely at the cap is sealed (AgeSealed): nothing is left out, it reads as stagnant.
                string stagnantTail = " of the room is stagnant (age at the 24 h cap) and is left out of the mean", stagnantTailKept = " of the room is stagnant (age at the 24 h cap)";
                g.Warnings.RemoveAll(w => w.StartsWith(roomName + ": ", StringComparison.Ordinal) && (w.EndsWith(stagnantTail, StringComparison.Ordinal) || w.EndsWith(stagnantTailKept, StringComparison.Ordinal)));
                bool dropCapped = DropCapped(capped, vCount);
                if (vCount > 0 && capped < vCount && capped > 0.001 * vCount)
                    g.Warnings.Add($"{roomName}: {100.0 * capped / vCount:0.#} %{(dropCapped ? stagnantTail : stagnantTailKept)}");
                if (dropCapped) { vSum -= cappedSum; vCount -= capped; }
                if (vCount > 0)
                {
                    rr.VolumeMeanAge = (float)(vSum / vCount);
                    // D1: fresh-air ACH = 3600 / room-volume mean age of air (Sandberg 1981; ISO 16000-8). Volume-independent.
                    rr.FreshAch = 3600f / MathF.Max(rr.VolumeMeanAge, 1e-6f);
                    if (r.Scalars.SteadyAge.Length == n)   // AGE-HIGH: the conservative estimate, the steady age on the same cells
                    {
                        double cSum = 0, cCapSum = 0; long cCount = 0, cCap = 0;
                        for (int i = 0; i < roomMask.Length; i++)
                            if (roomMask[i] && g.Cell[i] == VoxelGrid.Air && !float.IsNaN(r.Scalars.SteadyAge[i]))
                            {
                                cSum += r.Scalars.SteadyAge[i]; cCount++;
                                if (r.Scalars.SteadyAge[i] >= ScalarSolver.AgeCapSeconds * 0.999f) { cCap++; cCapSum += r.Scalars.SteadyAge[i]; }
                            }
                        if (DropCapped(cCap, cCount)) { cSum -= cCapSum; cCount -= cCap; }   // RA-05 (same rule as the mean age above)
                        if (cCount > 0) rr.ConservativeFreshAch = 3600f / MathF.Max((float)(cSum / cCount), 1e-6f);
                    }
                    // D4: air-change efficiency = exhaust age / (2 x room-volume mean age), 0.5 for a fully mixed room (ISO 16000-8; Sandberg &
                    // Sjoeberg 1983). Only for a room fed mainly (>= 90 %) straight from outside: air that arrives through another
                    // room already carries that room's age, and the ratio then measures the upstream room, not this one. The share is of ALL
                    // air entering (RV-08: plain-air faces too), and plain-air exits are exits in tau_e.
                    float entering = rr.Inflow + rr.InternalExchange;
                    if (entering > 1e-6f && rr.OutdoorInflow >= 0.9f * entering && !rr.AgeSealed)
                    {
                        connection ??= ConnectionCells(g, r.Model);
                        float tauE = ExhaustAge(g, field, roomMask, connection, r.Scalars.Age, ReturnPatchExhaust(r, g, roomMask));
                        if (!float.IsNaN(tauE)) rr.AirChangeEffectiveness = tauE / (2f * MathF.Max(rr.VolumeMeanAge, 1e-6f));
                    }
                }
            }
            if (comfortTotal > 0)
            {
                rr.ComfortFraction = (float)comfortCount / comfortTotal;
                rr.DraughtFraction = (float)draughtCount / comfortTotal;
            }
        }

        // ---- mass ledger: EXTERIOR openings only (see IsExteriorOpening) ----
        var ledger = new MassLedger();
        double inflow = 0, outflow = 0;
        var openFlows = new List<float>();
        int exteriorOpenings = 0, interiorOpenings = 0;
        foreach (var of in r.Openings)
        {
            if (!of.Opening.IsOpen) continue;
            if (!IsExteriorOpening(r, of.Opening, g.Dx)) { interiorOpenings++; continue; }
            exteriorOpenings++;
            openFlows.Add(of.FlowRate);
            // FlowRate is along +Normal, and Normal points outside when known, so inflow = max(0, -FlowRate).
            inflow += Math.Max(0, -of.FlowRate);
            outflow += Math.Max(0, of.FlowRate);
        }
        int devicesInLedger = 0, devicesViaOpening = 0;
        foreach (var sf in r.Sources)
        {
            if (!sf.Source.Enabled) continue;
            // Fan: recirculates room air, contributes to neither. That covers a ceiling fan and both
            // halves of a split AC / cassette, whose supply and return move the same air round the room.
            if (sf.Source.Kind == SourceKind.Fan) continue;

            // A body-force device creates no air: whatever it pushes across the envelope goes through the
            // opening it sits in, and that opening is already in the balance above. Counting the device as
            // well would double it (measured: an extract in a duct read 0.16 m3/s at the opening AND
            // 0.17 m3/s at the device, turning a 1% residual into 51%). Only a device with no opening to
            // account for it -- one discharging straight through the domain boundary -- is added here.
            if (sf.Source.Device != DeviceKind.Patch)
            {
                if (DeviceFlowCountedByOpening(r, g, sf.Source)) { devicesViaOpening++; continue; }
                devicesInLedger++;
            }

            if (sf.Source.Kind == SourceKind.Supply) inflow += MathF.Abs(sf.FlowRate);
            else outflow += MathF.Abs(sf.FlowRate);   // Return: an extract grille or an extract fan
        }

        ledger.In = (float)inflow;
        ledger.Out = (float)outflow;
        // P15-07: building fresh-air rate from the age of air, not the exterior-opening ledger (which rectifies net flow per opening, so a
        // single-sided building reads ~0). NaN while the age is not solved.
        ledger.FreshAch = float.NaN; ledger.SealedVolume = 0f; ledger.SealedRooms = new List<string>();
        if (hasAge) { ledger.FreshAch = BuildingFreshAch(g, r.Rooms.Select(rr => rr.Room), r.Scalars.Age, out float sealedVol, out var sealedNames, room => { int k = r.Model.Rooms.IndexOf(room); return k >= 0 && k < masks.Length ? masks[k] : RoomMask(g, room, out _); }); ledger.SealedVolume = sealedVol; ledger.SealedRooms = sealedNames; }
        ledger.ConservativeFreshAch = float.NaN;
        if (hasAge && r.Scalars.SteadyAge.Length == n) ledger.ConservativeFreshAch = BuildingFreshAch(g, r.Rooms.Select(rr => rr.Room), r.Scalars.SteadyAge, out _, out _, room => { int k = r.Model.Rooms.IndexOf(room); return k >= 0 && k < masks.Length ? masks[k] : RoomMask(g, room, out _); });   // AGE-HIGH
        ledger.Residual = (ledger.In < 1e-6f && ledger.Out < 1e-6f)
            ? 0f
            : MathF.Abs(ledger.In - ledger.Out) / MathF.Max(ledger.In, ledger.Out);

        bool hasNaN = false;
        for (int i = 0; i < n; i++)
        {
            if (g.Cell[i] == VoxelGrid.Solid) continue;
            // P17-06: any non-finite value (not only NaN) and the temperature too.
            if (!float.IsFinite(field.Ux[i]) || !float.IsFinite(field.Uy[i]) || !float.IsFinite(field.Uz[i]) || !float.IsFinite(field.P[i])
                || (field.T.Length == n && !float.IsFinite(field.T[i])))
            { hasNaN = true; break; }
        }
        bool divergedWarning = g.Warnings.Any(w => w.Contains("diverged", StringComparison.OrdinalIgnoreCase));

        ledger.IsPhysical = ledger.Residual <= 0.10f && !hasNaN && !divergedWarning;

        // Per-room boundary balance: Inflow/Outflow are gross EXCHANGE rates through the room's connections, so a
        // reversing room shows both > 0 without any fresh-air supply. What must hold is the NET: Inflow - Outflow
        // is ~0 unless the room has a source/sink or the average is unconverged. Reported, not gated.
        foreach (var rr in r.Rooms)
        {
            float big = MathF.Max(rr.Inflow, rr.Outflow);
            if (big < 1e-6f) continue;
            float res = MathF.Abs(rr.NetFlux) / big;
            ledger.WorstRoomResidual = MathF.Max(ledger.WorstRoomResidual, res);
            if (res > 0.10f && MathF.Abs(rr.NetFlux) > 1e-3f)
                ledger.Notes.Add($"Room {(rr.Room.Name.Length > 0 ? rr.Room.Name : rr.Room.Id)}: exchange in {rr.Inflow:0.###}, out {rr.Outflow:0.###}, " +
                    $"net {rr.NetFlux:+0.###;-0.###} m3/s ({res * 100:0}% of the larger); in/out is gross exchange, not fresh-air supply");
        }

        ledger.Notes.Add($"Inflow {ledger.In:0.##} m3/s, outflow {ledger.Out:0.##} m3/s ({ledger.Residual * 100:0}% residual)");
        ledger.Notes.Add($"{exteriorOpenings} exterior opening(s) in the balance, {interiorOpenings} interior opening(s) excluded");
        if (devicesInLedger > 0 || devicesViaOpening > 0)
            ledger.Notes.Add($"{devicesInLedger} device(s) in the balance, {devicesViaOpening} already counted at the opening they sit in");
        // What a device actually achieved, where the grid could not give it what it was asked for. This is
        // the ratio the voxelizer can only warn about in advance (too few cells across a fan disc, too
        // little clearance above it, an outlet in a pocket of still air).
        foreach (var d in field.Devices)
        {
            if (d.SetFlowRate <= 1e-6f) continue;
            float ratio = MathF.Abs(d.FlowRate) / d.SetFlowRate;
            if (ratio < 0.75f)
                ledger.Notes.Add($"Device {d.SourceName} moved {ratio * 100:0}% of its set flow " +
                    $"({MathF.Abs(d.FlowRate):0.###} of {d.SetFlowRate:0.###} m3/s): too coarse a grid, or too little room around it");
        }
        // A real cross-ventilated building always has some openings inflowing and some outflowing; if every
        // open opening's FlowRate came back the same sign, the +Normal convention above can't be trusted to
        // separate in from out (it still used |FlowRate| split by the velocity component's own sign, just
        // flag that the result may not reflect true in/out).
        if (openFlows.Count > 1 && (openFlows.TrueForAll(f => f >= 0) || openFlows.TrueForAll(f => f <= 0)))
            ledger.Notes.Add("Opening Normal orientation could not be verified; in/out split from velocity sign only.");
        foreach (var w in g.Warnings) ledger.Notes.Add(w);

        r.Ledger = ledger;
    }

    /// <summary>Outflow-weighted mean age of the air leaving a room: over every face with air going out (connection faces and plain-air faces
    /// alike), the age of the room cell behind it weighted by the VOLUMETRIC flow, m3/s (node velocity x Dx^2; RV-02: the same unit as
    /// the return grilles' flow). NaN when nothing leaves or no age is finite.</summary>
    internal static float ExhaustAge(VoxelGrid g, FlowField field, bool[] mask, bool[] connection, float[] age, (double Flow, double Age)? returnPatches = null)
    {
        double w = 0, wa = 0;
        if (returnPatches is { } rp && rp.Flow > 0) { w += rp.Flow; wa += rp.Flow * rp.Age; }
        void Face(int x, int y, int z, int dx, int dy, int dz)
        {
            int nIdx = FaceNeighbor(g, mask, x, y, z, dx, dy, dz);
            if (nIdx == -2) return;
            int idx = g.Index(x, y, z);
            if (float.IsNaN(age[idx])) return;
            float[] u = dx != 0 ? field.Ux : dy != 0 ? field.Uy : field.Uz;
            float avg = nIdx < 0 ? u[idx] : 0.5f * (u[idx] + u[nIdx]);
            float outward = (dx + dy + dz) > 0 ? avg : -avg;
            if (outward <= 0) return;
            double q = (double)outward * g.Dx * g.Dx;
            w += q; wa += q * age[idx];
        }
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            if (!mask[g.Index(x, y, z)]) continue;
            Face(x, y, z, -1, 0, 0); Face(x, y, z, 1, 0, 0); Face(x, y, z, 0, -1, 0);
            Face(x, y, z, 0, 1, 0); Face(x, y, z, 0, 0, -1); Face(x, y, z, 0, 0, 1);
        }
        return w > 0 ? (float)(wa / w) : float.NaN;
    }

    /// <summary>Building fresh-air rate, air changes per hour = 3600 / the volume-weighted mean age over every air cell of every room
    /// (full height, each cell once). A room entirely at the solver cap has no air path (<see cref="RoomResult.AgeSealed"/>) and is left out of
    /// the mean, as the age it carries is a ceiling, not an age; a building with no other room reads 3600 / cap. IMP-C: capped cells in any other room are stale pockets and stay in. RW-03: leaving them out
    /// raises the rate, so the sealed volume and rooms are returned too, every label that quotes the rate names them, and the opening
    /// sweep ranks by the sealed volume first (<see cref="OpeningSweep.Rank"/>).</summary>
    public static float BuildingFreshAch(VoxelGrid g, BuildingModel model, float[] age) => BuildingFreshAch(g, model, age, out _, out _);

    /// <summary>IMP-B (OF-1 consistency): the public rate takes the model so it counts every room's cells the way <see cref="Finish"/> does, outline plus band (<see cref="RoomMasks"/>).</summary>
    public static float BuildingFreshAch(VoxelGrid g, BuildingModel model, float[] age, out float sealedVolume, out List<string> sealedRooms)
    {
        var masks = model.Rooms.Count > 0 ? RoomMasks(g, model, out _) : [];
        return BuildingFreshAch(g, model.Rooms, age, out sealedVolume, out sealedRooms, room => { int k = model.Rooms.IndexOf(room); return k >= 0 && k < masks.Length ? masks[k] : RoomMask(g, room, out _); });
    }

    /// <summary>RA-05: the one rule for capped (24 h) cells in a room's mean age: left out when they are a minority of at most 1 % of the room's air cells, kept above that. A room entirely at the cap is sealed (not dropped here).</summary>
    internal static bool DropCapped(long capped, long cells) => cells > 0 && capped > 0 && capped < cells && capped <= 0.01 * cells;

    /// <param name="maskOf">OF-1: a room's cells (<see cref="RoomMasks"/> outline plus band).</param>
    internal static float BuildingFreshAch(VoxelGrid g, IEnumerable<Room> rooms, float[] age, out float sealedVolume, out List<string> sealedRooms, Func<Room, bool[]> maskOf)
    {
        var seen = new bool[g.Count];
        double sum = 0; long count = 0, sealedCells = 0;
        sealedRooms = new List<string>();
        foreach (var room in rooms)
        {
            var mask = maskOf(room);
            long roomCells = 0, roomSealed = 0; double roomSum = 0, roomCappedSum = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || seen[i]) continue;
                seen[i] = true;
                float a = age[i];
                if (g.Cell[i] != VoxelGrid.Air || float.IsNaN(a)) continue;
                roomCells++;
                if (a >= ScalarSolver.AgeCapSeconds * 0.999f) { roomSealed++; roomCappedSum += a; }
                roomSum += a;
            }
            // IMP-C (age-cap rule, the same as RoomResult.AgeSealed): a room entirely at the cap is sealed and left out; RA-05 (<see cref="DropCapped"/>): in any other room the capped cells
            // are left out of the mean up to 1 % of its volume, above that they count
            if (roomCells > 0 && roomSealed == roomCells) { sealedCells += roomSealed; sealedRooms.Add(room.Name); }
            else if (DropCapped(roomSealed, roomCells)) { sum += roomSum - roomCappedSum; count += roomCells - roomSealed; }
            else { sum += roomSum; count += roomCells; }
        }
        sealedVolume = (float)(sealedCells * (double)g.Dx * g.Dx * g.Dx);
        return count > 0 ? 3600f / MathF.Max((float)(sum / count), 1e-6f) : 3600f / ScalarSolver.AgeCapSeconds;
    }

    /// <summary>What the building fresh-air rate averages over, for a label (RW-03): "all indoor volume", or, when rooms are sealed, the
    /// volume without them, naming them.</summary>
    public static string VolumeText(float sealedVolume, IReadOnlyList<string> sealedRooms) =>
        sealedVolume <= 0f ? "all indoor volume"
        : sealedRooms.Count == 0 ? $"the indoor volume without {sealedVolume:0.#} m³ of air with no air path"   // RX-09c: no room is named (a capped pocket inside a ventilated room)
        : $"the indoor volume without the sealed {(sealedRooms.Count == 1 ? "room" : "rooms")} {string.Join(", ", sealedRooms)} ({sealedVolume:0.#} m³ at the 24 h age cap)";

    /// <summary>The extract grilles (Return patches) inside a room: their summed measured flow and the flow-weighted mean age of the air at
    /// their cells. They take air out from inside the room, so no boundary face of the room sees it. Null when the room has none.</summary>
    static (double Flow, double Age)? ReturnPatchExhaust(SimulationResult r, VoxelGrid g, bool[] mask)
    {
        double flow = 0, flowAge = 0;
        foreach (var sf in r.Sources)
        {
            var s = sf.Source;
            if (!s.Enabled || s.Device != DeviceKind.Patch || s.Kind != SourceKind.Return || s.FlowRate <= 0) continue;
            var cells = Voxelizer.SourceCells(g, s, c => c == VoxelGrid.Fixed);
            if (cells.Count == 0 || cells.Count(i => mask[i]) * 2 < cells.Count) continue;   // not mostly in this room
            double sum = 0; int k = 0;
            foreach (int i in cells) if (!float.IsNaN(r.Scalars.Age[i])) { sum += r.Scalars.Age[i]; k++; }
            if (k == 0) continue;
            double q = MathF.Abs(sf.FlowRate);
            flow += q; flowAge += q * sum / k;
        }
        return flow > 0 ? (flow, flowAge / flow) : null;
    }

    // Only an opening that connects indoor air to the outdoors belongs in the mass balance. An interior door
    // between two analysed rooms carries the same air twice -- once as "inflow" into the downstream room and
    // once as "outflow" from the upstream one -- and both entries land on the building-wide balance, which
    // inflates In and Out and, because the two sides never match exactly, the residual with them (the sample
    // house read 41.6% with interior doors counted against 13% for exterior openings alone). Interior openings
    // stay in r.Openings with their flow; they are only left out of the ledger.
    //
    // Classified geometrically: sample one point on each side of the opening centre along +-Normal, offset far
    // enough to clear the opening's own depth and one cell either way, and test each against the analysed
    // rooms' floor loops. Exactly one side indoor = exterior. Both sides indoor = interior. Neither side
    // indoor (an opening onto an unmodelled part of the building, or a room loop that stops short of the
    // wall) is treated as exterior, as is an opening with no usable Normal to sample along -- in both cases
    // there is nothing better to go on, and that is the behaviour every opening had before.
    static bool IsExteriorOpening(SimulationResult r, Opening o, float dx) => IsExteriorOpening(r.Model, o, dx);

    internal static bool IsExteriorOpening(BuildingModel model, Opening o, float dx)
    {
        // IMP-C: a detected opening carries what each side faces; indoor air in no room (a hall, a stair void) is indoor, not outdoors
        if (o.SideA != OpeningSide.Unknown && o.SideB != OpeningSide.Unknown) return o.SideA == OpeningSide.Outdoors || o.SideB == OpeningSide.Outdoors;
        var nrm = o.Normal;
        float len = nrm.Length();
        if (len < 1e-6f) return true;
        nrm /= len;
        float offset = o.Depth * 0.5f + 1.5f * dx;
        return !(IsIndoorPoint(model, o.Center + nrm * offset) && IsIndoorPoint(model, o.Center - nrm * offset));
    }

    /// <summary>Does an open exterior opening already carry this device's flow? True when any cell the
    /// device drives lies inside that opening's rough box, grown by one cell (the carve itself reaches
    /// 0.6 Dx past the wall face, and a device is normally placed flush in the hole). Used only to keep the
    /// mass ledger from counting the same air twice.</summary>
    static bool DeviceFlowCountedByOpening(SimulationResult r, VoxelGrid g, AirSource s)
    {
        var region = g.Devices.Find(d => d.SourceIndex >= 0 && r.Model.Sources.IndexOf(s) == d.SourceIndex)
                  ?? g.Devices.Find(d => d.SourceId == s.Id);
        if (region == null || region.Cells.Length == 0) return false;

        foreach (var of in r.Openings)
        {
            var o = of.Opening;
            if (!o.IsOpen || !IsExteriorOpening(r, o, g.Dx)) continue;
            var n0 = o.Normal; n0.Z = 0;
            if (n0.LengthSquared() < 1e-8f) continue;
            var n = Vector3.Normalize(n0);
            var t = new Vector3(-n.Y, n.X, 0);
            float halfW = MathF.Max(o.Width, g.Dx) * 0.5f + g.Dx;
            float halfH = MathF.Max(o.Height, g.Dx) * 0.5f + g.Dx;
            float halfD = o.Depth * 0.5f + 2f * g.Dx;
            foreach (int idx in region.Cells)
            {
                int x = idx % g.Nx, y = idx / g.Nx % g.Ny, z = idx / (g.Nx * g.Ny);
                var c = g.CellCenter(x, y, z) - o.Center;
                if (MathF.Abs(Vector3.Dot(c, t)) <= halfW && MathF.Abs(c.Z) <= halfH && MathF.Abs(Vector3.Dot(c, n)) <= halfD)
                    return true;
            }
        }
        return false;
    }

    // Inside an analysed storey's room, by the room's own floor loops and z-range. Rooms on storeys that were
    // not analysed are ignored (their air is not solved, so an opening onto them is an outlet as far as this
    // balance is concerned); when no storey is marked Analysed at all, every room counts.
    static bool IsIndoorPoint(BuildingModel model, Vector3 p)
    {
        bool anyAnalysed = model.Storeys.Any(s => s.Analysed);
        var p2 = new Vector2(p.X, p.Y);
        foreach (var rm in model.Rooms)
        {
            if (anyAnalysed && model.StoreyOf(rm) is not { Analysed: true }) continue;
            if (p.Z < rm.ZMin || p.Z > rm.ZMax) continue;
            if (InsideLoops(rm.Loops, p2)) return true;
        }
        return false;
    }

    public static ProbeValues Probe(SimulationResult r, Vector3 p)
    {
        var field = r.Field;
        var g = field.Grid;

        var velocity = SampleVelocity(field, p);
        float speed = velocity.Length();
        float pressure = SamplePressure(field, p);
        float temperature = SampleTrilinearOrNaN(field, field.T, p);

        int idx = NearestIndex(g, p);
        float co2 = r.Scalars.Co2.Length == g.Count ? r.Scalars.Co2[idx] : float.NaN;
        float age = r.Scalars.Age.Length == g.Count ? r.Scalars.Age[idx] : float.NaN;
        float pmv = r.Comfort.Pmv.Length == g.Count ? r.Comfort.Pmv[idx] : float.NaN;
        float ppd = r.Comfort.Ppd.Length == g.Count ? r.Comfort.Ppd[idx] : float.NaN;
        float dr = r.Comfort.Dr.Length == g.Count ? r.Comfort.Dr[idx] : float.NaN;

        string room = "";
        var p2 = new Vector2(p.X, p.Y);
        foreach (var rm in r.Model.Rooms)
            if (p.Z >= rm.ZMin && p.Z <= rm.ZMax && InsideLoops(rm.Loops, p2)) { room = rm.Name; break; }

        return new ProbeValues(velocity, speed, pressure, temperature, co2, age, pmv, ppd, dr, room);
    }

    static int NearestIndex(VoxelGrid g, Vector3 p)
    {
        int x = Math.Clamp((int)MathF.Round((p.X - g.Origin.X) / g.Dx - 0.5f), 0, g.Nx - 1);
        int y = Math.Clamp((int)MathF.Round((p.Y - g.Origin.Y) / g.Dx - 0.5f), 0, g.Ny - 1);
        int z = Math.Clamp((int)MathF.Round((p.Z - g.Origin.Z) / g.Dx - 0.5f), 0, g.Nz - 1);
        return g.Index(x, y, z);
    }

    // Trilinear sample of a per-cell scalar array; NaN when the array is empty (not computed) or the
    // sample point falls near a solid cell (NaN corners propagate through Lerp, unlike SamplePressure's 0).
    static float SampleTrilinearOrNaN(FlowField field, float[] arr, Vector3 p)
    {
        if (arr.Length == 0) return float.NaN;
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
            return g.Cell[idx] == VoxelGrid.Solid ? float.NaN : arr[idx];
        }

        float c00 = float.Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        float c10 = float.Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        float c01 = float.Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        float c11 = float.Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        float c0 = float.Lerp(c00, c10, fy);
        float c1 = float.Lerp(c01, c11, fy);
        return float.Lerp(c0, c1, fz);
    }

    public static SimulationResult Analyze(BuildingModel model, FlowField field, Vector2 wind, RunOptions options, CancellationToken ct = default, bool portal = true)
    {
        var g = field.Grid;
        var result = new SimulationResult { Model = model, Field = field, Wind = wind, Options = options };

        var openings = new List<OpeningFlow>();
        foreach (var o in model.Openings)
        {
            var of = AnalyzeOpening(g, field, o);
            if (options.OpeningModel == OpeningModel.Calibrated)
            {
                of.ModelledCd = o.DischargeCoefficient is float cd && cd > 0f ? cd : LbmSolver.DefaultOpeningCd;
                of.LossK = LbmSolver.OpeningLossK(o, g.Dx, options.ViscosityFloor);
            }
            openings.Add(of);
        }
        result.Openings = openings;
        WarnOpeningCd(g, openings, options.OpeningModel == OpeningModel.Calibrated ? CdWarnLowCalibrated : CdWarnLow);
        NoteOpeningCd(g, openings, options);
        var tooSmall = Envelope.Apply(g, model, wind, options);   // C4: openings under 4 cells, blocked throats, gust-driven rooms, wall model, blockage

        var rooms = new List<RoomResult>();
        var connection = model.Rooms.Count > 0 ? ConnectionCells(g, model) : [];
        var exterior = model.Rooms.Count > 0 ? ConnectionCells(g, model, exteriorOnly: true) : [];
        int[]? owner = null;     // RV-09: built only if a detected room needs it
        int[] outlineCells = [];
        var roomMasks = model.Rooms.Count > 0 ? RoomMasks(g, model, out outlineCells, conn: connection) : [];   // OF-1: outline plus band
        // Re-derived on every analysis (a live session analyses the same grid again and again): drop last
        // pass's room lines first so they are replaced, not stacked.
        g.Warnings.RemoveAll(w => w.StartsWith("Room ", StringComparison.Ordinal) &&
            (w.Contains(" outline crosses open air: ", StringComparison.Ordinal) || w.EndsWith(": no air cells (check its boundaries)", StringComparison.Ordinal))
            || w.Contains(UnlistedNoteTag, StringComparison.Ordinal));
        for (int ri = 0; ri < model.Rooms.Count; ri++)
        {
            var room = model.Rooms[ri];
            ct.ThrowIfCancellationRequested();   // RU-01: a room is the unit of work worth leaving between
            if (owner == null && IsDetectedRoom(room)) owner = RoomOwner(g, model, masks: roomMasks);
            var rr = AnalyzeRoom(g, field, room, room.ZMin, model.Sources, connection, exterior, IsDetectedRoom(room) ? owner : null, roomMasks[ri], outlineCells[ri]);
            if (rr != null)
            {
                rr.TooFewCells = tooSmall.Contains(room);   // IMP-C
                if (portal) AddPortalNumbers(g, field, model, roomMasks[ri], rr);   // portal step 1: measurement only; the batches skip it (landing only)
                rooms.Add(rr);
                if (rr.InternalExchange > MathF.Max(InternalExchangeNote * rr.Inflow, 1e-3f))
                    g.Warnings.Add($"Room {room.Name} outline crosses open air: {rr.InternalExchange:0.###} m3/s crosses it through plain air " +
                        $"(a stair void, an open-plan edge or an outline drawn through open air); it is in the total exchange, not in the {rr.Inflow:0.###} m3/s through openings");
                // D10: the share of the room's air entering that is in no number (fresh-air ACH sees it, total exchange does not)
                float entering = rr.Inflow + rr.InternalExchange + rr.UnlistedInflow;
                if (entering > 1e-6f && rr.UnlistedInflow > UnlistedNoteShare * entering)
                    g.Warnings.Add($"{room.Name}: {rr.UnlistedInflow / entering * 100f:0} {UnlistedNoteTag}");
            }
            else g.Warnings.Add($"Room {room.Name}: no air cells (check its boundaries)");
        }
        result.Rooms = rooms;

        result.Sources = model.Sources.Select((s, i) => AnalyzeSource(g, field, s, i)).ToList();
        WarnChokedFans(g, field);

        float maxSourceFace = 0f;
        foreach (var s in model.Sources)
            if (s.Enabled && s.FlowRate > 0 && s.Width > 0 && s.Height > 0)
                maxSourceFace = MathF.Max(maxSourceFace, s.FlowRate / (s.Width * s.Height));
        result.ColorMax = MathF.Max(0.1f, MathF.Max(wind.Length(), maxSourceFace));

        result.Streamlines = BuildStreamlines(model, field, options.MaxDegreeOfParallelism);
        return result;
    }

    /// <summary>Speeds (Nx*Ny) at the grid layer containing world height z, clamped to the grid; NaN for solid cells.</summary>
    public static float[] SliceSpeed(FlowField field, float z)
    {
        var g = field.Grid;
        int sliceZ = Math.Clamp((int)MathF.Floor((z - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
        var speeds = new float[g.Nx * g.Ny];
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, sliceZ);
            speeds[x + g.Nx * y] = g.Cell[idx] != 0
                ? float.NaN
                : new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]).Length();
        }
        return speeds;
    }

    // Fixed cells carry the solver's imposed velocity like any other air cell, so only Solid zeroes out.
    public static Vector3 SampleVelocity(FlowField field, Vector3 p)
    {
        var g = field.Grid;
        Vector3 lp = (p - g.Origin) / g.Dx - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(lp.X), y0 = (int)MathF.Floor(lp.Y), z0 = (int)MathF.Floor(lp.Z);
        float fx = lp.X - x0, fy = lp.Y - y0, fz = lp.Z - z0;

        Vector3 At(int x, int y, int z)
        {
            x = Math.Clamp(x, 0, g.Nx - 1);
            y = Math.Clamp(y, 0, g.Ny - 1);
            z = Math.Clamp(z, 0, g.Nz - 1);
            int idx = g.Index(x, y, z);
            return g.Cell[idx] == VoxelGrid.Solid ? Vector3.Zero : new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]);
        }

        var c00 = Vector3.Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        var c10 = Vector3.Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        var c01 = Vector3.Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        var c11 = Vector3.Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        var c0 = Vector3.Lerp(c00, c10, fy);
        var c1 = Vector3.Lerp(c01, c11, fy);
        return Vector3.Lerp(c0, c1, fz);
    }

    /// <summary>Trilinear sample of P at a world point; 0 in solid cells.</summary>
    public static float SamplePressure(FlowField field, Vector3 p)
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
            return g.Cell[idx] == VoxelGrid.Solid ? 0f : field.P[idx];
        }

        float c00 = float.Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        float c10 = float.Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        float c01 = float.Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        float c11 = float.Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        float c0 = float.Lerp(c00, c10, fy);
        float c1 = float.Lerp(c01, c11, fy);
        return float.Lerp(c0, c1, fz);
    }

    // ---- openings ----

    /// <summary>One cell-centred plane through the opening, using the same closed footprint as
    /// carving (Voxelizer.InClosed: a cell centre on the edge counts, batch 5). Plane tie-breaking is independent of normal sign; the flow projection keeps that sign.</summary>
    public static List<int> ThroatCells(VoxelGrid g, Opening o) => LayerCells(g, o, 0f, MathF.Max(o.Width,g.Dx) * 0.5f, MathF.Max(o.Height,g.Dx) * 0.5f);

    /// <summary>Non-solid cells of the one-cell layer at <paramref name="offset"/> metres along the opening's
    /// normal from its centre, within a half-width/half-height of its centre.</summary>
    static List<int> LayerCells(VoxelGrid g, Opening o, float offset, float halfW, float halfH)
    {
        var cells = new List<int>();
        if (!o.IsOpen) return cells;
        Vector3 n0 = o.Normal; n0.Z = 0;
        if (n0.LengthSquared() < 1e-8f) return cells;
        var n = Vector3.Normalize(n0);
        var center = o.Center + offset * n;
        if (n.X < 0 || (n.X == 0 && n.Y < 0)) n = -n;
        var t = new Vector3(-n.Y, n.X, 0);
        float half = 0.5f * g.Dx;
        // An axis-aligned plane must select one lattice layer. Subtracting rounded world
        // coordinates can put BOTH adjacent centres inside (-Dx/2, Dx/2] (MINIBAT, Dx=.02).
        // Choose the nearest actual centre; an exact tie goes in the canonical normal direction.
        int axis = n.X == 0f ? 1 : n.Y == 0f ? 0 : -1, layer = -1;
        if (axis >= 0)
        {
            float coordinate = axis == 0 ? center.X : center.Y, origin = axis == 0 ? g.Origin.X : g.Origin.Y;
            int lower = (int)MathF.Floor((coordinate - origin) / g.Dx - 0.5f);
            float a = MathF.Abs(origin + (lower + 0.5f) * g.Dx - coordinate);
            float b = MathF.Abs(origin + (lower + 1.5f) * g.Dx - coordinate);
            layer = a < b ? lower : lower + 1;
        }
        ForEachCellNear(g, center, halfW, halfH, half, (x, y, z) =>
        {
            int idx = g.Index(x, y, z);
            if (g.Cell[idx] == VoxelGrid.Solid) return;
            var c = g.CellCenter(x, y, z) - center;
            float e = Vector3.Dot(c, n);
            if (axis >= 0 ? (axis == 0 ? x : y) != layer : e <= -half || e > half) return;
            if (Voxelizer.InClosed(Vector3.Dot(c,t),halfW,g.Dx) && Voxelizer.InClosed(c.Z,halfH,g.Dx)) cells.Add(idx);   // RP follow-up 4: the carve's rule
        });
        return cells;
    }

    /// <summary>Density of air the solver's pressures are scaled with (LbmSolver pScale), kg/m3.</summary>
    const float RhoAir = 1.2f;

    static OpeningFlow AnalyzeOpening(VoxelGrid g, FlowField field, Opening o)
    {
        var flow = new OpeningFlow { Opening = o };
        var throat = ThroatCells(g, o);
        if (throat.Count == 0) return flow;
        var n = Vector3.Normalize(new Vector3(o.Normal.X, o.Normal.Y, 0));

        // Flow = integral of u.n over the carved throat (NOT a box-mean speed x the nominal Width x Height,
        // which ran ~14 % low: the box average took in the slower air a cell either side of the wall).
        double sum = 0;
        foreach (int idx in throat)
            sum += field.Ux[idx] * n.X + field.Uy[idx] * n.Y + field.Uz[idx] * n.Z;
        float cellArea = g.Dx * g.Dx;
        // The solver's conserved throat-plane link flux, when this field carries one for the opening (same +Normal sign);
        // node u.n over the throat is biased at the jambs (wall-bound population momentum), so it is only the fallback.
        var link = field.OpeningFluxes.FirstOrDefault(f => ReferenceEquals(f.Opening, o) || (o.Id.Length > 0 && f.Opening.Id == o.Id));
        if (link != null) sum = link.Net / cellArea;
        flow.CarvedArea = throat.Count * cellArea;
        flow.FlowRate = (float)(sum * cellArea);
        flow.MeanSpeed = (float)(sum / throat.Count);
        // RX-09b: an oblique opening's one-cell slab holds 0.63-1.50 of its area by phase, so the mean speed is the conserved link-plane flux over the nominal area.
        if (link != null && n.X != 0f && n.Y != 0f && o.Width * o.Height > 0f) flow.MeanSpeed = flow.FlowRate / (o.Width * o.Height);

        // Pressure drop across the opening, mean static pressure over the nominal footprint just clear of
        // the wall faces on either side, and the effective discharge coefficient it implies.
        float off = o.Depth * 0.5f + 1.5f * g.Dx;
        float halfW = MathF.Max(o.Width, g.Dx) * 0.5f, halfH = MathF.Max(o.Height, g.Dx) * 0.5f;
        float MeanP(float offset)
        {
            var cells = LayerCells(g, o, offset, halfW, halfH);
            if (cells.Count == 0) return float.NaN;
            double p = 0;
            foreach (int idx in cells) p += field.P[idx];
            return (float)(p / cells.Count);
        }
        flow.PressureDrop = MeanP(-off) - MeanP(off);
        float aNominal = o.Width * o.Height;
        float dp = MathF.Abs(flow.PressureDrop);
        flow.EffectiveCd = aNominal > 0 && dp > 1e-6f
            ? MathF.Abs(flow.FlowRate) / (aNominal * MathF.Sqrt(2f * dp / RhoAir))
            : float.NaN;
        return flow;
    }

    const string CdWarnTag = "effective discharge coefficient";

    /// <summary>An opening at least <see cref="CdWarnMinCells"/> cells across whose effective Cd falls outside
    /// low-0.8 (a sharp-edged orifice is ~0.6-0.65) is not behaving like a pressure-driven orifice at this
    /// resolution -- flow sweeping along the facade, a jet crossing the room, or a pressure drop too small to
    /// read -- so its flow rate deserves a second look. Re-derived on every analysis.</summary>
    static void WarnOpeningCd(VoxelGrid g, List<OpeningFlow> openings, float cdWarnLow)
    {
        g.Warnings.RemoveAll(w => w.Contains(CdWarnTag, StringComparison.Ordinal));
        foreach (var f in openings)
        {
            var o = f.Opening;
            if (!o.IsOpen || float.IsNaN(f.EffectiveCd) || MathF.Min(o.Width, o.Height) < CdWarnMinCells * g.Dx) continue;
            if (f.EffectiveCd >= cdWarnLow && f.EffectiveCd <= CdWarnHigh) continue;
            g.Warnings.Add($"Opening {(o.Name.Length > 0 ? o.Name : o.Id)}: {CdWarnTag} {f.EffectiveCd:0.00} " +
                $"(Q {f.FlowRate:0.###} m3/s, pressure drop {f.PressureDrop:0.###} Pa) is outside {cdWarnLow:0.00}-{CdWarnHigh:0.0} " +
                "- the flow here is not simple orifice flow (air sweeping along the facade, a jet across the room, or too " +
                "small a pressure difference to read); treat its flow rate with care");
        }
    }

    /// <summary>RW-08: drops the coarse ledger's "Device X moved N% of its set flow" note for a device whose flow a fine box answers for, so the note does not
    /// sit next to the box's own flow.</summary>
    public static void DropDeviceMovedNote(SimulationResult coarse, AirSource device)
    {
        string prefix = $"Device {device.Name} moved ";
        coarse.Ledger.Notes.RemoveAll(n => n.StartsWith(prefix, StringComparison.Ordinal));
    }

    const string CdResolvedTag = "the lattice resolves this opening at Cd", CdScatterTag = "discharge coefficient is uncertain by about";
    const string CdWallTag = "wall thickness taken from the geometry";

    /// <summary>RP-07: drops the Cd notes (resolved, placement uncertainty, wall thickness) the coarse grid wrote for an opening that a fine box answers for, so only the
    /// fine box's own notes (at its Dx) stand next to the flow it reports.</summary>
    public static void DropOpeningCdNotes(VoxelGrid coarse, Opening o)
    {
        string prefix = $"Opening {(o.Name.Length > 0 ? o.Name : o.Id)}: ";
        coarse.Warnings.RemoveAll(w => w.StartsWith(prefix, StringComparison.Ordinal) &&
            (w.Contains(CdResolvedTag, StringComparison.Ordinal) || w.Contains(CdScatterTag, StringComparison.Ordinal) || w.Contains(CdWallTag, StringComparison.Ordinal)));
    }
    const float CdScatterNoteAbove = 0.05f;

    /// <summary>The placement-uncertainty note of an opening, or null when X(n, t) is 5 % or less. X is <see cref="LbmSolver.CdScatter"/>: the largest
    /// |Cd(offset) - phase mean| / phase mean of the calibration rig over where the hole sits in the grid.</summary>
    public static string? OpeningCdScatterNote(Opening o, float dx)
    {
        float n = LbmSolver.OpeningCells(o, dx);
        if (n <= 0f) return null;
        float x = LbmSolver.CdScatter(n, LbmSolver.OpeningWallCells(o, dx));
        return x > CdScatterNoteAbove ? $"the opening's {CdScatterTag} ±{x * 100f:0} % at this cell size; a finer quality level reduces it" : null;
    }

    /// <summary>6d (P13a-03), calibrated opening model: (1) an opening the lattice already resolves below its requested Cd gets no loss (a negative
    /// loss is non-physical), so its flow is low by about 1 - X/Y; (2) wherever the placement scatter X(n, t) exceeds 5 %, the Cd the
    /// lattice resolves depends on where the opening sits relative to the grid (the table is the mean over that position): see
    /// <see cref="OpeningCdScatterNote"/> (fixes/b6d/res4a.txt, res4b.txt, res16.txt).</summary>
    static void NoteOpeningCd(VoxelGrid g, List<OpeningFlow> openings, RunOptions options)
    {
        g.Warnings.RemoveAll(w => w.Contains(CdResolvedTag, StringComparison.Ordinal) || w.Contains(CdScatterTag, StringComparison.Ordinal) || w.Contains(CdWallTag, StringComparison.Ordinal));
        if (options.OpeningModel != OpeningModel.Calibrated) return;
        foreach (var f in openings)
        {
            var o = f.Opening;
            if (!o.IsOpen || o.Width <= 0f || o.Height <= 0f) continue;
            string name = o.Name.Length > 0 ? o.Name : o.Id;
            // RP-06: which wall thickness t the Cd table was read at, stated when it is not the drawn Depth.
            if (o.MeasuredDepth > 0f && MathF.Abs(o.MeasuredDepth - o.Depth) >= MathF.Max(0.01f, 0.25f * g.Dx))
                g.Warnings.Add($"Opening {name}: {CdWallTag} ({o.MeasuredDepth:0.00} m; the drawn depth says {o.Depth:0.00} m)");
            var (solver, target) = LbmSolver.OpeningResolvedCd(o, g.Dx, options.ViscosityFloor);
            if (1f - solver / target >= 0.01f)
                g.Warnings.Add($"Opening {name}: {CdResolvedTag} {solver:0.00}, below the requested {target:0.00}; flow here may be about {(1f - solver / target) * 100f:0} % low");
            var scatter = OpeningCdScatterNote(o, g.Dx);
            if (scatter != null) g.Warnings.Add($"Opening {name}: {scatter}");
        }
    }

    const float CdWarnLow = 0.5f, CdWarnHigh = 0.8f, CdWarnMinCells = 4f;
    // Calibrated opening model: the floor scales with the model's own shift of a sharp opening's Cd, 0.72 resolved ->
    // 0.61 (x0.85): 0.5 x 0.85 = 0.42. A LEEWARD opening in cross flow reads ~0.8x the windward one on its local static
    // dp, because the cross-room jet stagnates on the leeward wall around it (sandbox 10b: 0.54 / 0.72 resolved,
    // 0.48 / 0.61 calibrated), which put it under 0.5 once the windward one sat on 0.61.
    const float CdWarnLowCalibrated = 0.42f;

    static void ForEachCellNear(VoxelGrid g, Vector3 center, float halfW, float halfH, float halfD, Action<int, int, int> body)
    {
        float reach = MathF.Sqrt(halfW*halfW + halfH*halfH + halfD*halfD) + g.Dx;
        Vector3 lo = center - new Vector3(reach) - g.Origin;
        Vector3 hi = center + new Vector3(reach) - g.Origin;
        int x0 = Math.Clamp((int)MathF.Floor(lo.X / g.Dx), 0, g.Nx - 1);
        int x1 = Math.Clamp((int)MathF.Floor(hi.X / g.Dx), 0, g.Nx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y / g.Dx), 0, g.Ny - 1);
        int y1 = Math.Clamp((int)MathF.Floor(hi.Y / g.Dx), 0, g.Ny - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z / g.Dx), 0, g.Nz - 1);
        int z1 = Math.Clamp((int)MathF.Floor(hi.Z / g.Dx), 0, g.Nz - 1);
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            body(x, y, z);
    }

    // ---- sources ----

    // The grid contract has no per-cell source ownership, so the source's Fixed cells are recovered by
    // re-running Voxelizer's own placement search (SourceCells), now testing for Fixed instead of Air.
    // Deterministic: same geometry, same attempt order, so it lands on the same cells.
    static SourceFlow AnalyzeSource(VoxelGrid g, FlowField field, AirSource s, int index)
    {
        var flow = new SourceFlow { Source = s };
        if (!s.Enabled) return flow;

        // A body-force device has no Fixed cells to measure across: the solver already integrated the flux
        // through its own driven cells (and, for a conditioning unit, its duty and return temperature)
        // while it had the populations to hand, so take those numbers.
        if (s.Device != DeviceKind.Patch)
        {
            foreach (var d in field.Devices)
            {
                if (d.SourceIndex >= 0 ? d.SourceIndex != index : d.SourceId != s.Id) continue;
                flow.FlowRate = d.FlowRate;
                flow.HeatRemovedWatts = d.HeatRemovedWatts;
                flow.ReturnTemperature = d.ReturnTemperature;
                break;
            }
            return flow;
        }

        if (s.FlowRate <= 0 || s.Width <= 0 || s.Height <= 0) return flow;

        var n = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : Vector3.UnitZ;
        var cells = Voxelizer.SourceCells(g, s, c => c == VoxelGrid.Fixed);
        if (cells.Count == 0) return flow;

        double sum = 0;
        foreach (var idx in cells)
            sum += Vector3.Dot(new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]), n);
        flow.FlowRate = (float)(sum / cells.Count) * s.Width * s.Height;
        return flow;
    }

    /// <summary>Achieved/set flow below which a fan with too little clear air above it is called choked.</summary>
    const float FanChokedRatio = 0.6f;

    /// <summary>The fan-intake warning, from what the run measured: a disc with less clear air above it than
    /// its intake needs (Voxelizer records it) that ALSO delivered under <see cref="FanChokedRatio"/> of
    /// its set flow. Re-derived on every analysis, so a live session re-analysing the same grid replaces
    /// the line instead of stacking copies, and drops it once the fan recovers.</summary>
    static void WarnChokedFans(VoxelGrid g, FlowField field)
    {
        foreach (var region in g.Devices)
        {
            if (float.IsNaN(region.IntakeClearance)) continue;
            string prefix = $"Device {region.SourceName}: only ";
            g.Warnings.RemoveAll(w => w.StartsWith(prefix, StringComparison.Ordinal) && w.Contains("choked"));
            if (!region.IntakeShort) continue;
            var d = field.Devices.Find(x => x.SourceIndex >= 0 ? x.SourceIndex == region.SourceIndex : x.SourceId == region.SourceId);
            if (d == null || d.SetFlowRate <= 1e-6f) continue;
            float ratio = MathF.Abs(d.FlowRate) / d.SetFlowRate;
            if (ratio >= FanChokedRatio) continue;
            g.Warnings.Add($"Device {region.SourceName}: only {region.IntakeClearance:0.##} m of clear air on the intake side " +
                $"of the fan at {g.Dx * 1000:0} mm and it moved {ratio * 100:0}% of its set flow - the disc is choked; " +
                "lower it, or solve a finer grid");
        }
    }

    // ---- rooms ----

    /// <summary>Plain-air exchange below this share of a room's Inflow is voxel noise at the outline, not a
    /// loop drawn through open air, and gets no Warning.</summary>
    const float InternalExchangeNote = 0.05f;

    /// <summary>D10: the unlisted-gap note is for a share of the room's air entering above this.</summary>
    const float UnlistedNoteShare = 0.10f;
    const string UnlistedNoteTag = "% of its air exchange passes through gaps that are not recognised as openings; total exchange excludes them.";
    /// <summary>D10: air that is still open this far beyond a detected room's band, along the face normal, has no wall in the way: a hole, not the
    /// room's own recirculation. Wider than the band (one detection cell, up to ~0.9 m) plus a wall.</summary>
    const float UnlistedReachMetres = 2f;

    /// <summary>D10: m3/s entering a detected room across band faces (see <see cref="RoomResult.UnlistedInflow"/>): the face is not a listed
    /// connection, the cell beyond it belongs to no room, and the air stays open (no solid cell) for <see cref="UnlistedReachMetres"/> straight on.</summary>
    static double UnlistedInflow(VoxelGrid g, FlowField field, bool[] mask, bool[] connection, int[] owner)
    {
        double sum = 0;
        int reach = Math.Max(2, (int)MathF.Ceiling(UnlistedReachMetres / g.Dx));
        int[] ddx = [-1, 1, 0, 0, 0, 0], ddy = [0, 0, -1, 1, 0, 0], ddz = [0, 0, 0, 0, -1, 1];
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, z);
            if (!mask[idx]) continue;
            for (int f = 0; f < 6; f++)
            {
                int dx = ddx[f], dy = ddy[f], dz = ddz[f];
                int nIdx = FaceNeighbor(g, mask, x, y, z, dx, dy, dz);
                if (nIdx < 0 || connection[idx] || connection[nIdx] || owner[nIdx] != 0) continue;
                float[] u = dx != 0 ? field.Ux : dy != 0 ? field.Uy : field.Uz;
                float signed = (dx + dy + dz) > 0 ? -0.5f * (u[idx] + u[nIdx]) : 0.5f * (u[idx] + u[nIdx]);
                if (signed <= 0f) continue;
                bool open = true;
                for (int s = 1; s <= reach && open; s++)
                {
                    int cx = x + dx * (s + 1), cy = y + dy * (s + 1), cz = z + dz * (s + 1);
                    if (cx < 0 || cy < 0 || cz < 0 || cx >= g.Nx || cy >= g.Ny || cz >= g.Nz) break;   // out of the domain: open
                    open = g.Cell[g.Index(cx, cy, cz)] != VoxelGrid.Solid;
                }
                if (open) sum += signed * g.Dx * g.Dx;
            }
        }
        return sum;
    }

    /// <summary>One opening as a room sees it: its time-mean plane fluxes (Pos along +Normal, Neg against it), which side of the plane the room is on
    /// (+1 = the +Normal side, -1 = the other), and whether the opening is exterior.</summary>
    internal readonly record struct PortalOpening(double Pos, double Neg, int Side, bool Exterior);

    /// <summary>Portal step 1: through = sum of the flux INTO the room across each opening plane; netOutdoor = sum over exterior openings of (in - out) floored at 0 per opening.</summary>
    internal static void PortalSums(IEnumerable<PortalOpening> ops, out double through, out double netOutdoor)
    {
        through = 0; netOutdoor = 0;
        foreach (var o in ops)
        {
            double inn = o.Side > 0 ? o.Pos : o.Neg, outt = o.Side > 0 ? o.Neg : o.Pos;
            through += inn;
            if (o.Exterior) netOutdoor += Math.Max(0, inn - outt);
        }
    }

    /// <summary>Share (0-1) of all air entering (total exchange + unlisted-gap inflow) that is not through the openings' planes.</summary>
    internal static float UnexplainedShareOf(double total, double unlisted, double through)
    {
        double all = total + unlisted;
        return all > 1e-9 ? (float)Math.Max(0.0, (all - through) / all) : 0f;
    }

    /// <summary>The openings whose plane a room mask touches on one side: mask cells just beyond the wall faces (to 3 cells), inside the footprint.
    /// A room on both sides (rare) takes the side with more cells; a tie leaves the opening out.</summary>
    internal static List<PortalOpening> PortalOpenings(VoxelGrid g, FlowField field, BuildingModel model, bool[] mask)
    {
        var list = new List<PortalOpening>();
        foreach (var link in field.OpeningFluxes)
        {
            var o = link.Opening;
            Vector3 n0 = o.Normal; n0.Z = 0;
            if (n0.LengthSquared() < 1e-8f) continue;
            var n = Vector3.Normalize(n0);
            var t = new Vector3(-n.Y, n.X, 0);
            float halfW = MathF.Max(o.Width, g.Dx) * 0.5f + g.Dx, halfH = MathF.Max(o.Height, g.Dx) * 0.5f + g.Dx;
            float wall = o.Depth * 0.5f, reachN = wall + 3f * g.Dx;
            int plus = 0, minus = 0;
            ForEachCellNear(g, o.Center, halfW, halfH, reachN, (x, y, z) =>
            {
                int i = g.Index(x, y, z);
                if (!mask[i]) return;
                var c = g.CellCenter(x, y, z) - o.Center;
                float e = Vector3.Dot(c, n);
                if (MathF.Abs(Vector3.Dot(c, t)) > halfW || MathF.Abs(c.Z) > halfH || MathF.Abs(e) <= wall || MathF.Abs(e) > reachN) return;
                if (e > 0) plus++; else minus++;
            });
            if (plus == minus) continue;
            list.Add(new PortalOpening(link.Pos, link.Neg, plus > minus ? 1 : -1, IsExteriorOpening(model, o, g.Dx)));
        }
        return list;
    }

    static void AddPortalNumbers(VoxelGrid g, FlowField field, BuildingModel model, bool[] mask, RoomResult rr)
    {
        if (field.OpeningFluxes.Count == 0) return;   // no plane fluxes (u.n fallback): leave NaN
        PortalSums(PortalOpenings(g, field, model, mask), out double through, out double netOut);
        rr.ThroughOpeningInflow = (float)through;
        rr.NetOutdoorInflow = (float)netOut;
        rr.UnexplainedShare = UnexplainedShareOf(rr.Inflow + rr.InternalExchange, rr.UnlistedInflow, through);
    }

    /// <summary>Cells through which air can really enter a room: the throat of every OPEN opening (the
    /// same box <see cref="AnalyzeOpening"/> measures, widened by a cell each way so an outline inset a
    /// cell off the wall face still meets it), every body-force device cell, every Fixed source patch and
    /// the domain's outer faces (a box cut through a building). A room face whose inside or outside cell is
    /// one of these counts toward Inflow; any other air-to-air face of the outline is internal exchange.
    /// <paramref name="exteriorOnly"/>: leave out the openings between two indoor spaces (interior doors), so what remains is
    /// where air enters straight from outside -- the OutdoorInflow share (D4).</summary>
    internal static bool[] ConnectionCells(VoxelGrid g, BuildingModel model, bool exteriorOnly = false)
    {
        var conn = new bool[g.Count];
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen) continue;
            if (exteriorOnly && !IsExteriorOpening(model, o, g.Dx)) continue;
            Vector3 n0 = o.Normal; n0.Z = 0;
            if (n0.LengthSquared() < 1e-8f) continue;
            var n = Vector3.Normalize(n0);
            var t = new Vector3(-n.Y, n.X, 0);
            float halfW = MathF.Max(o.Width, g.Dx) * 0.5f + g.Dx;
            float halfH = MathF.Max(o.Height, g.Dx) * 0.5f + g.Dx;
            float halfD = o.Depth * 0.5f + 2f * g.Dx;
            ForEachCellNear(g, o.Center, halfW, halfH, halfD, (x, y, z) =>
            {
                var c = g.CellCenter(x, y, z) - o.Center;
                if (MathF.Abs(Vector3.Dot(c, t)) <= halfW && MathF.Abs(c.Z) <= halfH && MathF.Abs(Vector3.Dot(c, n)) <= halfD)
                    conn[g.Index(x, y, z)] = true;
            });
        }
        foreach (var d in g.Devices) foreach (int i in d.Cells) conn[i] = true;
        foreach (var f in g.FixedCells) conn[f.Index] = true;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
            if (x == 0 || y == 0 || z == 0 || x == g.Nx - 1 || y == g.Ny - 1 || z == g.Nz - 1)
                conn[g.Index(x, y, z)] = true;
        return conn;
    }

    static RoomResult? AnalyzeRoom(VoxelGrid g, FlowField field, Room room, float floorElevation, List<AirSource> sources,
        bool[] connection, bool[] exterior, int[]? owner, bool[] mask, int count)
    {
        // OF-1: mask = outline plus band (every flux and mean below); count = the outline's cells alone, so the voxel-volume fallback (a room with no
        // nominal volume) and the volume shown stay the drawn/detected room (nominal footprint x height, or IMP-A's RoomDetect.RoomVolumes for a detected one):
        // the band is wall-gap air the drawn volume already counts, and a volume that moved with the grid phase would defeat the merge.
        if (count == 0) return null;

        float voxelVolume = count * g.Dx * g.Dx * g.Dx;
        float nominalVolume = NominalVolume(g, room, out string volumeNote);
        bool nominal = !float.IsNaN(nominalVolume);
        float volume = nominal ? nominalVolume : voxelVolume;

        // Only faces through a real connection count as inflow. A loop drawn through open air (no wall at
        // the outline) is crossed both ways by the recirculation of the space around it, and summing the
        // inward half of that is what read ~470 ACH on detected rooms of an imported mesh.
        double inflow = 0, exchange = 0, outflow = 0, outdoor = 0;
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            if (!mask[g.Index(x, y, z)]) continue;
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, -1, 0, 0, ref inflow, ref exchange, ref outflow, ref outdoor);
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, 1, 0, 0, ref inflow, ref exchange, ref outflow, ref outdoor);
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, 0, -1, 0, ref inflow, ref exchange, ref outflow, ref outdoor);
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, 0, 1, 0, ref inflow, ref exchange, ref outflow, ref outdoor);
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, 0, 0, -1, ref inflow, ref exchange, ref outflow, ref outdoor);
            FaceInflow(g, field, mask, connection, exterior, owner, x, y, z, 0, 0, 1, ref inflow, ref exchange, ref outflow, ref outdoor);
        }

        double unlisted = owner == null ? 0 : UnlistedInflow(g, field, mask, connection, owner);   // D10

        var measuredFlux = field.RoomFluxes.FirstOrDefault(r => ReferenceEquals(r.Room,room) || (room.Id.Length > 0 && r.Room.Id == room.Id));
        if (measuredFlux != null) { inflow = measuredFlux.Inflow; outflow = measuredFlux.Outflow; exchange = measuredFlux.InternalExchange; outdoor = measuredFlux.OutdoorInflow; }

        // Boundary flux only sees air crossing the room's surface: a Supply PATCH mounted flush against a
        // solid ceiling/wall injects air from *within* the room, so every face of its Fixed cells is either
        // Solid-blocked or an interior (both-mask) face -- invisible to the loop above. Add its measured
        // throughput directly for any enabled Supply whose patch is mostly inside this room's mask (falling
        // back to the nameplate FlowRate if the field gives a zero reading). Return grilles remove air and
        // Fan patches just recirculate room air, so neither one adds to Inflow.
        //
        // A body-force DEVICE is deliberately not in this list, whatever its SourceKind: it creates no air,
        // so every cubic metre it delivers to the room has already crossed the room's surface somewhere
        // (through the opening or duct the device sits in) and is in the flux above. Adding it here would
        // count the same air twice. Checks 45a/45b assert exactly that.
        foreach (var s in sources)
        {
            if (!s.Enabled || s.Device != DeviceKind.Patch || (s.Kind != SourceKind.Supply && s.Kind != SourceKind.Return)) continue;
            var cells = Voxelizer.SourceCells(g, s, c => c == VoxelGrid.Fixed);
            if (cells.Count == 0) continue;
            int inMask = cells.Count(idx => mask[idx]);
            if (inMask * 2 < cells.Count) continue;   // not mostly inside this room
            float measured = AnalyzeSource(g, field, s, sources.IndexOf(s)).FlowRate;
            // A Return patch extracts from within the room: its throughput is Outflow, never Inflow/ACH.
            if (s.Kind == SourceKind.Return) outflow += MathF.Abs(measured);
            else { double add = measured > 0 ? measured : g.PatchFlow(sources.IndexOf(s), s.FlowRate); inflow += add; outdoor += add; }   // a supply patch delivers outdoor air
        }

        float zLo = floorElevation + 0.1f, zHi = floorElevation + 1.8f;
        double speedSum = 0;
        int speedCount = 0, stagnantCount = 0;
        for (int z = 0; z < g.Nz; z++)
        {
            float cz = g.Origin.Z + (z + 0.5f) * g.Dx;
            if (cz < zLo || cz > zHi) continue;
            for (int y = 0; y < g.Ny; y++)
            for (int x = 0; x < g.Nx; x++)
            {
                int idx = g.Index(x, y, z);
                if (!mask[idx]) continue;
                float speed = new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]).Length();
                speedSum += speed;
                speedCount++;
                if (speed < StagnantSpeed) stagnantCount++;
            }
        }

        return new RoomResult
        {
            Room = room,
            Volume = volume,
            VoxelVolume = voxelVolume,
            NominalVolume = nominalVolume,
            VolumeIsNominal = nominal,
            VolumeNote = volumeNote,
            Inflow = (float)inflow,
            OutdoorInflow = (float)outdoor,
            Outflow = (float)outflow,
            NetFlux = (float)(inflow - outflow),
            InternalExchange = (float)exchange,
            UnlistedInflow = (float)unlisted,
            Ach =(float)(inflow + exchange) * 3600f / volume,   // D2: total exchange, plain-air faces (stair voids, open-plan edges) included
            MeanSpeed = speedCount > 0 ? (float)(speedSum / speedCount) : 0f,
            StagnantFraction = speedCount > 0 ? (float)stagnantCount / speedCount : 0f,
        };
    }

    /// <summary>Footprint area (even-odd over the loops: a loop inside another is a hole) x (ZMax - ZMin), m3, or NaN with
    /// <paramref name="note"/> saying why not: a voxel-detected room (its outline is the grid's own), no usable footprint or
    /// height, or a room reaching outside the solved domain (its flux is only the part inside, so a whole-room volume
    /// would divide by the wrong thing).</summary>
    internal static float NominalVolume(VoxelGrid g, Room room, out string note)
    {
        note = "";
        if (room.VoxelDerived || room.Id.StartsWith("det-", StringComparison.Ordinal))
        {
            if (room.DetectedVolume > 0f) return room.DetectedVolume;   // un-eroded outline x storey height (RoomDetect)
            note = "detected room: no nominal volume, ACH uses the voxel air volume"; return float.NaN;
        }
        float height = room.ZMax - room.ZMin;
        var loops = room.Loops.Where(l => l.Length >= 3).ToList();
        if (!(height > 0f) || loops.Count == 0)
        { note = "no footprint or height in the model, ACH uses the voxel air volume"; return float.NaN; }

        double area = 0;
        var lo = new Vector2(float.PositiveInfinity); var hi = new Vector2(float.NegativeInfinity);
        for (int i = 0; i < loops.Count; i++)
        {
            double a = 0; var p = loops[i];
            for (int k = 0, j = p.Length - 1; k < p.Length; j = k++) a += ((double)p[j].X * p[k].Y - (double)p[k].X * p[j].Y) * 0.5;
            int depth = 0;
            for (int j = 0; j < loops.Count; j++) if (j != i && PointInPolygon(loops[j], p[0])) depth++;
            area += depth % 2 == 0 ? Math.Abs(a) : -Math.Abs(a);
            foreach (var v in p) { lo = Vector2.Min(lo, v); hi = Vector2.Max(hi, v); }
        }
        if (!(area > 0)) { note = "footprint has no area, ACH uses the voxel air volume"; return float.NaN; }

        float eps = 1e-3f * g.Dx;
        bool inside = lo.X >= g.Origin.X - eps && lo.Y >= g.Origin.Y - eps && hi.X <= g.Origin.X + g.Nx * g.Dx + eps
            && hi.Y <= g.Origin.Y + g.Ny * g.Dx + eps && room.ZMin >= g.Origin.Z - eps && room.ZMax <= g.Origin.Z + g.Nz * g.Dx + eps;
        if (!inside) { note = "room extends beyond the solved domain, ACH uses the voxel air volume"; return float.NaN; }
        return (float)(area * height);
    }

    // Flow across one face of a mask cell from an outside air cell, positive part only (= into the room),
    // added to inflow when either cell is a connection (see ConnectionCells), else to exchange.
    static void FaceInflow(VoxelGrid g, FlowField field, bool[] mask, bool[] connection, bool[] exterior, int[]? owner, int x, int y, int z,
        int dx, int dy, int dz, ref double inflow, ref double exchange, ref double outflow, ref double outdoor)
    {
        int nIdx = FaceNeighbor(g,mask,x,y,z,dx,dy,dz);
        if (nIdx == -2) return;

        int idx = g.Index(x, y, z);
        float[] u = dx != 0 ? field.Ux : dy != 0 ? field.Uy : field.Uz;
        float avg = nIdx < 0 ? u[idx] : 0.5f * (u[idx] + u[nIdx]);
        float signed = (dx + dy + dz) > 0 ? -avg : avg;
        bool conn = nIdx < 0 || connection[idx] || connection[nIdx];
        if (signed < 0) { if (conn) outflow -= signed * g.Dx * g.Dx; return; }
        if (signed == 0) return;
        if (conn) { inflow += signed * g.Dx * g.Dx; if (nIdx < 0 || exterior[idx] || exterior[nIdx]) outdoor += signed * g.Dx * g.Dx; }
        else if (owner == null || (nIdx >= 0 && owner[nIdx] != 0)) exchange += signed * g.Dx * g.Dx;   // RV-09: a detected room's own band is not exchange
    }

    // internal: also used by Voxelizer.FloodFill to seed sealed-room interiors.
    internal static bool InsideLoops(List<Vector2[]> loops, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in loops)
            if (PointInPolygon(loop, p)) inside = !inside;
        return inside;
    }

    static bool PointInPolygon(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // ---- streamlines ----

    const int MaxStreamlines = 800;

    static List<Streamline> BuildStreamlines(BuildingModel model, FlowField field, int? maxDop)
    {
        var g = field.Grid;
        var (planMin, planMax) = Voxelizer.PlanBounds(model);

        float zLoBound = float.NegativeInfinity, zHiBound = float.PositiveInfinity;
        var analysed = model.Storeys.Where(s => s.Analysed).ToList();
        if (analysed.Count > 0)
        {
            zLoBound = analysed.Min(s => s.Elevation) - 1f;
            zHiBound = analysed.Max(s => s.Elevation + s.Height) + 1f;
        }

        // Pos + ForwardOnly: Supply/Fan seeds trace forward only (the seed is the device, so it must stay
        // the polyline's first point); opening and room seeds trace both ways as before.
        var seeds = new List<(Vector3 Pos, bool ForwardOnly)>();
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen) continue;
            Vector3 n0 = o.Normal; n0.Z = 0;
            if (n0.LengthSquared() < 1e-8f) continue;
            var n = Vector3.Normalize(n0);
            var t = new Vector3(-n.Y, n.X, 0);
            int wc = Math.Clamp((int)MathF.Round(o.Width / 0.4f), 1, 4);
            int hc = Math.Clamp((int)MathF.Round(o.Height / 0.4f), 1, 4);
            for (int j = 0; j < hc; j++)
            for (int i = 0; i < wc; i++)
            {
                float ot = ((i + 0.5f) / wc - 0.5f) * o.Width;
                float oz = ((j + 0.5f) / hc - 0.5f) * o.Height;
                seeds.Add((o.Center + t * ot + new Vector3(0, 0, oz), false));
            }
        }

        // Supply and Fan patches push air into the space, so they seed streamlines too (Return grilles
        // pull air in from every direction -- any seed there is arbitrary, so they're skipped).
        foreach (var s in model.Sources)
        {
            if (!s.Enabled || s.FlowRate <= 0 || s.Kind == SourceKind.Return) continue;
            var n = s.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Direction) : Vector3.UnitZ;
            var up = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            var t = Vector3.Normalize(Vector3.Cross(up, n));
            var b = Vector3.Cross(n, t);
            int wc = Math.Clamp((int)MathF.Round(s.Width / 0.2f), 1, 4);
            int hc = Math.Clamp((int)MathF.Round(s.Height / 0.2f), 1, 4);
            var offset = s.Center + n * g.Dx;
            for (int j = 0; j < hc; j++)
            for (int i = 0; i < wc; i++)
            {
                float lt = ((i + 0.5f) / wc - 0.5f) * s.Width;
                float lb = ((j + 0.5f) / hc - 0.5f) * s.Height;
                seeds.Add((offset + t * lt + b * lb, true));
            }
        }
        int prioritySeedCount = seeds.Count;   // opening + source seeds always win the cap below

        var roomSeeds = new List<(Vector3 Pos, bool ForwardOnly)>();
        for (float x = g.Origin.X; x <= g.Origin.X + g.Nx * g.Dx; x += 1.5f)
        for (float y = g.Origin.Y; y <= g.Origin.Y + g.Ny * g.Dx; y += 1.5f)
        {
            var p2 = new Vector2(x, y);
            Room? hitRoom = null;
            foreach (var room in model.Rooms)
                if (InsideLoops(room.Loops, p2)) { hitRoom = room; break; }
            if (hitRoom == null) continue;
            float seedZ = hitRoom.ZMin + 1.1f;
            int seedZi = Math.Clamp((int)MathF.Floor((seedZ - g.Origin.Z) / g.Dx), 0, g.Nz - 1);
            int xi = Math.Clamp((int)MathF.Floor((x - g.Origin.X) / g.Dx), 0, g.Nx - 1);
            int yi = Math.Clamp((int)MathF.Floor((y - g.Origin.Y) / g.Dx), 0, g.Ny - 1);
            if (g.Cell[g.Index(xi, yi, seedZi)] == VoxelGrid.Solid) continue;
            roomSeeds.Add((new Vector3(x, y, seedZ), false));
        }
        seeds.AddRange(roomSeeds);

        if (seeds.Count > MaxStreamlines)
        {
            List<(Vector3 Pos, bool ForwardOnly)> keptPriority;
            if (prioritySeedCount <= MaxStreamlines)
                keptPriority = seeds.GetRange(0, prioritySeedCount);
            else
            {
                keptPriority = new List<(Vector3, bool)>();
                for (int i = 0; i < MaxStreamlines; i++) keptPriority.Add(seeds[i * prioritySeedCount / MaxStreamlines]);
            }
            int roomBudget = Math.Max(0, MaxStreamlines - keptPriority.Count);
            var keptRooms = new List<(Vector3, bool)>();
            if (roomBudget > 0)
            {
                if (roomSeeds.Count <= roomBudget) keptRooms.AddRange(roomSeeds);
                else
                    for (int i = 0; i < roomBudget; i++) keptRooms.Add(roomSeeds[i * roomSeeds.Count / roomBudget]);
            }
            keptPriority.AddRange(keptRooms);
            seeds = keptPriority;
        }

        var lines = new Streamline?[seeds.Count];
        var po = new ParallelOptions();
        if (maxDop is int mdop && mdop > 0) po.MaxDegreeOfParallelism = mdop;
        Parallel.For(0, seeds.Count, po, i =>
        {
            var (seed, forwardOnly) = seeds[i];
            float speedSeed = SampleVelocity(field, seed).Length();
            if (speedSeed < 0.02f) return;

            var back = forwardOnly ? new List<(Vector3 p, float speed)>() : TraceHalf(field, planMin, planMax, zLoBound, zHiBound, seed, -1f);
            var fwd = TraceHalf(field, planMin, planMax, zLoBound, zHiBound, seed, 1f);

            var pts = new List<Vector3>(back.Count + 1 + fwd.Count);
            var spd = new List<float>(pts.Capacity);
            for (int k = back.Count - 1; k >= 0; k--) { pts.Add(back[k].p); spd.Add(back[k].speed); }
            pts.Add(seed); spd.Add(speedSeed);
            foreach (var (p, s) in fwd) { pts.Add(p); spd.Add(s); }

            if (pts.Count >= 4)
                lines[i] = new Streamline { Points = pts.ToArray(), Speeds = spd.ToArray() };
        });

        var result = new List<Streamline>();
        foreach (var l in lines) if (l != null) result.Add(l);
        return result;
    }

    static List<(Vector3 p, float speed)> TraceHalf(FlowField field, Vector2 planMin, Vector2 planMax, float zLoBound, float zHiBound, Vector3 seed, float sign)
    {
        var g = field.Grid;
        float ds = 0.5f * g.Dx;
        var result = new List<(Vector3, float)>();
        Vector3 p = seed;

        Vector3 Dir(Vector3 q)
        {
            var v = SampleVelocity(field, q);
            float s = v.Length();
            return s > 1e-9f ? v * (sign / s) : Vector3.Zero;
        }

        for (int step = 0; step < 1500; step++)
        {
            var vHere = SampleVelocity(field, p);
            float speedHere = vHere.Length();
            if (speedHere < 0.02f) break;

            var k1 = vHere * (sign / speedHere);
            var k2 = Dir(p + k1 * (ds * 0.5f));
            var k3 = Dir(p + k2 * (ds * 0.5f));
            var k4 = Dir(p + k3 * ds);
            var next = p + (k1 + 2 * k2 + 2 * k3 + k4) * (ds / 6f);

            if (!InGrid(g, next) || CellIsSolid(g, next) || OutsidePlanMargin(planMin, planMax, next, 3f)) break;
            if (next.Z < zLoBound || next.Z > zHiBound) break;   // exterior lines must not run to the domain edge
            float speedNext = SampleVelocity(field, next).Length();
            if (speedNext < 0.02f) break;

            result.Add((next, speedNext));
            p = next;
        }
        return result;
    }

    static bool InGrid(VoxelGrid g, Vector3 p) =>
        p.X >= g.Origin.X && p.X <= g.Origin.X + g.Nx * g.Dx &&
        p.Y >= g.Origin.Y && p.Y <= g.Origin.Y + g.Ny * g.Dx &&
        p.Z >= g.Origin.Z && p.Z <= g.Origin.Z + g.Nz * g.Dx;

    static bool CellIsSolid(VoxelGrid g, Vector3 p)
    {
        int x = (int)MathF.Floor((p.X - g.Origin.X) / g.Dx);
        int y = (int)MathF.Floor((p.Y - g.Origin.Y) / g.Dx);
        int z = (int)MathF.Floor((p.Z - g.Origin.Z) / g.Dx);
        if (x < 0 || x >= g.Nx || y < 0 || y >= g.Ny || z < 0 || z >= g.Nz) return true;
        return g.Cell[g.Index(x, y, z)] == VoxelGrid.Solid;
    }

    static bool OutsidePlanMargin(Vector2 min, Vector2 max, Vector3 p, float margin) =>
        p.X < min.X - margin || p.X > max.X + margin || p.Y < min.Y - margin || p.Y > max.Y + margin;
}
