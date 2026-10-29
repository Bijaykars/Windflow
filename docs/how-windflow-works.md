> Superseded by how-windflow-works.pdf (2-page explainer). Kept for reference.

# How WindFlow works

A technical guide for architects: the method, every benchmark it has been checked against, the results, and the limits. Every number comes from the project's own test records. A number that is not in those records is not in this document.

WindFlow is a design-stage airflow tool. Its results are estimates with stated uncertainty, not a substitute for a ventilation engineer or a code-compliance calculation. Use it to compare design options on the same model; it is less good at giving one exact number.

## 1. What WindFlow computes, and what it does not

### What it computes

WindFlow solves how air moves through and around a building for a given wind, then reads results off the solved air. For each room it reports:

- Fresh-air ACH: "ACH" means air changes per hour, how many times per hour the room's air is replaced. Fresh-air ACH is worked out from the age of air and is the room's headline number (chapter 5).
- Total air exchange (Total exch. ACH): every cubic metre per hour that enters the room volume, from outside and from other rooms alike. It is not a ventilation rate.
- Through openings and net outdoor air: the air entering through the room's own listed windows and doors, and the net outdoor air through its exterior openings only.
- Mean speed and stagnant share: the mean air speed in the occupied zone (0.1 to 1.8 m above the floor) and the share of that zone below the still-air threshold, 0.10 m/s in the PDF report.
- Thermal runs (beta) add: temperature, heater or cooler duty, CO2, and two comfort shares (PMV within ±0.5, and draught rate above 20 %).
- Openings: the flow through each opening, in m³/s, with its direction. Sources: the measured flow of each device.
- A mass balance (air in against air out) that drives the trust line in the app and the box on the report's cover page.

### What it does not do

- It is not a certification tool. The results are comparative design guidance.
- It has not been validated against a physical building. Treat absolute ACH, comfort fraction and room temperature as comparative and self-consistent.
- Not validated at all: single-sided ventilation (a room with one opening), openings narrower than 4 cells, and absolute ACH in a real building. Planned and not done: IEA Annex 20 3-D rooms with temperature, and the Architectural Institute of Japan (AIJ) guideline cases.
- No measured, heated, occupied room has been simulated, so the thermal model has never been checked against a measured room.
- Fresh-air numbers have not been checked against real tracer-gas measurements in a building, and heated rooms have one heat test that still fails.
- Ordinary walls, floors, roofs and glazing are adiabatic: there are no U-values and no wall heat loss.
- It does not import IFC files, only a Revit export and WindFlow's own model file.

## 2. The solver

### Lattice Boltzmann, in plain words

WindFlow splits the air around and inside the building into many small cubes (cells). Instead of solving the usual flow equations directly, a lattice-Boltzmann solver follows how packets of air, called populations, move from cell to cell and then collide, step by step. Speed and pressure emerge from those populations. The method used is called D3Q19: D3 means three dimensions, and Q19 means each cell exchanges populations with 19 directions (its neighbours).

- Collision: a regularized BGK collision (a standard way to model how populations relax towards equilibrium), with a Smagorinsky turbulence model. Smagorinsky is a large-eddy approach: eddies too small for the grid are replaced by an extra viscosity that grows with the local shear. Its constant is 0.16.
- Walls: a wall that crosses the link between two cell centres is treated as an interpolated link at its exact position (a Bouzidi link), from both sides, so a wall thinner than a cell is still leak-tight. Plain halfway bounce-back is used for the thermal walls.
- One method, two processors: the same calculation runs on the graphics card (GPU, through CUDA or OpenCL) and on the CPU. CPU and GPU agree: 0.0 % difference in flow and thermal results.
- Speed: about 1,300 to 3,400 million lattice updates per second (MLUPS, the standard speed measure for this kind of solver: how many cells are advanced one step each second) depending on load (about 3,390 in a clean benchmark), on an RTX 5070 Ti at the Accurate grid, before the online age adds 6 to 8 % per step.
- Live solving: the app steps the solver continuously and refreshes the display up to about 10 times a second; particles are advected through the field.

### The viscosity floor, and what it means

For stability the solver holds the viscosity of its simulated air above a floor (5e-4 in lattice units, the shipped value). The consequence is that on building-scale grids the simulated air is about 150 to 850 times more viscous than real air (Standard about 310 times, Preview about 470 times; the validation cases run at 4 to 28 times). Jets through windows and supplies behave like laminar jets, with an effective Reynolds number of a few hundred instead of about 10^5.

How much it matters, as measured: moving the floor from 5e-4 to 1e-4 changed the sample house's Living-room ACH by -30 % (13.4 to 9.4) and the Kitchen's by -15 %, and reversed the Kitchen window's flow. The floor was kept at 5e-4 because the same change made one validation case better and two worse. In a re-run in October 2026, Annex 20 improved on all four metrics (q 0.453 to 0.557), cross-ventilation got worse on q (0.746 to 0.587) and NMSE, and the MINIBAT ceiling jet got worse on all four. The rule's verdict was "WORSE: keep 5e-4". Treat absolute air changes and jet reach as indicative.

## 3. Building geometry

### Voxelisation

The building model (metres; X and Y horizontal, Z up) is turned into a grid of air, solid, fixed and device cells: every storey's walls, slabs, roofs, columns and furniture, plus an outdoor margin so the wind goes over the roof and around the façades and pressures emerge from the flow, not from a pressure-coefficient table.

- Centre sampling: a room's solved air volume reads -6.3 to +2.1 % of its nominal volume at Preview (254.5 mm cells) and -5.1 to +3.7 % at Accurate (169.6 mm). Room ACH divides by the nominal volume; the report's Nominal column says which volume was used.
- Thin walls seal: a 50 mm partition on a 255 mm grid leaks 0.0000 m³/s, and 0 of 60 leak probes leaked.
- Wall position: an interpolated wall in a channel flow is placed within 5 % of the true width (fitted 5.37 against true 5.40 cells), while plain halfway bounce-back is 9.2 % off.

### Openings and the discharge coefficient

Openings are carved open or filled closed without touching the source geometry, so toggling a window is a warm restart, not a rebuild. An opening's flow is the normal velocity integrated over the throat cells the voxelizer actually carved. The carved hole is rounded to whole cells and is 0 to 15 % larger than drawn at the checked grids.

The discharge coefficient (Cd) says how much of an opening's open area effectively passes air. A sharp-edged opening has Cd of about 0.61 at high Reynolds number (Idelchik; ISO 5167; Etheridge & Sandberg 1996). On a coarse lattice the hole resolves its own, different Cd, depending on how many cells fit across it and on the wall thickness. The default opening model is Calibrated: where the lattice resolves above 0.61, a sub-grid loss in the throat cells brings it down to the requested value (0.61 by default). Where it resolves below 0.61 nothing is added.

| Cells across the opening | Cd the lattice resolves | Calibrated Cd, mean of eight placements | What happens |
|---|---|---|---|
| 4 | 0.48 to 0.60 (0.562 at wall 1 cell) | 0.562 | below 0.61, no loss added, a note gives the flow shortfall (about 1 to 21 % low) |
| 8 | 0.62 to 0.65 (0.627) | 0.608 | calibrated to within ±1.3 % of 0.61 |
| 16 | 0.65 to 0.69 (0.651) | 0.610 | calibrated to within ±1.3 % of 0.61 |

The bracketed values are for a wall one cell thick.

Placement scatter. Where a window falls on the grid matters as well. The same window moved by a fraction of a cell changes its Cd by up to the figure below, and the app says so when it exceeds 5 % (scatter of the Cd the lattice resolves, over eight placements).

| Cells across | wall 0.5 cell | 1 | 2 | 3 | 4 |
|---|---|---|---|---|---|
| 4 | ±30.1 % | ±10.2 % | ±7.1 % | ±6.5 % | ±7.0 % |
| 8 | ±21.1 % | ±7.7 % | ±3.2 % | ±2.6 % | ±3.0 % |
| 16 | ±14.3 % | ±5.1 % | ±1.6 % | ±2.1 % | ±2.3 % |

Wall thickness is in cells across the top row. The table leaves out two placements, a hole aligned to the grid (edge on a cell face) and a hole with its edge on a cell centre; both lie outside it. A test of an aligned hole at 8 cells across with a one-cell wall reads Cd 0.716 against the requested 0.61 (+17 %). Openings much wider than 16 cells, rounded or thick-reveal openings, and rotated (non-axis-aligned) openings are not calibrated separately.

Warnings you will see. An opening under 4 cells across is flagged "is only N cells across", and its flow is unreliable. In Preview an opening 4 to 6 cells across has its size quantised to the grid (a ±1 cell row error of 15 to 25 % of its area). An effective Cd outside 0.42 to 0.8 on an opening at least 4 cells wide raises a warning.

### Imported meshes, room and opening detection

A building comes in as a Revit export, WindFlow's own model file, or an imported mesh; unnamed rooms detected on a mesh are called plain "Room 1", "Room 2" and so on, and their openings "Window N" and "Door N"; the direction and floor area suffix, as in "Hall (north, 24 m²)", is added only when two rooms share a real name. Things to know:

- Room volume: a room is the air inside its outline, filled in up to the room's storey top. A double-height room needs a storey height that covers its full height, and a void that opens into the storey above counts in the lower room.
- Room band: a 2-cell band from each detected room's core to its walls is added to the room. Imported rooms read total exchange 37 to 45 % higher than the same building drawn by hand. That is disclosed and not fixed: compare imported models with each other, not with drawn ones.
- Too few cells: a room under 3 cells across at this cell size is marked "unreliable (too few cells)" with a dagger in the report.
- Exchange instead of ACH: a room whose outline runs through open air has almost no inflow through openings; when the counted inflow is under 5 % of what crosses its outline, the row shows an exchange in m³/s with a grey dot instead of an ACH, and the report leaves it out of Best/Least ventilated.
- Glass removal on import is per material. Meshes are merged per material, so a glass material shared by a curtain wall and windows is removed as a whole; a shared name cannot be split.
- Model checks: a model is validated when opened (coordinates within ±1e5 m, sizes in (0, 1e3] m); a broken file is refused with a reason. A model more than 10 km from the origin (map coordinates) is centred in plan and a note says so.
- Editing: deleting an opening in an imported mesh seals the hole with a fill solid; adding a room, merging rooms and adding an opening re-voxelise the model and restart the live solve.

## 4. Wind and the outdoor domain

### Inflow and the outer ring

The whole-building run has its own outer ring: a non-equilibrium velocity inflow (the default), a constant-density outflow, an absorbing sponge on both, and a prescribed-velocity top lid. The report states the wind at 10 m height. A simpler equilibrium inflow was tried as the default and reverted in October 2026: it failed its cold-versus-warm-start bound (CPU 30 batches 1.4 % / 2.7 %; GPU 120 batches 1.03 % / 3.3 %) and the cross-ventilation case with 40 cells per building height failed with it (q 0.619 against a bound of 0.66, against 0.746 with the default). It remains an experiment; every published validation number uses the default inflow.

- Ground: smooth and no-slip, so wind near the ground is too weak: about 54 % under the EN 1991-1-4 profile at 1 m. For oblique winds the profile drifts 10.8 to 15.9 % at 2 m.
- Slow settling: the wind inflow settles slowly and depends on how the run started. Results land about 1.5 to 3 % high in a 10-minute run, and two starts settle 0.5 to 4 % apart (0.47 % at 60 thousand cells, 4.3 % at 400 thousand). Two of the solver's own tests fail for this reason.
- Open-top user boxes keep the older equilibrium ring, because a velocity inflow through a cut wall diverged in the checks.

### Compact and Full domain; blockage

- Compact (the default) keeps the outdoor margins small. On the sample house its blockage is 10.5 %, against about 2.4 % in the COST 732 domain.
- Full uses the COST 732 margins (5 H upstream, 10 H downstream plus a sponge, 5 H lateral, blockage ≤ 3 %, where H is the building height) at about 11.7 times the cells. Choose it under Expert, Settings, Domain.
- What the difference costs: at the Preview cell size (1.54 M cells) Compact read room total exchange 8 to 33 % higher than Full (median about 16 %) and the building fresh-air rate 25 % higher (12.1 against 9.7 ACH). After the later age-of-air fix the Compact-over-Full difference was +11.0 to +13.9 % per room and the building fresh-air rate still +22 %. Flows are therefore likely 8 to 33 % high in Compact; use Full for a final check. The app notes a blockage above 3 %.

### Gusts (High)

Standard and Preview use a steady wind profile. High adds gusty approach turbulence after EN 1991-1-4 (a synthetic turbulence field with Smirnov decorrelation, so the gusts change in time instead of staying frozen) and turbulent supply jets. The generated gust intensity reads 0.94 to 0.97 of the EN target. Because the inflow now fluctuates, each room's ACH moves from batch to batch, so High's ranges are wide and the run usually ends at its time budget.

Standard (steady wind) under-reads rooms that are ventilated mainly by gusts (one opening, or openings only on walls parallel to the wind); the app flags such rooms under Standard. Do not mix a Standard number with a High one.

### The optional wall model

A Werner-Wengle wall function (a model of the flow very close to a wall) exists under Expert, Settings. It is off by default, wind-only and greyed out while Thermal is on. On the measured cases it gave mixed, grid-dependent results (Annex 20 passes at 4 cells across the slot, fails at 8), so it stays experimental; its laminar-flow test fails (2.57 % against 2 %).

## 5. Age of air and air changes per hour

### The definitions

- Fresh-air ACH (air changes per hour from age of air) = 3600 ÷ the room's mean air age in seconds (Sandberg 1981; ISO 16000-8). It does not depend on the room volume. Air that reaches a room through other rooms counts at its real age, so a room fed by its neighbour reads low.
- It equals an air-change rate only when the room's air is fully mixed; otherwise it is the room's mean-age equivalent (Etheridge & Sandberg 1996; Hurraß 2026).
- "—" means the air age was not solved, or the room outline is drawn through open air; "sealed" means no air path reaches the room (the age sits at the solver's 24-hour cap).
- Total exch. ACH = all air entering the room volume per hour, including air from other rooms and through plain-air faces such as a stair void. It is not a ventilation rate.
- Air-change efficiency = exhaust age ÷ (2 × mean age): 50 % means fully mixed, up to 100 % for piston flow (ISO 16000-8). It is only given for a room fed at least 90 % straight from outside.
- Thru open. = through openings: air entering the room through its listed windows and doors (fixed planes, not the ragged room boundary).
- Outdoor air counts as age 0, so exhaust that leaves a room and is drawn back in counts as fresh. On the sample house the online age agrees with a tracer within 9 % in every room; a case with strong re-entry at a façade has not been tested. The region where age and CO2 are solved includes a 1 m outdoor ring.

### Steady age and online age

WindFlow has two ways to get a room's age of air.

- Steady age: solved on the time-mean flow with Sandberg's steady mean-age equation. It has no resolved turbulent mixing, so it is cheap but biased: it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones, and can differ from the age followed through time by about 30 % either way per room.
- Online age: the age of air is carried by the solver itself, followed through time with the turbulent mixing. It costs about 6 to 8 % more per step and about 17 bytes per cell on the graphics card.

Which one a result carries depends on the run:

| Run | Headline fresh-air ACH | What sits beside it |
|---|---|---|
| Preview | steady age on the mean flow | nothing; muted, marked "~" |
| Standard (fine grid, steady wind) | online age in each room whose age settled; steady age, marked "steady age", in a room that did not | no ± for a steady-age room |
| High (fine grid, gusty wind) | online age, followed through the gusts | steady age as "Fresh-air (steady age)": it ignores gust mixing and usually reads lower, up to about 3 times, but not in every room |

Why High lands the online age. On the sample house, a High run printed both ages on the same mean flow: steady-over-online age was ×2.04 in Living, ×1.56 Kitchen, ×1.23 Hall, ×0.91 Bedroom 1, ×3.07 Bedroom 2 and ×0.84 Landing; building fresh-air 27.0 ACH (online) against 13.3 (steady). The steady age under High read about 50 % low (Living -51 %, Bedroom 2 -67 %) and High fell below Standard (13.7 against 18.3 ACH building) before the change. No tracer-proven reference exists for a gusty case.

### How a run decides it is done (the convergence rule)

The Accurate run averages in batches, each at least one flow-through of the slowest room, and cannot stop before 16 batches. The start-up transient is cut by power-of-two truncation: the first d batches are dropped, d being the largest power of two not above half the batches so far. Each room's ACH is the mean of the kept batches, shown with a 95 % confidence interval (Student t) widened for the correlation between neighbouring batches.

The stop test is stricter than the range on screen: the 99 % half-width must fall under the aim (5 % of the room's ACH, with a floor), at least 5 batches must be kept, and the first and second halves of the kept batches must agree. Only a run that meets this reads Settled. A run cut off by its time budget reads "Time limit reached: ranges shown". A run with fewer than 16 batches in all, or fewer than 5 kept, shows "± —" instead of a range.

The rule was checked on simulated correlated batch means (95 % intervals covered the truth 96 % of the time at a correlation of 0.7); it has not been shown to be right for every building. "Settled" cannot exclude a drift of up to about 1.5 times the aim, and a room whose batches still drift is marked "(drifting)".

### Why a run takes 30 to 50 minutes

The default Standard run stops after 10 minutes of stepping (600 s). In 7 test runs it settled in none; only Bedroom 1 and the sealed bathroom did. A 60-minute run (328 batches) still left the Kitchen, Bedroom 2 and Landing not settled, and the 10-minute numbers sat within about 3 % of it for 5 of 6 rooms (Bedroom 2 +14 %).

The reason is the online age. Its settle rule (99 % half-width under 5 %, widened for correlation) meets an age whose batch-to-batch correlation is 0.85 to 0.97, which needs about 160 to 310 batches, 27 to 53 minutes at 5 M cells. A 20-minute Standard run landed the online age in Bedroom 1 only. That is not a defect; the rule is unchanged. In practice a shorter run mostly reports the steady age, marked "steady age". For a final check use High or a longer time limit.

The online age cannot always settle inside a run because it needs about one room fresh-air age: Bedroom 2 was still rising after 2000 s of physical time.

### Grid dependence and "±"

Per-room numbers depend on the grid. Compare design options on the same grid, and expect a change in cell size to move a single room's ACH by up to about 35 % and the whole building by about 20 %. The "±" is the averaging range of the model's own time mean (95 %, or the 99 % width the stop rule tested for a Settled room). It is statistical only: grid, inflow and model error are not in it, and it excludes slow age drift.

## 6. Quality levels and run times

| Level | Grid | Wind | Time | What it is for |
|---|---|---|---|---|
| Preview | about 1.5 M cells on the high tier (1.54 M cells at 254.5 mm on the sample house) | steady | settles in about 3 to 30 s | direction and first look; room numbers muted, marked "~" and "preview" |
| Standard (the default Accurate run) | finer whole-building grid, budget per tier: high 5 M, medium 3 M, low 1.2 M, CPU 0.6 M cells (5.09 M cells at about 170 mm on the sample house) | steady, no added turbulence | up to 10 minutes of stepping (600 s) | "what does a perfectly steady wind of this speed and direction do" |
| High | fine grid, Compact domain | gusty (EN 1991-1-4), turbulent supply jets, optional fine box | the budget you pick: 5, 10, 20 or 30 min | a real site, rooms ventilated mainly by gusts; for a final check |

- Preview then Accurate: once Preview has settled, the Accurate run starts on its own at the finer grid, from rest. It settles for 4 times the planned warm-up (2 times for a cold start, 1 for a thermal run), then averages in batches until every room's range closes or the time budget runs out.
- Cell budgets: the tier is chosen from a short startup solver run and the graphics card's memory, clamped to what that memory holds next to the Preview grid.
- Timings quoted for the sample house (RTX 5070 Ti): a Standard run on 5.09 M cells settles for about 12 s and then averages in batches of about 10 s. An Accurate run at the Preview cell size (1.54 M cells, 10 min cap) converged after 256 batches in 509 s. These timings predate some later fixes.
- Which grid to trust: compare options on the same grid and the same quality level, and run Full domain and High for a final check.
- Fine box: an optional 2:1 finer grid around one room. It sharpens the local view (particles, slices, probes) only; the room ACH and the opening flows always come from the whole-building run, because a two-way coupled box was built and did not beat it. Against a uniform run at the box's cell size, one hall opening read 14.7 % off with the box and 11.1 % off whole-building, and the Hall's total ACH 12.7 % against 3.3 %. It is isothermal only.

## 7. Thermal (beta), heat sources and devices

### The thermal scheme

Thermal is a hybrid scheme: the 3-D flow plus a finite-volume energy equation whose face fluxes are the flow solver's own mass fluxes (van Leer limited advection, Boussinesq buoyancy). A uniform temperature stays uniform and a sealed room's energy is conserved (0.004 to 0.07 % in the checks). It is labelled beta because it has been checked against textbook cases only, not measured rooms.

- Held temperature: with Thermal on, each room's volume mean is held at the indoor temperature by an ideal heater or cooler (on by default; free-running is a switch). The occupied zone of a stratified room can differ (a note appears above 1 K), and a room with its own split AC is not held. The report column "Duty W" shows + for heating and - for cooling, or "—" for a free-running room.
- Walls are adiabatic. Only Surface heat sources (a panel, or a cold window drawn as one) exchange heat, by an Alamdari-Hammond convection correlation with a forced term. It is checked against hand-computed plate values (3.413 W/m²K for a 20 K vertical plate with H = 3 m; 4.448 W/m²K for a 20 K hot floor, 2 m hydraulic diameter).
- Stack effect: with the hold off, the typed indoor temperature is only the initial air temperature, and the stack effect decays during the run.

### Small heat sources are under-stratified, and the opt-in plume model

The sealed filling-box test (a heater in a sealed room) reads a vertical temperature difference of 1.30 K against a reference of 1.8 to 8.6 K: about 25 to 40 % low, and it stays a FAIL. The heater delivers 99.3 % of its energy and nothing leaks; the plume is under-resolved and not grid-converged (1.35 / 1.35 / 1.55 / 1.76 K at cell sizes 0.268 / 0.198 / 0.134 / 0.100 m).

An opt-in plume model (experimental, October 2026, off by default) releases a small heater's power as a rising plume (Morton, Taylor & Turner; entrainment 0.12 from the literature, not fitted) one cell layer above the heater when the plume is too small for the grid. On the filling-box test it reads 3.65 / 4.55 / 5.96 / 7.07 K at those cell sizes, inside 1.8 to 8.6 K at every size, with energy within 1 %. But it is not grid-independent (it roughly doubles across that range), wall-mounted and Surface sources are not covered, wind does not bend it, and it was run on the CPU only.

### Devices

Ceiling fans, pedestal fans, split AC, cassette AC, exhaust fans, hoods and diffusers are modelled as body-force regions (Guo) that accelerate air already in the cell instead of injecting fixed-velocity patches, so they conserve mass by construction. A diffuser moves room air and adds no fresh air, and in a sealed room it brings in no outdoor air.

- Resolution chip: each device row carries "resolution OK" or "refine needed": a fan disc, or an outlet's short side, under 8 cells across at the current cell size needs a finer grid; a split AC's 120 mm slot needs 15 mm cells and reads "too small to resolve".
- Split AC delivers about 3 to 4 % below its set flow (0.145 against 0.15 m³/s, measured as the flux through the outlet face).
- Supply patches deliver their set flow to within 0.5 to 1 % (volumetric flow reads 0.5 to 1 % low).
- Sealed rooms with unbalanced devices: a sealed room whose set supply and extract differ has no pressure anchor, so the app scales the extract to the supply and adds a room note naming the room.
- Device inputs are capped: flow at 5 m³/s, radius at 1.5 m, supply temperature clamped to 5 to 40 °C.

## 8. Validation

### How to read these tables

"Band" is the pass/fail threshold coded in the test. "Status" is the status in the last run recorded. Rows that were not re-measured after the physics fixes of October 2026 carry the last figure. Where a band was changed, the row says so; reference values were recomputed, not moved to fit. The latest whole test-suite runs read 258 PASS and 3 FAIL (the filling-box test, and the two start-up drift tests), after an earlier run of 253 PASS and 3 FAIL.

The three measured-room cases are scored with four metrics (after Chang & Hanna 2004; hit rate after VDI 3783-9 and COST 732, with D = 0.25 and an absolute tolerance W per case):

- FAC2: the fraction of points where the prediction is within a factor of two of the measurement.
- FB (fractional bias): whether WindFlow reads too high or too low on average; 0 is no bias.
- NMSE (normalised mean square error): the size of the scatter between prediction and measurement; 0 is a perfect match.
- q (hit rate): the fraction of points that land within the measurement's relative and absolute tolerance.

The pass band is FAC2 ≥ 0.5, |FB| ≤ 0.3, NMSE ≤ 1.5, q ≥ 0.66. FAC2 in the tables below counts, as Hanna & Chang 2012 do, pairs where both values are below the tolerance W as hits; by the strict published definition (van Hooff, Blocken & Tominaga 2017) the values are 0.942 (MINIBAT), 0.619 (cross-ventilation, 40 cells per height) and 0.538 (Annex 20).

### 8.1 Measured rooms and wind-tunnel cases (re-run in October 2026)

| Case | Reference | Metric | WindFlow result | Band | Status |
|---|---|---|---|---|---|
| IEA Annex 20 2D isothermal room (8 cells across the slot, 106 points) | Nielsen 1990; LDA data of Restivo 1979 | FAC2 / FB / NMSE / q | 0.613 / 0.308 / 0.356 / 0.453 | FAC2 ≥ 0.5, abs FB ≤ 0.3, NMSE ≤ 1.5, q ≥ 0.66 | FAIL (q and FB) |
| Cross-ventilation, isolated building, 40 cells per building height (63 points) | Tominaga & Blocken 2015 wind tunnel | FAC2 / FB / NMSE / q | 0.778 / 0.119 / 0.216 / 0.746; Q/(UH A) 0.498 | same four; flux band 0.36 to 0.52 | PASS (all four) |
| Cross-ventilation, 20 cells per building height (11.6 M cells) | same | FAC2 / FB / NMSE / q | 0.381 / 0.007 / 1.228 / 0.286; Q/(UH A) 0.544 | same | FAIL (FAC2, q; flux above band) |
| MINIBAT isothermal ceiling jet (8 cells across the nozzle, 1419 points) | Kuznik, Rusaouen & Brau 2008; data Gresse, Merlier & Kuznik 2020 | FAC2 / FB / NMSE / q | 0.942 / 0.051 / 0.083 / 0.643 | same | FAIL (q only, 0.017 short) |
| Cross-ventilation flow rate, five opening layouts A to E, 40 cells per height | Tominaga & Blocken 2016 tracer values 0.37 / 0.27 / 0.23 / 0.22 / 0.25 | Q/(UH A) by tracer | A 0.588, B 0.529, C 0.496, D 0.447, E 0.522 (×1.59 to 2.16) | within ±20 % of the reference | FAIL in all five; A ranked first: PASS |

Q/(UH A) is the flow through the building divided by the wind speed at building height and the opening area.

### 8.2 Analytic and textbook references

| Case | Reference | WindFlow result | Band | Status |
|---|---|---|---|---|
| Two windows in series, effective Cd | orifice equation; sharp-edged Cd 0.6 to 0.65 | Cd 0.559 | 0.37 to 0.80 | PASS |
| Per-opening effective Cd, cross-ventilation scene | same | west 0.585, east 0.564 | 0.42 to 0.8 | PASS |
| Isolated sharp square orifice, 8 cells across | Cd about 0.61 (Idelchik; ISO 5167) | 0.716 (aligned hole) | 0.65 to 0.72 | PASS |
| Wall-built opening, 4 / 8 / 16 cells across: Cd the lattice resolves | the same orifice reference | 0.562 / 0.627 / 0.651 | information only | NOTE |
| Wall-built opening, 8 cells, calibrated, mean over placements | requested Cd 0.61 | 0.6093 | within 3 % | PASS |
| Two-storey house, windward-to-leeward pressure difference | self-consistent pressure-driven flow | 1.018 | 0.5 to 1.6 × | PASS |
| Stack (buoyancy) flow | orifice reference, Reynolds-corrected (Cd 0.421 at Re 149) | ratio 1.213, head error 7.9 % | 0.6 to 1.4 × | PASS |
| Exhaust air age against volume ÷ flow | Sandberg 1981 | 5.3 % error (106.2 s against 100.8 s) | ≤ 10 % | PASS |
| Two-room age ordering | ASHRAE 129 definitions | downstream age above upstream | qualitative | PASS |
| CO2 cap and rise | mass balance; ASHRAE 129 | within band | 0.4 to 2.5 × expected | PASS |
| CO2 field with no source stays uniform | no source, no decay | 419.9997 to 420.0002 ppm | 419.5 to 420.5 ppm | PASS |
| Open-air room loop | internal exchange must not read as fresh air | internal exchange 2.498 m³/s, inflow 0.0000 | qualitative | PASS |
| Channel flow with an interpolated wall | Bouzidi, Firdaouss & Lallemand 2001 | fitted width 5.37 against 5.40 cells | ≤ 5 % | PASS |
| Same channel with halfway bounce-back, for contrast | same | 9.2 % off | documents the gap | PASS (contrast) |
| 50 mm partition on a 255 mm grid | a sub-cell wall must seal | 0.0000 m³/s leak | = 0 | PASS |
| Box face inside a partition | must solve, not diverge | solves | no divergence | PASS |
| Laminar channel flow, wall model on against off | the model should change nothing | 2.5718 % | ≤ 2 % | FAIL |
| Heated cavity, Rayleigh number 1e4, 48 cells | Fusegi et al. 1991, Nusselt 2.05 | 2.059 (0.4 % off) | within 20 % | PASS |
| Heated cavity, Rayleigh number 1e5, 48 cells | Fusegi et al. 1991, Nusselt 4.34 | 4.382 (1.0 % off) | within 20 % | PASS |
| Heated cavity, 160 cells, GPU | Tric, Labrosse & Betrouni 2000, Nusselt 2.054 / 4.337 | 2.056 / 4.358 (0.1 % / 0.5 % off) | within 5 % | PASS |
| Sealed filling box, 3.0 m room | Baines & Turner 1969; Worster & Huppert 1983: 1.8 to 8.6 K | 1.30 K | 1.8 to 8.6 K | FAIL |
| Sealed-room heating energy balance | power × time ÷ (density × specific heat × volume) | 5.4 % error | ≤ 20 % | PASS |

### 8.3 Self-consistency, devices, nested box, inlets and warm start

| Case | Reference | WindFlow result | Band | Status |
|---|---|---|---|---|
| Cross-ventilation scene, CPU against GPU | same calculation, two processors | 0.0 % difference | ≤ 3 % flow, ≤ 5 % ACH | PASS |
| Orifice rig: CPU/GPU parity, mirror symmetry, offset symmetry | same calculation; mirror-identical masks | 0.6528 on both; 0 mismatches; within band | ≤ 1 %; = 0; ≤ 0.5 % | PASS |
| GPU against CPU thermal scheme, 3 kW room in wind | same scheme, two processors | max temperature difference 5.9e-5 K, energy 0.0000 to 0.0002 % | ≤ 1e-3 K, ≤ 0.01 % | PASS |
| Heated cavity Nusselt, CPU against GPU | same calculation | 0.0 % | ≤ 10 % | PASS |
| Ceiling fan disc flow | rated 1.200 m³/s | -3.5 % | ±25 % | PASS |
| Ceiling fan: sealed-room mass; CPU against GPU | mass by construction | residual 3.65e-5; GPU = CPU | ≤ 1e-3; ≤ 1 % | PASS |
| Split AC duty against its own air flow and temperature drop | internal consistency | 0.0 % | ≤ 15 % | PASS |
| Split AC duty against room enthalpy loss | room energy balance | 0.2 % | ≤ 20 % | PASS |
| Split AC thermal, CPU against GPU | same scheme | mean temperature 25.372 / 25.372 °C, duty 2360 / 2360 W | 0.05 K, 2 % | PASS |
| Split AC steady return temperature | energy conservation | +0.30 K (17.62 against 17.32 °C) | ±0.3 K | PASS (at the band edge) |
| Exhaust fan counted once in the mass ledger | counted once | residual 1.2 % | ≤ 10 % | PASS |
| Nested fine box equal to the whole domain at half the cell size | uniform fine grid | 2.6 % | ≤ 10 % | PASS |
| Nested fine box around a living room and landing | uniform whole-building run | 15 % / 10 % (whole-building alone 11.1 % / 17.5 %) | ≤ 25 % | PASS |
| Nested box: CPU against GPU; mass ledger | same calculation; mass conservation | 0.00 %; 0.2 % / 4.4 % | ≤ 1 %; ≤ 10 % | PASS |
| Supply inlet delivered against set flow, 1 / 2 / 4 / 6 cells | mass conservation | 0.995 / 0.993 / 0.999 / 0.997 | ±2 % | PASS |
| Two-cell supply patch in a corner | controller must settle on 6/5 | 0.991, scale 1.200 | ±2 %; 1.2 ± 0.02 | PASS |
| Return and fan boundary cells | density 1 imposed; GPU = CPU | no step-to-step flip; neighbour flip 7.2e-7 / 2.0e-4; ≤ 7e-7 | flip ≤ 1e-4; ≤ 1e-3 | PASS |
| Warm start, 1.04 M-cell GPU run | cold start | 1.05 % | ≤ 2 % | PASS |
| Warm start, 423 k-cell CPU run | cold start | 3.08 % | ≤ 2 % | FAIL |
| Older convergence loop, 250 k cells | must converge | capped after 587,955 steps, 36.04 ± 1.043 ACH | converged | FAIL |
| Steps to converge, warm against cold | warm fewer | cold 735,972; warm 429,804 | warm < cold | PASS |

### 8.4 Room-ACH checks on the sample house

| Check | Setup | WindFlow result | Band | Status |
|---|---|---|---|---|
| Command-line run against app | Standard, 10 min, 5.09 M cells | largest per-room fresh-air gap 6.7 % (Bedroom 2: 12.8 against 12.0 ACH) | per room within 10 % | PASS |
| Imported mesh against hand-drawn model, fresh-air | same house, same wind | 4 of 7 rooms within 25 % (Kitchen +72.0, Bedroom 2 -27.6, Landing +55.9 %) | per room within 25 % | FAIL |
| Imported mesh against hand-drawn model, total exchange | same | 4 of 7 rooms (Living +45.1, Kitchen +39.4, Bedroom 1 +37.2 %) | per room within 25 % | FAIL |
| Rotated model, steady wind, total exchange | model rotated, wind 270 against 180 | 6 of 6 rooms (largest 3.1 %) | per room within 8 % | PASS |
| Rotated model, High (gusty), total exchange | same | 4 of 6 (Hall -10.0, Bedroom 2 -16.7 %) | per room within 8 % | FAIL (gust noise) |
| Compact against Full domain, total exchange | same | +11.0 to +13.9 % on 6 of 6 | +7 to +16 % | PASS |
| Grid dependence, cell size ±2 % | about 5 M cells | see 8.5 | design bounds | FAIL |

### Paragraphs on each benchmark

IEA Annex 20 2D room (isothermal). A 2-D room with a slot jet, scaled so the Reynolds number is similar (slot 4 cells of 0.1 m, so H = 7.1 m against the specification's 3.0 m), compared as speed over inlet speed on the centre plane. The current inlet follows the specification's sine profile with 4 % turbulence. The October 2026 result misses q (0.453 against 0.66) and |FB| (0.308, just past 0.3), while FAC2 and NMSE pass. The ceiling jet decays about 25 % too fast at every grid tried, and this is the open gap. The setup changed from the earlier row (q 0.471), so the apparent regression is not like for like. The effective Reynolds number before the large-eddy term is about 1280 against the specification's 5000, from the viscosity floor.

Cross-ventilation (Tominaga & Blocken 2015). A generic isolated building in a wind tunnel. At 40 cells per building height with the measured approach turbulence and the product wind ring, WindFlow meets all four metrics (q 0.746, 63 points), and the opening-flux ratio Q/(UH A) of 0.498 is inside the 0.36 to 0.52 band. At 20 cells per height it fails (q 0.286); every earlier 20-cell run read q 0.24 to 0.33, so this is under-resolution that was already known. The pass is for that one case at that grid. The measured flow rate is itself ambiguous: the primary sources give 0.25 (Tominaga & Blocken, tracer), 0.50 (van Hooff, Blocken & Tominaga 2017) and an opening-flux band 0.40 to 0.47 (Karava PIV; Hwang & Gorle LES); none is chosen. The q is sensitive to the tolerance W: 0.365 at W = 0, 0.540 at 0.025 and 0.667 at 0.05 on the older run; W is chosen, not published.

MINIBAT ceiling jet (Kuznik 2008). A 3.1 × 3.1 × 2.5 m room with an isothermal ceiling jet, hot-wire data, full scale at 25 mm cells. FAC2, FB and NMSE pass; q reads 0.643 against 0.66, 0.017 short. Going from 6 to 8 cells across the nozzle leaves q flat (0.654 to 0.646) while the others improve, which suggests but does not prove a remaining model gap.

Tracer cross-ventilation, five layouts (October 2026): FAIL. Tominaga & Blocken 2016 opening layouts A to E, 40 cells per building height, a 24.8 M-cell domain with 4 H margins and 3.0 % blockage, GPU, 230 minutes. WindFlow's tracer flow is 1.59 to 2.16 times the paper's tracer values and fails the ±20 % band in all five; the ranking is right, with A first. WindFlow's tracer flow equals its own velocity-integrated flux to about 1 %, and the set-up and normalisation match the paper. The reference itself reads low: for layout E the paper puts the tracer value 37 % below the PIV opening flux (about 0.40), and an LES gives 0.44 to 0.47; against those E reads ×1.11 to ×1.33 high. Layouts A to D have no PIV or LES value. About +6 % of the gap is the 4 H domain. The band was not changed. Treat absolute cross-ventilation flow rates as likely high. An earlier error breakdown put 91 % of the error (in logarithmic units) on the opening's discharge coefficient and only 7 % on the domain.

Orifice, stack and façade pressure. These test the opening model against the orifice equation. The two-window and per-opening tests pass with Cd 0.559 and 0.585 / 0.564. The isolated rig reads 0.716 against the requested 0.61 (+17 %) because the aligned hole resolves higher than the mean over placements that the table is calibrated to; its band was moved to 0.65 to 0.72 for that reason. A rig with the hole at one offset read calibrated 0.739 / 0.716 / 0.670 at 4 / 8 / 16 cells, none within 3 % of 0.61 (it reads one placement of the scatter). The stack test passes at ratio 1.213 with Cd 0.421 at Re 149, where Cd = 0.6 does not apply below Re of about 1e4.

Heated cavity. The differentially heated 3-D cube, hot-wall Nusselt number (heat transfer relative to pure conduction), against Fusegi et al. 1991 at 48 cells and Tric et al. 2000 at 160 cells. After a test-geometry fix all pass: 0.4 % and 1.0 % off at 48 cells, 0.1 % and 0.5 % off at 160 cells; a conduction-limit geometry check reads Nusselt 1.00. At the older 48-cell grid the two Rayleigh cases read 8 to 10 % low.

Filling box: FAIL. A sealed room with a 500 W area heater, against the Baines & Turner / Worster & Huppert filling-box theory: 1.30 K against 1.8 to 8.6 K. The reference was recomputed for the corrected 3.0 m room, not moved to fit. The plume is under-resolved and the cause is open. See the plume model in chapter 7.

Devices. The device tests are mass and energy balances, not comparisons with measured devices. The fan disc reads 3.5 % under rated flow, the split AC duty closes the room enthalpy balance to 0.2 %, and the steady return temperature sits at the edge of its ±0.3 K band (+0.30 K). A split AC delivers about 3 % below its set flow.

Nested (fine) box. The fine box matches a uniform fine-grid run to 2.6 %, but on the sample house the box does not reduce the error in general (the whole-building run alone is 11.1 % / 17.5 % from the same reference). The test's ≤ 25 % band is too loose to catch drift; the box never supplies a room or opening number.

Warm start and the older loop. The warm-start CPU test and the older convergence-loop test fail because the wind inflow drifts slowly (about 40k steps) and its steady state depends on how the run started: the gap between two starts is 0.47 % at 60k cells and 4.3 % at 400k cells, and 0.1 to 0.2 % with an equilibrium inflow. The older loop is retired and nothing shipped calls it.

### 8.5 Grid dependence

Grid series (October 2026). Standard run, steady wind, sample house, three cell budgets (2.38 M cells at 220 mm, 5.19 M at 169 mm, 11.41 M at 130 mm; refinement ratio about 1.30). The first two converged; the finest hit its cap with 5 rooms not settled.

| Room | Fresh-air ACH at 220 / 169 / 130 mm | Behaviour | Total exchange at 220 / 169 / 130 mm | Behaviour |
|---|---|---|---|---|
| Living | 9.8 / 10.8 / 16.1 | divergent | 22.6 / 24.8 / 32.1 | divergent |
| Kitchen | 21.8 / 26.9 / 39.0 | divergent | 57.6 / 62.9 / 84.0 | divergent |
| Hall | 48.3 / 48.2 / 50.4 | within 5 % | 85.2 / 97.9 / 125.4 | divergent |
| Bedroom 1 | 112.4 / 110.4 / 111.6 | within 2 % | 115.4 / 168.0 / 139.0 | oscillatory |
| Bedroom 2 | 11.3 / 11.4 / 9.1 | oscillatory | 32.2 / 33.6 / 23.1 | oscillatory |
| Landing | 51.6 / 57.1 / 32.6 | oscillatory | 111.5 / 134.6 / 89.3 | oscillatory |

Verdict: not grid-converged for room ACH. Only Hall and Bedroom 1 fresh-air hold within 5 %, and a grid-convergence index cannot be computed for a divergent or oscillatory series.

Cell-size jitter. The same house and wind at 170.4 mm and at 169 mm (a 0.6 % change in cell size) moved fresh-air ACH Living 12.3 to 10.8 (-12 %), Kitchen 33.5 to 26.9 (-20 %) and Landing 46.0 to 57.1 (+24 %), and Bedroom 1 total exchange 123.3 to 168.0 (+36 %). Both runs had tight ranges, so this is not noise: openings are a few cells wide, so where the grid falls across a window changes its flow.

Re-check after the age-of-air fix (October 2026). Nine runs on the same build, sample house, wind 4 m/s from 270, about 5 M cells, all ended Converged:

| Run | Cell size | Building fresh-air ACH |
|---|---|---|
| Cell-size sweep | 167.0 mm | 22.1 |
| Cell-size sweep | 168.7 mm | 20.3 |
| Cell-size sweep (reference grid) | 170.4 mm | 19.6 |
| Cell-size sweep | 172.1 mm | 20.2 |
| Cell-size sweep | 173.8 mm | 18.3 |
| grid origin shifted 0.25 / 0.25 cell | 170.4 mm | 18.8 |
| model rotated, wind 180 | 170.4 mm | 18.9 |

| Test | Result against the design bounds |
|---|---|
| Same grid run twice | ok: 0.0 % on every metric, so the spread is not run-to-run noise |
| Cell size ±2 % | outside on 10 of 13 metrics; building fresh-air 18.8 % (bound 6 %); single rooms up to 33.9 % (Kitchen fresh-air) |
| Grid origin shifted | building fresh-air 4.2 % ok; Kitchen fresh-air 13.8 %, Bedroom 1 and Bedroom 2 total 11.3 % and 14.3 % outside (bound 10 %) |
| Rotated model and wind | ok on all 13 metrics (building 3.6 %) |

Verdict: FAIL (13 failing lines). The spread is in the flow itself, not the age calculation; total exchange moves as much as fresh air, which matches the placement of walls and envelope on the grid (openings 5 to 6 cells across, thin partitions about 1 cell). This is why you should compare options on one grid and expect up to about 35 % for a single room and about 20 % for the building.

### 8.6 Experiments that did not make it, and the paired comparison

Paired comparison. WindFlow can run two design options on the same set of K sub-cell grid offsets (2 to 8, default 4) and report per room the B-A difference (mean %, min to max over pairs, wins k of K, better/worse/unclear). Two criteria were fixed in advance and tested on the sample house at 4 offsets with 5-minute capped Standard runs. Sign stable in every pair where the mean difference exceeded 15 %: 3 of 3, PASS (fresh-air Kitchen -27 %, total Living -18 %, total Kitchen -16 %). Paired spread at most 50 % of unpaired: met in 5 of 13 room and metric rows, median ratio about 0.6: FAIL (the building fresh-air rate 0.74). The comparison stays; the claim that pairing "largely cancels" the grid noise was corrected to "partly".

PSM experiment: not merged. PSM means partial cells at wall edges: letting a wall cut a cell fractionally instead of in whole cells. It was tested on six grids (5.40 / 5.23 / 5.09 / 4.96 / 4.82 M cells plus a shifted origin), Standard, 10 min cap. The aim was a room total-exchange spread under 10 %. With PSM the spread was 16.5 to 38.7 % against 8.2 to 17.8 % without it, worse in all six rooms; the building fresh-air spread was 43.7 % against 16.2 %; and the cross-ventilation case with 40 cells per height fell to q 0.651 (FAIL, default 0.746). The coarse CPU tests that looked favourable did not carry over to the GPU grid. It was not merged.

Opt-in plume model: merged as an opt-in and disclosed; see chapter 7. Online age in Standard: merged, per room, online age where settled. A 20-minute run landed it in Bedroom 1 only, which is why shorter runs mostly report the steady age.

The full test records, including the raw data and scripts, are published with the WindFlow source code.

## 9. Known biases and limits

This chapter summarises the known limitations; the sizes are as measured. The three that matter most (October 2026):

- Per-room numbers depend on the grid: up to about 35 % for a single room and about 20 % for the building, with no convergence across 2.4, 5.2 and 11.4 M cells. Compare options on one grid.
- The default Standard run (10 min) did not settle in any of 7 test runs: it shows ranges, not a settled value.
- The wind inflow settles slowly and depends on how the run started: results land about 1.5 to 3 % high in a 10-minute run.

The rest, in short:

1. Viscosity floor: air about 150 to 850 times stickier than real air (chapter 2).
2. Smooth ground: wind at 1 m about 54 % under the EN profile; oblique winds drift 10.8 to 15.9 % at 2 m.
3. Compact domain: blockage 10.5 %; flows likely 8 to 33 % high against Full.
4. Small openings: Cd 0.48 to 0.60 at 4 cells across (8 to 21 % low flow at wall 1 to 4 cells); placement scatter in the chapter 3 table; rotated openings uncalibrated.
5. Walls do not conduct heat; only Surface sources exchange it.
6. Small heat sources are under-stratified: 1.30 K against 1.8 to 8.6 K; plume model opt-in.
7. Outdoor air counts as fresh; strong façade re-entry untested.
8. An open-top simulation box keeps the older boundary ring.
9. Pressure is a little noisier without gusts: density fluctuation 9.6e-5 against 6.3e-5 (1.5 times).
10. Supply turbulence is tuned for patches 4 to 16 cells across (factors 2.06, 1.41, 1.31, 1.17, 1.22 at 4, 6, 8, 12, 16 cells); smaller patches deliver less (3 cells about 0.35 to 0.5 of the request); Revit air terminals under High only.
11. The fine box sharpens the local view only.
12. A diffuser adds no fresh air.
13. Age-of-air estimator: the steady and online ages differ by about 30 % either way per room; High's steady-age value can read up to about 3 times lower.
14. Glass removal is per material.
15. Cross-ventilation flow rates read high: 1.6 to 2.2 times the paper's tracer values, ranking right; against independent opening-flux and LES values about 10 to 35 % high.

Other known limits: thermal is beta; the wall model is experimental; the "±" is statistical only; the mass-balance warning errs on the side of alarming (it compares inflow and outflow relative to the larger, so a building ventilated through one side can show a red warning on a sound result); stairs and double-height spaces can count a void's air in two rooms; imported (mesh) rooms read total exchange 37 to 45 % higher than the same building drawn by hand; a nested box is isothermal only; the older convergence-loop test and the warm-start CPU test fail; there is no code signing on the installer yet.

## 10. How to read the report

Cover page. A summary paragraph (wind, openings open, the building's fresh-air rate, best and least ventilated room, rooms with no air path, the result's status); an optional 3-D view; Key numbers (Result, Wind at 10 m, Quality / backend, Grid, Steps / solve time, Rooms analysed, Best and Least ventilated, Building fresh-air rate, Air age used, Total inflow); and a mass-balance box, green when the residual is physical and amber with the solver's notes when it is not. A Preview result says so on the cover.

The result line. It says whether the run settled, or ran out of time, and which grid and wind treatment was used: "Preview", "Fine grid, steady wind (Standard)" or "Fine grid, gusty wind (High)". "Air age used" says which age the numbers carry.

Rooms table (columns).

| Column | Meaning |
|---|---|
| Level, Name | the storey and the room name |
| Vol m³ | the room's volume, used to turn flows into air changes |
| Voxel m³ | the volume of air cells the solver actually counted in the room |
| Nominal | yes = the nominal (drawn) volume was used for ACH |
| Fresh-air ACH | 3600 ÷ mean air age; "—" not solved, "sealed" no air path, "†" under 3 cells across (unreliable); "(steady)" = this room's online age had not settled; "(drifting)" = the averaged batches had not levelled off |
| ± | approximate spread of the room's per-batch time-averaged air age, as fresh-air ACH (95 %, or the 99 % width a settled room's stop rule tested); model error is not included |
| Total exch. | all air entering the room, including from other rooms |
| Thru open. | air entering through the room's listed windows and doors |
| Speed m/s | mean speed in the occupied zone (0.1 to 1.8 m) |
| Stagnant % | share of the occupied zone below 0.10 m/s |
| Temp °C, Duty W | thermal runs: occupied-zone temperature; heater (+) or cooler (-) duty, "—" if free-running |
| CO2 ppm | occupied-zone CO2 |
| Age occ. min | mean age in minutes in the occupied zone ("sealed" at the cap) |
| Air-change eff. | exhaust age ÷ (2 × mean age); 0.5 = fully mixed; needs a room fed at least 90 % from outside |
| Comfort %, Draught % | share of the occupied zone with PMV within ±0.5; share with draught rate above 20 % |

Openings table. Name, Level, Kind, Open or Closed, and the flow in and out in m³/s. Sources table. For each device, its set flow and its measured flow in m³/h ("—" if switched off). Figures. Plan and section images, each captioned. Notes. At the end: where the result is less certain. They do not mean the result is wrong; repeats are grouped into one line.

The app's trust line under the panel gives the mass balance as a dot and a phrase, and "N notes" that open to plain sentences. The comfort words on a room are stuffy (red fresh-air band), draughty (the room's mean occupied-zone speed above 0.8 m/s), uneven (plenty of air but over 40 % of the room still) and open air.

### The age wording that goes with each variant

- Preview, or a steady-age result: Fresh-air ACH comes from the steady age of air on the averaged flow. It can differ from the age followed through time by about 30 % either way per room: it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones.
- Standard on a fine grid: Fresh-air ACH is the age of air followed through time, with the turbulent mixing, in each room whose air age settled. A room marked "steady age" had not settled within the time budget and uses the steady age instead, with no ±.
- High: in gusty wind the headline fresh-air ACH is the age of air followed through the gusts. The steady-age value ignores gust mixing and usually reads lower, up to about 3 times, but not in every room.
- In every variant: per-room numbers depend on the cell size, so a small change in grid size can move a single room by up to about 35 % and the whole building by about 20 %. Compare design options on the same grid.

## 11. Reference values (guidance)

Reference values for design guidance, not a compliance certification.

- Outdoor air (ASHRAE 62.1-2022, Table 6-1): for example offices about 2.5 L/s per person plus 0.3 L/s per m².
- Thermal comfort (ASHRAE 55): |PMV| ≤ 0.5 and PPD ≤ 10 %.
- Draught (ISO 7730): draught rate (DR) below 20 %. The draught-rate model and its 20 % limit are ISO 7730, not ASHRAE 55.
- CO2 as a ventilation indicator: about 1000 ppm is often used.

## 12. References

Papers and standards named in this document, cited by author and year.

- Sandberg (1981) and ISO 16000-8: age of air, air-change efficiency. Sandberg & Sjoeberg (1983). ASHRAE 129.
- Etheridge & Sandberg (1996): discharge coefficients and the air-change interpretation. ASHRAE Fundamentals, chapter 16. Hurraß (2026).
- Bouzidi, Firdaouss & Lallemand (2001): interpolated wall position. Ladd: moving-wall bounce-back. Guo: body-force model for devices. Werner & Wengle: wall function. Smagorinsky: sub-grid model.
- Idelchik; ISO 5167: sharp-edged orifice Cd.
- Nielsen (1990), IEA Annex 20; LDA data of Restivo (1979).
- Tominaga & Blocken (2015), Build. Environ. 92:452-461; Tominaga & Blocken (2016), JWEIA 155; van Hooff, Blocken & Tominaga (2017), Build. Environ. 114; Karava et al. (PIV); Hwang & Gorle (2022) LES; Costola et al. (2009); Ramponi & Blocken (2012); Swami & Chandra.
- Kuznik, Rusaouen & Brau (2008), Build. Simul. 1:72-82; Gresse, Merlier & Kuznik (2020), Data in Brief 29:105382.
- de Vahl Davis (1983); Fusegi et al. (1991); Tric, Labrosse & Betrouni (2000): heated cavity.
- Baines & Turner (1969); Worster & Huppert (1983): filling box. Morton, Taylor & Turner: plume model. van den Bremer & Hunt (2010), JFM 644:165. Alamdari & Hammond: convection correlation.
- Chang & Hanna (2004); Hanna & Chang (2012); VDI 3783-9; COST 732: model-evaluation metrics and best-practice guidelines. Law & Kelton: batch means.
- EN 1991-1-4: wind actions and gusts. Smirnov: turbulence decorrelation.
- ASHRAE 62.1-2022; ASHRAE 55; ISO 7730: guidance values.

For developers: WindFlow has a command-line mode, documented in the source repository along with the test suite and the full records.
