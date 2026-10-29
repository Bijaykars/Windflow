using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App;

public static partial class SelfCheck
{
    /// <summary>Portal step 1: through-opening exchange, net outdoor inflow and the unexplained share, from crafted opening-plane fluxes of a two-room house.</summary>
    static void Checks_Portal(List<(string Name, bool Ok, string Detail)> feature)
    {
        // Room A sits on the -Normal side of an exterior door E and a window W and of the interior door I; room B is on I's +Normal side.
        var e = new Analysis.PortalOpening(Pos: 0.30, Neg: 0.50, Side: -1, Exterior: true);   // 0.50 in, 0.30 out
        var w = new Analysis.PortalOpening(Pos: 0.40, Neg: 0.10, Side: -1, Exterior: true);   // 0.10 in, 0.40 out: net outflow, floored to 0
        var i = new Analysis.PortalOpening(Pos: 0.20, Neg: 0.05, Side: +1, Exterior: false);  // seen from B: 0.20 in
        var iA = i with { Side = -1 };                                                       // seen from A: 0.05 in
        Analysis.PortalSums([e, w, iA], out double thA, out double netA);
        Analysis.PortalSums([i], out double thB, out double netB);
        bool sums = Math.Abs(thA - 0.65) < 1e-9 && Math.Abs(netA - 0.20) < 1e-9 && Math.Abs(thB - 0.20) < 1e-9 && Math.Abs(netB) < 1e-9;
        feature.Add(("PORTAL: through-opening inflow and net outdoor inflow from known opening plane fluxes (A 0.65 / 0.20, B 0.20 / 0)", sums, $"A through {thA:0.000} net outdoor {netA:0.000}; B through {thB:0.000} net outdoor {netB:0.000}"));

        float none = Analysis.UnexplainedShareOf(total: 0.65, unlisted: 0, through: 0.65);
        float back = Analysis.UnexplainedShareOf(total: 0.80, unlisted: 0, through: 0.65);
        float gap = Analysis.UnexplainedShareOf(total: 0.65, unlisted: 0.10, through: 0.65);
        feature.Add(("PORTAL: unexplained share is 0 when openings carry all the air, > 0 for boundary back-and-forth or an unlisted gap",
            none == 0f && Math.Abs(back - 0.1875f) < 1e-5f && gap > 0.1f, $"explained {none:0.000}; extra boundary air {back:0.0000}; unlisted gap {gap:0.0000}"));

        // PortalOpenings on a crafted 12 x 7 x 3 grid (Dx 1): room A = cells x 1..4, room B = cells x 6..10, an interior door I in the wall cell x = 5 (normal +X) and an exterior
        // window W in the wall cell x = 0 (normal -X, outward). A touches both openings from their -Normal side; B touches only I, from its +Normal side.
        {
            var g = new VoxelGrid { Nx = 12, Ny = 7, Nz = 3, Dx = 1f, Origin = Vector3.Zero, Cell = new byte[12 * 7 * 3] };
            bool[] Mask(int x0, int x1)
            {
                var m = new bool[g.Count];
                for (int z = 0; z < 3; z++) for (int y = 1; y < 6; y++) for (int x = x0; x <= x1; x++) m[g.Index(x, y, z)] = true;
                return m;
            }
            var door = new Opening { Id = "I", Center = new Vector3(5.5f, 3.5f, 1.5f), Normal = Vector3.UnitX, Width = 2, Height = 2, Depth = 1, SideA = OpeningSide.Room, SideB = OpeningSide.Room };
            var window = new Opening { Id = "W", Center = new Vector3(0.5f, 3.5f, 1.5f), Normal = -Vector3.UnitX, Width = 2, Height = 2, Depth = 1, SideA = OpeningSide.Outdoors, SideB = OpeningSide.Room };
            var model = new BuildingModel { Openings = { door, window } };
            var field = new FlowField { Grid = g };
            field.OpeningFluxes.Add(new OpeningLinkFlux { Opening = door, Pos = 0.20, Neg = 0.05 });
            field.OpeningFluxes.Add(new OpeningLinkFlux { Opening = window, Pos = 0.10, Neg = 0.40 });
            var a = Analysis.PortalOpenings(g, field, model, Mask(1, 4));
            var b = Analysis.PortalOpenings(g, field, model, Mask(6, 10));
            Analysis.PortalSums(a, out double pthA, out double pnetA);
            Analysis.PortalSums(b, out double pthB, out double pnetB);
            bool map = a.Count == 2 && a[0] == new Analysis.PortalOpening(0.20, 0.05, -1, false) && a[1] == new Analysis.PortalOpening(0.10, 0.40, -1, true) &&
                       b.Count == 1 && b[0] == new Analysis.PortalOpening(0.20, 0.05, +1, false) &&
                       Math.Abs(pthA - 0.45) < 1e-9 && Math.Abs(pnetA - 0.30) < 1e-9 && Math.Abs(pthB - 0.20) < 1e-9 && Math.Abs(pnetB) < 1e-9;
            feature.Add(("PORTAL: openings are assigned to the rooms from the grid (two rooms, one interior door, one exterior window): A sees both from the -Normal side, B only the door from the +Normal side",
                map, $"A {a.Count} openings [{string.Join("; ", a.Select(o => $"side {o.Side} ext {o.Exterior}"))}] through {pthA:0.00} net outdoor {pnetA:0.00}; B {b.Count} [{string.Join("; ", b.Select(o => $"side {o.Side} ext {o.Exterior}"))}] through {pthB:0.00} net outdoor {pnetB:0.00}"));
        }
    }
}
