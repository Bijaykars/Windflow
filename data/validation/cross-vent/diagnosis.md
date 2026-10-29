**Which cause dominates (2026-09-28, N = 20 unless noted): the discharge coefficient, not the driving pressure.**
Q/(UH A) is 0.585 against 0.25 measured (x2.34; ln 2.34 = 0.850 is the error being split). Sealed-body
windward Cp at the opening centre is +0.70 in the 4 H domain and +0.69 in the guideline domain. The literature band
is +0.6 to +0.8; Swami & Chandra give 0.6 for normal incidence, and cube wind-tunnel data scatter by +-0.12 (both via
Costola et al. 2009). Leeward Cp is -0.44 in the 4 H domain and -0.38 in the guideline domain, against a band of -0.2 to -0.4.
Sealed dCp over the opening patch is 1.117 in the 4 H domain and 1.047 in the guideline domain.
With that dCp, the measured Q implies an envelope Cd of 0.25 / sqrt(1.047 / 2) = 0.35. WindFlow's envelope Cd is 0.763
in the guideline domain and 0.783 in the 4 H domain. The coupled per-opening Cd is 0.90 at the windward opening and
0.77-0.82 at the leeward one. The sharp-edged textbook value is 0.6-0.65 (Etheridge & Sandberg 1996).
Error shares in ln units:
- Cd: 0.770 (91 %), from ln(0.763 / 0.354) at a literature-midpoint dCp of 1.0.
- Domain: 0.058 (7 %). Moving from 4 H to the guideline domain (5 H upstream, 15 H downstream, 5 H lateral, top 5 H
  above the roof) takes Q from 0.585 to 0.552 and blockage from 3.0 % to 1.9 %.
- dCp: 0.023 (3 %) against the band midpoint. It would be 16 % if the true dCp were at the band's low end, 0.8.
The Cd gap itself has two parts:
- A solver reproducing Cd = 0.61 exactly would still give 0.61 x sqrt(1.047 / 2) = 0.44, i.e. +77 %.
- Karava et al.'s PIV for the same configuration gives 0.40 (Tominaga-Blocken 2015, Section 4.1).
Opening shape: h/H 0.20 -> 0.25 raises Q by 4.6 % (N = 20, 0.585 -> 0.612); h/H 0.20 -> 0.225 raises it by 2.2 %
(N = 40, 0.588 -> 0.601). Both variants are normalised by their own drawn area, which is +6 % and +4 %.
Wall thickness: t = 0.05 H at N = 20 and 0.025 H at N = 40 give the same Q (0.585 and 0.588). The papers do not give it.
Karava's 1:200 twin used 2 mm sheet, t = 0.025 H (Ramponi & Blocken 2012).
The per-opening patch Cp is read 1-2 cells outside the facade, so it depends on the grid: windward patch Cp is 0.48 at N = 20
and 0.40 at N = 40. The envelope Cd and the series Cd are the grid-robust measures.
