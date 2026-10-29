# BIM outputs: release spec

Status: decisions D1-D6 taken on 2026-10-27 (§7); ready to implement. Applies to WindFlow 0.2.0 (`src/WindFlow.App/WindFlow.App.csproj:24`).
Code facts were read from the source on 2026-10-27; file:line references are to that state.

## 1. Why

People who work in BIM document ventilation in four places:
- parameters on the model's rooms, which drive their tags, colour fills and schedules;
- drawing sheets;
- schedules;
- a report for the submission.

Today WindFlow's results reach only the PDF and the `--solve` text. There is no CSV, vector, IFC or BCF output, and results are not saved
(`src/WindFlow.App/UserFiles.cs:34`). So users retype numbers and redraw figures, and every design change repeats that work.

**Goal:** results flow into those four places and regenerate on every run. They are keyed to the user's own rooms and windows, use the units
their standards use, and state their uncertainty.

**Not in this spec:**
- IFC import or export (`docs/LIMITATIONS.md:83`);
- BCF;
- thermal-comfort outputs on isothermal runs;
- a rules engine that interprets a standard for the user.

## 2. Rules for every item

1. **One results file is the source of truth.** CSV, Revit parameters, PDF tables and figure labels are all rendered from it, never
   recomputed on their own.
2. **Keys are the model's stable ids:** `Room.Id` and `Opening.Id`. For Revit exports these are Revit UniqueIds
   (`src/WindFlow.Revit/RevitExtractor.cs:347, 451, 501`).
3. **Units are SI.**
   - Flows are in l/s in tables and files (m³/s internally).
   - Ages are in minutes, CO2 in ppm, areas in m² and volumes in m³.
   - Revit parameters use Revit's flow types, so they display in the project's own units (l/s, m³/h or cfm).
4. **Every number travels with its uncertainty and a run stamp.** A value with no known ± says "none" and gives the reason.
5. **Wording follows the app's existing caveats** and `audits/2026-10-12/fixes/ACCURACY_PLAN.md` §8. No new accuracy claims.
   - The grid-uncertainty and noise figures are the published ones in `VALIDATION.md`, never set per project (no tuning).
6. **No new package without owner approval.** CSV, SVG and DXF are plain text and are written by hand. XLSX would need a package, so it is
   deferred.
7. **Each item has one runnable check (§6)** and can ship on its own.

## 3. What exists today

| Area | Today | Gap |
|---|---|---|
| Revit export | Rooms: `Id` = UniqueId and `Name` (`RevitExtractor.cs:347`). Openings: `Id` = UniqueId (`:451`, `:501`); `Name` = "Family: Type" (`:544`) or the literal "Wall opening" (`:501`). Levels carried (`:72`). The Mark is read (`:546`) but used only as a sort key (`:383`) | No room Number and no opening Mark in the model. Of the coordinate transform, only `NorthDeg` is stored (`Simulation.cs:20`; `RevitExtractor.cs:399-408`) |
| Revit writes | LiveLink pipe (`LiveLink.cs:41`). Revit to app: `model`, `opening`, `close`. App to Revit: `select`, `ack` | Nothing writes to the Revit document: no Transaction, no `Parameter.Set`, no shared parameters |
| Mesh import | Assimp formats (`MeshImport.cs:17-26`) | Source names and ids are dropped (`MeshImport.cs:77`). Rooms and openings get generated ids (`RoomDetect.cs:160`, `OpeningDetect.cs:413`) |
| Room results | `RoomResult` (`Simulation.cs:687-774`): Inflow, Outflow and OutdoorInflow (m³/s); Ach; FreshAch = 3600 / VolumeMeanAge (`Analysis.cs:87`); ConservativeFreshAch (steady age, `Analysis.cs:93`); MeanAge (s, occupied zone 0.1-1.8 m); MeanCo2 (ppm, occupied zone); DraughtFraction (share of occupied-zone cells with draught rate > 20 %) | No l/s, per-person or per-m² values. No floor-area field (the room has footprint `Loops`, `Simulation.cs:106`) |
| Opening results | `OpeningFlow` (`Simulation.cs:650-672`): FlowRate (m³/s, net, positive along Normal), MeanSpeed, CarvedArea, PressureDrop, EffectiveCd, ModelledCd, LossK | Net flow only, no gross in/out. Each side says Outdoors, Indoor or Room, but not which room (`Simulation.cs:93`) |
| Room-to-room flow | `RoomFlux` holds per-room totals (`RoomFlux.cs:7-13`), tallied by room only (`:229-238`). The per-cell room labels from `RoomOwner` (`:247-256`) are temporary | No room-to-room matrix |
| Convergence | `RoomConvergence` keeps the final Mean, HalfWidth, FreshHalfWidth and BatchCount (`Convergence.cs:24-39`) | The per-batch series is private (`ConvergenceMonitor._batches`, `:117`) |
| PDF | PDFsharp 6.2.4. Cover; speed plates (a plan per storey at 1.1 m, an E-W section, B-A); rooms, openings and sources tables; guidance; disclaimer (`ReportBuilder.cs`). Plates are PNG at 40 px/m (`Renderer.cs:20`) | Age and CO2 plates exist (`Renderer.cs:15`) but are unused. Raster only. At 40 px/m a 20 m building prints at about 100 dpi across A4. The openings table's "In/Out" columns are just the sign of the net flow (`ReportBuilder.cs:420`) |
| Project data | ProjectName, Author and Scenario (`ReportBuilder.cs:13-15`), kept in app settings (`Settings.cs:16-17`), not in the model | No job number, revision, client, address or status. The weather-file location is parsed (`Climate.cs:20-40`) but never printed |
| Version | 0.2.0 (`WindFlow.App.csproj:24`) | No informational version. The repository has no commits, so no build can be traced |
| 3D and field export | GltfWriter writes geometry only (`GltfWriter.cs:17`). VtkWriter writes velocity, pressure and cell kind (`VtkWriter.cs:12`). Neither has a menu entry | No results in 3D exports |
| Standards | Constants only (`Standards.cs:19-118`) | No presets |

## 4. Release R1 (next release)

Build order: B1, then B2 and B3, then B4, B6 and B5. Effort: S is under 1 day, M is 1-3 days, L is over 3 days.

### B1. Results file (M)

**Where it is written:**
- `<model>.windflow-results.json` next to the model file after each completed run, and on *File > Save results*.
- `--results-out <path>` for `--solve`.

`schemaVersion` 1. Later changes add fields only, so old files keep loading.

**Fields:**
- `stamp`:
  - schemaVersion, runId (GUID), appVersion (informational version with the build commit, once the repository is versioned);
  - modelSha256 (of the saved model JSON), exportId (B4), createdUtc;
  - backend and device, quality, cells, dxMm, domain, blockagePct;
  - wind {speedMs, fromDeg, gusts}, thermal, ageMode (steady | online);
  - stopReason, keptBatches, averagedSeconds, ledgerResidualPct.
- `rooms[]`:
  - Identity and size: id, name, number (new), level, areaM2, volumeM3 and volumeBasis (nominal | voxel: the volume Ach divides by,
    `Analysis.cs:938-939`), occupants.
  - Fresh air: freshAch, freshFlowLs, freshPerPersonLs, freshPerAreaLsM2, outdoorInflowLs.
  - Other results: totalExchangeAch, totalExchangeLs, meanAgeOccMin, meanAgeVolMin, co2Ppm (null when air quality is off), draughtPct (null
    unless thermal), stagnantPct, airChangeEfficiency (same rule as today's table).
  - `uncertainty`: {freshPct, freshKind, totalPct, totalKind, gridPct, gridSource}. The kind is "99 % stop width", "95 % CI" or
    "published figure".
  - `flags`: [sealed, ageCapped, notSettled, gridSensitive, steadyAge].
  - `target` and `verdict` (B3).
- `openings[]`:
  - Identity and size: id, name, mark (new), level, kind, isOpen, widthM, heightM, sillM, carvedAreaM2.
  - Flow: netFlowLs, from, to (new, see below), meanSpeedMs, pressureDropPa, effectiveCd, modelledCd.
  - `notes`, e.g. cdOutsideBand.
- `building`: freshAch, freshFlowLs, uncertainty, sealedRooms.
- `definitions`: the definitions below as text, so the file explains itself.

**Definitions** (also printed in the PDF):
- **freshFlowLs** = freshAch × volumeM3 / 3.6. This is the outdoor-air rate that would give the room its mean age if the room were fully
  mixed.
- **outdoorInflowLs** = OutdoorInflow × 1000: air entering straight from outside. It differs from freshFlowLs when a room is fed by other
  rooms, so tables show both.
- **areaM2** = |Σ shoelace areas of `Room.Loops`|. Check the loop orientation convention: for rooms with a nominal volume, area ×
  (ZMax − ZMin) must equal the nominal volume.
- **sillM** = Center.Z − Height / 2 − the level's elevation.
- **from / to:**
  - During analysis, take the first air cell beyond Depth / 2 along −Normal and along +Normal, and read its room label from `RoomOwner`
    (`Analysis.cs:490` already has the grid).
  - The value is a room id, "outdoors", "indoor (no room)" or "unknown".
  - A positive netFlowLs means from → to.

**Model additions** (additive JSON fields): `Room.Number`, `Opening.Mark`, the project fields (B6), `Views` (B5b), `Revit.ExportId` and
`Revit.Transform` (B4, B5c).

**Fix in passing:** the FreshAch doc comment says MeanAge, but the code uses VolumeMeanAge (`Simulation.cs` around 733-735, `Analysis.cs:87`).

### B2. Schedules (S)

*File > Export > Schedules (CSV)*, and `--schedules-out <dir>` on the command line. Both write `rooms.csv`, `openings.csv` and `run.csv` (the
stamp as key/value pairs) from the results file.

**Format:**
- UTF-8 with BOM, comma separator, `.` decimal point.
- Units in the header, e.g. `Fresh air [l/s]`. Id in the first column, one row per item.

**Rooms columns:** Id, Number, Name, Level, Area [m²], Volume [m³], Occupants, Fresh air [ACH], Fresh air ± [%], Fresh air [l/s], Fresh air
[l/s per person], Fresh air [l/s per m²], Outdoor inflow [l/s], Total exchange [ACH], Total ± [%], Mean age occupied zone [min], CO2 [ppm],
Draught [%], Target [l/s], Verdict, Flags.

**Openings columns:** Id, Mark, Name, Level, Kind, Open, Width [m], Height [m], Sill [m], Carved area [m²], Net flow [l/s], From, To, Pressure
drop [Pa], Effective Cd, Notes.

**PDF changes:**
- The PDF rooms and openings tables switch to these columns and units.
- The openings table's "In/Out" columns become "Net flow [l/s]" with From and To.

`// ponytail: invariant CSV only; add a locale option (";" separator, "," decimal) if users ask.`

### B3. Targets and verdicts (S)

**Target.** Each room, or each room type, gets a target, set in the app and stored in the model:

    q_target [l/s] = a × occupants + b × area [m²] + c × volume [m³] / 3.6

- a is l/s per person, b is l/s per m², and c is ACH.
- This one form covers standards that add a per-person and a per-area rate (e.g. EN 16798-1, the ASHRAE 62.1 ventilation rate procedure),
  as well as plain ACH targets.
- The target is compared with the fresh-air flow (outdoor air), not with total exchange.
- R1 ships no presets: the user types a, b, c and a source label (e.g. "EN 16798-1 Cat II"). Presets come in R2, and only with values
  checked against the standard's text and edition (D2).

**Verdict.** Let Q be the room's fresh-air flow, and U = sqrt(stat² + grid²) the combined relative uncertainty.

| Verdict | Rule |
|---|---|
| Meets target | Q × (1 − U) ≥ q_target |
| Below target | Q × (1 + U) < q_target |
| Too close to call | otherwise |

The two parts of U:
- **stat:**
  - When the age is online, the room's own fresh-air ±.
  - With the steady age there is no per-run ±, so stat is the published fresh-air noise figure for the quality level, flagged.
  - W3a A2/A3 measured seed-to-seed fresh-air differences of up to 9 % (Living), so stat = 0 would understate the uncertainty.
- **grid:** the published per-room grid figure for the quality level. Today that is the app note's "up to about 35 %" per room; after W3 it is
  the measured figure in `VALIDATION.md`.

Report stat and grid separately: in the results file, in the CSV flags and in a PDF footnote.

### B4. Revit: write results back (M-L)

**Deferred (owner, 2026-10-27): no new Revit plugin work in R1.** This section stays as documentation for later; R1 builds B1, B2, B3, B5 and B6.

New add-in ribbon commands, next to Export: **Import results** and **Clear results**.

**Parameters:**
- Import reads a results file, matches elements by UniqueId, and writes instance shared parameters in one Transaction.
- A shared-parameter file ships with the add-in. It uses the group "WindFlow" and fixed GUIDs, so tags and schedules keep working across
  projects.
- Bindings to Rooms, Windows, Doors and Project Information are created on the first import.
- Wall openings (`Opening` elements) are not written; they are listed instead.

**Rooms:**

| Parameter | Type |
|---|---|
| WF Fresh air ACH | Number |
| WF Fresh air | AirFlow |
| WF Fresh air per person | Number (l/s per person) |
| WF Fresh air per area | AirFlowDensity |
| WF Total exchange ACH | Number |
| WF Uncertainty % | Number (U from B3) |
| WF Mean age min | Number |
| WF CO2 ppm | Number |
| WF Target | AirFlow |
| WF Verdict | Text |
| WF Flags | Text |
| WF Run | Text (date, quality, run id) |

**Windows and doors:**

| Parameter | Type |
|---|---|
| WF Net flow | AirFlow |
| WF Flow direction | Text, e.g. "Kitchen → outdoors" |
| WF Pressure drop | HvacPressure |
| WF Effective Cd | Number |
| WF Run | Text |

**Units.**
- Types come from `SpecTypeId`, so values display in the project's units.
- Values are converted from SI with `UnitUtils.ConvertToInternalUnits`.
- Check the spec type names against the Revit 2026 API before building.

**Stale results.**
- Each export writes a new ExportId (GUID). It goes into the model and into the Project Information parameter "WF Last export"; results
  carry it.
- If the ids differ, Import warns "these results come from an older export of this model", and imports only if the user confirms.

**After import**, the add-in reports what was matched and written, ids in the file that were not found, and model rooms with no result.

**Clear results** blanks every WF parameter, so old numbers cannot linger on sheets.

**Export additions:**
- `Room.Number` from the Revit room Number.
- `Opening.Mark`, which is already read at `RevitExtractor.cs:546`.
- `Revit.ExportId`, and `Revit.Transform` (B5c).

**Docs:** add a short how-to to `docs/LIVELINK.md`: a colour scheme by WF Verdict, a room tag label showing WF Fresh air and WF Verdict, and
a room schedule with the WF fields.

`// R2: push results over the LiveLink pipe instead of a file (B10).`

### B5. Sheet-ready 2D figures (L, in three parts)

**B5a. Quantities, labels, scales and resolution (M)**
- **Quantities:**
  - Add Age plates (`FieldKind.Age`) on every run with age on, and CO2 plates (`FieldKind.Co2`) when air quality is on.
  - Draught (`FieldKind.DraughtRate`) only on thermal runs, because it needs the comfort pass (`Simulation.cs:317`).
  - The headline number comes from air age, so the report must show age, not only speed.
- **Colour scales:** one fixed scale per quantity across a whole report and across B-A pairs. It is either the union range of those plates
  or the view's fixed scale, and the legend states its units.
- **Plan labels:**
  - Each room gets its name, fresh-air ACH ± and a verdict chip.
  - Opening arrows show the net l/s and the direction (B1 from/to).
- **Age contours** at 2, 5, 10 and 20 min, drawn with `RenderKit.DrawContours` (`RenderKit.cs:698`).
- **Resolution:** set PixelsPerMetre from the printed size, so plates are at least 200 dpi. Today it is 40 px/m (`Renderer.cs:20`).
- **Fix:** the Age plate's no-data text says "Air quality enabled", but age alone produces the plate (`RenderKit.cs:148`).

**B5b. Saved views (M)**
- **The model gets `Views[]`.** Each view has:
  - a name, a kind (plan | section) and a quantity;
  - for a plan: {level, heightAboveFloorM};
  - for a section: {p1, p2 on plan, zMin, zMax}, at any angle;
  - a scale {min, max}, or auto.
- **Defaults:** one plan per storey at 1.1 m above the floor, plus the existing E-W section.
- **In the app:** the plan slider and the section cut get "Save view". The report and the exports draw every saved view.
- **Angled sections** need a new trilinear sampler on the averaged field; today the cut can only run along X or Y
  (`MainWindow.axaml.cs:1320`).

`// R2: import Revit levels and section marks as views.`

**B5c. Vector and CAD overlay exports (M)**
- **Formats.** Each view exports as:
  - SVG: the heat map as an embedded image, plus vector contours, arrows, labels and room outlines;
  - DXF R12 ASCII, in metres: layers WF-ROOMS, WF-CONTOURS, WF-ARROWS and WF-LABELS, no raster. One file per view; a plan sits at z = 0.
- **Coordinates.**
  - The DXF uses the source model's coordinates.
  - For Revit exports, record `Revit.Transform` at export: the offset from the internal origin, the angle to true north and the units.
    Today only NorthDeg is kept.
  - Write the DXF in Revit internal coordinates, so *Link CAD* with *Auto - Origin to Internal Origin* lines up.
  - First check whether `P()` (`RevitExtractor.cs:35`) already keeps internal coordinates. If it does, the transform is just the units
    conversion.

### B6. Method, assumptions and record pages (M)

- **Project fields move from app settings into the model:**
  - project name, job number, revision, client, site address, status or purpose of issue, author, checked by, approved by;
  - the cover and every page footer print them;
  - the existing settings values seed new models.
- **Assumptions register:**
  - a table of openings: id, mark, level, kind, open, size, sill, and Cd requested and modelled;
  - the wind: speed, direction, profile and gusts;
  - thermal on or off; occupants and outdoor CO2 (420 ppm, `Simulation.cs:222`);
  - domain, quality, cells, Dx and blockage;
  - the location and weather file (`Climate.cs:20-40`) when one is used.
- **Method and convergence:**
  - a solver summary, reusing the `docs/HOW_IT_WORKS.md` wording;
  - the averaging length in batches and physical seconds; the stop reason; the mass-balance residual; the age mode; the uncertainty
    definitions;
  - one small convergence chart per room, from the batch series. This needs read-only access to `ConvergenceMonitor._batches`
    (`Convergence.cs:117`).
- **Limitations:**
  - the run's grid and opening notes;
  - the grid figure used in the verdicts;
  - the headlines from `docs/LIMITATIONS.md`.
- **Validation status:** the cases and results as listed in `VALIDATION.md` at build time, and no other claims.
- **Record stamp**, also kept in the results file: run id, app version and build commit, model SHA-256, date and device.

Prerequisite (owner): commit and tag the repository, so the build commit exists.

## 5. Release R2 (after)

| Item | What | Depends on |
|---|---|---|
| B7 Airflow network diagram | A diagram drawn on the plan. Rooms are nodes at their plan centres; openings and open-plan boundaries are arrows in l/s; outdoors sits at the edge. It comes with a room-to-room flow matrix, and a "where this room's air comes from" view using a per-room tracer on the mean flow (`ScalarSolver`) | Gross two-way flow per opening. A neighbour-room tally in `RoomFluxPlan.Reduce` (`RoomFlux.cs:229-238`), using the `RoomOwner` labels of each face's Inside and Outside cells |
| B8 Options and revisions report | Per-room differences between two results files, each marked "real" or "within noise" | A calibrated σ. W3a A2/A3 (FIXLOG, 2026-10-27) showed the four-group σ understates building fresh-air noise |
| B9 3D export | glTF with room volumes coloured by verdict or ACH, streamlines as tubes, and saved views as textured planes. A VTK menu item, with age and CO2 added | GltfWriter is geometry-only today |
| B10 Live results | Push results to Revit over the LiveLink pipe | B4 |
| B11 Annual and sweep pages | Show `AnnualResult` (`Climate.cs:171-195`) and `SweepEntry` (`OpeningSweep.cs:6-17`) in the report: hours meeting target per room, and flow against opening fraction | B3 |
| B12 Standard presets | Target presets, with values checked against each standard's text and edition | Owner picks the standards |
| B13 Smaller items | XLSX; BCF; Revit levels and sections as views; keeping mesh-import names (`MeshImport.cs:77`); a suggested CO2-sensor position | Owner approval for any package |

## 6. Checks (one per item; added to `--selfcheck` unless noted)

| Item | Check | Pass |
|---|---|---|
| B1 | RES-01: run the sample house, write the results file and read it back | Every model room and opening appears once. freshFlowLs = freshAch × V / 3.6 within 1e-9 relative. areaM2 × height = nominal volume within 1e-6 for nominal rooms. Every opening has from/to set |
| B2 | RES-02: write CSV from the RES-01 file | Headers equal §4 B2. Row counts equal the room and opening counts. Parsing back matches the results file to the printed precision |
| B3 | RES-03: verdict table | Nine fixed cases, including both boundaries, give the expected verdict |
| B4 | A unit test of the matcher in the add-in project (pure C#: results plus element list in, writes plus unmatched out), and a manual Revit script in `docs/LIVELINK.md` | Matcher exact. Manual: values equal the file to display precision; a second import changes nothing; a newer export triggers the stale warning |
| B5 | RES-04: plates and DXF for the sample house | Plates are at least 200 dpi at print size. All plates of one quantity share min and max. The DXF parses, and a known wall corner lands within 1 mm of the model corner |
| B6 | RES-05: report text | It contains the run id and model hash. The assumptions table has one row per opening. The hash is stable across two saves of an unchanged model |

## 7. Decisions

Taken by the owner on 2026-10-27, as recommended.

| # | Question | Decided |
|---|---|---|
| D1 | Verdict uncertainty: combine the statistical ± with the published grid figure, or use the statistical ± only (labelled) | **Combined**, as B3 is written. Most rooms will read "too close to call" until W3 publishes a smaller grid figure |
| D2 | Targets: user-entered only in R1, or presets now | **User-entered only in R1.** Presets wait for B12, with values verified against each standard |
| D3 | Revit category: Rooms only (as exported today), or Rooms and MEP Spaces | **Rooms only in R1** |
| D4 | Which users come first: Revit add-in users or mesh-import users | **Revit first**, in the §4 build order. If mesh import turns out to be common, keep the source names (`MeshImport.cs:77`) and do B2 and B5 before B4 |
| D5 | XLSX in R1 (needs a package), or CSV only | **CSV only in R1.** XLSX stays in B13 |
| D6 | Commit and tag the repository before release, so results can name their build | **Yes, before R1 ships.** Owner action, not done yet: the repository still has no commits |

## 8. Issues found while writing this

- The FreshAch doc comment says MeanAge, but the code uses VolumeMeanAge. Fix in B1.
- The Age plate's no-data text is stale. Fix in B5a.
- The openings table's "In/Out" columns are only the sign of the net flow. Fix in B2.
- The open dialog leaves out `.ply`, although the importer accepts it (`MainWindow.axaml.cs:341`). Not part of this spec.
