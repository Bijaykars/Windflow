"""Digitise Tominaga & Blocken (2015) Fig. 9a (u/UH, 7 vertical profiles) and Fig. 7 (centreline u/UH) from the
embedded rasters of the open submitted manuscript (Lirias). Method:
  markers   = connected components left after a morphological opening (disc r = 4 px) of the dark-pixel mask:
              removes the 3-4 px curves / dashed lines / ring markers, keeps the ~11 px filled dots; centroid.
  Fig. 9a x = dot column minus that profile's dashed zero line (a straight-line fit through the 7 detected
              dashed columns, x/D = k/8), scaled by the scale bar: one line spacing (0.125 D) = 0.5 u/UH.
  Fig. 9a z = the measurement heights of Fig. 5 (10, 20, 40, ..., 140, 150 mm), assigned by row order; the
              row -> z straight-line fit residual is reported as a check.
  Fig. 7  y = straight-line fit through the detected horizontal gridlines (u/UH = 0.2, 0.4, 0.6, 0.8) and the
              x-axis (0).
Usage: python digitise.py raw/tominaga2015_submitted.pdf measured.csv   (needs PyMuPDF, numpy, scipy, Pillow)"""
import sys
import numpy as np
from PIL import Image
from scipy import ndimage as nd

PDF, OUT = sys.argv[1], sys.argv[2]
import io, fitz


def page_image(page, which):
    """The embedded raster of a figure, at its native resolution (page numbers 1-based)."""
    doc = fitz.open(PDF)
    xref = doc[page - 1].get_images(full=True)[which][0]
    pix = fitz.Pixmap(doc, xref)
    if pix.n - pix.alpha > 3: pix = fitz.Pixmap(fitz.csRGB, pix)
    return io.BytesIO(pix.tobytes("png"))


def load(img):
    return np.asarray(Image.open(img).convert("L")).astype(float)


def dots(a, thr=110, r=4):
    dark = a < thr
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    op = nd.binary_opening(dark, structure=(xx * xx + yy * yy) <= r * r)
    lab, n = nd.label(op)
    res = []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        w, h = xs.max() - xs.min() + 1, ys.max() - ys.min() + 1
        if 60 <= len(xs) <= 400 and 0.6 < w / h < 1.6 and max(w, h) < 24:
            res.append((float(xs.mean()), float(ys.mean())))
    return res


def runs(idx):
    """Group consecutive integers, return group centres."""
    groups, cur = [], [idx[0]]
    for v in idx[1:]:
        if v == cur[-1] + 1: cur.append(v)
        else: groups.append(cur); cur = [v]
    groups.append(cur)
    return [float(np.mean(g)) for g in groups]


# ---------------- Fig. 9a ----------------
a = load(page_image(17, 0))          # Fig. 9 (a and b side by side)
dark = a < 110
cols = dark[110:480, :690].mean(axis=0)
dash = [c for c in runs(np.nonzero((cols > 0.25) & (cols < 0.8))[0]) if 180 < c < 610]
assert len(dash) == 7, dash
k = np.arange(7)
b, a0 = np.polyfit(k, dash, 1)            # x0_k = a0 + b k
x0 = a0 + b * k
pxPerU = b / 0.5
d9 = [d for d in dots(a) if d[0] < 690 and d[1] < 485]
assert len(d9) == 63, len(d9)
rows = sorted(d9, key=lambda d: d[1])
zmm = [150, 140, 120, 100, 80, 60, 40, 20, 10]
fig9 = []
rowY = []
for j, z in enumerate(zmm):
    row = sorted(rows[7 * j:7 * j + 7])
    ys = [d[1] for d in row]
    assert max(ys) - min(ys) < 3, ys
    rowY.append(np.mean(ys))
    for kk, d in enumerate(row):
        fig9.append((kk, z, (d[0] - x0[kk]) / pxPerU, d[0], d[1]))
cz = np.polyfit(zmm, rowY, 1)
zres = np.array(rowY) - np.polyval(cz, zmm)
print(f"Fig 9a: dashed zero lines at {np.round(dash, 1)} (fit spacing {b:.2f} px, max residual {np.abs(np.array(dash) - x0).max():.2f} px)")
print(f"        {pxPerU:.2f} px per u/UH; z rows fit {cz[0]:.3f} px/mm, max residual {np.abs(zres).max():.2f} px = {np.abs(zres).max() / abs(cz[0]):.2f} mm")
print(f"        z/H=0 at row {np.polyval(cz, 0):.1f}, z/H=1 at row {np.polyval(cz, 160):.1f} (frame lines centred at ~97.5 / ~94)")

# ---------------- Fig. 7 ----------------
f = load(page_image(16, 1))          # Fig. 7
d7 = sorted(d for d in dots(f) if d[0] > 150)
# gridlines: long light-grey horizontal rows inside the plot; axis line: long dark row.
grey = ((f > 150) & (f < 235))[:, 200:880].mean(axis=1)
darkrow = (f < 110)[:, 200:880].mean(axis=1)
gl = runs(np.nonzero(grey > 0.6)[0])
ax = runs(np.nonzero(darkrow > 0.6)[0])
print(f"Fig 7: gridline rows {np.round(gl, 1)}, axis rows {np.round(ax, 1)}, {len(d7)} filled dots")
yl = [423.0, 325.0, 228.5, 130.5, 33.5]           # u/UH = 0, .2, .4, .6, .8 (axis line; gridline centres above)
cy = np.polyfit([0, 0.2, 0.4, 0.6, 0.8], yl, 1)
print(f"        {cy[0]:.2f} px per u/UH, max gridline residual {np.abs(np.polyval(cy, [0, .2, .4, .6, .8]) - yl).max():.2f} px")
d7 = [d for d in d7 if not (d[1] < 110 and d[0] > 500)]    # drop the legend marker (top-right box)
assert len(d7) == 7, d7
fig7 = [(kk, (d[1] - cy[1]) / cy[0]) for kk, d in enumerate(sorted(d7))]
z80 = {r[0]: r[2] for r in fig9 if r[1] == 80}
diff = [fig7[kk][1] - z80[kk] for kk in range(7)]
print("x/D   fig7   fig9a(z=80mm)  diff")
for kk in range(7): print(f"{(kk + 1) / 8:.3f} {fig7[kk][1]:.3f} {z80[kk]:.3f} {diff[kk]:+.3f}")
print(f"mean diff {np.mean(diff):+.4f}, rms {np.sqrt(np.mean(np.square(diff))):.4f}")
with open(OUT, "w", newline="\n") as o:
    o.write("# Tominaga & Blocken 2015, Build. Environ. 92:452-461, doi:10.1016/j.buildenv.2015.05.026 -- unsheltered building,\n")
    o.write("# vertical centre section. Digitised from Fig. 9a (fig=9a) and Fig. 7 (fig=7) of the open submitted manuscript;\n")
    o.write("# see SOURCE.md for the method and uncertainty. x_over_D from the windward facade, z_over_H from the floor.\n")
    o.write("fig,x_over_D,z_over_H,u_over_UH\n")
    for kk, z, u, _, _ in sorted(fig9, key=lambda r: (r[0], r[1])):
        o.write(f"9a,{(kk + 1) / 8:.4f},{z / 160:.5f},{u:.3f}\n")
    for kk, u in fig7:
        o.write(f"7,{(kk + 1) / 8:.4f},0.50000,{u:.3f}\n")
print("wrote", OUT)
print("note: Fig. 9a scale bar ticks (-0.5 .. 0.5) span 134 px = 5 % more than two zero-line spacings (127.6 px); the")
print("      spacing scale is used because Fig. 7 agrees with it (fit slope 127 px per u/UH at the 7 common points).")
