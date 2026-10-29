using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

// Ultra checkpoints (session half): what an isothermal LbmSession needs to continue exactly at a batch boundary (or early in the settle).
// Saved: step count, the populations of the current buffer (the second buffer restarts as a copy, as at Start), the supply flux controllers
// (scale, integral, window, perturbation fields), the online age (mask, follow list, field). Not saved because it is a pure function of the step
// or zero at a boundary: wind-turbulence phases, the batch sums and flux sums, the age batch sum, the divergence cadence.
// Refused (CheckpointRefusal): thermal, held temperature, wall model, snapshots, nested ring.
public static partial class LbmSolver
{
    public sealed partial class LbmSession
    {
        const int CkptMagic = 0x57464B31;   // "WFK1"

        /// <summary>Why this session cannot be checkpointed ("" = it can): the stateful features Ultra resume does not serialise.</summary>
        public string CheckpointRefusal =>
            _inert ? "no flow to carry" :
            _thermal ? "thermal" :
            _hybrid ? "hybrid thermal" :
            _hold ? "held indoor temperature" :
            _wmOn ? "the wall model" :
            _snapSteps.Length > 0 ? "snapshots" :
            _nestedRing ? "a nested fine box" : "";

        /// <summary>Samples in the running average window: 0 right after <see cref="ResetAveraging"/>, the state a checkpoint needs.</summary>
        public int AverageSamples => _avgSamples;
        /// <summary>True when the window holds nothing (a batch boundary): the accumulators need no saving.</summary>
        public bool WindowEmpty => _avgSamples == 0 && _ageSamples == 0;
        /// <summary>The step the running average started at.</summary>
        public long AverageFromStep => _averageFromStep;
        /// <summary>Test seam: the current populations (CPU: AoS, GPU: SoA), copied.</summary>
        internal float[] PopulationsNow()
        {
            if (!_useGpu) return (float[])_cur.Clone();
            _acc!.Synchronize();
            return _dCur!.GetAsArray1D();
        }
        internal float[] AgeFieldNow() => _ageOn ? AgeNow() : [];

        /// <summary>A GPU readback that fails (a lost device) is a <see cref="GpuFaultException"/>, so the run's fault policy sees it; <paramref name="ct"/> is checked once per chunk.</summary>
        public void WriteState(BinaryWriter w, CancellationToken ct = default)
        {
            try { WriteStateCore(w, ct); }
            catch (Exception ex) when (_useGpu && IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }
        }

        void WriteStateCore(BinaryWriter w, CancellationToken ct)
        {
            if (CheckpointRefusal is { Length: > 0 } why) throw new InvalidOperationException("resume not supported with " + why);
            w.Write(CkptMagic);
            w.Write(_steps); w.Write(_averageFromStep);
            w.Write(_useGpu); w.Write(_grid.Count);
            long len = (long)_grid.Count * Q;
            w.Write(len);
            if (_useGpu)
            {
                _acc!.Synchronize();
                for (long o = 0; o < len; o += CkptIo.Chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(CkptIo.Chunk, len - o);
                    CkptIo.WriteSpan<float>(w, _dCur!.View.SubView(o, n).GetAsArray1D());
                }
            }
            else CkptIo.WriteSpan<float>(w, new ReadOnlySpan<float>(_cur, 0, (int)len));
            w.Write(_supply.Count);
            foreach (var l in _supply)
            {
                w.Write(l.Source); w.Write(l.Scale); w.Write(l.Integral); w.Write(l.Gain); w.Write(l.Window); w.Write(l.WindowSteps); w.Write(l.Updates);
                CkptIo.Write(w, l.Pert); CkptIo.Write(w, l.PertT1); CkptIo.Write(w, l.PertT2);
            }
            w.Write(_ageOn);
            if (_ageOn)
            {
                CkptIo.Write(w, _ageMask); CkptIo.Write(w, _ageFollow);
                w.Write(_useGpu ? 1 : 0);
                if (_useGpu)
                {
                    w.Write((long)_grid.Count);
                    for (long o = 0; o < _grid.Count; o += CkptIo.Chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(CkptIo.Chunk, _grid.Count - o);
                        CkptIo.WriteSpan<float>(w, _dAgeCur!.View.SubView(o, n).GetAsArray1D());
                    }
                }
                else CkptIo.Write(w, _ageCur);
            }
        }

        /// <summary>Loads what <see cref="WriteState"/> wrote into this freshly started session (same grid, model, wind and options), then restarts the average at that step.</summary>
        public void ReadState(BinaryReader r, CancellationToken ct = default)
        {
            try { ReadStateCore(r, ct); }
            catch (Exception ex) when (_useGpu && IsGpuCallFailure(ex)) { throw new GpuFaultException(ex); }
        }

        void ReadStateCore(BinaryReader r, CancellationToken ct)
        {
            if (CheckpointRefusal is { Length: > 0 } why) throw new InvalidOperationException("resume not supported with " + why);
            if (r.ReadInt32() != CkptMagic) throw new InvalidDataException("not a session state");
            long steps = r.ReadInt64(), from = r.ReadInt64();
            bool gpu = r.ReadBoolean(); int count = r.ReadInt32();
            if (gpu != _useGpu) throw new InvalidDataException(gpu ? "the checkpoint was written on the GPU" : "the checkpoint was written on the CPU");
            if (count != _grid.Count) throw new InvalidDataException("the checkpoint is for another grid");
            long len = r.ReadInt64();
            if (len != (long)count * Q) throw new InvalidDataException("population size differs");
            if (_useGpu)
            {
                for (long o = 0; o < len; o += CkptIo.Chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    var buf = new float[(int)Math.Min(CkptIo.Chunk, len - o)];
                    CkptIo.ReadSpan<float>(r, buf);
                    _dCur!.View.SubView(o, buf.Length).CopyFromCPU(buf);
                }
                _dNxt!.View.CopyFrom(_dCur!.View);
                _acc!.Synchronize();
            }
            else { CkptIo.ReadSpan<float>(r, new Span<float>(_cur, 0, (int)len)); Array.Copy(_cur, _nxt, len); }
            _steps = steps; _averageFromStep = from;
            int nLoops = r.ReadInt32();
            var loops = new List<(int Source, float Scale, float Integral, float Gain, double Window, int WindowSteps, int Updates, float[] P, float[] T1, float[] T2)>();
            for (int i = 0; i < nLoops; i++)
                loops.Add((r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadDouble(), r.ReadInt32(), r.ReadInt32(), CkptIo.ReadFloats(r), CkptIo.ReadFloats(r), CkptIo.ReadFloats(r)));
            if (nLoops != _supply.Count) throw new InvalidDataException("the supply patches differ");
            foreach (var s in loops)
            {
                var l = _supply.Find(x => x.Source == s.Source) ?? throw new InvalidDataException("a supply patch is missing");
                if (l.Pert.Length != s.P.Length) throw new InvalidDataException("a supply patch has another size");
                l.Scale = s.Scale; l.Integral = s.Integral; l.Gain = s.Gain; l.Window = s.Window; l.WindowSteps = s.WindowSteps; l.Updates = s.Updates;
                Array.Copy(s.P, l.Pert, s.P.Length); Array.Copy(s.T1, l.PertT1, s.T1.Length); Array.Copy(s.T2, l.PertT2, s.T2.Length);
                ApplySupplyScale(l);
            }
            if (_useGpu && _supply.Count > 0) _dBVel!.View.CopyFromCPU(_bl.Vel);
            if (r.ReadBoolean())
            {
                var mask = CkptIo.ReadBytes(r); var follow = CkptIo.ReadInts(r);
                int gpuAge = r.ReadInt32(); float[] cur;
                if (gpuAge == 1) { long n = r.ReadInt64(); cur = new float[n]; CkptIo.ReadSpan<float>(r, cur); }
                else cur = CkptIo.ReadFloats(r);
                StartOnlineScalar(cur, mask, null, follow);
            }
            ResetAveraging(false);
        }
    }
}
