using System.Numerics;

namespace WindFlow.Core;

/// <summary>What <see cref="HighAccuracy.Plan"/> chose and why: the grid, its memory, and every reason it fell short.</summary>
public sealed class HighAccuracyPlan
{
    /// <summary>The whole building's cell size, always Accurate's (<see cref="AccurateDx"/>), and its cell count.</summary>
    public float Dx, AccurateDx;
    public int Cells;
    /// <summary>The 2:1 fine box around the focus room (null: no box), its cell size (Dx / 2) and its cell count.</summary>
    public DomainBox? FineBox;
    public float FineDx;
    public int FineCells;
    public string FocusRoomId = "", FocusRoomName = "";
    /// <summary>Margin of the fine box around the room's footprint AABB: plan and vertical, metres.</summary>
    public float MarginPlan, MarginZ;
    /// <summary>Estimated GPU bytes of the coarse + fine grids (<see cref="HighAccuracy.BytesPerCell"/> x cells) and the budget they were fitted to.</summary>
    public long Bytes, BudgetBytes;
    /// <summary>Estimated coarse steps per second (<see cref="HighAccuracy.MlupsEstimate"/> / (coarse + 2 x fine cells)).</summary>
    public double StepsPerSecond;
    /// <summary>The smallest open opening of the focus room (m) and how many fine cells across it is (NaN: no box or no open opening).</summary>
    public float OpeningSize = float.NaN, OpeningCells = float.NaN;
    public List<string> Warnings = new();

    public override string ToString() =>
        $"coarse Dx {Dx * 1000:0.#} mm (= Accurate), {Cells / 1e6:0.##}M cells" +
        (FineBox is { } b ? $" + fine box around {FocusRoomName} at {FineDx * 1000:0.#} mm: {FineCells / 1e6:0.###}M cells, {b.Size.X:0.#} x {b.Size.Y:0.#} x {b.Size.Z:0.#} m (margin {MarginPlan:0.##} m plan / {MarginZ:0.##} m up)" : ", no fine box") +
        $", ~{Bytes / 1e9:0.##} GB of {BudgetBytes / 1e9:0.##} GB budget, ~{StepsPerSecond:0} coarse steps/s" +
        (float.IsNaN(OpeningCells) ? "" : $", smallest open opening of the focus room {OpeningSize * 1000:0} mm = {OpeningCells:0.#} fine cells across") +
        (Warnings.Count > 0 ? " | " + string.Join(" | ", Warnings) : "");
}

/// <summary>The "High Accuracy" mode: Accurate's solver with only the validated additions (HANDOFF.md, 2026-10-05/06):
/// the Accurate-resolution whole building plus a 2:1 nested fine box around one focus room (when given), measured-style supply-inlet turbulence, approach-flow turbulence, and a batch-means average with a
/// 95 % CI target, a 2-flow-through warm-up and a wall-time cap. The wall model stays OFF. It is a recipe over
/// <see cref="RunOptions"/>: Preview and Accurate never call it, so their options (Tu 0, no wind turbulence) are untouched.</summary>
public static class HighAccuracy
{
    /// <summary>GPU bytes per cell: the one figure of <see cref="LbmSolver.GpuBytesPerCell"/> (P17-05); a thermal run adds the difference to <see cref="LbmSolver.GpuBytesPerCellThermal"/>.</summary>
    public const long BytesPerCell = LbmSolver.GpuBytesPerCell, ThermalExtraBytesPerCell = LbmSolver.GpuBytesPerCellThermal - LbmSolver.GpuBytesPerCell;
    /// <summary>Share of (free) VRAM the grid may take. Core itself refuses a grid above 75 % of the card.</summary>
    public const double VramShare = 0.6;
    /// <summary>The focus room's smallest open opening should be at least this many FINE cells across (cross-vent validation: about 8; MINIBAT 6-8); reported only.</summary>
    public const int MinOpeningCells = 8;
    /// <summary>Wall-time caps the user may pick, minutes; the default is 10.</summary>
    public static readonly int[] CapMinutes = [5, 10, 20, 30];
    public const int DefaultCapMinutes = 10;
    /// <summary>Warm-up flow-throughs before the first batch.</summary>
    public const int WarmupFlowThroughs = 2;

    /// <summary>The nearest allowed cap (5/10/20/30) to <paramref name="minutes"/>.</summary>
    public static int SnapCapMinutes(int minutes) => CapMinutes.OrderBy(c => Math.Abs(c - minutes)).First();

    // ---------------------------------------------------------------- A. grid

    /// <summary>Measured throughput of the solver on the dev card (RTX 5070 Ti, 3000 MLUPS), used only for the steps-per-second estimate.</summary>
    public const double MlupsEstimate = 3000;
    /// <summary>Smallest fine-box margin (m) the plan shrinks to when memory is short: plan, then vertical.</summary>
    public const float MinMarginPlan = 0.5f, MinMarginZ = 0.3f;

    /// <summary>The whole building at Accurate's Dx (<paramref name="accurateCells"/>: the cell budget Accurate would use, the app's tier
    /// row, which fixes that Dx), plus, when <paramref name="focusRoomId"/> names a room (Id or Name), a 2:1 fine box (Dx / 2, the existing
    /// <see cref="LbmSolver.StartNested"/> machinery) around it: the room's footprint AABB (Loops x ZMin..ZMax) with a margin on every side,
    /// clamped to the domain. The margin is the sponge band of the box (the cells where the fine grid is pulled to the coarse run, so
    /// openings there gain nothing) plus two fine cells; if the coarse + fine grids then exceed the memory budget it shrinks toward
    /// <see cref="MinMarginPlan"/> / <see cref="MinMarginZ"/>, and if they still do not fit there is no box (a warning, not a failure).
    /// <paramref name="freeVramBytes"/>: free GPU memory (0 = unknown / no GPU); about 60 % of it is the budget. A thermal run gets no box
    /// (nesting is isothermal only). No focus room: no box.</summary>
    public static HighAccuracyPlan Plan(BuildingModel model, long freeVramBytes, int accurateCells, string? focusRoomId = null, bool thermal = false)
    {
        var (min, _, side, extW, extD, height) = Voxelizer.AutoDomain(model);
        double volume = (double)extW * extD * height;
        accurateCells = Math.Min(accurateCells, LbmSolver.MaxCells);   // P17-04
        var plan = new HighAccuracyPlan();
        float dx = plan.AccurateDx = plan.Dx = Math.Clamp((float)Math.Cbrt(volume / Math.Max(1, accurateCells)), 0.1f, 1f);
        long perCell = BytesPerCell + (thermal ? ThermalExtraBytesPerCell : 0);
        plan.BudgetBytes = (long)(freeVramBytes * VramShare);
        plan.Cells = (int)Math.Min(int.MaxValue, (long)Math.Ceiling(extW / dx) * (long)Math.Ceiling(extD / dx) * ((long)Math.Ceiling(height / dx) + 1));
        plan.Bytes = plan.Cells * perCell;
        if (freeVramBytes <= 0) plan.Warnings.Add("no GPU memory figure (no GPU?): memory not checked");
        else if (plan.Bytes > plan.BudgetBytes)
            plan.Warnings.Add($"the whole building at Accurate's cell size ({plan.Bytes / 1e9:0.##} GB) is over the {plan.BudgetBytes / 1e9:0.##} GB budget");

        if (!string.IsNullOrWhiteSpace(focusRoomId))
        {
            var room = model.Rooms.FirstOrDefault(r => string.Equals(r.Id, focusRoomId, StringComparison.OrdinalIgnoreCase))
                    ?? model.Rooms.FirstOrDefault(r => string.Equals(r.Name, focusRoomId, StringComparison.OrdinalIgnoreCase));
            if (room == null || room.Loops.Count == 0) plan.Warnings.Add($"focus room '{focusRoomId}' not found (or has no footprint): no fine box");
            else if (thermal) plan.Warnings.Add("nested fine boxes are isothermal only: no fine box on a thermal run");
            else PlanBox(plan, model, room, min, side, extW, extD, height, perCell);
        }
        if (plan.StepsPerSecond == 0) plan.StepsPerSecond = MlupsEstimate * 1e6 / plan.Cells;   // no box: the coarse grid alone
        return plan;
    }

    static void PlanBox(HighAccuracyPlan plan, BuildingModel model, Room room, Vector2 planMin, float side, float extW, float extD, float height, long perCell)
    {
        float dx = plan.Dx, fdx = dx / 2f;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var loop in room.Loops) foreach (var p in loop) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
        // The domain the coarse grid covers (Voxelizer.Build: plan bounds - side, ground .. ground + height).
        var dMin = new Vector3(planMin.X - side, planMin.Y - side, model.GroundZ);
        var dMax = new Vector3(planMin.X - side + extW, planMin.Y - side + extD, model.GroundZ + height);
        float sponge = (LbmSolver.NestedSpongeDefault + 1) * fdx;
        float preferred = sponge + 2 * fdx;
        float mh = MathF.Max(preferred, MinMarginPlan), mv = MathF.Max(preferred, MinMarginZ);

        DomainBox Make(float h, float v)
        {
            var lo = Vector3.Max(new Vector3(x0 - h, y0 - h, room.ZMin - v), dMin);
            var hi = Vector3.Min(new Vector3(x1 + h, y1 + h, room.ZMax + v), dMax);
            return new DomainBox(lo, hi);
        }
        // Snapping outward to coarse faces (StartNested) adds up to one coarse cell per side.
        int CellsOf(DomainBox b) => (int)Math.Min(int.MaxValue, (long)Math.Ceiling((b.Size.X + 2 * dx) / fdx) * (long)Math.Ceiling((b.Size.Y + 2 * dx) / fdx) * (long)Math.Ceiling((b.Size.Z + 2 * dx) / fdx));
        bool Fits(int fineCells) => plan.BudgetBytes <= 0 || plan.Bytes + fineCells * perCell <= plan.BudgetBytes;

        var box = Make(mh, mv);
        int fine = CellsOf(box);
        for (int i = 0; i < 8 && !Fits(fine) && (mh > MinMarginPlan || mv > MinMarginZ); i++)
        {
            mh = MathF.Max(MinMarginPlan, mh - (preferred - MinMarginPlan) / 4f);
            mv = MathF.Max(MinMarginZ, mv - (preferred - MinMarginZ) / 4f);
            box = Make(mh, mv); fine = CellsOf(box);
        }
        if (!Fits(fine) || !box.IsValid)
        {
            plan.Warnings.Add(!box.IsValid ? $"fine box around {room.Name} is under a metre on an axis after clamping: no fine box"
                : $"GPU memory ({plan.BudgetBytes / 1e9:0.##} GB budget) does not fit the fine box around {room.Name} ({(plan.Bytes + fine * perCell) / 1e9:0.##} GB): no fine box");
            return;
        }
        if (mh < preferred * 0.999f)
            plan.Warnings.Add($"fine box margin shrunk to {mh:0.##} m (sponge band {sponge:0.##} m): openings within the band gain little from the finer grid");
        plan.FineBox = box; plan.FineDx = fdx; plan.FineCells = fine; plan.Bytes += fine * perCell;
        plan.FocusRoomId = room.Id; plan.FocusRoomName = room.Name; plan.MarginPlan = mh; plan.MarginZ = mv;
        plan.StepsPerSecond = MlupsEstimate * 1e6 / (plan.Cells + 2.0 * fine);

        // Reported only: the focus room's open openings (centre within 0.3 m of its AABB) at the FINE cell size.
        float smallest = float.MaxValue;
        foreach (var o in model.Openings)
        {
            if (!o.IsOpen || o.Width <= 0 || o.Height <= 0) continue;
            var c = o.Center;
            if (c.X < x0 - 0.3f || c.X > x1 + 0.3f || c.Y < y0 - 0.3f || c.Y > y1 + 0.3f || c.Z < room.ZMin - 0.3f || c.Z > room.ZMax + 0.3f) continue;
            smallest = MathF.Min(smallest, MathF.Min(o.Width, o.Height));
        }
        if (smallest < float.MaxValue)
        {
            plan.OpeningSize = smallest; plan.OpeningCells = smallest / fdx;
            if (plan.OpeningCells < MinOpeningCells * 0.999f)
                plan.Warnings.Add($"smallest open opening of {room.Name} ({smallest * 1000:0} mm) is only {plan.OpeningCells:0.#} fine cells across (aim {MinOpeningCells})");
        }
    }

    /// <summary>Fills <paramref name="o"/> for a High Accuracy run: the planned grid, per-supply Tu, EN 1991-1-4 approach turbulence,
    /// and the convergence-driven average (95 % CI on room ACH within 5 %, 2 flow-throughs of warm-up, wall-time cap).
    /// The wall model is left as it is (OFF by default) on purpose. Fixed-length options a caller already set are kept.</summary>
    public static void Configure(RunOptions o, BuildingModel model, HighAccuracyPlan plan, int capMinutes = DefaultCapMinutes)
    {
        o.Quality = Quality.HighAccuracy;
        o.TargetCells = plan.Cells;
        o.MinDx = MathF.Min(plan.Dx * 0.99f, 0.1f);
        o.FocusBox = plan.FineBox;
        o.AutoConverge = true;
        o.WarmupFlowThroughs = WarmupFlowThroughs;
        o.WallCapSeconds = SnapCapMinutes(capMinutes) * 60.0;
        o.SupplyTuPerSource = true;
        o.WindTurbulence = ApproachTurbulence(model, o.WindProfileExponent);
        o.OnlineAge = true;   // AGE-HIGH: the gusty pass lands the online age (the steady age rides along as the conservative estimate)
    }

    // ---------------------------------------------------------------- C. supply Tu

    /// <summary>Default inlet turbulence intensity Tu (rms of each velocity component over the face velocity) by supply type, used
    /// when <see cref="AirSource.TurbulenceIntensity"/> is null and <see cref="RunOptions.SupplyTuPerSource"/> is on. From Said, Jouini
    /// and Plett 1993 and Kurabuchi 1990 (RESEARCH_PROMPT_6, HANDOFF.md):
    /// plain slot / linear 0.04; round nozzle or duct end 0.10; grille 0.10; radial or swirl ceiling diffuser 0.20; perforated or
    /// displacement panel 0 (no preset: set it by hand).
    /// <para>Only link-wise supply patches (<see cref="DeviceKind.Patch"/>, Kind Supply: the Revit air terminals) consume it.
    /// The palette devices are body-force regions (fans, split AC, cassette, diffuser) and extracts never reach this value in
    /// the solver; their rows are kept so the table is complete, and the device inspector shows the "turb" field for patches only (P13a-02).
    /// A source that fits no supply type gets 0.10, the grille / duct-end value, as the middle of the measured range.</para>
    /// <para>Sub-grid rule: a length scale l0 below 2 cells carries no resolved eddy, so the solver injects nothing for a supply whose
    /// face is under 2 cells across (LbmSolver.SupplyTu, l0 = the patch's smaller in-face extent). The formula for a physical l0
    /// would be l0 ~ 0.1 x hydraulic diameter, injected only when l0 &gt;= 2 dx; the patch extent is used because no l0 is stored.</para></summary>
    public static float DefaultSupplyTu(AirSource s)
    {
        if (s.Kind == SourceKind.Return) return 0f;                       // extracts
        switch (s.Device)
        {
            case DeviceKind.Diffuser:   return 0.20f;                      // radial / swirl ceiling diffuser (body-force device: not consumed)
            case DeviceKind.CassetteAc: return 0.20f;                      // four-way ceiling discharge, diffuser-like (body-force device: not consumed)
            case DeviceKind.SplitAc:    return 0.04f;                      // slot outlet (body-force device: not consumed)
            case DeviceKind.ExhaustFan:
            case DeviceKind.Hood:       return 0f;
            case DeviceKind.Patch:
                {
                    float a = MathF.Max(s.Width, s.Height), b = MathF.Min(s.Width, s.Height);
                    return b > 0 && a / b >= 5f ? 0.04f : 0.10f;           // slot (aspect >= 5) vs grille / duct end
                }
            default: return 0.10f;                                         // fans: no supply type fits (not consumed); middle of the measured range
        }
    }

    // ---------------------------------------------------------------- D. approach-flow turbulence

    /// <summary>EN 1991-1-4:2005 roughness length z0 (m) for a power-law profile exponent a, for the app's single terrain input.
    /// Table 4.1 categories against the usual exponents, interpolated in log z0: 0.11 -> 0.01 (I, lakes), 0.15 -> 0.05 (II, farmland),
    /// 0.22 -> 0.3 (III, suburban: the app's default), 0.30 -> 1.0 (IV, urban). The app has no terrain setting, only the exponent.</summary>
    public static float Z0FromExponent(float a)
    {
        float[] ea = [0.11f, 0.15f, 0.22f, 0.30f], z0 = [0.01f, 0.05f, 0.3f, 1.0f];
        if (a <= ea[0]) return z0[0];
        if (a >= ea[^1]) return z0[^1];
        for (int i = 1; i < ea.Length; i++)
            if (a <= ea[i])
            {
                float t = (a - ea[i - 1]) / (ea[i] - ea[i - 1]);
                return MathF.Exp(MathF.Log(z0[i - 1]) + t * (MathF.Log(z0[i]) - MathF.Log(z0[i - 1])));
            }
        return 0.3f;
    }

    /// <summary>EN 1991-1-4 Table 4.1 minimum height z_min (m) for a roughness length: 1 (z0 &lt;= 0.01), 2 (0.05), 5 (0.3), 10 (1.0).</summary>
    static float ZMin(float z0) => z0 <= 0.01f ? 1f : z0 <= 0.05f ? 2f : z0 <= 0.3f ? 5f : 10f;

    /// <summary>Approach-flow turbulence of an open-terrain atmosphere from EN 1991-1-4:2005 (null with a uniform profile, exponent 0).
    /// Turbulence intensity, section 4.4 eq. 4.7: I_v(z) = k_I / (c0(z) ln(z/z0)) for z_min &lt;= z &lt;= z_max (200 m), with k_I = 1 and the
    /// orography factor c0 = 1; constant at I_v(z_min) below z_min. Integral length scale, Annex B.1 eq. B.1: L(z) = L_t (z/z_t)^alpha,
    /// L_t = 300 m, z_t = 200 m, alpha = 0.67 + 0.05 ln(z0) (z0 in m), constant at L(z_min) below z_min. The solver's
    /// <see cref="WindTurbulence"/> takes ONE length scale, so L is taken at the building's roof height (not below z_min).
    /// The ratios sigma_v/sigma_u = 0.8, sigma_w/sigma_u = 0.5 are WindTurbulence's defaults.
    /// <para>The cross-ventilation validation used the wind-tunnel k(z) instead; this is the full-scale standard equivalent.
    /// The mean profile stays the app's power law (z0 comes from its exponent, <see cref="Z0FromExponent"/>); I_v is sigma_u over the local mean.</para></summary>
    public static WindTurbulence? ApproachTurbulence(BuildingModel model, float profileExponent)
    {
        if (profileExponent <= 0f) return null;
        float z0 = Z0FromExponent(profileExponent), zMin = ZMin(z0);
        const float kI = 1f, c0 = 1f, zMax = 200f, Lt = 300f, zt = 200f;
        float alpha = 0.67f + 0.05f * MathF.Log(z0);
        float roof = model.GroundZ;
        foreach (var m in model.Obstacles) foreach (var v in m.Vertices) roof = MathF.Max(roof, v.Z);
        float zRef = Math.Clamp(roof - model.GroundZ, zMin, zMax);
        return new WindTurbulence
        {
            LengthScale = Lt * MathF.Pow(zRef / zt, alpha), TimeRefHeight = zRef,
            Iu = z => kI / (c0 * MathF.Log(Math.Clamp(z, zMin, zMax) / z0)),
        };
    }
}
