"""Convert the MINIBAT isothermal Airflow_dynamics.xlsx sheets to measured.csv, then check the round trip.

Input:  raw/Isothermal case/<plane folder>/Airflow_dynamics.xlsx (Mendeley Data doi:10.17632/hp2cx64vw6, CC BY 4.0)
Output: measured.csv, one row per measurement position, every numeric cell copied as Python repr (exact).

status column (our label, not the source's):
  ok        -- a measured position with |V| >= 0.1 m/s
  below0.1  -- non-zero values but |V| < 0.1 m/s: below the probe's stated measurable floor (Kuznik et al. 2008 p. 74-75)
  masked    -- all nine quantities exactly 0: no measurement at this position (not documented; see SOURCE.md)
Rows with no Y/Z/values at all (one in T2) are dropped and counted.

Run: python data/validation/minibat-iso/convert.py   (needs openpyxl)
"""
import csv, math, os, sys
import openpyxl

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw", "Isothermal case")
PLANES = [("M1", "Median plan M1"), ("T1", "Transversal plan T1"), ("T2", "Transversal plan T2"),
          ("T3", "Transversal plan T3"), ("T4", "Transversal plan T4")]
HEAD = ["X", "Y", "Z", "Vx", "Vy", "Vz", "txx", "tyy", "tzz", "txy", "txz", "tyz"]
COLS = ["plane", "x_m", "y_m", "z_m", "vx_mps", "vy_mps", "vz_mps",
        "txx", "tyy", "tzz", "txy", "txz", "tyz", "status"]


def read_sheet(path):
    ws = openpyxl.load_workbook(path, read_only=True, data_only=True).worksheets[0]
    rows = list(ws.iter_rows(values_only=True))
    assert list(rows[2][:12]) == HEAD, (path, rows[2])
    assert rows[3][:3] == ("(m)", "(m)", "(m)"), rows[3]
    full, dropped = [], 0
    for r in rows[4:]:
        if all(isinstance(v, (int, float)) for v in r[:12]):
            full.append([float(v) for v in r[:12]])
        elif any(v is not None for v in r[:12]):
            dropped += 1
    return rows[0][0], full, dropped


def status(r):
    if all(v == 0 for v in r[3:12]):
        return "masked"
    return "ok" if math.sqrt(r[3] ** 2 + r[4] ** 2 + r[5] ** 2) >= 0.1 else "below0.1"


def main():
    out = os.path.join(HERE, "measured.csv")
    sheets = {}
    with open(out, "w", newline="", encoding="utf-8") as f:
        f.write("# MINIBAT isothermal case (Gresse, Merlier & Kuznik 2020, Data in Brief 29:105382; data Mendeley doi:10.17632/hp2cx64vw6, CC BY 4.0)\n")
        f.write("# Converted by convert.py from raw/Isothermal case/*/Airflow_dynamics.xlsx. Coordinates: room frame of the source (m), x across,\n")
        f.write("# y along the jet from the south facade, z up from the floor. v*: mean velocity components (m/s). t**: Reynolds stresses (m^2/s^2):\n")
        f.write("# turbulence, NOT measurement uncertainty. Mean-velocity uncertainty +-0.05 m/s (source). status: see convert.py / SOURCE.md.\n")
        w = csv.writer(f, lineterminator="\n")
        w.writerow(COLS)
        for tag, folder in PLANES:
            title, rows, dropped = read_sheet(os.path.join(RAW, folder, "Airflow_dynamics.xlsx"))
            sheets[tag] = rows
            counts = {}
            for r in rows:
                s = status(r)
                counts[s] = counts.get(s, 0) + 1
                w.writerow([tag] + [repr(v) for v in r] + [s])
            print(f"{tag}: '{title}' -> {len(rows)} rows ({counts}), dropped {dropped} empty row(s)")

    # Round trip: read measured.csv back and compare every value to the spreadsheet, exactly.
    back = {}
    with open(out, encoding="utf-8") as f:
        rd = csv.reader(l for l in f if not l.startswith("#"))
        assert next(rd) == COLS
        for p in rd:
            back.setdefault(p[0], []).append([float(v) for v in p[1:13]])
    n = 0
    for tag, rows in sheets.items():
        assert len(back[tag]) == len(rows), tag
        for a, b in zip(rows, back[tag]):
            assert a == b, (tag, a, b)
            n += 1
    print(f"round trip OK: {n} rows x 12 values identical to the spreadsheets")


if __name__ == "__main__":
    sys.exit(main())
