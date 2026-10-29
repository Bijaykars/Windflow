# WindFlow standalone — build plan (2026-09-21)

Sources: two deep-research reports (Downloads: `compass_artifact_wf-97ac0869…md`, `deep-research-report (2).md`)
plus the current repo state in `HANDOFF.md`. Where the reports disagree, the decision and reason are below.

## Decisions

| topic | decision | why |
|---|---|---|
| App stack | .NET 10 + **Avalonia** (MIT) shell + own **Direct3D 12 viewport via Vortice.Windows** (MIT) | Both reports rank it first. Reuses the validated C# solver. No royalties. |
| Solver GPU layer | **Keep ILGPU now.** Copy a downsampled velocity field (about 128³) to the renderer 5–15 times a second. | Reports disagree: one says port to ComputeSharp at week 6 for zero-copy. The copy is about 32 MiB per update, which is cheap. Particles do not need the full grid. Porting a validated solver before there is a product is the expensive path. Revisit only if a measurement shows the copy is the bottleneck. ComputeSharp is also single-maintainer and Windows-only. |
| Solver interface | No new `ISolverBackend` yet. Add a **stepping API** to the existing solver (start, step N, publish field, stop). | One implementation needs no interface. The JSON model contract is already the seam. |
| UI kit | FluentAvalonia (MIT) controls, own dark tokens, **Lucide** icons (ISC), viridis/cividis + a diverging map. Dock (MIT) only in Advanced, later. | Report 2. One icon family. No docking in Basic. |
| In-viewport tools | Own gizmos drawn in the D3D12 pass (box with six face handles, wind arrow, device placement by ray-pick). ImGui.NET only if own gizmos stall. | Fewest dependencies. |
| Hero visual | GPU particles with fading trails, 250k–1M, pattern from Mapbox webgl-wind (ISC). | Both reports. |
| Import | **Revit thin exporter first** (existing extractor writes the JSON model). Assimp (BSD) meshes second. IFC decision deferred to month 3. | Reports disagree on web-ifc (MPL-2.0) and xbim (CDDL). That is a business call for you, not needed yet. |
| Devices | Guo body-force regions, not fixed-velocity patches. Ceiling fan = actuator disk with axial + swirl profiles. Split AC = linked return + cooled supply, zero net mass. | Both reports. Fixed-velocity patches inject mass and fight the trust gate. |
| Collision model | Keep regularized BGK + Smagorinsky. Walls first: signed-distance voxels + interpolated bounce-back. | Report 2: walls matter more than collision on coarse grids. Cumulant later if a benchmark demands it. |
| ML surrogate | Not in the first six months. Later: coarse-to-fine residual network, ONNX Runtime. | Both reports. No training data yet. |
| Video export | Windows Media Foundation. No FFmpeg. | GPL risk. H.264 patent position unverified; offer it, keep GIF. |
| Installer | Velopack (MIT). Code signing from first public beta. | Both reports. |
| Do not use | FluidX3D, Palabos, waLBerla, OpenLB, Qt, Unity, Unreal, Veldrid (dead), HelixToolkit.SharpDX (D3D11), OpenVDB, FFmpeg, QuestPDF, iText, Fluent System Icons. | Licence or health. |

## Repo layout after the change

- `src/WindFlow.Core` — unchanged role (net8.0, no UI). Gains the stepping API and body-force devices.
- `src/WindFlow.App` — **new**, net10.0-windows, Avalonia shell + `Viewport/` (D3D12 renderer, particles, gizmos).
- `src/WindFlow.Revit` — shrinks to a thin exporter: ribbon button writes `model.json`; later a named-pipe live link.
  The WPF panel and DirectContext3D server are deleted once the app reaches parity (end of phase 2).
- `src/WindFlow.Viz`, `WindFlow.Report` — kept for plan images and the PDF until the app renders them itself.
- `src/WindFlow.Sandbox` — stays the validation harness. Every phase must keep it green.

## Phase 0 — clean baseline (2–3 days)

1. Resolve the half-finished thermal edit in `LbmSolver.cs` (CPU kernel edited, GPU not): finish it or revert it.
   → verify: sandbox checks 1–17 pass and CPU/GPU parity holds.
2. Mark thermal/age/CO₂ checks 18–21 as known-failing in the sandbox output (fixed in phase 3, not now).
3. **First git commit** (needs your yes). Everything is untracked today; a rebuild without history is reckless.
4. Export two real multi-room Revit models to JSON with the existing extractor (needs the add-in installed once,
   needs your yes) or use sandbox scenes `twostorey` and `atrium` as stand-ins.

## Phase 1 — smooth particles in a real building (2 weeks)

Scope rule: no docking, no IFC, no stream tubes, no new physics, no nested grid.

1. Avalonia window, dark tokens, top bar (Load, Run), status strip. → verify: launches, loads `model.json`.
2. D3D12 viewport embedded in Avalonia. **Risk spike, day 1–2:** NativeControlHost swap chain versus Avalonia
   composition GPU interop; pick the one that allows UI overlays without airspace problems.
   → verify: spinning cube at 60 fps inside the window, resize works.
3. Building render: meshes from the model, clay + ghosted walls, orbit/pan/zoom. → verify: both test buildings.
4. Run the existing solver to completion, upload the downsampled mean field as a 3D texture.
5. Compute-shader particle advection (RK2/RK4), respawn in low-age regions and at open inlets, fading trails,
   colour by speed, one legend. → verify: 500k particles, **60 fps on the RTX 5070 Ti and ≥ 30 fps on the AMD
   integrated GPU** in the same machine (frame-time overlay, measured not guessed).

Done: open a real multi-room building, orbit through it, smooth particles everywhere air moves.

## Phase 2 — live, interactive app (to week 6)

1. Solver stepping API in Core; solver on its own thread; publishes the downsampled field 5–15 Hz through a
   triple buffer; renderer interpolates. → verify: sandbox still green; viewport never drops below 55 fps while solving.
2. Click an opening in the viewport to open or close it; wind arrow gizmo; warm restart from the current state.
   → verify: toggle a window, flow visibly changes within a few seconds.
3. Basic mode inspector (scope, wind, openings, Run/Pause, quality). Trust badge from the mass ledger.
4. Simulation box v1: drag a box, solver runs a single dense grid inside it with open boundaries.
5. Section box and x-ray walls. MP4 turntable via Media Foundation. Velopack installer.
6. Revit add-in reduced to exporter. Delete the WPF panel and DirectContext3D server.

Done: clean-machine install, load building, move a window, see the air change, export a video.

## Phase 3 — physics credibility and devices (to month 3)

1. Fix the three known bugs in this order: mean age ≈ 2× (source term and outlet audit against τ = V/Q),
   zero-wind buoyant divergence (Guo forcing, de Vahl Davis + Fusegi cavity as the test), temperature
   checkerboard (TRT thermal lattice). → verify: sandbox 29/29 plus cavity Nusselt within published bands.
2. Signed-distance voxelisation + interpolated bounce-back + thin-wall handling.
3. Body-force device primitive; ceiling fan (swirl), pedestal fan, split AC, cassette, exhaust fan, with generic
   presets. Supply temperature on sources. → verify: each device conserves mass and energy in a sealed room.
4. SET / elevated-air-speed comfort (re-implement from pythermalcomfort, MIT, as reference).
5. Nested 2:1 fine box fed by the coarse whole-building run, sponge band 8–16 cells.
   → verify: fine box agrees with a uniform fine reference run.
6. Migrate Advanced workflows into the app: compare A/B, probe, slices, iso-surfaces, annual, sweep, report.
7. Assimp mesh import + generalized-winding-number inside/outside test. zfp snapshot playback.
8. Validation page: Annex 20 room, Karava/Tominaga cross-ventilation, numbers published with the app.

## Phase 4 — shippable (to month 6)

IFC decision and reader, Revit live link over a named pipe (Revit side done; app side in progress), geometry
diagnostics, device library UX, GPU fallback tiers, crash reporting, code signing, automated licence inventory
(done: `tools/licence-inventory.ps1`), 30-minute no-help user test, macOS feasibility spike, first ML dataset
generation. Licence keys and trial are dropped: the user has committed publicly to releasing everything open
source (MIT), including the solver and HVAC modelling.

## Open questions for you

1. Git commit now? (recommended before phase 1)
2. Install the current add-in once to export real buildings, or start with sandbox scenes?
3. IFC policy: is MPL-2.0 (web-ifc) acceptable to ship, or strictly MIT/Apache/BSD? Needed by month 3.
4. Is macOS a requirement in the first year? If yes, the viewport should go Vulkan/WebGPU instead of D3D12 — say so before phase 1.
