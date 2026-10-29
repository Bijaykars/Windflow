# WindFlow plan v2 — accuracy, GPU budget, redesign (2026-09-28)

Inputs: the RESEARCH_PROMPT_3 report (2026-09-28), REDESIGN_NOTES.md, HANDOFF.md, VALIDATION.md.
Replaces the remaining phases of PLAN.md. PLAN.md's Decisions table still holds.

## Decisions

| topic | decision | why |
|---|---|---|
| Priority | Accuracy before features. The next release is an "accuracy release". | User: "more accurate, fine if slower". Research ranks the accuracy fixes highest per week. |
| Opening flow | Integrate over the carved throat cells, not the nominal area. Report an effective Cd per opening. | Removes the known ~14 % low bias in the number architects read. |
| Velocity walls | Han–Ooka wall-function bounce (Spalding law) on top of the existing signed distances. | Same accuracy as bounce-back on a grid twice as fine (Han, Ooka & Kikumoto 2021). |
| Thermal walls | Han et al. 2025 temperature wall function plus interpolated walls on the D3Q7 lattice. | Twice-grid accuracy for < 3 % cost. Targets the 12–16 % cavity error. |
| Collision | Keep regularized BGK + Smagorinsky. Cumulant only after wall functions are validated. | Evidence of cumulant benefit on coarse grids with walls is mixed. |
| Storage | FP16 distributions, FP32 arithmetic, DDF shifting (Lehmann et al., PRE 2022), behind a switch. | Halves memory traffic, which is what the GPU spends its time on. |
| Esoteric pull | **Deferred.** I differ from the report's rank 5. | The paper itself says the speed gain on dedicated GPUs is insignificant, and memory is not our constraint (6 M cells ≈ 0.6 GB with FP16). Revisit only if laptop measurements demand it. |
| GPU budget | App-side duty-cycled burst governor with measured GPU time, yield-style synchronisation, viewport frame caps. | Driver priorities only act inside one process. Clock locking needs admin rights. |
| Validation metrics | FAC2 ≥ 0.5, abs(FB) ≤ 0.3, NMSE, hit rate q ≥ 0.66 at D = 0.25, flow rate within ~10 %. | The criteria peer-reviewed papers and VDI 3783-9 / COST 732 use. |
| Public claims | "Validated against measured rooms" only after Annex 20 2D, MINIBAT and cross-ventilation pass. | Answers the LinkedIn critic's question properly. |
| Confidence | One High / Medium / Low badge per result. Grid convergence index only in Expert. | Reviewer expectation: grid sensitivity, converged average, closed balances. |
| IFC | Write a minimal STEP reader for IfcSpace, IfcOpeningElement and walls. | Settles the open licence question: nothing LGPL or MPL is shipped. |
| Compare A/B | Keep, minimal, Expert only: two runs, synced camera, per-room difference table. | Early design is comparative; a scenario manager is not needed. |
| Naming | Preview (fast, directional) and Refine (slower, reportable). | Copies Autodesk Forma's two-tier split. |
| Legal guard | Every agent brief says: papers only; never open FluidX3D, Palabos, OpenLB, waLBerla, lbmpy, OpenFOAM, Ladybug/Butterfly, IfcOpenShell or web-ifc source. XLB and Neon (Apache-2.0) may be read. | FluidX3D is non-commercial; the rest are copyleft. |

## Lanes (parallel by file ownership)

- **Lane S, solver kernels** (LbmSolver.cs, LbmNested.cs, Voxelizer.cs, Analysis.cs, Sandbox checks). One agent at a time, strictly in order. Only Lane S runs the full sandbox.
- **Lane V, validation harness** (new `src/WindFlow.Sandbox/Validation/`, `data/validation/`). Parallel with S. Runs only its own cases.
- **Lane A, app** (src/WindFlow.App). Parallel. Layout and visual work on Opus 5.5.

## Phase 0 — safety net and loose ends

0.1 **First git commit plus an MIT LICENSE file. Needs the user's yes.** Phase 1 deliberately moves validated numbers, and without history there is no way back.
→ verify: `git log` shows the commit; `tools/licence-inventory.ps1` still exits 0.

0.2 Sandbox writes `out/checks.json` (id, value, band, pass) and gains `--compare <baseline.json>` that prints per-check deltas. Save today's run as the baseline.
→ verify: two runs of the same build differ only in MLUPS.

0.3 Finish the three items cut on 2026-09-24 (see HANDOFF.md): nested-box refresh on the GPU; the app side of the Revit live link with the fake-Revit test; viridis in the report images.
→ verify: `--nested` passes with nested total ≥ 1500 MLUPS; the live-link headless test log shows model, opening and acks; a report plan image read back.

## Phase 1 — accuracy release (Lanes S and V)

1.1 **Carved opening area** (S). Opening flow is the integral over carved throat cells. Effective Cd per opening, flagged outside 0.5–0.8. Every moved threshold in checks 2–16 gets a written physical reason; the orifice check becomes a Cd check.
→ verify: the "nominal area ≈ 14 % low" line leaves VALIDATION.md; full sandbox passes; `--compare` shows the expected shifts only.

1.2 **Velocity wall function** (S). Han–Ooka WFB with Spalding's law, CPU and GPU, `RunOptions.WallModel { BounceBack, Interpolated, WallFunction }`. Default switches to WallFunction only after 1.4 shows it helps.
→ verify: new turbulent channel check against the log law; WFB at 2Δx within 5 % of interpolated bounce-back at Δx on the cross-ventilation scene; CPU = GPU.

1.3 **Thermal wall function** (S). Han et al. 2025 plus interpolated anti-bounce-back on D3Q7.
→ verify: cavity Nusselt at n = 48 closer to Fusegi than today's 12 % / 16 %; heated-room energy balance still within 5 %; cost within 5 %.

1.4 **Measured-room harness** (V, starts on day one). A `ValidationCase` format (geometry, boundary conditions, measured profiles with uncertainty), metrics FAC2 / FB / NMSE / hit rate, profile plots to `out/validation/*.png`, tables written into VALIDATION.md automatically. Data: an agent fetches the open-access sets (Nielsen AAU pages, MINIBAT Data in Brief, AIJ database, CBE fan data) and lists any paywalled paper for the user to download. Case order:
   1. IEA Annex 20 2D (Nielsen, Rong & Olmedo 2010)
   2. Annex 20 3D room, isothermal then heated
   3. MINIBAT jets (Gresse et al. 2020 data; LBM-LES reference 2024)
   4. Tominaga–Blocken 2015 cross-ventilation, then Karava via Ramponi–Blocken 2012
   5. AIJ single building (Kikumoto et al. 2026 database)
   6. CBE ceiling fans (Liu et al. 2018 plus the field dataset)
   7. ASHRAE RP-1009 diffusers
→ verify: Annex 20 2D runs end to end on today's solver as a baseline, then again after 1.1–1.3 with the delta shown.

### 1.4b Fixes for the three failing measured cases (2026-09-28, from the RESEARCH_PROMPT_4 report)

Baseline: Annex 20 2D FAC2 0.09; MINIBAT FAC2 0.09; cross-ventilation Q 0.585 (FAC2 0.33–0.37). These come before
1.2 and 1.3: a wall model cannot be judged while the supply is 30–60 % short and the room flow is laminar. Each
step is kept or reverted on the three cases plus the full sandbox (`--compare baseline.json`).

| # | step | lane | why | verify |
|---|---|---|---|---|
| F1 | Supply patch → link-wise velocity (Ladd moving-wall) bounce-back on the patch face + PI flux controller | S | Equilibrium reset fixes the cell, not the face flux (42–71 % delivered) | delivered/set 1.00 ± 0.02 at 1/2/4/6 cells; Annex + MINIBAT re-scored |
| F2 | Harness: cross-vent Q vs opening-flux data (Karava PIV 0.40, Hwang & Gorlé LES 0.44–0.47), tracer 0.25 separate; guideline domain default; MINIBAT `--inlet disk`; lattice params from `LbmSession` | V | Part of the "+134 %" is apples-to-oranges | tables regenerated |
| F3 | Opening edges: no Bouzidi on convex sharp corners; isolated sharp-orifice Cd check vs 4/8/16 cells and Re | S (experiment first) | Interpolated corners act like rounded edges (Cd 0.8–0.9 vs 0.61) | orifice Cd 0.60–0.65 |
| F4 | Turbulence in the room: floor 5e-4 → ~1e-5, Cs ~0.1 test, seeded perturbation on supply faces and wind inflow | S (experiment first) | Floor holds Re_eff = 160·N → TI ≤ 0.003, laminar coherent jets | room TI > 0; jet dips; Q in 0.40–0.47 |
| F5 | Only if F4 is unstable or insufficient: HRR collision on D3Q19 (Jacob, Malaspinas & Sagaut 2018); WALE | S | Robust low-viscosity LES | jet decay, then the three cases |
| F6 | 2:1 box to ≥ 8–10 cells across supply and openings; SEM inflow (Buffa 2021); then 1.2 wall function | S | Resolution floor, jet spreading | 4 → 8 → 16 cells |

**F3/F4 results (scratch `dbg3/h1h2`, 2026-09-28):** cross-vent over-prediction = isolated-orifice Cd too high
(0.70–0.76 at 4/8/16 cells, any Re; 2-D slot 0.75–0.79 vs 0.611; square jet barely contracts at ≤ 8 cells) ×
jet carry-over (~11 %). Bouzidi off on corners: −2–3 % only. Floor 1e-5: stable, raises Q. Cs 0.1: diverges with
inflow turbulence. Crude inflow modes distort sealed ΔCp (not divergence-free). No variant makes the jet dip.
Decisions: (1) HRR experiment now (scratch `dbg5`, F5 promoted). (2) Add the sandbox orifice-Cd check (8 cells,
Re ~1e4, band 0.60–0.65; fails today at 0.746) after F1 lands. (3) If HRR plus the nested box cannot bring
orifice Cd to ≤ 0.65, add a **calibrated opening-discharge model** for openings under ~16 cells: a Forchheimer
loss (Guo body force) in the throat cells sized from the rig's Cd(cells) curve, behind
`RunOptions.OpeningModel { Resolved, Calibrated }`, documented as a sub-grid model. Synthetic inflow only as
divergence-free SEM; it is not expected to move mean Q.

**HRR result (scratch `dbg5`): rejected.** Built CPU+GPU from Jacob et al. 2018; stable at Cs 0 to opening Re 5e4
with steady inflow, but it does not move Cd (0.73–0.76 at 4/8/16 cells, every collision → ~0.73 at 16), Q
(0.55–0.68), jet decay (the viscosity floor decides the jet, not the collision) or room TI, is ~10 % slower on GPU
and diverges with inflow turbulence at low Cs. Opt-in patch kept at `dbg5/corepatch.py`. Conclusion: the excess
cross-vent flow is an opening-resolution problem that neither collision nor 2× nesting fixes → **next Lane S step
after F1: the calibrated opening-discharge model** (Forchheimer loss K = 1/Cd_target² − 1/Cd_solver(cells)², with
Cd_solver from the orifice rig; target 0.61 by default, per-opening Cd later) plus the sandbox orifice-Cd check.
Verify: rig 0.61 ± 0.02 at 4/8/16 cells; cross-vent Q into 0.36–0.52; sandbox `--compare` with reasons.

**After F1 (2026-09-28):** Annex FAC2 0.58 / FB 0.46 / q 0.33; MINIBAT 0.60 / 0.20 / 0.30. The misses in q are in:
- Annex: the floor return, which is 57 % weak and has its sign reversed in half the misses.
- MINIBAT: the jet core, 18 % weak, and the lower shear layer, 60–76 % weak. The jet is too narrow.

The scratch jet-decay study found the viscosity floor sets the jet: at a floor of 1e-4, centreline decay is near the classic law.

Next accuracy step, after the calibrated opening model:
- lower the floor to about 1e-4;
- add a seeded 2–5 % perturbation on the supply faces;
- recalibrate the Cd_solver(n) table at the new floor.

Keep this only if Annex and MINIBAT q rise and cross-vent stays in its band. Then 1.2, the wall function, for the Annex floor return.

1.5 **Gate.** Cases 1, 3 and 4 inside their bands before any "validated against measured rooms" wording appears in README, VALIDATION.md or posts.

## Phase 2 — GPU budget and efficiency (Lane A first, then S)

2.1 **GPU budget governor** (A, plus a small hook in S). A slider at 25 / 50 / 75 / 100 %, default 60. Solver bursts with measured device time, a PI controller on burst length, bursts ≤ 12 ms, yield-style CUDA synchronisation, viewport capped at 30 fps while solving and 5 fps when unfocused or minimised. Show measured duty, steps per second and time to converged.
→ verify: nvidia-smi `utilization.gpu` and `power.draw` sampled at each setting on the sample house; the 50 % setting reads 45–55 %; the UI stays responsive.

2.2 **Smarter convergence** (S). Stop when the batch-means 95 % interval of every room's ACH is below 5 %. Averaging starts after the largest room's flow-through time. Preview-to-Refine restart by interpolation.
→ verify: runs stop by themselves and report their interval; Refine from Preview converges in fewer steps than from rest (measured).

2.3 **FP16 storage** (S), from the Lehmann PRE 2022 paper only.
→ verify: every sandbox check and every measured-room metric within tolerance of FP32 via `--compare`; MLUPS and memory before and after.

2.4 **Startup tier** (A). A two-second bandwidth micro-benchmark picks cell budget, particle count and frame cap. The "250k" control leaves the top bar for Settings.
→ verify: chosen tiers logged on the RTX and the AMD iGPU; iGPU stays ≥ 15 fps.

Deferred: esoteric pull, sparse tiles, multi-level outdoor grid.

## Phase 3 — trust layer (S then A)

3.1 Seven automatic self-checks per run: mass balance, energy balance, Mach and lattice-speed cap, sealed-room leak test, opening Cd range, duplicate or zero-thickness openings, time-average drift.

3.2 Confidence badge per result from the Preview-versus-Refine ACH delta, the batch-means interval and the residuals. Any failed self-check caps it at Low and says why.

3.3 Geometry-driven refinement rules, applied at load and explained to the user:
- opening ≥ 8–10 cells across: coarse is enough;
- 4–8 cells: nested box around it with 1–2 opening widths of margin;
- < 4 cells, a fan disc < 8 cells, or a room < 10 cells in its short side: full fine run;
- one-way nesting refused where it feeds back (an AC intake near its own jet, a box spanning a cross-ventilation path).
→ verify: each self-check has a sandbox scene that trips it; the rules are checked against the cross-ventilation case at two resolutions.

## Phase 4 — redesign (Lane A; can start after 2.1)

4.1 **Rooms for every model.** Flood fill on the solver grid, erosion by half an opening width, region growing. Revit or IFC names when present, otherwise "Room 3 (north, 24 m²)". Rename and merge. Clicking a room highlights its volume as a translucent block.

4.2 **Openings for meshes.** Detect gaps between room regions and outdoors; classify door, window or vent by size and sill height; toggle them like Revit openings.

4.3 **Basic mode as its own product.** Load → wind → openings → Preview or Refine → per room four numbers (ACH, dead-zone %, freshness, comfort flag), one path view plus still air, the confidence badge, PDF. Nothing else is visible.

4.4 **Expert mode.** Box with refinement rules, devices, slices, probes, minimal Compare, sweep, annual, live link, exports, GPU budget and tier.

4.5 **Devices.** Presets first, snap to surface, constrained drag, and a live "resolution OK / refine needed" indicator tied to 3.3.

4.6 **Hallway test.** A written 30-minute task script for an architect who has never seen the app. Findings go back into 4.3–4.5.
→ verify: full-window screenshots of Basic and Expert read back; `--selfcheck` extended to room naming and opening detection.

## Phase 5 — later

Minimal IFC reader; cumulant collision; synthetic inflow turbulence; multi-level outdoor grid; esoteric pull; humidity, radiation and aerosols (RESEARCH_PROMPT_2 topics); crash reporting; code signing; macOS spike.

## First session once approved

Phase 0.1–0.2 inline, then three agents at once: 0.3 nested refresh followed by 1.1 (Lane S); 1.4 harness with Annex 20 2D (Lane V); 0.3 live link followed by 2.1 governor (Lane A).

## Needed from the user

- Yes to 0.1 (git commit + MIT LICENSE).
- Download any paywalled validation paper the harness lists.
- Optional: an architect for the 30-minute test in 4.6.
- Verdict on the still-air view.
