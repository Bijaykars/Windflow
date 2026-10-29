# Validation data still needed from the user

Open data is fetched by the harness agent with curl (see each case's `SOURCE.md`). What follows is
behind a paywall, not machine-readable, or missing a number the case cannot be built without. No
number in `data/validation/` is estimated (except the MINIBAT housing and extraction-box positions, read off
renders and flagged as such); a case without its inputs is not built.

## Case 3 -- MINIBAT isothermal jet (Gresse, Merlier & Kuznik 2020): nothing needed

Received 2026-09-28: the dataset (all three cases), the Data in Brief paper and Kuznik, Rusaouen & Brau 2008
(in `minibat-iso/raw/`, git-ignored). The "needs confirmation" labels are resolved by K08 Fig. 2: the nozzle centre
is (x0, y0, z0) = (1.55, 0.57, 2.32) m, i.e. 1.55 m = lateral position, 0.57 m = exit plane from the south facade,
0.18 m = axis depth below the ceiling. Still **not dimensioned in any source**: the supply housing and the exact
extraction-box position (both estimated from the renders, see `minibat-iso/SOURCE.md`). Kuznik's 2005 PhD thesis
(INSA Lyon) would have them; not needed to run the case. The case runs as `-- minibat-iso`.

## Case 1 -- Annex 20 2D: nothing needed

Fetched from AAU (see `annex20-2d/SOURCE.md`). Optional: Nielsen, Rong & Olmedo (2010), *The IEA
Annex 20 two-dimensional benchmark test for CFD predictions*, Clima 2010 -- only for context (it
collects later CFD results on this case); no number here depends on it.

## Case 4 -- cross-ventilation (Tominaga & Blocken 2015): nothing needed to run it

Built from the open submitted manuscript (see `cross-vent/SOURCE.md`); the velocities are **digitised** from
its Fig. 9a / Fig. 7 (+-0.025 UH). Would improve it, optional:
- the numeric data behind Fig. 9 (ask the corresponding author; none is deposited anywhere we found);
- the wall thickness of the model (not stated in the paper).
Karava, Stathopoulos & Athienitis (2011), doi:10.1016/j.buildenv.2010.07.022 (PIV, same geometry) is closed
access and figures only -- not needed.

## Data status of the remaining cases (searched 2026-09-28, not built)

| Case | Data | Where | Status |
|---|---|---|---|
| 2 -- IEA Annex 20 3D room (isothermal and heated) | Lemaire (ed.) 1993, *Room air and contaminant flow, evaluation of computational methods*, Annex 20 Subtask-1 summary report (TNO) | AAU's CFD-benchmark site (https://www.en.build.aau.dk/web/cfd-benchmarks) hosts only the 2D case | **not found** online; report is print-only as far as we can tell |
| 5 -- AIJ single building | AIJ CFD guidebook validation database (cases A-G; case A = 1:1:2 building) | https://www.aij.or.jp/jpn/publish/cfdguide/index_e.htm | **open** per AIJ, but the site sits behind an AWS bot challenge (HTTP 202, no content to scripts): **please download case A in a browser** into `data/validation/aij-a/raw/`. Background: Tominaga et al. 2008, doi:10.1016/j.jweia.2008.02.058 (closed). No "Kikumoto et al. 2026" database found |
| 6 -- CBE ceiling fans | Chen, Liu, Gao, Zhang, Arens, Zhao & Liu 2018, *Build. Environ.* 130, doi:10.1016/j.buildenv.2017.12.016 | https://escholarship.org/content/qt37s8h4w4/qt37s8h4w4.pdf | **paper open** (eScholarship), **no numeric dataset found** (DataCite/OpenAlex); values are in figures -- would need digitising |
| 7 -- ASHRAE RP-1009 | Srebric & Chen, *A Method of Test to Obtain Diffuser Data for CFD Modeling of Room Airflow* (ASHRAE RP-1009 final report, 2001); Srebric & Chen 2002, *HVAC&R Research* 8(3), doi:10.1080/10789669.2002.10391442 | ASHRAE bookstore / Taylor & Francis | **paywalled** (both) |
