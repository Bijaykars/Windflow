using System.Numerics;
using WindFlow.Core;

namespace WindFlow.App.Import;

/// <summary>PLAN_V2 4.2: openings for a plain mesh. A mesh has no window objects, only HOLES: this finds the
/// holes on the same unsealed voxel grid <see cref="RoomDetect"/> uses, classifies each one, and hands back
/// ordinary <see cref="Opening"/>s, so the row switch, the viewport click and the highlight work on them
/// exactly as on Revit openings (closing one fills its box solid; see Voxelizer.ApplyOpening).
/// <para><b>Method.</b> Per storey, the clear band is the run of layers around the 1.1 m plane where under half
/// the covered footprint is solid (floor top to ceiling soffit, slabs excluded). A covered column that is solid
/// somewhere in that band AND air somewhere in it is a wall with a hole in it (sill, head or both); its air
/// cells in the band are aperture cells. 6-connected aperture cells form one gap. The wall normal is the
/// horizontal axis along which the gap's faces meet open air; a gap thicker than
/// <see cref="MaxThicknessM"/> along it is not a hole in a wall (a low beam, a bulkhead) and is dropped, and neither is one with no wall
/// at the ends of its width (the air under a roof overhang). IMP-B: the band never reaches below the storey's own floor; a sloped roof or gable
/// (a soffit that follows a ramp, <see cref="SlopeMinRunM"/>) is no wall and the sky above a wall's top is no aperture (D9); two gaps of the same
/// width and height on the two sides of a pane or frame in one wall are one opening (D8b); a stack of short pieces from the floor is windows (D8a);
/// and a floor-to-ceiling notch with no sill and no head, in a wall that runs on at least <see cref="FullHeightJambM"/> beyond it, is a gap too.
/// Rooms are then detected with every gap filled solid (so a doorway no longer joins two rooms), and each gap
/// walks out along its normal to find what is on either side: a room, the outdoors (air with nothing above
/// it) or nothing known. A gap with the same space on both sides (air over a table, a hole in a garden wall)
/// is not an opening.</para>
/// <para><b>Merge rule (mullions).</b> Two gaps in the same wall (same normal axis, overlapping through the
/// wall) are one opening when at most <see cref="MergeGapCells"/> blocked cell separates them, side by side
/// with their heights overlapping (a mullion) or one above the other with their widths overlapping (a
/// transom). Its size is then the outer size, mullion included. The ceiling of this rule: a thin mullion that
/// straddles a cell face marks TWO cells solid and the two panes stay two openings; finer cells fix it.</para>
/// <para><b>Size.</b> Conservative voxelisation marks every cell a reveal face touches, so a hole of width w
/// leaves on average w/Dx − 1 whole air cells, and the count alone is good to a cell or more (S3-02: a 0.7 m window
/// read 0.51 m at 257 mm cells). The air cells only say where to look: the reveal faces lie in the solid cell just
/// beyond each end, so the width and height (and centre) of an opening are the gaps between the mesh's own
/// reveal planes found there, exact whatever the grid. Where the mesh has no such plane (or none is passed) a side
/// falls back to the cell estimate, 0.49 Dx past the last air cell, i.e. (n + 0.98)·Dx overall: unbiased to
/// within the 0.02 cell kept back so the open-state carve never eats the jamb cells either side. The class table
/// below still reads the cell estimate.</para>
/// <para>What it cannot see: glazing modelled as a closed pane (no hole, no opening), a floor-to-ceiling passage with no wall running on
/// beside it (an open-plan pass: it reads as one space), a hole in a sloped roof (a skylight), and anything in
/// a wall that is not axis-aligned to within the staircase the voxels make of it.</para></summary>
public static class OpeningDetect
{
    // ---------------------------------------------------------------- classification table
    //  class    | sill above floor   | size                         | Opening.Kind | named
    //  door     | < 0.10 m           | height >= 1.8 m              | Door         | "Door n"
    //  vent     | any                | longest side < 0.40 m        | Opening      | "Vent n"
    //  vent     | > 1.50 m           | any (a high-level grille)    | Opening      | "Vent n"
    //  window   | 0.30 - 1.50 m      | longest side >= 0.40 m       | Window       | "Window n"
    //  opening  | anything else (a sill of 0.1-0.3 m, a low door-height hatch) | Opening | "Opening n"
    // Rules are tried top to bottom; the first that fits wins. Sill and size are measured on the grid, so each
    // is good to about one cell: at 255 mm cells a door sill reads 0 exactly, a window sill 0.26 m or more.
    public const float DoorMaxSill = 0.10f, DoorMinHeight = 1.8f;
    public const float WindowMinSill = 0.30f, WindowMaxSill = 1.50f;
    public const float VentMaxSide = 0.40f;
    /// <summary>D4: the narrowest floor-to-ceiling notch in a wall line that is taken for an opening.</summary>
    public const float FullHeightMinWidthM = 0.5f, FullHeightJambM = 1.0f;
    /// <summary>D9: a soffit that stays within one or two layers of its neighbour column for this long and ends this far from where it began, is a sloped roof
    /// (a round window head or an arch does the same over half its width at most, and a flat soffit ends where it began).</summary>
    public const float SlopeMinRunM = 1.5f, SlopeMinRiseM = 0.15f;
    /// <summary>D8a: the tallest a casement in a stack between horizontal mullions can be.</summary>
    public const float StackMaxPieceHeight = 1.0f;

    /// <summary>Deepest a hole in a wall can be, metres along its normal (IMP-B D4: it was 4 cells, 1.0 m at 250 mm but 0.2 m at 50 mm).</summary>
    public const float MaxThicknessM = 0.8f;
    /// <summary>The widest or tallest hole in a wall taken for one: air further than this from a solid cell on either side is open space, not a reveal.</summary>
    public const float HoleMaxWidthM = 8f;
    /// <summary>A solid cell whose solid neighbourhood is at least SlabMinRunM along both horizontal axes and no thicker than SlabMaxThickM vertically is part of a slab (a roof, a ledge), not a wall.</summary>
    public const float SlabMinRunM = 0.4f, SlabMaxThickM = 0.3f;
    /// <summary>The merge rule: pieces separated by at most this many blocked cells are one opening.</summary>
    public const int MergeGapCells = 1;
    /// <summary>Pieces stacked one above the other are one opening only when their widths agree to this many cells at each end.</summary>
    public const int TransomWidthSlackCells = 2;
    /// <summary>How far each side is searched for a room or the outdoors, metres.</summary>
    public const float SideWalkM = 2f;

    public enum GapClass { Door, Window, Vent, Opening }

    public static GapClass Classify(float sill, float width, float height)
    {
        float longest = MathF.Max(width, height);
        if (sill < DoorMaxSill && height >= DoorMinHeight) return GapClass.Door;
        if (longest < VentMaxSide) return GapClass.Vent;
        if (sill > WindowMaxSill) return GapClass.Vent;
        if (sill >= WindowMinSill) return GapClass.Window;
        return GapClass.Opening;
    }

    /// <summary>One hole in one wall, in grid cells.</summary>
    public sealed class Gap
    {
        public int Storey, KFloor, Pieces = 1;
        public bool[] Covered = [];
        public List<int> Cells = new();
        /// <summary>The layer range of each piece merged into this gap (D8a: a stack of short pieces is a stack of casements, not a door).</summary>
        public List<(int Z0, int Z1)> Rows = new();
        public int X0 = int.MaxValue, X1 = int.MinValue, Y0 = int.MaxValue, Y1 = int.MinValue, Z0 = int.MaxValue, Z1 = int.MinValue;
        /// <summary>True: the wall runs along Y and the opening faces ±X.</summary>
        public bool NormalX;

        public void Add(int i, int x, int y, int z)
        {
            Cells.Add(i);
            X0 = Math.Min(X0, x); X1 = Math.Max(X1, x);
            Y0 = Math.Min(Y0, y); Y1 = Math.Max(Y1, y);
            Z0 = Math.Min(Z0, z); Z1 = Math.Max(Z1, z);
        }

        public (int A0, int A1) Along => NormalX ? (Y0, Y1) : (X0, X1);
        public (int N0, int N1) Across => NormalX ? (X0, X1) : (Y0, Y1);
    }

    /// <summary>Gaps on one storey; <paramref name="kLowest"/> is the layer of the storey's elevation, the lowest the clear band may reach. <paramref name="covered"/> is RoomDetect's "solid somewhere above" map at
    /// <paramref name="kPlane"/> (the 1.1 m plane): it is the storey's footprint and the outdoors test.</summary>
    public static List<Gap> Find(VoxelGrid grid, int storey, string storeyName, int kPlane, bool[] covered, List<string> notes, int kLowest = 0, int kHighest = int.MaxValue)
    {
        int nx = grid.Nx, ny = grid.Ny;
        float Frac(int k)
        {
            int n = 0, s = 0;
            for (int f = 0; f < covered.Length; f++)
            {
                if (!covered[f]) continue;
                n++;
                if (grid.Cell[grid.Index(f % nx, f / nx, k)] == VoxelGrid.Solid) s++;
            }
            return n == 0 ? 1f : s / (float)n;
        }
        if (Frac(kPlane) >= 0.5f) { notes.Add($"{storeyName}: no clear band at the 1.1 m plane, openings not searched"); return []; }
        int kF = kPlane, kC = kPlane;
        while (kF - 1 >= kLowest && Frac(kF - 1) < 0.5f) kF--;   // IMP-B: never below the storey's own floor (a slab under half the footprint let the band run down to the ground)
        while (kC + 1 < grid.Nz && kC + 1 <= kHighest && Frac(kC + 1) < 0.5f) kC++;

        // A horizontal slab inside the band (a porch or lean-to roof, a ledge, a mezzanine, a strip of floor that does not cover half the footprint) is no wall and
        // no soffit: its air would join every hole beside it into one wide gap, and a roof that ends at a window head would call the window's columns sloped.
        // A solid cell is slab when the solid around it runs at least SlabMinRunM along BOTH horizontal axes (a wall is long one way and thin the other) and the
        // column through it is solid for no more than SlabMaxThickM (a wall is solid from floor to head).
        int layerCells = nx * ny, bandLayers = kC - kF + 1;
        int slabRun = Math.Max(2, (int)MathF.Ceiling(SlabMinRunM / grid.Dx - 1e-3f));
        int slabThick = Math.Max(1, (int)MathF.Floor(SlabMaxThickM / grid.Dx + 1e-3f));
        var slab = new bool[layerCells * bandLayers];
        {
            var runX = new int[layerCells];
            var runY = new int[layerCells];
            bool Sol(int x, int y, int k) => grid.Cell[grid.Index(x, y, k)] == VoxelGrid.Solid;
            for (int k = kF; k <= kC; k++)
            {
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx;)
                    {
                        int x0 = x; bool sol = Sol(x, y, k);
                        while (x < nx && Sol(x, y, k) == sol) x++;
                        for (int xx = x0; xx < x; xx++) runX[y * nx + xx] = x - x0;
                    }
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny;)
                    {
                        int y0 = y; bool sol = Sol(x, y, k);
                        while (y < ny && Sol(x, y, k) == sol) y++;
                        for (int yy = y0; yy < y; yy++) runY[yy * nx + x] = y - y0;
                    }
                for (int f = 0; f < layerCells; f++)
                {
                    int x = f % nx, y = f / nx;
                    if (!Sol(x, y, k) || runX[f] < slabRun || runY[f] < slabRun) continue;
                    int n = 1;   // the column's solid run through this layer, counted up to one past the limit
                    for (int kk = k - 1; kk >= 0 && n <= slabThick && Sol(x, y, kk); kk--) n++;
                    for (int kk = k + 1; kk < grid.Nz && n <= slabThick && Sol(x, y, kk); kk++) n++;
                    if (n <= slabThick) slab[(k - kF) * layerCells + f] = true;
                }
            }
        }
        bool WallSolid(int f, int k) => grid.Cell[grid.Index(f % nx, f / nx, k)] == VoxelGrid.Solid && !slab[(k - kF) * layerCells + f];

        // Wall-with-a-hole columns: solid (not slab) somewhere in the clear band and air somewhere in it.
        // IMP-B D9: a sloped roof or gable is not a wall. Per column, the soffit is the bottom layer of the topmost solid run in the band (-1: no
        // solid); a column whose soffit steps by one or two layers against a neighbour's is on a slope (a door head is flat, the jambs beside it
        // step by the whole door height) and is no wall. The air above the top solid of a column (the sky over a gable) is no aperture either.
        var soffit = new int[nx * ny];
        var topSolid = new int[nx * ny];   // the highest solid layer of the whole column (the lid layer is air): above it is sky
        for (int f = 0; f < soffit.Length; f++)
        {
            soffit[f] = -1;   // every column, covered or not: a roof's overhang is not covered at the 1.1 m plane but is part of the slope
            int top = grid.Nz - 1;
            while (top >= 0 && grid.Cell[grid.Index(f % nx, f / nx, top)] != VoxelGrid.Solid) top--;
            topSolid[f] = top;
            int k = Math.Min(kC, top);
            while (k >= kF && !WallSolid(f, k)) k--;
            if (k < kF) continue;
            while (k - 1 >= kF && WallSolid(f, k - 1)) k--;
            soffit[f] = k;
        }
        bool Sloped(int f)
        {
            if (soffit[f] < 0) return false;
            int x = f % nx, y = f / nx;
            foreach (var (ddx, ddy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int run = 0, last = soffit[f];
                for (int cx = x + ddx, cy = y + ddy; cx >= 0 && cy >= 0 && cx < nx && cy < ny; cx += ddx, cy += ddy)
                {
                    int here = soffit[cy * nx + cx], step = here - last;
                    if (here < 0 || Math.Abs(step) > 2) break;
                    last = here; run++;
                }
                if (run * grid.Dx >= SlopeMinRunM && Math.Abs(last - soffit[f]) * grid.Dx >= SlopeMinRiseM) return true;
            }
            return false;
        }
        var wall = new bool[nx * ny];
        var free = new bool[nx * ny];   // covered and air over the whole band: no sill, no head
        var candidate = new bool[nx * ny];   // solid that is not slab and air in the band, before the slope test
        for (int f = 0; f < wall.Length; f++)
        {
            if (!covered[f]) continue;
            bool solid = false, wallSolid = false, air = false;
            for (int k = kF; k <= kC; k++)
                if (grid.Cell[grid.Index(f % nx, f / nx, k)] == VoxelGrid.Solid) { solid = true; if (!slab[(k - kF) * layerCells + f]) wallSolid = true; } else air = true;
            wall[f] = wallSolid && air && !Sloped(f);
            free[f] = !solid;
            candidate[f] = wallSolid && air;
        }
        // One column the slope test threw out between two wall columns (a gable's last roof cell over a window; a barge board) is part of that wall.
        var wallBefore = (bool[])wall.Clone();
        for (int f = 0; f < wall.Length; f++)
        {
            if (!candidate[f] || wall[f]) continue;
            int x = f % nx, y = f / nx;
            if ((x > 0 && x + 1 < nx && wallBefore[f - 1] && wallBefore[f + 1]) || (y > 0 && y + 1 < ny && wallBefore[f - nx] && wallBefore[f + nx])) wall[f] = true;
        }
        // A hole in a wall is bounded by its reveals: along the wall by the jambs, and up and down by the sill (or the floor) and the head. Air in a wall column
        // that is not enclosed that way on at least one wall axis (the air over a porch beside a post, between rails, under an eave) is no hole, however it
        // touches the real ones; without this the gap leaks through every post and the whole porch reads as one gap far thicker than a wall.
        int reach = (int)MathF.Ceiling(HoleMaxWidthM / grid.Dx);
        var tunnel = new sbyte[layerCells * bandLayers];   // 0 not asked, 1 yes, -1 no
        bool Solid(int x, int y, int k) => x >= 0 && y >= 0 && x < nx && y < ny && k >= 0 && k < grid.Nz && grid.Cell[grid.Index(x, y, k)] == VoxelGrid.Solid;   // off the grid is open air
        bool Bounded(int x, int y, int k, int dx, int dy)   // solid within reach on both sides along (dx, dy); the band's floor and top count as solid
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int n = 1;
                while (n <= reach && !Solid(x + sgn * dx * n, y + sgn * dy * n, k)) n++;
                if (n > reach) return false;
            }
            return true;
        }
        bool BoundedUp(int x, int y, int k)
        {
            int n = 1;
            while (k - n >= kF && !Solid(x, y, k - n)) n++;   // the floor under the band's first layer counts as the sill
            if (n > reach) return false;
            n = 1;
            while (k + n <= kC && !Solid(x, y, k + n)) n++;   // the band's top layer counts as the head
            return n <= reach;
        }
        bool IsHoleCell(int x, int y, int k)
        {
            ref sbyte memo = ref tunnel[(k - kF) * layerCells + y * nx + x];
            if (memo == 0) memo = (sbyte)(BoundedUp(x, y, k) && (Bounded(x, y, k, 0, 1) || Bounded(x, y, k, 1, 0)) ? 1 : -1);
            return memo > 0;
        }
        bool Aperture(int x, int y, int k) =>
            x >= 0 && y >= 0 && x < nx && y < ny && k >= kF && k <= kC && wall[y * nx + x] && k < topSolid[y * nx + x] &&
            grid.Cell[grid.Index(x, y, k)] != VoxelGrid.Solid && IsHoleCell(x, y, k);
        bool OpenAir(int x, int y, int k) =>
            x >= 0 && y >= 0 && x < nx && y < ny && k >= 0 && k < grid.Nz &&
            grid.Cell[grid.Index(x, y, k)] != VoxelGrid.Solid && !Aperture(x, y, k);

        var seen = new HashSet<int>();
        var raw = new List<Gap>();
        var queue = new Queue<(int X, int Y, int Z)>();
        int thick = 0, sealedPockets = 0, unflanked = 0;
        for (int k = kF; k <= kC; k++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
        {
            if (!Aperture(x, y, k) || !seen.Add(grid.Index(x, y, k))) continue;
            var gap = new Gap { Storey = storey, KFloor = kF, Covered = covered };
            int openX = 0, openY = 0;
            queue.Enqueue((x, y, k));
            while (queue.Count > 0)
            {
                var (cx, cy, cz) = queue.Dequeue();
                gap.Add(grid.Index(cx, cy, cz), cx, cy, cz);
                if (OpenAir(cx - 1, cy, cz)) openX++;
                if (OpenAir(cx + 1, cy, cz)) openX++;
                if (OpenAir(cx, cy - 1, cz)) openY++;
                if (OpenAir(cx, cy + 1, cz)) openY++;
                Visit(cx - 1, cy, cz); Visit(cx + 1, cy, cz); Visit(cx, cy - 1, cz);
                Visit(cx, cy + 1, cz); Visit(cx, cy, cz - 1); Visit(cx, cy, cz + 1);
            }
            if (openX + openY == 0) { sealedPockets++; continue; }
            gap.NormalX = openX >= openY;
            var (n0, n1) = gap.Across;
            if ((n1 - n0 + 1) * grid.Dx > MaxThicknessM + 1e-3f) { thick++; continue; }
            if (!Flanked(gap)) { unflanked++; continue; }
            gap.Rows.Add((gap.Z0, gap.Z1));
            raw.Add(gap);
        }

        void Visit(int x, int y, int k)
        {
            if (Aperture(x, y, k) && seen.Add(grid.Index(x, y, k))) queue.Enqueue((x, y, k));
        }

        // IMP-B D9: a hole in a wall has wall at both ends of its width, at its mid height. The air under a roof overhang runs on into more air.
        bool Flanked(Gap g)
        {
            int kc = (g.Z0 + g.Z1) / 2;
            bool Solid(int a) // a: the position along the wall just past an end; the cells across the gap's depth at that position
            {
                var (n0, n1) = g.Across;
                for (int n = n0; n <= n1; n++)
                {
                    int x = g.NormalX ? n : a, y = g.NormalX ? a : n;
                    if (x < 0 || y < 0 || x >= nx || y >= ny || grid.Cell[grid.Index(x, y, kc)] == VoxelGrid.Solid) return true;
                }
                return false;
            }
            var (a0, a1) = g.Along;
            return Solid(a0 - 1) && Solid(a1 + 1);
        }

        int maxThickCells = (int)MathF.Floor(MaxThicknessM / grid.Dx + 1e-3f);
        FullHeight(grid, storey, kF, kC, kPlane, covered, free, maxThickCells, raw);
        var merged = Merge(raw, maxThickCells);
        if (thick > 0 || sealedPockets > 0 || unflanked > 0)
            notes.Add($"{storeyName}: {thick} gap{(thick == 1 ? "" : "s")} deeper than {MaxThicknessM:0.#} m, " +
                      $"{sealedPockets} sealed pocket{(sealedPockets == 1 ? "" : "s")}" + (unflanked > 0 ? $" and {unflanked} with no wall beside them (under a roof overhang)" : "") +
                      " are not holes in a wall, dropped");
        return merged;
    }

    /// <summary>IMP-B D4: openings with no sill and no head, floor to ceiling. At the 1.1 m plane a wall is solid and the opening is a run of
    /// <paramref name="free"/> columns (air over the whole clear band) with a wall at both ends; a run of the same extent over at most
    /// <paramref name="maxThickCells"/> consecutive columns across the wall, with no wall beyond either side of it, is a notch through the wall
    /// line, wide at least <see cref="FullHeightMinWidthM"/>. It becomes a gap of the whole band, so it is classed, measured, filled for the
    /// room search and resolved like any other; a notch with the same space on both sides (a pillar and a wall) is dropped there.</summary>
    static void FullHeight(VoxelGrid grid, int storey, int kF, int kC, int kPlane, bool[] covered, bool[] free, int maxThickCells, List<Gap> into)
    {
        int nx = grid.Nx, ny = grid.Ny, minWidth = Math.Max(1, (int)MathF.Ceiling(FullHeightMinWidthM / grid.Dx)), jamb = (int)MathF.Ceiling(FullHeightJambM / grid.Dx);
        bool Wall(int x, int y) => x < 0 || y < 0 || x >= nx || y >= ny || grid.Cell[grid.Index(x, y, kPlane)] == VoxelGrid.Solid;
        for (int pass = 0; pass < 2; pass++)
        {
            bool normalX = pass == 0;   // the wall runs along Y and the opening faces +-X; else the wall runs along X
            int across = normalX ? nx : ny, along = normalX ? ny : nx;
            bool Free(int a, int b) => free[(normalX ? b : a) * nx + (normalX ? a : b)];      // a: across the wall, b: along it
            bool IsWall(int a, int b) => normalX ? Wall(a, b) : Wall(b, a);
            // chords: for each position across the wall, the runs of free columns along it that end in a wall on both sides
            var chords = new List<(int B0, int B1)>[across];
            for (int a = 0; a < across; a++)
            {
                chords[a] = [];
                for (int b = 0; b < along; b++)
                {
                    if (!Free(a, b)) continue;
                    int b0 = b;
                    while (b + 1 < along && Free(a, b + 1)) b++;
                    if (b - b0 + 1 >= minWidth && IsWall(a, b0 - 1) && IsWall(a, b + 1)) chords[a].Add((b0, b));
                }
            }
            for (int a = 0; a < across; a++)
                foreach (var (b0, b1) in chords[a])
                {
                    if (a > 0 && chords[a - 1].Contains((b0, b1))) continue;   // not the first column of its group
                    int a1 = a;
                    while (a1 + 1 < across && chords[a1 + 1].Contains((b0, b1))) a1++;
                    if (a1 - a + 1 > maxThickCells) continue;
                    bool open = true;
                    for (int b = b0; b <= b1 && open; b++) open = !IsWall(a - 1, b) && !IsWall(a1 + 1, b);
                    for (int aa = a; aa <= a1 && open; aa++)
                        for (int j = 1; j <= jamb && open; j++) open = IsWall(aa, b0 - j) && IsWall(aa, b1 + j);   // wall goes on both sides: a jamb, not a pillar or a stub
                    if (!open) continue;
                    var gap = new Gap { Storey = storey, KFloor = kF, Covered = covered, NormalX = normalX };
                    for (int k = kF; k <= kC; k++)
                    for (int aa = a; aa <= a1; aa++)
                    for (int bb = b0; bb <= b1; bb++)
                    {
                        int x = normalX ? aa : bb, y = normalX ? bb : aa;
                        gap.Add(grid.Index(x, y, k), x, y, k);
                    }
                    gap.Rows.Add((kF, kC));
                    into.Add(gap);
                }
        }
    }

    /// <summary>The merge rule (class remarks): same wall, at most <see cref="MergeGapCells"/> blocked cell apart.</summary>
    static List<Gap> Merge(List<Gap> gaps, int maxThickCells)
    {
        var parent = Enumerable.Range(0, gaps.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        static int Between(int a0, int a1, int b0, int b1) => Math.Max(a0, b0) - Math.Min(a1, b1) - 1;   // < 0: overlap

        for (int i = 0; i < gaps.Count; i++)
        for (int j = i + 1; j < gaps.Count; j++)
        {
            Gap a = gaps[i], b = gaps[j];
            if (a.NormalX != b.NormalX) continue;
            int along = Between(a.Along.A0, a.Along.A1, b.Along.A0, b.Along.A1);
            int up = Between(a.Z0, a.Z1, b.Z0, b.Z1);
            if (Between(a.Across.N0, a.Across.N1, b.Across.N0, b.Across.N1) > 0)
            {   // D8b: the same hole through the two layers of one wall (a frame or a cavity between them): equal in width and height to a cell, and no thicker together than a wall
                bool same = Math.Abs(a.Along.A0 - b.Along.A0) <= 1 && Math.Abs(a.Along.A1 - b.Along.A1) <= 1 && Math.Abs(a.Z0 - b.Z0) <= 1 && Math.Abs(a.Z1 - b.Z1) <= 1;
                if (same && Math.Max(a.Across.N1, b.Across.N1) - Math.Min(a.Across.N0, b.Across.N0) + 1 <= maxThickCells) parent[Root(j)] = Root(i);
                continue;
            }
            // A transom (one above the other) is as wide as the piece under it, to a cell or two at the frame: a window under a 12 m open slot is two things.
            bool sameWidth = Math.Abs(a.Along.A0 - b.Along.A0) <= TransomWidthSlackCells && Math.Abs(a.Along.A1 - b.Along.A1) <= TransomWidthSlackCells;
            if ((along <= MergeGapCells && up < 0) || (up <= MergeGapCells && along < 0 && sameWidth))
                parent[Root(j)] = Root(i);
        }

        var result = new Dictionary<int, Gap>();
        for (int i = 0; i < gaps.Count; i++)
        {
            int r = Root(i);
            if (!result.TryGetValue(r, out var into)) { result[r] = gaps[i]; continue; }
            var g = gaps[i];
            into.Cells.AddRange(g.Cells);
            into.X0 = Math.Min(into.X0, g.X0); into.X1 = Math.Max(into.X1, g.X1);
            into.Y0 = Math.Min(into.Y0, g.Y0); into.Y1 = Math.Max(into.Y1, g.Y1);
            into.Z0 = Math.Min(into.Z0, g.Z0); into.Z1 = Math.Max(into.Z1, g.Z1);
            into.Pieces += g.Pieces;
            into.Rows.AddRange(g.Rows);
        }
        return result.Values.ToList();
    }

    const int Outdoors = -1, Unknown = -2;

    /// <summary>Turns gaps into named openings once the rooms exist. Gaps with the same space on both sides are
    /// dropped. Names follow the room table: "Window 3 (Room 2, north)", "Door 1 (Room 1 to Room 3)".</summary>
    public static List<Opening> Resolve(VoxelGrid grid, List<Gap> gaps, List<Room> rooms, List<Storey> storeys, List<string> notes, IReadOnlyList<Mesh>? meshes = null, List<Gap>? kept = null)
    {
        var flat = FlatFaces(meshes);
        int sideWalkCells = Math.Max(1, (int)MathF.Round(SideWalkM / grid.Dx));
        var model = new BuildingModel { Storeys = storeys, Rooms = rooms };
        var display = RoomNames.Compute(model);
        var order = new Dictionary<Room, int>(ReferenceEqualityComparer.Instance);
        var shortName = new Dictionary<Room, string>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < display.Count; i++)
        {
            var (room, name) = display[i];
            order[room] = i;
            int cut = name.IndexOf(" (", StringComparison.Ordinal);
            shortName[room] = RoomNames.IsUnnamed(room) && cut > 0 ? name[..cut] : name;
        }
        // D3: the display numbering (north first) becomes the rooms' own name, so the opening names, the JSON, the solve text and the GUI agree.
        // Only the "Room N" part: the GUI adds the direction and area to a placeholder name itself, and still reads it as one.
        foreach (var room in rooms) room.Name = shortName[room];

        var found = new List<(Gap Gap, int A, int B, Vector3 Centre, float Width, float Height, float Depth, float Sill, GapClass Class, OpeningSide SideA, OpeningSide SideB)>();
        var dropped = new int[storeys.Count];
        foreach (var g in gaps)
        {
            int a = Side(g, -1), b = Side(g, +1);
            if (a == b || (a == Unknown && b == Unknown)) { dropped[g.Storey]++; continue; }
            float dx = grid.Dx;
            var (a0, a1) = g.Along;
            var (n0, n1) = g.Across;
            var centre = grid.Origin + new Vector3((g.X0 + g.X1 + 1) * 0.5f, (g.Y0 + g.Y1 + 1) * 0.5f, (g.Z0 + g.Z1 + 1) * 0.5f) * dx;
            float width = (a1 - a0 + 1.98f) * dx, height = (g.Z1 - g.Z0 + 1.98f) * dx, depth = (n1 - n0 + 1) * dx;
            float sill = (g.Z0 - g.KFloor) * dx;
            var cls = Classify(sill, width, height);
            if (cls == GapClass.Door && StackOfShortPieces(g, dx)) cls = GapClass.Window;   // D8a
            Measure(grid, g, flat, ref centre, ref width, ref height);   // S3-02: the mesh's own reveal-to-reveal size
            found.Add((g, a, b, centre, width, height, depth, sill, cls, Kind(g, -1, a), Kind(g, +1, b)));
            kept?.Add(g);
        }

        var sorted = found.OrderBy(f => f.Gap.Storey)
                          .ThenByDescending(f => MathF.Round(f.Centre.Y * 2))
                          .ThenBy(f => MathF.Round(f.Centre.X * 2))
                          .ThenBy(f => f.Centre.Z).ToList();
        var counters = new Dictionary<GapClass, int>();
        var openings = new List<Opening>();
        foreach (var f in sorted)
        {
            int n = counters[f.Class] = counters.GetValueOrDefault(f.Class) + 1;
            var axis = f.Gap.NormalX ? Vector3.UnitX : Vector3.UnitY;
            string where;
            Vector3 normal;
            if (f.A == Outdoors || f.B == Outdoors)
            {
                // Outward normal: toward the outdoor side. The compass word is the way the opening faces.
                normal = f.B == Outdoors ? axis : -axis;
                int room = f.A == Outdoors ? f.B : f.A;
                where = room >= 0 ? $"{shortName[rooms[room]]}, {Compass(normal)}" : $"{Compass(normal)} façade";
            }
            else if (f.A >= 0 && f.B >= 0)
            {
                normal = axis;
                var (first, second) = order[rooms[f.A]] <= order[rooms[f.B]] ? (f.A, f.B) : (f.B, f.A);
                where = $"{shortName[rooms[first]]} to {shortName[rooms[second]]}";
            }
            else
            {
                normal = axis;
                where = shortName[rooms[f.A >= 0 ? f.A : f.B]];
            }
            var storey = storeys[f.Gap.Storey];
            openings.Add(new Opening
            {
                Id = $"det-open-{storey.Id}-{openings.Count + 1}",
                Name = $"{f.Class} {n}",
                Level = storey.Id,
                Kind = f.Class switch { GapClass.Door => OpeningKind.Door, GapClass.Window => OpeningKind.Window, _ => OpeningKind.Opening },
                IsOpen = true,                     // the hole is in the mesh: it starts open
                Center = f.Centre,
                Normal = normal,
                Width = f.Width,
                Height = f.Height,
                Depth = f.Depth,
                SideA = f.SideA,   // IMP-C: A the -axis side, B the +axis side
                SideB = f.SideB,
            });
        }

        for (int s = 0; s < storeys.Count; s++)
        {
            var mine = found.Where(f => f.Gap.Storey == s).ToList();
            if (mine.Count == 0 && dropped[s] == 0) continue;
            string classes = string.Join(", ", mine.GroupBy(f => f.Class).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}"));
            int joined = mine.Count(f => f.Gap.Pieces > 1);
            notes.Add($"{storeys[s].Name}: {mine.Count} opening{(mine.Count == 1 ? "" : "s")}" +
                      (classes.Length > 0 ? $" ({classes})" : "") +
                      (joined > 0 ? $", {joined} joined across a one-cell mullion" : "") +
                      (dropped[s] > 0 ? $"; {dropped[s]} gap{(dropped[s] == 1 ? "" : "s")} dropped (same space on both sides)" : ""));
        }
        return openings;

        // IMP-C: what one side of the gap is for the ledger. A room is a room. Otherwise walk out as Side does but stop at the first wall after air: covered air that a wall bounds
        // (a hall without a loop, a stair void, a closet) is indoor air in no room, even when Side walks on through the wall and finds the open air beyond. Covered air that opens
        // straight on to uncovered air (a porch) is outdoors; so is the grid edge. A walk that ends in covered air without meeting a wall (a long hall) is indoor.
        OpeningSide Kind(Gap g, int dir, int side)
        {
            if (side >= 0) return OpeningSide.Room;
            var storey = storeys[g.Storey];
            int xc = (g.X0 + g.X1) / 2, yc = (g.Y0 + g.Y1) / 2, kc = (g.Z0 + g.Z1) / 2;
            bool air = false;
            for (int s = 1; s <= sideWalkCells; s++)
            {
                int x = g.NormalX ? (dir > 0 ? g.X1 + s : g.X0 - s) : xc;
                int y = g.NormalX ? yc : (dir > 0 ? g.Y1 + s : g.Y0 - s);
                if (x < 0 || y < 0 || x >= grid.Nx || y >= grid.Ny) return OpeningSide.Outdoors;
                var c = grid.CellCenter(x, y, kc);
                for (int r = 0; r < rooms.Count; r++)
                    if (model.StoreyOf(rooms[r]) == storey && Inside(rooms[r], new Vector2(c.X, c.Y))) return OpeningSide.Room;
                if (grid.Cell[grid.Index(x, y, kc)] == VoxelGrid.Solid) { if (air) return OpeningSide.Indoor; continue; }
                if (!g.Covered[y * grid.Nx + x]) return OpeningSide.Outdoors;
                air = true;
            }
            return side == Outdoors ? OpeningSide.Outdoors : OpeningSide.Indoor;
        }

        // What is on one side: walk out from the gap's face along its normal until a room loop or open sky.
        int Side(Gap g, int dir)
        {
            var storey = storeys[g.Storey];
            int xc = (g.X0 + g.X1) / 2, yc = (g.Y0 + g.Y1) / 2, kc = (g.Z0 + g.Z1) / 2;
            for (int s = 1; s <= sideWalkCells; s++)
            {
                int x = g.NormalX ? (dir > 0 ? g.X1 + s : g.X0 - s) : xc;
                int y = g.NormalX ? yc : (dir > 0 ? g.Y1 + s : g.Y0 - s);
                if (x < 0 || y < 0 || x >= grid.Nx || y >= grid.Ny) return Outdoors;
                var c = grid.CellCenter(x, y, kc);
                for (int r = 0; r < rooms.Count; r++)
                    if (model.StoreyOf(rooms[r]) == storey && Inside(rooms[r], new Vector2(c.X, c.Y))) return r;
                if (grid.Cell[grid.Index(x, y, kc)] != VoxelGrid.Solid && !g.Covered[y * grid.Nx + x]) return Outdoors;
            }
            return Unknown;
        }
    }

    /// <summary>D8a: a gap with a sill under <see cref="DoorMaxSill"/> that is a stack of two or more pieces, each run of pieces shorter than
    /// <see cref="StackMaxPieceHeight"/>, is a stack of casements between horizontal mullions, not a door.</summary>
    static bool StackOfShortPieces(Gap g, float dx)
    {
        var runs = new List<(int Z0, int Z1)>();   // pieces that touch or overlap in height are one run; a blocked layer (a mullion) between them starts the next
        foreach (var r in g.Rows.OrderBy(r => r.Z0))
            if (runs.Count > 0 && r.Z0 <= runs[^1].Z1 + 1) runs[^1] = (runs[^1].Z0, Math.Max(runs[^1].Z1, r.Z1));
            else runs.Add(r);
        return runs.Count >= 2 && runs.All(r => (r.Z1 - r.Z0 + 1) * dx < StackMaxPieceHeight);
    }

    /// <summary>An axis-aligned face of the mesh (normal within 2.5 deg of a grid axis): the plane coordinate and its extent.</summary>
    readonly record struct Flat(int Axis, float C, Vector3 Min, Vector3 Max);

    static List<Flat> FlatFaces(IReadOnlyList<Mesh>? meshes)
    {
        var result = new List<Flat>();
        if (meshes == null) return result;
        foreach (var mesh in meshes)
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                Vector3 a = mesh.Vertices[mesh.Indices[t]], b = mesh.Vertices[mesh.Indices[t + 1]], c = mesh.Vertices[mesh.Indices[t + 2]];
                var n = Vector3.Cross(b - a, c - a);
                float len = n.Length();
                if (len < 1e-12f) continue;
                n = Vector3.Abs(n / len);
                int axis = n.X >= 0.999f ? 0 : n.Y >= 0.999f ? 1 : n.Z >= 0.999f ? 2 : -1;
                if (axis < 0) continue;
                float cc = axis == 0 ? (a.X + b.X + c.X) / 3f : axis == 1 ? (a.Y + b.Y + c.Y) / 3f : (a.Z + b.Z + c.Z) / 3f;
                result.Add(new Flat(axis, cc, Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c))));
            }
        return result;
    }

    /// <summary>S3-02: refines a gap's width, height and centre (along the wall and in height; the depth centre stays on the cells) from the
    /// mesh's reveal planes. The air cells span [lo, hi] on each axis; the reveal that bounds them lies in the solid cell beyond, so it is the
    /// nearest axis-aligned face in [lo - Dx, lo] (lower side) or [hi, hi + Dx] (upper side) whose extent overlaps the gap box on the other two axes.</summary>
    static void Measure(VoxelGrid grid, Gap g, List<Flat> flat, ref Vector3 centre, ref float width, ref float height)
    {
        float dx = grid.Dx;
        var lo = grid.Origin + new Vector3(g.X0, g.Y0, g.Z0) * dx;
        var hi = grid.Origin + new Vector3(g.X1 + 1, g.Y1 + 1, g.Z1 + 1) * dx;
        int along = g.NormalX ? 1 : 0;
        float eps = 0.01f * dx;

        float Reveal(int axis, bool upper)
        {
            float l = Comp(lo, axis), h = Comp(hi, axis);
            float from = upper ? h - eps : l - dx - eps, to = upper ? h + dx + eps : l + eps;
            float best = float.NaN;
            foreach (var f in flat)
            {
                if (f.Axis != axis || f.C < from || f.C > to) continue;
                bool overlaps = true;
                for (int k = 0; k < 3 && overlaps; k++)
                    if (k != axis) overlaps = MathF.Min(Comp(f.Max, k), Comp(hi, k)) - MathF.Max(Comp(f.Min, k), Comp(lo, k)) > 1e-4f;
                if (!overlaps) continue;
                if (float.IsNaN(best) || (upper ? f.C < best : f.C > best)) best = f.C;
            }
            return float.IsNaN(best) ? (upper ? h + 0.49f * dx : l - 0.49f * dx) : best;
        }

        float a0 = Reveal(along, false), a1 = Reveal(along, true), z0 = Reveal(2, false), z1 = Reveal(2, true);
        width = a1 - a0; height = z1 - z0;
        if (g.NormalX) centre.Y = (a0 + a1) * 0.5f; else centre.X = (a0 + a1) * 0.5f;
        centre.Z = (z0 + z1) * 0.5f;
    }

    static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Even-odd point in polygon over all of a room's loops.</summary>
    static bool Inside(Room room, Vector2 p)
    {
        bool inside = false;
        foreach (var loop in room.Loops)
            for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y) &&
                    p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>8-point compass word for a horizontal direction (+Y north, +X east).</summary>
    public static string Compass(Vector3 d)
    {
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        float deg = MathF.Atan2(d.X, d.Y) * 180f / MathF.PI;
        return names[(int)MathF.Floor(((deg + 360f + 22.5f) % 360f) / 45f) % 8];
    }
}
