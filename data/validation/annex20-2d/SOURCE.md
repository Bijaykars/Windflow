# IEA Annex 20 two-dimensional test case (isothermal, case 2D1)

## Citation

- **Specification and data:** Nielsen, P.V. (1990). *Specification of a Two-Dimensional Test Case.*
  IEA Energy Conservation in Buildings and Community Systems, Annex 20: Air Flow Pattern Within
  Buildings. Research Item No. 1.45, Institut for Bygningsteknik, Aalborg Universitetscenter.
  ISSN 0902-7513 R9040, November 1990.
- **Measurements (laser-Doppler anemometry):** Restivo, A. (1979). *Turbulent Flow in Ventilated
  Rooms.* Ph.D. thesis, University of London, March 1979 (reference [1] of the specification).
  See also Nielsen, P.V., Restivo, A. & Whitelaw, J.H. (1978), The velocity characteristics of
  ventilated rooms, *Journal of Fluids Engineering* 100, 291-298.
- **Current description of the benchmark:** Nielsen, P.V., Rong, L. & Olmedo, I. (2010), The IEA
  Annex 20 two-dimensional benchmark test for CFD predictions, Clima 2010 (REHVA World Congress).
  Cited as the brief asked; this paper itself was **not** retrieved or used for any number here.

## Where the files came from (fetched 2026-09-28 with curl)

Landing page: https://www.en.build.aau.dk/web/cfd-benchmarks/two-dimensional-benchmark-test
(the old `homes.civil.aau.dk/pvn/cfd-benchmarks/` address now answers 403; `www.cfd-benchmarks.com`
redirects to the page above).

| file (in `raw/`, not committed) | URL | SHA-256 |
|---|---|---|
| `592302_2d_-benchmark_test_measurements_new.xls` | https://www.aaudxp-cms.aau.dk/media/udffy5eg/592302_2d_-benchmark_test_measurements_new.xls | e6dc80485371e3d076cf0677202958df07d07e9207b787053ea68313bc745a8e |
| `592301_2d_benchmark_test.pdf` (the 1990 specification, scanned) | https://www.aaudxp-cms.aau.dk/media/v0xnnqtw/592301_2d_benchmark_test.pdf | 40b4af4ad2f59051ee22cbc161c054125a45156078d5989051ddea9866652138 |
| `literature-list-2d-benchmark-test.pdf` | https://www.aaudxp-cms.aau.dk/media/ybamfbkg/literature-list-2d-benchmark-test.pdf | 16f41d05b2af3ec84abd159667ce2e1b9975aa41d0bbe1976c43b2dc829ba188 |

```bash
mkdir -p data/validation/annex20-2d/raw && cd data/validation/annex20-2d/raw
curl -sLO https://www.aaudxp-cms.aau.dk/media/udffy5eg/592302_2d_-benchmark_test_measurements_new.xls
curl -sLO https://www.aaudxp-cms.aau.dk/media/v0xnnqtw/592301_2d_benchmark_test.pdf
```

## Terms of use

The AAU page publishes the specification, the measurement spreadsheet and a literature list as an
open benchmark for anyone to compare CFD results against ("it is attractive to a new user to stick
to a geometry used by a number of other researchers"). No explicit licence is stated. We therefore
do **not** redistribute the original files (`raw/` is git-ignored); `measured.csv` holds only the
numeric measurements needed for the comparison, with this attribution. Ask AAU (build@build.aau.dk)
before redistributing the spreadsheet or the report themselves.

## How the numbers in `measured.csv` were obtained

From the **data file**, not digitised from a figure. The spreadsheet (Excel 97 `.xls`, one sheet,
title "Laser-doppler measurements of the isothermal two dimensional test case", author A. J.
Petersen, last saved by P. V. Nielsen 2006) was opened read-only through Excel's COM interface and
exported cell by cell to CSV; a script then copied the four blocks (x = H, x = 2H, y = h/2,
y = H - h/2; columns position, Mean, RMS) into `measured.csv`. Values were rounded to 5 decimals,
and the script asserts that rounding changed no value by more than 1e-9 (the spreadsheet stores
4- and 5-decimal numbers as binary doubles). 106 points: 25 + 25 vertical, 28 + 28 horizontal.

Units and coordinates, all from the specification (pages 1-5):

- `x_over_H`: distance from the inlet wall / H. `y_over_H`: distance measured **downward from the
  ceiling** / H (Fig. 1 of the specification: origin at the top of the inlet wall, y pointing down).
  That is why the x = H profile peaks near y/H = 0.04 (the ceiling wall jet) and why the line labelled
  y = h/2 carries the jet (u/u0 = 1.08 at the inlet) while y = H - h/2 carries the floor return flow.
- For the horizontal lines `y_over_H` holds the nominal value h/(2H) = 0.028 or 1 - 0.028 = 0.972
  (h/H = 0.056, spec eq. 1 and 9); the harness samples at the simulated room's own h/2.
- `u_over_u0`: mean x-velocity / supply velocity. `urms_over_u0`: rms of the x-velocity fluctuation
  / u0. **The rms is turbulence, not measurement uncertainty**; no uncertainty is published. The plots
  draw it as bars and say so.
- Re = u0 h / nu = 5000 (spec eq. 3; nu = 15.3e-6 m^2/s at 20 C, u0 = 0.455 m/s for H = 3 m).
- The LDA model had H = 89.3 mm and **W/H = 1.0**, and the data are on its symmetry plane (spec
  page 5). The specification notes the measured flow "is slightly three-dimensional".

## How WindFlow runs it (see `src/WindFlow.Validation/Annex20TwoD.cs`)

- **3D room, compared on the centre plane.** Our solver is 3D, so the case is a room of width W = H
  with a full-width supply slot and a full-width outlet slot, sampled on the plane y = W/2 -- the
  same W/H = 1 geometry and symmetry plane the measurements were taken on.
- **Reynolds-similar scale, not H = 3 m.** `Voxelizer` clamps a simulation box's Dx to >= 0.1 m, so an
  H = 3 m room could only have 1.7 cells across the 0.168 m slot (the brief asks for >= 4). The case
  is isothermal and fully defined by L/H, h/H, t/H and Re, so it is built at the scale where the slot
  is 4 cells of 0.1 m: H = 7.1 m, L = 21.3 m, h = 0.4 m, t = 1.1 m, u0 = Re nu / h = 0.191 m/s, and
  compared as u/u0 against x/H and y/H. The measurements themselves were taken on an 89.3 mm model
  and transferred to the 3 m specification by the same similarity. Discretised ratios:
  L/H = 3.000, h/H = 0.0563 (spec 0.056), t/H = 0.155 (spec 0.16).
- Inlet: a `Patch` supply `AirSource` (fixed velocity u0, one cell thick, full width) in the first air
  column at the top of the inlet wall. Outlet: an open `Opening` through the 0.2 m outlet wall into an
  outdoor plenum (half a room height long, same cross-section) whose far face is the only open domain
  face; zero wind. `RunOptions.Domain` = room + walls + plenum, so the cell budget sits in the room.
- A grid study at 8 cells across the slot is `-- annex20-2d --slot-cells 8`.
