namespace WindFlow.Core;

/// <summary>Ultra checkpoints: a FlowField window (the batch means BatchWindowMean carries) to and from a stream. The grid is not stored: the caller supplies the
/// rebuilt one (the checkpoint hash guarantees it is the same). Rooms, openings are stored by Id. Snapshots and held-room records are refused (Ultra has none).</summary>
public static class FlowFieldIo
{
    public static void Write(BinaryWriter w, FlowField f)
    {
        if (f.Snapshots.Count > 0 || f.RoomHolds.Count > 0) throw new InvalidOperationException("resume not supported with snapshots or held rooms");
        CkptIo.Write(w, f.Ux); CkptIo.Write(w, f.Uy); CkptIo.Write(w, f.Uz); CkptIo.Write(w, f.P); CkptIo.Write(w, f.T); CkptIo.Write(w, f.Tu); CkptIo.Write(w, f.Age);
        w.Write(f.AverageSamples); w.Write(f.NonFiniteSkipped); w.Write(f.Steps); w.Write(f.Seconds); w.Write(f.Backend ?? "");
        w.Write(f.RoomFluxes.Count);
        foreach (var x in f.RoomFluxes) { w.Write(x.Room.Id ?? ""); w.Write(x.Inflow); w.Write(x.Outflow); w.Write(x.InternalExchange); w.Write(x.OutdoorInflow); }
        w.Write(f.OpeningFluxes.Count);
        foreach (var x in f.OpeningFluxes) { w.Write(x.Opening.Id ?? ""); w.Write(x.Pos); w.Write(x.Neg); }
        w.Write(f.Supplies.Count);
        foreach (var s in f.Supplies) { w.Write(s.SourceIndex); w.Write(s.SetFlowRate); w.Write(s.FlowRate); w.Write(s.AreaRatio); w.Write(s.Scale); w.Write(s.Gain); w.Write(s.Updates); w.Write(s.Cells); w.Write(s.Links); }
        w.Write(f.Devices.Count);
        foreach (var d in f.Devices)
        {
            w.Write(d.SourceId); w.Write(d.SourceName); w.Write(d.SourceIndex); w.Write((int)d.Kind); w.Write(d.FlowRate); w.Write(d.SetFlowRate);
            w.Write(d.SupplyTemperature); w.Write(d.ReturnTemperature); w.Write(d.HeatRemovedWatts); w.Write(d.Cells);
        }
    }

    public static FlowField Read(BinaryReader r, VoxelGrid grid, BuildingModel model)
    {
        var f = new FlowField { Grid = grid };
        f.Ux = CkptIo.ReadFloats(r); f.Uy = CkptIo.ReadFloats(r); f.Uz = CkptIo.ReadFloats(r); f.P = CkptIo.ReadFloats(r); f.T = CkptIo.ReadFloats(r); f.Tu = CkptIo.ReadFloats(r); f.Age = CkptIo.ReadFloats(r);
        f.AverageSamples = r.ReadInt64(); f.NonFiniteSkipped = r.ReadInt64(); f.Steps = r.ReadInt32(); f.Seconds = r.ReadDouble(); f.Backend = r.ReadString();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string id = r.ReadString();
            f.RoomFluxes.Add(new RoomFlux { Room = model.Rooms.Find(x => x.Id == id) ?? new Room { Id = id }, Inflow = r.ReadDouble(), Outflow = r.ReadDouble(), InternalExchange = r.ReadDouble(), OutdoorInflow = r.ReadDouble() });
        }
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string id = r.ReadString();
            f.OpeningFluxes.Add(new OpeningLinkFlux { Opening = model.Openings.Find(x => x.Id == id) ?? new Opening { Id = id }, Pos = r.ReadDouble(), Neg = r.ReadDouble() });
        }
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
            f.Supplies.Add(new SupplyResult { SourceIndex = r.ReadInt32(), SetFlowRate = r.ReadSingle(), FlowRate = r.ReadSingle(), AreaRatio = r.ReadSingle(), Scale = r.ReadSingle(), Gain = r.ReadSingle(), Updates = r.ReadInt32(), Cells = r.ReadInt32(), Links = r.ReadInt32() });
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
            f.Devices.Add(new DeviceResult { SourceId = r.ReadString(), SourceName = r.ReadString(), SourceIndex = r.ReadInt32(), Kind = (DeviceKind)r.ReadInt32(), FlowRate = r.ReadSingle(), SetFlowRate = r.ReadSingle(), SupplyTemperature = r.ReadSingle(), ReturnTemperature = r.ReadSingle(), HeatRemovedWatts = r.ReadSingle(), Cells = r.ReadInt32() });
        return f;
    }
}
