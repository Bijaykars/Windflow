using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

public static partial class SelfCheck
{
    /// <summary>Sub-grid plume source model (WINDFLOW_PLUME_MODEL, LbmPlume.cs): D* arithmetic, the D*/dx gate, and the energy and flux bookkeeping of the injection on a tiny grid.</summary>
    static void Checks_Plume(List<(string Name, bool Ok, string Detail)> feature)
    {
        // D* = (P/(rho cp T sqrt(g)))^(2/5): 500 W at 20 C = 0.04591 m (hand calculation, 5 digits).
        float d = LbmSolver.PlumeDStar(500f, 293.15f);
        feature.Add(("plume model: D* of 500 W at 20 C is 0.04591 m within 0.2 %", MathF.Abs(d - 0.04591f) < 0.04591f * 0.002f, $"D*={d:0.00000} m"));

        var model = new BuildingModel();
        model.Heat.Add(new HeatSource { Id = "h", Name = "heater", Kind = HeatKind.Equipment, Center = new Vector3(0.6f, 0.6f, 0.2f), Width = 0.4f, Depth = 0.4f, Height = 0.2f, Watts = 500f });
        VoxelGrid Grid(float dx, int n) => new() { Nx = n, Ny = n, Nz = n, Dx = dx, Origin = Vector3.Zero, Cell = new byte[n * n * n] };
        var coarse = LbmSolver.PlumeSources(Grid(0.1f, 12), model).Values.Single();      // D*/dx 0.46: applied
        var fine = LbmSolver.PlumeSources(Grid(0.04f, 30), model).Values.Single();       // D*/dx 1.15: resolved, left alone
        feature.Add(("plume model gate: D*/dx 0.46 applies the plume, D*/dx 1.15 leaves the volumetric source",
            coarse.Applied && coarse.Ratio < 1f && !fine.Applied && fine.Ratio >= 1f && fine.Cells.Length == 0,
            $"dx 0.10: ratio {coarse.Ratio:0.00} applied={coarse.Applied}; dx 0.04: ratio {fine.Ratio:0.00} applied={fine.Applied}"));

        // Energy: the injected heat (cells x K/s x rho cp x cell volume) is the source's power, and the volume flux through the cells is V_p.
        const float dx0 = 0.1f;
        double watts = coarse.Cells.Length * (double)coarse.Q * 1.2 * 1005 * dx0 * dx0 * dx0, flux = coarse.Cells.Length * (double)coarse.W * dx0 * dx0;
        var st = LbmSolver.PlumeAt(500f, 0.15f, 293.15f);
        bool enthalpy = Math.Abs(st.Vp * st.DeltaT * 1.2 * 1005 - 500) < 5;   // k_v k_T rho cp = 1
        feature.Add(("plume model energy: injected heat equals the source power within 1 %, the layer's flux is V_p, and V_p dT_p rho cp = P",
            Math.Abs(watts - 500) < 5 && Math.Abs(flux - coarse.Vp) < 0.01 * coarse.Vp && enthalpy,
            $"heat {watts:0.0} W of 500, flux {flux:0.0000} of {coarse.Vp:0.0000} m3/s, V_p dT_p rho cp = {st.Vp * st.DeltaT * 1.2 * 1005:0.0} W"));
    }
}
