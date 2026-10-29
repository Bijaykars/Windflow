using System.Numerics;
using WindFlow.Core;

namespace WindFlow.Sandbox;

static partial class FixChecks
{
    /// <summary>Exact volume of a union of axis-aligned boxes (coordinate compression).</summary>
    static double UnionVolume(IReadOnlyList<(Vector3 Lo, Vector3 Hi)> boxes)
    {
        if (boxes.Count == 0) return 0;
        float[] Axis(Func<Vector3, float> f) => boxes.SelectMany(b => new[] { f(b.Lo), f(b.Hi) }).Distinct().OrderBy(v => v).ToArray();
        var xs = Axis(v => v.X); var ys = Axis(v => v.Y); var zs = Axis(v => v.Z);
        double vol = 0;
        for (int i = 0; i + 1 < xs.Length; i++) for (int j = 0; j + 1 < ys.Length; j++) for (int k = 0; k + 1 < zs.Length; k++)
        {
            var c = new Vector3((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2, (zs[k] + zs[k + 1]) / 2);
            if (boxes.Any(b => c.X > b.Lo.X && c.X < b.Hi.X && c.Y > b.Lo.Y && c.Y < b.Hi.Y && c.Z > b.Lo.Z && c.Z < b.Hi.Z))
                vol += (double)(xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]) * (zs[k + 1] - zs[k]);
        }
        return vol;
    }

    /// <summary>Sample house (Sandbox copy of App.SampleBuilding, no fan) at the Preview and Accurate cell budgets:
    /// material volume of walls and slabs, room air volumes and carved opening areas against nominal. Printed as
    /// NOTE lines; the only assertions are "nothing is filled or sealed" sanity bounds.</summary>
    static void SampleHouseNumbers()
    {
        var model = SampleHouse.Build();
        (Vector3 Lo, Vector3 Hi) Aabb(Mesh m) => (m.Vertices.Aggregate(Vector3.Min), m.Vertices.Aggregate(Vector3.Max));
        var boxes = model.Obstacles.Select(Aabb).ToList();
        int[] wallIdx = [0, 1, 2, 3, 10, 11, 12, 13, 14], slabIdx = [5, 6, 7, 8, 9];   // 4 is the ground slab (below layer 0)
        var wallBoxes = wallIdx.Select(i => boxes[i]).ToList();
        var allBoxes = wallIdx.Concat(slabIdx).Select(i => boxes[i]).ToList();
        double nominalWall = UnionVolume(wallBoxes), nominalAll = UnionVolume(allBoxes);
        var open = model.Openings.Where(o => o.IsOpen).ToList();
        double cutVolume = open.Sum(o => (double)o.Width * o.Height * o.Depth);
        Note("F1i sample-house nominal", $"wall_union_m3={nominalWall:F2} slab_only_m3={nominalAll - nominalWall:F2} open_openings={open.Count} opening_cut_m3={cutVolume:F2} nominal_solid_m3={nominalAll - cutVolume:F2}");

        foreach (var (tier, cells) in new[] { ("Preview", Simulation.TargetCells(Quality.Preview, true)), ("Accurate", 5_000_000) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Voxelizer.Build(model, cells);
            double buildSeconds = sw.Elapsed.TotalSeconds;
            double cell3 = (double)g.Dx * g.Dx * g.Dx;
            bool InWall(Vector3 p) => wallBoxes.Any(b => p.X > b.Lo.X && p.X < b.Hi.X && p.Y > b.Lo.Y && p.Y < b.Hi.Y && p.Z > b.Lo.Z && p.Z < b.Hi.Z);
            bool InAny(Vector3 p) => allBoxes.Any(b => p.X > b.Lo.X && p.X < b.Hi.X && p.Y > b.Lo.Y && p.Y < b.Hi.Y && p.Z > b.Lo.Z && p.Z < b.Hi.Z);
            long wallCells = 0, slabCells = 0, rim = 0;
            for (int z = 1; z < g.Nz; z++) for (int y = 0; y < g.Ny; y++) for (int x = 0; x < g.Nx; x++)
            {
                if (g.Cell[g.Index(x, y, z)] != VoxelGrid.Solid) continue;
                var c = g.CellCenter(x, y, z);
                if (c.Z < 0 || c.Z > 6.5f || c.X < -1 || c.X > 13 || c.Y < -1 || c.Y > 9) continue;
                if (InWall(c)) wallCells++; else if (InAny(c)) slabCells++; else rim++;
            }
            double total = (wallCells + slabCells + rim) * cell3;
            Note($"F1i sample-house material {tier}", $"dx_m={g.Dx:F3} cells={g.Count} wall_cells_m3={wallCells * cell3:F2} slab_cells_m3={slabCells * cell3:F2} rim_cells_m3={rim * cell3:F2} " +
                $"solid_total_m3={total:F2} nominal_solid_m3={nominalAll - cutVolume:F2} ratio={total / (nominalAll - cutVolume):F2} build_s={buildSeconds:F1}");

            var roomParts = new List<string>(); double worstRoom = double.MaxValue;
            foreach (var r in model.Rooms)
            {
                var mask = Analysis.RoomMask(g, r, out int n);
                var loop = r.Loops[0]; double area = Math.Abs((loop[1].X - loop[0].X) * (loop[2].Y - loop[1].Y));
                double nominal = area * (r.ZMax - r.ZMin), measured = n * cell3;
                worstRoom = Math.Min(worstRoom, measured / nominal);
                roomParts.Add($"{r.Id}={measured:F1}/{nominal:F1}({measured / nominal:F2})");
            }
            Note($"F1i sample-house room air volumes {tier}", string.Join(" ", roomParts) + " (measured_m3/loop_area_x_height_m3)");

            var areaParts = new List<string>(); double sumC = 0, sumN = 0, worstArea = 0;
            foreach (var o in open)
            {
                double carved = Analysis.ThroatCells(g, o).Count * (double)g.Dx * g.Dx, nominal = (double)o.Width * o.Height;
                sumC += carved; sumN += nominal; worstArea = Math.Max(worstArea, Math.Abs(carved / nominal - 1));
                areaParts.Add($"{o.Id}={carved:F2}/{nominal:F2}");
            }
            Note($"F1i sample-house carved opening areas {tier}", $"sum_carved_m2={sumC:F2} sum_nominal_m2={sumN:F2} ratio={sumC / sumN:F3} worst_rel_err={worstArea:F2} | " + string.Join(" ", areaParts));
            Check($"F1j sample house {tier}: every room keeps at least 60% of its nominal air volume and carved openings stay within 2x nominal area",
                worstRoom >= .6 && sumC / sumN > .5 && sumC / sumN < 2, $"worst_room_ratio={worstRoom:F2} opening_area_ratio={sumC / sumN:F2}");
        }
    }
}
