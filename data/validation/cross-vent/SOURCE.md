# Cross-ventilation of a generic isolated building (Tominaga & Blocken 2015, unsheltered)

## Citation

- **Measurements, geometry, inflow:** Tominaga, Y. & Blocken, B. (2015). Wind tunnel experiments on
  cross-ventilation flow of a generic building with contaminant dispersion in unsheltered and sheltered
  conditions. *Building and Environment* 92, 452-461. doi:10.1016/j.buildenv.2015.05.026.
  Atmospheric boundary-layer wind tunnel, Niigata Institute of Technology; split-fibre probe (3 components,
  60 s at 100 Hz) and tracer-gas flow rate.
- Same geometry as configuration E1 of Karava, Stathopoulos & Athienitis (2011), *Building and Environment*
  46, 266-279, doi:10.1016/j.buildenv.2010.07.022 (PIV) -- **not used**: closed access, and its data are
  figures only. Ramponi & Blocken (2012), *Building and Environment* 53, 34-48,
  doi:10.1016/j.buildenv.2012.01.004, validated CFD against Karava; its open preprint was read for context only.
- **Opening-flux reference (used for the primary Q/(UH A) check, task F2):** Hwang, S. & Gorle, C. (2022).
  Large-eddy simulations of wind-driven cross ventilation for the same Karava geometry. arXiv:2204.00786
  (Part 1; Part 2 investigates Cd separately, not fetched). Open access preprint, fetched 2026-09-28 with curl
  from https://arxiv.org/pdf/2204.00786, no e-mail sent. SHA-256
  `c59bca94d5cebef52010317596d4e96228cfea4c154b93a5efbf59436f2eb813`.

## Opening-flux Q/(UH A): 0.40-0.47, not the 0.25 tracer value (diagnosis.md, F2)

The 0.25 tracer-gas value (Section 4.1, digitised text below) measures **air exchange**, not the flow through
one opening; WindFlow's `Q_in_over_UH_A` (u.n over a throat) is the opening-flux quantity, and the correct
references are:

- **Karava PIV, ~0.40.** Tominaga & Blocken 2015 Section 4.1 (p. verso of `tominaga2015_submitted.pdf`, "Flow
  and concentration fields for sheltered building" / 4.1 "Air flow rate"), exact quote: *"The dimensionless air
  flow rate Q/UHAinlet for the unsheltered building was 0.25. This value was 37% smaller than that for the same
  configuration reported in Karava et al. [7]."* -- so Karava's value = 0.25 / (1 - 0.37) = 0.397, rounded 0.40.
  The same passage: *"the air flow rate reported in ref. [7] was calculated as U_opening . A, where Uopening is
  the area-averaged velocity downstream (1 cm) the inlet opening measured on a horizontal mid-plane (using the
  PIV technique), and A is the geometric area of the opening"* -- i.e. computed the same way as WindFlow's own
  `Q_in_over_UH_A` (opening-normal velocity x opening area), not the tracer-gas method. Geometry: Tominaga &
  Blocken built their model "so that the results could be compared" with Karava et al. (Section 2.1, geometry
  described as geometrically identical at a different scale, 1:100 vs Karava's 1:200) -- same plan, opening
  size ratio and position; not an independent building.
- **Hwang & Gorle 2022 LES, 0.44-0.47.** arXiv 2204.00786, Section 5.1 (grid sensitivity), verified against
  `hwang_gorle_2204.00786.pdf.txt` (extracted text, this directory): *"Comparing the mean values of
  Qu,ins(t)/(Uref.A) for the coarse, base, and fine meshes, the values are 0.470, 0.452 and 0.462, respectively;
  the corresponding values for Qp,avg/(Uref.A) are 0.445, 0.440 and 0.445."* Earlier in the same section:
  *"The results obtained using the different LESs and the PIV measurements are in close (3%) agreement."*
  Cd = 0.61 (still-air discharge coefficient) is used only in the pressure-based `Qp,avg` calculation (their
  eq. 9), not the velocity-based `Qu,ins`, which is the number closer to a direct measurement of throat flow.
  Geometry: Hwang & Gorle simulate "the reference experiment by Karava, et al." directly (their Section 2,
  Uref = 6.6 m/s at building height, z0 = 0.025 mm model scale) -- the same building as the PIV reference above,
  not Tominaga-Blocken's own model.
- **WindFlow's choice.** Primary band QFluxLow = 0.40 (Karava PIV, the actual measurement for this geometry) to
  QFluxHigh = 0.47 (Hwang & Gorle's coarse-mesh, the upper end of their reported spread), +-10% around each
  bound (see `CrossVentTB15.cs` `QFluxLow`/`QFluxHigh` and `FlowSummary`). The 0.25 tracer value stays in the
  report as a separately labelled, non-scored "air exchange (tracer)" line -- it is not comparable to opening
  flux (see the quote above: air exchange integrates net through-flow after subtracting recirculation, which
  is smaller than either opening's one-way flux by construction).

## Where the files came from (fetched 2026-09-28 with curl; found through OpenAlex, no e-mail sent)

| file (in `raw/`, not committed) | URL | SHA-256 |
|---|---|---|
| `tominaga2015_submitted.pdf` (submitted manuscript, KU Leuven Lirias) | https://lirias.kuleuven.be/retrieve/cc03d95f-3a9a-4821-8d55-9c3fc80fef32 | 98b7723f0573a297a2c79f327a023e9f45df0b89a84655bfd07c4b939acf6b87 |
| `ramponi2012_submitted.pdf` (context only) | https://lirias.kuleuven.be/retrieve/96f84840-5048-47a4-84fe-0e0ddbe30a28 | 767849039a2e4d0c508abace226683380b980b5551022b880ae06953ee8eae04 |
| `tominaga2016_submitted.pdf` (opening-position follow-up, JWEIA 155, not used) | https://pure.tue.nl/ws/files/22888138/tomiwind2016.pdf | 008e463f28da888f4384bb2698c14fa647f4f1c12e00d4947a58f64318b6ff98 |
| `costola2009.pdf` (Cp data overview, Build. Environ. 44:2027-2036, doi:10.1016/j.buildenv.2009.02.006; diagnostics only) | https://pure.tue.nl/ws/files/3237341/672163548140087.pdf | 0a496f4d97679548dadfa71a8d6c479efab4e6b1ca6318ab29e415d07996f24d |

Landing pages: https://lirias.kuleuven.be/handle/123456789/508326 and
https://research.tue.nl/en/publications/13ab8b0d-3c23-4696-b5ea-d58d9a1a3e97 (the TU/e portal answers 403 to scripts).

## Terms of use

The repositories list the manuscript as open access ("other-oa") with **no licence stated**; the published
version is Elsevier (user licence). We therefore do not redistribute the PDFs (`raw/` is git-ignored).
`measured.csv` holds only numbers we digitised from its figures, with this attribution.

## What is used, and how it was obtained

**Stated in the text or drawn with dimensions (exact):**
- Model W x D x H = 0.20 x 0.20 x 0.16 m (1:100). Two openings, 0.092 m wide x 0.036 m high, centred in the
  windward and leeward facades, centre at z = 0.08 m (Fig. 1a). Wall thickness is **not given** -- not in
  this paper nor in the 2016 opening-position follow-up (Tominaga & Blocken 2016, JWEIA 155; the sheltered case is in the 2015
  paper itself, there is no separate sheltered paper). The nearest statement: Karava et al.'s geometrically identical 1:200
  model (H = 80 mm) was built from 2 mm PMMA sheet (Ramponi & Blocken 2012 preprint, Section 2), i.e. t = 0.025 H; if
  Tominaga-Blocken used the same 2 mm sheet at 1:100, t = 0.0125 H. WindFlow's walls are 1 cell = 0.05 H (N = 20) or 0.025 H (N = 40).
- Approach flow u/UH = (z/H)^0.25 (eq. 1), k/UH^2 = 0.033 exp(-0.32 z/H) (eq. 2); UH = 4.3 m/s at z = H,
  building Re ~ 45,000; z0 = 7 mm model scale. Wind normal to the openings.
- Measurement points (Fig. 5): x = 25, 50, ..., 175 mm from the windward facade, z = 10, 20, 40, ..., 140,
  150 mm, on the vertical centre section.
- Flow rate Q/(UH A_inlet) = **0.25**, uncertainty about +-7 % (tracer gas, Section 2.3 and 4.1).
- No surface pressure coefficients are reported.

**Digitised (`measured.csv`, script `digitise.py`, run `python digitise.py raw/tominaga2015_submitted.pdf measured.csv`):**
- `fig=9a`: mean u/UH at the 63 points of Fig. 9a (7 verticals x 9 heights). `fig=7`: the 7 "present exp."
  points of Fig. 7 (u/UH on the centreline z = H/2; the same points as Fig. 9a at z/H = 0.5).
- Method: the figures are rasters (Fig. 9: 1386 x 576 px, Fig. 7: 907 x 551 px), read at native resolution
  from the PDF. Markers are the connected components left after a morphological opening (disc, r = 4 px) of
  the dark-pixel mask, which removes curves, dashed lines and hollow markers; centroid per marker.
  Heights come from Fig. 5 (row order; the row-to-height line fits to 0.3 mm). u/UH in Fig. 9a = marker
  column minus that profile's dashed zero line (a straight-line fit through the 7 detected zero lines, 0.7 px
  max residual), with one zero-line spacing (0.125 D, 63.8 px) = 0.5 u/UH. Fig. 7 is read against its
  gridlines (0.45 px max residual).
- **Uncertainty added by digitising:** Fig. 9a's own scale bar spans 134 px for 1.0 u/UH, 5 % more than two
  zero-line spacings (127.6 px); the spacing scale is used because Fig. 7 agrees with it (a free fit gives 127
  px). At the 7 common points Fig. 7 reads 0.013 higher on average (max 0.020) than Fig. 9a. We therefore
  quote **+-0.025 u/UH** absolute (drawn as bars), plus a possible 5 % scale error. No uncertainty is
  published for the velocities; the flapping jet (Section 3.1) makes the mean itself sensitive.

## Three experimental flow-rate references that disagree (R2-02)

| Source | Q/(UH A_inlet) | Note |
|---|---|---|
| Tominaga & Blocken 2015, Sect. 4.1 (tracer gas) | 0.25 | the value this SOURCE.md works from |
| Tominaga & Blocken 2016, JWEIA 155, Table 1, configuration E (centre openings, same model) | 0.25 | same model, same value |
| van Hooff, Blocken & Tominaga 2017, B&E 114, Table 3, p.157 "Experiments [65]" | 0.50 | [65] is Tominaga & Blocken 2015 itself |
| Karava PIV / Hwang & Gorle 2022 LES (derived band, see above) | 0.40-0.47 | opening-flux quantity, computed like WindFlow's |

WindFlow's 0.454 is +82 % against 0.25, -9 % against 0.50, and inside the band. The authors (Tominaga, van Hooff) have not been asked which value and normalisation is correct.

## The hit-rate tolerance W (R2-01, R2-09)

W = 0.05 u/UH is our choice, not a published number. The only published figures are the digitisation error (~0.025 UH here) and the facility's "within approximately 10 %"
velocity uncertainty (Tominaga & Blocken 2016, Sect. 2.2, p.75, same probe). On the recorded `smirnov-windtu` run q is 0.365 at W = 0, 0.540 at W = 0.025 and 0.667 at W = 0.05
(42 of 63 hits; 41.6 needed for 0.66), and the largest block-mean standard error (0.051 UH) is as large as W: the verdict is borderline.

## How WindFlow runs it (see `src/WindFlow.Validation/CrossVentTB15.cs`)

- **Product path.** A `BuildingModel` of wall and roof box meshes with two open `Window` openings, one room
  loop, a `DomainBox` (see Domain below), and the `RunOptions` power-law wind (exponent 0.25) from -X onto the
  windward facade. The building floor is the solid ground.
- **Scaling.** Geometric similarity at H = N cells of 0.1 m (default N = 20: H = 2.0 m, W = D = 2.5 m, x12.5).
  At the 0.1 m Dx floor the 0.036 m opening can only reach 4 cells at this scale. Cells cannot hold the
  exact opening: it is **4 x 13 cells (h = 0.200 H, w = 0.52 W) vs 0.225 H x 0.46 W, with the area kept
  (0.130 vs 0.129 H^2)** and the centre at z = H/2 exactly. Walls and roof are 1 cell (0.05 H). The power law
  is imposed as u = U10 (z/10 m)^0.25 with U10 = UH (10 m/H)^0.25; the solver holds u constant below
  z = 0.5 m (0.25 H), where the measured incident profile is itself ~0.7 UH (Fig. 4).
- **Reynolds number.** UH = 0.3375 m/s makes the nominal building Re 45,000, as measured. The flow is
  sharp-edged and Reynolds-independent above about 1.1e4, but the solver's viscosity floor (`LbmSession`'s
  `ViscosityFloor`) dominates the molecular value here, so the effective Re before the Smagorinsky term is
  about 2,050 (N = 20, guideline domain) or 4,400 (N = 40), below that threshold. Computed from the actual
  `LbmSession.TimeStepSeconds`/`LatticeViscosity`/`VelocityScale` (task F2; was a hard-coded 0.08/5e-4 estimate
  before), in `CheckField` (after the run, once the session exists) and reported in metrics.json.
- **Domain (default since task F2: guideline, `--domain guide`).** Best-practice margins (Franke et al. 2007
  COST 732; Tominaga et al. 2008 AIJ): 5 H upstream, 5 H lateral, top 5 H above the roof, 15 H downstream;
  N = 20 blockage 1.9 %. `--diag <name> --domain auto` (a diagnostic variant only) uses Voxelizer.Build's own
  whole-building margin instead (8 m = 4 H each side, top at 9 m = 4.5 H, blockage 3.0 %) -- see diagnosis.md:
  moving from that 4 H domain to the guideline one takes Q/(UH A) from 0.585 to 0.552.
- **Not modelled:** inflow turbulence (the inflow is steady; the measured k/UH^2 is ~0.03 at z = H), ground
  roughness (the ground is smooth), and the 8 mm gas outlet in the floor.
- **Compared:** u/UH on the 7 verticals (overall metrics); the Fig. 7 centreline as a separate plot, not in
  the overall. Flow = u.n over each opening's carved throat (`Analysis.ThroatCells`), block-averaged, over
  UH x the drawn opening area, against the opening-flux band 0.40-0.47 (Karava PIV to Hwang & Gorle 2022 LES,
  +-10 % of each bound, see above) as the primary flow-rate check; the tracer-gas 0.25 is reported separately,
  not scored (it measures air exchange, a different quantity -- see "Opening-flux Q/(UH A)" above).
