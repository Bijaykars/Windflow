# WindFlow — Technical Reference

October 2026 · Bijay Karki

WindFlow ranks design options for natural and mechanical ventilation on one building model and one grid; it does not certify a single absolute airflow number. This reference sets out the method behind each result, the equations the solver uses, every benchmark it has been checked against, and every known limit with its measured size.

## About this document

**The one rule.** Compare design options on the same model, the same grid and the same quality level. A change of cell size alone can move one room's air-change rate by up to about 35 % and the whole building by about 20 % (chapter 11).

**Where the numbers come from.** Every measured figure is taken from the project's test records, last re-run in October 2026. Equations labelled *standard form* are the textbook form of the named method. Where the solver's source shows the form actually used (the eddy viscosity, the force term of the devices, the opening loss, the surface convection and the widening of the batch interval), the text gives that form instead.

**Status.** WindFlow is a design-stage tool. Its results are estimates with stated uncertainty, not a substitute for a ventilation engineer or a code-compliance calculation.

### Notation

| Symbol | Meaning | Unit |
| --- | --- | --- |
| f\_i | population moving in lattice direction i | lattice |
| c\_i, w\_i | lattice velocity and weight of direction i (D3Q19: i = 0…18) | lattice |
| c\_s | lattice speed of sound, 1/√3 | lattice |
| τ | relaxation time of the collision | time steps |
| ν, ν\_t | molecular and turbulent (sub-grid) kinematic viscosity | m²/s or lattice |
| Δx, Δt | cell size and time step | m, s |
| ρ, u | density and velocity | kg/m³, m/s |
| a | local age of air (time since the air was outdoors) | s |
| ⟨a⟩ | volume-mean age of air in a room | s |
| V | room volume (nominal, as drawn) | m³ |
| Q | volume flow | m³/s |
| C\_d | discharge coefficient of an opening | – |
| A | open area of an opening | m² |
| U\_H | wind speed at building height | m/s |
| H | building height | m |
| T, β | temperature and thermal expansion coefficient | K, 1/K |

## 1. What WindFlow computes, and what it does not

WindFlow solves the wind-driven air flow through and around a building, then reads every room result off that solved air. Pressures on the façades are not looked up from a pressure-coefficient table: they emerge from the flow.

On the way from building model to report, only the solve and tracer steps repeat: the solver keeps stepping and averaging in batches until each room's result settles or the time budget runs out.

### Outputs per room

| Output | What it measures | Notes |
| --- | --- | --- |
| Fresh-air ACH | 3600 ÷ the room's mean age of air (chapter 6) | The headline number. Air that reaches the room through other rooms counts at its real age. |
| Total exch. ACH | Every m³/h entering the room volume, from outside and from other rooms | Not a ventilation rate. |
| Thru open. | Air entering through the room's own listed windows and doors | Measured on fixed opening planes, not the ragged room boundary. |
| Net outdoor air | Net flow through the room's exterior openings only |  |
| Mean speed | Mean air speed in the occupied zone, 0.1 to 1.8 m above the floor | m/s |
| Stagnant share | Share of the occupied zone below 0.10 m/s | % |
| Thermal (beta) | Temperature, heater or cooler duty, CO2, PMV within ±0.5, draught rate above 20 % | Chapter 9 |
| Openings | Flow through each opening and its direction | m³/s |
| Devices | Measured flow of each fan, AC unit, hood or diffuser | m³/h |
| Mass balance | Air in against air out; drives the trust line and the report cover box | Chapter 13 |

### What it does not do

- **Not a certification tool.** Results are comparative design guidance.
- **Not validated against a physical building.** Treat absolute ACH, comfort fraction and room temperature as comparative and self-consistent.
- **Not validated at all:** single-sided ventilation (one opening per room), openings narrower than 4 cells, absolute ACH in a real building.
- **Planned, not done:** IEA Annex 20 3-D rooms with temperature; the Architectural Institute of Japan (AIJ) guideline cases.
- **Thermal model unchecked against a measured room.** No measured, heated, occupied room has been simulated; one heat test (the filling box) still fails.
- **Fresh-air numbers unchecked against tracer gas** measured in a real building.
- **No wall heat loss.** Ordinary walls, floors, roofs and glazing are adiabatic: no U-values.
- **No IFC import.** Only a Revit export, WindFlow's own model file, or a mesh. A Revit OBJ is recognised and read as feet and Z-up automatically (chapter 4).

## 2. The solver

WindFlow is a D3Q19 lattice-Boltzmann solver with regularized BGK collision and a Smagorinsky sub-grid model (constant 0.16). The same kernel runs on the GPU (CUDA or OpenCL) and the CPU, and the two agree to 0.0 % on flow and thermal results.

### Lattice Boltzmann in plain words

The air is split into cubic cells. Each cell holds 19 *populations* f\_i: packets of air moving in one of 19 fixed directions. Every time step has two moves: **stream** (each packet hops to the neighbouring cell it points at) and **collide** (the packets in each cell relax towards an equilibrium set by the local density and velocity). Density, velocity and pressure are sums over the packets; nothing else is solved.

The 19 directions are the cell itself (rest), its 6 face neighbours and its 12 edge neighbours. The weights w\_i are the equilibrium shares of a cell at rest; face neighbours get twice the share of edge neighbours (1/18 against 1/36) because they are closer.

### Equations (standard form)

Stream and collide, for each direction i:

```math
f_i(\mathbf{x} + \mathbf{c}_i \Delta t,\; t + \Delta t) = f_i^{\mathrm{eq}}(\mathbf{x}, t) + \left(1 - \frac{1}{\tau_{\mathrm{eff}}}\right) f_i^{\mathrm{neq,reg}}(\mathbf{x}, t)
```

Density and velocity are moments of the populations:

```math
\rho = \sum_{i=0}^{18} f_i, \qquad \rho\,\mathbf{u} = \sum_{i=0}^{18} f_i\,\mathbf{c}_i, \qquad p = c_s^2\,\rho, \quad c_s = 1/\sqrt{3}
```

The equilibrium is the second-order expansion of a Maxwell distribution:

```math
f_i^{\mathrm{eq}} = w_i\,\rho \left[ 1 + \frac{\mathbf{c}_i\cdot\mathbf{u}}{c_s^2} + \frac{(\mathbf{c}_i\cdot\mathbf{u})^2}{2c_s^4} - \frac{\mathbf{u}\cdot\mathbf{u}}{2c_s^2} \right], \qquad w_0 = \tfrac{1}{3},\; w_{1\ldots6} = \tfrac{1}{18},\; w_{7\ldots18} = \tfrac{1}{36}
```

*Regularized* BGK rebuilds the non-equilibrium part from its stress moment before collision, which removes higher-order noise that destabilises plain BGK at low viscosity:

```math
f_i^{\mathrm{neq,reg}} = \frac{w_i}{2c_s^4}\,\mathbf{Q}_i : \boldsymbol{\Pi}^{\mathrm{neq}}, \qquad \mathbf{Q}_i = \mathbf{c}_i\mathbf{c}_i - c_s^2\mathbf{I}, \qquad \boldsymbol{\Pi}^{\mathrm{neq}} = \sum_i \mathbf{c}_i\mathbf{c}_i\,(f_i - f_i^{\mathrm{eq}})
```

The relaxation time sets the viscosity, and the Smagorinsky eddy viscosity is added to it cell by cell. The molecular viscosity used is the larger of real air's value and the stability floor (chapter 3):

```math
\nu = c_s^2\left(\tau - \tfrac{1}{2}\right)\Delta t, \qquad \nu_t = (C_s\,\Delta x)^2\,|\bar{S}|, \qquad |\bar{S}| = \sqrt{2\,\bar{S}_{\alpha\beta}\bar{S}_{\alpha\beta}}, \qquad C_s = 0.16
```

```math
\tau_{\mathrm{eff}} = \frac{1}{2} + \frac{\nu + \nu_t}{c_s^2\,\Delta t} = \frac{1}{2}\left[\tau_0 + \sqrt{\tau_0^{2} + \frac{18\sqrt{2}\,C_s^{2}\,\|\boldsymbol{\Pi}^{\mathrm{neq}}\|_F}{\rho}}\right], \qquad \tau_0 = \frac{1}{2} + \frac{\nu}{c_s^2\,\Delta t}
```

The solver solves the eddy viscosity and the relaxation time together in closed form, which is the right-hand side above, in lattice units (Δx = Δt = 1): the eddy part follows from the Frobenius norm of the non-equilibrium stress, taken over its six independent components with the off-diagonal ones counted twice. The regularization is the one shown, and C\_s = 0.16.

### Walls: interpolated (Bouzidi) links

A wall that cuts the link between two cell centres at fraction q of the link is placed at its true position by interpolation, from both sides. That is why a wall thinner than a cell stays leak-tight. Standard linear form, with ī the direction opposite to i and f\* the post-collision value:

```math
f_{\bar{\imath}}(\mathbf{x}_f, t+\Delta t) =
\begin{cases}
2q\, f_i^{*}(\mathbf{x}_f) + (1 - 2q)\, f_i^{*}(\mathbf{x}_f - \mathbf{c}_i\Delta t), & q < \tfrac{1}{2} \\[4pt]
\dfrac{1}{2q}\, f_i^{*}(\mathbf{x}_f) + \dfrac{2q - 1}{2q}\, f_{\bar{\imath}}^{*}(\mathbf{x}_f), & q \ge \tfrac{1}{2}
\end{cases}
```

Thermal walls use plain halfway bounce-back (the wall at q = ½ regardless of its true position). In a channel test the interpolated wall reads the width to within 5 % (5.37 against 5.40 cells); halfway bounce-back is 9.2 % off.

### Speed

| Measure | Value |
| --- | --- |
| Isothermal throughput, RTX 5070 Ti | about 1,300 to 3,400 MLUPS, depending on GPU load and grid; about 3,390 in a clean benchmark |
| With thermal on | about 1,300 MLUPS |
| Online age-of-air cost | +6 to 8 % per step, about 17 bytes per cell on the GPU |
| Live display refresh | up to about 10 per second, particles advected through the field |

MLUPS = million lattice-cell updates per second. The low end is a GPU shared with other work; the app's own start-up probe read 2,098 MLUPS on 29 October 2026 (on a 425 k-cell grid, which reads below the solver's peak). Steps per second follow directly:

```math
\text{steps/s} = \frac{\mathrm{MLUPS}\times 10^6}{N_{\mathrm{cells}}}
```

For the sample house at Standard (5.09 M cells), a clean run near 3,100 MLUPS gives 3,100 × 10⁶ ÷ 5.09 × 10⁶ ≈ 610 steps per second isothermal, before the online age; a loaded GPU at 1,300 MLUPS gives about 255.

## 3. The viscosity floor

For stability the solver never lets its lattice viscosity fall below 5 × 10⁻⁴ (lattice units). On building-scale grids that makes the simulated air about 150 to 850 times more viscous than real air, so jets through windows and supplies behave like laminar jets with an effective Reynolds number of a few hundred instead of about 10⁵. Treat absolute air changes and jet reach as indicative.

### Why the penalty grows with cell size

Lattice and physical viscosity are linked through the cell size and time step. The time step is set by keeping the lattice wind speed U\_lat (a lattice Mach number) fixed, so:

```math
\nu_{\mathrm{phys}} = \nu_{\mathrm{lat}}\,\frac{\Delta x^2}{\Delta t}, \qquad \Delta t = \frac{U_{\mathrm{lat}}\,\Delta x}{U} \quad\Longrightarrow\quad \nu_{\mathrm{phys}} = \nu_{\mathrm{lat}}\,\frac{U\,\Delta x}{U_{\mathrm{lat}}} \;\propto\; \Delta x
```

At a fixed floor, the simulated viscosity grows in proportion to the cell size. The records bear this out: Preview cells (254.5 mm) are 1.50 times Standard cells (169.6 mm), and the viscosity penalty is about 470 times against about 310 times, a ratio of 1.52. The validation cases, at much finer cells, run at only 4 to 28 times.

### What it does to jets

The Reynolds number of a jet through an opening N cells across depends on the cell count, not on the physical width:

```math
\mathrm{Re}_{\mathrm{eff}} = \frac{U\,L}{\nu_{\mathrm{phys}}} = N\,\frac{U_{\mathrm{lat}}}{\nu_{\mathrm{lat}}}, \qquad N = L/\Delta x
```

With openings 4 to 16 cells across this lands at a few hundred, where a real window jet sits near 10⁵. Laminar jets spread and decay differently from turbulent ones, which is the main reason jet reach and absolute ACH are indicative only. In the Annex 20 room the effective Reynolds number before the sub-grid term is about 1,280 against the specification's 5,000.

### Measured sensitivity

Lowering the floor from 5 × 10⁻⁴ to 1 × 10⁻⁴ changes results substantially, and not all in one direction.

| Case | Floor 5e-4 | Floor 1e-4 | Effect of lowering |
| --- | --- | --- | --- |
| Sample house, Living ACH | 13.4 | 9.4 | −30 % |
| Sample house, Kitchen ACH | — | — | −15 %; Kitchen window flow reverses |
| IEA Annex 20, hit rate q | 0.453 | 0.557 | Better on all four metrics |
| Cross-ventilation, hit rate q | 0.746 | 0.587 | Worse on q and NMSE |
| MINIBAT ceiling jet | — | — | Worse on all four metrics |

The decision rule's verdict (October 2026 re-run) was “WORSE: keep 5e-4”, because one validation case improved and two got worse.

## 4. Building geometry and openings

The model (metres; X and Y horizontal, Z up) is turned into a grid of air, solid, fixed and device cells, with an outdoor margin so the wind passes over the roof and around the façades. Openings are the most grid-sensitive part of the model: an opening's effective discharge coefficient depends on how many cells fit across it and where it falls on the grid.

### Voxelisation

| Check | Result |
| --- | --- |
| Room air volume against nominal, Preview (254.5 mm cells) | −6.3 to +2.1 % |
| Room air volume against nominal, Accurate (169.6 mm cells) | −5.1 to +3.7 % |
| 50 mm partition on a 255 mm grid | 0.0000 m³/s leak; 0 of 60 probes leaked |
| Interpolated wall position in a channel | within 5 % (5.37 against 5.40 cells) |

Room ACH divides by the nominal (drawn) volume; the report's Nominal column says which volume was used. Cells are classified by their centre point.

### The orifice model

An opening's flow is the normal velocity integrated over the throat cells the voxelizer actually carved:

```math
Q = \sum_{\text{throat cells}} \mathbf{u}\cdot\mathbf{n}\;\Delta x^2
```

The discharge coefficient compares that flow with the ideal flow through the same area under the same pressure drop (the orifice equation):

```math
Q = C_d\,A\,\sqrt{\frac{2\,\Delta p}{\rho}} \qquad\Longleftrightarrow\qquad C_d = \frac{Q}{A\sqrt{2\Delta p/\rho}}
```

A sharp-edged opening has C\_d ≈ 0.61 at high Reynolds number (Idelchik; ISO 5167; Etheridge & Sandberg 1996). The carved hole is rounded to whole cells and is 0 to 15 % larger than drawn at the checked grids.

### Calibration to C\_d = 0.61

On a coarse lattice the hole resolves its own C\_d, which depends on the cells across it and the wall thickness. The default **Calibrated** model adds a sub-grid loss in the throat cells wherever the lattice resolves *above* the requested value (0.61 by default). Losses in series add in the head, so the equivalent loss coefficient is:

```math
\frac{1}{C_{d,\mathrm{target}}^{2}} = \frac{1}{C_{d,\mathrm{lat}}^{2}} + K \quad\Longrightarrow\quad K = \frac{1}{0.61^2} - \frac{1}{C_{d,\mathrm{lat}}^{2}} \;\;(K \ge 0)
```

The solver applies this K as a quadratic drag on the through-flow component of the air velocity only, in the one-cell-thick throat layer: a force of −c ρ |u\_n| u\_n along the opening's normal, with c = K ÷ 2 per lattice cell; cross-flow and vertical momentum are left alone. C\_d,lat is read from a table of the lattice-resolved values (mean over placements) indexed by the cells across the opening, taken as its hydraulic diameter 2WH ÷ (W + H) over Δx, and by the wall thickness. Where the lattice resolves *below* 0.61, K would be negative, so nothing is added and the flow is short by the ratio C\_d,lat ÷ 0.61: for 4 cells across, 0.48 to 0.60 ÷ 0.61 gives 1 to 21 % low.

At 8 and 16 cells across, calibration lands within ±1.3 % of 0.61. At 4 cells the lattice already resolves below the target, so the opening passes less air than drawn and a note gives the shortfall.

### Placement scatter

The same window moved by a fraction of a cell changes the C\_d the lattice resolves by up to the figures below (scatter over eight placements). The app warns when the scatter exceeds 5 %.

| Cells across ↓ / wall thickness → | 0.5 cell | 1 cell | 2 cells | 3 cells | 4 cells |
| --- | --- | --- | --- | --- | --- |
| 4 | ±30.1 % | ±10.2 % | ±7.1 % | ±6.5 % | ±7.0 % |
| 8 | ±21.1 % | ±7.7 % | ±3.2 % | ±2.6 % | ±3.0 % |
| 16 | ±14.3 % | ±5.1 % | ±1.6 % | ±2.1 % | ±2.3 % |

Two placements lie outside this table: a hole aligned to the grid (edge on a cell face) and one with its edge on a cell centre. An aligned hole 8 cells across in a one-cell wall reads C\_d 0.716 against the requested 0.61 (+17 %). Openings much wider than 16 cells, rounded or thick-reveal openings, and rotated (non-axis-aligned) openings are not calibrated separately.

### Opening warnings

| Condition | Warning | Meaning |
| --- | --- | --- |
| Under 4 cells across | “is only N cells across” | Flow unreliable |
| 4 to 6 cells across, Preview | size quantised to the grid | ±1 cell row is 15 to 25 % of the area |
| Effective C\_d outside 0.42 to 0.8, at least 4 cells wide | C\_d warning | Check the opening |
| Placement scatter above 5 % | scatter note | Result sensitive to grid position |

### Imports, rooms and edits

- **Room volume.** A room is the air inside its outline, filled up to the storey top. A double-height room needs a storey height covering its full height; a void opening into the storey above counts in the lower room.
- **Room band.** Detected rooms get a 2-cell band from core to walls. Imported rooms read total exchange 37 to 45 % higher than the same building drawn by hand. Compare imported models with each other, not with drawn ones.
- **Too few cells.** A room under 3 cells across is marked “unreliable (too few cells)” with a dagger.
- **Exchange instead of ACH.** When counted inflow through openings is under 5 % of what crosses the room outline, the row shows an exchange in m³/s with a grey dot and is left out of Best/Least ventilated.
- **Glass removal is per material.** A glass material shared by a curtain wall and windows is removed as a whole.
- **Model checks.** Coordinates within ±1 × 10⁵ m, sizes in (0, 1000\] m; a broken file is refused with a reason. A model more than 10 km from the origin is centred in plan with a note.
- **Editing.** Toggling a window is a warm restart, not a rebuild. Deleting an opening in an imported mesh seals it with a fill solid; adding a room, merging rooms or adding an opening re-voxelises and restarts the live solve.
- **Naming.** Detected rooms with no name of their own are called “Room 1”, “Room 2” and so on, in display order (storey, then position on plan); detected openings are “Window 1”, “Door 1” and so on. A direction and floor-area suffix, for example “Kitchen (east, 18 m²)”, is added only when two rooms share a real name.
- **Revit OBJ.** An OBJ exported from Revit (first line “generated by Autodesk ATF”) is recognised and read as feet and Z-up automatically.
- **Large model files.** Before reading a model file, the app compares four times the file size with the free memory and asks for confirmation when the file looks too big for it.

## 5. Wind and the outdoor domain

The default Compact domain is fast but tight: on the sample house it blocks 10.5 % of the wind's cross-section, and room total exchange reads about 11 to 14 % higher than in the Full (COST 732) domain in the newest measurement (after the age-of-air fix; an older comparison at the Preview cell size read 8 to 33 %, see below). Use Compact to compare options and Full for a final check.

### The boundary ring

| Boundary | Treatment |
| --- | --- |
| Inflow (upwind) | Non-equilibrium velocity inflow, the default, with an absorbing sponge |
| Outflow (downwind) | Constant density, with an absorbing sponge |
| Top | Prescribed-velocity lid |
| Ground | Smooth, no-slip |
| Open-top user boxes | Older equilibrium ring (a velocity inflow through a cut wall diverged in the checks) |

The report states the wind at 10 m height.

Both domains carry an absorbing sponge at inflow and outflow that stops reflected waves; the Full domain costs about 11.7 times the cells of Compact.

### Blockage and the two domains

Blockage is the share of the domain's cross-section taken up by the building's frontal area:

```math
B = \frac{A_{\mathrm{frontal}}}{A_{\text{domain cross-section}}} \qquad (\text{COST 732 asks } B \le 3\,\%)
```

| Domain | Margins | Blockage, sample house | Cells |
| --- | --- | --- | --- |
| Compact (default) | small | 10.5 % | 1 × |
| Full (Expert → Settings → Domain) | 5H upstream, 10H downstream plus sponge, 5H lateral | ≤ 3 % (COST 732 domain ≈ 2.4 %) | about 11.7 × |

**What Compact costs**, measured against Full on the sample house:

| Comparison | Older run, Preview cell size, before the age-of-air fix | Newest run, after the fix |
| --- | --- | --- |
| Room total exchange, Compact over Full | +8 to +33 % (median about +16 %) | +11.0 to +13.9 % per room |
| Building fresh-air rate | 12.1 against 9.7 ACH (+25 %) | +22 % |

The newest column is the one to quote. The app's own blockage note still quotes the older 8 to 33 %, which is wider and therefore safe. The app adds a note whenever blockage exceeds 3 %.

### Wind profile and ground

Eurocode EN 1991-1-4 gives the mean wind as a logarithmic profile over terrain roughness z₀ (standard form):

```math
v_m(z) = c_r(z)\,c_o(z)\,v_b, \qquad c_r(z) = k_r \ln\!\left(\frac{z}{z_0}\right), \qquad I_v(z) = \frac{\sigma_v}{v_m(z)} = \frac{k_I}{c_o(z)\ln(z/z_0)}
```

WindFlow's ground is smooth and no-slip, so wind near the ground is too weak: about 54 % under the EN profile at 1 m. For oblique winds the profile drifts 10.8 to 15.9 % at 2 m.

### Slow settling of the inflow

The inflow settles slowly (about 40,000 steps) and its steady state depends on how the run started. In a 10-minute run results land about 1.5 to 3 % high, and two starts settle 0.5 to 4 % apart (0.47 % at 60 thousand cells, 4.3 % at 400 thousand). Two of the solver's own tests fail for this reason (chapter 10).

A simpler equilibrium inflow was tried as the default and reverted in October 2026. It failed its cold-versus-warm-start bound (CPU 30 batches 1.4 % / 2.7 %; GPU 120 batches 1.03 % / 3.3 %), and the 40-cell cross-ventilation case failed with it (q 0.619 against a bound of 0.66, against 0.746 with the default). Every published validation number uses the default inflow.

### Gusts (High quality)

Standard and Preview use a steady profile. High adds gusty approach turbulence after EN 1991-1-4, generated as a synthetic field with Smirnov decorrelation so gusts evolve in time, plus turbulent supply jets. The generated gust intensity reads 0.94 to 0.97 of the EN target.

- Each room's ACH then moves from batch to batch, so High's ranges are wide and the run usually ends at its time budget.
- Standard under-reads rooms ventilated mainly by gusts (one opening, or openings only on walls parallel to the wind); the app flags such rooms.
- Never mix a Standard number with a High one.

### Optional wall model

A Werner–Wengle wall function sits under Expert → Settings. It is off by default, wind-only, and greyed out while Thermal is on. Results were mixed and grid-dependent (Annex 20 passes at 4 cells across the slot, fails at 8), and its laminar-flow test fails (2.57 % against 2 %), so it stays experimental.

## 6. Age of air and air changes per hour

A room's headline number, fresh-air ACH, is 3600 divided by the mean age of the air in that room. Because it is built from age, air that reaches a room second-hand through a neighbour counts at its real age, so a room fed by another room reads low even when plenty of air passes through it.

### Definitions

The local age of air a(x) is the time since the air at x was last outdoors. Outdoor air has age 0. The room mean age and the fresh-air rate are:

```math
\langle a \rangle = \frac{1}{V}\int_{V} a\,\mathrm{d}V, \qquad \mathrm{ACH}_{\mathrm{fresh}} = \frac{3600}{\langle a \rangle\,[\mathrm{s}]}\;\;[\mathrm{h}^{-1}]
```

Fresh-air ACH does not use the room volume. It equals a true air-change rate only for fully mixed air, where the mean age equals the nominal time constant:

```math
\text{fully mixed:}\quad \langle a \rangle = \tau_n = \frac{V}{Q} \quad\Longrightarrow\quad \mathrm{ACH}_{\mathrm{fresh}} = \frac{3600\,Q}{V}
```

Otherwise it is the room's *mean-age equivalent* (Etheridge & Sandberg 1996; Hurraß 2026). Total exchange counts every inflow, whatever its origin:

```math
\mathrm{ACH}_{\mathrm{total}} = \frac{3600\;Q_{\mathrm{in,\,all\;faces}}}{V_{\mathrm{nominal}}}
```

Air-change efficiency (ISO 16000-8) compares the exhaust age with the room mean; it is reported only for a room fed at least 90 % straight from outside:

```math
\varepsilon_a = \frac{a_{\mathrm{exhaust}}}{2\,\langle a \rangle} \qquad 0.5 = \text{fully mixed}, \quad 1.0 = \text{piston flow}
```

A bedroom ventilated through a hall can therefore show high total exchange and a modest fresh-air ACH at once, because the air it receives from the hall is already old; read the two columns together.

### Display states

| Shown | Meaning |
| --- | --- |
| — | Age not solved, or the room outline is drawn through open air |
| sealed | No air path reaches the room; its age sits at the solver's 24-hour cap |
| (steady) | This room's online age had not settled; the steady age is shown, with no ± |
| (drifting) | The averaged batches had not levelled off |

Outdoor air counts as age 0, so exhaust that leaves a room and is drawn back in counts as fresh. The region where age and CO2 are solved includes a 1 m outdoor ring. On the sample house the online age agrees with a tracer within 9 % in every room; strong re-entry at a façade has not been tested.

### Two ways to get the age

**Steady age** solves Sandberg's steady mean-age equation on the time-averaged flow ū. It is cheap but carries no resolved turbulent mixing:

```math
\nabla\cdot(\bar{\mathbf{u}}\,a) = \nabla\cdot(D\,\nabla a) + 1, \qquad a = 0 \text{ in outdoor air}
```

**Online age** is carried by the solver every step, through the instantaneous turbulent flow, and then time-averaged:

```math
\frac{\partial a}{\partial t} + \nabla\cdot(\mathbf{u}\,a) = \nabla\cdot(D\,\nabla a) + 1
```

The “+1” source makes age grow by one second per second wherever the air is. The steady form is biased: it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones, by about 30 % either way per room. Online age costs 6 to 8 % more per step and about 17 bytes per cell on the GPU. The exhaust-age check against V ÷ Q reads 106.2 s against 100.8 s (5.3 % error, band ≤ 10 %).

| Run | Headline fresh-air ACH | Shown beside it |
| --- | --- | --- |
| Preview | Steady age on the mean flow | Nothing; muted, marked “\~” |
| Standard (fine grid, steady wind) | Online age where it settled; steady age, marked “steady age”, where it did not | No ± for a steady-age room |
| High (fine grid, gusty wind) | Online age through the gusts | “Fresh-air (steady age)”, which ignores gust mixing and usually reads lower |

### Why High uses the online age

In gusty wind the steady age misses most of the mixing. On the sample house, one High run printed both ages on the same mean flow. The building fresh-air rate was 27.0 ACH from the online age against 13.3 from the steady age; before this change High read below Standard (13.7 against 18.3 ACH). No tracer-proven reference exists for a gusty case.

The steady age can err in both directions. In that run, steady over online age was ×2.04 in Living, ×1.56 in Kitchen, ×1.23 in Hall and ×3.07 in Bedroom 2 (the steady age reads the air older), and ×0.91 in Bedroom 1 and ×0.84 in Landing (younger). In High the steady value read about 50 % low overall (Living −51 %, Bedroom 2 −67 % in fresh-air ACH), which is why High carries the online age.

## 7. How a run decides it is done

An Accurate run averages each room's ACH over batches and stops only when a strict 99 % test says the mean has settled. With the online age that test usually needs 160 to 310 batches, so the default 10-minute Standard run normally ends at its time budget and shows ranges, not settled values.

The range shown on screen is the 95 % interval; the stop test uses the wider 99 % width, so a room can show a tight range and still not read Settled.

### The rule, step by step

1. **Batches.** Each batch lasts at least one flow-through of the slowest room. No run stops before 16 batches.
2. **Cut the start-up.** Of n batches so far, drop the first d, where d is the largest power of two not above n/2. Keep m = n − d.
3. **Mean and interval.** Each room's ACH is the mean of the kept batches, with a Student-t interval widened for correlation between neighbouring batches.
4. **Stop test.** The 99 % half-width is under the aim (5 % of the room's ACH, with a floor of 0.2 ACH), at least 5 batches are kept, and the first and second halves of the kept batches agree. Only then does the room read *Settled*.

In symbols:

```math
d = 2^{\lfloor \log_2 (n/2) \rfloor}, \qquad m = n - d, \qquad \bar{x} = \frac{1}{m}\sum_{k=d+1}^{n} x_k
```

```math
h_{p} = t_{p,\,m-1}\;\frac{s}{\sqrt{m}}\;\kappa, \qquad \text{report } \bar{x} \pm h_{0.975}, \qquad \text{stop when } h_{0.995} < \max(0.05\,\bar{x},\; \text{floor}),\;\; m \ge 5,\;\; \text{halves agree}
```

For example, at n = 16 the first 8 batches are dropped; at n = 40, the first 16. The interval is widened for batch-to-batch correlation by the factor κ below, where ρ is the lag-1 correlation of the room's batch means; the Student-t degrees of freedom come from the effective number of independent batches it implies:

```math
\kappa = \sqrt{\frac{1 + \rho}{1 - \rho}}, \qquad m_{\mathrm{eff}} = m\,\frac{1 - \rho}{1 + \rho}
```

The solver estimates ρ from the batch means with a small-sample bias correction (Marriott & Pope), clamps it to between 0 and 0.9, and sets it to 0 below 8 batches. The records give ρ = 0.85 to 0.97 for the online age's batch correlation and report that 160 to 310 batches (27 to 53 minutes at 5 M cells) are needed. With the clamp at 0.9, each independent batch costs at most about 19 real batches by this form (about 12 at ρ = 0.85), so the form alone does not reproduce the 160 to 310; the records do not say what else sets that number.

### What the rule guarantees, and what it does not

- Checked on simulated correlated batch means: at ρ = 0.7 the 95 % intervals covered the truth 96 % of the time.
- Not shown to be right for every building.
- *Settled* cannot exclude a drift of up to about 1.5 times the aim; a room whose batches still drift is marked “(drifting)”.
- The ± is statistical only. Grid, inflow and model error are not in it, and it excludes slow age drift.

### Outcomes on screen

| Shown | When |
| --- | --- |
| Settled | Stop test met for every room |
| “Time limit reached: ranges shown” | Time budget ran out first |
| “± —” | Fewer than 16 batches in all, or fewer than 5 kept |
| (drifting) | A room's kept batches had not levelled off |
| (steady) | A room's online age never settled; steady age shown instead |

### Why a run takes 30 to 50 minutes

The default Standard run stops after 600 s of stepping.

| Test | Result |
| --- | --- |
| 7 Standard runs, 10 min | Settled in none; only Bedroom 1 and the sealed bathroom settled |
| One 60-minute run (328 batches) | Kitchen, Bedroom 2 and Landing still not settled |
| 10-minute numbers against the 60-minute run | Within about 3 % for 5 of 6 rooms (Bedroom 2 +14 %) |
| 20-minute Standard run | Online age landed in Bedroom 1 only |

The online age cannot settle faster than about one room fresh-air age; Bedroom 2 was still rising after 2,000 s of physical time. This is not a defect and the rule is unchanged. In practice a short run mostly reports the steady age, marked “steady age”. For a final check use High or a longer time limit.

## 8. Quality levels and run times

Preview is for direction and a first look, Standard is the default comparison run, and High (gusty wind) is the final check on a real site. Never compare numbers across levels. A fourth level, Ultra, is described at the end of this chapter.

| Level | Grid | Wind | Time | Use it for |
| --- | --- | --- | --- | --- |
| Preview | about 1.5 M cells on the high tier (1.54 M at 254.5 mm on the sample house) | Steady | settles in about 3 to 30 s | Direction and first look; room numbers muted, marked “\~” and “preview” |
| Standard (default Accurate run) | Finer whole-building grid; budget per tier: high 5 M, medium 3 M, low 1.2 M, CPU 0.6 M cells (5.09 M at about 170 mm on the sample house) | Steady, no added turbulence | up to 600 s of stepping | “What does a perfectly steady wind of this speed and direction do” |
| High | Fine grid, Compact domain, optional fine box | Gusty (EN 1991-1-4), turbulent supply jets | Budget you pick: 5, 10, 20 or 30 min | A real site; rooms ventilated mainly by gusts; final check |

### How the levels chain

- **Preview, then Accurate.** Once Preview settles, the Accurate run starts on its own at the finer grid, from rest. It settles for 4 times the planned warm-up (2 times for a cold start, 1 for a thermal run), then averages in batches until every room's range closes or the budget runs out.
- **Cell budgets.** The tier is chosen from a short start-up solver run and the graphics card's memory, clamped to what that memory holds next to the Preview grid.
- **Which grid to trust.** Compare options on the same grid and quality level; run Full domain and High for a final check.

### Timings on the sample house (RTX 5070 Ti)

| Run | Timing |
| --- | --- |
| Standard, 5.09 M cells | settles for about 12 s, then batches of about 10 s |
| Accurate at Preview cell size, 1.54 M cells, 10 min cap | converged after 256 batches in 509 s |

These timings predate some later fixes.

### The fine box

An optional 2:1 finer grid around one room sharpens the local view only: particles, slices and probes. Room ACH and opening flows always come from the whole-building run, because a two-way coupled box was built and did not beat it. Against a uniform run at the box's cell size, one hall opening read 14.7 % off with the box and 11.1 % off whole-building; the Hall's total ACH read 12.7 % off against 3.3 %. The box is isothermal only.

### Ultra (new, October 2026; not separately validated)

Ultra is an isothermal level that spends hours on a much finer uniform grid.

- **Grid.** The aim is 16 cells across the narrowest open opening, so Δx = the narrowest opening ÷ 16, with a floor of 25 mm, limited by GPU memory and never coarser than Standard.
- **Domain.** The full outdoor domain if it fits at a finer cell size than Standard, otherwise Compact, with a note.
- **Run length.** The run cap is 6, 12, 20 or 24 h. The run stops early when every room's online age has settled.
- **Robustness.** Ultra keeps the PC awake. It saves progress about every 20 minutes and can resume after a crash or a close. On a GPU fault it retries once from the last save; there is no CPU fallback.
- **Time estimate.** The estimate uses the GPU speed measured on the user's own computer.
- **Sample house on a 16 GB GPU.** About 83 mm cells (10 cells across the narrowest window) and about 43 M cells, against Standard's about 170 mm.
- **Cost.** Cells grow as Δx⁻³ and steps as Δx⁻¹, so the cost of a run scales roughly as Δx⁻⁴.
- **Opening coefficient.** At 16 cells across, calibration lands within ±1.3 % of 0.61 and the placement scatter is ±1.6 to ±2.3 % for walls 2 cells thick or more (chapter 4), so little further change in C\_d is expected from finer cells, about 1 to 3 %. This has not been measured above 16 cells.

No Ultra accuracy result has been measured. Do not read the validation tables of this document as results for Ultra.

## 9. Thermal (beta), heat sources and devices

Thermal is a hybrid: the 3-D lattice flow plus a finite-volume energy equation that uses the flow solver's own mass fluxes. It passes its textbook cases (heated cavity within 1 %), but small heat sources are under-stratified by about 25 to 40 %, and it has never been checked against a measured room, hence *beta*.

### Equations

Energy (finite volume, van Leer–limited advection, face fluxes from the flow solver):

```math
\frac{\partial T}{\partial t} + \nabla\cdot(\mathbf{u}\,T) = \nabla\cdot\big((\alpha + \alpha_t)\,\nabla T\big) + \frac{\dot{q}}{\rho\,c_p}
```

Buoyancy (Boussinesq), added to the flow as a body force:

```math
\mathbf{F} = -\rho_0\,\beta\,(T - T_{\mathrm{ref}})\,\mathbf{g}
```

A uniform temperature stays uniform, and a sealed room conserves its energy to 0.004 to 0.07 %. Body forces enter the lattice through Guo forcing, which also drives every device. WindFlow changes one weight for its regularized collision: the term that carries momentum is weighted ½ instead of (1 − 1/2τ), so that each step adds exactly F Δt of momentum, and the second-moment term keeps the (1 − 1/2τ) weight:

```math
F_i = w_i \left[\frac{1}{2}\,\frac{\mathbf{c}_i\cdot\mathbf{F}}{c_s^2} + \left(1 - \frac{1}{2\tau}\right)\left(\frac{(\mathbf{c}_i\cdot\mathbf{u})(\mathbf{c}_i\cdot\mathbf{F})}{c_s^4} - \frac{\mathbf{u}\cdot\mathbf{F}}{c_s^2}\right)\right]\Delta t, \qquad \rho\,\mathbf{u} = \sum_i f_i\,\mathbf{c}_i + \tfrac{1}{2}\mathbf{F}\,\Delta t
```

### Held temperature and walls

- **Ideal heater or cooler (default on).** Each room's volume-mean temperature is held at the indoor setting. The report's “Duty W” shows + for heating, − for cooling, “—” for a free-running room. A stratified room's occupied zone can differ (a note appears above 1 K); a room with its own split AC is not held.
- **Free-running.** With the hold off, the typed indoor temperature is only the starting air temperature, and the stack effect decays during the run.
- **Adiabatic walls.** Only *Surface* heat sources (a panel, or a cold window drawn as one) exchange heat.

### Surface convection (Alamdari–Hammond)

Surface sources use the Alamdari–Hammond natural-convection correlations, blended with a forced-convection term. The natural part h\_n is below for a vertical surface of height H and for a buoyant horizontal surface (hot floor or cold ceiling) of hydraulic diameter D = 4A ÷ P of its plan; a stable horizontal surface (hot ceiling or cold floor) uses h\_n = 0.6 (|ΔT| ÷ D²)^(1/5). The blend adds a velocity term h\_f = 3.8 |u\_t|, in W/m²K per m/s, from the air speed u\_t along the face in the first fluid cell (a McAdams-type form):

```math
\begin{aligned}
h_{\mathrm{vert}} &= \left[\left(1.5\left(\frac{|\Delta T|}{H}\right)^{1/4}\right)^{6} + \left(1.23\,|\Delta T|^{1/3}\right)^{6}\right]^{1/6}, \qquad h_{\mathrm{up}} = \left[\left(1.4\left(\frac{|\Delta T|}{D}\right)^{1/4}\right)^{6} + \left(1.63\,|\Delta T|^{1/3}\right)^{6}\right]^{1/6} \\
h &= \left(h_n^{3} + h_f^{3}\right)^{1/3}, \qquad h_f = 3.8\,|u_t|
\end{aligned}
```

These reproduce the records' hand-check values: a 20 K vertical plate with H = 3 m gives 3.413 W/m²K, and a 20 K hot floor with D = 2 m gives 4.448 W/m²K.

### Small heat sources and the plume model

The sealed filling-box test (a 500 W area heater in a sealed 3.0 m room) reads a vertical temperature difference of 1.30 K against the Baines & Turner / Worster & Huppert reference of 1.8 to 8.6 K, and stays a FAIL. The heater delivers 99.3 % of its energy and nothing leaks: the plume is under-resolved and not grid-converged.

An opt-in plume model (experimental, October 2026, off by default) releases a small heater's power as a rising Morton–Taylor–Turner plume one cell layer above the heater whenever the plume is too small for the grid. Its two inputs are the buoyancy flux of the heater and the entrainment hypothesis, with α = 0.12 taken from the literature, not fitted:

```math
B = \frac{g\,\beta\,P}{\rho\,c_p}, \qquad u_e = \alpha\,w, \qquad b(z) = \tfrac{6}{5}\,\alpha\,z, \qquad w(z) \propto B^{1/3} z^{-1/3}
```

On the filling-box test at cell sizes of 0.268, 0.198, 0.134 and 0.100 m, the default reads 1.35, 1.35, 1.55 and 1.76 K: it creeps up as cells shrink but stays under the 1.8 K floor of the band at every size. The plume model reads 3.65, 4.55, 5.96 and 7.07 K, inside the band throughout and with energy within 1 %, but it roughly doubles across the range, so it is not grid-independent either. It does not cover wall-mounted or Surface sources, wind does not bend it, and it has run on the CPU only.

A separate energy-balance check heats a sealed room and compares the mean temperature rise with the closed-form value; it reads 5.4 % error (band ≤ 20 %):

```math
\Delta \bar{T} = \frac{P\,t}{\rho\,c_p\,V}
```

### Devices

Ceiling fans, pedestal fans, split and cassette AC, exhaust fans, hoods and diffusers are body-force regions (Guo) that accelerate air already in the cell, so they conserve mass by construction. A diffuser moves room air and adds no fresh air; in a sealed room it brings in no outdoor air.

| Device behaviour | Measured |
| --- | --- |
| Ceiling fan disc against rated 1.200 m³/s | −3.5 % |
| Split AC delivered against set flow | about 3 % low (0.145 against 0.15 m³/s) |
| Supply patches against set flow | 0.5 to 1 % low |
| Split AC duty against room enthalpy loss | 0.2 % |
| Split AC steady return temperature | +0.30 K (at the ±0.3 K band edge) |

- **Resolution chip.** A fan disc, or an outlet's short side, under 8 cells across reads “refine needed”. A split AC's 120 mm slot needs 15 mm cells and reads “too small to resolve”.
- **Sealed rooms with unbalanced devices** have no pressure anchor, so the app scales the extract to the supply and adds a room note.
- **Input caps.** Flow ≤ 5 m³/s, radius ≤ 1.5 m, supply temperature clamped to 5 to 40 °C.

## 10. Validation

The latest whole run of the Sandbox test suite reads 258 PASS and 3 FAIL (the filling-box test and the two start-up drift tests), after an earlier 253 PASS and 3 FAIL. The app's own self-check, a separate suite, reads 189 PASS and 0 FAIL on the current build. Of the measured-room cases, only cross-ventilation at 40 cells per building height passes all four metrics; Annex 20 and MINIBAT each miss on the hit rate, and absolute cross-ventilation flow rates read 1.6 to 2.2 times the published tracer values while ranking the layouts correctly.

### How measured cases are scored

Four metrics after Chang & Hanna (2004), with the hit rate after VDI 3783-9 and COST 732. O is the measurement, P the prediction, overbars are means over the N points:

```math
\mathrm{FAC2} = \frac{1}{N}\sum_{j=1}^{N} n_j, \quad n_j = \begin{cases} 1 & 0.5 \le P_j/O_j \le 2 \\ 0 & \text{otherwise} \end{cases}
```

```math
\mathrm{FB} = \frac{\bar{O} - \bar{P}}{\tfrac{1}{2}(\bar{O} + \bar{P})}, \qquad \mathrm{NMSE} = \frac{\overline{(O - P)^2}}{\bar{O}\,\bar{P}}
```

```math
q = \frac{1}{N}\sum_{j=1}^{N} n_j, \quad n_j = \begin{cases} 1 & \left|\dfrac{P_j - O_j}{O_j}\right| \le D \;\text{ or }\; |P_j - O_j| \le W \\ 0 & \text{otherwise} \end{cases}, \qquad D = 0.25
```

Pass band: FAC2 ≥ 0.5, |FB| ≤ 0.3, NMSE ≤ 1.5, q ≥ 0.66. With this sign convention a positive FB means WindFlow reads low on average. W is an absolute tolerance chosen per case, not published. FAC2 here counts pairs where both values are below W as hits (Hanna & Chang 2012); by the strict definition (van Hooff, Blocken & Tominaga 2017) FAC2 is 0.942 (MINIBAT), 0.619 (cross-ventilation, 40 cells) and 0.538 (Annex 20).

MINIBAT misses only on hit rate, by 0.017; Annex 20 misses hit rate and, narrowly, bias; the 20-cell cross-ventilation run fails because it is under-resolved.

### Measured rooms and wind tunnels, case by case

- **IEA Annex 20, 2-D isothermal room** (Nielsen 1990; LDA data of Restivo 1979; 8 cells across the slot, 106 points). Fails q (0.453) and FB (0.308, just past 0.3); FAC2 0.613 and NMSE 0.356 pass. The ceiling jet decays about 25 % too fast at every grid tried; this is the open gap. The room is scaled (slot 4 cells of 0.1 m, H = 7.1 m against 3.0 m) to keep the Reynolds number similar, and the inlet follows the specification's sine profile with 4 % turbulence. An earlier row (q 0.471) used a different set-up, so the change is not a like-for-like regression.
- **Cross-ventilation, isolated building** (Tominaga & Blocken 2015 wind tunnel; 40 cells per building height, 63 points). Passes all four (FAC2 0.778, FB 0.119, NMSE 0.216, q 0.746), and the opening flux Q/(U\_H A) = 0.498 sits inside its 0.36 to 0.52 band. At 20 cells per height it fails (q 0.286; every earlier 20-cell run read 0.24 to 0.33): known under-resolution. The pass is for that one case at that grid. q is sensitive to W: 0.365 at W = 0, 0.540 at 0.025, 0.667 at 0.05 on an older run.
- **MINIBAT isothermal ceiling jet** (Kuznik, Rusaouen & Brau 2008; data Gresse, Merlier & Kuznik 2020; 3.1 × 3.1 × 2.5 m room, 25 mm cells, 1,419 points). FAC2 0.942, FB 0.051, NMSE 0.083 pass; q 0.643 is 0.017 short. Going from 6 to 8 cells across the nozzle leaves q flat (0.654 to 0.646) while the others improve, which suggests but does not prove a remaining model gap.

### Tracer cross-ventilation, five layouts

The flux ratio normalises the flow through the building by the wind at building height and the opening area:

```math
\hat{Q} = \frac{Q}{U_H\,A}
```

Tominaga & Blocken 2016 layouts A to E were run at 40 cells per height on a 24.8 M-cell domain (4H margins, 3.0 % blockage; 230 minutes on the GPU). WindFlow's tracer flow equals its own velocity-integrated flux to about 1 %, and the set-up and normalisation match the paper.

WindFlow's tracer flux ratios for layouts A to E are 0.588, 0.529, 0.496, 0.447 and 0.522, against the paper's tracer values of 0.37, 0.27, 0.23, 0.22 and 0.25 (1.59 to 2.16 times). All five fail the ±20 % band; the ranking is right, with A first (that criterion passes). The reference itself reads low: for layout E the paper's tracer value is 37 % below its PIV opening flux (about 0.40), and an LES gives 0.44 to 0.47; against those, E reads 1.11 to 1.33 times high. Layouts A to D have no PIV or LES value. About +6 % of the gap is the 4H domain; an earlier breakdown put 91 % of the error (in log units) on the opening's discharge coefficient and 7 % on the domain. The band was not changed. The primary sources disagree on the flow rate itself: 0.25 (tracer), 0.50 (van Hooff et al. 2017), and an opening-flux band of 0.40 to 0.47 (Karava PIV; Hwang & Gorlé LES).

### Analytic and textbook references

| Case | Reference | WindFlow | Band | Status |
| --- | --- | --- | --- | --- |
| Two windows in series, effective C\_d | orifice equation; sharp-edged 0.6 to 0.65 | 0.559 | 0.37 to 0.80 | PASS |
| Per-opening C\_d, cross-ventilation scene | same | west 0.585, east 0.564 | 0.42 to 0.8 | PASS |
| Isolated sharp square orifice, 8 cells, aligned | C\_d ≈ 0.61 (Idelchik; ISO 5167) | 0.716 | 0.65 to 0.72 (moved: aligned hole) | PASS |
| Wall-built opening, 4 / 8 / 16 cells, resolved C\_d | same | 0.562 / 0.627 / 0.651 | information | NOTE |
| Wall-built opening, 8 cells, calibrated, mean of placements | requested 0.61 | 0.6093 | within 3 % | PASS |
| One-offset rig, calibrated, 4 / 8 / 16 cells | requested 0.61 | 0.739 / 0.716 / 0.670 | within 3 % | none within 3 % |
| Two-storey house, windward-to-leeward Δp | self-consistent pressure-driven flow | 1.018 | 0.5 to 1.6 × | PASS |
| Stack flow | orifice, Reynolds-corrected (C\_d 0.421 at Re 149) | ratio 1.213, head error 7.9 % | 0.6 to 1.4 × | PASS |
| Exhaust age against V ÷ Q | Sandberg 1981 | 106.2 s against 100.8 s (5.3 %) | ≤ 10 % | PASS |
| Two-room age ordering | ASHRAE 129 | downstream above upstream | qualitative | PASS |
| CO2 cap and rise | mass balance; ASHRAE 129 | within band | 0.4 to 2.5 × | PASS |
| CO2 with no source stays uniform | no decay | 419.9997 to 420.0002 ppm | 419.5 to 420.5 | PASS |
| Open-air room loop | internal exchange is not fresh air | exchange 2.498 m³/s, inflow 0.0000 | qualitative | PASS |
| Channel, interpolated wall | Bouzidi et al. 2001 | 5.37 against 5.40 cells | ≤ 5 % | PASS |
| Channel, halfway bounce-back | same | 9.2 % off | documents the gap | PASS (contrast) |
| 50 mm partition, 255 mm grid | sub-cell wall seals | 0.0000 m³/s | leak = 0 | PASS |
| Box face inside a partition | must solve | solves | no divergence | PASS |
| Laminar channel, wall model on vs off | no change expected | 2.5718 % | ≤ 2 % | FAIL |
| Heated cavity, Ra 1e4, 48 cells | Fusegi et al. 1991, Nu 2.05 | 2.059 (0.4 %) | within 20 % | PASS |
| Heated cavity, Ra 1e5, 48 cells | Fusegi et al. 1991, Nu 4.34 | 4.382 (1.0 %) | within 20 % | PASS |
| Heated cavity, 160 cells, GPU | Tric et al. 2000, Nu 2.054 / 4.337 | 2.056 / 4.358 (0.1 / 0.5 %) | within 5 % | PASS |
| Sealed filling box, 3.0 m room | Baines & Turner 1969; Worster & Huppert 1983 | 1.30 K | 1.8 to 8.6 K | FAIL |
| Sealed-room heating energy balance | P t ÷ (ρ c\_p V) | 5.4 % | ≤ 20 % | PASS |

The Nusselt number is heat transfer relative to pure conduction; a conduction-limit geometry check reads Nu 1.00. At an older 48-cell grid the two Rayleigh cases read 8 to 10 % low, before a test-geometry fix.

### Self-consistency, devices, nested box and starts

| Case | WindFlow | Band | Status |
| --- | --- | --- | --- |
| Cross-ventilation, CPU against GPU | 0.0 % | ≤ 3 % flow, ≤ 5 % ACH | PASS |
| Orifice rig: CPU/GPU parity, mirror and offset symmetry | 0.6528 both; 0 mismatches; within band | ≤ 1 %; = 0; ≤ 0.5 % | PASS |
| Thermal, 3 kW room in wind, GPU against CPU | max ΔT 5.9e-5 K; energy 0.0000 to 0.0002 % | ≤ 1e-3 K; ≤ 0.01 % | PASS |
| Heated cavity Nusselt, CPU against GPU | 0.0 % | ≤ 10 % | PASS |
| Ceiling fan disc against rated 1.200 m³/s | −3.5 % | ±25 % | PASS |
| Ceiling fan: sealed-room mass; CPU against GPU | residual 3.65e-5; equal | ≤ 1e-3; ≤ 1 % | PASS |
| Split AC duty against its own flow and ΔT | 0.0 % | ≤ 15 % | PASS |
| Split AC duty against room enthalpy loss | 0.2 % | ≤ 20 % | PASS |
| Split AC thermal, CPU against GPU | 25.372 / 25.372 °C; 2360 / 2360 W | 0.05 K; 2 % | PASS |
| Split AC steady return temperature | +0.30 K (17.62 against 17.32 °C) | ±0.3 K | PASS (edge) |
| Exhaust fan counted once in the mass ledger | residual 1.2 % | ≤ 10 % | PASS |
| Nested box = whole domain at half the cell size | 2.6 % | ≤ 10 % | PASS |
| Nested box round living room and landing | 15 % / 10 % (whole-building alone 11.1 / 17.5 %) | ≤ 25 % | PASS |
| Nested box: CPU against GPU; mass ledger | 0.00 %; 0.2 / 4.4 % | ≤ 1 %; ≤ 10 % | PASS |
| Supply inlet at 1 / 2 / 4 / 6 cells | 0.995 / 0.993 / 0.999 / 0.997 | ±2 % | PASS |
| Two-cell corner supply patch | 0.991, scale 1.200 | ±2 %; 1.2 ± 0.02 | PASS |
| Return and fan boundary cells | neighbour flip 7.2e-7 / 2.0e-4; GPU = CPU | flip ≤ 1e-4; ≤ 1e-3 | PASS |
| Warm start, 1.04 M cells, GPU | 1.05 % from cold | ≤ 2 % | PASS |
| Warm start, 423 k cells, CPU | 3.08 % from cold | ≤ 2 % | FAIL |
| Older convergence loop, 250 k cells | capped after 587,955 steps, 36.04 ± 1.043 ACH | converged | FAIL (retired) |
| Steps to converge, warm against cold | 429,804 against 735,972 | warm < cold | PASS |

The two start-up failures share one cause: the inflow drifts slowly (about 40k steps) and its steady state depends on the start. The gap between two starts is 0.47 % at 60k cells and 4.3 % at 400k cells (0.1 to 0.2 % with an equilibrium inflow). Nothing shipped calls the older loop. The device tests are mass and energy balances, not comparisons with measured devices.

### Room-ACH checks on the sample house

| Check | Result | Band | Status |
| --- | --- | --- | --- |
| Command line against app (Standard, 10 min, 5.09 M cells) | largest per-room fresh-air gap 6.7 % (Bedroom 2: 12.8 against 12.0) | ≤ 10 % per room | PASS |
| Imported mesh against hand-drawn, fresh-air | 4 of 7 rooms within 25 % (Kitchen +72.0, Bedroom 2 −27.6, Landing +55.9 %) | ≤ 25 % per room | FAIL |
| Imported mesh against hand-drawn, total exchange | 4 of 7 (Living +45.1, Kitchen +39.4, Bedroom 1 +37.2 %) | ≤ 25 % per room | FAIL |
| Rotated model, steady wind (270° against 180°), total exchange | 6 of 6 (largest 3.1 %) | ≤ 8 % per room | PASS |
| Rotated model, High (gusty), total exchange | 4 of 6 (Hall −10.0, Bedroom 2 −16.7 %) | ≤ 8 % per room | FAIL (gust noise) |
| Compact against Full, total exchange | +11.0 to +13.9 % on 6 of 6 | +7 to +16 % | PASS |
| Grid dependence, cell size ±2 % | chapter 11 | design bounds | FAIL |

## 11. Grid dependence

Room ACH is not grid-converged. Across 2.4, 5.2 and 11.4 M cells only two of six rooms hold their fresh-air rate within 5 %, and a 0.6 % change in cell size can move a room by 20 % or more. Compare design options on one grid, and expect up to about 35 % for a single room and about 20 % for the building between grids.

### Grid series (October 2026)

Standard run, steady wind, sample house, three cell budgets: 2.38 M cells at 220 mm, 5.19 M at 169 mm and 11.41 M at 130 mm (refinement ratio about 1.30). The first two converged; the finest hit its cap with 5 rooms not settled.

Only Hall and Bedroom 1 hold their fresh-air ACH within 5 % across the series. The table below gives total exchange as well, which converges for none of the six rooms.

| Room | Fresh-air ACH, 220 / 169 / 130 mm | Behaviour | Total exchange, 220 / 169 / 130 mm | Behaviour |
| --- | --- | --- | --- | --- |
| Living | 9.8 / 10.8 / 16.1 | divergent | 22.6 / 24.8 / 32.1 | divergent |
| Kitchen | 21.8 / 26.9 / 39.0 | divergent | 57.6 / 62.9 / 84.0 | divergent |
| Hall | 48.3 / 48.2 / 50.4 | within 5 % | 85.2 / 97.9 / 125.4 | divergent |
| Bedroom 1 | 112.4 / 110.4 / 111.6 | within 2 % | 115.4 / 168.0 / 139.0 | oscillatory |
| Bedroom 2 | 11.3 / 11.4 / 9.1 | oscillatory | 32.2 / 33.6 / 23.1 | oscillatory |
| Landing | 51.6 / 57.1 / 32.6 | oscillatory | 111.5 / 134.6 / 89.3 | oscillatory |

A grid-convergence index needs a monotone series; for a divergent or oscillatory one it cannot be computed. In the usual three-grid test (standard form), with φ the result on fine (1), medium (2) and coarse (3) grids:

```math
R = \frac{\varphi_2 - \varphi_1}{\varphi_3 - \varphi_2}: \quad 0 < R < 1 \text{ monotone convergence}, \quad R < 0 \text{ oscillatory}, \quad R > 1 \text{ divergence}
```

### Cell-size jitter

The same house and wind at 170.4 mm and at 169 mm (a 0.6 % change in cell size):

| Room | Metric | 170.4 mm | 169 mm | Change |
| --- | --- | --- | --- | --- |
| Living | Fresh-air ACH | 12.3 | 10.8 | −12 % |
| Kitchen | Fresh-air ACH | 33.5 | 26.9 | −20 % |
| Landing | Fresh-air ACH | 46.0 | 57.1 | +24 % |
| Bedroom 1 | Total exchange | 123.3 | 168.0 | +36 % |

Both runs had tight ranges, so this is not noise. Openings are only a few cells wide, so where the grid falls across a window changes its flow (chapter 4, placement scatter).

### Re-check after the age-of-air fix

Nine runs on the same build, sample house, wind 4 m/s from 270°, about 5 M cells, all ending Converged.

Building fresh-air ACH across the five cell sizes (167.0 to 173.8 mm) ran from 18.3 to 22.1 (19.6 at the 170.4 mm reference); the run with the grid origin shifted read 18.8 and the rotated run 18.9, both at 170.4 mm. Shifting the grid origin or rotating the model on the same cell size keeps the building rate in bound; changing the cell size does not.

| Test | Result against the design bound |
| --- | --- |
| Same grid run twice | 0.0 % on every metric: the spread is not run-to-run noise |
| Cell size ±2 % | Outside on 10 of 13 metrics; building fresh-air 18.8 % (bound 6 %); single rooms up to 33.9 % (Kitchen fresh-air) |
| Grid origin shifted 0.25 cell | Building fresh-air 4.2 %, within bound; Kitchen fresh-air 13.8 %, Bedroom 1 and 2 total 11.3 % and 14.3 % outside (bound 10 %) |
| Model and wind rotated | Within bound on all 13 metrics (building 3.6 %) |

Verdict: FAIL (13 failing lines). The spread is in the flow itself, not the age calculation: total exchange moves as much as fresh air, which matches how walls and envelope land on the grid (openings 5 to 6 cells across, thin partitions about 1 cell).

### Paired comparison

WindFlow can run two design options on the same set of K sub-cell grid offsets (2 to 8, default 4) and report, per room, the B − A difference: mean %, min to max over pairs, wins k of K, and better / worse / unclear. Two criteria were fixed in advance and tested at 4 offsets with 5-minute Standard runs:

| Criterion | Result | Status |
| --- | --- | --- |
| Sign stable in every pair where the mean difference exceeds 15 % | 3 of 3 (Kitchen fresh-air −27 %, Living total −18 %, Kitchen total −16 %) | PASS |
| Paired spread at most 50 % of unpaired | met in 5 of 13 rows; median ratio about 0.6; building fresh-air 0.74 | FAIL |

The comparison stays; the claim that pairing “largely cancels” grid noise was corrected to “partly”.

### Experiments not merged

- **Partial cells at wall edges (PSM).** Tested on six grids (5.40 / 5.23 / 5.09 / 4.96 / 4.82 M cells plus a shifted origin), aiming at a room total-exchange spread under 10 %. With PSM the spread was 16.5 to 38.7 % against 8.2 to 17.8 % without, worse in all six rooms; building fresh-air spread 43.7 % against 16.2 %; and the 40-cell cross-ventilation case fell to q 0.651 (FAIL, default 0.746). Coarse CPU tests that looked favourable did not carry over to the GPU grid. Not merged.
- **Plume model.** Merged as an opt-in and disclosed (chapter 9).
- **Online age in Standard.** Merged, per room, where settled.

## 12. Known biases and limits

Three limits matter most (October 2026): room numbers depend on the grid (up to about 35 % per room, 20 % per building); the default 10-minute Standard run settled in none of 7 test runs; and the wind inflow settles slowly, landing results about 1.5 to 3 % high. Every other limit is listed below with its measured size and what to do about it.

| # | Limit | Measured size | What to do | Chapter |
| --- | --- | --- | --- | --- |
| 1 | Per-room results depend on the grid | up to \~35 % per room, \~20 % per building; no convergence across 2.4 / 5.2 / 11.4 M cells | Compare options on one grid; use paired offsets | 11 |
| 2 | Standard run rarely settles in 10 min | 0 of 7 runs settled | Read ranges; use High or a longer limit for a final check | 7 |
| 3 | Inflow settles slowly, depends on start | +1.5 to 3 % in 10 min; starts 0.5 to 4 % apart | Keep runs alike when comparing | 5 |
| 4 | Viscosity floor | air 150 to 850 × too viscous; jets laminar | Treat jet reach and absolute ACH as indicative | 3 |
| 5 | Smooth ground | wind at 1 m \~54 % under EN profile; oblique drift 10.8 to 15.9 % at 2 m | Expect low near-ground speeds | 5 |
| 6 | Compact domain blockage | 10.5 %; room total exchange about 11 to 14 % high against Full in the newest run (8 to 33 % in the older Preview-grid run, which the app's note still quotes) | Use Full for a final check | 5 |
| 7 | Small openings | C\_d 0.48 to 0.60 at 4 cells (flow 1 to 21 % low); placement scatter; rotated openings uncalibrated | Keep openings ≥ 8 cells across where it matters | 4 |
| 8 | Adiabatic walls | no U-values, no wall heat loss | Model cold windows as Surface sources | 9 |
| 9 | Small heat sources under-stratified | 1.30 K against 1.8 to 8.6 K | Try the opt-in plume model; treat as indicative | 9 |
| 10 | Outdoor air counts as fresh | façade re-entry untested | Watch rooms near exhausts | 6 |
| 11 | Open-top box keeps older boundary ring | — | — | 5 |
| 12 | Pressure noisier without gusts | density fluctuation 9.6e-5 against 6.3e-5 (1.5 ×) | — | 5 |
| 13 | Supply turbulence tuned for 4 to 16-cell patches | factors 2.06 / 1.41 / 1.31 / 1.17 / 1.22 at 4 / 6 / 8 / 12 / 16 cells; a 3-cell patch delivers \~0.35 to 0.5 of the request | Keep supply patches ≥ 4 cells; Revit air terminals under High only | 9 |
| 14 | Fine box is local view only | never supplies a room or opening number | Read ACH from the whole-building run | 8 |
| 15 | Diffuser adds no fresh air | by construction | Model outdoor-air supply explicitly | 9 |
| 16 | Age estimator | steady vs online \~30 % either way per room; High's steady age up to \~3 × lower | Read which age a result carries | 6 |
| 17 | Glass removal is per material | shared material removed as a whole | Split glass materials before export | 4 |
| 18 | Cross-ventilation flow reads high | 1.6 to 2.2 × paper tracer; \~10 to 35 % above PIV/LES | Trust the ranking more than the number | 10 |
| 19 | Imported rooms read high | total exchange 37 to 45 % above hand-drawn | Compare imported with imported | 4 |

### Other known limits

- Thermal is beta; the wall model is experimental; the nested box is isothermal only.
- The ± is statistical only (chapter 7).
- The mass-balance warning errs towards alarm: it compares inflow and outflow relative to the larger, so a building ventilated through one side can show red on a sound result:

```math
r = \frac{|Q_{\mathrm{in}} - Q_{\mathrm{out}}|}{\max(Q_{\mathrm{in}},\,Q_{\mathrm{out}})}
```

- Stairs and double-height spaces can count a void's air in two rooms.
- The older convergence-loop test and the warm-start CPU test fail (chapter 10).
- The installer is not yet code-signed.

## 13. How to read the report

Read the result line first: it says whether the run settled or ran out of time, which grid and wind it used, and which age of air the numbers carry. Every room number below it inherits those conditions.

### Cover page

- **Summary paragraph:** wind, openings open, building fresh-air rate, best and least ventilated room, rooms with no air path, the result's status.
- **3-D cover image:** the building with the airflow, when the app has a viewport to take it from.
- **Key numbers:** Result, Wind (stated at 10 m height), Quality / backend, Grid, Steps / solve time, Rooms analysed, Best ventilated, Least ventilated, Building fresh-air rate, Air age used, Total inflow. “Air age used” is a real row; it appears whenever the run carries convergence information.
- **Mass-balance box:** green “Mass balance OK” with the residual when the residual is physical; amber “Mass balance warning” with up to six of the solver's notes when it is not.
- A Preview result says so on the cover. The cover points to the Notes at the end when there are any.

### Result line

| Result line reads | Grid and wind |
| --- | --- |
| Preview | coarse grid, steady wind |
| Fine grid, steady wind (Standard) | Standard grid, steady wind |
| Fine grid, gusty wind (High) | fine grid, EN 1991-1-4 gusts |

“Air age used” says whether the numbers carry the steady or the online age.

### Rooms table

| Column | Meaning |
| --- | --- |
| Level, Name | Storey and room name |
| Vol m³ | Room volume used to turn flows into air changes |
| Voxel m³ | Air-cell volume the solver actually counted in the room |
| Nominal | yes = the drawn volume was used for ACH |
| Fresh-air ACH | 3600 ÷ mean age; “—” not solved, “sealed” no air path, “†” under 3 cells across, “(steady)” online age not settled, “(drifting)” batches not levelled |
| ± | Spread of the per-batch time-averaged age as fresh-air ACH (95 %, or the 99 % width a settled room was tested at); model error not included |
| Total exch. | All air entering the room, including from other rooms |
| Thru open. | Air entering through the room's listed windows and doors |
| Speed m/s | Mean occupied-zone speed (0.1 to 1.8 m) |
| Stagnant % | Share of the occupied zone below 0.10 m/s |
| Temp °C, Duty W | Thermal: occupied-zone temperature; heater (+) or cooler (−) duty, “—” free-running |
| CO2 ppm | Occupied-zone CO2 |
| Age occ. min | Mean age in the occupied zone, minutes (“sealed” at the cap) |
| Air-change eff. | Exhaust age ÷ (2 × mean age); 0.5 fully mixed; needs ≥ 90 % outdoor feed |
| Comfort %, Draught % | Share with PMV within ±0.5; share with draught rate above 20 % |
| Fresh-air (steady age) | High only: the steady age on the averaged flow, beside the online fresh-air ACH |

**Openings table:** name, level, kind, open or closed, flow in and out (m³/s). **Sources table:** each device's set and measured flow (m³/h; “—” if off). **Figures:** captioned plan and section images. **Notes:** the last section of the report, in plain dark text rather than an alarm colour. A lead line says they mark where the result is less certain and do not mean it is wrong, and repeats are grouped into one line each (for example “N openings: …”).

There is no guidance page in the report. The definitions, the per-variant age wording and the reference values of chapter 14 are in the separate “How WindFlow works” PDF, which the Rooms table footnote points to.

### Trust line and comfort words in the app

The trust line under the panel shows the mass balance as a dot and a phrase, plus “N notes” that open to plain sentences.

| Word on a room | Trigger |
| --- | --- |
| stuffy | red fresh-air band |
| draughty | mean occupied-zone speed above 0.8 m/s |
| uneven | plenty of air but over 40 % of the room still |
| open air | room outline drawn through open air |

### The age wording on each variant

- **Preview or a steady-age result:** fresh-air ACH comes from the steady age on the averaged flow; it can differ from the age followed through time by about 30 % either way per room, reading lower in slowly recirculating rooms and higher in fast-flushed ones.
- **Standard on a fine grid:** fresh-air ACH is the age followed through time, with turbulent mixing, in each room whose age settled. A room marked “steady age” did not settle in time and uses the steady age, with no ±.
- **High:** in gusty wind the headline is the age followed through the gusts. The steady-age value ignores gust mixing and usually reads lower, up to about 3 times, though not in every room.
- **Every variant:** a small change of grid size can move one room by up to about 35 % and the building by about 20 %. Compare design options on the same grid.

## 14. Reference values and references

### Reference values (design guidance, not compliance)

| Quantity | Guidance | Source |
| --- | --- | --- |
| Outdoor air, offices | about 2.5 L/s per person plus 0.3 L/s per m² | ASHRAE 62.1-2022, Table 6-1 |
| Thermal comfort | \|PMV\| ≤ 0.5 and PPD ≤ 10 % | ASHRAE 55 |
| Draught | draught rate DR below 20 % | ISO 7730 (not ASHRAE 55) |
| CO2 as a ventilation indicator | about 1,000 ppm often used | common practice |

### References

- **Age of air and air-change efficiency:** Sandberg (1981); Sandberg & Sjöberg (1983); ISO 16000-8; ASHRAE 129; Etheridge & Sandberg (1996); ASHRAE Fundamentals, chapter 16; Hurraß (2026).
- **Lattice Boltzmann and walls:** Bouzidi, Firdaouss & Lallemand (2001), interpolated walls; Ladd, moving-wall bounce-back; Guo, body-force model; Werner & Wengle, wall function; Smagorinsky, sub-grid model.
- **Orifices:** Idelchik; ISO 5167.
- **Measured rooms:** Nielsen (1990), IEA Annex 20; Restivo (1979) LDA data; Kuznik, Rusaouen & Brau (2008), Build. Simul. 1:72–82; Gresse, Merlier & Kuznik (2020), Data in Brief 29:105382.
- **Cross-ventilation:** Tominaga & Blocken (2015), Build. Environ. 92:452–461; Tominaga & Blocken (2016), JWEIA 155; van Hooff, Blocken & Tominaga (2017), Build. Environ. 114; Karava et al. (PIV); Hwang & Gorlé (2022), LES; Costola et al. (2009); Ramponi & Blocken (2012); Swami & Chandra.
- **Heated cavity:** de Vahl Davis (1983); Fusegi et al. (1991); Tric, Labrosse & Betrouni (2000).
- **Plumes and filling box:** Baines & Turner (1969); Worster & Huppert (1983); Morton, Taylor & Turner; van den Bremer & Hunt (2010), JFM 644:165; Alamdari & Hammond, convection correlation.
- **Model evaluation:** Chang & Hanna (2004); Hanna & Chang (2012); VDI 3783-9; COST 732; Law & Kelton, batch means.
- **Wind:** EN 1991-1-4; Smirnov, turbulence decorrelation.
- **Guidance values:** ASHRAE 62.1-2022; ASHRAE 55; ISO 7730.

For developers: WindFlow has a command-line mode, documented in the source repository. The full test records are kept by the author; the results are summarised in VALIDATION.md.
