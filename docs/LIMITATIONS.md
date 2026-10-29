# Limitations, validation summary and roadmap

## Known limitations

What WindFlow does not get right yet, in plain words, each with the size we measured. The batch named in brackets is in
`audits/2026-10-12/fixes/FIXLOG.md`. Queue V (2026-10-20, logs in `audits/2026-10-12/runs-v/`) re-measured the numbers below where it says so; a number marked "not in queue V" has not been re-measured on the fixed code.

**The three that matter most (queue V, 2026-10-20):**

- **Per-room numbers depend on the grid.** Openings are only a few cells wide, so where the grid falls across a window changes its flow. A 0.6 % change in cell size moved single rooms by **12-36 %**, and refining the grid did not converge: across 2.4, 5.2 and 11.4 million cells the Living room and Kitchen fresh-air rates kept climbing (9.8, 10.8, 16.1 and 21.8, 26.9, 39.0 ACH) and the Landing and Bedroom 2 swung up and down; only Hall and Bedroom 1 fresh-air stayed within 5 %. Building totals and well-ventilated rooms are steadier. Re-checked after the age-of-air fix (queue W2, 2026-10-26): on the sample house at about 5 million cells, a +-2 % change in cell size still moves the building's fresh-air ACH by up to about 19 % and single rooms by up to about 34 % (fresh-air) and 26 % (total exchange); the same grid run twice agrees exactly, and rotating the building passes. The spread is in the flow itself, not the age calculation. Compare design options on the same grid, and expect the grid to move a room's ACH by **up to about 35 %** for a single room and **about 20 %** for the whole building. ([`VALIDATION.md`](../VALIDATION.md), "Grid convergence (P16-G4)" and "Queue W2".)
- **The default Standard run (10 minutes) did not settle in any of 7 test runs.** It shows ranges, not a settled value; only Bedroom 1 and a sealed room settled. A 60-minute run still left the Kitchen, Bedroom 2 and Landing unsettled, and the 10-minute numbers sat within about 3 % of it for 5 of 6 rooms (Bedroom 2 +14 %). For a final check use High or a longer time limit. Standard switches a room to the online age only when that room's online age has settled within the run's time budget; on a ~5 M cell grid this typically needs 30-50 min of run time, so shorter runs mostly report the steady age (marked "steady age").
- **The wind inflow settles slowly and depends on how the run started.** Results land about 1.5-3 % high in a 10-minute run, and two different starts settle 0.5-4 % apart (0.47 % at 60 thousand cells, 4.3 % at 400 thousand; 0.1-0.2 % with an equilibrium inflow; `audits/2026-10-12/fixes/bV/drift/`). The Sandbox checks 53 and 56b fail for this reason.

1. **Air is stickier than real air (viscosity floor).** Changing the floor from the shipped 5e-4 to 1e-4 changed the sample house's
   Living room air changes by **-30 %** and the Kitchen's by **-15 %** (P11-G2). The shipped floor stays 5e-4: the sweep made one validation case
   better and two worse, and the paired re-run at 1e-4 on the fixed code (queue V) agreed: Annex 20 got better on all four metrics (q 0.453 to 0.557), cross-ventilation got worse on q (0.746 to 0.587) and NMSE, MINIBAT on all four, so 5e-4 stays (Batch 6m rule). Treat absolute air changes and jet reach as indicative.
2. **The ground is smooth, with no slip, so wind near the ground is too weak.** At 1 m the wind is about **54 % under** the EN 1991-1-4 profile (Batch 6b+6c).
   For oblique winds the profile also drifts by **10.8-15.9 %** at 2 m (the old ring: 12.1-18.2 %; Batch RZ). The generated gust intensity Iu reads
   0.94-0.97 of the EN target over 120 L/U (Batch RZ: 0.957 / 0.939 at 3.1 / 6.2 m height, 270 degrees; Batch 6b+6c gate 0.965 / 0.941).
3. **The default Compact wind domain blocks the flow.** On the sample house the measured blockage is 10.5 % in Compact against about 2.4 % in the COST 732 domain.
   At the Preview cell size (1.54 M cells) the room total exchange reads **8 to 33 % higher** (median about 16 %) and the building's fresh-air rate **12.1 against 9.7 ACH (25 % higher)** in Compact than in Full (queue V, 2026-10-20; the Full run had 81 kept batches and 3 of 6 rooms still unsettled; the older Batch 6b+6c figures were 7-16 % and 27 %, from a Full run of 2 batches)
   Bedroom 2 is the worst room (+33 % total exchange, +44 % fresh-air). Flows are therefore
   likely **about 11-14 % higher per room than the Full domain (newest test; an older run read 8-33 %)** in Compact: use Expert -> Settings -> Domain: Full for a final check (Full costs 11.7 times the cells). The app notes a blockage above 3 %.
4. **Small openings pass the wrong amount of air.** A window 4 cells across resolves a discharge coefficient of **0.48-0.60** instead of 0.61 (wall one cell thick: 0.562, about 8 % low;
   four cells thick: 0.482, about 21 % low). No extra loss is added and a note says how much low the flow may be. Openings 8 and 16 cells across are calibrated to within +-1.3 % of 0.61.
   Where the opening falls on the grid matters too: the same window moved by a fraction of a cell changes its Cd by up to the figure below, and the app says so when it exceeds 5 % (Batch RP).

   | Cells across | wall 0.5 cell | 1 | 2 | 3 | 4 |
   |---|---|---|---|---|---|
   | 4 | +-30.1 % | +-10.2 % | +-7.1 % | +-6.5 % | +-7.0 % |
   | 8 | +-21.1 % | +-7.7 % | +-3.2 % | +-2.6 % | +-3.0 % |
   | 16 | +-14.3 % | +-5.1 % | +-1.6 % | +-2.1 % | +-2.3 % |

   (wall thickness in cells across the top row; per-placement scatter X of the Cd the lattice resolves, over eight placements.)
   The table leaves out two placements: a hole aligned to the grid (edge on a cell face) and a hole with its edge on a cell centre (it opens n + 1 rows). Both lie outside X: check 58a reads Cd 0.716 against the requested 0.61 (+17 %) for an aligned hole at n 8, wall 1 (2026-10-20, `audits/2026-10-12/fixes/bRW/orifice.txt`). Queue V (2026-10-20) measured the BENCH orifice rig, which holds the hole at one offset, at n = 4 / 8 / 16: calibrated Cd 0.739 / 0.716 / 0.670, none within 3 % of 0.61; it reads one placement of the scatter above, not the phase mean the calibration targets (FIXLOG Batch 6d, follow-up 2).
5. **Walls do not conduct heat; only Surface sources exchange it.** Ordinary walls, floors, roofs and glazing are adiabatic (no U-values). A Surface heat source (a panel or a cold
   window drawn as one) exchanges heat with the air through a convection correlation (Alamdari-Hammond with a forced term; Batch 6e). That correlation is checked against hand-computed
   plate values (a 20 K vertical plate, H = 3 m: 3.413 W/m2K; a 20 K hot floor, 2 m hydraulic diameter: 4.448 W/m2K; Sandbox check RT-10b) and one sealed-room energy check, but the
   heated-cavity benchmarks (31a-e) run the older face rule, so they do not test it, and no measured room has. With Thermal on, each room is held at its indoor temperature by an ideal
   heater/cooler (volume mean, default on), so wall heat loss does not set the room temperature.
6. **Warm air from small heat sources is under-stratified.** The sealed filling-box check (19a) reads a vertical temperature difference of **1.30 K** against a reference of 1.8-8.6 K: about
   **25-40 % low** (the lead's sizing after Batch RT), and it stays a FAIL (Batch S, RT). The heater delivers 99.3 % of its energy, the room gain equals the injected energy to 0.1 % and nothing leaks; the plume is under-resolved and
   not grid-converged: 1.35 / 1.35 / 1.55 / 1.76 K at Dx 0.268 / 0.198 / 0.134 / 0.100 m.
   **Opt-in plume model (experimental, 2026-10-28):** `WINDFLOW_PLUME_MODEL=1` releases a small heater's power as a rising plume (Morton-Taylor-Turner, entrainment 0.12 from the literature, not fitted) one cell layer above the heater when the plume is too small for the grid (D*/Dx < 1). On 19a it reads 3.65 / 4.55 / 5.96 / 7.07 K at Dx 0.268 / 0.198 / 0.134 / 0.100 m (off: 1.35-1.76 K), inside 1.8-8.6 K at every cell size and energy within 1 %, but it is not grid-independent (it doubles across that range), wall-mounted and Surface sources are not covered, wind does not bend it, and it was run on the CPU only. Off by default. Evidence: `audits/2026-10-28-fixes/plume/plume_results.md`.
7. **Outdoor air counts as fresh.** The air age is set to zero outdoors, so exhaust that leaves a room and is drawn back in counts as fresh. On the sample house the (opt-in) online age agrees with a tracer
   within **9 %** in every room (Batch 6f+6k); a case with strong re-entry at a facade has not been tested.
8. **A simulation box with an open top keeps the older boundary ring.** The whole-building run has the new wind ring (Batch 6b); a velocity inlet through a cut wall diverged in the checks
   (FC4, 48b v1), so user boxes with an open top keep the earlier equilibrium ring.
9. **Pressure is a little noisier without gusts.** With steady wind the density fluctuation is **9.6e-5 against 6.3e-5** before (1.5 times; Batch RZ, 270 degrees). With gusts it is
   2.07e-3 against 2.04e-3 (+1.6 %) (Batch RZ).
10. **Supply turbulence is tuned for patches 4-16 cells across.** The turbulence factor table covers 4, 6, 8, 12 and 16 cells (2.06, 1.41, 1.31, 1.17, 1.22) and is clamped outside. One cell off the face the delivered
    turbulence is 0.95-1.01 of the request; further downstream it drifts: a 4-cell patch delivers 0.63 of the request 3 cells away and 0.44 at 9 cells, an 8-16-cell patch 1.03-1.21 at 3-9 cells (Tu 0.19, Batch RP).
    A patch under 4 cells delivers less (3 cells about 0.35-0.5 of the request, 2 cells axial only) and the app notes it. The total momentum reads 3-9 % high at Tu 0.19 (the added turbulent term).
    It applies to Revit air terminals under High only.
11. **The fine box sharpens the local view only.** Room air changes and opening flows always come from the whole-building run, because a two-way coupled box was built and did not beat it. Against
    a uniform run at the box's cell size (Batch 6l):

    | Measure | Focus box (one-way) | Whole-building run |
    |---|---|---|
    | w-hall-e opening error | 14.7 % | 11.1 % |
    | Hall total-ACH error | 12.7 % | 3.3 % |

    (The two-way version read 16.0 % and 11.7 %: no better, so it was reverted.) Particles, slices and probes inside the box show the finer field.
12. **A diffuser moves room air and adds no fresh air.** In a sealed room it brings in no outdoor air either (a body-force device creates no mass); add a leakage path or a transfer opening.
13. **Standard fresh-air ACH is the online age (followed through time) in each room where it settled, and the steady age, marked "steady age", in a room where it did not; the steady age can differ from the online one by about 30 % either way per room (AGE-FIX, audit P14-06, RA-01); High has its own split (AGE-HIGH).** Fresh-air ACH = 3600 / mean age equals an air-change rate only when the room's air is fully mixed; otherwise it is the room's mean-age equivalent (Etheridge & Sandberg 1996; Hurraß 2026). The steady age is solved on the landed time-mean flow without a resolved turbulent scalar flux, so it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones. Reason for the steady fallback (the Standard landing before the per-room change, AGE-FIX): the online age cannot always settle inside a run (it needs about one room fresh-air age; Bedroom 2 was still rising after 2000 s physical), so the earlier landing was dominated by its warm-start seed (warm against cold start 11.6 % mean / 17.8 % max, 14–23 % too high on average against a 4x longer run). The steady age scatters 1.5 % / 6.5 % (0.8 / 2.7 % on long runs). A room on the steady age has no fresh-air ± range (the total exchange keeps its ±); a room on the online age shows its averaging range. **High (AGE-HIGH, gusty wind):** the online age lands as the fresh-air ACH and the steady age on the averaged flow is shown as "Fresh-air (steady age)"; the headline is the age followed through the gusty flow, and the steady-age value ignores gust mixing and usually reads lower, by up to about 3 times (sample house, queue V's High job: steady/online age ×2.04 Living, ×3.07 Bedroom 2, building 13.3 against 27.0 ACH). It is not universal per room (Kitchen's steady-age value was 2 % above its online value on a coarse CPU grid). The grid-dependence limitation above is unchanged (the GPU grid check ran, queue W2, FAIL: building fresh-air spread 18.8 %, rooms up to 33.9 %, `audits/2026-10-12/runs-w2/VERDICT.txt`). Evidence: `audits/2026-10-12/fixes/bAGE/age_summary.txt`, FIXLOG "Experiment AGE-1", "Batch AGE-FIX", "Queue V2" and "Batch AGE-HIGH".
14. **Glass removal on import is per material.** Meshes are merged per material on import, so a glass material shared by a curtain wall and windows is removed as a whole (a curtain-wall material name keeps it, but a shared name cannot be split).
15. **Cross-ventilation flow rates tend to read high.** Against a wind-tunnel cross-ventilation case (Tominaga & Blocken 2016, five opening layouts) WindFlow's flow through the building is 1.6-2.2 times the paper's tracer-gas values; that reference itself reads low (for one layout, independent opening-flux measurements and an LES are 1.6-1.9 times its tracer value), and against those WindFlow reads about 10-35 % high. The ranking of the layouts was right. Use the numbers to compare options; treat absolute flow rates as likely high. See [`VALIDATION.md`](../VALIDATION.md) section 4.

## Other known limits

- Thermal is beta: checked against textbook cases, not measured rooms. Each room is held at the indoor temperature by an ideal heater/cooler by default (walls are adiabatic). The wall model is experimental and wind-only and gave mixed, grid-dependent results on the measured rooms; its laminar-shear check WM1 fails (2.57 % against 2 %, queue V; the Smagorinsky viscosity inside the wall model acts under laminar shear; pre-existing, off by default).
- **Viscosity floor.** The solver holds the lattice viscosity at a stability floor. On a building-scale grid the simulated air is therefore about 150–850 times
  more viscous than real air (Standard ≈ 310×, Preview ≈ 470×), and jets through windows and supplies behave like laminar jets (effective Reynolds number of a few
  hundred instead of ~10^5). The validation cases run at 4–28× and do not cover this regime. Treat absolute ACH and jet reach as indicative: moving the floor to
  1e-4 changed the sample house's Living ACH by −30 % and reversed the Kitchen window's flow (P11-01).
- **The fine box is one-way.** It sharpens the local velocity field only; a room's ACH and its opening flows come from the whole-building run (Known limitations, item 11; P11-02).
- **Notes on the result.** Beyond the mass balance the app says when a result sits outside what was checked: openings under 4 cells across, openings 4–6 cells across in Preview
  (size quantised to the grid, ±1 cell row), openings left blocked by the carve, rooms ventilated mainly by gusts (flagged under Standard), the wall model being on, a domain
  blockage above 3 %, a grid clamped to graphics memory, and a sealed room whose supply and extract differ (WindFlow balances them at the supply flow and names the room).
- Still open (see [Known limitations](LIMITATIONS.md) and [`VALIDATION.md`](../VALIDATION.md)): the viscosity floor, domain blockage in the default Compact domain, the smooth-ground wind profile, the discharge coefficient of openings under 4 cells and of rotated openings.
- Not validated: single-sided ventilation, openings narrower than 4 cells, and absolute ACH in a real building.
- The ± shown on the online (High) fresh-air figure is the averaging range of the kept batches; it leaves out slow drift of the age, so the true uncertainty is wider (re-audit RA-09; the range is labelled "averaging range (excludes slow age drift)"; a corrected estimator is planned after release).
- The mass-balance check compares inflow and outflow relative to the larger of the two, so a building ventilated through one side (small, nearly balanced flows) can show a red "mass balance" warning on a physically sound result. The warning errs on the side of alarming, not hiding (re-audit RA-15; revisit after release with measured cases).
- Stairs and double-height spaces: the air in a void that opens into the storey above is counted in the lower room up to its top, so if the upper storey is drawn as a separate room, the void's air counts in both. Air above furniture is counted up to the storey top, so a double-height room needs a storey height that covers its full height (re-audit RC-01/RC-03).
- Imported (mesh) models: detected rooms read total exchange 37-45 % higher than the same building drawn by hand (queue V2, the S3 native/imported pair, `audits/2026-10-12/runs-v2/COMPARE.md`). The room band merge (OF-1) adds the air between an outline and its walls to the room, and in detected rooms it likely over-counts the flow near openings; the merge is also all-or-nothing per air pocket (re-audit RA-06, RA-10; not fixed). Compare imported models with each other, not with drawn ones.
  High mode's ranges are wide in gusty wind (see [`VALIDATION.md`](../VALIDATION.md), Known limitations).
- Devices (fans, split AC, etc.) are in progress. A device's reported flow is the flux through its outlet face; a split AC delivers about 3 % below its set flow, not above it (P13a-01).
- A nested simulation box is isothermal only (greyed out while Thermal is on), and slows the live solve several-fold (see above).
- Simulation box faces must not cut indoor air in v1 mode (see above).
- No IFC import yet; only Revit export and the app's own `model.json`.
- No code signing on the Velopack installer yet.
- Comparative design guidance, not certification CFD.

## Roadmap

See [`PLAN.md`](../PLAN.md) for the phased plan and open decisions.


## Validation

Run with `dotnet run -c Release --project src/WindFlow.Sandbox` (synthetic buildings, no Revit needed). The latest full
run (2026-10-10) passed 222 of 222 checks; the physics fixes of 2026-10-12 changed the numbers, so the rows below carry the queue V (2026-10-20) values where it re-measured them. `--selfcheck` 126/126 (the app); Sandbox `--fixes` 97, `--audit` 23, `--walls` 5, `--orifice` 5 (+ the 58b note), `--fluxcons` 21, `--nested` 8, `--hold` 6, `--boundary` 7, `--devices` 22, `--inlet` 16, `--hybrid-gpu` 2, and the fast default list (18a-c, 19d-f, 22a-d; 19a and 19c print [WAIT] there) passes; 19a fails at 1.30 K when it runs, a known and documented FAIL. All of this is the 2026-10-20 Batch RW run on the final tree (logs in `audits/2026-10-12/fixes/bRW/`). The queue V full run (2026-10-20, 83 minutes) passed 253 and failed 3: 19a (1.30 K), and 53 and 56b (the legacy loop and the warm-start check, which fail because the wind inflow settles slowly and depends on how the run started, see Known limitations); the wall-model check WM1 fails too (2.57 % against 2 %; pre-existing, optional feature, off by default). Every row, with its reference and band, is in [`VALIDATION.md`](../VALIDATION.md); these are a
selection.

| Check | Result |
|---|---|
| Cross-ventilation: effective discharge coefficient of two windows in series | 2026-10-20 queue V run 0.559, band 0.37–0.80; 2026-10-10 run 0.510 |
| Façade pressure (windward–leeward ΔCp) | 2026-10-20 queue V run 1.018, band 0.5–1.6x; 2026-10-10 run 0.982 |
| CPU vs. GPU parity | 0.0 % difference (flow, thermal, Hybrid kernels) |
| Stack (buoyancy-driven) flow vs. Reynolds-corrected orifice reference | 2026-10-20 run (Batch RW): ratio 1.213 (stack scene free-running since Batch RT), band 0.6–1.4; 2026-10-10 run 0.892 |
| Heated sealed-room energy balance vs. P·t/(ρ·cp·V) | 5.4 % (Batch 6e+6j, 2026-10-13; not re-measured: 19c prints [WAIT] in the 2026-10-20 default list); 4.8 % on 2026-10-10, band 20 % |
| Heated cube cavity Nusselt vs. Tric et al. 2000 (n = 160) | 2026-10-20 queue V run (after the per-face geometry fix P14-02): 2.056 / 4.358, 0.1 % / 0.5 % from Tric (Ra 1e4 / 1e5), band 5 %. 2026-10-10 run, old test geometry: 3.6 % / 2.9 % low |
| Sealed filling box ΔT(2.5 m − 0.5 m) at ~1750 s | 1.30 K on the corrected 3.0 m room, band 1.8–8.6 K: **below the band** (2.13 K on the old 2.73 m room); cause: an under-resolved plume, 1.35 / 1.35 / 1.55 / 1.76 K at Dx 0.268 / 0.198 / 0.134 / 0.100 m, not grid-converged (Batch S diagnosis) |
| Exhaust age of air vs. nominal (Volume/Inflow) | 2026-10-20 queue V run 5.3 % error, band 10 %; 2026-10-10 run 1.3 % error. The opt-in online age on a closed 4 x 4 x 2.8 m room (BENCH 8): +0.05 % without a fan, -0.08 % with one (Batch 6k) |
| GPU throughput (RTX 5070 Ti) | ~3,100 million lattice updates/s isothermal, ~1,300 with thermal (Accurate grid); before the opt-in online age, which adds 6–8 % per step (Batch 6k); not in queue V |

Against **measured** rooms the result is mixed (latest runs, see [`VALIDATION.md`](../VALIDATION.md)): cross-ventilation (Tominaga-Blocken
2015) meets all four metrics in the queue V run on the fixed code (2026-10-20, 40 cells per building height, measured approach turbulence, product wind ring: FAC2 0.78 by the Hanna & Chang W rule, 0.619 by the literature definition; FB 0.12, NMSE 0.22, q 0.746 against 0.66). At 20 cells per height it fails (q 0.286; pre-existing under-resolution, every earlier 20-cell run read 0.24-0.33).
The pass is for that one case at that grid. The MINIBAT ceiling jet is still just
short on the hit rate (FAC2 0.94 by the W rule, q 0.643 against 0.66); the IEA Annex 20 room fails it and is slightly worse than before (q 0.471 to 0.453, and |FB| 0.308, just past its 0.3 band, at the new default inlet with 4 % turbulence). No measured room has
been used to check the thermal model.
