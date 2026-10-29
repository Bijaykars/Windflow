# Peer GPU-LBM-LES papers vs WindFlow

A commenter pointed to two Leeds papers as "validated real-time GPU LBM-LES with Smagorinsky." Read in full
(open-access copies below); a third, closely related Leeds paper validates against the *same* wind-tunnel
data WindFlow failed at the time of writing (2026-09; it met all four metrics in a 2026-10-09 run with measured approach turbulence, before the 2026-10-12 fixes; it meets all four again on the fixed code in the queue V run of 2026-10-20, q 0.746) (Tominaga-Blocken 2015), so it is included. Sources: PDFs downloaded from
White Rose eprints and read directly (no paywalled text quoted beyond short phrases).

- **P1** Khan, Delbosc, Noakes, Summers (2015) "Real-time flow simulation of indoor environments using
  lattice Boltzmann method," *Building Simulation* 8:405-414. doi:10.1007/s12273-015-0232-9. Open PDF:
  eprints.whiterose.ac.uk/86462 (accepted manuscript).
- **P2** Delbosc, Summers, Khan, Kapur, Noakes (2014) "Optimized implementation of the Lattice Boltzmann
  Method on a graphics processing unit towards real-time fluid simulation," *Comput. Math. Appl.* 67(2):462-475.
  doi:10.1016/j.camwa.2013.10.002. No open PDF of the journal version was found; content verified instead
  from N. Delbosc's PhD thesis (Univ. of Leeds, 2017, same solver, same author, same equations and cases),
  open at etheses.whiterose.ac.uk/13546 — cited below as "P2/thesis," with thesis page numbers.
- **P3 (bonus)** King, Khan, Noakes (2018) "Modelling urban airflow and natural ventilation using a
  GPU-based lattice-Boltzmann method," *Building and Environment* 125:273-284. Open PDF:
  eprints.whiterose.ac.uk/120807. A 3-page extended abstract of the same study is eprint 141158.

## 1. Paper facts

| Item | P1 (Khan et al. 2015, ventilated chamber + hospital room) |
|---|---|
| Lattice / collision | D3Q19 velocity + D3Q6 thermal (Guo et al. 2002 coupled Boussinesq); **standard BGK**, not regularized or MRT |
| Smagorinsky | Cs = 0.1 (same value, physical and lattice — dimensionless); applied via local relaxation time; turbulent Prandtl Prt = 0.85 |
| Viscosity floor / Re / Mach | No explicit floor. Molecular lattice viscosity ~1.05e-6 (negligible); Re (hydraulic dia.) = 10,200; lattice inlet velocity u0 = 0.1, "should never exceed 0.2" |
| Grid / domain | 160x86x127 ~= 1.7M cells, uniform cubic; inlet 9x18 cells; room 4.2x3.36x2.26 m |
| Inlet / outlet BC | **Zou-He** velocity at inlet; zero-pressure (Neumann) outlet |
| Wall treatment | Half-way bounce-back (velocity, no-slip); fixed-value (Dirichlet) wall temperature; **no wall function** |
| Validation | (a) 3D lid-driven cavity Re 100/1000 @256^3 vs spectral benchmarks (Ku 1987; Albensoeder & Kuhlmann 2005) — good agreement, no % error stated. (b) chamber vs **ANSYS Fluent LES, not experiment** — qualitative only, "differ numerically" near the ceiling. (c) hospital ward — illustrative only, no reference data at all. Paper states experimental validation is future work. |
| Performance | NVIDIA Tesla K40; 1.7M-node chamber run: 0.34 s compute per simulated second -> 2.97x real-time. "Real-time" = physical time per step >= wall-clock time per step. |

| Item | P2/thesis (Delbosc et al. 2014, GPU implementation + boundary-condition survey) |
|---|---|
| Lattice / collision | D3Q19; production/validation runs use **standard BGK** + Smagorinsky (MRT, cascaded LBM benchmarked separately, not used for validation cases) |
| Smagorinsky | Literature value "typically Cs ~ 0.1"; own 2D cavity sensitivity study (Re 10,000, N=256) found **Cs = 0.03 more accurate**, unstable below that |
| Viscosity floor / Re / Mach | None; same chamber case as P1 (u0 = 0.1 lattice) |
| Grid / domain | Same 1.7M-cell chamber case as P1; standalone GPU benchmarks up to several hundred^3 cells |
| Inlet / outlet BC | Chapter catalogs Force-Equilibrium, half-/full-way Bounce-Back, Zou-He, Interpolated (Bouzidi) Bounce-Back. States plainly: force-equilibrium "does not conserve mass... should be avoided in production code" — the chamber case (P1) instead uses Zou-He. |
| Wall treatment | Half-way bounce-back standard; interpolated (Bouzidi) bounce-back described as more accurate but not used in the validated chamber case |
| Validation | 2D Poiseuille flow, 2D/3D lid-driven cavity vs Ghia et al., thermal diffusion/advection/natural convection in a cavity — all analytic/numerical benchmarks, **no experimental data** |
| Performance | Tesla K40 / GTX Titan / GTX 780Ti / dual-GPU Titan Z benchmarked; D3Q19 BGK isothermal kernel: ~1518-1531 MLUPS (K40), ~1785-1800 MLUPS (780Ti), ~2500 MLUPS (Titan Z, dual-GPU); journal abstract's headline figure is lower (~half a billion/s) than the thesis's later peak (~1.5 billion/s) — same code line, different point in its optimization. |

| Item | P3 (King, Khan, Noakes 2018, cross-ventilated cube, vs Tominaga-Blocken 2015) |
|---|---|
| Lattice / collision | D3Q19, standard BGK + Smagorinsky (same equations as P2) |
| Smagorinsky | Not pinned to one number for the LBM run; text cites "C typically 0.01-0.2"; Fluent's own LES used Cs = 0.02 |
| Viscosity floor / Re / Mach | None stated; total viscosity = molecular + Smagorinsky eddy viscosity |
| Grid / domain | Mesh sensitivity: H/16 = 1.2M cells, H/32 = 9.8M cells, H/48 = 33.2M cells (H = cube height). Velocity converged by H/16; **facade pressure needed H/32**. Production runs at H/32. |
| Inlet / outlet BC | Uniform velocity (scale model) or log-law ABL profile (full-scale Silsoe case) at inlet; **constant mass-flux outflow** at outlet (not zero-pressure) |
| Wall treatment | Half-way bounce-back; explicitly **no wall function**, sensitivity to wall-adjacent mesh noted |
| Validation | Directly against **Tominaga & Blocken (2015)** wind-tunnel + anemometry — the same dataset WindFlow failed in 2026-09 and met in its last pre-fix run (2026-10-09) and again on the fixed code (queue V, 2026-10-20, q 0.746). Normalised ventilation rate Q*: <10% error (LBM) vs <5.6% (Fluent) for the isolated-cube, perpendicular case; up to 60% for a low-data-count case. Velocity: LBM "tends to overestimate ... peak regions." |
| Performance | NVIDIA GTX 780Ti; "several orders of magnitude"/~700x faster than Fluent (companion 3-page abstract) |

**Important caveat on P3's headline number:** Q was not measured as the LBM's own volumetric throat flow.
It was computed from the LBM's simulated ΔCp fed through the orifice equation using an **externally supplied,
experimentally derived discharge coefficient (Cd = 0.658)** — the same Cd for experiment, Fluent and LBM alike.
That validates the solver's facade pressure prediction, not its throat discharge physics. WindFlow's failure
mode is specifically the throat discharge coefficient (it measures actual carved-opening flow and back-derives
Cd = 0.76-0.90 vs 0.6-0.65 expected) while its sealed-body ΔCp is already inside the literature band — i.e.
WindFlow's sealed-body windward Cp is inside the literature band and its leeward Cp is at or beyond the band's edge, and the throat Cd that P3 side-steps is untested in the same way.

## 2. Side-by-side vs WindFlow

| Item | WindFlow | P1/P2 (indoor chamber) | P3 (cross-vent) |
|---|---|---|---|
| Lattice / collision | D3Q19, **regularized** BGK + Smagorinsky | D3Q19, standard BGK + Smagorinsky | same as P2 |
| Smagorinsky Cs | 0.16 | 0.1 (P1 production); 0.03 found better in a coarse-cavity study | 0.01-0.2 range cited, not pinned |
| Viscosity floor | explicit floor, NuLatMin = 5e-4 | none (molecular v is just computed and is negligible) | none |
| Lattice velocity scale | 0.08 | 0.1 (P1: "never exceed 0.2") | not stated |
| Cell size / openings | 0.1-0.3 m; openings/inlets 4-9 cells across | inlet 9x18 cells | H/16-H/48 (16-48 cells across cube height) |
| Velocity inlet BC | link-wise Ladd bounce-back + PI flux controller (F1, replaced an equilibrium reset) | **Zou-He** (production case); thesis flags equilibrium-reset as mass-non-conserving | uniform / log-law profile, method per P2 |
| Outlet BC | pressure/open Opening | zero-pressure (Neumann) | constant mass-flux outflow |
| Wall treatment | halfway bounce-back + Bouzidi interpolation (momentum); plain halfway BB (thermal) | plain halfway bounce-back, no wall function | plain halfway bounce-back, no wall function |
| Validation targets | IEA Annex 20 (Nielsen/Restivo LDA), Tominaga-Blocken 2015 (wind tunnel), MINIBAT (Kuznik/hot-wire) — all **measured** data; one meets its metrics on the fixed code (cross-ventilation at H = 40 with measured approach turbulence; queue V, 2026-10-20), two fail on q | lid cavity vs spectral CFD; chamber vs **Fluent LES**, not experiment | Tominaga-Blocken 2015 wind tunnel, same case as WindFlow |
| Cross-vent result | Q/(UH A) 0.454 against an opening-flux band of 0.40-0.47 (tracer-gas air exchange 0.25); Cd_solver 0.76-0.90 vs 0.6-0.65 (2026-10-09 figures; queue V, 2026-10-20, read Q/(UH A) 0.498 at H = 40; Cd_solver not in queue V) | n/a | Q* within 10%, but via externally supplied Cd (see caveat above) |
| Inflow turbulence | steady in Preview/Standard; EN 1991-1-4 synthetic turbulence in High and in the cross-vent validation | steady, none (P1); not discussed (P2) | steady/log-law, none |
| GPU perf | (not in these docs) | ~918 MLUPS realised (chamber run), ~1.5 GLUPS peak kernel (K40) | not quantified beyond "~700x Fluent" |

## 3. What WindFlow should adopt or test

1. **F1's Ladd-bounce-back supply inlet is independently vindicated.** P2/thesis names the "force equilibrium"
   reset (WindFlow's pre-F1 method) as mass-non-conserving and explicitly warns against it in production code;
   P1's own validated case uses Zou-He instead. No new action — confirms F1 was the right fix. *Confidence: high.*
2. **Chase Cd/Cp convergence to a finer grid than Q convergence needs.** P3 found velocity/Q converge by H/16
   cells across the cube but facade pressure needs H/32. WindFlow's own H20->H40 grid study shows the same
   pattern under the surface: bulk Q barely moves (0.585->0.588) but per-opening patch Cp drops 0.48->0.40
   (N20->N40) — not yet converged. Run the cross-vent case at H=60-80 tracking Cd/Cp specifically, not just Q.
   *Confidence: high — it is WindFlow's own un-converged data, corroborated by an independent mesh study.*
3. **P3's <10% Q agreement likely does not clear WindFlow of the discharge-coefficient problem** — it was
   computed from an external Cd, not the solver's own throat flow (see caveat above). Don't treat it as
   evidence that plain BGK+Smagorinsky+bounce-back at this resolution gets Cd right; it wasn't tested there.
   This supports RESEARCH_PROMPT_4's own conclusion that a **calibrated opening-discharge model** is the
   needed next step, rather than expecting collision/turbulence tweaks to fix Cd. *Confidence: medium-high.*
4. **Cs 0.16 is on the high side of the range these three papers use (0.02-0.1, with 0.03 found more accurate
   in a coarse-grid sensitivity study) but not obviously wrong** — none of them report a value near 0.16, and
   RESEARCH_PROMPT_4's own F4 experiment found Cs 0.1 already diverges with inflow turbulence at WindFlow's
   current floor/resolution. Lowering Cs without first fixing the inflow-turbulence forcing looks unlikely to
   help and matches WindFlow's own prior finding, not new information. *Confidence: low — already tested by us.*
5. **None of the three papers use synthetic inflow turbulence either** (P1: none; P3: uniform/log-law, no
   turbulence injection), yet P3 still lands within 10% on bulk ventilation rate with steady inflow. This
   weakens the "no turbulence -> wrong Q" half of RESEARCH_PROMPT_4's diagnosis for C, and strengthens the
   "discharge coefficient, not turbulence" reading already reached there. *Confidence: medium.*
6. **Wall treatment is not the differentiator.** All three papers use plain half-way bounce-back with no wall
   function, same as WindFlow (momentum walls already add Bouzidi interpolation, ahead of these papers). Not
   a source of WindFlow's gap; no action indicated.
7. **Lattice velocity scale (0.08) and lack of an explicit viscosity floor are the two structural differences**
   worth a cheap experiment: these papers get stability from Smagorinsky alone at their (much lower, ~9-48
   cells) resolutions without a floor. WindFlow's own F4 already tested floor 5e-4 -> 1e-5 and found it moves
   delivered flow <1% on Annex 20 — so a floor-only change is not expected to matter much here either.
   *Confidence: low, mostly already tested.*

## 4. Is their validation data usable in our harness?

- **P1/P2 chamber and hospital-room data: not usable.** The chamber is compared only against ANSYS Fluent LES,
  not measurement; no experimental profiles are published in either the paper or the thesis. The hospital
  ward has no reference data at all. Nothing here can serve as a measured-room case for WindFlow.
- **P3's Tominaga-Blocken comparison: same source WindFlow already has.** WindFlow's SOURCE.md already cites
  and uses digitised Tominaga & Blocken (2015) data directly; P3 adds no new open dataset, only a second
  solver's result on it (useful as a literature comparison point in VALIDATION.md, not as new harness input).
- **P3's own Meinders et al. (1999) approach-flow and AIJ array data** are potential *new* reference points
  (turbulent channel approach flow; array-of-cubes k/Uref^2) but are third-party experimental data cited
  secondhand in P3, not reproduced in full — would need the original Meinders/AIJ sources, not checked here.
- **Lid-driven-cavity benchmarks (P1, P2)** are numerical (Ghia et al.; Ku et al.; Albensoeder & Kuhlmann), not
  measured, and WindFlow's harness targets measured-room validation — lower priority than the three measured
  cases already in PLAN_V2 §1.4b.

## 5. What could not be verified

- The exact Smagorinsky Cs used in P3's *own LBM run* (only Fluent's 0.02 and a general "0.01-0.2" range for
  the LBM are stated in the extracted text).
- P2's journal text directly (doi:10.1016/j.camwa.2013.10.002) — no open copy found; content cross-checked
  via the PhD thesis (same author, same equations, matching citations in P1) and via P1's own reference list,
  not the journal PDF itself.
- Exact original-paper GPU/MLUPS figure for P2 (web search summaries cite "half a billion/s"; the thesis,
  written three years later on updated code, reports up to 1.5 billion/s on the same kernel family) — could
  not confirm which figure the 2014 journal text itself states.
- Whether P3's per-opening simulated Cd/Cp (as opposed to the externally supplied Cd = 0.658 used for Q)
  was ever reported — the extracted text shows facade Cp plots but no explicit simulated-Cd number.
