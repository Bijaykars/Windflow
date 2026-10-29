using ILGPU;
using ILGPU.Runtime;

namespace WindFlow.Core;

/// <summary>6j (P14-01): one room's ideal heater/cooler on a held-temperature thermal run (<see cref="RunOptions.HoldIndoorTemperature"/>).</summary>
public sealed class RoomHold
{
    public Room Room = new();
    /// <summary>Time-mean duty over the averaging window, W; positive = heating, negative = cooling.</summary>
    public double DutyWatts;
    /// <summary>The set point, °C (<see cref="Environment.IndoorTemperature"/>).</summary>
    public float SetPoint;
}

public static partial class LbmSolver
{
    // 6j (P14-01): held indoor temperature. Each room with an outline gets an ideal heater/cooler: a uniform volumetric source over its air
    // cells (the Cell-mask room mask with its band, Analysis.RoomMasks), Q = PI(T_set - T_mean) with T_mean the room's VOLUME mean over those same cells
    // (S2: the ideal-loads energy balance; sensing the occupied zone with a uniform source over-heated a stratified room's upper volume to
    // 28.8 C at 3x the duty), reduced every HoldCadence steps (CPU and
    // GPU alike; the GPU gathers the cells' T as UpdateDeviceUnits does). A room holding a conditioning device's outlet (a device with its own
    // supply temperature) is not held: the device is its conditioning (addendum). The plant is C dT/dt = Q - losses, C = rho c_p V;
    // with Kp = C / tau and Ki = Kp / (4 tau) the closed loop is critically damped with time constant tau = HoldFlowThroughs room
    // flow-throughs (the room's longest plan side over the run's velocity scale). Anti-windup: |Q| is capped at HoldMaxFactor C dT_ref / tau
    // and the integral stops while the cap binds. Air outside every room is not controlled; nor are cells in the absorbing band.
    const int HoldCadence = 50;
    const float HoldFlowThroughs = 3f;
    const float HoldMaxFactor = 10f;

    public sealed partial class LbmSession
    {
        int _cdHoldPlane = -1, _cdHoldTable, _cdSurfTable;   // cellData offsets (BuildCellData)

        /// <summary>RT-07: per room, the air cells of its mask outside the ring and open planes (null = a device room, not held). Valid while the
        /// geometry and rooms are; a wind change only moves the absorbing band, which BuildHold filters on top.</summary>
        Dictionary<Room, int[]?> _holdBase = [];

        /// <summary>Rebuilds the controlled rooms and their cells (start, UpdateCells: <paramref name="reuseMasks"/> false; a ring change, which only
        /// moves the absorbing band: true). A room that was controlled before keeps its PI state.</summary>
        void BuildHold(bool reuseMasks = false)
        {
            if (!reuseMasks) _holdBase = [];
            var grid = _grid; int count = grid.Count, Nx = grid.Nx, Ny = grid.Ny, Nz = grid.Nz;
            var prevRooms = _holdRooms; var prevInt = _holdInt; var prevW = _holdWatts; var prevSum = _holdDutySum;
            var rooms = new List<Room>(); var cells = new List<int[]>(); var measure = new List<int[]>();
            _holdRoomOf = _hold ? new int[count] : [];
            bool[][]? masks = null;
            if (_hold)
            {
                Array.Fill(_holdRoomOf, -1);
                foreach (var room in _model.Rooms)
                {
                    if (room.Loops.Count == 0) continue;
                    if (!_holdBase.TryGetValue(room, out var baseCells))
                    {
                        masks ??= Analysis.RoomMasks(_source, _model, out _);   // OF-1: outline plus band, the same cells the room's ACH and means use
                        var mask = masks[_model.Rooms.IndexOf(room)];
                        var device = grid.Devices.FirstOrDefault(d => !float.IsNaN(d.SupplyTemperature) && d.OutletCount > 0
                            && d.Cells.Take(d.OutletCount).Count(c => mask[c]) * 2 > d.OutletCount);
                        if (device != null)
                        {
                            string note = $"{room.Name} is conditioned by its {device.SourceName}; the ideal hold is off there.";
                            if (!grid.Warnings.Contains(note)) grid.Warnings.Add(note);
                            baseCells = null;
                        }
                        else
                        {
                            var all = new List<int>();
                            for (int i = 0; i < count; i++)
                            {
                                if (!mask[i] || grid.Cell[i] != VoxelGrid.Air) continue;
                                int x = i % Nx, y = i / Nx % Ny, z = i / (Nx * Ny);
                                if (x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || z == 0 && grid.OpenZMin || z == Nz - 1 && grid.OpenZMax) continue;   // ring / open planes
                                all.Add(i);
                            }
                            baseCells = all.ToArray();
                        }
                        _holdBase[room] = baseCells;
                    }
                    if (baseCells == null) continue;
                    var list = new List<int>();
                    foreach (int i in baseCells)
                    {
                        if (_holdRoomOf[i] >= 0 || InSponge(i % Nx, i / Nx % Ny, Nx, Ny, _spongeFaces, _spongeCells)) continue;
                        list.Add(i);
                    }
                    if (list.Count == 0) continue;
                    foreach (int i in list) _holdRoomOf[i] = rooms.Count;
                    rooms.Add(room); cells.Add(list.ToArray());
                    measure.Add(cells[^1]);   // S2: the controller senses the room VOLUME mean (the heated cells themselves)
                }
            }
            int n = rooms.Count;
            _holdRooms = rooms.ToArray(); _holdCells = cells.ToArray(); _holdMeasure = measure.ToArray();
            _holdQ = new float[n]; _holdWatts = new double[n]; _holdInt = new double[n]; _holdKp = new double[n]; _holdTau = new double[n];
            _holdMax = new double[n]; _holdDutySum = new double[n];
            float cellV = _dx * _dx * _dx, u = MathF.Max(_velScale * ULat, 1e-6f);
            float dRef = MathF.Max(MathF.Abs(_holdSet - _tRef), 5f);
            for (int r = 0; r < n; r++)
            {
                float xmin = float.MaxValue, xmax = float.MinValue, ymin = float.MaxValue, ymax = float.MinValue;
                foreach (var loop in rooms[r].Loops)
                    foreach (var p in loop) { xmin = MathF.Min(xmin, p.X); xmax = MathF.Max(xmax, p.X); ymin = MathF.Min(ymin, p.Y); ymax = MathF.Max(ymax, p.Y); }
                double c = (double)RhoCp * cells[r].Length * cellV;
                double tau = HoldFlowThroughs * MathF.Max(MathF.Max(xmax - xmin, ymax - ymin), _dx) / u;
                _holdTau[r] = tau; _holdKp[r] = c / tau; _holdMax[r] = HoldMaxFactor * c * dRef / tau;
                int k = Array.IndexOf(prevRooms, rooms[r]);
                if (k >= 0) { _holdInt[r] = prevInt[k]; _holdWatts[r] = prevW[k]; _holdDutySum[r] = prevSum[k]; }
                _holdQ[r] = (float)(_holdWatts[r] * _dt / (c));
            }
            _holdAll = measure.SelectMany(a => a).ToArray();
            _holdT = new float[_holdAll.Length];
        }

        /// <summary>(Re)allocates the device copy of the controlled cells' indices and their T (GPU runs).</summary>
        void AllocateHoldGpu()
        {
            _dHoldAll?.Dispose(); _dHoldT?.Dispose(); _dHoldAll = null; _dHoldT = null;
            if (_holdAll.Length == 0) return;
            var accel = _acc!;
            _dHoldAll = accel.Allocate1D(_holdAll);
            _dHoldT = accel.Allocate1D<float>(_holdAll.Length);
        }

        /// <summary>One controller update: the room means, then Q per room (W) and the per-step source the flow pass adds (K per cell).
        /// <paramref name="dtc"/> = seconds since the last update (0 at the start: no integration).</summary>
        void UpdateHold(float dtc)
        {
            int n = _holdRooms.Length;
            if (n == 0) return;
            int count = _grid.Count;
            if (_useGpu)
            {
                if (_hybrid) _hybridGatherTKernel!(_holdAll.Length, _dHoldAll!.View, _dTCur!.View, _dHoldT!.View);
                else _gatherTemperatureKernel!(_holdAll.Length, _dHoldAll!.View, _dCurG!.View, count, _dHoldT!.View);
                _acc!.Synchronize();
                _dHoldT!.View.CopyToCPU(_holdT);
            }
            else
            {
                var all = _holdAll; var t = _holdT; var tc = _tCur; var g = _curG; bool hyb = _hybrid;
                Parallel.For(0, all.Length, k =>
                {
                    int b = all[k] * Q7;
                    t[k] = hyb ? tc[all[k]] : g[b] + g[b + 1] + g[b + 2] + g[b + 3] + g[b + 4] + g[b + 5] + g[b + 6];
                });
            }
            float cellV = _dx * _dx * _dx;
            for (int r = 0, k = 0; r < n; r++)
            {
                int len = _holdCells[r].Length, lenM = _holdMeasure[r].Length;
                double sum = 0;
                for (int j = 0; j < lenM; j++) sum += _holdT[k + j];
                k += lenM;
                double e = _holdSet - sum / lenM;
                double integral = _holdInt[r] + e * dtc;
                double q = _holdKp[r] * (e + integral / (4.0 * _holdTau[r]));
                if (Math.Abs(q) > _holdMax[r]) q = Math.Clamp(q, -_holdMax[r], _holdMax[r]);   // anti-windup: the integral holds while capped
                else _holdInt[r] = integral;
                _holdWatts[r] = q;
                _holdQ[r] = (float)(q * _dt / ((double)RhoCp * len * cellV));
            }
            if (_useGpu) _dCellData!.View.SubView(_cdHoldTable, n).CopyFromCPU(_holdQ);
        }

        /// <summary>Per step: the controller cadence, and the duty sum over the averaging window.</summary>
        void HoldStep(int step, bool accumulate)
        {
            if (_holdRooms.Length == 0) return;
            if (step > 0 && step % HoldCadence == 0) UpdateHold(HoldCadence * _dt);
            if (!accumulate) return;
            for (int r = 0; r < _holdWatts.Length; r++) _holdDutySum[r] += _holdWatts[r];
            _holdDutySteps++;
        }

        /// <summary>The time-mean duty of every controlled room (the current duty before any averaged step).</summary>
        void ReadHold(FlowField field)
        {
            for (int r = 0; r < _holdRooms.Length; r++)
                field.RoomHolds.Add(new RoomHold
                {
                    Room = _holdRooms[r], SetPoint = _holdSet,
                    DutyWatts = _holdDutySteps > 0 ? _holdDutySum[r] / _holdDutySteps : _holdWatts[r],
                });
        }

        /// <summary>A ring change moves the absorbing band: rebuild the controlled cells and, on the GPU, the cellData copy. RT-07: nothing to do
        /// when the band did not move (<paramref name="bandMoved"/>), and the rooms' cell lists are kept when it did.</summary>
        void RefreshHoldAfterRing(bool bandMoved)
        {
            if (!_hold || _inert || !bandMoved) return;
            BuildHold(reuseMasks: true);
            if (_useGpu && _dCellData != null)
            {
                _dCellData.Dispose();
                _dCellData = _acc!.Allocate1D(BuildCellData());
                AllocateHoldGpu();
            }
            UpdateHold(0f);
        }
    }
}
