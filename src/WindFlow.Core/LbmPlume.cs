using System.Numerics;

namespace WindFlow.Core;

public static partial class LbmSolver
{
    // ---- sub-grid plume source model (opt-in: WINDFLOW_PLUME_MODEL=1, read once; default off) --------------------------------------
    // Why: a heat source whose plume the grid cannot resolve (D*/dx < 1, fire-engineering practice) gets numerical entrainment, not the
    // turbulent one (THERMAL_VALIDATION_PLAN section A: sealed filling box 19a reads 1.30 K against 1.8-8.6 K). Jin, Liu & Chen 2015
    // (Building and Environment 85:144-152) recovered measured stratification on a coarse grid by injecting a Morton-Taylor-Turner plume
    // as a source. Here, per non-Surface HeatSource with D*/dx < 1, free-standing only:
    //   * the source's heat (all of its Watts: the solver has no radiation model, so sensible = convective, P_c = Watts) is NOT released
    //     in the box; it is released, with the same total, in the plume cells one layer above the box top (energy conserving by construction);
    //   * those cells are driven toward the plume's vertical velocity w by the existing body-force device mechanism (DeviceForce: relax to
    //     a target velocity, mass neutral), so the volume flux through the layer is V_p(z); the enthalpy P_c / V_p gives dT_p(z).
    // Coefficients (literature only, fixed before any run; none adjusted afterwards):
    //   alpha = 0.12, top-hat entrainment coefficient of a pure round plume (range 0.10-0.16; Carazzo, Kaminski & Tait 2006, JFM 547:137-148
    //           tabulate typical 0.12-0.13; Gaussian/top-hat factor: van Reeuwijk & Craske 2015, JFM 782:333, section 1 and App. B). Lead decision.
    //   MTT top-hat closure (Morton, Taylor & Turner 1956, Proc. R. Soc. A 234:1-23), B = F = g*beta*P/(rho cp) the TOTAL buoyancy flux (no 1/pi), conserved:
    //           V_p = C(alpha) B^(1/3) (z+z_v)^(5/3),  C = (6a/5)(9a/10)^(1/3) pi^(2/3),  b = (6/5) alpha (z+z_v)   [re-derived; matches the plan's A1]
    //   => Jin form V_p = k_v P^(1/3) (z+z_v)^(5/3) with k_v = C (g beta/(rho cp))^(1/3);  dT_p = P/(rho cp V_p) = k_T P^(2/3) (z+z_v)^(-5/3)
    //      with k_T = 1/(rho cp k_v)  (k_v k_T rho cp = 1: the plume carries exactly P_c).
    //   z_v = 0, z measured from the source's top face (as NFPA 92B omits the virtual origin). Heskestad's z0 (Fire Safety J. 5:109-114, 1983) is a
    //           flaming pool-fire correlation on total heat release and is not justified for a < 1 kW heater. Upgrade path: the lazy-plume virtual
    //           origin of van den Bremer & Hunt 2010 (JFM 644:165). Lead decision.
    //   D* = (P/(rho cp T_inf sqrt(g)))^(2/5) (FDS User's Guide 6.3.6 / Heskestad); gate D*/dx < 1.
    //   rho cp = 1.2 x 1005 J/m3K, T_inf = outdoor T (the solver's buoyancy reference), g = 9.81.
    // Limits (ponytail): the layer has N whole cells, N dx^2 >= pi b^2, so w = V_p/(N dx^2) keeps flux and enthalpy, not momentum flux (low by
    // pi b^2/(N dx^2)); wall-attached sources (a solid cell beside the box) are excluded (no wall-plume relation used); the force is not ramped with
    // the heat; a cross-flow does not bend the plume; the device path is shared with the GPU kernels but only the CPU path was run.
    internal static bool PlumeModel = System.Environment.GetEnvironmentVariable("WINDFLOW_PLUME_MODEL") == "1";
    const float PlumeAlpha = 0.12f;

    internal readonly record struct PlumeState(float Zv, float Vp, float B, float DeltaT, float Kv, float Kt);

    internal sealed class PlumeSource
    {
        public HeatSource H = new();
        public float Watts, DStar, Ratio, Vp, B, W, DeltaT, Q;   // Q: K/s per injection cell
        public bool Applied;
        public string Note = "";
        public int[] Cells = [];
    }

    /// <summary>D* in metres for a heat release of <paramref name="watts"/> into air at <paramref name="tAmbK"/>.</summary>
    internal static float PlumeDStar(float watts, float tAmbK) => MathF.Pow(watts / (RhoCp * tAmbK * MathF.Sqrt(GAccel)), 0.4f);

    /// <summary>MTT plume of power <paramref name="watts"/> at height z above the source's top face.</summary>
    internal static PlumeState PlumeAt(float watts, float z, float tAmbK, float alpha = PlumeAlpha)
    {
        const float zv = 0f;   // see the header: no virtual origin
        float zz = z + zv;
        if (zz <= 0f) return new PlumeState(zv, 0f, 0f, 0f, 0f, 0f);
        float c = 1.2f * alpha * MathF.Cbrt(0.9f * alpha) * MathF.Pow(MathF.PI, 2f / 3f);
        float kv = c * MathF.Cbrt(GAccel / (tAmbK * RhoCp));
        float vp = kv * MathF.Pow(watts, 1f / 3f) * MathF.Pow(zz, 5f / 3f);
        return new PlumeState(zv, vp, 1.2f * alpha * zz, watts / (RhoCp * vp), kv, 1f / (RhoCp * kv));
    }

    /// <summary>Per non-Surface HeatSource the plume plan (gate, cells, velocity, heat rate) or the reason it stays the volumetric box. The per-source line goes to grid.Warnings once.</summary>
    internal static Dictionary<HeatSource, PlumeSource> PlumeSources(VoxelGrid grid, BuildingModel model)
    {
        var res = new Dictionary<HeatSource, PlumeSource>();
        float tAmbK = model.Env.OutdoorTemperature + 273.15f, dx = grid.Dx;
        void Done(PlumeSource p, string text)
        {
            p.Note = text;
            if (!grid.Warnings.Contains(text)) grid.Warnings.Add(text);
        }
        foreach (var h in model.Heat)
        {
            if (!h.Enabled || h.Kind == HeatKind.Surface) continue;
            float watts = h.Kind == HeatKind.People && h.Watts <= 0f ? PeopleWatts * h.People : h.Watts;
            if (watts <= 0f) continue;
            var box = BoxCells(grid, h.Center, h.Width, h.Depth, h.Height);
            if (box.Count == 0) continue;
            var ps = new PlumeSource { H = h, Watts = watts, DStar = PlumeDStar(watts, tAmbK) };
            ps.Ratio = ps.DStar / dx;
            res[h] = ps;
            string head = $"Plume model: {h.Name} {watts:0} W, D*={ps.DStar:0.000} m, D*/dx={ps.Ratio:0.000}";
            if (ps.Ratio >= 1f) { Done(ps, head + ": resolved plume, left as the volumetric source."); continue; }
            int plane = grid.Nx * grid.Ny, layer = box.Max(i => i / plane) + 1;
            bool wall = false;
            foreach (int i in box)
            {
                int x = i % grid.Nx, y = i / grid.Nx % grid.Ny;
                if ((x > 0 && grid.Cell[i - 1] == VoxelGrid.Solid) || (x < grid.Nx - 1 && grid.Cell[i + 1] == VoxelGrid.Solid)
                    || (y > 0 && grid.Cell[i - grid.Nx] == VoxelGrid.Solid) || (y < grid.Ny - 1 && grid.Cell[i + grid.Nx] == VoxelGrid.Solid)) { wall = true; break; }
            }
            if (wall) { Done(ps, head + ": wall-attached, no wall-plume relation applied, left as the volumetric source."); continue; }
            if (layer >= grid.Nz) { Done(ps, head + ": no cell layer above the box, left as the volumetric source."); continue; }
            float boxTop = h.Center.Z + 0.5f * h.Height, z = MathF.Max(grid.Origin.Z + (layer + 0.5f) * dx - boxTop, 0f);
            var st = PlumeAt(watts, z, tAmbK);
            if (st.Vp <= 0f) { Done(ps, head + ": plume origin at the injection plane, left as the volumetric source."); continue; }
            var cells = new List<int>();
            int rr = (int)MathF.Ceiling(st.B / dx) + 1;
            int cx = (int)MathF.Floor((h.Center.X - grid.Origin.X) / dx), cy = (int)MathF.Floor((h.Center.Y - grid.Origin.Y) / dx);
            int best = -1; float bestD = float.MaxValue;
            for (int y = Math.Max(0, cy - rr); y <= Math.Min(grid.Ny - 1, cy + rr); y++)
                for (int x = Math.Max(0, cx - rr); x <= Math.Min(grid.Nx - 1, cx + rr); x++)
                {
                    int i = grid.Index(x, y, layer);
                    var c = grid.CellCenter(x, y, layer);
                    float d = MathF.Sqrt((c.X - h.Center.X) * (c.X - h.Center.X) + (c.Y - h.Center.Y) * (c.Y - h.Center.Y));
                    if (d < bestD) { bestD = d; best = i; }
                    if (d <= st.B && grid.Cell[i] == VoxelGrid.Air) cells.Add(i);
                }
            if (cells.Count == 0 && best >= 0 && grid.Cell[best] == VoxelGrid.Air) cells.Add(best);
            if (cells.Count == 0) { Done(ps, head + ": injection cells are not air, left as the volumetric source."); continue; }
            ps.Cells = cells.ToArray();
            ps.Vp = st.Vp; ps.B = st.B; ps.DeltaT = st.DeltaT;
            ps.W = st.Vp / (cells.Count * dx * dx);
            ps.Q = watts / (RhoCp * cells.Count * dx * dx * dx);
            ps.Applied = true;
            Done(ps, head + $": applied at z={z:0.00} m above the box (z_v=0): V_p={st.Vp:0.000} m3/s, b={st.B:0.00} m, dT_p={st.DeltaT:0.0} K, {cells.Count} cells, w={ps.W:0.00} m/s.");
        }
        return res;
    }

    /// <summary>(cell, target w m/s) for every applied plume source: the cells BuildDeviceData drives with the device force. Null when the model is off.</summary>
    internal static List<(int Cell, float W)>? PlumeDriveCells(VoxelGrid grid, BuildingModel model)
    {
        if (!PlumeModel) return null;
        var list = new List<(int, float)>();
        foreach (var ps in PlumeSources(grid, model).Values) if (ps.Applied) foreach (int i in ps.Cells) list.Add((i, ps.W));
        return list;
    }
}
