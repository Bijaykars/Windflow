using System.Numerics;
using WindFlow.App.Viewport;
using WindFlow.Core;

namespace WindFlow.App;

/// <summary>One grid for the renderer out of a nested run: the fine box cells inside the box, the coarse
/// whole-building cells everywhere else. It covers the part of the domain worth drawing (the building plus
/// the particle margin, and the whole box) at the fine Dx when that fits in <see cref="FieldVolume.MaxSide"/>
/// cells per side, else at the smallest pitch that does, so <see cref="FieldVolume.Build"/> keeps it at
/// stride 1 and the box shows its own resolution. Particles, slices, the still-air cloud and the rooms'
/// still % all read the volume built from this, so none of them needs to know there are two grids.
/// <para>The mapping is built once per geometry (the solver thread rebuilds it after UpdateCells or a new
/// box); <see cref="Fill"/> is then one pass over each grid. Built and filled on the solver thread only;
/// <see cref="FillScalar"/> writes into a caller buffer and only reads the immutable mapping, so the stats
/// thread may call it.</para></summary>
public sealed class NestedComposite
{
    public readonly VoxelGrid Grid;
    public readonly VoxelGrid Coarse, Fine;
    public readonly float[] Ux, Uy, Uz;
    readonly int[] coarseOf;        // per composite cell: the coarse cell under its centre, -1 = fed by fine cells
    readonly int[] fineTo;          // per fine AIR cell: the composite cell it averages into, -1 = none
    readonly float[] fineWeight;    // per composite cell: 1 / (air fine cells averaged into it), 0 = none

    NestedComposite(VoxelGrid grid, VoxelGrid coarse, VoxelGrid fine, int[] coarseOf, int[] fineTo, float[] fineWeight)
    {
        Grid = grid; Coarse = coarse; Fine = fine;
        this.coarseOf = coarseOf; this.fineTo = fineTo; this.fineWeight = fineWeight;
        Ux = new float[grid.Count]; Uy = new float[grid.Count]; Uz = new float[grid.Count];
    }

    public static NestedComposite Build(VoxelGrid coarse, VoxelGrid fine, BuildingModel model)
    {
        var domMin = coarse.Origin;
        var domMax = coarse.Origin + new Vector3(coarse.Nx, coarse.Ny, coarse.Nz) * coarse.Dx;
        var boxMin = fine.Origin;
        var boxMax = fine.Origin + new Vector3(fine.Nx, fine.Ny, fine.Nz) * fine.Dx;
        var (focusMin, focusMax) = FieldVolume.FocusRegion(model, domMin, domMax);
        var lo = Vector3.Max(Vector3.Min(focusMin, boxMin), domMin);
        var hi = Vector3.Min(Vector3.Max(focusMax, boxMax), domMax);
        var ext = hi - lo;
        // Two cells of slack: the origin is snapped down onto the fine lattice below.
        float pitch = MathF.Max(fine.Dx, MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) / (FieldVolume.MaxSide - 2));
        // On the fine lattice, so at pitch = fine Dx a composite cell IS a fine cell (no resampling blur).
        var steps = new Vector3(MathF.Floor((lo.X - fine.Origin.X) / pitch), MathF.Floor((lo.Y - fine.Origin.Y) / pitch),
                                MathF.Floor((lo.Z - fine.Origin.Z) / pitch));
        var origin = fine.Origin + steps * pitch;
        int Side(float span) => Math.Clamp((int)MathF.Ceiling(span / pitch - 1e-3f), 1, FieldVolume.MaxSide);
        var grid = new VoxelGrid
        {
            Nx = Side(hi.X - origin.X), Ny = Side(hi.Y - origin.Y), Nz = Side(hi.Z - origin.Z),
            Dx = pitch, Origin = origin,
        };
        grid.Cell = new byte[grid.Count];

        // Fine cells -> composite cells; a composite cell is fine-fed when its centre is inside the box.
        int n = grid.Count;
        var fineTo = new int[fine.Count];
        var airCount = new int[n];
        var anySolid = new bool[n];
        var fed = new bool[n];
        for (int z = 0; z < fine.Nz; z++)
        for (int y = 0; y < fine.Ny; y++)
        for (int x = 0; x < fine.Nx; x++)
        {
            int f = fine.Index(x, y, z);
            fineTo[f] = -1;
            int c = IndexOf(grid, fine.CellCenter(x, y, z));
            if (c < 0) continue;
            var cc = Centre(grid, c);
            if (cc.X < boxMin.X || cc.Y < boxMin.Y || cc.Z < boxMin.Z || cc.X > boxMax.X || cc.Y > boxMax.Y || cc.Z > boxMax.Z) continue;
            fed[c] = true;
            if (fine.IsSolid(f)) { anySolid[c] = true; continue; }
            fineTo[f] = c;
            airCount[c]++;
        }

        var coarseOf = new int[n];
        var weight = new float[n];
        for (int c = 0; c < n; c++)
        {
            if (fed[c])
            {
                coarseOf[c] = -1;
                // Any solid fine cell wins, as in FieldVolume.BuildOccupancy: a one-cell partition must still
                // stop particles when the composite pitch is coarser than the fine Dx.
                grid.Cell[c] = anySolid[c] || airCount[c] == 0 ? VoxelGrid.Solid : VoxelGrid.Air;
                weight[c] = airCount[c] > 0 ? 1f / airCount[c] : 0f;
                continue;
            }
            int k = IndexOf(coarse, Centre(grid, c), clamp: true);
            coarseOf[c] = k;
            grid.Cell[c] = coarse.IsSolid(k) ? VoxelGrid.Solid : VoxelGrid.Air;
        }
        // A composite cell marked solid must not collect fine velocities either.
        for (int f = 0; f < fineTo.Length; f++)
            if (fineTo[f] >= 0 && grid.Cell[fineTo[f]] == VoxelGrid.Solid) fineTo[f] = -1;

        // Device outlets, for the particle spawn table: the fine ones inside the box, the coarse ones outside.
        foreach (var d in fine.Devices) AddDevice(grid, d, i => fineTo[i]);
        foreach (var d in coarse.Devices)
            AddDevice(grid, d, i =>
            {
                int z = i / (coarse.Nx * coarse.Ny), rem = i - z * coarse.Nx * coarse.Ny;
                int c = IndexOf(grid, coarse.CellCenter(rem % coarse.Nx, rem / coarse.Nx, z));
                return c >= 0 && coarseOf[c] >= 0 && grid.Cell[c] != VoxelGrid.Solid ? c : -1;
            });
        return new NestedComposite(grid, coarse, fine, coarseOf, fineTo, weight);
    }

    static void AddDevice(VoxelGrid grid, DeviceRegion d, Func<int, int> map)
    {
        var cells = new List<int>();
        for (int i = 0; i < d.OutletCount && i < d.Cells.Length; i++)
        {
            int c = map(d.Cells[i]);
            if (c >= 0 && !cells.Contains(c)) cells.Add(c);
        }
        if (cells.Count == 0) return;
        grid.Devices.Add(new DeviceRegion
        {
            SourceId = d.SourceId, SourceName = d.SourceName, SourceIndex = d.SourceIndex, Kind = d.Kind,
            Cells = [.. cells], Target = new Vector3[cells.Count], OutletCount = cells.Count,
        });
    }

    /// <summary>The composite velocity from the two instantaneous fields (the arrays ReadVelocity filled).</summary>
    public void Fill(float[] cux, float[] cuy, float[] cuz, float[] fux, float[] fuy, float[] fuz)
    {
        Compose(cux, fux, Ux);
        Compose(cuy, fuy, Uy);
        Compose(cuz, fuz, Uz);
    }

    /// <summary>Same composition for one per-cell scalar (pressure), into <paramref name="into"/> (Grid.Count).</summary>
    public void FillScalar(float[] coarseValues, float[] fineValues, float[] into) => Compose(coarseValues, fineValues, into);

    void Compose(float[] c, float[] f, float[] into)
    {
        for (int i = 0; i < into.Length; i++)
        {
            int k = coarseOf[i];
            into[i] = k >= 0 && k < c.Length ? c[k] : 0f;
        }
        int nf = Math.Min(f.Length, fineTo.Length);
        for (int i = 0; i < nf; i++)
        {
            int t = fineTo[i];
            if (t >= 0) into[t] += f[i] * fineWeight[t];
        }
    }

    /// <summary>Is this point inside the fine box (the grid the fine session actually solves)?</summary>
    public static bool Inside(VoxelGrid fine, Vector3 p, float inset = 0f)
    {
        var min = fine.Origin + new Vector3(inset);
        var max = fine.Origin + new Vector3(fine.Nx, fine.Ny, fine.Nz) * fine.Dx - new Vector3(inset);
        return p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;
    }

    static Vector3 Centre(VoxelGrid g, int c)
    {
        int z = c / (g.Nx * g.Ny), rem = c - z * g.Nx * g.Ny;
        return g.CellCenter(rem % g.Nx, rem / g.Nx, z);
    }

    static int IndexOf(VoxelGrid g, Vector3 p, bool clamp = false)
    {
        int x = (int)MathF.Floor((p.X - g.Origin.X) / g.Dx);
        int y = (int)MathF.Floor((p.Y - g.Origin.Y) / g.Dx);
        int z = (int)MathF.Floor((p.Z - g.Origin.Z) / g.Dx);
        if (clamp) { x = Math.Clamp(x, 0, g.Nx - 1); y = Math.Clamp(y, 0, g.Ny - 1); z = Math.Clamp(z, 0, g.Nz - 1); }
        else if (x < 0 || y < 0 || z < 0 || x >= g.Nx || y >= g.Ny || z >= g.Nz) return -1;
        return g.Index(x, y, z);
    }
}
