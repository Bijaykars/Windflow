# How it works

## Overview

The app (`src/WindFlow.App`, Avalonia + Direct3D 12 via Vortice, .NET 10) reuses the C# lattice-Boltzmann
solver core (checked against analytic and published benchmarks, see [`VALIDATION.md`](../VALIDATION.md); not a substitute for measured-building validation) (`src/WindFlow.Core`) that used to live behind a Revit plugin. The Revit add-in (`src/WindFlow.Revit`)
is being reduced to a thin exporter: pick storeys in Revit, write `model.json`, open it in WindFlow.


## How it works

```
BuildingModel (metres, model axes) ──► Voxelizer.Build ──► VoxelGrid (air / solid / fixed / device cells)
                                                │
                                      LbmSolver / LbmSession ──► FlowField (velocity, pressure, temperature)
                                                │
                                      Analysis.Analyze / ScalarSolver ──► SimulationResult
                                                │
                          WindFlow.App.Viewport (D3D12 particles, slices, gizmos)
```

- **Solver**: D3Q19 lattice Boltzmann, regularized BGK collision with Smagorinsky LES, one per-cell kernel
  shared by the CPU path (`Parallel.For`) and the GPU path (ILGPU: CUDA, OpenCL, or CPU fallback).
- **Thermal (beta)** is a hybrid scheme: the D3Q19 flow plus a finite-volume energy equation whose face fluxes are the
  lattice's own mass fluxes (van Leer limited advection, Boussinesq buoyancy), so a uniform temperature stays uniform
  and a sealed room's energy is conserved (0.004–0.07 % in the checks). It is checked against textbook cases: the 3-D
  differentially heated cavity read 3–4 % below Tric et al. (2000) at n = 160 on 2026-10-10, with a test geometry whose hot and cold boxes also
  held strips of the adiabatic side walls at their temperature (P14-02, fixed in Batch 4: faces are now fixed per solid face); the queue V run (2026-10-20) reads 0.1 % / 0.5 % from Tric (Nu 2.056 / 4.358 at n = 160); the sealed filling box (19a) reads 1.30 K on the corrected 3.0 m room
  against its 1.8–8.6 K band and FAILS it (an under-resolved plume, see Known limitations). With Thermal on, each room is held at the indoor temperature by an ideal heater/cooler
  (on by default; the "Hold indoor temperature" box turns it off; a room with its own split AC is not held and says so). The hold acts on the room's volume mean, so a stratified room's
  occupied zone differs, and a note says so when it is more than 1 K off. Ordinary walls are adiabatic (no U-values); only Surface heat sources exchange heat with the air, by a convection
  correlation (Alamdari–Hammond natural convection with a forced term, Batch 6e). It has not been checked against measured rooms, which is why the toggle says "beta". The older D3Q7 thermal lattice stays behind an option (`RunOptions.ThermalScheme`).
- **Wind turbulence** (High only) follows EN 1991-1-4 (a Kraichnan-style synthetic field, Smirnov decorrelation in time);
  Standard and Preview use a steady profile. The **wall model** (Expert → Settings, "Wall model (experimental,
  wind-only)", a Werner–Wengle wall function) is off by default on every launch and greyed out while Thermal is on. On the measured test rooms it gave
  mixed, grid-dependent results (Annex 20 passes at 4 cells across the slot, fails at 8), so it stays experimental.
- **Domain**: the whole building in 3D — every storey's walls, slabs, roofs, columns and furniture — plus an
  outdoor margin so wind goes over the roof and around the façades and pressures emerge from the flow, not
  from a Cp table. The default **Compact** wind domain keeps the margins small (blockage 10.5 % on the sample house); Expert → Settings → Domain: **Full** uses the COST 732 margins
  (5 H upstream, 10 H downstream plus a sponge, 5 H lateral, blockage ≤ 3 %) at about 11.7 times the cells. The app notes a blockage above 3 % (see [Known limitations](LIMITATIONS.md)). The whole-building outer ring
  has a non-equilibrium velocity inflow, ρ = 1 outflow, an absorbing sponge and a prescribed-velocity lid (batch 6b); a simulation box with an open top keeps the older equilibrium ring. An equilibrium inflow also exists as an experimental switch (`WINDFLOW_INFLOW=EQ`) and is not validated.
- **Openings** are carved open or filled closed without touching the source geometry, so toggling a window is
  cheap (a warm restart, not a rebuild).
- **Devices** (ceiling fans, pedestal fans, split AC, cassette AC, exhaust fans, hoods, diffusers) are modelled
  as Guo body-force regions that accelerate air already in the cell rather than injecting fixed-velocity
  patches, so they conserve mass by construction (a diffuser moves room air and adds no fresh air). Expert → **Devices** is a palette of named presets
  (Ceiling fan 1.2 m, Pedestal fan, Split AC 1.5 ton, Cassette AC, Exhaust fan (bath), Kitchen hood, Ceiling
  diffuser): click a tile, and a ghost follows the cursor on the surface that preset mounts on (ceiling, wall
  or floor); click to drop it, **R** turns a floor fan 45°, **Esc** cancels. A dropped device drags only along
  its own surface. The numbers sit behind **Details** on the selected row. Every row carries a live
  **resolution OK / refine needed** chip from PLAN_V2 3.3: a fan disc, or an outlet's short side, under
  8 cells across at the current cell size (the fine box's inside a simulation box) needs a finer grid.
  Click a red **refine needed** chip to put a simulation box around that device (with the other flagged
  devices in the same room): 1.5 outlet or disc widths of margin plus the Grow-box blend-band clearance. Inside
  a box that resolves it the chip reads **resolution OK (box)**; a device no box can resolve here (a split AC's
  120 mm slot needs 15 mm cells) reads **too small to resolve**, in grey, with the reason in its tip.
- **Units and axes**: metres, seconds, m/s, Pa, °C, ppm. Axes follow the source model: X/Y horizontal, Z up.
- **Live solving**: the app steps the solver on its own thread and publishes a downsampled field at up to
  ~10 Hz; the viewport interpolates between the last two fields so particle motion stays smooth between
  publishes.
- **Rendering**: up to 1,000,000 GPU particles advected through the velocity field with world-space fading
  trails, colour by Speed or by Still air (a static amber cloud of cells slower than a threshold, default
  0.10 m/s, adjustable via a slider in the panel; the room table's still % uses the same threshold; the PDF's Stagnant % always uses 0.10 m/s). Slices
  show speed, pressure or temperature; probes read velocity/pressure/temperature/CO₂/age/comfort at a point;
  the results panel reports room ACH, mean speed, stagnant fraction and (advanced) temperature, CO₂, mean
  age and comfort.
- **Through-opening exchange and net outdoor inflow** (portal step 1, measurement only): `RoomResult.ThroughOpeningInflow` is the time-mean air entering a room across the throat planes (`OpeningFluxPlan` Pos/Neg) of the openings whose wall it touches on one side, in m³/s and ACH; `NetOutdoorInflow` sums, over the room's exterior openings only, inflow minus outflow floored at 0 per opening. `UnexplainedShare` is the part of all air entering (total exchange plus the unlisted-gap inflow) that is not through an opening plane. Room membership, the ledger and every threshold are unchanged.
- **Exchange instead of ACH**: a room whose outline runs through open air (typically a room detected on an
  imported mesh, where there is no wall at that edge) has almost no inflow through openings or devices;
  what crosses its outline is the same air circulating. When the counted inflow is under 5 % of that
  crossing, the room row shows `exchange 2.8 m³/s` with a grey dot instead of an ACH (hover for why), rather
  than a bogus 0 or several hundred ACH.
- **Devices** are validated in the inspector: flow capped at 5 m³/s, radius capped at 1.5 m, supply
  temperature clamped to 5–40 °C. An edit that raises the peak speed above the session's limit restarts
  the solve.
- **Logging**: each launch writes a log file to `%LOCALAPPDATA%\WindFlow\logs\`; the last 10 are kept.
  Settings → "Open log folder"; the log path is shown in the status strip after an error.

### Simulation box (nested fine grid)

Expert → **Box**: drag the box around the area to study. The whole building keeps solving at its usual
resolution, and a second grid at **half the cell size** is solved inside the box, fed at its faces by the
whole-building run (`LbmSolver.StartNested`, one-way 2:1 nesting with an 8-cell sponge band). Particles,
slices, the still-air cloud and probes inside the box come from the fine grid; outside it, from the whole
building. The box sharpens the local field view only (particles, slices, probes, devices inside it): a room's ACH and its opening flows always come from the whole-building run, never from the box.
A two-way coupled box was built and dropped in batch 6l because it did not beat the whole-building run: against a uniform run at the box's cell size the box read the w-hall-e opening 14.7 % off and the Hall's total ACH 12.7 % off,
the whole-building run 11.1 % and 3.3 % (VALIDATION.md check 48b; FIXLOG Batch 6l). The mass ledger and every other room stay whole-building.
The status strip reads `box 7.6×9.9×5.1 m · fine 127 mm · 187.2k cells · coarse 255 mm`.

- Box faces may cut through rooms: the faces carry the building flow, so there is no "cut" warning any
  more. Core still notes a face that is partly inside the building or had to be closed as a wall.
- The **blend band** (the sponge, ~1.15 m on the sample house) is drawn faintly inside the box outline. An
  open opening in it is tagged **in blend band** (the fine grid is pulled toward the coarse run there), and
  **Grow box** in the top bar moves the offending faces out until none is.
- **Lock** with a box shows the averaged fine-in-coarse composite, not the last instantaneous field.
- Dragging the box replaces only the fine grid (warm-started from the whole-building flow, which keeps
  running); the averaging window restarts, so the numbers read "averaging" for one flow-through.
- Thermal runs cannot nest yet (isothermal only): with Thermal on, the box falls back to v1 and says so.
- Cost: the fine box refreshes its faces on the CPU every coarse step, so the solve advances several times
  slower in simulated time than the whole building alone (sample house, RTX 5070 Ti: ~300 vs ~1,700 MLUPS
  of lattice work). A nested run takes about a minute of wall time to reach its first trusted numbers.

**Expert → Settings → "Box is the whole domain (v1)"** (default off) keeps the old behaviour for
comparison: the box alone is solved with open faces, which misreads any face that cuts through indoor air
(Bedroom 1 door flow 5× the whole-building value). `--box-v1` does the same for headless runs.
