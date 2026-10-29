"""Writes media/courtyard-house.windflow.json: WindFlow's own demo building (no third-party model).

An L-shaped courtyard house: a two-storey main wing (16 x 8 m) with a roof overhang, a balcony and an open stair,
a one-storey side wing (7 x 7 m) with a parapet, and a walled courtyard with a pergola. Same conventions as the
sample house (out/sample.windflow.json): X east, Y north, Z up, metres; walls are full boxes and the openings are
cut by the solver; rooms are plan loops with a Z range.

    python tools/make-courtyard-house.py
"""
import json, os, uuid

T, TI = 0.25, 0.12            # exterior / interior wall thickness
L1, L2B, L2T = 3.0, 3.2, 6.0  # ground floor top, upper floor bottom (slab 3.0-3.2), upper floor top
obstacles, openings, rooms = [], [], []
IDX = [0,1,2,0,2,3, 4,6,5,4,7,6, 0,4,5,0,5,1, 1,5,6,1,6,2, 2,6,7,2,7,3, 3,7,4,3,4,0]

def box(x0, x1, y0, y1, z0, z1):
    v = [(x0,y0,z0),(x1,y0,z0),(x1,y1,z0),(x0,y1,z0),(x0,y0,z1),(x1,y0,z1),(x1,y1,z1),(x0,y1,z1)]
    obstacles.append({"Id": uuid.uuid4().hex, "Vertices": [{"X": a, "Y": b, "Z": c} for a,b,c in v], "Indices": IDX})

def wall_x(y, x0, x1, z0, z1, t=T): box(x0, x1, y - t/2, y + t/2, z0, z1)   # wall along X at y
def wall_y(x, y0, y1, z0, z1, t=T): box(x - t/2, x + t/2, y0, y1, z0, z1)   # wall along Y at x

def op(id_, name, level, door, open_, x, y, z, normal, w, h, depth=T):
    openings.append({"Id": id_, "Name": name, "Level": level, "Kind": 1 if door else 0, "IsOpen": bool(open_),
                     "Center": {"X": x, "Y": y, "Z": z}, "Normal": dict(zip("XYZ", normal)),
                     "Width": w, "Height": h, "Depth": depth, "DischargeCoefficient": None})

def room(id_, name, level, x0, x1, y0, y1, z0, z1, occ):
    rooms.append({"Id": id_, "Name": name, "Level": level,
                  "Loops": [[{"X": x0, "Y": y0}, {"X": x1, "Y": y0}, {"X": x1, "Y": y1}, {"X": x0, "Y": y1}]],
                  "ZMin": z0, "ZMax": z1, "Occupants": occ, "VoxelDerived": False, "DetectedVolume": 0})

# ---- site and envelope
box(-2, 18, -2, 17, -0.2, 0)                       # ground plate
wall_x(0, 0, 16, 0, L2T); wall_x(8, 0, 16, 0, L2T)   # main wing south / north (north is interior below the side wing)
wall_y(16, 0, 8, 0, L2T); wall_y(0, 0, 8, 0, L2T)
wall_y(0, 8, 15, 0, 3.8); wall_x(15, 0, 7, 0, 3.8); wall_y(7, 8, 15, 0, 3.8)   # side wing, walls run up as parapet
box(-0.5, 16.5, -1.0, 8.5, L2T, L2T + 0.2)        # main roof with a 1 m south overhang
box(0, 7, 8, 15, L1, L2B)                         # side wing roof
box(9, 16, -1.6, 0, L1, L2B)                      # balcony
# upper floor slab with the stair hole (x 6.5-8.5, y 5.5-7.6)
box(0, 16, 0, 5.5, L1, L2B); box(0, 16, 7.6, 8, L1, L2B); box(0, 6.5, 5.5, 7.6, L1, L2B); box(8.5, 16, 5.5, 7.6, L1, L2B)
# courtyard: garden walls with a gate, a pergola on four posts
wall_y(16, 8, 15, 0, 1.8, 0.2); wall_x(15, 7, 10, 0, 1.8, 0.2); wall_x(15, 11.5, 16, 0, 1.8, 0.2)
for x in (8.5, 10.5, 12.5, 14.5):
    box(x - 0.08, x + 0.08, 8, 12, 2.7, 2.9)       # beams
for x in (8.5, 14.5):
    box(x - 0.1, x + 0.1, 11.9, 12.1, 0, 2.7)      # posts

# ---- ground floor
wall_y(9, 0, 8, 0, L1, TI); wall_x(5, 9, 16, 0, L1, TI); wall_x(12, 0, 7, 0, L1, TI)
room("living", "Living", "L1", 0.15, 8.94, 0.15, 7.85, 0, L1, 4)
room("kitchen", "Kitchen", "L1", 9.06, 15.85, 0.15, 4.94, 0, L1, 2)
room("entry", "Entry", "L1", 9.06, 15.85, 5.06, 7.85, 0, L1, 0)
room("studio", "Studio", "L1", 0.15, 6.85, 8.15, 11.94, 0, L1, 1)
room("guest", "Guest room", "L1", 0.15, 6.85, 12.06, 14.85, 0, L1, 1)
op("w-liv-s1", "Living south window 1", "L1", 0, 1, 2.5, 0, 1.5, (0,-1,0), 1.8, 1.4)
op("w-liv-s2", "Living south window 2", "L1", 0, 1, 6.0, 0, 1.5, (0,-1,0), 1.8, 1.4)
op("w-liv-w", "Living west window", "L1", 0, 1, 0, 4, 1.5, (-1,0,0), 1.4, 1.3)
op("d-liv-yard", "Living courtyard door", "L1", 1, 1, 8.0, 8, 1.1, (0,1,0), 1.6, 2.2)
op("d-liv-kit", "Living to kitchen", "L1", 1, 1, 9, 2.5, 1.05, (1,0,0), 1.0, 2.1, TI)
op("d-liv-studio", "Living to studio", "L1", 1, 1, 3.5, 8, 1.05, (0,1,0), 0.9, 2.1)
op("w-kit-s", "Kitchen south window", "L1", 0, 1, 12.5, 0, 1.5, (0,-1,0), 1.6, 1.2)
op("w-kit-e", "Kitchen east window", "L1", 0, 1, 16, 2.5, 1.5, (1,0,0), 1.2, 1.2)
op("d-kit-entry", "Kitchen to entry", "L1", 1, 1, 10.5, 5, 1.05, (0,1,0), 0.9, 2.1, TI)
op("d-entry-yard", "Entry door", "L1", 1, 1, 12.5, 8, 1.05, (0,1,0), 1.0, 2.1)
op("w-entry-e", "Entry east window", "L1", 0, 1, 16, 6.5, 1.5, (1,0,0), 1.0, 1.1)
op("w-studio-w", "Studio west window", "L1", 0, 1, 0, 10, 1.5, (-1,0,0), 1.4, 1.3)
op("d-studio-yard", "Studio courtyard door", "L1", 1, 1, 7, 10, 1.1, (1,0,0), 2.0, 2.2)
op("d-studio-guest", "Studio to guest room", "L1", 1, 1, 2, 12, 1.05, (0,1,0), 0.9, 2.1, TI)
op("w-guest-n", "Guest room north window", "L1", 0, 1, 3.5, 15, 1.5, (0,1,0), 1.4, 1.3)
op("w-guest-e", "Guest room east window", "L1", 0, 0, 7, 13.5, 1.5, (1,0,0), 1.2, 1.3)

# ---- upper floor
wall_y(5.5, 0, 8, L2B, L2T, TI); wall_y(11, 0, 5, L2B, L2T, TI); wall_y(13, 5, 8, L2B, L2T, TI)
wall_x(5, 5.5, 16, L2B, L2T, TI)
room("bed1", "Bedroom 1", "L2", 0.15, 5.44, 0.15, 7.85, L2B, L2T, 2)
room("bed2", "Bedroom 2", "L2", 5.56, 10.94, 0.15, 4.94, L2B, L2T, 2)
room("bed3", "Bedroom 3", "L2", 11.06, 15.85, 0.15, 4.94, L2B, L2T, 1)
room("landing", "Landing", "L2", 5.56, 12.94, 5.06, 7.85, L2B, L2T, 0)
room("bath", "Bathroom", "L2", 13.06, 15.85, 5.06, 7.85, L2B, L2T, 0)
z2w, z2d = L2B + 1.4, L2B + 1.05
op("w-b1-s", "Bedroom 1 south window", "L2", 0, 1, 2.7, 0, z2w, (0,-1,0), 1.6, 1.3)
op("w-b1-w", "Bedroom 1 west window", "L2", 0, 1, 0, 5, z2w, (-1,0,0), 1.2, 1.3)
op("w-b1-n", "Bedroom 1 north window", "L2", 0, 1, 2.7, 8, z2w, (0,1,0), 1.2, 1.3)
op("w-b2-s", "Bedroom 2 south window", "L2", 0, 1, 7, 0, z2w, (0,-1,0), 1.4, 1.3)
op("d-b2-balc", "Bedroom 2 balcony door", "L2", 1, 1, 10, 0, z2d, (0,-1,0), 1.0, 2.1)
op("d-b3-balc", "Bedroom 3 balcony door", "L2", 1, 1, 13.5, 0, z2d + 0.05, (0,-1,0), 1.6, 2.2)
op("w-b3-e", "Bedroom 3 east window", "L2", 0, 1, 16, 2.5, z2w, (1,0,0), 1.2, 1.3)
op("w-landing-n", "Landing north window", "L2", 0, 1, 8, 8, z2w, (0,1,0), 1.4, 1.3)
op("w-bath-e", "Bathroom east window", "L2", 0, 1, 16, 6.5, z2w, (1,0,0), 0.8, 0.8)
op("w-bath-n", "Bathroom north window", "L2", 0, 0, 14.5, 8, z2w, (0,1,0), 0.8, 0.8)
op("d-b1", "Bedroom 1 door", "L2", 1, 1, 5.5, 6.5, z2d, (1,0,0), 0.85, 2.1, TI)
op("d-b2", "Bedroom 2 door", "L2", 1, 1, 9.5, 5, z2d, (0,1,0), 0.85, 2.1, TI)
op("d-b3", "Bedroom 3 door", "L2", 1, 1, 12, 5, z2d, (0,1,0), 0.85, 2.1, TI)
op("d-bath", "Bathroom door", "L2", 1, 0, 13, 6.5, z2d, (1,0,0), 0.8, 2.1, TI)

model = {
    "Name": "Courtyard house", "NorthDeg": 0, "GroundZ": 0,
    "Storeys": [{"Id": "L1", "Name": "Level 1", "Elevation": 0, "Height": L1, "Analysed": True},
                {"Id": "L2", "Name": "Level 2", "Elevation": L1, "Height": L2T - L1, "Analysed": True}],
    "Obstacles": obstacles, "Openings": openings, "Rooms": rooms, "Sources": [],
    "Env": {"OutdoorTemperature": 20, "IndoorTemperature": 22, "RelativeHumidity": 50, "OutdoorCo2": 420},
    "Heat": [], "Contaminants": [], "Warnings": [],
}
out = os.path.join(os.path.dirname(__file__), "..", "media", "courtyard-house.windflow.json")
with open(out, "w") as f:
    json.dump(model, f, indent=1)
print(f"{os.path.normpath(out)}: {len(rooms)} rooms, {len(openings)} openings, {len(obstacles)} obstacles")
