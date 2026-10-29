# WindFlow — handoff (2026-09-24, stopped by user)

**2026-10-29 OWNER DECISION: licence changed to MIT** (`LICENSE`). No dual licensing; outside contributions are welcome under MIT. The 2026-10-27 note below is superseded.

**2026-10-27 OWNER DECISION (superseded): licence.** WindFlow is licensed under the GNU AGPL v3 (`LICENSE`, the official gnu.org text, sha256 0d96a4ff...abcb0). A paid commercial licence for closed-source use will be offered later (dual licensing). Consequences: keep every shipped dependency permissive (all are today, see `docs/LICENSES.md`); IfcConvert (LGPL) stays an external binary; before accepting outside contributions, add a contributor agreement (CLA) so the owner can still sell the commercial licence. This supersedes the older "MIT LICENSE" to-do below. The product name may change.

**2026-10-20 status (queue V done).** The GPU re-run queue V finished on the fixed tree (63 jobs, `audits/2026-10-12/runs-v/`, summary `runs-v/SUMMARY.csv`, analysis `audits/2026-10-12/fixes/QUEUE_V_RESULTS.md`, follow-up reruns in `audits/2026-10-12/fixes/FIXLOG.md` Batches V2 and V3); `README.md` (Known limitations) and `VALIDATION.md` carry its numbers. Open items: room ACH is not grid-converged (a 0.6 % Dx change moves rooms 12-36 %); the default 10-minute Standard run does not settle; Compact reads +8 to +33 % on room total exchange; Annex 20 regressed (q 0.453, |FB| 0.308) and cross-vent H = 20 fails (q 0.286); Sandbox 19a, 53, 56b and WM1 FAIL; the batch 6b inflow drifts slowly (53, 56b); the product still prints "7-27 %" for the Compact bias; the G6b age does not reproduce the 156-158 s reference (136.0 / 159.4 s with the hold off); imported opening names point at the wrong rooms; the orifice gate at n = 4 and the S3 native/imported gap (34.1 against 29.8 ACH) are open.

**2026-10-20 batch 5 status note (read first).** This file is a running log. Its older blocks predate fix batches 1-6l (2026-10-12 to 2026-10-20). Stale in it:
(a) lines 6 and 31, "the AC unit delivers ~12 % above its set flow": the split AC delivers about 3-4 % below its set flow, and the 12 % was a measurement artefact (P13a-01);
(b) the 2026-10-09 stationarity-rule block (about lines 90-129): the headless MaxBatches 60 (since RW-01 `AveragedPass`, app and headless, has no batch limit: it ends on Converged, Diverged, the time cap or 3 x the cap on the raw clock; only the Sandbox-only `ConvergeCore` keeps MaxBatches 60), the exact `--cap` for High, the 4-flow-through plan and the `ConvergeCore` loop it describes were replaced by `AveragedPass` (Batch 6f: one driver for the app and headless; settle 2x cold / 4x warm / 1x thermal; the cap is real wall time minus pauses; a room is Settled only when its online air age is too, Batch 6k; since AGE-FIX that applies only with the opt-in online age), and the Standard and High ACH lists in it are pre-fix numbers (queue V, 2026-10-20, re-measured the Standard run: 0 of 7 runs settled in 10 minutes; building fresh-air 20.1 ACH);
(c) "Living 13.3 vs 20-23.5 ACH" predates the fresh-air ACH definition and the later fixes (not in queue V; its High run read building fresh-air 27.0 ACH against 20.1 in Standard).
Current limits, with sizes: `README.md` (Known limitations) and `VALIDATION.md` (sections 4 and 5); the fix log is `audits/2026-10-12/fixes/FIXLOG.md`.

**2026-10-10 READY FOR AUDIT. Next step: run `FULL_AUDIT_PROMPT.md` (it is the auditing agent's brief).**
- **State:** UI honesty fixes done: (1) a pass with under 16 batches in all, or under 5 kept for a room, shows "± —" (tooltip "run too short to estimate a range") and the status "Time limit reached: too short for ranges, use a longer budget"; one rule in `WindFlow.Report/ResultStatus.cs` (`TooShort`, `AchCell`, `Text`, `ReportLine`) used by the room table, the pass strip and the PDF (± column shows "—"); (2) the report cover's Quality line is `ResultStatus.QualityLabel` (Preview / Accurate Standard / Accurate High; without a convergence report it keeps `Options.Quality`); (3) the Fine box toggle is greyed out while Thermal is on, tooltip says why (`ToolTip.ShowOnDisabled`, set in `ShowAccurateControls`, refreshed from `RefreshThermalState`). Retaken shots: `audits/ui-2026-10-10/c-high-landed.png` (WINDFLOW_ACCURATE_CAP_S=60, `--shotmode accurate-time --accurate-mode high --focus Kitchen --gpu 100`: every row "± —") and `f-report-page1.png` (page 1 of the new `c-high-landed.pdf`). README.md and VALIDATION.md rewritten to match the code (measured-room latest table and a "Known limitations" section added by hand; the harness was NOT run, so the generated block still holds the older runs and will be rewritten the next time it runs).
- **Gates:** `dotnet build WindFlow.sln -c Release` 0 errors; `WindFlow.App.exe --selfcheck` ALL PASSED (new check: too-short rule); last full Sandbox run 222 PASS / 0 FAIL (2026-10-10, before these UI-only edits, no Core change since). Not re-run after the UI edits: Sandbox, `--converge-stats`.
- **Open items (all stated in VALIDATION.md "Known limitations"):** Annex 20 ceiling jet decays ~25 % too fast (q .471 at s8); no measured-room thermal validation (Thermal stays "beta"); single-sided ventilation not validated; High's ranges are wide in gusty wind; steady vs gusty differ (Living 13.3 vs 20-23.5 ACH); the AC unit delivers ~12 % above its set flow (to investigate); openings under 4 cells are unreliable; MINIBAT q .646 (band .66) is the only near-miss.
- **Also noticed, not fixed:** the pass-strip tooltip for a capped pass still says "each room's ± is its 95 % range" even when the run was too short for ranges; a sealed room (Bathroom, 0 ACH) shows "0.0 ± —"; the Fine box toggle keeps its checked state while greyed under Thermal (the plan still drops the box and says so in the pass line).

**2026-10-10 UI honesty pass + High Accuracy controls (ui-designer). Build 0 errors, `--selfcheck` 41 PASS, shots in `audits/ui-2026-10-10/`.**
- **Accurate controls (both modes, footer above the pass strip, `AccurateControls`):** Standard / High segmented pair; High shows the 5/10/20/30 min budget (bound to `Settings.HighAccuracyCapMinutes`, saved on click, read by LiveSolver when the next High pass opens) and a "Fine box on <room>" toggle that captures the room selected/isolated when switched on (`focusRoomId`; re-select by toggling off/on). Wired through `ApplyAccurateControls` -> `LiveSolver.SetAccurateMode`. One-line note under them. Disabled while locked (UiTable row `AccurateControls`, ChangesFlow).
- **Status after the pass lands:** `LiveSolver.FinishAccurate` now attaches a `ConvergenceReport` (monitor's last `ConvergenceStatus` under the pass's own `accReason`, so a wall-cap end is `Capped`) to `AccurateStats.Convergence`. New `WindFlow.Report/ResultStatus.cs` is the one mapping: Converged -> "Settled", anything else -> "Time limit reached: ranges shown", null -> "Preview"; also `AchCell` ("13.3 ±0.6") and `HalfWidth(report, roomId)`. Pass strip: "Standard · Settled · 5.1M cells at 170 mm"; capped passes get the warm chip (`Border.chip.pass.done.capped`), never the green. Room rows: ACH "13.3 ±0.6" (95 % half-width, column MinWidth 64), Expert second line adds "varies ±X % batch-to-batch"; row tooltip says what ± is and whether the aim was met. Preview rows keep "~".
- **Labels:** ACH header tooltip + rooms footer say ACH counts all incoming air (outside and other rooms) and point to air age; air-age column visible from the start; probe rows prefixed "live" (Preview) / "averaged" (Accurate) with an "indicative: one cell" hint; "Thermal (beta)" with the textbook-not-measured-rooms tooltip and note.
- **Expert, experimental:** Settings `WallModelRow` checkbox "Wall model (experimental, wind-only)" -> `LiveSolver.WallModelWanted` (applied in Start and ConfigCommand option builds, restarts via Reconfigure), greyed out while Thermal is on, off by default each launch. Device inspector "turb" field (%, Tag `turb`) -> `AirSource.TurbulenceIntensity` (fraction; blank/— = null = preset default), acts only in High.
- **Report:** cover "Result" line (`ResultStatus.ReportLine`), rooms table "± 95 %" column after ACH, "Age min" (was "Age s"), a four-line footnote under the table (ACH definition, age, ±, status).
- **Shots:** `--shotmode accurate-time` now drives the panel controls for `--accurate-mode high --focus <room>`, writes `<name>-expert.png` (Expert Results tab, second line) and `<name>.pdf` (the report of the landed result) beside the shot, and logs a `status` line (pass text, chip tone, every row's ACH cell). `--shotmode device-presets` opens the inspector's Details.
- **Not done / follow-ups:** cap changes while a High pass is running apply to the next pass only (tooltip says so); the fine-box toggle is not greyed out under Thermal (the plan drops the box and says so in the pass line); `--solve` headless reports already carry `Convergence`, so their PDFs get the same status line untested here; no "as of" time on the room table (the trust line is the stale signal).

**2026-10-10 Hybrid thermal GPU port DONE, and `ThermalScheme.Hybrid` is now the product default (supersedes "CPU-only / default Lbm" in the block below). Keep Lbm via `RunOptions.ThermalScheme = Lbm` or env `WINDFLOW_THERMAL_SCHEME=lbm`.**
- **Code:** `Core/LbmHybridThermal.cs` (GPU section: `EnsureGpuHybrid`, `HybridRingKernel`, `HybridDensityKernel`, `HybridEnergyKernel`, `StepGpuHybrid`, `InitHybridGpu`, `PullHybridStats`) + `LbmSolver.cs`: `StepKernelThermal` takes flag bit 2 (T plane in via `curG`, rho*/kappa/q out via `nxtG`; no D3Q7), `InitGpu`/`UpdateCells`/`UpdateDeviceUnits`/`ReadTemperature`/`CaptureGpu`/`Dispose` branch on `_hybrid`; the NotSupportedException is gone. No D3Q7 buffers are allocated on Hybrid. Step order on the GPU: boundary kernel, ring T, flow kernel (collision + rho*,kappa,q), density kernel (rho* of the non-fresh cells), energy kernel, room flux, swap.
- **Stability / diagnostics on the GPU:** non-finite T flag and running extrema (minT, maxT, max outflow fraction, max 6 kappa) live in an 8-int device buffer (order-preserving int keys, atomics only when a cell beats the stored value); heat sum by `Atomic.Add`. They are read at the existing sync cadence (every 50 steps and at the end of `Step`), so a breach throws at the next sync, not the exact step (CPU throws on the step). `Hybrid*` properties, `HybridEnergy`/`HybridMassSum` work on both backends.
- **Equivalence (new check 19f, `-- --hybrid-gpu` alone, ~2 min):** 150k cells, CPU vs GPU, max|dT| 4.0e-5 K (sealed 3 kW, 4000 steps), 6.3e-5 K (cross + heater in a 3 m/s wind, 800 steps), 3.4e-5 K (supply/return + heater, 800 steps); energy diff 0.0000 % (limits 1e-3 K, 0.01 %); injected heat identical (7814.8 K-cells). 41e: mean T 25.372 / 25.372, duty 2360 / 2360 W.
- **31d (n = 160, GPU, Tric 2000 2.054 / 4.337, band 5 %):** Hybrid 1.981 (-3.6 %) / 4.213 (-2.9 %); D3Q7 1.974 (-3.9 %) / 4.168 (-3.9 %). 31d runs Hybrid; `WINDFLOW_31D_SCHEME=lbm` runs the D3Q7 scheme through the same assertion. 31a/b/c at n = 48 under Hybrid: 1.847 (-9.9 %) / 3.993 (-8.0 %), GPU = CPU 0.0 %.
- **Step cost (heat scene, scratch bench `scratchpad/hy/bench`, ms/step Lbm -> Hybrid):** GPU 400k cells 0.21 -> 0.26 (1.23x), GPU 1.56M cells 0.71 -> 0.95-0.99 (1.34-1.38x); CPU 400k cells 6.5-9.1 -> 14.7-16.4 (about 2.2x on this scene, not the 1.4x of the earlier estimate; CPU timings are noisy, 8 threads). In the app (`--shotmode thermal`, Accurate 5.09M cells, CUDA): isothermal 3105 MLUPS, thermal Hybrid 1292, thermal Lbm 1762 (Hybrid = 1.36x the Lbm step); the `[hybrid]` line from `WF_HYBRID_DIAG=1` appears only with the default scheme, so the app does run Hybrid on the GPU.
- **Gates with Hybrid as the default (no env):** build 0 errors; `--selfcheck` ALL PASSED; `--converge-stats` PASS; full Sandbox ALL CHECKS PASSED, 222 PASS / 0 FAIL (221 + 19f); `--devices` all PASS incl. 41e and 41g (17.62 C, band edge +0.30 K). Sandbox `WINDFLOW_CHECK_ONLY` now skips the stack/heat/comfort scenes unless the regex matches 18, 19 or 22.
- **Not done / follow-ups:** the GPU running minima/maxima are exact (per step) but throws are only seen at the sync; the nested box path is still isothermal-only; check 19a/41g still force the CPU backend.

**2026-10-10 Hybrid thermal scheme (D3Q19 flow + mass-consistent FV energy equation), Phase 1 CPU, behind `RunOptions.ThermalScheme` (default Lbm; env `WINDFLOW_THERMAL_SCHEME=hybrid`).**
- **Code:** `Core/LbmHybridThermal.cs`. Each of the 18 D3Q19 links is an FV face with the LBM's own mass flux m = f*_q(A) - f*_qbar(B) (links into solids carry 0; the free-slip lid links wrap in-plane like the gather; Supply cells use SupplyBounce). T_new = (rho* T - sum m T_face + diffusion + rho* q) / (rho* - sum m), T_face = van Leer MUSCL along the link, upwind by sign(m), advected quantity T - tRef. Diffusion central over 6 faces, kappa = alpha_mol + nu_t/0.9, no floor; CFL (outgoing mass fraction < 0.5) and 6 kappa < 1 asserted (throws). `RunStepThermal` got optional `hyb`/`tHyb` (flow pass only, no D3Q7 allocated). Hybrid on the GPU throws `NotSupportedException`. Diagnostics `Hybrid*`, `WF_HYBRID_DIAG=1`.
- **Why this form:** the earlier face-averaged-velocity form (T uniform not preserved, -T div u from non-solenoidal node velocities) failed 19d by 2 K and put 42 C in the 26 C stack scene; the u.grad form lost 20 % energy. The link-flux form keeps a uniform T uniform to round-off (check 19e: 0.0 K in the 19d room and in a 3 m/s windy scene) and a sealed room's energy above tRef to 0.004 % (19e) / 0.07 % (19a, 1447 s). Residual lattice mass drift (float round-off, ~4e-7 per s) times 22 K is why the energy is taken above the outdoor T.
- **Results (Lbm -> Hybrid):** 19a ambient dT dx 0.268 at 1447 s 0.585 -> 2.29 K (room mean 2.13, band [2, 9]); dx 0.134 at 655 s 0.837 -> 2.38 K; far-field 0.043 -> 0.016 m/s; min T = initial; 19d PASS (0.000 K); 18/19b/19c/22 PASS; cavity Nu error vs Tric -12.1 / -12.0 % -> -10.1 / -7.9 %; 41a-d,f PASS.
- **41g reframed (decided):** "return <= room mean + 0.5 K" assumed mixing ventilation (a cold jet over warm air is stably stratified; the old scheme's diffusion floor made it hold). Now an energy balance on a known load: sealed adiabatic 3 x 3 x 2.6 m room, 600 W heater, split AC 14 C / 0.15 m3/s; steady (time-averaged return of two successive 156 s flow-throughs within 0.05 K) T_return = T_supply + Q/(rho cp V_set) = 17.32 C, +-0.3 K. Hybrid 17.62 (+0.30 K, PASS at the band edge, steady after 4 flow-throughs), Lbm 17.77 (+0.45 K, FAIL). V is the SET flow (the sink acts on it); against the 12 % higher flow measured through the outlet cells the error would be +0.66 / +0.82 K. Room mean is printed only. The old 41 scene has no load (steady state = T_supply after ~25 min), so 41g has its own room; `-- --ac-balance` runs it alone.
- **19a and 41g now run Hybrid explicitly** (the product default stays Lbm until the GPU port); 19b/19c/others still Lbm. VALIDATION.md rows 19a/19c/41g and the AC note updated. Sandbox `WINDFLOW_CHECK_ONLY=<label regex>` runs only matching checks among 18-31.
- Trimmed by owner request: Lbm at dx 0.134 not rerun; Hybrid 0.134 only to 655 s. The 5 % Tric criterion is deferred to 31d (n=160) after the GPU port; the CPU criterion is |Hybrid err| <= |Lbm err| at n=48.

**2026-10-10 19a grid refinement and reference check (measurement only, no product change; harness `scratchpad/thermal2`, CPU, default domain).**
- **Same-time ΔT(2.5−0.5 m) at t = 655 s (600 s + 55 s window):** dx 0.268 → 0.316 K, 0.200 → 0.604, 0.134 → 0.784 (far-field speed 0.037 → 0.033 → 0.024 m/s, α floor 13.8 → 10.3 → 6.9 ×10⁻³ m²/s). Full-time 0.268 reproduces 0.544 K. 0.200 diverged at ~1250 s; 0.067 was not run (the GPU was held by Blender at ~99 %, CPU would take ~19 h).
- **Trend:** three-grid fit p ≈ 2.3, apparent limit ≈ 0.9 K, but the grids are not asymptotic. A 1-D filling box with ambient diffusivity = the α floor gives 0.62 K at dx 0.268 and the same profile shape, so the floor controls the answer at these dx. It needs κ below about 0.001 m²/s (dx below about 0.02 m) to reach 4 K.
- **Reference:** "4–8 K" is a point source at the floor (deep-debug `fb/` model, 2026-10-05). For this 1 m² × 0.5 m heater the same model gives 2–8 K at t = 1420 s (α_e 0.083–0.13, virtual origin 0–1 m below the floor), so the reference was loose but the sim is still 4–15× below even the low end.
- **Verdict:** a finer Dx or focus box (option a) will not close it. Option b (FV T with TVD advection) is indicated. First measure whether the 0.024–0.037 m/s spurious far-field currents (about 5× the physical 0.005 m/s) also need fixing. Sandbox `Domain` box + Thermal diverges on this sealed room (follow-up).

**2026-10-10 thermal floor study (check 19a, sealed 500 W room; dx 0.268 m, dt 39.2 ms).** Full Sandbox 220 PASS / 0 FAIL (53 and 54 now pass).
- **Part 0:** `Simulation.ConvergeCore` `hardStepCap` = max(old, `session.Steps` + (MaxBatches+1)·batchSteps); 53/54 converge at 16 batches.
- **Diagnosis:** the floor is a cause, not the only one. tauG 0.55 gives α 0.0229 m²/s (~1100× air, 25× the ν floor), H²/α = 392 s against a 464 s filling box. FLatMax engages in 0.00 % of room cells at every stable floor (needs 52 K, the room peaks ~16 K above ambient); it only acts once the lattice already diverges, so it stays.
- **Bug fixed:** D3Q7 w0 1/4, w1 1/8 has c_s² = 1/4, not 1/3. Equilibrium now 4c·u and tauG = 4α + ½ (`GInvCs2`, CPU and GPU). Steady Nu is unchanged (same Péclet); 19a 0.212 → 0.306 K at floor 0.55.
- **Floor sweep, 19a dT(2.5 m − 0.5 m), old code:** 0.55 0.21 K · 0.52 0.76 · 0.51 3.3 (room T down to 18.8, undershoot) · 0.505 13 K but negative g (2292) · 0 diverges. New code BGK: 0.54 and 0.53 stable (19d mean speed 0.0052 / 0.0091 of the 0.01 m/s limit), 0.52 fails 19d (0.019 m/s), 0.51 negative g and a 3.4 K spurious stratification. **TauGMin 0.55 → 0.53, chosen by stability and checks only; 19a = 0.543 K** (filling box says 4–8 K).
- **TRT** (Ginzburg 2005, Servan-Camas & Tsai 2008) is in `CollideG` behind `const GMagicLambda` (0 = off). Λ = 1/4 is worse than BGK: 71 k negative g at 0.55, diverges at 0.52 and 0.7; Λ = 1/6 diverges at 0.55, Λ = 1/12 has negative g at 0.52. Off.
- **Why 19a stays 8–15× short:** BGK stability ends at tauG ≈ 0.53, the cell-Péclet limit of a 0.27 m grid (plume ~0.35 m/s). Next options: a finer grid for 19a, a limited (TVD) thermal advection, or a TRT variant with another Λ (needs a decision; the brief fixed Λ = 1/4).
- **Cavity** (new check 31d, 3-D cube, n=160 GPU, vs Tric 2000 2.054 / 4.337, 5 %): before 1.972 / 4.165, after 1.974 / 4.168 (−3.9 %). The 2-D de Vahl Davis values 2.243 / 4.519 are only printed: a 3-D no-slip cube sits 8.4 % / 4.0 % below them, so 5 % to 2-D is not assertable.

**PC HUNG 2026-10-10 during the overnight run; the session ended.**
- The agent "Hybrid FV temperature solver (CPU)" was stopped mid-evaluation. Its transcript is `~/.claude/projects/<repo>/c84e4a6c-…/subagents/agent-a8c1bf6eeaf270161.jsonl`.
- Its last source edits were at about 04:02 (`LbmHybridThermal.cs` new; `LbmSolver.cs`, `Simulation.cs`, `Sandbox/Program.cs`).
- The app exe was built at 04:02:35, so that build compiled.
- A full no-argument Sandbox run was in progress at 07:17 and hogged about 8 cores. That is a likely contributor to the hang.
- **On resume:**
  - Read the transcript's Edit calls and check the hybrid edits.
  - Run heavy jobs at BelowNormal priority with capped parallelism.
  - Then continue the declared hybrid evaluation (trimmed scope: dx 0.268 for both schemes; dx 0.134 Hybrid-only to 655 s). After that: the GPU port if it passes, then the UI pass, then docs.

**STOPPED BY USER 2026-10-07 (token budget; resume next week).**
- The agent "Smirnov decorrelation in wind turbulence" was stopped mid-task. It had edited `WindTurbulence.cs`, `LbmSolver.cs` and `Sandbox/BoundaryChecks.cs` (last writes about 01:00, partial).
- No WindFlow processes are running.
- **First on resume:**
  - Review those three files for the half-done ω change.
  - The intent: phase k·(x−Ut) + ω_m t, with ω_m = g_m/τ, g_m ~ N(0,1) drawn after all existing draws, τ = L/σ_u. It is added in double inside `TimePhases()` and in host `WindTurbulenceStep`.
  - Finish or revert, then build, `--selfcheck`, `--boundary` and `--nested`.
- **Then:**
  - Cross-vent `--wind-tu` rerun into `out/validation-wind-tu-smirnov/` (compare .730/.231/.351/.651, jet peak .25).
  - A 40-min in-app High rerun (old: Bedroom 1 rel sd .35, ρ1 .71, trend −36 %/h, landed 100.8).
  - Then decide High's stopping rule: probably time-budgeted averaging with an honest ± range, possibly MSER-5.
  - Then the ui-designer.
- **RESUMED 2026-10-09: Smirnov temporal decorrelation finished, measured.**
  - **Code:** phase is k·(x−Ut) + ω_m t, ω_m = g_m/τ, g_m ~ N(0,1) drawn after all old draws, τ = L/σ_u at `WindTurbulence.TimeRefHeight` (HighAccuracy: zRef; cross-vent: H). `WindTurbulenceField.SetTimeScale` is called from `EnsureTuCache`; ω·t is added in double in `TimePhases()` and in host `Eval`. The GPU kernel is unchanged.
  - **Check:** 57d now also asserts the zero-convection inlet u′ autocorrelation at 5τ < 0.2 (measured −0.07).
  - **Gates:** build 0 errors; app `--selfcheck`, Sandbox `--boundary` (57f max_du 3.3e-6 U_ref) and `--nested` ALL PASSED.
  - **Cross-vent** (auto h40, `--wind-tu measured`, normal-only throat; `out/validation-wind-tu-smirnov/smirnov-windtu/`): old → new FAC2 .730 → .730, FB .231 → .264, NMSE .351 → .316, q .651 → .667 (now PASS), Q/(UH·A) .481 → .454, x/D=.5 jet peak z/H .25 → .25 (13 min, 17 blocks, converged).
  - **In-app High, 40 min capped, `--gpu 100`** (kept 0 in both; the new run lands the mean of all 222 batches):

    | Room | landed old → new | rel sd | ρ1 | trend %/h |
    |---|---|---|---|---|
    | Living | 20.2 → 22.8 | .30 → .28 | .32 → .27 | +11 → +7 |
    | Kitchen | 37.0 → 42.7 | .40 → .37 | .43 → .30 | −31 → −5 |
    | Hall | 45.0 → 48.3 | .68 → .63 | .38 → .24 | −59 → −39 |
    | Bedroom 1 | 100.8 → 113.2 | .35 → .28 | .71 → .63 | −36 → −14 |
    | Bedroom 2 | 24.2 → 25.7 | .38 → .38 | .22 → .12 | −20 → −11 |
    | Landing | 100.0 → 108.8 | .32 → .31 | .47 → .40 | −21 → −11 |

    - Still capped, Hall tolerance NOT MET. Hall's trend is still p .02 (AR .06).
    - Rel sd barely moved, so the inlet signal was not the main source of the noise.
- **2026-10-09: new batch-means stationarity rule (Accurate Standard/High and headless `--solve`), measured.**
  - **Rule** (`ConvergenceMonitor`): drop d(n) = largest power of two <= n/2 batches (0 for n<2), keep the rest. CI = t(df)·s/√kept·√((1+ρ̂)/(1−ρ̂)), ρ̂ = lag-1 of all n batch means (+ Marriott–Pope bias, clamp [0,.9], 0 under 8), df from n_eff. Stationary = first-half vs second-half kept means within max(tol, 2·halfWidth). Converged = kept >= 5, every room hw < tol and stationary. `RoomConvergence` gains `RelSd`, `Rho`, `Stationary`.
  - **Files:** `Core/Convergence.cs`, `Core/FlowFieldMean.cs` (new `BatchWindowMean`: two running windows (d,n] and (2d,n], replaces LiveSolver accPrev/accSum/accTrans and ConvergeCore allC/prevC/sumC), `Core/Simulation.cs` ConvergeCore, `App/LiveSolver.cs` (TakeBatch/FinishAccurate, High cap), `App/MainWindowAccurate.cs`, Sandbox `Program.cs` (new check 54b, also `-- --converge-stats`), `AuditChecks.cs` (BatchCount is now kept batches).
  - **High cap:** `AccurateCapThisPass` = env WINDFLOW_ACCURATE_CAP_S, else High = `SnapCapMinutes(Settings.HighAccuracyCapMinutes)`·60, else 600 s.
  - **54b:** AR(1) ρ .7 coverage .960 (the plain rule gave .875), transient bias +.068 ACH, 94.5 % within tol, accumulator max diff 1.2e-7.
  - **Standard** (cap 900, `--gpu 100`): Converged at batch 52 (d 16, kept 36), 544 s (old 63 / 703 s). Landed = monitor mean: Living 13.29, Kitchen 35.57, Hall 55.34, Bedroom 1 107.75, Bedroom 2 25.39, Landing 103.14 (old 13.50 / 35.58 / 54.39 / 107.73 / 24.94 / 102.85).
  - **High** (default 10 min, no focus): Capped at batch 57 (d 16, kept 41), 600 s, all rooms NOT MET. Landed ± hw (RelSd): Living 23.53 ± 1.96 (.26), Kitchen 44.95 ± 5.80 (.33), Hall 51.40 ± 10.11 (.57), Bedroom 1 119.25 ± 29.24 (.27), Bedroom 2 27.45 ± 2.75 (.33), Landing 116.60 ± 17.50 (.28).
  - **Headless** `--solve --quality accurate --cap 15` (snaps to 10 min): completes, 4 batches, capped, MaxBatches 60 is the headless limit for High. Gates: build 0 errors, `--selfcheck` ALL PASSED, full Sandbox 218 PASS / only 19a FAIL (known, thermal, unrelated); 53, 54, 54b, F9a/b, audit and nested all pass.
  - **Review fixes 2026-10-09 (build 0 errors, `--selfcheck` ALL PASSED; Standard run + full Sandbox re-run after the 54b(d) fix below):** `Rho`/`Stationary` assignments were swallowed by a trailing `//` comment (restored); Converged now needs total n >= `MinTotalBatches` (16); t past df 30 = Cornish-Fisher; LiveSolver reads `AccurateModeWanted` once (`mode`); `BatchWindowMean` weights every batch equally (true sample total still lands); ETA `ConvergedFraction` uses the smallest N with N - d(N) >= needKept and N >= 16 (SelfCheck expectation 12 -> 16 batches); FinishAccurate reads the GPU back only when no batch mean exists; ConvergeCore cap comment fixed.
  - **54b(d) fixed by a stricter STOP test (2026-10-09 pre-declared experiment, candidate A wins):** stop when the 99 % half-width (t at 0.995, same df and rho-hat correction) < tolerance; the reported CI stays 95 %, tolerance, truncation, MinTotalBatches 16 unchanged. Old rule worst cell 0.371; A 0.045, C 0.043 (pass; A has the smaller Standard-like mean n 56.5 vs 58.1); B 0.356 and D 0.289 fail. Final 54b(d) table (mean 10, tol 0.5, cap n=256, 2000 trials; miss | stopped, mean n at stop; cells with < 50 stops printed, not asserted): rho 0: sd .25 0.000 n16.7, .5 0.001 n20.7, 1 0.009 n47.3, 2 0.014 n174.9; rho .5: .25 0.000 n24.0, .5 0.004 n39.2, 1 0.025 n118.6, 2 stops 2 % (4/42, 0.095, not asserted); rho .7: .25 0.000 n35.8, .5 0.018 n64.9, 1 0.045 n174.6 (74 % stop), 2 stops 0.1 % (not asserted). Cost: mean n-at-stop in rho {0,.5} x sd {.5,1} 34.2 -> 56.5.
  - **Gates after A:** build 0 errors; `--converge-stats` (a)-(d) PASS; `--selfcheck` ALL PASSED; full Sandbox 216 PASS / 3 FAIL: known 19a, plus **53 and 54 (new, open)**: `Simulation.ConvergeCore` `hardStepCap` (max(PlannedSteps, warm-up+flow-through) x 8) ends those runs at 8 batches (Capped), below `MinTotalBatches` 16, so Converged is unreachable (introduced by review fix 4, not by A; 218-PASS run predates it). Needs a decision on the headless step budget.
  - **Standard after A** (cap 900 s, `--gpu 100`, desktop GPU not quiet, 18 s/batch vs 10.5 s before): Capped at batch 49 (d 16, kept 33), 902 s; NOT MET: Living 13.31 +/-0.59 (tol 0.67, 95 % CI, 99 % is wider), Hall 55.19 +/-2.96, Bedroom 2 25.35 +/-1.34 (not stationary). Landed = monitor mean to 0.01: Living 13.309, Kitchen 35.658, Hall 55.194, Bedroom 1 107.752, Bedroom 2 25.351, Landing 103.134. Rho .22 / .00 / .14 / .31 / .12 / .00, all stationary except Bedroom 2. The 99 % test needs ~1.8x the kept batches, so Standard likely will not settle inside 900 s even on a quiet GPU.
- **Done this session (2026-10-06/07, verified):**
  - **Nested box:** no bug. The 77 steps/s was contention. The box's design cost is 1.3–1.7×.
  - **GPU wind-turbulence kernel:** check 57f, 3.3e-5×U_ref.
  - **Accurate publish rate:** 4 Hz.
  - **Timings (idle GPU):**
    - Standard converges at 703 s, over the 600 s default cap.
    - High and High+box never converge in 40 min.
  - **Capped-pass landing fix** (`LiveSolver.TakeBatch/FinishAccurate`): a capped pass with kept < 5 now lands the mean of all post-settle batches, equal to the monitor's mean. It used to land a 0–14 s partial window.
  - **MaxBatches comment** corrected; the wall cap governs.
  - **Analysis** (scratch `b\`):
    - The pair stationarity test fails on noise alone (all-room pass probability about 1e-5 per pair).
    - The monitor CI is understated about 2.4× when ρ1 is 0.7.
    - MSER-5 / half-split would accept Standard at batch 38 against 63.
    - High's drift is the deterministic inlet signal: 2 near-frozen Kraichnan modes, periods 6581 s and 17954 s, holding 9 % of the variance; correlation 0.96 with Bedroom 1's ACH.

**2026-10-06: the user chose plan A** (ship the proven pieces; the wall model stays experimental). Steps:
1. **High Accuracy backend v1 done.**
   - Code: `HighAccuracy.cs` and `HeadlessSolve.cs` (`--solve --quality preview|accurate|high --wind 4,270 --cap 10`).
   - Per-supply Tu: `AirSource.TurbulenceIntensity`, `RunOptions.SupplyTuPerSource`, check 55i.
   - EN 1991-1-4 wind turbulence, with z₀ = 0.3 m mapped from exponent 0.22.
   - `RunOptions.WallCapSeconds` and `WarmupFlowThroughs`.
   - **But a global Dx/2 does not work:** 40.8M cells and 8.2 GB, about 3 h to settle (one flow-through of the longest room is 110k steps).
   - **Decision:** HighAccuracy = Accurate-Dx whole building plus the physics upgrades, plus a 2:1 nested fine box around one focus room, reusing LbmNested.
   - **v2 done (headless).**
     - `HighAccuracy.Plan(model, vram, accurateCells, focusRoomId?, thermal)` uses Accurate Dx plus an optional 2:1 `FocusBox`. Its margin is the sponge (9 fine cells) plus 2 cells, about 0.94 m.
     - `ConvergeDriver` and `Simulation.AnalyzeNested` (a Core port of the App's `AnalyseFields`) handle the nested case.
     - `Simulation.Run` now returns the batch-MEAN field.
     - Headless: `--solve --quality high [--focus id] --cap <min>`.
     - **Both 15-min runs were capped.** The strict converge needs about 790k steps (Living flow-through 103k steps), roughly 31 min without the box and 41 min with Bedroom 1 boxed.
   - **Decision:** in-app High Accuracy = the existing Accurate pass (about 9 min) + Tu presets + EN wind turbulence + an optional focus box. The strict headless converge stays for validation.
   - **In-app wiring done.**
     - `Accurate.cs` has `AccurateMode {Standard, High}` and `AccurateRecipe`; Standard returns the live options object unchanged.
     - `LiveSolver.SetAccurateMode(mode, focusRoomId)`; `RunOptions.Clone()`.
     - Shot mode: `--accurate-mode high --focus`.
     - The timing runs were all capped at 600 s and were likely contaminated by user app windows on the GPU (Standard at about 1340 MLUPS against 3270 clean).
     - Real findings:
       - Inflow turbulence widens ACH batch CIs 4–10×. High needs longer averaging, which is physical.
       - The nested accurate box has no bug. The 77 steps/s was most likely GPU contention. The box's design cost is 1.3-1.45x, because the sponge is 43 % of the fine cells.
       - **2026-10-06 perf pass.**
         - Cause 1: High lost 0.6-0.7 ms per step to the host `WindTurbulenceStep` (Parallel.For over ~85k ring entries x 64 modes, 1.7-2.4 ms, plus a 0.12 ms CopyFromCPU).
         - Cause 2: publishing the field at 12 Hz during the Accurate averaging phase took 32 % of wall time.
         - Fix 1: the mode sum now runs in `WindTuKernel` (`LbmSolver.cs`). `WindTurbulenceGpu` writes straight into `_dBVel`. Per step it uploads only 64 time phases, reduced mod 2 pi in double. The host version stays for check 57f in `BoundaryChecks.cs` (max |du| 3e-5 U_ref at step 1e5).
         - Fix 2: `AccuratePublishInterval` = 0.25 s in `LiveSolver.cs`. It applies only while an Accurate session runs.
         - Timings, steps/s settle / averaging, `--gpu 100`, idle GPU. Before -> after:
           - Standard: 480/330 -> 480-575/410-430.
           - High: 380/240-275 -> 520-610/450-470.
           - High + Bedroom 1 box: 260-300/165-175 -> 330-375/250-280.
         - **High time-to-settle (idle GPU, 2026-10-06, `--gpu 100`, cap 2400 s, sample house 5.09M cells @ 170 mm, cold start, GPU 0-8 % before each run):**

| mode | settle end | averaging ends | result | slowest room | steps/s (avg) |
|---|---|---|---|---|---|
| Standard | 12 s | 703 s (63 batches, ~11 s each) | Converged | Hall (continuously in band from batch 63) | 410-430 |
| High | 13 s | 2400 s cap (242 batches) | Capped, stationarity never found (kept 0) | Hall (hw 3.84 vs tol 2.25 at the cap) | 445-490 |
| High + Bedroom 1 box | 18 s | 2400 s cap (136 batches, ~17 s each) | Capped, stationarity never found | Kitchen, Hall, Bedroom 1, Bedroom 2 all NOT MET | 260-275 |

           - Per-room time (s, from the start of averaging) from which the room stays inside its own band (n >= 5, CI half-width < max(5 % mean, 0.2 ACH)):
             - High: Bathroom 70, Living 1334, Landing 1355, Bedroom 1 1627, Bedroom 2 2245, Kitchen 2397, Hall never.
             - High + box: Bathroom 105, Living 2335, Landing 2388, Kitchen / Hall / Bedroom 1 / Bedroom 2 never.
             - Standard (for reference): Bedroom 1, Landing, Bathroom 559, Kitchen 603, Bedroom 2 647, Living 670, Hall 703.
           - The pass-level gate is the start-up stationarity test: two consecutive batch means of EVERY room within 5 %. Standard found it at batch 46. High never found it in 242 batches (so "kept 0"), whatever the per-room CIs say.
           - Bedroom 1 drifts under High: batch mean ACH 123 -> 101 over 40 min while its CI shrinks 26 -> 4.5. Living 17.6 -> 20.2. The turbulent-inflow signal has slow wander, so more than 2400 s would be needed.
           - The in-app cap path ignores `ConvergenceMonitor.MaxBatches` (60): `LiveSolver` ends only on Converged / Diverged / the wall cap (`Capped` from the monitor does not stop it). High ran to 242 batches.
           - Capped runs land a field whose Living ACH (23.3 High, 9.7 box) differs from the monitor's last Living mean (20.2, 19.9). Unexplained: with kept 0 the landed field is probably the last one or two batches.
   - **STOPPED BY USER (2026-10-06, they are gaming).** The agent was stopped early ("clean timings + nested-box slowdown", scratch `hiacc4\`); no WindFlow processes were left running. It may have left partial edits in LiveSolver, LbmNested or LbmSolver, so check them, rebuild, and run `--nested` and `--selfcheck` first. **Resume with:**
     - the nested accurate-box slowdown (77 against 264 steps/s);
     - the clean re-timing of Standard, High and High + Bedroom 1 box, with no other GPU users;
     - High's time-to-settle per room (measurement cap 40 min);
     - then the ui-designer: a High Accuracy control with time estimate and focus room, per-device Tu in Expert, an experimental wall-model toggle, and "indicative" point-speed labels.
2. **Next:** the ui-designer adds a High Accuracy control, time/memory estimates, per-device turbulence in Expert, an experimental wall-model toggle, and "indicative" point-speed labels.
3. The user's YouTube link (Two Minute Papers, "This Fluid Simulation Should Not Be Possible") hit a CAPTCHA and was not watched. It may be about the SPH octree neighbour-search paper (Fernández-Fernández 2022); unconfirmed. The relevant takeaway is adaptive resolution (fine cells at openings), linked to the existing nested box.

**2026-10-05: the audit fix pass is COMPLETE.** The full report is `audits/2026-10-05/REPORT.md`, with the 13-defect table, the extra bugs, before/after and the limitations.
- Sample house: Accurate ACH per room with CI is in that report. The new check 19d is `--quiescent`.
- Open items:
  - `ThroatCells` at exactly 45°;
  - Bedroom 2's window Cd of 0.34 warning;
  - Preview ACH is a 3.5 s transient.
- **Viscosity-floor sweep done, 2026-10-05; floor NOT adopted.**
  - Results are in `out/validation-floor-sweep/`. The baseline was restored.

    | Floor | Annex 20 (FAC2 / q) | MINIBAT (FAC2 / q) | cross-vent (FAC2 / q / Q) |
    |---|---|---|---|
    | 5e-4 | 0.575 / 0.340 | 0.592 / 0.296 | 0.365 / 0.254 / 0.458 |
    | 1e-4 | 0.603 / 0.377 | 0.536 / 0.274 | 0.317 / 0.269 / 0.508 |
    | 3e-5 = 1e-5 | 0.594 / 0.405 | 0.54 / 0.27 | 0.253 / 0.222 / 0.513 |

  - Below 3e-5 the floor does not bind, so Smagorinsky Cs 0.16 sets the effective viscosity.
  - The cross-vent Q rise is a confound: the Cd_s(n) calibration table was measured at 5e-4 and must be re-measured at any new floor.
  - Next levers: grid refinement; shear-improved Smagorinsky (RP5); a Cd_s table per floor.
- **Grid refinement at default settings, 2026-10-05.** Results are in `out/validation-refine/`.
  - Annex 20, slot-cells 8 (10.8M cells, 31 min): FAC2 0.613, FB 0.281 (in band), NMSE 0.308, q 0.471, up from 0.575 / 0.454 / 0.566 / 0.340. The best result so far.
  - MINIBAT, nozzle-cells 6 (6.0M cells, 15 min): WORSE, at 0.465 / 0.172 / 0.454 / 0.205. **A method that gets worse under refinement is the key open question.**
  - cross-vent, h-cells 40: needs 17.1 GB against 16 GB available, so it did not run. It would need a smaller domain or the nested box.
- **3 % supply perturbation** (`WINDFLOW_SUPPLY_PERTURB=0.03`, RP5 value, untuned). Results are in `out/validation-perturb-3pct/`. Not adopted; Annex 20 and the sandbox are untested with it.
  - MINIBAT n4: 0.615 / 0.204 / 0.312 / 0.303 (slightly better).
  - MINIBAT n6: 0.496 / 0.152 / 0.404 / 0.222 (better than n6 without it, still below n4). The laminar-jet hypothesis explains only part of the refinement drop.
- **2026-10-05: the user is handing the MINIBAT investigation to another Claude session.** The in-session agent was stopped right after it started.
  - It may have written to scratchpad `minibat\`. Check for a modified VALIDATION.md or `out/validation/minibat-iso*`.
  - Clean copies: `out/validation-floor-sweep/baseline-5e-4/` and `out/validation-refine/`.
- **The MINIBAT refinement drop was explained by ChatGPT on 2026-10-06 and reviewed by Opus.** Its report and the region metrics are in scratchpad `minibat\codex\`.
  - Matched 13-block averaging gives q of .296 / .230 / .206 at n = 4 / 5 / 6. Averaging is ruled out.
  - The losses sit on the M1 median plane (102 of 127 extra misses), in the lower shear layer (bias −63 → −77 %) and at the ceiling: n6 does not bend up to the ceiling.
  - Why the coarse jet looks right:
    - The inlet's D3Q19 diagonal links put 2/(3n) of the flux outside the nominal footprint (17 / 13 / 11 %).
    - The physical floor (4.19 / 3.35 / 2.79e-4 m²/s) plus SGS (2.3 / 1.6e-4) is 30–45× molecular.
    - Both spread the coarse jet for numerical reasons. The measured jet is turbulent at the nozzle (T1 median Tu about 19 %); ours enters laminar.
  - Fixed: `Analysis.LayerCells`. Float rounding picked two throat layers at n5. It now picks the nearest layer; the selftest covers n 4/5/6, both axes and both signs.
  - Note: stopping a subagent does NOT kill its child validation process. Check `tasklist` after a stop.
- **Measured inlet turbulence fixes MINIBAT (2026-10-06).**
  - **Input.** `SupplyPerturbation` is now isotropic 3-component Tu and flux-neutral (`ApplySupplyScale` rescales over all room-bound links), with check 55h. The value is the T1 jet-core Tu 0.19 (n 27, mean 0.186), used once. Results are in `out/validation-inlet-tu/`.

    | Grid | FAC2 | FB | NMSE | q |
    |---|---|---|---|---|
    | n4 | .718 | .241 | .167 | .424 |
    | n6 | .814 | .126 | .120 | .648 |

  - Refinement now improves the result.
  - Caveats:
    - n6 stopped at 6 blocks.
    - T1 is both the source of the Tu input and a scored plane.
    - Default Tu is still 0, so the app is unchanged.
  - **Confirmed at a fixed 13 blocks** (results in `out/validation-inlet-tu/`):

    | Grid | FAC2 | FB | q | 95 % CI | q without T1 |
    |---|---|---|---|---|---|
    | n6 | .816 | .124 | .654 | [.642, .661] | .660 |
    | n5 | – | – | .479 | – | – |
    | n4 | – | – | .424 | – | – |

    - n6 is flat from block 6 to 13.
    - The trend is not monotone in step size: most of the gain is between n5 and n6, on M1. An n8 run is needed for grid convergence; lift the harness's 4–6 limit, about 80 min.
  - **Annex 20 with the spec's inlet Tu 4 %** (raw `592301_2d_benchmark_test.pdf` p.2 eq.5: k_o = 1.5(0.04 u_o)²) is slightly worse:

    | Grid | q before | q with Tu |
    |---|---|---|
    | s4 | .340 | .302 |
    | s8 | .471 | .425 |

    Not adopted. Annex 20's lever is resolution.
- **Wind-inflow turbulence implemented, not adopted.**
  - Code: `WindTurbulence.cs` (Kraichnan/Smirnov 64-mode, divergence-free, Taylor-convected), `RunOptions.WindTurbulence` (default off), checks 57d/57e.
  - Also fixed: the inflow kernels forced w = 0; they now use velZ, which is 0 in existing runs.
  - Input: Tominaga & Blocken 2015 eq. 2, k/UH² = 0.033·exp(−0.32 z/H). Two assumptions are flagged: L = H and σ ratios 1 : 0.8 : 0.5.
  - Results are in `out/validation-wind-tu/`:

    | cross-vent | FAC2 | q | Q/(UH·A) | windward Cp | jet peak z/H |
    |---|---|---|---|---|---|
    | baseline | .365 | .254 | .458 | .488 | .375 |
    | `--wind-tu measured` | .460 | .333 | .483 (above the band) | .579 | .375 (no dip) |

  - The dip probably needs facade downwash resolution.
- **The RESEARCH_PROMPT_6 answer came back 2026-10-06.** The user pasted two versions; the second is the usable one. Decisions:
  - **Checked, no trap:**
    - Smagorinsky is `τ = ½(τ0 + √(τ0² + 18√2 Cs² |Π|_F/ρ))` with the Frobenius |Π| (`LbmSolver.cs:381`). That is exact for ν_t = (CsΔ)²√(2S:S) with self-consistent τ_eff.
    - Cs 0.16 is the Cs convention, not C = Cs².
    - Cs 0.16 is above the 0.10–0.14 used in indoor LBM-LES (Han: 0.12). Do not change it except as part of a model change.
  - **Checked, no change:** the research's "measured Q/(UH·A) = 0.25" is the tracer value. Our 0.40–0.47 opening-flux band is documented in cross-vent SOURCE.md (Karava PIV 0.25/(1 − 0.37) and Hwang & Gorlé LES).
  - **Step A, a physics fix:** the throat loss is isotropic, F = −cρ|u|u (`DeviceForce`, line 420). It damps the jet's transverse/vertical momentum, which is the likely reason the cross-vent jet is too straight. Change it to normal-only, F = −cρ|u·n̂|(u·n̂)n̂, passing n̂ in the target slots when rate < 0. K was calibrated on straight flow, so `--orifice` must be unchanged.
  - **Step B, the wall model** (Han, Ooka & Kikumoto 2021, the Annex 20 LBM test, which found it about equal to a half-size grid):
    - Werner–Wengle explicit u_τ, at the first fluid node, with d from WallLinks q.
    - Slip u_s = (U − τ_w d/(ρν_w)) û_t through the Bouzidi moving-wall term; clamp to [0, U] and relax in time with ω ≈ 0.1.
    - The law uses physical ν; ν_w = lattice ν0 + ν_t.
    - The f0 mass correction is unchanged.
    - Option, default off. Pre-declared success: Annex 20 s4 q ≥ 0.471, and MINIBAT n6 (Tu 0.19) q within ±0.03 of 0.654.
  - **Later:**
    - resolved-fraction scaling of inlet Tu and device presets (Said, Jouini & Plett 1993 table);
    - shear-improved Smagorinsky;
    - thermal wall function (Han 2025) and a floor that applies only where needed;
    - cross-vent: inflow L_u ≈ 3H, k checked at the building, ≥ 10 cells per opening (nested box).
- **Convergence batch done** (`out/validation-conv/`; `Minibat.cs` now allows nozzle-cells 4..8).
  - **MINIBAT n8, Tu 0.19, 13 blocks (14.1M cells, 2.9 GB, 76 min): FAC2 .839, FB .084, NMSE .097, q .646** (q without T1 .650). From n6 to n8 the q is flat (.654 → .646) while the other three metrics improve: **converged**.
  - **cross-vent** on the auto domain (blockage 3.0 %):

    | Run | FAC2 | q | Q |
    |---|---|---|---|
    | h20 | .381 | .238 | .483 |
    | h40 (24.8M cells, 5 GB) | .365 | .206 | .479 |
    | h40 + wind-tu | .508 | .365 | .481 |

    - The jet peak is z/H .375 in **every** run: the grid, the domain and the turbulence change nothing. That points to the isotropic throat loss (Step A, now running).
    - Note: the default cross-vent is Dx 100 mm on a 42.5 × 22.5 × 12 m guide domain. The 17.1 GB run was h40 on the guide domain.
- **Step A done: the normal-only throat loss is now the default.**
  - The force is F = −cρ|u_n|u_n n̂, with n̂ carried in the target slots. Check F14.
  - The Cd_s table was refreshed to 0.675 / 0.735 / 0.720.
    - n8 and n16 are the re-measured Resolved Cd. The old 0.743/0.760 were stale, from before WallLinks and the mass correction.
    - n4 = 0.675 is set directly so the reference orifice gives 0.61; the series model is inexact at 4 cells.
  - 58b calibrated Cd: .611 / .609 / .615.
  - These suites all pass: `--orifice`, `--devices`, `--fixes`, `--walls`, `--boundary`, `--inlet`; Validation selftest.
  - **cross-vent results** (in `out/validation-throat/`):

    | Run | FAC2 | FB | NMSE | q | Q | jet peak |
    |---|---|---|---|---|---|---|
    | auto h40 + wind-tu | .730 | .231 | .351 | .651 | .481 | **.25 (= measured)** |
    | guide h20 | .492 | – | – | .286 | .453 | .375 |

  - **Best settings so far, q:**

    | Case | Settings | q |
    |---|---|---|
    | MINIBAT | n8 + Tu .19 | .646 |
    | cross-vent | h40 + wind-tu + normal throat | .651 |
    | Annex 20 | s8 | .471 |

- **Step B done: wall model built, default OFF** (`RunOptions.WallModel`, env `WINDFLOW_WALL_MODEL=ww`; isothermal only).
  - Code: Sandbox `--wallmodel`, a GPU `StepKernelWm`, and test hooks `TestBodyForceX` / `TestPeriodicX`.
  - **With the model on** (results in `out/validation-wallmodel/`):

    | Run | FAC2 | FB | NMSE | q |
    |---|---|---|---|---|
    | Annex 20 s4 | .660 | .312 | .265 | **.491** |
    | Annex 20 s8 | .642 | .276 | .280 | .472 |
    | MINIBAT n6 Tu .19 | .814 | .126 | .129 | **.618** (−.036) |
    | cross-vent best | .746 | .176 | .291 | **.667** (Q .508, above band) |

  - **The pre-declared rule FAILS** on MINIBAT (−.036, beyond ±.03), so the model is not adopted.
  - WM2b fails: u_τ² is 0.689 of G·h. **Cause: ν_w omits Smagorinsky ν_t,** because ν_t is computed only inside Collide. The delivered wall stress is about 1.45× the law's. This is an implementation gap against the spec, not tuning.
  - **Fixed the same day:** ν_w now includes ν_t from the node's previous post-collision populations, via the shared `NeqStress`/`SmagTau`/`SmagNuW`. WM2b now passes at 0.924.
  - **Model-ON runs after the fix** (results in `out/validation-wallmodel2/`):

    | Run | FAC2 | FB | NMSE | q | Notes |
    |---|---|---|---|---|---|
    | Annex 20 s4 | .802 | .259 | .177 | **.670** | all bands PASS, 2 min |
    | Annex 20 s8 | .632 | .260 | .213 | .500 | worse than s4: **non-monotone, investigate** |
    | MINIBAT n6 | .841 | .121 | .122 | .648 | |
    | cross-vent | .778 | .164 | .251 | .651 | Q .506 |

  - **The pre-declared rule PASSES.**
  - WM1 still fails: 2.23 % against 2 %. The Smagorinsky ν_t is non-zero in laminar shear, which gives a little slip. Left failing on purpose; the better fix is to compare WM1 against the analytic profile.
  - **Full sandbox against g3** (logs `full-off.log` / `full-on.log` in the scratchpad):
    - **Model OFF:** only 19a fails. Today's default changes (normal-only throat, Cd_s table, velZ) are clean.
    - **Model ON:** 9 flips.
      - 55a/b/c/f/g: supply delivered/set 0.90–1.04.
      - 45a: residual 10.2 %.
      - 20e: 10.4 %.
      - 54: warm refine slower.
      - 56b: 2.25 %.
    - Not adopted as default.
  - **The supply-node exclusion hypothesis was FALSIFIED.** Zeroing u_s at the 20 Fixed-adjacent nodes left 55c at 0.950; edits reverted, repo unchanged.
  - The real symptom: with the model ON the duct flux slowly oscillates (period about 3 averaging windows, 0.85–1.04). The time-mean plane flux varies along the duct (0.99 → 0.95 → 0.99). With the model OFF it is steady at 1.000. Probe in scratchpad `wallmodel3\probe`.
  - **Deep-debug verdict: no defect.** Details are in deep-debug MEMORY; scratch in `wmosc\`.
    - **Mass is exact:** the link flux F = 1.000 at every plane.
    - **The 0.955 is an observable bias.** A node-velocity plane sum M differs from the conserved link flux by the momentum of wall-bound populations. With slip walls, the wall rows carry backflow, so M reads 0.935–0.985.
    - **The oscillation is a start-up quarter-wave acoustic mode** (T 443 steps = 4L/c_s). It is present with the model OFF too, about 6× weaker. Slip walls damp it slowly (e-fold about 14k steps against 600). It decays.
    - Slip fraction is 0.974: 92 % of wall nodes are in the viscous branch, u_s/U = 1 − ν_p/ν_w with floor ν ≫ ν_p.
  - **Consequence:** room ACH (`LbmRoomFlux` uses face-mean node u) would be biased with the model ON.
  - **Done: link-flux room flux** (`RoomFlux.cs` builds per-face link lists; `LbmRoomFlux.cs` has the CPU and GPU kernels; the 55 observables use `PlaneLinkFluxX`). Solver dynamics are bit-identical.
    - Diagonal ownership is a heuristic: convex corners count at every A-side face; concave corners go to one destination face. Open domain faces still use node u.
    - Model OFF: 45a ACH +3.8 %. The full sandbox OFF has only 19a.
    - Model ON: 55a/b/c/f/g are cleared. **Still failing:**
      - 45a and 20e: the opening-throat u·n is still node-velocity based.
      - 54 and 56b: settling is slower because slip walls damp acoustics about 20× more slowly.
    - Check 2e's FlowField copy now carries RoomFluxes (Program.cs).
    - Process slip: the agent used python for some edits; endings were preserved.
  - **`--fluxcons` added** (FixChecks.FluxCons.cs, also run in `--fixes`): the exact invariant ΔM_room = −net link flux per step, on CPU and GPU.
    - FC1–FC5 and FC7 pass.
    - A mutation test (concave rule disabled) makes 5 scenes fail, so the test has teeth.
    - **FC6 FAILS**, a wall-less room in open air. The cause: convex-edge diagonal links are double-counted when both sides are open (72 of 2040 links). Real open-plan rooms hit this.
    - **Resolved by decision (b):**
      - Exact single ownership conflicts with exact per-face gross: FC7 and Reversal read 0.917/1.083, ±u/12 per edge cell.
      - Kept the hybrid, so per-face gross (which feeds ACH) stays exact.
      - FC6 is now a whole-run bound of ≤ 1 % of gross (0.388 % measured), with the limitation comment in FixChecks.FluxCons.cs.
      - `--fluxcons` all pass; `--fixes` 0 failures.
    - **Done: per-opening link flux.**
      - `OpeningFluxPlan` in RoomFlux.cs; `FlowField.OpeningFluxes`, blended by FlowFieldMean.
      - `AnalyzeOpening` takes FlowRate, EffectiveCd and MeanSpeed from it. Oblique openings and older fields fall back to u·n.
      - The ledger still splits the time-mean net by sign.
      - Model OFF: 45a residual 2.9 % → 0.0 %; 58b calibrated Cd .611 / .609 / .615.
      - Full sandbox OFF: only 19a. ON: 19a, 20e, 54, 56b.
        - 20e: the age-identity check uses its own node-velocity u·n with the ScalarSolver age; untouched.
        - 54 and 56b: acoustic settling.
  - **Annex 20 s8 verdict: ON at s4 is partly right for the wrong reasons.** Scratch in `annexs8\`.
    - ON q falls monotonically with grid: s4/s5/s6/s8 = .670/.547/.519/.500.
    - The s4 gain at x = H comes from a spanwise redistribution. Slip sidewalls carry the forward flow, which depletes the centre plane and gives a strong centre-plane return. The ceiling jet (y = h/2) gets worse.
    - Controls at s4:
      - sidewalls no-slip: y = h/2 q .39 → .71, overall q .52, FAC2 .75;
      - floor and ceiling no-slip: the result equals OFF.
    - **The floor/ceiling slip helps the floor return for real.**
    - The ceiling jet decays about 25 % too fast at every grid: the real remaining gap.
    - The wall model is NOT ready as the default. Next idea: a sidewall-only no-slip variant, plus a spanwise-uniformity metric.
  - **Perf defect introduced by the link flux:** `AccumulateRoomFluxGpu` runs every averaging step and drops GPU speed 2650 → 465 MLUPS on Annex s8 (5.7×). **FIXED.**
    - Cause: the opening throat pseudo-face (16k links) ran on one GPU thread.
    - Fix: a segmented two-kernel accumulate in LbmRoomFlux.cs.
    - Speed with flux on is within 3–4 % of flux off: Annex s8 2900–3160 MLUPS; sample-house Preview 2370–2610.
    - Room flux is bit-identical; opening flux differs by 4e-7 relative.
    - These suites all pass: `--fluxcons`, `--fixes`, `--audit`, `--devices`, `--inlet`, `--orifice`, `--nested`; also the app selfcheck.
  - **Before the wall model can be the default:**
    - the s8 verdict;
    - a start-up ramp or acoustic damping (54, 56b);
    - 20e (age advection on link flux, or accept a marginal result);
    - the thermal sessions inconsistency: the model is ignored when thermal is on.
  - The user asked about a hole in a ceiling. It is the deliberate 2.5 × 2.5 m stair void (`SampleBuilding.cs:39`, x 6–8.5, y 5–7.5, above the Hall and under the Landing) with no stair modelled. Suggested: label it or draw stairs, and have detection recognise floor voids.
  - **Before the wall model can be the default:**
    - link flux for the opening ledger (45a, 20e);
    - acoustic start-up damping or a ramp (54, 56b);
    - the Annex 20 s8 non-monotonicity.
  - **Nothing is committed to git.** Recommend that the user commits soon.
  - **Later, if needed:** a start-up ramp to avoid exciting the acoustic mode.
  - WM4: a pre-existing float32 mass drift of about 1.5e-8 per step, present with the model off too. Logged, not fixed.
- **Running now:** the ui-designer (Fable) on room isolate (view-only), plain-language slice titles and legends, and the Basic/Expert split. Screenshots go to scratchpad `ui\`.
- **App direction (told to the user):** standalone exe first, plus connectors (the Revit live link exists; IFC import next, licence check needed). Mesh import and Room/OpeningDetect already exist. Ask the user for a real student model to test detection.
- **Must do before any default change:** the full sandbox (`--compare out/checks-2026-09-28-g3.json`). Solver defaults have changed since the last full run: the throat loss, the Cd_s table and the inflow velZ.
- **NEXT:**
  1. MINIBAT: use the inlet-Tu result. Then consider a footprint-exact inlet (the 2/(3n) leak) and shear-improved Smagorinsky. Check the inlet patch model at n6, the ceiling wall-jet resolution, the Smagorinsky Δ scaling, and which planes or regions lose.
  2. Cross-vent refinement inside 16 GB.
  3. A "high accuracy" app mode at about 2× finer cells, once refinement is shown to help consistently.

(The older notes below are kept for history.)
**STOPPED BY USER 2026-10-05; since resumed and finished.**
- **Final checks already run, all on today's code.**
  - Build: 0 errors.
  - Full sandbox (`--compare out/checks-2026-09-28-g3.json`): 327 PASS, 1 FAIL, 0 not run. Log: session scratchpad `full-sandbox.log`.
  - App `--selfcheck`: 37/37.
  - Measured cases: see the table below.
- **The one failure is 19a, sealed-room heat stratification.**
  - dT(2.5 m − 0.5 m) fell from 2.17 K to 0.21 K against a 0.3 K band. 19c energy balance still passes (err 3.9 %).
  - **Resolved: the old value was an artefact.** The leaky Bouzidi walls (no mass correction) made a divergent velocity, and T advected by it stratified a still room.
    - Quiet sealed room, no heater, 8000 steps: old 1.32 K, correction off 0.83 K, correction on 0.009 K.
    - The f0 correction alone takes 19a from 1.94 to 0.21 K. Fixes (b)–(e) took it from 2.17 to 1.94 K.
  - 19a stays failing, with its band unchanged. It now shows a real limitation: the room over-mixes.
    - Far-field speed is 0.07–0.09 m/s against about 0.006 m/s physical.
    - Baines & Turner predicts 3.8–7.7 K.
    - dT rises with refinement (0.21 → 0.45 at dx 0.268 → 0.213), so it is resolution-limited. Smagorinsky diffusivity is untested.
  - Regression check 19d (quiet room stays uniform) is being added.
- **Orifice §5 resolved (opus).** There is no bug: the total-pressure gain is a discretisation overshoot at the edge.
  - Fixed-Re refinement takes the gain from 0.385 to 0.136 to 0.003 at n = 8/16/32.
  - Cd of about 0.69 is a real thin-plate value at the Re of about 10³ that the floor allows.
- **Done since (kept for history):**
  1. 19a root cause.
  2. Sample-house before/after: ACH per room with CI and stop status, repeat runs at 4 m/s, using the App `--bench` (with `--twice` / `--accurate-*`).
  3. The audit report covering §7 deliverables 1–6.

**2026-10-05, audit fix pass in progress.** ChatGPT implemented 12 of the 13 fixes; I closed the gaps.
- **Status.** Every fix has a test: `--fixes` 55, `--audit` 23, `--walls` 5, `--devices` 22, all PASS.
- **Devices 45a: the old pass was two errors cancelling.**
  - Linear (Bouzidi) interpolated bounce-back is not mass conserving. At the geometric qw ≈ 0.94 of thin walls it made a source of about 13 % of the supply flow at a nozzle mouth.
  - Old stale wall distances put a matching sink at the window jambs.
  - Fix: `GatherBoundary` and `GatherBoundarySoA` move the per-node excess (reflected − sent) into the rest population f0. Node mass and all momentum moments are unchanged. CPU and GPU are bit-identical.
  - This is a solver-wide wall change, so the measured cases must be re-run before it is trusted.
  - Devices check 43: the extract device was 1.6 m wide over a 1.2 m duct hole and drove air into the wall. The scene now uses 1.0 m. Its bands are unchanged.
- **ACH now divides by the nominal room volume** (footprint × height, holes subtracted).
  - `RoomResult` keeps `VoxelVolume` separately. Detected or cropped rooms fall back to the voxel volume, flagged in `VolumeNote`.
  - Transport times (air-change effectiveness, Converge warm-up) still use `VoxelVolume`.
- **Measured cases re-run on all of this.** The scores barely moved:

  | Case | FAC2 | FB | NMSE | q | Other |
  |---|---|---|---|---|---|
  | Annex 20 | 0.575 | 0.454 | 0.566 | 0.340 | |
  | MINIBAT | 0.592 | 0.208 | 0.320 | 0.296 | |
  | cross-vent | 0.365 | 0.271 | 1.63 | 0.254 | Q 0.458 |

  The wall mass fix is therefore harmless there.
- **App.**
  - The Accurate EndBatch/Finish publish is guarded by the epoch.
  - Warm-up uses `VoxelVolume`, and detected rooms set `VoxelDerived`.
  - The room panel shows gross in/out and net; ACH on the voxel volume is marked †; the PDF gained Voxel/Nominal columns.
  - Selfcheck #37 drives the real LiveSolver through an A→B model swap.
- **Lead E (validation sampling, metrics, origins): no bug.** The q misses are physics.
  - The MINIBAT note said 773 points were below the probe floor; the true count is 515. Corrected, with no score effect.
  - Open observation: the cross-vent digitised centre-section implies a mean opening velocity of 0.30 UH or less, against the 0.40–0.47 band. The band is unchanged, and this is not verified.
- **Seam leads B/C/D/F/G found two bugs, both fixed with tests:**
  - The carve margin was 0.6 Dx, but the halo of a 45° wall is 0.707 Dx, which left a solid layer across 10 of 48 openings. The margin is now halo + 0.1 Dx (`F3e`).
  - GPU thermal `UpdateCells` reset new-Air cells to 0 °C (`F7i`).
- **Open: `ThroatCells` at exactly 45°/135°.** It catches one or two diagonal lattice lines, so opening Q/Cd reads 0.75–1.5× too low or high. Fixing it means choosing a different estimator. Room ACH is face-based and unaffected.

**NEXT SESSION: START HERE. The external audit fix list is `CLAUDE_FIX_PROMPT.md`, written by ChatGPT.** Its 13
findings came from `CODE_AUDIT_PROMPT.md`; the probes are in `audits/2026-09-29/`. Two sonnet reviewers checked
every finding against the code (2026-09-29) and found all 13 mechanisms real and reproducible. None of them
explains a validation failure: the validation meshes are thin, tie-free and built by hand. The findings in priority
order:
1. **#1 Hollow thick walls.** Walls thicker than Dx are rasterised as a shell only, and carving an opening
   connects the hollow core to the room. The sample house (0.25 m walls, 0.2 m slabs) is about 74 % hollow on the
   Accurate grid and about 10 % on Preview. This is a real app-accuracy bug: air flows through wall cores.
2. **#3 Inclusive opening bounds (`<=`).** One extra row appears only on exact ties, and round sizes hit them
   often (1.2 m at Dx 0.2 → +17 %).
3. **#6 ACH from the mean velocity.** It cancels reversing flow, so ACH reads low. Use instantaneous positive flux.
4. **#8 Stale-epoch publish** in `LiveSolver.Analyse`.
5. **#7 Stale `_model`** after `UpdateCells`. The worse, unreported case: closing a window and then moving the
   nested box builds the fine grid from the stale model.
6. **#9 `Simulation.Converge` returns an empty field** (ResetAveraging before return), and its batch count is off
   by one. Fix this before the app uses it.
7. **#4 GPU float ρ sums.** They cause ±1e-3 quantisation, about 1 % of Δp; accumulate ρ−1 in double.
8. **#2 Bouzidi distances** are computed before carving, and marked-solid cells get the wrong sign. This is second
   order.
9. **#10–12** affect only the v1 floating-box mode; **#5 and #13** are negligible.

Isolated-orifice Cd 0.73 and total-pressure gain, explained 2026-10-05 (deep-debug, scratch `orifice/`): the centreline gain (+0.25 Δp at 8 cells, steady, Mach- and Cs-independent, references agree to 0.05 %) is a lattice discretisation overshoot of the under-resolved edge/jet. At fixed Re ~900, t/d 1/8, Cs 0 it goes 0.385 / 0.136 / 0.003 at n = 8 / 16 / 32, and Cc_mom goes 0.94 / 0.77 / 0.75. Cd converges to ~0.69, which is physical for a thin plate at Re ~10³ under the viscosity floor; it does not converge to 0.61. Not a measurement, units or driving-layer bug.
**SESSION ENDED 2026-09-29 mid-run. Three runs were killed, and each may have left partial edits:**
- Lane S: viscosity floor as a `RunOptions` value, plus the supply perturbation.
- Lane A: Accurate stops on `Converge`, air-age every pass, Cd presets, device box clear of walls.
- Lane V: refreshing the measured rows. Its harness edits did land: `--opening-model` and the "Opening model" column.

First step next time: build the solution, run the app's `--selfcheck`, then the full sandbox `--compare out/checks-2026-09-28-g3.json`.

Then run a bug review with `reviewer` (sonnet) against the pre-v2 source in `.snapshots/pre-v2-2026-09-28.zip`. Priority files:
- LbmSolver.cs: supply BC, opening-loss force, ρ-buffer init;
- ScalarSolver.cs;
- Convergence.cs;
- App: Accurate.cs and LiveSolver.cs.

User preference (2026-09-29): smaller agents. Use Sonnet 5.5 by default, haiku for locating and reading, opus only for physics decisions.

Research prompt ready for the user: RESEARCH_PROMPT_5.md. It covers:
- Cd per window type and angle, for the Expert presets. The top-hung 0.4 and louvre 0.3 values are unverified guesses.
- turbulent jets without a viscosity floor;
- averaging length. Keep what passes; revert half-done edits.

The user asked to cut token use:
- one agent at a time;
- sonnet by default;
- short reports;
- one full sandbox run per batch.
**Lane S g-run landed (2026-09-29): sandbox 113 PASS / 0 FAIL** (out/checks-2026-09-28-g3.json).
- **ρ-buffer fix:** the never-initialised second population buffer is now filled. Return and Fan cells read ρ 0 → 0.9, ring cells read 0 → NaN; check 57 now passes.
- **Calibrated opening model:** now the default. It is a Forchheimer loss on the throat cells, K = 1/Cd_t² − 1/Cd_s(n)², with the Cd_s table 0.677 / 0.743 / 0.760 at 4 / 8 / 16 cells.
  - Orifice check 58: 0.611 / 0.611 / 0.604.
  - Cross-vent Q 0.552 → 0.463, inside the band; FAC2 0.365, q 0.25, still failing.
  - Every window-driven sandbox flow dropped 10–13 %.
  - 10b and the app's Cd warning floor moved from 0.5 to 0.42 under Calibrated. I judged this OK: the per-window Cd is taken from the local static Δp, which reads low on leeward windows.
  - Openings wider than 16 cells use the 16-cell value; this is not validated.
- **ScalarSolver:** age-only mode and warm start; sealed pockets are now skipped. Full solve went 0.7–1 s → 0.15–0.28 s; age-only warm is about 0.09 s.
- **Check 54:** `Simulation.Converge` fixed. Warm start takes 47k steps against 72k cold.
- **Running now:**
  - Lane S: floor 2e-4 / 1e-4 plus supply perturbation, with recalibration.
  - Lane V: refresh rows, cross-vent at N=40, and facade Cp convergence at H/20–60.
  - Lane A: Accurate stops on Converge (95 % CI ≤ 5 %), air age every pass, Cd presets in the UI, device box kept clear of walls.
**Peer papers (commenter's links) digested: docs/PEER_LBM_COMPARISON.md.** Leeds real-time LBM (Khan et al. 2015;
Delbosc et al. 2014): D3Q19 plain BGK + Smagorinsky Cs 0.1, no viscosity floor, Zou–He inlet (their thesis warns
against equilibrium reset, which matches our F1); the room validation is against Fluent LES, not measurements.
King, Khan & Noakes 2018 used the same Tominaga–Blocken case: Q within 10 % but computed from simulated ΔCp with
an experimental Cd 0.658, so they validated facade pressure, not opening discharge. That supports our calibrated
opening model. Their mesh study: facade Cp needs H/32 → Lane V to-do: cross-vent Cp and Cd convergence at H = 60–80.
**Accurate pass findings (Lane A, 2026-09-28c):** the old 1× settle read Living/Kitchen ACH 33–64 % high; now cold
2× settle (within 5–9 % of a 4×/4× reference). Warm start measured worse (needs 4× settle, scatters) → switched OFF
by me in `LiveSolver.StartAccurate` (not yet rebuilt; `--accurate-start warm` still tests it). Hall / Bedroom 2 /
Landing scatter 20–50 % run to run: the 2-flow-through average is too short → **next Lane A: drive the Accurate
average by Core's ConvergenceMonitor (per-room 95 % CI ≤ 5 %, with a cap)** once Lane S fixes check 54. Also: device
box should avoid walls; thermal settle unmeasured. Done: Run → "Done" after Accurate, slices from the Accurate
field, one-click device box, docs/HALLWAY_TEST.md, notes rule, mesh-openings shot (16/16). 32 selfcheck rules.
**F1 LANDED (2026-09-28): link-wise supply inlet + PI flux controller.** Delivered/set 0.99–1.00 at 1–6 cells
(was 0.29–0.67 in the sandbox scene). Annex 20 flush FAC2 0.09 → 0.58, FB 1.08 → 0.46, q 0.05 → 0.33 (jet now
stays on the ceiling); MINIBAT FAC2 0.09 → 0.60, FB 1.05 → 0.20, q 0.04 → 0.30. Both still fail on q (and Annex on
FB). Sandbox 105 PASS / 1 FAIL (54, from killed 2.2). GPU warm-start SoA bug fixed (checks 56a/b). Old metrics in
`out/validation/*-pre-f1`. Running now: Lane S (uninitialised ρ buffer on equilibrium cells → calibrated
opening-discharge model + orifice-Cd check → ScalarSolver warm start/age-only → check 54), Lane V (pre-f1 rows,
MINIBAT disk variants, q breakdown), Lane A (Accurate warm start, Run label, slices, one-click box,
HALLWAY_TEST.md, notes rule, mesh-openings shot).
**RESUMED 2026-09-28 with the RESEARCH_PROMPT_4 report** (Downloads `compass_artifact_wf-27775f57…md`); plan in
PLAN_V2 §1.4b (F1–F6). Running: F1 link-wise velocity inlet + PI flux controller (Lane S, Core), F2 harness
targets (Lane V), F3/F4 orifice-edge + room-turbulence experiments (scratch dbg3). The old inlet-BC experiment
(item 1 below) is superseded by F1. **F2 done:** cross-vent now scored against opening-flux references (Karava
PIV 0.397 derived from TB2015 §4.1; Hwang & Gorlé 2022 LES 0.44–0.47, Table 2), tracer 0.25 separate; guideline
domain default → Q 0.552 (band 0.36–0.52, still FAIL, +17 % vs 0.47). MINIBAT `--inlet disk-table|disk-momentum`
built, not run. F3/F4 done (see PLAN_V2 §1.4b results). HRR experiment done: rejected (PLAN_V2 §1.4b); next Lane S step after F1 = calibrated
opening-discharge model + orifice-Cd check. **Lane A: Preview → Accurate done** (`Accurate.cs`,
`MainWindowAccurate.cs`; 5.1M cells @ 170 mm, ~60–70 s on the sample house, edits cancel in 0.3 s, progress
monotonic, 30 selfcheck rules). 4.2 mesh openings landed (16/16 round-trip). Lane A leftovers: one-click box for
"refine needed", docs/HALLWAY_TEST.md, notes-count rule, run `--shotmode mesh-openings`, Run button reads "Pause"
while idle after Accurate ✓, slice not republished from Accurate. Core warm-start SoA bug (GPU `_warmStart` writes
AoS into SoA `_dCur`) sent to the F1 agent; then set `warmStartWorks = true` in `LiveSolver.StartAccurate`.
**STOPPED BY USER 2026-09-28 (token budget).** All agents stopped, orphan Sandbox/Validation processes killed;
solution builds (0 errors, 0 warnings). User OK'd GitHub search (permissive licences only) when resuming.
Killed mid-task, resume in this order:
1. **Inlet BC experiment** (scratch `scratchpad/dbg2`, not in repo): compare (a) Ladd velocity bounce-back,
   (b) Guo non-eq extrapolation, (c) flux-corrected equilibrium, (d) Latt 2008 regularized (XLB/LUMA pattern,
   docs/GITHUB_SURVEY.md) on Annex 20 nh = 1/2/4; then Lane S lands the winner + re-run all 3 measured cases.
2. **Opening Cd experiment** (scratch `dbg3`): baseline reproduced (Q 0.585, Cd 0.91/0.815/0.86); **finding: the
   whole cross-vent flow is steady, Iu ≤ 0.003 even in the room**, i.e. the LES is effectively laminar. Supports
   H2 (no inflow turbulence → coherent loss-free jet). Still to run: isolated orifice Cd vs Re, inflow
   perturbations, off-axis outlet.
3. **Lane S 2.2** convergence monitor + warm start: code state unknown (was waiting on its sandbox run); run the
   full sandbox + `--compare src/WindFlow.Sandbox/baseline.json` first.
4. **Lane A 4.2** mesh opening detection + one-click box for "refine needed" + docs/HALLWAY_TEST.md + notes-count
   check: was at "shot sequence for mesh openings"; code state unknown, run `--selfcheck` first.

**Next plan: `PLAN_V2.md` (2026-09-28)** — accuracy release, GPU budget, redesign. In progress since 2026-09-28.
Licensing and the git commit are deferred by the user; rollback point instead: `.snapshots/pre-v2-2026-09-28.zip`
(source only, gitignored). Lanes running: S (baseline checks.json, nested GPU refresh, carved opening area),
V (new src/WindFlow.Validation, Annex 20 2D first), A (live-link server, GPU budget governor).
**Lane A done 2026-09-28:** live link wired (fake-Revit test: 7 msgs, 7 acks, 3 bad ones rejected, no crash;
`tools/livelink-fake-revit.ps1`, `--livelink`); GPU governor 2.1 (`--gpu N`, Settings slider, default 60; nvidia-smi
50 % → 49.4 %, bursts ≤ 12 ms; 100 % reaches only ~87 % duty, 2240 vs 2806 MLUPS). **Lane S 2026-09-28b done:** blocking sync, EnsureGpu race, `RunOptions.MinDx`, `LbmSession.TimeStepSeconds /
LatticeViscosity / VelocityScale / ViscosityFloor`, VALIDATION rows 10 + 48; 98/98 PASS. Link-flux for opening
flow DEFERRED: needs raw populations, since Analysis only sees averaged u (43b/45a residual stays ~5.5 %, in band).
Lane S now on 2.2 convergence monitor + Preview→Refine warm start. **Next Lane S brief must also carry:**
`ScalarSolver.Solve(..., ScalarFields? initial = null)` warm start, `ParallelOptions { MaxDegreeOfParallelism }`
on its `Parallel.For`s, and an age-only option. For 1.2 wall function: docs/GITHUB_SURVEY.md (Musubi BSD-2
wall-function module shape; Spalding from the Han–Ooka paper). Inlet BC: XLB/LUMA Regularized (Latt 2008) + Smagorinsky (App air-age pass costs 1.2–4.8 s on all cores, run every 60 s). (Was queued: (a) CUDA
accelerator with `CudaAcceleratorFlags.ScheduleBlockingSync` in `LbmSolver.TryCreate` (solver thread spins a CPU
core for the whole busy phase); (b) race in `LbmSolver.EnsureGpu`: `_gpuChecked = true` is set before `TryCreate`
finishes, so a concurrent `GpuAvailable` returns false and a session silently runs on CPU; move it after the
`TryCreate` calls inside the lock (App works around it with `StartupTier.WaitReady()`). Lane A now on: status-strip effective MLUPS, 2.4 startup tier, 4.1 room names +
highlight. After that: 4.3/4.4 Basic/Expert split, then blend-band tags + Grow box (needs Lane S SpongeWidth).
**Lane V first result 2026-09-28: Annex 20 2D baseline FAILS** (FAC2 0.09, FB 1.08, NMSE 4.44, q 0.05; band
FAC2 ≥ 0.5, |FB| ≤ 0.3). Harness: `dotnet run -c Release --project src/WindFlow.Validation -- annex20-2d`. Three
Core findings, root cause being investigated read-only (deep-debug): Patch supply delivers only 0.68 of its
FlowRate; a flush supply patch makes the ceiling jet drop (recessed attaches); `NuLatMin` caps effective Re at
~160 × cells across the inlet. **These outrank 1.2 in Lane S's next brief.** Also requested for Core:
`RunOptions.MinDx` below 0.1 m inside a box; read-only lattice parameters on `LbmSession`. **Cross-ventilation (Tominaga–Blocken 2015)
also FAILS: flow rate +134 % (Q/(UH·A) 0.585 vs 0.25), grid-independent** (N=20 and N=40), jet too narrow and
strong; FAC2 0.37/0.33. Effective building Re ~2,200 from `NuLatMin` (needs > 1.1e4). Cp/Cd split done
(`out/validation/cross-vent-diag/table.md`): sealed facade Cp is right (ΔCp ≈ 1.05); ~91 % of the error is the
opening discharge coefficient (envelope Cd 0.76–0.78, per-opening 0.8–0.9 vs ~0.61 sharp-edged); domain 7 %
(TODO Lane V: make the guideline domain the case default). Benchmark itself is uncertain: tracer gas 0.25 vs
Karava PIV 0.40. Opus experiment running in scratch: isolated orifice Cd vs Re/resolution (H1) vs jet
carry-over from turbulence-free inflow (H2). **MINIBAT isothermal also FAILS** (FAC2 0.09, FB 1.05 at 4 cells;
same at 6): jet stays on the ceiling but loses its core within ~0.4 m; Patch delivers 0.58–0.65; supply set to
the measured T1 momentum flux (2.68 m/s, tabulated 1.67 is inconsistent with the hot-wire data). Hot/cold cases,
Temperatures.xlsx, `--inlet table` not done. Harness Runner now passes `MinDx` (was stuck at 0.1 m). Deep-debug (sonnet) result: `NuLatMin` is NOT the cause in either case (25× lower changes
Annex flow < 1 %; cross-vent Q gets worse, 0.585 → 0.61–0.64; Smagorinsky dominates); flush-jet drop = entrainment
under a weak jet. Its "no local fix" conclusion for the 0.68 patch deficit is disputed: Fixed cells impose u0 at
the cell centre, not a face flux; opus experiment running in scratch (Ladd velocity bounce-back vs Guo
non-equilibrium extrapolation vs flux-corrected equilibrium). AIJ case A needs a browser download by the
user (bot challenge); Annex 20 3D not online; RP-1009 paywalled. NEEDED.md: MINIBAT Mendeley zip (CC BY 4.0) + Kuznik 2008 (paywalled) for the user.
No "validated against measured rooms" wording anywhere until PLAN_V2 1.5 passes.
**Viz viridis done** (verified 2026-09-28: plan image read back, legend and field both viridis, no `Colormap.Speed`
left in Viz/Report). Stale comments for Lane A to drop: App `Viewport/Shaders.cs` ~175 and `MainWindow.axaml.cs`
~1625 still say report images use `Colormap.Speed`.

Standalone desktop app (`src/WindFlow.App`, Avalonia + Direct3D 12) over the C# solver core. Plan: `PLAN.md`.
Validation page: `VALIDATION.md`. README is current. **Zero git commits** (user has said publicly it will be
open source, MIT; has not yet told me to commit). Solution builds: 0 errors, 3 harmless warnings.

Run: `dotnet run -c Release --project src/WindFlow.App` → Sample building → Run.
Keys: Space pause · P particles · G ghost · X wall mode · F frame · V Speed/Still air · S slice · B probe · Esc.
Logs: `%LOCALAPPDATA%\WindFlow\logs\` (Export ▾ → Open log folder). `--selfcheck` = 12 app rules, no GPU.
Sandbox: `dotnet run -c Release --project src/WindFlow.Sandbox` (~35 min, 95 checks; suites `--devices`,
`--walls`, `--nested`). **Last full run: 95 PASS / 0 FAIL.** Licence check: `tools/licence-inventory.ps1` (45
packages, all permissive, exit 0).

## Age-of-air method change (2026-10-26, AGE-1 / AGE-FIX)

Per-room fresh-air ACH now lands from the steady age of air (`ScalarSolver`) on the landed time-mean flow, not the online transient age (the online age cannot settle within a run, so the old landing was dominated by its warm-start seed: warm-vs-cold scatter 11.6 % mean / 17.8 % max, now 1.5 % / 6.5 %). Known bias (P14-06): fresh-air ACH can read up to about 30 % low (conservative). *(Superseded 2026-10-28, re-audit RA-01/02/08: the steady age differs from the age followed through time by about 30 % either way per room; grid size moves a room by up to about 35 % and the building by about 20 %; the High column is "Fresh-air (steady age)".)* Fresh-air ACH has no ± range; total-exchange ± is unchanged; one disclosure note (UI tooltip, headless text, PDF footnote). The online age is opt-in (`RunOptions.OnlineAge`). Evidence: `audits/2026-10-12/fixes/FIXLOG.md` ("Experiment AGE-1", "Batch AGE-FIX"), `fixes/bAGE/age_summary.txt`, `fixes/bAGEFIX/`. README, VALIDATION and REAUDIT_PROMPT were updated to match.

**Queue W2 result (2026-10-26): VERDICT FAIL.** The GPU grid check on the post-AGE-FIX build (9 runs, all Converged) shows same-grid repeats exact (G0 0.0 %) and rotation passing (G3), but a +-2 % Dx sweep still moves building fresh-air ACH 18.8 % (bound 6) and single rooms up to 33.9 % fresh-air / 25.9 % total exchange (bound 10-15); the origin-shift pair fails on 3 metrics. Lead reading: the spread is in the flow (total exchange moves as much as fresh air; DESIGN_OPENING_FIX term (e)), not in the age. Decision: no further accuracy research before release; disclose roughly +-30 % on single rooms and roughly +-10 % on building fresh-air ACH (README, VALIDATION, UI note, PDF) *(Superseded 2026-10-28, re-audit RA-01/02/08: the steady age differs from the age followed through time by about 30 % either way per room; grid size moves a room by up to about 35 % and the building by about 20 %; the High column is "Fresh-air (steady age)".)*; V-EQ inflow (made the default in accuracy step 1, reverted 2026-10-28, RA-04) and the placement ensemble (option F) are post-release research items. Next: validation reruns affected by AGE-FIX (queue V2). Evidence: `audits/2026-10-12/runs-w2/VERDICT.txt` and `queue.log`, `fixes/FIXLOG.md` ("Queue W2"), VALIDATION.md "Queue W2".

## 2026-10-26/27 batches: UI redesign, model editing, AGE-HIGH

**UI redesign.** The side panel now has a section picker (Rooms · Openings · View in Basic; plus Devices · Studies in Expert), Rooms first. Rooms is an outliner (room, then its openings) with a pinned inspector, Standard / High pinned at the bottom and a viewport header strip (Speed / Still air, Plan / Section); the Openings list is built from the model. Review fixes 1-7 and the UI lag fix (lists repaint after a toggle: the switch flips in 10-67 ms, it was 1-10 s on CPU) are in FIXLOG "UI responsiveness + review fixes 1-7". Evidence: `audits\2026-10-12\ui-design\` (SPEC.md), `audits\2026-10-12\ui-preview\shots\`, `probe-*.out`, `selfcheck-after.out`.

**Model editing.** Openings: add (click a wall, 1.2 × 1.2 m, 0.9 m sill, editable), delete with a fill solid for imported mesh holes, one-step Undo, session-only Restore list, carving of fills under a new opening. Rooms: delete, merge (Ctrl+click), add from "Spaces without a room" (detected once per edit off the UI thread) or in 3D, Undo; all persist through save/load except the Restore list. Edits re-voxelise and restart the live solve. Checks: SelfCheck "review fixes", "spaces without a room", "re-add over a deleted window" (147 PASS at that point). Shots: `ui-preview\shots\rooms-candidates.png`, `openings-restore.png`. Four UI fixes have no automated check (focus guard, multi-delete undo selection, Del routing, inspector commit target).

**AGE-HIGH.** High (gusty) lands the online age again and shows the steady age as "Fresh-air (conservative)"; Standard unchanged (steady age). *(Superseded 2026-10-28, re-audit RA-01/02/08: the steady age differs from the age followed through time by about 30 % either way per room; grid size moves a room by up to about 35 % and the building by about 20 %; the High column is "Fresh-air (steady age)".)* Reason: queue V2 showed the steady age reads about 50 % low under gusty wind (×2.04 Living, ×3.07 Bedroom 2; building 13.3 against 27.0 ACH) and High fell below Standard. Evidence: FIXLOG "Queue V2" item 3 and "Batch AGE-HIGH"; SelfCheck "AGE-HIGH" (148 PASS). No GPU rerun of High on the new build yet.

## Stopped mid-task — check first next session

Three agents were killed mid-edit. Everything compiles, but:
1. **Nested box GPU refresh** (src/WindFlow.Core/LbmNested.cs, src/WindFlow.Sandbox/NestedChecks.cs): goal was to
   move `NestedSession.Refresh` (ring interpolation + sponge) onto the GPU — the app measured 8–10 ms per coarse
   step (178–464 MLUPS total vs thousands) because of a per-step GPU sync + host round trip. Last words: "Now add
   the speed probe to NestedChecks and a `--nested-speed` entry" — so the GPU kernel may be in and only the check
   was missing. Run `--nested` first; expected 48a–48d unchanged (4.1 %, 3 %, 0.16 %, 0.4/5.6 %). Also in that
   batch: a grid warning when an opening lies inside the sponge band + `NestedSession.SpongeWidth`; making
   `Analysis.Analyze` stop appending duplicate room warnings to the live grid on every pass. Check which landed.
2. **App live-link server** (src/WindFlow.App): pipe server at `\\.\pipe\WindFlow.LiveLink` per docs/LIVELINK.md
   (Revit side is done in src/WindFlow.Revit/LiveLink.cs; tools/livelink-fake-app.ps1 fakes the app). Last
   words: "Baseline: 0 errors. Now the pipe server." — probably nothing landed. Also in that batch: "in blend
   band" tags + "Grow box" button + drawn sponge band.
3. **Viz viridis** (src/WindFlow.Viz/RenderKit.cs, Renderer.cs): make plan/section images use the app's viridis
   for Speed. Last words: "Just need `using System.IO;`" — nearly done; build is clean, so check whether
   Renderer uses the new map or still Core's rainbow `Colormap.Speed`.

## Done this session

- Lane A 2026-09-28c (warm start, Run/Done, slices, one-click device box, hallway test, notes rule, mesh-openings
  shot): **Accurate warm start on** (`warmStartWorks = true`, Core's SoA fix). Settle rule, measured on the sample
  house (1.54M -> 5.09M cells, wind 270 @ 4 m/s, GPU 60 %) against the longest run (cold, 4x settle + 4x average):
  at the old 1x settle, warm or cold, Living/Kitchen ACH read 33-64 % high (the pass shipped with that); cold 2x
  within 5-9 % on every room; warm 2x 16 % / 13 % low on the current Core (a 4x longer average did not move it:
  11.4 vs 11.1 ACH, so it is the seed washing out, not the window); warm 4x within 4-8 % on Living/Kitchen
  (3 runs). Uncontended timings: old cold 1x 5348 steps, 16-25 s stepping; cold 2x 8436, 37 s; warm 4x 14612,
  60-78 s; + 1-5 s rooms/air age; 2-5x longer while a game holds the GPU. Rule: warm 4x, cold 2x
  (isothermal), thermal keeps Core's warm-up. **So the warm pass is the LONGER one here** (14612 steps vs 8436 for
  cold 2x, 5348 before): recommend flipping `warmStartWorks` back to false (one line) unless a model shows the
  opposite. Hall / Bedroom 2 / Landing scatter 20-50 % between runs (warm runs vary with the Preview state they
  seed from; cold runs wander with settle length on the older Core: Hall 108/60/65/47/28 ACH at 1/2/3/4/6x):
  a low-frequency unsteady flow that a 2-flow-through average cannot pin down. A longer, batch-checked average
  (Core's ConvergenceMonitor, the `AccurateAverageSeconds` ponytail) is the likely fix, only tested as one 4x run. Core changed twice under the measurements (Analysis.cs 22:18, LbmSolver.cs
  22:42): room numbers shift up to 25 % between Core builds, so only same-binary rows compare. Test hooks:
  `--shotmode accurate-time`, `--accurate-start cold|warm`, `--accurate-settle x`, `--accurate-average x`.
  **Run button** reads Done (greyed, tip) once the Accurate pass lands; Space and click do nothing until an edit.
  **Slices** republished from the Accurate field when it lands and when the slice is switched on afterwards
  (sidecar: "Accurate pressure from 5090k cells onto 124x112x46"). **One-click box**: a red "refine needed" chip is
  clickable when a box can resolve the device: box = outlet extent + 1.5 widths + blend band + one fine cell
  (Devices.DeviceBox), around every flagged device of the same room; inside a solved box that resolves it the chip
  reads "resolution OK (box)" (judged on the fine grid's own notes); a device no box resolves (120 mm split-AC slot:
  0.9 cells at 127 mm) reads "too small to resolve" in grey with the reason. Shot `--shotmode device-boxed`
  (out/app-device-boxed.png). The fan box lands two faces on walls (Core closes them, notes say so) and puts 6
  openings in the blend band (Grow box (6)): wall-aware placement not done. **docs/HALLWAY_TEST.md** (30 min, 8
  tasks). **Notes**: `Notes.Gather` is the one list, every air-age solve goes through `LiveSolver.SolveAge`; new
  selfcheck rule runs 4 age + 4 analysis passes on a still field (mutation-tested: without the RemoveAll the grid
  warnings grow 3,3,6,6,9,...). **mesh-openings shot** run for the first time: 16/16 openings matched; fixed room
  names running under the comfort chip and ACH (row name now trims), and the status line / sidecar now use the
  panel's room names ("Room 2 (west, 43 m²)", not the raw "Room 1"). Selfcheck 32 rules.
- Lane A 2026-09-28b (Preview, then Accurate): live run = **Preview**; once it settles an **Accurate** pass starts on
  its own (whole building, tier budget high 5M / medium 3M / low 1.2M / CPU 0.6M cells, VRAM-clamped beside the idle
  Preview session; settle = Core warm-up, average = 2 flow-throughs; rooms + air age on the fine grid). It replaces
  the stepping (Preview resident, idle); any wind/opening/device/environment/box edit cancels it (Accurate.cs tracker,
  LiveSolver). Bar + stage text above the trust line; Preview numbers muted with "~" and a "preview" tag; PDF and
  Lock prefer Accurate, a Preview PDF says so on the cover and in the file name; Settings "Accurate grid" (Expert
  types a budget). Sample house, GPU 60 %, shared with a Sandbox run and a game: Preview settle 2-9 s (1.54M @
  255 mm), Accurate ~60 s (5.09M @ 170 mm; 36 s stepping + 23 s analysis). Selfcheck 30 rules. **Core bug found
  (not fixed, not App-owned):** the GPU branch of the Preview-to-Refine warm start (LbmSolver.cs, `_warmStart`
  block after `_dSampleRho`) fills the host buffer with `WriteAoS` and uploads it into the SoA population buffer
  (`InitKernel` uses `WriteSoA`): a 1.5M -> 5.1M warm start diverges within 200 steps, cold start runs fine. App uses
  a cold start on GPU until Core writes SoA there (`warmStartWorks` in LiveSolver.StartAccurate).
- Lane A 2026-09-28 (PDF speed, air age, 4.5 devices, box band): **PDF export** measured per stage: the section
  plate's streamline halo was ONE StreamGeometry holding every trace, and WPF widens a stroked geometry into a
  single fill resolving every crossing, so recirculating traces cost 1.3–16 s (data-dependent, superlinear;
  the earlier ~2.5 min was not reproduced today). Halo now drawn in 24-point pieces inside one opacity layer
  (RenderKit.FlowLines): section 0.3–0.7 s, whole report ~1.3 s, progress on the status line, worst UI-thread
  gap 38 ms. **Air age** in Basic: LiveSolver runs ScalarSolver on the averaged field once settled, then every
  60 s while running (1.5–4.8 s of all cores per solve), reused in between, dropped on a new averaging epoch;
  column "air age" in min, "…" pending, "> 1 h" for no fresh air. **Devices (4.5)**: palette tiles of named
  presets (Ceiling fan 1.2 m, Pedestal fan, Split AC 1.5 ton = 0.26 m³/s, Exhaust fan (bath) = 25 L/s, …),
  ghost with a halo, R turns floor fans, numbers behind Details, "resolution OK / refine needed" chip (3.3:
  < 8 cells across at the live, box-aware cell size). **Box**: "in blend band" opening tags, Grow box (2 Core
  sponge notes -> 0 on the sample), band drawn faintly, Lock with a box shows the averaged composite.
  `--selfcheck` 24 rules. Shots: `--shotmode basic-fresh|device-presets|device-place`; boxsolve also writes
  `-grown.png` / `-locked.png`.
- Lane A 2026-09-28 (4.3 + 4.4 + Lock + particle setting): Basic is its own product (top bar: Open, Sample, wind,
  Run, Lock, Speed | Still air, Export PDF, Basic | Expert, gear; panel: ROOMS with ACH / still / air age columns
  and one comfort word, OPENINGS in m³/s, one-row ENVIRONMENT; one trust line pinned under the panel with
  "N notes" in plain language, full text in Expert and the log). Expert = four tabs (Results, Devices, View,
  Studies; per-room A/B/Δ ACH compare table). Settings popover (gear, both modes). What each mode shows and
  what Lock disables is one table, `UiTable.cs`; 4 new `--selfcheck` rules read it against MainWindow.axaml (20
  total). Lock result: LiveSolver pause + SuspendAnalysis, particles on the last averaged field, flow controls
  greyed with a reason, vsync cap, Auto particles up to the measured limit; unlock resumes (sim t 14.1 -> 21.0 s).
  Particle count typed in Settings (Auto default) with "your GPU: up to ~N" from GPU timestamps around the
  particle pass (Renderer.ParticleGpuMs). PDF uses display names. Shots: `--shotmode basic|basic-notes|expert|
  settings|locked`, `--empty --shotmode walk` (Basic flow end to end). Freshness column is hidden: the live
  analysis never runs ScalarSolver, so MeanAge is NaN. PDF export took ~2.5 min in the walk (Viz, not
  investigated).
- Lane A 2026-09-28 (2.4 + 4.1): status strip shows the governed rate ("797 MLUPS (peak 1084)"); stale viridis
  comments fixed; startup tier (Tier.cs: ~2 s probe + DXGI VRAM -> cells / particles / idle fps, logged `tier:`,
  `--tier-probe`); particle control removed from the UI, runtime auto-adjust 100k/60k/30k (`--particles` pins,
  `--fps-inject` tests); room display names (RoomNames.cs), rename, translucent room volume + opening quad on
  select, floor click selects the row, Esc clears; `--shotmode rooms`; 5 new `--selfcheck` rules (17 total).
  Solver cannot target the AMD iGPU (Core picks CUDA first, no device override). Renames not persisted (no
  project file).
- Logging (one file per launch), device inspector crash fixed (UI re-entrancy), input validation, model
  snapshots per solver command, 13 second-review fixes, `--selfcheck`, `--edit-device`, `--swap-import`.
- Still air view replaces Freshness: amber cloud of cells < 0.10 m/s (slider) + cyan-white moving-air lanes;
  room still % from the same threshold. **User has not yet said whether this reads right.**
- Core: AC return placed ≥ 0.5 m above the outlet with a warning under 0.4 m (41f real assertion, 41h);
  room inflow only through real connections + `RoomResult.InternalExchange`; `Voxelizer.Build(...,
  sealUnreachable:false)`; 19a explanation; `LbmSession.MaxSpeed`; metric fan clearance (0.25 m, choked only
  when achieved/set < 0.6); box-face "partly inside the building" note; **nested 2:1 fine box** (`LbmNested.cs`,
  4.1 % vs uniform fine, 3 % vs whole building on the case that was 5× off).
- App: nested box is the default box mode (v1 open-face box behind an Advanced checkbox / `--box-v1`),
  composed fine/coarse field, "exchange" display for open-air rooms, `MaxSpeed` used, room-detection
  workaround deleted. Finding: with < 1 m margins the openings sit in the sponge band and the fine box just
  copies the coarse flow; 2 m margins halve the window error; interior doors never improve (one-way nesting).
- Revit live link (Revit side + protocol + fake-app script). Licence inventory script. VALIDATION.md.
  PLAN.md: licence keys/trial dropped (open source).

## Known rough edges

- Nested box slow until item 1 lands; thermal runs fall back to the v1 box; holds/report images use the coarse
  field. Opening flow is now the carved-throat integral (PLAN_V2 1.1 done; carved holes are 0–15 % larger than
drawn; checks 10/12a are effective-Cd checks, band 0.37–0.80); throat sum reads a few % low next to walls (43b/45a
ledger residual ~5.5 %, link-flux fix queued). Scalar region includes a
  1 m outdoor ring. D3Q7 thermal walls plain bounce-back. 41f margin thin (1.34 K vs 1.62 K bound). Video encodes
  at viewport size. Installer unsigned, no update feed.

## Remaining plan

Finish the three cut items; IFC (needs the user's MPL decision); geometry diagnostics; GPU fallback tiers; crash
reporting (service = user's choice); code signing (cert = user); user test; macOS spike; measured-room validation
cases (Annex 20, Karava/Tominaga) listed in VALIDATION.md; RESEARCH_PROMPT_2.md results when the user has them.

## Open questions for the user

Still-air view verdict. Git commit + MIT LICENSE. IFC licence policy (MPL-2.0 ok?). Crash-reporting service.
Code-signing certificate. Real Revit export for test buildings (needs the add-in installed once).
