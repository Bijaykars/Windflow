namespace WindFlow.Core;

/// <summary>Combine means on the same fixed grid with their actual sample counts. Mutates the first
/// field, which must no longer be in use by another consumer. Geometry and prescribed settings stay fixed.</summary>
public static class FlowFieldMean
{
    /// <summary>RT-09: the prefix of the note a run carries when <see cref="Add"/> skipped non-finite samples.</summary>
    public const string NonFiniteTag="Averaging: ";
    public static void Add(FlowField mean, FlowField next)
    {
        if (mean.Grid.Nx != next.Grid.Nx || mean.Grid.Ny != next.Grid.Ny || mean.Grid.Nz != next.Grid.Nz ||
            mean.Grid.Dx != next.Grid.Dx || mean.Grid.Origin != next.Grid.Origin)
            throw new ArgumentException("Cannot average fields on different grids");
        long a=Math.Max(1,mean.AverageSamples),b=Math.Max(1,next.AverageSamples);
        double w=(double)b/(a+b);
        float Blend(float x,float y)=>(float)(x+(y-x)*w);
        long skipped=0;
        void ArrayMean(float[] x,float[] y)
        {
            if(x.Length!=y.Length) throw new ArgumentException("Cannot average different field components");
            Missed.TryGetValue(x,out var miss);   // RW-03: per cell, the weight (in the units of a and b) of the samples this cell is missing
            for(int i=0;i<x.Length;i++)
            {
                // RT-09: a non-finite sample would poison the cell's mean for good; skip it (the running value stays), count it, flag it below
                if(float.IsNaN(y[i])&&float.IsNaN(x[i])) continue;   // V2: NaN on both sides is "no value" (Age outside its mask), not a bad sample; the cell stays NaN
                if(!float.IsFinite(y[i])){skipped++;miss??=Missed.GetValue(x,k=>new double[k.Length]);miss[i]+=b;continue;}
                // RW-03: the cell's own valid weight is a - missed, so its mean is the mean of the samples it has (w_i = b / (valid + b))
                double wi=miss==null||miss[i]==0?w:b/(Math.Max(0,a-miss[i])+b);
                x[i]=float.IsFinite(x[i])?(float)(x[i]+(y[i]-x[i])*wi):y[i];
            }
        }
        ArrayMean(mean.Ux,next.Ux);ArrayMean(mean.Uy,next.Uy);ArrayMean(mean.Uz,next.Uz);
        ArrayMean(mean.P,next.P);ArrayMean(mean.T,next.T);ArrayMean(mean.Tu,next.Tu);
        if(mean.Age.Length==next.Age.Length) ArrayMean(mean.Age,next.Age); else mean.Age=[];   // 6k: a batch without the online age drops it from the mean
        if(mean.RoomFluxes.Count!=next.RoomFluxes.Count) mean.RoomFluxes.Clear();
        else for(int i=0;i<mean.RoomFluxes.Count;i++)
        {
            var x=mean.RoomFluxes[i];var y=next.RoomFluxes[i];
            if(x.Room.Id!=y.Room.Id) throw new ArgumentException("Room identities changed during averaging");
            x.Inflow+=(y.Inflow-x.Inflow)*w;x.Outflow+=(y.Outflow-x.Outflow)*w;x.InternalExchange+=(y.InternalExchange-x.InternalExchange)*w;x.OutdoorInflow+=(y.OutdoorInflow-x.OutdoorInflow)*w;
        }
        if(mean.OpeningFluxes.Count!=next.OpeningFluxes.Count) mean.OpeningFluxes.Clear();
        else for(int i=0;i<mean.OpeningFluxes.Count;i++)
        {
            var x=mean.OpeningFluxes[i];var y=next.OpeningFluxes[i];
            if(x.Opening.Id!=y.Opening.Id) throw new ArgumentException("Opening identities changed during averaging");
            x.Pos+=(y.Pos-x.Pos)*w;x.Neg+=(y.Neg-x.Neg)*w;
        }
        foreach(var x in mean.RoomHolds)
        {
            var y=next.RoomHolds.FirstOrDefault(y=>ReferenceEquals(y.Room,x.Room)||(x.Room.Id.Length>0&&y.Room.Id==x.Room.Id));if(y==null) continue;
            x.DutyWatts+=(y.DutyWatts-x.DutyWatts)*w;
        }
        foreach(var x in mean.Supplies)
        {
            var y=next.Supplies.FirstOrDefault(y=>y.SourceIndex==x.SourceIndex);if(y==null) continue;
            x.FlowRate=Blend(x.FlowRate,y.FlowRate);x.Scale=y.Scale;x.Gain=y.Gain;x.Updates=y.Updates;
        }
        foreach(var x in mean.Devices)
        {
            var y=next.Devices.FirstOrDefault(y=>y.SourceIndex==x.SourceIndex);if(y==null) continue;
            x.FlowRate=Blend(x.FlowRate,y.FlowRate);x.HeatRemovedWatts=Blend(x.HeatRemovedWatts,y.HeatRemovedWatts);
            x.ReturnTemperature=Blend(x.ReturnTemperature,y.ReturnTemperature);
        }
        mean.NonFiniteSkipped+=next.NonFiniteSkipped+skipped;
        if(skipped>0) Flag(mean);
        mean.AverageSamples=a+b;mean.Steps=next.Steps;mean.Seconds=next.Seconds;mean.Snapshots=next.Snapshots;
    }

    /// <summary>A copy that <see cref="Add"/> can mutate without touching <paramref name="f"/>: the arrays and the mutable per-room/opening/device records are copied
    /// (the Grid and the snapshots are shared; Add never writes them). For the group-noise seam, which averages the same batches into a second mean.</summary>
    internal static FlowField Clone(FlowField f)
    {
        // ponytail: reflection MemberwiseClone keeps this in step with the record classes; fine for a once-per-group seam copy
        static T Shallow<T>(T x) where T : class => (T)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(x, null)!;
        var c = Shallow(f);
        c.Ux=(float[])f.Ux.Clone();c.Uy=(float[])f.Uy.Clone();c.Uz=(float[])f.Uz.Clone();c.P=(float[])f.P.Clone();c.T=(float[])f.T.Clone();c.Tu=(float[])f.Tu.Clone();c.Age=(float[])f.Age.Clone();
        c.Snapshots=new(f.Snapshots);
        c.RoomFluxes=f.RoomFluxes.Select(Shallow).ToList();c.OpeningFluxes=f.OpeningFluxes.Select(Shallow).ToList();c.RoomHolds=f.RoomHolds.Select(Shallow).ToList();
        c.Supplies=f.Supplies.Select(Shallow).ToList();c.Devices=f.Devices.Select(Shallow).ToList();
        return c;
    }

    // RW-03: per component array, the weight each cell is missing (allocated on the first skip); keyed by the array so it follows the mean in place
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<float[],double[]> Missed=new();

    /// <summary>RW-03: a window seeded with <paramref name="seed"/> (one batch, weight <paramref name="unit"/>): counts and flags its non-finite values, and records them
    /// as missing so the first valid sample takes the cell at full weight.</summary>
    internal static void Seed(FlowField seed,double unit)
    {
        long n=0;
        void Scan(float[] x,bool infOnly=false)
        {
            double[]? miss=null;
            for(int i=0;i<x.Length;i++) if(!float.IsFinite(x[i])&&!(infOnly&&float.IsNaN(x[i]))){n++;miss??=Missed.GetValue(x,k=>new double[k.Length]);miss[i]+=unit;}
        }
        Scan(seed.Ux);Scan(seed.Uy);Scan(seed.Uz);Scan(seed.P);Scan(seed.T);Scan(seed.Tu);Scan(seed.Age,true);   // V2: Age is NaN by design outside the age mask; only +-Inf counts
        if(n==0) return;
        seed.NonFiniteSkipped+=n;
        Flag(seed);
    }

    static void Flag(FlowField f)
    {
        var affected=new bool[f.Grid.Count];int cells=0;
        foreach(var x in new[]{f.Ux,f.Uy,f.Uz,f.P,f.T,f.Tu,f.Age})
            if(Missed.TryGetValue(x,out var miss)) for(int i=0;i<miss.Length&&i<affected.Length;i++) if(miss[i]>0&&!affected[i]){affected[i]=true;cells++;}
        var warnings=f.Grid.Warnings;warnings.RemoveAll(w=>w.StartsWith(NonFiniteTag,StringComparison.Ordinal));
        warnings.Add($"{NonFiniteTag}{f.NonFiniteSkipped} non-finite (NaN or infinite) samples were left out of the time average; the mean is biased or missing in {cells} cells");
    }
}

/// <summary>Equal-weight mean (each batch counts once, whatever its sample count) of a run's batch fields over batches (d, n], d = <see cref="ConvergenceMonitor.TruncationFor"/>(n):
/// the same batches the <see cref="ConvergenceMonitor"/> keeps, so the landed field's room ACH is the one it bounded. Feed every batch
/// field in order with <see cref="Add"/>. Because d only ever takes the values 0, 1, 2, 4, 8, ..., two running means are enough: the
/// current window (d, n] and the next one (2d, n], which starts at batch 2d+1 and replaces the current one when n reaches 2(2d).
/// Only <see cref="FlowFieldMean.Add"/> does arithmetic, so there is no second copy of the per-array code and no field is cloned.
/// <para>Add mutates (and keeps) the fields it is given: pass each batch field once and do not use it afterwards.
/// Use one instance for the coarse field and one for a nested fine field.</para></summary>
public sealed class BatchWindowMean
{
    FlowField? _cur, _next;
    int _n, _curD, _nextD = 1, _curK, _nextK;       // K: batches in the window
    long _curS, _nextS;                              // S: their real sample total

    /// <summary>Batches added so far.</summary>
    public int Batches => _n;

    /// <summary>Ultra checkpoint: the window state (both running means with their counters).</summary>
    internal void WriteState(BinaryWriter w)
    {
        w.Write(_n); w.Write(_curD); w.Write(_nextD); w.Write(_curK); w.Write(_nextK); w.Write(_curS); w.Write(_nextS);
        w.Write(_cur != null); if (_cur != null) FlowFieldIo.Write(w, _cur);
        w.Write(_next != null); if (_next != null) FlowFieldIo.Write(w, _next);
    }

    internal void ReadState(BinaryReader r, VoxelGrid grid, BuildingModel model)
    {
        _n = r.ReadInt32(); _curD = r.ReadInt32(); _nextD = r.ReadInt32(); _curK = r.ReadInt32(); _nextK = r.ReadInt32(); _curS = r.ReadInt64(); _nextS = r.ReadInt64();
        _cur = r.ReadBoolean() ? FlowFieldIo.Read(r, grid, model) : null;
        _next = r.ReadBoolean() ? FlowFieldIo.Read(r, grid, model) : null;
    }

    /// <summary>Leading batches excluded from <see cref="Mean"/>.</summary>
    public int Dropped => _fixedDrop >= 0 ? Math.Min(_fixedDrop, _n) : ConvergenceMonitor.TruncationFor(_n);

    /// <summary>WINDFLOW_FIXED_BATCHES / group-noise seam: a non-negative value drops exactly that many leading batches and keeps every later one in one
    /// window (no power-of-two truncation); 0 keeps all. The default (-1) is the normal rule, unchanged.</summary>
    public BatchWindowMean(int fixedDrop = -1) { _fixedDrop = fixedDrop; if (fixedDrop >= 0) { _curD = fixedDrop; _nextD = int.MaxValue; } }
    readonly int _fixedDrop;

    /// <summary>The window's running mean whatever its length (null before its first batch); <see cref="Mean"/> needs two batches.</summary>
    internal FlowField? Window => _cur;

    /// <summary>The mean over batches (Dropped, Batches]; null with fewer than 2 batches, so the caller keeps its partial-window fallback.</summary>
    public FlowField? Mean => _n < 2 ? null : _cur;

    public void Add(FlowField batch)
    {
        _n++;
        // Every batch weighs the same (batch means are unweighted, like the monitor's ACH mean): FlowFieldMean.Add weights by
        // AverageSamples, so each window carries its batch count while blending and its true sample total afterwards.
        long samples = batch.AverageSamples;
        batch.AverageSamples = 1;
        if (_cur != null) { _cur.AverageSamples = _curK; FlowFieldMean.Add(_cur, batch); _curK++; _curS += samples; _cur.AverageSamples = _curS; }
        if (_next != null) { _next.AverageSamples = _nextK; FlowFieldMean.Add(_next, batch); _nextK++; _nextS += samples; _next.AverageSamples = _nextS; }
        batch.AverageSamples = samples;
        if (_cur == null && _n == _curD + 1) { _cur = batch; _curK = 1; _curS = samples; FlowFieldMean.Seed(batch, 1); }   // RW-03: a window's first batch is counted and flagged too (weights in batch units)
        else if (_next == null && _n == _nextD + 1) { _next = batch; _nextK = 1; _nextS = samples; FlowFieldMean.Seed(batch, 1); }
        if (_fixedDrop < 0 && ConvergenceMonitor.TruncationFor(_n) > _curD) { _cur = _next; _curK = _nextK; _curS = _nextS; _curD = _nextD; _next = null; _nextD = 2 * _curD; }
    }
}
