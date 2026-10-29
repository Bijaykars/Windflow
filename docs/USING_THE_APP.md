# Using the app

## Basic and Expert

**Basic** (the default) is the whole product for most questions: open a model or the sample, set the wind,
Run, switch openings on the row switches (or click them in the view), and read the air as **Speed** or
**Still air**. The panel holds three sections (see "The side panel and editing" below): **ROOMS** (per room: **fresh-air ACH**, the still share, the **air age** in
minutes since the air came in from outside, lower is fresher, and at most one comfort word: *stuffy* (red fresh-air ACH band), *draughty* (room average of the time-mean air speed in the occupied zone above
0.8 m/s, the ASHRAE 55 limit without occupant control; local jets can be faster in rooms that do not get the tag), *uneven* (plenty of air, over 40 % of the room still),
or *open air* (no ACH for a room loop in open air)); **OPENINGS** (flow in m³/s with its direction);
**VIEW** (Speed / Still air and the display options). Under the panel is one trust line: a dot and a phrase from
the mass balance ("Mass balance holds", "Settling · 3 of 4 s averaged"), with "N notes" that open to one
plain sentence per solver note. The full technical text goes to Expert and to the log. **Export PDF** is in
the top bar; it shows its progress on the status line ("report · drawing the section (3 of 4)…") and takes
about 1.5 s on the sample house.

**The side panel and editing.** A section picker on the panel switches between **Rooms**, **Openings** and **View** in Basic
(Expert adds **Devices** and **Studies**); **Rooms** opens first. The Rooms list is an outliner (each room with its openings
under it) with a pinned inspector for the selection, the Standard / High choice pinned at the bottom, and a header strip on the
viewport (Speed / Still air, Plan / Section). The Openings list is built from the model, so its rows exist before the first Run.
*Openings:* **+ Add opening**, then click a wall: a 1.2 × 1.2 m window with a 0.9 m sill, with editable Width / Height / Sill / Type
(added openings only; limited to the free wall run and the storey head room). Delete an opening (row ×, inspector or the Del key):
a hole in an imported mesh is sealed with a fill solid. **Undo** (one step) sits in the status strip. While Add opening is armed,
deleted openings show as dashed outlines in 3D and as "Deleted: … · Restore" rows; this list is for the session only. A new
opening over a deleted one carves its fill. *Rooms:* delete (adjacent openings are re-sided to indoor); merge with Ctrl+click
(same storey by elevation; a multi-loop room, volumes and occupants summed); add a room from the "Spaces without a room" list
(detected once per edit, off the UI thread) or by clicking or hovering a space in 3D; Undo. Del in the 3D view deletes rooms too.
Every edit re-voxelises the model and restarts the live solve (it cancels a running Accurate pass). Everything persists through
save and load except the Restore list.

**Fresh-air ACH** (the room's headline number, and what the dot and the comfort words judge) is 3600 divided by the room's mean
**air age** in seconds (Sandberg 1981; ISO 16000-8). For a well-mixed room supplied with outdoor air it is the usual air change
rate, and it does not depend on the room volume. It equals an air-change rate only when the room's air is fully mixed; otherwise it is the room's mean-age equivalent (Etheridge & Sandberg 1996; Hurraß 2026). Air that reaches a room through other rooms counts at its real age, so a room
fed by its neighbour reads low. The **total air exchange** (every cubic metre entering the room volume per hour, from outside and
from other rooms alike, through openings, devices and plain-air faces such as a stair void) is the Expert second line and the
report's "Total exch. ACH" column; it is not a ventilation rate. Beside it the report, the headless table and the inspector tooltip give two numbers read on the openings' own planes: **through openings** (air entering the room through its listed windows and doors, summed over the openings that belong to it; fixed physical planes rather than the ragged room-boundary faces) and **net outdoor air** (through its exterior openings only: per opening inflow minus outflow, floored at 0, summed), plus the share of the air entering that is not through an opening. Measurement only: they change no headline number or status. In a Standard pass the online age (the age of air followed through time on the lattice, batch 6k) is
solved too, warm-started from the steady age, and **each room lands the online age where that room's age settled** (that room's age monitor: 99 % half-width under 5 %, the kept halves agreeing); Standard's stop rule and each room's Settled status do not depend on it; a room whose online age had not settled lands the steady age instead (`ScalarSolver.Solve`, Sandberg steady mean age, on the landed time-mean flow) and is marked "steady age" (status column, tooltip, PDF "(steady)"), with no ± for it. Standard switches a room to the online age only when that room's online age has settled within the run's time budget; on a ~5 M cell grid this typically needs 30-50 min of run time, so shorter runs mostly report the steady age (marked "steady age"). A room on the online age shows its averaging range (excludes slow age drift). The note (tooltip, headless text, PDF footnote) says which estimator each room used. The steady age differs from the age followed through time by about 30 % either way per room: it reads fresh air lower in slowly recirculating rooms and higher in fast-flushed ones (see [LIMITATIONS.md](LIMITATIONS.md)). **High** (gusty wind) lands the online age instead (AGE-HIGH), because the steady age on the averaged flow has no gust-driven exchange and can read up to about 3 times too old there; the steady value is shown beside it as "Fresh-air (steady age)" (inspector tooltip, headless column, PDF); it ignores gust mixing and usually reads lower, but not in every room. The steady solve costs about 0.5–1 s on the
sample house at Preview resolution and about 3 s at the Accurate grid (2 CPU cores; the app uses them all), so it runs once the
average has settled and then once a minute while the solver runs (not every 5 s pass); the opt-in online age (`RunOptions.OnlineAge`) adds 17 bytes per cell on the graphics card and about 6–8 % to the cost of a step (Batch 6f+6k); Standard runs it, so Standard steps cost about 6–8 % more per step (the through-opening numbers are worked out once, at landing, not on every batch).
The fresh-air ACH reads "—" with a grey dot
until then and after a wind or geometry change, never the total exchange standing in for it. A room no fresh air reaches reads "sealed". Outdoor air counts as age 0, so exhaust that leaves a room and is drawn back in counts as fresh (see [Known limitations](LIMITATIONS.md)).

**Expert** adds two sections to the picker and more detail in the others: the Rooms inspector gains occupants, the
air-age column and the in/out/residual numbers, the notes show in full, **View** gains ghost walls, particles,
playback speed and probes, and the new sections are **Devices** and **Studies** (Hold A/B with a
per-room A / B / Δ ACH table, best windows sweep, annual). The top bar gains Box, Clip and the video export
menu. What each mode shows is one table (`src/WindFlow.App/UiTable.cs`); `--selfcheck` reads the same table
against `MainWindow.axaml`.

**Settings** (gear, both modes): GPU use with its measured share, the performance tier, the particle count,
the log folder; Expert also shows "Box is the whole domain (v1)".

**Preview, then Accurate** (both modes, strip above the trust line): the live run is the **Preview** — a
coarse grid (1.5M cells on the high tier), instant particles, good for direction; its room numbers are muted,
marked "~" and tagged "preview". Once Preview has settled, the **Accurate** pass starts on its own: a finer
whole-building grid (tier budget: high 5M, medium 3M, low 1.2M, CPU 0.6M cells; clamped to what VRAM holds
next to the Preview grid), started cold from rest (the warm start from the Preview average is built but not used by the app: `warmStartWorks = false`; a measurement hook only),
settled for 4x Core's planned warm-up (2x for a cold start, 1x for a thermal run; the last fifth of it is a probe window that sets the batch length ), then averaged in batches until every room's ACH range
closes or the time budget runs out, then the rooms and air age on the fine grid. The app and the headless run use one driver for all of this (`AveragedPass`, batch 6f). Why: on the sample house a pass settled for the plain warm-up read the Living and
Kitchen ACH 33-64 % high, warm or cold; cold 2x lands within 5-9 % of a long run, and the warm seed, which carries
the Preview flow in, needs 4x to get within 4-8 % (measured 2026-09-28, before the fixes of 2026-10-12; not in queue V). A calm bar shows "letting the air settle…", "averaging · 64 % · about 40 s left" (ETA from the
measured steps/s; "…" when unknown) and, when the pass lands, "Averaged result ✓ settled" (a tick only when every room settled; a pass cut off by its time budget reads "Time limit reached" in amber, with no tick). The Accurate pass replaces the stepping (Preview stays
resident but idle, so an edit warm-starts it at once) and the particles follow the fine field once it is
averaging. Any flow-changing edit (wind, opening, device, environment, box) cancels it and restarts the cycle;
with a simulation box on there is no Accurate pass (the box is the finer grid). Export PDF and Lock use the
Accurate result when there is one (the PDF cover names it: Preview, "Fine grid, steady wind (Standard)" or "Fine grid, gusty wind (High)", with the
status line and a footnote on the age method); otherwise the PDF says "PREVIEW result" on the cover and "(preview)" in the
suggested file name. Settings shows the "Averaged-pass grid" budget (Expert may type one, e.g. `4M`). Measured on the
sample house (RTX 5070 Ti, `--gpu 100`, idle desktop): Preview settles in ~3–30 s (1.54M cells @ 255 mm); a Standard
pass on 5.09M cells @ 170 mm settles for about 12 s, then averages in batches of about 10 s: under the earlier, looser stop rule a run closed every room after 544 s; with the current rule no sample-house Standard run has settled inside its time limit. These timings predate the 2026-10-12 fixes and the online age; queue V (2026-10-20) found that the 10-minute Standard pass settled in none of 7 runs (Known limitations). A headless Accurate pass at the Preview cell size (1.54M cells, 10 min cap) converged after 256 batches (128 kept) in 509 s of wall time (Batch 6f+6k). A pass has no batch limit (RW-01): it ends on convergence, on the time limit or on three times the limit in raw time, so a pass of that length can run to its end.
The Standard pass stops after 10 minutes of stepping (600 s); a run on a busy GPU, or at the default 60 % GPU use, usually reaches that limit before every room settles.
Expect at most about 10 minutes of stepping plus 2–20 s for the rooms and air age; fewer steps fit in those 10 minutes when the GPU is shared or GPU use is below 100 %, and the result is then time-limited; a pass that
runs out of time says so (below). `--shotmode accurate` photographs it half-way and
finished; `--shotmode accurate-time` times one pass (`--accurate-start cold|warm`, `--accurate-settle <x>` for
measurements). When the pass lands the GPU is idle and **Run** reads **Done** (greyed, with a tip) until the next
edit; the pressure / temperature slice is redrawn from the Accurate field.

**Standard and High** (the pair above the pass strip). **Standard** is the default Accurate pass: steady wind (a
constant speed and direction) with no added turbulence, up to about ten minutes, an answer to "what does a perfectly steady wind of this speed and direction do"; rooms ventilated mainly by gusts (one opening, or openings only on walls parallel to the wind) read low, compare them with High. **High** adds what a real site has: gusty wind (approach turbulence after EN 1991-1-4, with Smirnov
decorrelation so the gusts change in time instead of staying frozen), turbulent supply jets (a per-terminal "turb" field
in the device inspector, defaulted from the preset; it applies to Revit air terminals only, not to the palette fans, split AC, cassette or diffuser) and an optional **Fine box**, a 2:1 finer grid around the room
selected when you switch it on (isothermal only, so it is greyed out while Thermal is on). High averages for the time
budget you pick (5, 10, 20 or 30 min, remembered). Because the inflow now fluctuates, each room's ACH moves from batch
to batch, so High's ranges are wide and the pass usually ends at its budget. The two treat the wind differently and can differ a lot: on the sample house the Living room read 13.3 ACH in steady wind and 20–23.5 in gusty wind (2026-10-09 figures, before the fresh-air ACH definition and the later fixes; not in queue V, which compared the building fresh-air rate instead: 27.0 ACH in High against 20.1 in Standard, +34 %). For rooms ventilated mainly by gusts the gusty number is the more realistic one; neither has been checked against measurements for such rooms. Do not mix a
Standard number with a High one.

**How the Accurate pass decides it is done.** It averages in batches (each at least one flow-through of the slowest
room) and cannot stop before 16 batches. The start-up transient is cut by power-of-two truncation: the first d batches
are dropped, d the largest power of two not above half the batches so far. Each room's ACH is the mean of the kept
batches, shown with a 95 % confidence interval (Student t) widened for the lag-1 autocorrelation of the batch means
(AR(1) correction). The stop test is stricter than the range on screen: the 99 % half-width must fall under the aim
(5 % of the room's ACH, with a floor), at least 5 batches must be kept, and the first and second halves of the kept
batches must agree. Only a pass that meets this reads **Settled**; since batch 6k with the opt-in online age with High's online age a room is also Settled only when that age has settled (a second monitor on the per-batch room mean age: 99 % half-width under 5 %, the kept halves agreeing, 1 s floor); Standard's online age has no such gate (it only picks each room's estimator at landing). The time cap is real elapsed wall time minus pauses, and a pass also ends at three times the cap in raw time (RU-02); the number of batches does not end it (RW-01). A pass cut off by its time budget reads "Time limit
reached: ranges shown" and each room shows its total-exchange ± range and whether the aim was met; a pass with fewer than 16 batches in
all, or fewer than 5 kept, is too short for any range and shows "± —" instead of a number. The rule was checked on
simulated autocorrelated batch means (Sandbox check 54b: 95 % intervals covered the truth 96 % of the time at lag-1
correlation 0.7); it has not been shown to be right for every building.

**Lock result** (top bar, both modes): when the setup is right, Lock pauses the solver (the GPU stops
solving), keeps the particles flowing through the averaged field of the last analysis pass, lifts the frame
cap to the display refresh and lets Auto raise the particles to the measured limit. Everything that would
change the flow (open, sample, wind, Run, environment, devices, box, studies, live link) is greyed out and
says why; camera, Speed/Still air, slice, room highlight and Export PDF stay live. Unlock carries on solving
from where it stopped. Measured on the sample house (RTX 5070 Ti, 165 Hz display): unlocked 29 fps (30 fps
solving cap) with 100k particles; locked 164 fps with 400k.

## Keys (viewport focused)

| Key | Action |
|---|---|
| Space | Pause / resume a live run |
| Click an opening | Toggle it open/closed, warm-restart the solve |
| P | Toggle particles |
| V | Switch particle view: Speed / Still air |
| G | Toggle ghosted walls |
| X | Cycle wall display (solid / x-ray / section) |
| S | Toggle the slice plane |
| B | Toggle probe placement mode, then click the slice or a floor |

Top-bar and panel controls cover the rest: simulation box, clip/section box, Hold A/B compare, opening
sweep, annual runs, PDF report, and video export.


## Screenshots

These are screenshots produced by the app's own `--shot <path>` flag (see Quick start in the [README](../README.md)), not staged
marketing images. The files are not included in the repository.

| File | What it shows |
|---|---|
| `app-shell.png` | The shell: top bar, 3D viewport, results panel. |
| `app-empty.png` | Empty state before a model is loaded. |
| `app-live-before.png` / `app-live-after.png` | Before/after a window is toggled during a live run. |
| `app-box.png` | Simulation box gizmo (drag the six face handles). |
| `app-boxsolve.png` | Whole building, then a nested fine box around one room; the sidecar compares both runs' opening flows. |
| `app-xray.png` | X-ray wall mode with a section cut. |
| `app-thermal.png` | Thermal run with a temperature slice. |
| `app-compare.png` | Hold A / Hold B / Compare (B − A). |
| `app-advanced.png` | Expert mode, Studies tab: sweep and annual results. |
| `app-basic.png` | Basic mode: the sample house solving, one room selected. |
| `app-basic-notes.png` | Basic mode with the trust line's notes open (plain-language sentences). |
| `app-expert.png` | Expert mode, Results tab. |
| `app-settings.png` | The Settings popover (gear, top right). |
| `app-basic-fresh.png` | Basic mode with the air age column filled in (minutes since the air came in). |
| `app-device-presets.png` | Expert → Devices: the preset palette, a device selected with its numbers folded behind Details. |
| `app-device-place.png` | Placing a ceiling fan: the ghost sits on the ceiling under the cursor, with the resolution verdict. |
| `app-boxsolve.png` | A simulation box with its blend band drawn and "in blend band" tags on the openings in it. |
| `app-locked.png` | A locked result: solver stopped, flow controls greyed, particles at the measured limit. |
| `app-walk.png` | End of the scripted Basic walk (Sample, Run, wind, a window closed, Still air, PDF). |
| `app-report-dialog.png` | The PDF report dialog. |
| `app-bench.png` | Headless bench mode, Speed particle view. |
| `app-still.png` | Still air particle view (amber cloud of cells below the still-air threshold). |
