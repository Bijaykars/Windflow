# GitHub survey: permissively-licensed LBM code for WindFlow's three accuracy problems

Date: 2026-09-28. Scope: GitHub only (one paper-linked PDF is cited as literature, not code).
Every repo's LICENSE/COPYING file was opened before any source file. No code was copied into
this repo; quotes below are short excerpts (≤15 lines) with source URLs.

## 1. Repo table

| Repo | URL | License (verified) | Last commit (pushed) | Problems covered | Files read |
|---|---|---|---|---|---|
| Autodesk **XLB** | https://github.com/Autodesk/XLB | Apache-2.0 — [LICENSE](https://github.com/Autodesk/XLB/blob/main/LICENSE) | 2026-09-16 | 1, 2 (partial), 3 (no) | `bc_equilibrium.py`, `bc_zouhe.py`, `bc_regularized.py`, `smagorinsky_les_bgk.py`, `kbc.py`, `windtunnel_3d.py` |
| Autodesk **Neon** | https://github.com/Autodesk/Neon | Apache-2.0 — [LICENSE.txt](https://github.com/Autodesk/Neon/blob/main/LICENSE.txt) | not read (backend library for XLB; not LBM-specific, skipped for time) | — | README only |
| **lettuce** (lettucecfd) | https://github.com/lettucecfd/lettuce | MIT — [LICENSE](https://github.com/lettucecfd/lettuce/blob/master/LICENSE) | 2026-04-13 | 1, 3 (collision only) | `equilibrium_boundary_pu.py`, `regularized_collision.py`, `smagorinsky_collision.py` |
| **pylbm** | https://github.com/pylbm/pylbm | BSD-3-Clause — [LICENSE](https://github.com/pylbm/pylbm/blob/master/LICENSE) | 2026-05-14 | none of the three (generic 1D/2D/3D scheme framework, no LES/inlet-flux focus) | `boundary.py` listing only (not read in full — no LES/turbulence hits) |
| **LUMA** (cfdemons) | https://github.com/cfdemons/LUMA | Apache-2.0 — [LICENSE](https://github.com/cfdemons/LUMA/blob/master/LICENSE) | 2025-07-02 | 1 | `GridObj_ops_lbm_optimised.cpp` (`_LBM_regularised_opt`), `GridObj_ops_lbm.cpp` |
| **Musubi** (apes-suite/musubi-source) | https://github.com/apes-suite/musubi-source | BSD-2-Clause — [LICENSE](https://github.com/apes-suite/musubi-source/blob/main/LICENSE) | 2026-09-08 | 1, 3 | `mus_bc_fluid_nonEqExpol_module.fpp`, `mus_turb_wallFunc_module.f90`, `mus_wall_function_reichardt_module.f90`, file tree only for `mus_bc_fluid_wall_module.fpp` / `mus_bc_fluid_turbulent_module.fpp` |

Repos checked and found **not usable / not applicable**, licence already fine, but no relevant
capability found or code unreachable:
- **STLBM** — hosted on GitLab (`gitlab.com/unigehpfs/stlbm`), not GitHub, so out of scope for
  this survey; reported MIT in secondary sources but not verified first-hand. Not investigated further.
- **Aspen-Pxl/LBM-CFD** (claimed BGK/MRT/Cumulant + Smagorinsky LES, D3Q19) — the repository
  returned HTTP 404 on both the GitHub web UI and the API at survey time; it may have been
  renamed, deleted, or made private. Could not verify licence or content — **excluded**.
- **Taichi LBM_Taichi** (`hietwll/LBM_Taichi`) — 2D teaching example, no licence file found on a
  quick check, no boundary-flux or LES content beyond a basic D2Q9 solver. Not pursued.
- **JAX-LaB**, **LBM_JAX_Autodiff** — small personal JAX projects, no LES/wall-function/inlet-flux
  content surfaced in search; not investigated in detail given time budget.

## 2. Findings by problem

### Problem 1 — supply inlet under-delivers

**Best finding: XLB's `RegularizedBC` / `ZouHeBC`** (Apache-2.0)
`https://github.com/Autodesk/XLB/blob/main/xlb/operator/boundary_condition/bc_zouhe.py`
`https://github.com/Autodesk/XLB/blob/main/xlb/operator/boundary_condition/bc_regularized.py`

Implements Zou & He (1997, *Phys. Fluids* 9(6)) and Latt et al. (2008, *Phys. Rev. E* 77, 056703).
Both solve for the unknown (missing) populations at the boundary node from mass/momentum
conservation, then reconstruct with non-equilibrium bounce-back (`RegularizedBC` additionally
regularizes/filters the non-equilibrium part). This sets the **flux through the boundary face**,
not just the cell-centre velocity — directly addresses WindFlow's hypothesis. XLB's own docstring
on `ZouHeBC` says explicitly: *"One needs to use 'Regularized' BC at higher Reynolds [numbers]"*
because plain Zou-He doesn't enforce zero tangential velocity. XLB pairs `RegularizedBC` with
`SmagorinskyLESBGK` in its own wind-tunnel example
(`examples/cfd/windtunnel_3d.py:103`: `bc_left = RegularizedBC("velocity", prescribed_value=..., indices=inlet)`),
i.e. the exact combination WindFlow wants (regularized BGK + Smagorinsky + a flux-correct inlet).
Usability: moderate C# port effort — the math (10-15 lines of linear algebra per boundary node,
no PDEs) ports cleanly; the JAX/Warp scaffolding does not, so budget for a from-scratch
re-implementation of the formulas, not a translation of the file. Fits regularized BGK and
halfway bounce-back with no conflict — `RegularizedBC` is designed to compose with a bounce-back
wall implementation for corners/edges.

**Second finding: LUMA's regularised velocity BC** (Apache-2.0)
`https://github.com/cfdemons/LUMA/blob/master/LUMA/src/GridObj_ops_lbm_optimised.cpp` (function
`_LBM_regularised_opt`, ~line 313)

```cpp
/// \brief	Optimised application of regularised BC
///
///			This implementation assumes the velocity is normal to the wall as is
///			the case in the Latt et al. 2008 paper on which the conditions are
///			based. https://doi.org/10.1103/PhysRevE.77.056703
void GridObj::_LBM_regularised_opt(int i, int j, int k, int id, eType type, int subcycle)
```

Same Latt et al. (2008) method as XLB, but in plain imperative C++ over a flat array — closer in
shape to WindFlow's C# loop-based solver than XLB's JAX kernels, so **this is the easier one to
port line-by-line**. Weak point: LUMA's docs don't discuss flux delivery accuracy directly; the
match to WindFlow's problem is inferred from the method, not from LUMA's own validation.

**Third finding: Musubi's non-equilibrium extrapolation BC** (BSD-2-Clause)
`https://github.com/apes-suite/musubi-source/blob/main/source/bc/mus_bc_fluid_nonEqExpol_module.fpp`

Implements Guo & Shi (2002, *Chinese Physics*) and Guo, Shi & Zheng (2002, *Phys. Fluids* 14) —
the fourth method the task asked about (non-equilibrium extrapolation / Grad-family). Header
comment: *"The boundary condition for straight boundary with qVal=0 is based on: Guo, Z., & Shi,
B. (2002)... Non-equilibrium extrapolation method for velocity and pressure boundary conditions."*
Fortran (`.fpp`, preprocessed), ~570 lines for the module — larger port effort than the other two,
and Musubi's octree/treelm mesh abstractions are baked into the code, so this is best used as a
**reference for the formula**, not a source to translate directly.

**Documentation evidence of the under-delivery problem itself:** lettuce's `EquilibriumBoundaryPU`
(MIT) — `https://github.com/lettucecfd/lettuce/blob/master/lettuce/ext/_boundary/equilibrium_boundary_pu.py` —
carries this docstring warning verbatim:
> "Sets distributions on this boundary to equilibrium with predefined velocity and pressure. Note
> that this behavior is generally not compatible with the Navier-Stokes equations. This boundary
> condition should only be used if no better options are available."

This is independent, permissively-licensed confirmation that the equilibrium-reset inlet
(WindFlow's supply method until F1; since batch 3b Return/Fan patches are link-wise too, and the equilibrium reset remains for the wind ring) is a known-weak approach in the field — not a WindFlow-specific bug.
Note: lettuce itself does **not** ship a Zou-He or regularized *boundary condition* (only bounce-back,
anti-bounce-back outlet, and a regularized *collision* operator), so lettuce is evidence of the
problem, not a source of the fix.

No direct GitHub issue/test explicitly quantifying "42/61/68/71% at 1/2/4/6 cells" was found in any
of the surveyed repos; the lettuce docstring above is the closest documented acknowledgment.

### Problem 2 — openings pass too much air (discharge coefficient / cross-ventilation)

This is the weakest area of the survey — **no permissively-licensed repo was found with an orifice
discharge-coefficient or building cross-ventilation validation case.**

- XLB's own external-aero validation cases (`windtunnel_3d.py`, `multires_windtunnel_3d.py`,
  `flow_past_sphere_3d.py`, `turbulent_channel_3d.py`) are car/airfoil/sphere/channel flows, not
  openings or building envelopes. XLB does ship a KBC (Karlin-Bösch-Chikatamarla, entropic)
  collision operator — `https://github.com/Autodesk/XLB/blob/main/xlb/operator/collision/kbc.py` —
  one of the alternative collision families the task asked about, but it is implemented only for
  D2Q9 and D3Q27 velocity sets in the current source, not D3Q19, so it cannot be dropped into
  WindFlow without first deriving the D3Q19 KBC moments — non-trivial extra work, not a quick port.
- A paper, Tominaga-style cross-ventilation LBM study hosted at
  `https://eprints.whiterose.ac.uk/id/eprint/141158/3/Paper%20-%20LBM%20Cross-flow%20ventilation.pdf`,
  turned up in search as directly on-topic (LBM cross-flow ventilation validation), but the PDF
  would not decode through the fetch tool and the underlying code's licence/availability could not
  be confirmed in the time available. **Flagging as a lead for manual follow-up, not a verified
  finding** — read it directly rather than relying on this survey.
- No permissive repo implementing synthetic eddy method, digital-filter, or random-Fourier-mode
  inflow turbulence for LBM was found. The technique appears in papers (e.g. arXiv:2205.02774,
  arXiv:2304.04214) but the survey did not locate an accompanying permissively-licensed
  implementation.
- The excluded-by-rule codebases (OpenLB, FluidX3D, Palabos, waLBerla) are the ones most likely to
  have this validated in the literature (their published papers do orifice/ventilation cases), but
  per the task's licence rules their source was not opened and is not usable for WindFlow anyway.

**Weak conclusion:** the discharge-coefficient overshoot is more likely a physics/BC-composition
gap (equilibrium-family openings + no inflow turbulence + BGK/regularized collision underpredicting
shear-layer breakdown) than something one permissive repo already solves. Nothing found here should
be treated as validated — this is the one problem where I am not confident in the survey's coverage.

### Problem 3 — wall jets and near-wall shear on coarse grids

**Best finding: Musubi's wall-function family** (BSD-2-Clause)
`https://github.com/apes-suite/musubi-source/blob/main/source/turbulence/mus_turb_wallFunc_module.f90`
`https://github.com/apes-suite/musubi-source/blob/main/source/turbulence/mus_wall_function_reichardt_module.f90`
(+ `mus_wall_function_musker_module.f90`, `mus_wall_function_schmitt_module.f90`,
`mus_bc_fluid_wall_module.fpp`, `mus_bc_fluid_turbulent_module.fpp`)

Not Spalding's law itself, but the same family: log-law-based wall functions (Musker, Reichardt,
Schmitt) that compute a friction velocity and a corrected near-wall streamwise velocity, coupled
into the LBM boundary condition instead of plain bounce-back. The Reichardt module cites exactly
the literature WindFlow should be looking at:
```fortran
!! Haussmann, Marc; BARRETO, Alejandro CLARO; ... Krause, Mathias J. (2019):
!! Large-eddy simulation coupled with wall models for turbulent channel
!! flows at high Reynolds numbers with a lattice Boltzmann method
!! ... DOI: 10.1016/j.camwa.2019.04.033.
!! ... S. Wilhelm, J. Jacob, and P. Sagaut, "An explicit power-law-based wall
!! model for lattice Boltzmann method-RANS ... of the flow around airfoils"
!! Physics of Fluids 30, 065111 (2018) https://doi.org/10.1063/1.5031764
```
This is the closest permissively-licensed analogue to Han, Ooka & Kikumoto (2021) found — same
concept (replace bounce-back shear with a wall-model-corrected value at the first off-wall node),
different specific log-law. No implementation of Spalding's law itself, and no code from Han, Ooka
& Kikumoto's own group was found on GitHub (searched directly; nothing surfaced).
Usability: Fortran, moderate-to-high port effort (the wall-function math itself is ~30-60 lines of
iterative log-law solve per module and ports fine; the surrounding treelm/octree plumbing does
not). Compatible in principle with halfway bounce-back: Musubi's wall BC modifies the same
post-collision populations that a bounce-back BC would touch, so the two are structurally
compatible, not competing halves of the boundary treatment.

**Temperature wall function:** no open-source implementation found. The one directly relevant
paper — "A simple implementation of temperature wall function boundary in lattice Boltzmann method
for indoor thermal turbulent flows" (Building and Environment, 2025, involving Kikumoto) — was
found only as a paper; no linked GitHub repo surfaced in search. Treat this as **unmet** — if a
thermal wall function is needed, it will likely have to be implemented from the paper's algorithm
description rather than ported from existing code.

## 3. Skipped for licence reasons

| Repo | Licence | Action taken |
|---|---|---|
| TCLB (CFD-GO/TCLB) | GPL-3.0 | Reference only — README/docs not opened beyond confirming licence |
| Sailfish (sailfish-team/sailfish) | LGPL-3.0 | Reference only |
| FluidX3D | (excluded by task rule, never checked) | Not opened at all, per instructions |
| Palabos | (excluded by task rule) | Not opened at all |
| OpenLB | (excluded by task rule) | Not opened at all |
| waLBerla | (excluded by task rule) | Not opened at all |
| lbmpy | (excluded by task rule) | Not opened at all — referenced only via its public docs URL for the word "cumulant", no source read |
| Ladybug Tools / Butterfly, IfcOpenShell, web-ifc | (excluded by task rule) | Not opened at all |

No repo with a missing/absent LICENSE file was found among the ones actually inspected in this
survey (LUMA's top-level `NOTICE` alone was ambiguous, but its separate `LICENSE` file — checked
before concluding — is Apache-2.0).

## 4. Recommendations

1. **Inlet flux (Problem 1):** Port XLB's `RegularizedBC` formulas (Latt et al. 2008, built on
   Zou-He 1997) — best documented pairing with regularized BGK + Smagorinsky, but reimplement the
   math fresh in C# rather than translating the JAX/Warp code; use LUMA's `_LBM_regularised_opt`
   C++ as the more directly portable reference for the same algorithm.
2. **Opening discharge coefficient (Problem 2):** No permissive off-the-shelf answer found — this
   needs either (a) hand-rolled inflow turbulence (synthetic eddy/digital filter — implement from
   papers, no permissive reference code found), or (b) a from-scratch D3Q19 KBC/cumulant collision
   (XLB's KBC is D2Q9/D3Q27 only). Treat this recommendation as low-confidence; the whiterose.ac.uk
   PDF lead is worth chasing manually before committing engineering time.
3. **Wall jets / near-wall shear (Problem 3):** Port the method shape of Musubi's Reichardt/Musker
   wall functions (same family as Han, Ooka & Kikumoto 2021) — no Spalding's-law-specific
   permissive code exists, so implement Spalding's law directly from the paper using Musubi's
   module structure (friction-velocity solve → corrected near-wall population) as the template. No
   permissive thermal wall function exists; implement from the 2025 Building and Environment paper
   if/when needed.
