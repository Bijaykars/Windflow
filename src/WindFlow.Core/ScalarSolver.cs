using System.Linq;
using System.Numerics;

namespace WindFlow.Core;

/// <summary>Steady advection–diffusion of passive scalars on the final (frozen) velocity field:
/// CO2 from occupants (ppm), a unit-rate contaminant tracer, and mean age of air (Sandberg 1981).
/// Runs after the flow solve, so it never affects the airflow.</summary>
public static class ScalarSolver
{
    public const float Co2PerPersonLps = 0.0052f;   // L/s, ASHRAE 62.1 / Persily & de Jonge 2017, office activity
    public const float Diffusivity = 1.5e-5f;       // m2/s molecular; turbulent part comes from the flow's eddy viscosity
    const float ScT = 0.9f;                          // turbulent Schmidt number
    const int MaxSweeps = 8000;                       // a ceiling-fan room needs ~5300 (BENCH 8); P14-19 fluxes, was 4000
    const double Tolerance = 1e-5;                   // stop when the relative L2 residual of the assembled
                                                        // equations drops below this (see the sweep loop for how
                                                        // it is normalised); MaxSweeps is the iteration cap
    public const float AgeCapSeconds = 86_400f;       // the age at which a room counts as sealed (RoomResult.AgeSealed)
    const float AgeCap = AgeCapSeconds, Co2Cap = 50_000f;   // "no steady state" ceilings, s and ppm
    const string AgeCapNote = "Age of air has no steady state: no fresh air reaches part of the building (values capped at 24 h)";
    public const string AgeNotConvergedNote = "Age of air not converged within the sweep limit: ages are a lower bound";   // P14-09
    static readonly string[] NotePrefixes =
        ["Scalar transport hit its", "CO₂ has no steady state", "Age of air has no steady state", "Age of air not converged", "Contaminant has no steady state"];
    /// <summary>RU-03: removes the notes <see cref="Solve"/> adds (iteration cap, no steady state, not converged) from a grid's warnings.</summary>
    public static void StripNotes(List<string> warnings) => warnings.RemoveAll(w => NotePrefixes.Any(n => w.StartsWith(n, StringComparison.Ordinal)));
    const string Co2CapNote = "CO₂ has no steady state: no fresh air reaches part of the building (values capped at 50 000 ppm)";
    const float Omega = 1.0f;                        // plain Gauss-Seidel. Do NOT over-relax: a_P is exactly
                                                        // sum(a_Q) (see the coefficient assembly), so the system
                                                        // is only weakly diagonally dominant and omega > 1
                                                        // diverges to NaN here -- measured at 1.5 and at 1.8

    /// <summary>Finite-volume advection–diffusion (first-order upwind advection, central diffusion), solved to
    /// steady state by SOR-accelerated red-black Gauss-Seidel sweeps. Only cells inside Voxelizer.PlanBounds
    /// (the building footprint, wall face to wall face) and within the analysed storeys' z-range are solved --
    /// everything else (outside the footprint, unanalysed storeys) is held fixed at outdoor values, which doubles
    /// as the inlet boundary condition: because advection is upwind, a solved cell only "sees" a fixed neighbour's
    /// value when flow actually enters from it (outflow faces get zero advective coupling to the neighbour, only
    /// the usually-tiny diffusive term), so cells just outside an opening behave like a Dirichlet outdoor boundary
    /// and outflow faces behave like a zero-gradient outlet without any per-face bookkeeping. Age and Contaminant
    /// are indoor-only quantities, so both are reported as NaN outside the footprint; CO2 keeps the outdoor value
    /// out there for context.
    /// <para><paramref name="initial"/>: warm start -- the solved cells start from this earlier solution (same grid,
    /// e.g. the previous pass on a slowly changing averaged field) instead of outdoor values; ignored when its
    /// arrays do not match the grid. Same converged answer, far fewer sweeps when the flow has barely moved.</para>
    /// <para><paramref name="ageOnly"/>: solve mean age of air alone; Co2 and Contaminant come back all NaN (not
    /// computed). About a third of the sweep work.</para>
    /// Every Parallel.For here honours <see cref="RunOptions.MaxDegreeOfParallelism"/> and <paramref name="ct"/>.</summary>
    public static ScalarFields Solve(BuildingModel model, FlowField field, RunOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default, ScalarFields? initial = null, bool ageOnly = false)
    {
        var g = field.Grid;
        StripNotes(g.Warnings);   // RU-03: the grid is shared by every solve on it; the previous solve's notes go first, so each appears once
        int n = g.Count;
        var po = new ParallelOptions { CancellationToken = ct };
        if (options.MaxDegreeOfParallelism is int mdop && mdop > 0) po.MaxDegreeOfParallelism = mdop;
        var co2 = new float[n];
        var age = new float[n];
        var contam = new float[n];
        float outdoorCo2 = model.Env.OutdoorCo2;

        // ---- analysed z-range (fallback to rooms, then the whole grid, if no storey is marked Analysed) ----
        float zLo = float.MaxValue, zHi = float.MinValue;
        foreach (var s in model.Storeys)
            if (s.Analysed) { zLo = MathF.Min(zLo, s.Elevation); zHi = MathF.Max(zHi, s.Elevation + s.Height); }
        if (zLo > zHi)
            foreach (var r in model.Rooms) { zLo = MathF.Min(zLo, r.ZMin); zHi = MathF.Max(zHi, r.ZMax); }
        if (zLo > zHi) { zLo = g.Origin.Z; zHi = g.Origin.Z + g.Nz * g.Dx; }

        // RV-05 / RW-08: "outdoors" (age 0) is the air outside the analysed building's ENVELOPE, not outside the bounding rectangle of all geometry. A site
        // plate or a neighbouring building inside that rectangle is outdoors (its wake air must not age). The envelope is per storey: each storey's own
        // outline over its own height range (an unanalysed storey takes the outline of the storey below), so the air above a lower roof next to a taller
        // block stays outdoors. A model with no rooms keeps the rectangle (and the analysed height).
        var envelope = BuildingEnvelope(model, g);
        var (planMin, planMax) = Voxelizer.PlanBounds(model);

        // ---- classify every cell: Solid (NaN, never solved), outside the footprint (fixed at outdoor values,
        // Age/Contaminant reported as NaN -- they're indoor-only quantities) or inside it (solved). `indoor`
        // records the geometric footprint test on its own, since the supply-source loop below turns some indoor
        // cells' `solved` flag back off without making them any less indoor. ----
        var solved = new bool[n];
        var indoor = new bool[n];
        float cellVolume = g.Dx * g.Dx * g.Dx;
        var rhsAgeFull = new float[n];      // unit volumetric source, "everywhere in air" that's solved

        for (int z = 0; z < g.Nz; z++)
        {
            float cz = g.Origin.Z + (z + 0.5f) * g.Dx;
            for (int y = 0; y < g.Ny; y++)
            for (int x = 0; x < g.Nx; x++)
            {
                int idx = g.Index(x, y, z);
                if (g.Cell[idx] == VoxelGrid.Solid) { co2[idx] = float.NaN; age[idx] = float.NaN; contam[idx] = float.NaN; continue; }

                var c = g.CellCenter(x, y, z);
                bool inRegion = envelope != null ? InEnvelope(envelope, y * g.Nx + x, cz)
                                                 : c.X >= planMin.X && c.X <= planMax.X && c.Y >= planMin.Y && c.Y <= planMax.Y && cz >= zLo && cz <= zHi;
                co2[idx] = outdoorCo2; age[idx] = 0f; contam[idx] = 0f;
                bool domainInflow = (x == 0 && field.Ux[idx] > 0) || (x == g.Nx-1 && field.Ux[idx] < 0)
                    || (y == 0 && field.Uy[idx] > 0) || (y == g.Ny-1 && field.Uy[idx] < 0)
                    || (z == 0 && g.OpenZMin && field.Uz[idx] > 0) || (z == g.Nz-1 && g.OpenZMax && field.Uz[idx] < 0);
                if (inRegion) { solved[idx] = !domainInflow; indoor[idx] = true; rhsAgeFull[idx] = domainInflow ? 0 : cellVolume; }
            }
        }

        // Supply source patches inject fresh air: their Fixed cells are pinned to outdoor values (fresh air),
        // not solved, even though they sit inside the region. Return/Fan Fixed cells stay ordinary solved cells --
        // their imposed velocity already makes them outflow/recirculation, upwind advection handles the rest.
        for (int si = 0; si < model.Sources.Count; si++)
        {
            var s = model.Sources[si];
            if (!s.Enabled || s.Kind != SourceKind.Supply || g.PatchFlow(si, s.FlowRate) <= 0) continue;   // RW-02: the flow the LBM runs at
            foreach (var idx in Voxelizer.SourceCells(g, s, c => c == VoxelGrid.Fixed))
            {
                solved[idx] = false;
                co2[idx] = outdoorCo2; age[idx] = 0f; contam[idx] = 0f;
            }
        }

        // RV-04: the palette Diffuser is an outdoor-air supply: its outlet cells are fresh air, as a Supply patch's are, and the
        // building rate (3600 / mean age) counts it by the same token. A fan, a split AC and a cassette recirculate room air (no
        // pinning); an exhaust is a sink. They are ordinary air cells, so nothing else changes for them.
        var freshPinned = new bool[n];
        foreach (var reg in g.Devices)
        {
            if (reg.Kind != DeviceKind.Diffuser) continue;
            var src = reg.SourceIndex >= 0 && reg.SourceIndex < model.Sources.Count ? model.Sources[reg.SourceIndex] : model.Sources.Find(x => x.Id == reg.SourceId);
            if (src == null || !src.Enabled || src.Kind != SourceKind.Supply || src.FlowRate <= 0) continue;
            if (g.NoFreshAir.Contains(model.Sources.IndexOf(src))) continue;   // RW-05: in a sealed room it only stirs the room's air
            for (int k = 0; k < reg.OutletCount && k < reg.Cells.Length; k++)
            {
                int idx = reg.Cells[k];
                solved[idx] = false; freshPinned[idx] = true;
                co2[idx] = outdoorCo2; age[idx] = 0f; contam[idx] = 0f;
            }
        }

        // Warm start: solved cells only (boundary/pinned cells keep their fixed values); a non-finite value (NaN
        // from an age-only pass, or outside the old footprint) falls back to the cold start.
        if (initial != null)
            for (int i = 0; i < n; i++)
            {
                if (!solved[i]) continue;
                if (initial.Age.Length == n && float.IsFinite(initial.Age[i])) age[i] = initial.Age[i];
                if (initial.Co2.Length == n && float.IsFinite(initial.Co2[i])) co2[i] = initial.Co2[i];
                if (initial.Contaminant.Length == n && float.IsFinite(initial.Contaminant[i])) contam[i] = initial.Contaminant[i];
            }

        // ---- CO2 and contaminant sources, accumulated into per-cell RHS arrays over the whole grid ----
        var rhsCo2Full = new float[n];
        var rhsContamFull = new float[n];

        foreach (var room in model.Rooms)
        {
            if (room.Occupants <= 0) continue;
            AddCo2Source(g, solved, rhsCo2Full, room.Occupants, cell =>
            {
                float cz = cell.Z;
                return cz >= room.ZMin + 0.1f && cz <= room.ZMin + 1.8f && Analysis.InsideLoops(room.Loops, new Vector2(cell.X, cell.Y));
            });
        }
        foreach (var h in model.Heat)
        {
            if (!h.Enabled || h.Kind != HeatKind.People || h.People <= 0) continue;
            Vector3 half = new(h.Width * 0.5f, h.Depth * 0.5f, h.Height * 0.5f);
            Vector3 lo = h.Center - half, hi = h.Center + half;
            AddCo2Source(g, solved, rhsCo2Full, h.People, cell =>
                cell.X >= lo.X && cell.X <= hi.X && cell.Y >= lo.Y && cell.Y <= hi.Y && cell.Z >= lo.Z && cell.Z <= hi.Z);
        }
        foreach (var cs in model.Contaminants)
        {
            if (!cs.Enabled) continue;
            Vector3 lp = (cs.Center - g.Origin) / g.Dx;
            int cx = Math.Clamp((int)MathF.Floor(lp.X), 0, g.Nx - 1);
            int cy = Math.Clamp((int)MathF.Floor(lp.Y), 0, g.Ny - 1);
            int cz = Math.Clamp((int)MathF.Floor(lp.Z), 0, g.Nz - 1);
            int idx = g.Index(cx, cy, cz);
            // ponytail: a source centred in solid or outside the solved region is dropped, not snapped to the
            // nearest solved cell -- real scenes keep contaminant sources inside a room.
            if (solved[idx]) rhsContamFull[idx] += cs.Rate;
        }

        // ---- sealed pockets: solved air with no path through air to any fixed (outdoor / fresh-air) cell. Age (and
        // CO2 where people sit in it) has no steady state there -- it grows every sweep, so the residual never falls
        // under tolerance and every solve of a building with a closed room ran the full MaxSweeps (and a warm start
        // could not help). Such a pocket is set straight to the "no steady state" caps the cap guard below would
        // clamp it to anyway and left out of the sweep, so the rest converges on its own. A pocket holding a
        // contaminant source stays in the sweep (its cap comes from the solved field's own distribution). ----
        int pocketCells = 0; bool pocketCo2 = false;
        {
            var reach = new bool[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (g.Cell[i] != VoxelGrid.Solid && !solved[i]) { reach[i] = true; queue.Enqueue(i); }
            void Visit(int j, Queue<int> q, bool[] seen) { if (!seen[j] && g.Cell[j] != VoxelGrid.Solid) { seen[j] = true; q.Enqueue(j); } }
            void Neighbours(int i, Queue<int> q, bool[] seen)
            {
                int x = i % g.Nx, y = i / g.Nx % g.Ny, z = i / (g.Nx * g.Ny);
                if (x > 0) Visit(i - 1, q, seen);
                if (x < g.Nx - 1) Visit(i + 1, q, seen);
                if (y > 0) Visit(i - g.Nx, q, seen);
                if (y < g.Ny - 1) Visit(i + g.Nx, q, seen);
                if (z > 0) Visit(i - g.Nx * g.Ny, q, seen);
                if (z < g.Nz - 1) Visit(i + g.Nx * g.Ny, q, seen);
            }
            while (queue.Count > 0) Neighbours(queue.Dequeue(), queue, reach);

            // Label each pocket (reach doubles as "visited"), then settle it as a whole.
            var pocket = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (reach[i] || !solved[i]) continue;
                pocket.Clear();
                reach[i] = true; queue.Enqueue(i);
                while (queue.Count > 0) { int c = queue.Dequeue(); pocket.Add(c); Neighbours(c, queue, reach); }
                bool hasContam = false, hasCo2 = false;
                foreach (int c in pocket) { hasContam |= rhsContamFull[c] > 0f; hasCo2 |= rhsCo2Full[c] > 0f; }
                if (hasContam) continue;
                foreach (int c in pocket)
                {
                    solved[c] = false;
                    age[c] = AgeCap;
                    if (hasCo2) co2[c] = Co2Cap;
                }
                pocketCells += pocket.Count; pocketCo2 |= hasCo2;
            }
        }
        if (pocketCells > 0)
        {
            g.Warnings.Add(AgeCapNote);
            if (pocketCo2 && !ageOnly) g.Warnings.Add(Co2CapNote);
        }

        // ---- solved-cell list, split into the two red-black colours so each half can run under Parallel.For
        // without racing its neighbours (any two 6-neighbours always differ in (x+y+z) parity). ----
        var color0 = new List<int>();
        var color1 = new List<int>();
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, z);
            if (!solved[idx]) continue;
            (((x + y + z) & 1) == 0 ? color0 : color1).Add(idx);
        }
        var solvedList = new int[color0.Count + color1.Count];
        color0.CopyTo(solvedList, 0);
        color1.CopyTo(solvedList, color0.Count);
        int split = color0.Count, count = solvedList.Length;
        if (count == 0)
        {
            if (ageOnly) { Array.Fill(co2, float.NaN); Array.Fill(contam, float.NaN); }
            return new ScalarFields { Co2 = co2, Age = age, Contaminant = contam };
        }

        // ---- effective diffusivity per non-solid cell: molecular + turbulent/Sc_t. Tu is the flow's turbulence
        // intensity (0..1); nu_t = 0.1*|u|*dx*Tu, or 0.05*|u|*dx if Tu wasn't computed (ponytail: both constants
        // are crude mixing-length estimates, not a real turbulence model -- good enough for a passive scalar). ----
        var dEff = new float[n];
        bool hasTu = field.Tu.Length == n;
        Parallel.For(0, n, po, i =>
        {
            if (g.Cell[i] == VoxelGrid.Solid) return;
            float speed = new Vector3(field.Ux[i], field.Uy[i], field.Uz[i]).Length();
            float nut = hasTu ? 0.1f * speed * g.Dx * field.Tu[i] : 0.05f * speed * g.Dx;
            dEff[i] = Diffusivity + nut / ScT;
        });

        // ---- precompute the (velocity- and diffusivity-dependent, but scalar-independent) FV coefficients once
        // and reuse them for all three scalars: a_P * phi_P = sum(a_Q * phi_Q) + rhs, upwind advection + central
        // diffusion (Patankar). A Solid neighbour face is simply skipped (zero-flux). A non-solid neighbour that
        // isn't itself solved (ring/exterior/fresh-air Fixed cell) still contributes its coefficient -- it's just
        // never updated, so it acts as a Dirichlet value pinned at its initial outdoor/fresh-air setting. ----
        Span<int> ddx = stackalloc int[6] { 1, -1, 0, 0, 0, 0 };
        Span<int> ddy = stackalloc int[6] { 0, 0, 1, -1, 0, 0 };
        Span<int> ddz = stackalloc int[6] { 0, 0, 0, 0, 1, -1 };
        var ddxA = ddx.ToArray(); var ddyA = ddy.ToArray(); var ddzA = ddz.ToArray();

        var aP = new float[count];
        var nbrIdx = new int[6][];
        var nbrCoeff = new float[6][];
        for (int d = 0; d < 6; d++) { nbrIdx[d] = new int[count]; nbrCoeff[d] = new float[count]; Array.Fill(nbrIdx[d], -1); }
        float area = g.Dx * g.Dx;
        // P14-19 / D5: mass-consistent face fluxes (m3/s towards the +axis neighbour), not node averages.
        var fluxClock = System.Diagnostics.Stopwatch.StartNew();
        var flux = ConsistentFluxes(model, g, field, solved, indoor, po);
        bool projectionConverged = flux.Converged;
        long fluxMs = fluxClock.ElapsedMilliseconds;

        Parallel.For(0, count, po, si =>
        {
            int idx = solvedList[si];
            int x = idx % g.Nx, y = idx / g.Nx % g.Ny, z = idx / (g.Nx * g.Ny);
            float nbrSum = 0f;
            for (int d = 0; d < 6; d++)
            {
                int nx = x + ddxA[d], ny = y + ddyA[d], nz = z + ddzA[d];
                if (nx < 0 || nx >= g.Nx || ny < 0 || ny >= g.Ny || nz < 0 || nz >= g.Nz) continue;
                int nIdx = g.Index(nx, ny, nz);
                if (g.Cell[nIdx] == VoxelGrid.Solid) continue;

                var fa = ddxA[d] != 0 ? flux.X : ddyA[d] != 0 ? flux.Y : flux.Z;
                int sign = ddxA[d] + ddyA[d] + ddzA[d];               // outward face normal sign, this axis
                float fPQ = sign > 0 ? fa[idx] : -fa[nIdx];            // volumetric flow P -> Q, m3/s
                // A Supply patch is a prescribed-flux inlet: it feeds fresh air by convection only. Turbulent diffusion
                // back across its face would drain age from the room into the pinned patch (2 % of the exhaust identity
                // under a ceiling fan) -- air that is not leaving through any opening.
                bool supplyFace = (g.Cell[nIdx] == VoxelGrid.Fixed && indoor[nIdx] && !solved[nIdx]) || freshPinned[nIdx];
                float dF = supplyFace ? 0f : 0.5f * (dEff[idx] + dEff[nIdx]) * area / g.Dx;

                float aQ = dF + MathF.Max(-fPQ, 0f);
                nbrIdx[d][si] = nIdx;
                nbrCoeff[d][si] = aQ;
                nbrSum += aQ;
            }
            // Patankar's mass-source treatment (Numerical Heat Transfer and Fluid Flow, §5.2-4). The textbook
            // diagonal is sum(a_Q) + sum(f), where sum(f) is the cell's net outflow: exactly zero for a
            // divergence-free velocity, but the LBM field is only approximately divergence-free, so every cell
            // carries a small continuity defect. Keeping that defect in the diagonal breaks the one property
            // both scalars depend on -- a uniform field must be an exact solution of the source-free equations
            // (sum(a_Q * phi) = phi * sum(a_Q)). The old one-sided max(0, -sum(f)) correction restored that in
            // net-INFLOW cells only (where a_P < sum(a_Q) would let a scalar grow instead); in a net-OUTFLOW
            // cell a_P stayed larger than sum(a_Q), which acts as a sink and made the scalar decay toward zero
            // -- CO2 settled BELOW its own 420 ppm outdoor boundary value. Dropping sum(f) is the standard
            // remedy and is identical to carrying the defect as a deferred source sum(f)*phi_P on the RHS:
            // a_P = sum(a_Q) for every sign of the defect, so every neighbour weight is <= 1 (boundedness,
            // the reason the old one-sided max(0, -sum(f)) correction was there) and the weights sum to
            // exactly 1, which is what makes a uniform field a fixed point.
            aP[si] = MathF.Max(nbrSum, 1e-12f);   // flood-filled air always keeps at least one open face; guard anyway
        });

        var rhsCo2 = new float[count];
        var rhsAge = new float[count];
        var rhsContam = new float[count];
        Parallel.For(0, count, po, si =>
        {
            int idx = solvedList[si];
            rhsCo2[si] = rhsCo2Full[idx];
            rhsAge[si] = rhsAgeFull[idx];
            rhsContam[si] = rhsContamFull[idx];
        });

        // ---- red-black Gauss-Seidel to steady state, stopped on the RESIDUAL of the assembled equations
        // (r_P = rhs_P + sum(a_Q phi_Q) - a_P phi_P) rather than on the per-sweep change: a slowly-crawling
        // Gauss-Seidel holds its change under any tolerance while still being far from a solution, and the
        // change says nothing about how big the imbalance it is still carrying actually is. Gauss-Seidel
        // forms sum(a_Q phi_Q) + rhs and a_P anyway, so the residual costs nothing extra.
        //
        // ||r||_2 is normalised, per scalar, by the VARIATION of a_P*phi over the solved cells (its standard
        // deviation times sqrt(count)) plus a thousandth of its magnitude -- the same shape as OpenFOAM's
        // normFactor, and for the same reason. Normalising by the magnitude ||a_P phi|| alone hides a scalar
        // that sits on a big offset (CO2's 420 ppm outdoor base is ~10x the indoor rise the check cares about,
        // so a magnitude-normalised residual is ~10x too optimistic for it), while normalising by the FIRST
        // sweep's residual is unreachable here: at float32 precision the a_P*phi cancellation floors the CO2
        // residual near 2e-4 of its initial value, so a 1e-5 target would hit the iteration cap every run.
        // The magnitude term only bites if a scalar's solution is so nearly uniform that its variation reaches
        // that same round-off floor, where it stops the test spinning to the cap over pure noise.
        //
        // acc[0..2] = sum of squared residuals per scalar (CO2, age, contaminant); acc[3..5] = sum of
        // a_P*phi; acc[6..8] = sum of (a_P*phi)^2. Variance = acc[6+k] - acc[3+k]^2/count, one pass, no
        // second sweep over the field. ----
        object lockObj = new();
        var acc = new double[9];

        void SweepRange(int start, int end)
        {
            if (ageOnly)
            {
                Parallel.For(start, end, po,
                    () => new double[9],
                    (si, _, local) =>
                    {
                        int idx = solvedList[si];
                        double sumAge = rhsAge[si];
                        for (int d = 0; d < 6; d++)
                        {
                            int nb = nbrIdx[d][si];
                            if (nb >= 0) sumAge += nbrCoeff[d][si] * age[nb];
                        }
                        float a = aP[si];
                        double rAge = sumAge - a * age[idx];
                        float newAge = age[idx] + Omega * (float)(rAge / a);
                        age[idx] = newAge;
                        double sAge = a * newAge;
                        local[1] += rAge * rAge; local[4] += sAge; local[7] += sAge * sAge;
                        return local;
                    },
                    local => { lock (lockObj) for (int k = 0; k < 9; k++) acc[k] += local[k]; });
                return;
            }
            Parallel.For(start, end, po,
                () => new double[9],
                (si, _, local) =>
                {
                    int idx = solvedList[si];
                    double sumCo2 = rhsCo2[si], sumAge = rhsAge[si], sumContam = rhsContam[si];
                    for (int d = 0; d < 6; d++)
                    {
                        int nb = nbrIdx[d][si];
                        if (nb < 0) continue;
                        float c = nbrCoeff[d][si];
                        sumCo2 += c * co2[nb];
                        sumAge += c * age[nb];
                        sumContam += c * contam[nb];
                    }
                    float a = aP[si];
                    float oldCo2 = co2[idx], oldAge = age[idx], oldContam = contam[idx];
                    double rCo2 = sumCo2 - a * oldCo2;      // residual of this cell's assembled equation
                    double rAge = sumAge - a * oldAge;
                    double rContam = sumContam - a * oldContam;
                    float newCo2 = oldCo2 + Omega * (float)(rCo2 / a);
                    float newAge = oldAge + Omega * (float)(rAge / a);
                    float newContam = oldContam + Omega * (float)(rContam / a);
                    co2[idx] = newCo2; age[idx] = newAge; contam[idx] = newContam;
                    local[0] += rCo2 * rCo2; local[1] += rAge * rAge; local[2] += rContam * rContam;
                    double sCo2 = a * newCo2, sAge = a * newAge, sContam = a * newContam;
                    local[3] += sCo2; local[4] += sAge; local[5] += sContam;
                    local[6] += sCo2 * sCo2; local[7] += sAge * sAge; local[8] += sContam * sContam;
                    return local;
                },
                local => { lock (lockObj) for (int k = 0; k < 9; k++) acc[k] += local[k]; });
        }

        bool converged = false;
        int sweepsUsed = 0;
        double relResidual = 0;
        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(acc);
            SweepRange(0, split);
            SweepRange(split, count);
            sweepsUsed = sweep + 1;
            relResidual = 0;
            for (int k = ageOnly ? 1 : 0; k < (ageOnly ? 2 : 3); k++)
            {
                double variance = acc[6 + k] - acc[3 + k] * acc[3 + k] / count;
                double den = Math.Sqrt(Math.Max(variance, 0)) + 1e-3 * Math.Sqrt(acc[6 + k]);
                // A scalar that is identically zero everywhere (the contaminant tracer in a scene with no
                // contaminant source) has no scale to normalise against and no residual either: it is left
                // out of the test rather than dividing by zero.
                if (den > 0) relResidual = Math.Max(relResidual, Math.Sqrt(acc[k]) / den);
            }
            progress?.Report((sweep + 1) / (double)MaxSweeps);
            if (relResidual <= Tolerance) { converged = true; break; }
        }
        progress?.Report(1);

        // ponytail: diagnostic only, off by default -- set WINDFLOW_SCALAR_DEBUG=1 to see convergence behaviour
        // (sweep count, final relative residual, peak age) for a scene without adding permanent log spam.
        if (System.Environment.GetEnvironmentVariable("WINDFLOW_SCALAR_DEBUG") == "1")
        {
            float maxAge = 0f;
            foreach (var idx in solvedList) if (age[idx] > maxAge) maxAge = age[idx];
            Console.Error.WriteLine($"[ScalarSolver] flux projection {fluxMs} ms, sweeps={sweepsUsed}/{MaxSweeps} converged={converged} relResidual={relResidual:E3} maxAge={maxAge:F1}s");
        }

        // Iteration cap hit: the fields below may still have been climbing when the sweeps ran out, so say so
        // with the numbers needed to judge it (a residual barely above the tolerance is a different situation
        // from one still at 1.0, which means no steady state exists at all -- see the cap warnings below).
        if (!converged)
            g.Warnings.Add($"Scalar transport hit its {MaxSweeps}-sweep iteration cap: relative residual " +
                $"{relResidual:0.###E+0} after {sweepsUsed} sweeps (tolerance {Tolerance:0.###E+0})");

        // ---- no-steady-state guard: a sealed room (or any part of the building fresh air never reaches) makes
        // these fields grow without bound instead of converging. Cap them at a generous-but-finite ceiling and
        // warn, rather than returning physically meaningless multi-million-ppm/second values silently. ----
        if (!ageOnly) CapIfUnbounded(g, solvedList, co2, Co2Cap, !converged || !projectionConverged, Co2CapNote);
        CapIfUnbounded(g, solvedList, age, AgeCap, !converged || !projectionConverged, AgeCapNote, AgeNotConvergedNote);
        if (!ageOnly) CapContaminantIfUnbounded(g, solvedList, contam, !converged);
        else { Array.Fill(co2, float.NaN); Array.Fill(contam, float.NaN); }

        // ---- Age and Contaminant are indoor-only quantities: NaN outside the building footprint (CO2 keeps the
        // outdoor value out there for context, so it's left alone). ----
        for (int i = 0; i < n; i++)
        {
            if (indoor[i] || g.Cell[i] == VoxelGrid.Solid) continue;
            age[i] = float.NaN;
            contam[i] = float.NaN;
        }

        return new ScalarFields { Co2 = co2, Age = age, Contaminant = contam };
    }

    /// <summary>RV-05 / RW-08: the analysed building's envelope as height bands, each with its own plan mask over the grid columns. One band per
    /// storey: the union of that storey's room loops (rooms matched by <see cref="Room.Level"/>), over the storey's height range and its rooms'
    /// own; a storey with no rooms (unanalysed) takes the plan of the nearest storey below, else above. Rooms of no listed storey get a band
    /// of their own. Each plan is grown by the host wall thickness so the throats of the openings (cells carved through a wall, outside the loops) count
    /// as inside, and by an opening's own depth only around that opening, in the band at its height. Null when the model has no room: the caller keeps
    /// its rectangle.</summary>
    internal static List<(float ZLo, float ZHi, bool[] Plan)>? BuildingEnvelope(BuildingModel model, VoxelGrid g)
    {
        if (model.Rooms.Count == 0 || model.Rooms.All(r => r.Loops.Count == 0)) return null;
        const float wallReach = 0.35f;                    // 0.25 m wall + the ~0.1 m the loops sit off the wall faces
        var bands = new List<(float ZLo, float ZHi, bool[]? Plan)>();
        var storeys = model.Storeys.OrderBy(st => st.Elevation).ToList();
        var placed = new HashSet<Room>();
        foreach (var st in storeys)
        {
            var rs = model.Rooms.Where(r => model.StoreyOf(r) == st && r.Loops.Count > 0).ToList();
            float lo = st.Elevation, hi = st.Elevation + st.Height;
            foreach (var r in rs) { placed.Add(r); lo = MathF.Min(lo, r.ZMin); hi = MathF.Max(hi, r.ZMax); }
            bands.Add((lo, hi, rs.Count > 0 ? PlanMask(rs, g, wallReach) : null));
        }
        foreach (var r in model.Rooms)
            if (r.Loops.Count > 0 && !placed.Contains(r)) bands.Add((r.ZMin, r.ZMax, PlanMask([r], g, wallReach)));
        for (int i = 0; i < bands.Count; i++)
        {
            if (bands[i].Plan != null) continue;
            int from = -1;
            for (int j = i - 1; j >= 0 && from < 0; j--) if (bands[j].Plan != null) from = j;
            for (int j = i + 1; j < storeys.Count && from < 0; j++) if (bands[j].Plan != null) from = j;
            if (from >= 0) bands[i] = (bands[i].ZLo, bands[i].ZHi, bands[from].Plan);
        }
        var result = new List<(float ZLo, float ZHi, bool[] Plan)>();
        foreach (var (lo, hi, plan) in bands)
            if (plan != null) result.Add((lo, hi, (bool[])plan.Clone()));   // a copy per band: the local opening growth below must not leak into a shared plan
        foreach (var o in model.Openings)
        {
            if (o.Depth <= wallReach - 0.1f) continue;
            float reach = o.Depth + 0.1f, half = MathF.Max(o.Width * 0.5f, g.Dx);
            var nrm = new Vector2(o.Normal.X, o.Normal.Y);
            bool oriented = nrm.LengthSquared() > 1e-6f;
            if (oriented) nrm = Vector2.Normalize(nrm);
            var tan = new Vector2(-nrm.Y, nrm.X);
            float ext = MathF.Max(reach, half);
            int ix0 = Math.Max(0, (int)MathF.Floor((o.Center.X - ext - g.Origin.X) / g.Dx)), ix1 = Math.Min(g.Nx - 1, (int)MathF.Floor((o.Center.X + ext - g.Origin.X) / g.Dx));
            int iy0 = Math.Max(0, (int)MathF.Floor((o.Center.Y - ext - g.Origin.Y) / g.Dx)), iy1 = Math.Min(g.Ny - 1, (int)MathF.Floor((o.Center.Y + ext - g.Origin.Y) / g.Dx));
            foreach (var (lo, hi, plan) in result)
            {
                if (o.Center.Z < lo || o.Center.Z > hi) continue;
                for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    var d = new Vector2(g.Origin.X + (x + 0.5f) * g.Dx - o.Center.X, g.Origin.Y + (y + 0.5f) * g.Dx - o.Center.Y);
                    bool near = oriented ? MathF.Abs(Vector2.Dot(d, nrm)) <= reach && MathF.Abs(Vector2.Dot(d, tan)) <= half : d.LengthSquared() <= ext * ext;
                    if (near) plan[y * g.Nx + x] = true;
                }
            }
        }
        return result;
    }

    /// <summary>Is the grid column <paramref name="column"/> at height <paramref name="z"/> inside the envelope (<see cref="BuildingEnvelope"/>)?</summary>
    static bool InEnvelope(List<(float ZLo, float ZHi, bool[] Plan)> envelope, int column, float z)
    {
        foreach (var (lo, hi, plan) in envelope) if (z >= lo && z <= hi && plan[column]) return true;
        return false;
    }

    /// <summary>The grid columns inside any of <paramref name="rooms"/>' loops or within <paramref name="reach"/> of one.</summary>
    static bool[] PlanMask(List<Room> rooms, VoxelGrid g, float reach)
    {
        var inside = new bool[g.Nx * g.Ny];
        foreach (var room in rooms)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var loop in room.Loops) foreach (var p in loop) { x0 = MathF.Min(x0, p.X); x1 = MathF.Max(x1, p.X); y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y); }
            int ix0 = Math.Max(0, (int)MathF.Floor((x0 - reach - g.Origin.X) / g.Dx)), ix1 = Math.Min(g.Nx - 1, (int)MathF.Floor((x1 + reach - g.Origin.X) / g.Dx));
            int iy0 = Math.Max(0, (int)MathF.Floor((y0 - reach - g.Origin.Y) / g.Dx)), iy1 = Math.Min(g.Ny - 1, (int)MathF.Floor((y1 + reach - g.Origin.Y) / g.Dx));
            for (int y = iy0; y <= iy1; y++)
            for (int x = ix0; x <= ix1; x++)
            {
                if (inside[y * g.Nx + x]) continue;
                float cx = g.Origin.X + (x + 0.5f) * g.Dx, cy = g.Origin.Y + (y + 0.5f) * g.Dx;
                var c = new Vector2(cx, cy);
                if (Analysis.InsideLoops(room.Loops, c) || NearLoops(room.Loops, c, reach)) inside[y * g.Nx + x] = true;
            }
        }
        return inside;
    }

    static bool NearLoops(List<Vector2[]> loops, Vector2 c, float reach)
    {
        float r2 = reach * reach;
        foreach (var loop in loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
            {
                var a = loop[j]; var ab = loop[i] - a;
                float len2 = ab.LengthSquared();
                float t = len2 > 1e-12f ? Math.Clamp(Vector2.Dot(c - a, ab) / len2, 0f, 1f) : 0f;
                if ((c - (a + ab * t)).LengthSquared() <= r2) return true;
            }
        return false;
    }

    // Clamps a scalar field to `cap` and records `warning` when a solved cell exceeds the cap (no air path: the
    // field really has no steady state). A solve that only ran out of sweeps gets `notConvergedWarning` instead
    // (P14-09): it is biased LOW, not unbounded, and "no fresh air reaches" would be false.
    static void CapIfUnbounded(VoxelGrid g, int[] solvedList, float[] field, float cap, bool notConverged, string warning, string? notConvergedWarning = null)
    {
        bool exceeds = false;
        foreach (var idx in solvedList)
            if (field[idx] > cap) { exceeds = true; break; }
        if (!exceeds && !notConverged) return;

        foreach (var idx in solvedList)
            if (field[idx] > cap) field[idx] = cap;
        // Both can be true (cells at the cap AND the sweeps ran out): say both, the not-converged note must never be swallowed by the cap note.
        if (exceeds && !g.Warnings.Contains(warning)) g.Warnings.Add(warning);
        if (notConverged && notConvergedWarning != null && !g.Warnings.Contains(notConvergedWarning)) g.Warnings.Add(notConvergedWarning);
        if (notConverged && notConvergedWarning == null && !exceeds && !g.Warnings.Contains(warning)) g.Warnings.Add(warning);
    }

    /// <summary>Face fluxes of the scalar transport, m3/s from each cell to its +x / +y / +z neighbour (0 across
    /// solid), forced to be discretely divergence-free (P14-19). The raw fluxes are node averages 0.5*(uP+uQ)*A of
    /// the LBM mean velocity, whose continuity defect (5-15 % of the through-flow) the scalar scheme would create
    /// or destroy age and tracer with. Pressure-Poisson projection on the scalar grid: solve the graph Laplacian of
    /// phi = divergence of F over the solved air cells, F -= grad phi. Faces to solid carry no correction (zero
    /// normal gradient); faces to cells that are not solved and not a source patch (outdoor air, the domain
    /// boundary, domain-inflow cells) have phi = 0, so they take up any imbalance. Faces to an in-footprint Fixed
    /// cell (Supply / Return / Fan patch) are PRESCRIBED from that patch cell's own velocity normal to the face
    /// -- the delivered flow, not the room cell's slower neighbour average -- and the Fixed cell is left out of the
    /// projection as the source / sink it is. Conjugate gradient, relative residual 1e-4, at most 600 iterations
    /// (ponytail: if prescribed patch flows do not balance in a room with no cell open to outdoor, the mean imbalance is
    /// spread over that room; a stalled CG just leaves the remaining defect in place).</summary>
    static (float[] X, float[] Y, float[] Z, bool Converged) ConsistentFluxes(BuildingModel model, VoxelGrid g, FlowField field, bool[] solved, bool[] indoor, ParallelOptions po)
    {
        int n = g.Count, nx = g.Nx, nxy = g.Nx * g.Ny;
        float area = g.Dx * g.Dx;
        var fx = new float[n]; var fy = new float[n]; var fz = new float[n];
        bool Patch(int i) => g.Cell[i] == VoxelGrid.Fixed && indoor[i];   // prescribed-flux source / sink cell
        bool Proj(int i) => solved[i] && g.Cell[i] != VoxelGrid.Fixed;   // cell that must be divergence-free

        // 1. raw fluxes
        void Raw(float[] f, float[] u, int stride, Func<int, bool> hasNext) =>
            Parallel.For(0, n, po, i =>
            {
                if (!hasNext(i)) return;
                int j = i + stride;
                if (g.Cell[i] == VoxelGrid.Solid || g.Cell[j] == VoxelGrid.Solid) return;
                bool pi = Patch(i), pj = Patch(j);
                float v = pi && !pj ? u[i] : pj && !pi ? u[j] : 0.5f * (u[i] + u[j]);
                f[i] = v * area;
            });
        Raw(fx, field.Ux, 1, i => i % nx != nx - 1);
        Raw(fy, field.Uy, nx, i => i / nx % g.Ny != g.Ny - 1);
        Raw(fz, field.Uz, nxy, i => i / nxy != g.Nz - 1);

        // 1b. Supply and Return patches: the LBM's PI controller regulates the link flux through each to its FlowRate (the node velocity
        // times the carved cell area reads a few % off where the drawn W x H rasterises differently), so scale their prescribed faces
        // to that flow. Since batch 3b a Return is flow-controlled like a Supply (it was 95.8 % of set in BENCH 8, RV-01 follow-up). A Fan
        // patch passes air through itself with a net of about zero: its faces stay as the field has them (no factor to bound).
        bool debugFlux = System.Environment.GetEnvironmentVariable("WINDFLOW_SCALAR_DEBUG") == "1";
        for (int srcIdx = 0; srcIdx < model.Sources.Count; srcIdx++)
        {
            var s = model.Sources[srcIdx];
            float setFlow = g.PatchFlow(srcIdx, s.FlowRate);   // RW-02: the flow the LBM runs at (a sealed room's extract is balanced to its supply)
            if (!s.Enabled || s.Device != DeviceKind.Patch || setFlow <= 0) continue;
            double net = 0;
            var entries = new List<(float[] F, int E)>();
            foreach (int c in Voxelizer.SourceCells(g, s, t => t == VoxelGrid.Fixed))
            {
                if (!Patch(c)) continue;
                int x = c % nx, y = c / nx % g.Ny, z = c / nxy;
                for (int d = 0; d < 6; d++)
                {
                    int axis = d >> 1; bool plus = (d & 1) == 0;
                    int stride = axis == 0 ? 1 : axis == 1 ? nx : nxy;
                    bool inside = axis == 0 ? (plus ? x < nx - 1 : x > 0) : axis == 1 ? (plus ? y < g.Ny - 1 : y > 0) : (plus ? z < g.Nz - 1 : z > 0);
                    if (!inside) continue;
                    int j = plus ? c + stride : c - stride;
                    if (g.Cell[j] == VoxelGrid.Solid || Patch(j)) continue;
                    var f = axis == 0 ? fx : axis == 1 ? fy : fz;
                    int e = plus ? c : j;
                    net -= plus ? f[e] : -f[e];                    // air leaving the room cell j into the patch cell c
                    entries.Add((f, e));
                }
            }
            double target = s.Kind == SourceKind.Return ? setFlow : -setFlow;
            if (debugFlux) Console.Error.WriteLine($"[ScalarSolver] patch {s.Name} ({s.Kind}): prescribed flux into the patch {net:0.00000} m3/s, set {setFlow:0.00000}{(s.Kind != SourceKind.Fan ? " (scaled to set)" : " (kept as the field has it)")}");
            if (s.Kind == SourceKind.Fan) continue;        // RV-01: a Fan has no net flow to scale
            if (net * target <= 0 || Math.Abs(net) < 1e-9) continue;
            float k = (float)(target / net);
            foreach (var (f, e) in entries) f[e] *= k;
        }

        // 2. projection cells, their free/Dirichlet neighbours, and the divergence to remove
        var cells = new List<int>();
        var compact = new int[n];
        Array.Fill(compact, -1);
        for (int i = 0; i < n; i++) if (Proj(i)) { compact[i] = cells.Count; cells.Add(i); }
        int m = cells.Count;
        if (m == 0) return (fx, fy, fz, true);
        var nb = new int[6][];
        for (int d = 0; d < 6; d++) { nb[d] = new int[m]; Array.Fill(nb[d], -1); }
        var diag = new float[m];
        var b = new float[m];
        var hasDirichlet = new bool[m];                  // RW-02: the cell has a face with phi = 0 behind it (outdoors, a domain edge, a non-projected cell)
        Parallel.For(0, m, po, k =>
        {
            int i = cells[k], x = i % nx, y = i / nx % g.Ny, z = i / nxy;
            double div = 0; int nFree = 0;
            for (int d = 0; d < 6; d++)
            {
                int axis = d >> 1; bool plus = (d & 1) == 0;
                int stride = axis == 0 ? 1 : axis == 1 ? nx : nxy;
                bool inside = axis == 0 ? (plus ? x < nx - 1 : x > 0) : axis == 1 ? (plus ? y < g.Ny - 1 : y > 0) : (plus ? z < g.Nz - 1 : z > 0);
                if (!inside)
                {
                    // The edge of the grid: an open face (the wind ring on x / y, a lid only when the box is open in z) lets the
                    // cell's own node flow leave or enter; it has no cell behind it, so phi = 0 there and the scalar scheme never sees it.
                    if (axis == 2 && !(plus ? g.OpenZMax : g.OpenZMin)) continue;
                    var ub = axis == 0 ? field.Ux : axis == 1 ? field.Uy : field.Uz;
                    div += (plus ? 1 : -1) * ub[i] * area;
                    nFree++; hasDirichlet[k] = true;
                    continue;
                }
                int j = plus ? i + stride : i - stride;
                if (g.Cell[j] == VoxelGrid.Solid) continue;
                var f = axis == 0 ? fx : axis == 1 ? fy : fz;
                div += plus ? f[i] : -f[j];
                if (Patch(j)) continue;                       // prescribed: carries no correction
                nFree++;
                if (compact[j] >= 0) nb[d][k] = compact[j]; else hasDirichlet[k] = true;
            }
            diag[k] = nFree; b[k] = (float)-div;
        });
        // RV-06 / RW-02: a connected set of projection cells with no Dirichlet face anywhere (a sealed room) has a singular Laplacian; the
        // solvable right-hand side has zero mean over the set, so shift each such set by its own mean. The imbalance of the prescribed patch
        // flows is spread over that room alone and the rest of the building keeps its true Dirichlet system.
        {
            var seen = new bool[m]; var stack = new Stack<int>(); var members = new List<int>();
            for (int k0 = 0; k0 < m; k0++)
            {
                if (seen[k0]) continue;
                members.Clear(); bool anyDirichlet = false; double sum = 0;
                seen[k0] = true; stack.Push(k0);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); members.Add(c); anyDirichlet |= hasDirichlet[c]; sum += b[c];
                    for (int d = 0; d < 6; d++) { int q = nb[d][c]; if (q >= 0 && !seen[q]) { seen[q] = true; stack.Push(q); } }
                }
                if (anyDirichlet) continue;
                float sh = (float)(sum / members.Count);
                foreach (int c in members) b[c] -= sh;
            }
        }

        // 3. Jacobi-preconditioned CG on  diag*phi - sum(phi_nbr) = b
        var phi = new float[m]; var r = (float[])b.Clone(); var zv = new float[m]; var p = new float[m]; var ap = new float[m];
        double Dot(float[] u, float[] v)
        {
            var parts = new double[(m + 65535) / 65536];
            Parallel.For(0, parts.Length, po, c =>
            {
                double s = 0; int hi = Math.Min(m, (c + 1) * 65536);
                for (int k = c * 65536; k < hi; k++) s += (double)u[k] * v[k];
                parts[c] = s;
            });
            double t = 0; foreach (var s in parts) t += s; return t;
        }
        double bb = Dot(b, b);
        double rrLast = bb;
        if (bb > 0)
        {
            Parallel.For(0, m, po, k => { zv[k] = diag[k] > 0 ? r[k] / diag[k] : 0f; p[k] = zv[k]; });
            double rz = Dot(r, zv);
            for (int it = 0; it < 600 && rz > 0; it++)
            {
                po.CancellationToken.ThrowIfCancellationRequested();
                Parallel.For(0, m, po, k =>
                {
                    float s = diag[k] * p[k];
                    for (int d = 0; d < 6; d++) { int q = nb[d][k]; if (q >= 0) s -= p[q]; }
                    ap[k] = s;
                });
                double pap = Dot(p, ap);
                if (pap <= 0) break;
                float alpha = (float)(rz / pap);
                Parallel.For(0, m, po, k => { phi[k] += alpha * p[k]; r[k] -= alpha * ap[k]; zv[k] = diag[k] > 0 ? r[k] / diag[k] : 0f; });
                double rr = Dot(r, r);
                rrLast = rr;
                if (rr <= 1e-8 * bb) break;                    // relative residual 1e-4
                double rzNew = Dot(r, zv);
                float beta = (float)(rzNew / rz); rz = rzNew;
                Parallel.For(0, m, po, k => p[k] = zv[k] + beta * p[k]);
            }
        }

        // 4. F -= grad phi on the free and Dirichlet faces (phi = 0 outside the projection set)
        void Correct(float[] f, int stride, Func<int, bool> hasNext) =>
            Parallel.For(0, n, po, i =>
            {
                if (!hasNext(i)) return;
                int j = i + stride;
                if (g.Cell[i] == VoxelGrid.Solid || g.Cell[j] == VoxelGrid.Solid || Patch(i) || Patch(j)) return;
                int ci = compact[i], cj = compact[j];
                if (ci < 0 && cj < 0) return;
                f[i] += (ci >= 0 ? phi[ci] : 0f) - (cj >= 0 ? phi[cj] : 0f);
            });
        Correct(fx, 1, i => i % nx != nx - 1);
        Correct(fy, nx, i => i / nx % g.Ny != g.Ny - 1);
        Correct(fz, nxy, i => i / nxy != g.Nz - 1);
        // A stalled CG (600 iterations, residual still above 1e-4) leaves a continuity defect that biases age: reported through the not-converged note.
        return (fx, fy, fz, bb <= 0 || rrLast <= 1e-8 * bb);
    }

    // Contaminant has no natural physical ceiling (it's a relative unit-rate tracer), so the cap is derived from
    // the field's own distribution: 10x the 99.9th percentile of solved values, which only bites when a handful
    // of cells have run away (e.g. a sealed pocket the tracer can never leave) rather than the whole field.
    static void CapContaminantIfUnbounded(VoxelGrid g, int[] solvedList, float[] contam, bool notConverged)
    {
        if (solvedList.Length == 0) return;
        var sorted = new float[solvedList.Length];
        for (int i = 0; i < solvedList.Length; i++) sorted[i] = contam[solvedList[i]];
        Array.Sort(sorted);
        float p999 = sorted[(int)Math.Clamp((long)(sorted.Length * 0.999), 0, sorted.Length - 1)];
        if (p999 <= 0f) return;   // no meaningful contaminant present; nothing to cap
        float cap = p999 * 10f;

        bool exceeds = false;
        foreach (var idx in solvedList)
            if (contam[idx] > cap) { exceeds = true; break; }
        if (!exceeds && !notConverged) return;

        foreach (var idx in solvedList)
            if (contam[idx] > cap) contam[idx] = cap;
        g.Warnings.Add($"Contaminant has no steady state: no fresh air reaches part of the building (values capped at {cap:0.###} units/m3)");
    }

    // Spreads People * Co2PerPersonLps evenly (as ppm/s converted to a flat per-cell add, see below) over every
    // solved cell whose centre satisfies `inZone`. q [m3/s] / V_cells * 1e6 ppm, integrated over one cell's
    // volume, collapses to q*1e6/cellCount -- dx cancels, so no cell-volume term is needed here.
    static void AddCo2Source(VoxelGrid g, bool[] solved, float[] rhsCo2Full, int people, Func<Vector3, bool> inZone)
    {
        var cells = new List<int>();
        for (int z = 0; z < g.Nz; z++)
        for (int y = 0; y < g.Ny; y++)
        for (int x = 0; x < g.Nx; x++)
        {
            int idx = g.Index(x, y, z);
            if (!solved[idx]) continue;
            if (inZone(g.CellCenter(x, y, z))) cells.Add(idx);
        }
        if (cells.Count == 0) return;
        float q = people * Co2PerPersonLps * 1e-3f;   // L/s -> m3/s
        float perCell = q * 1e6f / cells.Count;
        foreach (var idx in cells) rhsCo2Full[idx] += perCell;
    }
}
