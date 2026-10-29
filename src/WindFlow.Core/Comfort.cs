using System.Numerics;

namespace WindFlow.Core;

/// <summary>Thermal comfort indices. PMV/PPD per ISO 7730 Annex D / ASHRAE 55 (Fanger), draught rate per
/// Fanger, Melikov, Hanzawa &amp; Ring 1988. Formulas re-derived from the standards; numeric checks against the
/// MIT-licensed pythermalcomfort reference values.</summary>
public static class Comfort
{
    /// <param name="ta">air temperature °C</param><param name="tr">mean radiant temperature °C</param>
    /// <param name="vel">air speed m/s</param><param name="rh">relative humidity %</param>
    /// <param name="met">metabolic rate met</param><param name="clo">clothing insulation clo</param>
    public static (float Pmv, float Ppd) Pmv(float ta, float tr, float vel, float rh, float met, float clo)
    {
        // Water vapour partial pressure (Pa).
        float pa = rh * 10f * MathF.Exp(16.6536f - 4030.183f / (ta + 235f));

        float icl = 0.155f * clo;               // clothing insulation, m2K/W
        float m = met * 58.15f;                 // metabolic rate, W/m2
        float mw = m;                            // internal heat production (external work = 0)
        float fcl = icl <= 0.078f ? 1f + 1.29f * icl : 1.05f + 0.645f * icl;

        float hcf = 12.1f * MathF.Sqrt(vel);     // forced convection coefficient
        float taa = ta + 273f;
        float tra = tr + 273f;
        float tcla = taa + (35.5f - ta) / (3.5f * icl + 0.1f);   // first guess of clothing surface temperature

        float p1 = icl * fcl;
        float p2 = p1 * 3.96f;
        float p3 = p1 * 100f;
        float p4 = p1 * taa;
        float p5 = 308.7f - 0.028f * mw + p2 * MathF.Pow(tra / 100f, 4);
        float xn = tcla / 100f;
        float xf = xn;
        float hc = hcf;

        // Standard iterative solve for the clothing surface temperature (max 150 iterations, ISO 7730 Annex D).
        for (int n = 0; n < 150; n++)
        {
            xf = (xf + xn) / 2f;
            float hcn = 2.38f * MathF.Pow(MathF.Abs(100f * xf - taa), 0.25f);
            hc = hcf > hcn ? hcf : hcn;
            xn = (p5 + p4 * hc - p2 * MathF.Pow(xf, 4)) / (100f + p3 * hc);
            if (MathF.Abs(xn - xf) <= 0.00015f) break;
        }

        float tcl = 100f * xn - 273f;

        float hl1 = 3.05e-3f * (5733f - 6.99f * mw - pa);              // diffusion of water vapour through skin
        float hl2 = mw > 58.15f ? 0.42f * (mw - 58.15f) : 0f;          // heat loss by sweating
        float hl3 = 1.7e-5f * m * (5867f - pa);                        // latent respiration heat loss
        float hl4 = 0.0014f * m * (34f - ta);                          // dry respiration heat loss
        float hl5 = 3.96f * fcl * (MathF.Pow(xn, 4) - MathF.Pow(tra / 100f, 4));  // radiation
        float hl6 = fcl * hc * (tcl - ta);                             // convection

        float ts = 0.303f * MathF.Exp(-0.036f * m) + 0.028f;
        float pmv = ts * (mw - hl1 - hl2 - hl3 - hl4 - hl5 - hl6);
        float ppd = 100f - 95f * MathF.Exp(-0.03353f * MathF.Pow(pmv, 4) - 0.2179f * pmv * pmv);

        return (pmv, ppd);
    }

    /// <summary>DR = (34 − ta)(v − 0.05)^0.62 (0.37 v Tu + 3.14), Tu in %; 0 when v &lt; 0.05 m/s; capped at 100.</summary>
    public static float DraughtRate(float ta, float v, float tuPercent)
    {
        if (v < 0.05f) return 0f;
        float dr = (34f - ta) * MathF.Pow(v - 0.05f, 0.62f) * (0.37f * v * tuPercent + 3.14f);
        return Math.Clamp(dr, 0f, 100f);
    }

    /// <summary>Per-cell PMV/PPD/DR for air cells inside some room's own footprint (even-odd loop test) and its
    /// [ZMin, ZMax]; NaN everywhere else (corridors, plant rooms, outdoors). Uses field.T (or Env.IndoorTemperature
    /// when empty), field.Tu (or 40 % when empty), tr = ta.</summary>
    public static ComfortFields Compute(BuildingModel model, FlowField field, RunOptions options)
    {
        var g = field.Grid;
        int n = g.Count;
        var result = new ComfortFields { Pmv = new float[n], Ppd = new float[n], Dr = new float[n] };
        Array.Fill(result.Pmv, float.NaN);
        Array.Fill(result.Ppd, float.NaN);
        Array.Fill(result.Dr, float.NaN);

        if (model.Rooms.Count == 0) return result;

        bool hasT = field.T.Length == n;
        bool hasTu = field.Tu.Length == n;
        float met = options.MetabolicRate, clo = options.Clothing, rh = model.Env.RelativeHumidity;

        // OF-1: a room's cells include its band (the air between its outline and its walls), so the occupied-zone comfort counts in Analysis see values there
        var banded = new bool[n];
        foreach (var m in Analysis.RoomMasks(g, model, out _)) for (int i = 0; i < n; i++) if (m[i]) banded[i] = true;

        Parallel.For(0, g.Nz, z =>
        {
            float cz = g.Origin.Z + (z + 0.5f) * g.Dx;

            for (int y = 0; y < g.Ny; y++)
            {
                float cy = g.Origin.Y + (y + 0.5f) * g.Dx;
                for (int x = 0; x < g.Nx; x++)
                {
                    float cx = g.Origin.X + (x + 0.5f) * g.Dx;

                    int idx = g.Index(x, y, z);
                    if (g.Cell[idx] == VoxelGrid.Solid) continue;   // Fixed cells (diffusers) count as air

                    // Comfort is a room quantity: NaN unless the cell sits inside some analysed room's footprint
                    // and within that room's own z-range (corridors, plant rooms and the like stay NaN).
                    var p2 = new Vector2(cx, cy);
                    bool inRoom = banded[idx];
                    if (!inRoom) foreach (var room in model.Rooms)
                        if (cz >= room.ZMin && cz <= room.ZMax && Analysis.InsideLoops(room.Loops, p2)) { inRoom = true; break; }
                    if (!inRoom) continue;

                    float ta = hasT ? field.T[idx] : model.Env.IndoorTemperature;
                    float speed = new Vector3(field.Ux[idx], field.Uy[idx], field.Uz[idx]).Length();
                    float tuPercent = hasTu ? 100f * field.Tu[idx] : 40f;

                    var (pmv, ppd) = Pmv(ta, ta, MathF.Max(speed, 0.05f), rh, met, clo);
                    result.Pmv[idx] = pmv;
                    result.Ppd[idx] = ppd;
                    result.Dr[idx] = DraughtRate(ta, speed, tuPercent);
                }
            }
        });

        return result;
    }
}
