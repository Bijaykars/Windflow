# Command line and Revit export

## CLI flags (`src/WindFlow.App/Program.cs`)

```
WindFlow.App.exe [model.json]
  --adapter <substring>        pick a GPU by DXGI description substring
  --bench <seconds>             run headless for N seconds and print a bench summary, then exit
  --live                        during --bench, run the stepping solver instead of a single batch solve
  --particles <n>                fixed particle count, no auto-adjust (default: startup tier, then auto)
  --tier-probe <mlups>           skip the startup probe and pick the tier from this MLUPS (testing)
  --fps-inject <fps>             feed the particle auto-adjust this fps instead of the measured one (testing)
  --view speed|still              initial particle view
  --fakelive                     republish a perturbed field at 10 Hz (hitch test)
  --box xmin,ymin,zmin,xmax,ymax,zmax   nest a fine box (half the cell size) in the whole-building solve
  --box-v1                       with --box: the old v1 box (the box is the whole domain, open faces)
  --shot <path>                  save one full-window screenshot and exit
  --shotmode box|xray|advanced|compare|boxsolve|rooms|basic|basic-notes|basic-fresh|expert|settings|locked|menu|wind|
             device-presets|device-place|device-boxed|walk|mesh-openings|accurate|accurate-time|tour|pause
                                 scene to set up before --shot fires (walk: with --empty, the Basic flow end to end)
  --theme light|dark             this run's appearance (Settings → Appearance is the saved one); with --shot, the variant to photograph
  --tour-step <n>                with --shotmode tour: photograph only step n (1-based; steps 3+ load the sample, 5+ start the preview run); without it every step lands as <stem>01..12.png plus <stem>-after-skip.png
  --report <path>                 write one PDF report headlessly and exit
  --method-pdf <path>             write the "How WindFlow works" method PDF (no window, no solve) and exit
  --export-turntable <seconds> <path>   render a turntable MP4 and exit
  --occupants <n>                  people the thermal check puts in the test room (default 6)
  --twice                          run the turntable export twice in one process
  --empty                          start with no model loaded
  --edit-device "<name>" field=value   headlessly edit a device property and exit
  --gpu <percent>                  GPU budget for this run (not saved); in --bench it also turns the frame cap on
  --update-now                     (installed copy, testing) check, download and apply an update once without asking; WINDFLOW_UPDATE_SOURCE=<folder> swaps GitHub for a local feed
  --livelink                       listen for the Revit add-in on \\.\pipe\WindFlow.LiveLink for this run
  --import-test <file>             import a mesh file with the dialog defaults, detect rooms and openings, print a summary (also out\import-test.txt) and exit; no window, no solve
  --export-sample <path>           write the sample house as a .windflow.json and exit
  --accurate-start cold|warm       force the Accurate pass's seed (measurement only)
  --accurate-settle <x>            Accurate settle = x times Core's planned warm-up (measurement only)
  --selfcheck                      run the app's own checks without a window and exit (writes out/app-selfcheck.txt)
  --solve --quality preview|accurate|high [--focus room] [--wind 4,270] [--cap min] [--domain compact|full] [--cells n]
          [--thermal [--free-running]] [model.json] [--solve-out path]
                                   headless solve, no window; High runs use the Compact domain (--domain full is not applied to them)
  --compare <A.json> <B.json> [--offsets K] [--compare-out file.csv] [--quality ...] [--wind ...] [--cap ...] [--domain ...] [--cells n]
                                   paired design comparison: A then B on the same K (2-8, default 4) sub-cell grid offsets (x,y only), per room the B-A
                                   difference in fresh-air and total ACH (mean %, min..max over pairs, wins k/K, better/worse/unclear); table to stdout.
                                   The offsets only shift the automatic grid, so --domain full and --quality high are refused (exit 2), as are fewer than
                                   two model paths and --offsets outside 2-8. Each run logs Dx, grid origin and backend (GPU/CPU); a pair whose A and B differ
                                   in any of them, or where a room is missing in one run, is left out and counted; for fresh-air ACH a pair is also left out
                                   when A and B used different age estimators ("different age method"; the building row then uses only pairs where neither
                                   run mixed estimators, else n/a)
```

Settings (last model, window layout, Basic/Expert, GPU use, particle count, live link) persist to
`%LOCALAPPDATA%\WindFlow`.

**Settings → GPU use** (both modes, 25–100 %, default 60 %): the solver runs in short bursts
(≤ 12 ms) and sleeps between them so its measured GPU-busy share matches the setting; the line under the
slider shows the measured share, steps per second and the time until the running average covers one
flow-through. The viewport is capped at 30 fps while solving, at the tier's cap (60 or 30 fps) otherwise, and
5 fps while WindFlow is minimised or in the background. The status strip shows the solver rate under this
budget (steps/s x cells), with the burst peak alongside when they differ: "1400 MLUPS (peak 2814)".

**Startup tier** (Settings → Performance, read-only): once per launch a ~2 s solver run on a small grid and
the render adapter's dedicated VRAM pick the live solver's cell budget (1.5M / 1M / 500k / 250k cells), the
particle count and the idle frame cap (table in `src/WindFlow.App/Tier.cs`; the choice is logged as
`tier: ...`). The particle count then follows the frame rate: one step down (100k, 60k, 30k) after 3 s under
90 % of the frame cap, one step up after 10 s at the cap (twice as long each time a step up had to be taken
back), never above 100k, where more particles add clutter, not information.

**Settings → Particles**: Auto (the default, as above) or a typed number ("250k" and "1.5M" work), which
fixes the count and turns auto-adjust off; clearing the box or pressing Auto turns it back on. Next to it,
"your GPU: up to ~N" is measured, not guessed: GPU timestamps around the particle pass (advection + trail
draw) give its cost at the current count, and N is where that cost, extrapolated linearly, fills 80 % of one
display refresh next to the rest of the frame (logged as `particle limit: ...`; solver-idle readings refine
it, since readings taken while solving share the GPU). Typed values are clamped to N and the line says so.
The 100k ceiling applies to Auto only. `--particles` pins the count for one run.

**Rooms.** Every room row has a name you can tell apart: the Revit name when it is unique, "Hall (north,
24 m²)" when two rooms share one (direction of the room
from the building centre, +Y north; floor area), plain "Room 1", "Room 2" for unnamed rooms detected in a
mesh, with their openings called "Window N" / "Door N". Rows are ordered by storey, then north to south and west to
east. Click a row to see the room as a translucent violet volume (click again or Esc to clear); click a floor in
the viewport to select its row; double-click a row to rename it (Enter keeps, Esc cancels). Renames live on
the loaded model and survive live-link updates of the same document; they are not saved to disk. Opening rows
work the same way (the opening is highlighted as a filled quad); the switch on the row, or a click on the
opening in the viewport, opens or closes it.

**Live link (Revit):** start WindFlow with `--livelink` to listen for the WindFlow Revit add-in (docs/LIVELINK.md). The
status strip reads "live link: waiting for Revit" / "connected · <document>". A model from Revit replaces the
one on screen (the camera stays, devices you added in the app stay), a door/window toggle in Revit toggles
it here, and picking a room, opening or device row selects it in Revit. Test without Revit:
`tools/livelink-fake-revit.ps1`.

## Revit export

```bash
dotnet build src/WindFlow.Revit -p:DeployToRevit=true
```

Copies the thin exporter into `%APPDATA%\Autodesk\Revit\Addins\2026\`. In Revit: **WindFlow → Export**,
select storeys, write `model.json`, open it in WindFlow.App.
