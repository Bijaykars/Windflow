using System.Numerics;
using System.Reflection;
using WindFlow.Core;
using Environment = WindFlow.Core.Environment;

namespace WindFlow.Sandbox;

// 6e (P14-03) and RY-07: Surface HeatSource faces exchange heat by a convection correlation (Alamdari-Hammond natural convection,
// McAdams forced term, cube blend), applied as a source in the first fluid cell, and a Surface box also holds the faces of a wall thinner
// than a cell (cut links).
static partial class FixChecks
{
    static void SurfaceConvectionChecks()
    {
        foreach (var scheme in new[] { ThermalScheme.Hybrid, ThermalScheme.Lbm }) SurfaceHeatTransfer(scheme);
        ThinSlabSurfaceCheck();
        SurfaceSideCheck();
        SurfaceSharedCellCheck();
        SurfaceGridOffsetCheck();
        SurfacePlateLiteralCheck();
    }

    /// <summary>RT-10b: the solver's correlation read against published Alamdari-Hammond values worked out by hand, not against <see cref="CorrelationH"/> (a copy of the
    /// solver's formula that a wrong coefficient would also satisfy). dT = 20 K, still air. Vertical plate, H = 3 m: h = ((1.5 (20/3)^0.25)^6 + (1.23 * 20^(1/3))^6)^(1/6)
    /// = 3.413 W/m2K. Hot floor (buoyant horizontal), hydraulic diameter 2 m: ((1.4 (20/2)^0.25)^6 + (1.63 * 20^(1/3))^6)^(1/6) = 4.448 W/m2K. Within 1 %
    /// (the solver's float Newton roots).</summary>
    static void SurfacePlateLiteralCheck()
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
        var core = typeof(LbmSolver);
        var plate = new HeatSource { Id = "p", Name = "plate", Kind = HeatKind.Surface, Width = 0.5f, Depth = 2f, Height = 3f, SurfaceTemperature = 40f };
        var floor = new HeatSource { Id = "f", Name = "floor", Kind = HeatKind.Surface, Width = 2f, Depth = 2f, Height = 0.5f, SurfaceTemperature = 40f };
        float H(HeatSource s, int orient)
        {
            var c = (System.Runtime.CompilerServices.ITuple)core.GetMethod("SurfaceCoefficients", F)!.Invoke(null, [s, 0.1f])!;
            return (float)core.GetMethod("SurfaceH", F)!.Invoke(null, [20f, orient, 0f, c[0]!, c[1]!, c[2]!])!;
        }
        float hv = H(plate, 0), hu = H(floor, 2);
        Check("RT-10b Correlation plate: a 20 K vertical plate (H 3 m) and a 20 K hot floor (2 m hydraulic diameter) read the published Alamdari-Hammond h, 3.413 and 4.448 W/m2K (hand-computed), within 1 %",
            Math.Abs(hv - 3.413) < 0.034 && Math.Abs(hu - 4.448) < 0.045, $"vertical {hv:0.000}, hot floor {hu:0.000} W/m2K");
    }

    /// <summary>A Dx 0.25 grid of nx x ny x nz cells, a one-cell solid shell, plus the cells <paramref name="solid"/> marks; BuildThermal's inputs.</summary>
    static (VoxelGrid Grid, BuildingModel Model) ShellGrid(int nx, int ny, int nz, Func<int, int, int, bool> solid)
    {
        const float dx = 0.25f;
        var g = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = dx, Origin = Vector3.Zero, Cell = new byte[nx * ny * nz] };
        for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            g.Cell[g.Index(x, y, z)] = x == 0 || x == nx - 1 || y == 0 || y == ny - 1 || z == 0 || z == nz - 1 || solid(x, y, z) ? VoxelGrid.Solid : VoxelGrid.Air;
        var m = new BuildingModel
        {
            Name = "surface-shell", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3f, Analysed = true } },
            Env = new Environment { IndoorTemperature = 20, OutdoorTemperature = 20 },
        };
        m.Obstacles.Add(new Mesh { Id = "dummy", Vertices = [new(0, 0, 0), new(nx * dx, 0, 0), new(nx * dx, ny * dx, 0), new(0, ny * dx, 0)], Indices = [0, 1, 2, 0, 2, 3] });
        return (g, m);
    }

    static System.Runtime.CompilerServices.ITuple BuildThermalProbe(VoxelGrid g, BuildingModel m, bool resolved) =>
        (System.Runtime.CompilerServices.ITuple)typeof(LbmSolver).GetMethod("BuildThermal", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [g, m, resolved])!;

    /// <summary>RT-02: in Correlation mode a Surface face counts only on the side the box was drawn on. Two rooms (Dx 0.25, 7 and 6 x 6 x 6
    /// air cells each side of a one-cell partition at x = 2.0-2.25): a 40 C box drawn into the partition from room A (x 1.8-2.15, it holds the partition
    /// centre 2.125, and room B's first cell centre 2.375 lies within a Dx of it) heats room A only: 36 +x faces on A's last layer, none facing B, and
    /// room A's air takes up the energy the host sum h A dT predicts while room B stays at 20 C. A glazing box on a one-cell exterior wall (x 1.6-1.9
    /// on a wall at 1.5-1.75) holds the indoor face and none of the outdoor air's.</summary>
    static void SurfaceSideCheck()
    {
        const float dx = 0.25f, tAir = 20f, tWall = 40f;
        const int nx = 16, ny = 8, nz = 8;
        var (g, m) = ShellGrid(nx, ny, nz, (x, y, z) => x == 8);
        m.Heat.Add(new HeatSource { Id = "p", Name = "panel", Kind = HeatKind.Surface, Center = new Vector3(1.975f, 1f, 1f),
            Width = 0.35f, Depth = 1.5f, Height = 1.5f, SurfaceTemperature = tWall });
        var r = BuildThermalProbe(g, m, false);
        var fixedMask = (int[])r[2]!;
        int facesA = 0, facesB = 0, other = 0;
        for (int i = 0; i < fixedMask.Length; i++)
        {
            if (fixedMask[i] == 0) continue;
            int x = i % nx, mk = fixedMask[i] & 63;
            if (x == 7 && mk == 1) facesA++; else if (x == 9 && (mk & 2) != 0) facesB++; else other++;
        }
        // the air's energy rise against the host sum h A dT over A's faces (sealed, adiabatic, quiescent)
        var opts = new RunOptions { Backend = Backend.Cpu, Thermal = true, ThermalScheme = ThermalScheme.Lbm };
        using var s = LbmSolver.Start(g, m, Vector2.Zero, opts);
        int count = g.Count;
        var faceCells = new List<int>();
        for (int z = 1; z < nz - 1; z++) for (int y = 1; y < ny - 1; y++) faceCells.Add(g.Index(7, y, z));
        double cellV = dx * dx * dx, rhoCp = 1.2 * 1005, dt = s.TimeStepSeconds;
        var t = new float[count]; var ux = new float[count]; var uy = new float[count]; var uz = new float[count];
        (double A, double B) Energy()
        {
            s.ReadTemperature(t);
            double a = 0, b = 0;
            for (int i = 0; i < count; i++) if (g.Cell[i] == VoxelGrid.Air) { if (i % nx < 8) a += t[i] - tAir; else b += t[i] - tAir; }
            return (a * rhoCp * cellV, b * rhoCp * cellV);
        }
        double Sample()
        {
            s.ReadTemperature(t); s.ReadVelocity(ux, uy, uz);
            double w = 0;
            foreach (int i in faceCells)
            {
                double dT = tWall - t[i];
                w += CorrelationH(dT, 0, Math.Sqrt(uy[i] * uy[i] + uz[i] * uz[i]), 1.5, 1.5) * dx * dx * dT;
            }
            return w;
        }
        const int every = 10, samples = 60;
        var (a0, b0) = Energy();
        double integral = 0, wPrev = Sample();
        for (int k = 0; k < samples; k++)
        {
            s.Step(every);
            double w = Sample();
            integral += 0.5 * (wPrev + w) * every * dt; wPrev = w;
        }
        var (a1, b1) = Energy();
        double riseA = a1 - a0, riseB = b1 - b0, err = Math.Abs(riseA - integral) / integral;

        // glazing on a one-cell exterior wall (x = 6 is the wall: 1.5-1.75; indoor x >= 7; outdoor x <= 5)
        var (g2, m2) = ShellGrid(14, 8, 8, (x, y, z) => x == 6);
        m2.Heat.Add(new HeatSource { Id = "gl", Name = "glazing", Kind = HeatKind.Surface, Center = new Vector3(1.75f, 1f, 1f),
            Width = 0.3f, Depth = 1.5f, Height = 1.5f, SurfaceTemperature = 5 });
        var fm2 = (int[])BuildThermalProbe(g2, m2, false)[2]!;
        int indoor = 0, outdoor = 0;
        for (int i = 0; i < fm2.Length; i++) if (fm2[i] != 0) { if (i % 14 >= 7) indoor++; else outdoor++; }
        Check("RT-02 Correlation Surface faces are side-specific: a box drawn into a one-cell partition from room A holds A's +x faces only (none facing B); A's air takes the host sum h A dT, B stays at 20 C; a glazing box on a one-cell exterior wall holds the indoor face only",
            facesA == 36 && facesB == 0 && other == 0 && err <= 0.02 && Math.Abs(riseB) <= 1e-3 * riseA && indoor == 36 && outdoor == 0,
            $"faces facing A {facesA} (36), facing B {facesB}, other {other} | air energy rise A {riseA:F1} J vs host sum {integral:F1} J ({err * 100:F2} %), B {riseB:F4} J | glazing: indoor faces {indoor} (36), outdoor {outdoor} | dt {dt * 1000:F2} ms");
    }

    /// <summary>RT-03: two Surface boxes that share a cell both keep their faces (a 40 C wall box and a 30 C floor box meeting along a corner: the
    /// corner cells hold -x at 40 C and -z at 30 C, the cells away from the corner just their own face). RT-05: a box buried in a thick floor says so.</summary>
    static void SurfaceSharedCellCheck()
    {
        const int n = 10;
        var (g, m) = ShellGrid(n, n, n, (x, y, z) => false);
        m.Heat.Add(new HeatSource { Id = "f", Name = "floor", Kind = HeatKind.Surface, Center = new Vector3(1.25f, 1.25f, 0.15f),
            Width = 2.5f, Depth = 2.5f, Height = 0.3f, SurfaceTemperature = 30 });
        m.Heat.Add(new HeatSource { Id = "w", Name = "wall", Kind = HeatKind.Surface, Center = new Vector3(0.15f, 1.25f, 1.25f),
            Width = 0.3f, Depth = 2.5f, Height = 2.5f, SurfaceTemperature = 40 });
        var r = BuildThermalProbe(g, m, false);
        var code = (int[])r[2]!; var temp = (float[])r[3]!; var coef = (float[])r[6]!;
        // per cell: the faces and their temperatures, decoded from the primary group and the table entry's two extra groups
        var faceT = new float[6];
        int corner = 0, cornerOk = 0, wallOnly = 0, floorOnly = 0, bad = 0;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == 0) continue;
            int x = i % n, y = i / n % n, z = i / (n * n);
            Array.Clear(faceT);
            int mk = code[i] & 63, e = (code[i] >> 7) * SurfaceStrideProbe;
            for (int b = 0; b < 6; b++) if ((mk >> b & 1) != 0) faceT[b] = temp[i];
            for (int j = 0; j < 2; j++)
            {
                int o = e + 3 + 5 * j, m2 = (int)coef[o];
                for (int b = 0; b < 6; b++) if ((m2 >> b & 1) != 0) faceT[b] = coef[o + 1];
            }
            bool wall = faceT[1] == 40f, floor = faceT[5] == 30f;
            int nFaces = faceT.Count(v => v != 0f);
            if (x == 1 && z == 1 && y >= 1 && y <= 8) { corner++; if (wall && floor && nFaces == 2) cornerOk++; }
            else if (nFaces == 1 && wall) wallOnly++;
            else if (nFaces == 1 && floor) floorOnly++;
            else bad++;
        }
        // RT-05: a 0.75 m floor slab (cells z 0-2); a box at z 0.1-0.4 lies inside it
        var (g3, m3) = ShellGrid(n, n, n, (x, y, z) => z < 3);
        m3.Heat.Add(new HeatSource { Id = "b", Name = "buried", Kind = HeatKind.Surface, Center = new Vector3(1.25f, 1.25f, 0.25f),
            Width = 2f, Depth = 2f, Height = 0.3f, SurfaceTemperature = 30 });
        BuildThermalProbe(g3, m3, false);
        string note = g3.Warnings.FirstOrDefault(w => w.StartsWith("Surface ")) ?? "";
        Check("RT-03 two Surface boxes sharing cells both keep their faces (corner of a 40 C wall box and a 30 C floor box); RT-05 a box buried in a thick floor says its box lies inside the solid",
            corner == 8 && cornerOk == 8 && bad == 0 && wallOnly > 0 && floorOnly > 0 && note.Contains("its box lies inside the solid; nothing touches air"),
            $"corner cells {corner} (8), holding both faces {cornerOk} | wall-only {wallOnly}, floor-only {floorOnly}, wrong {bad} | note: {(note.Length > 0 ? note : "none")}");
    }

    /// <summary>RU-06: a Surface box drawn across one side of a two-cell partition (Dx 0.25, slab x 3.0-3.5, box x 2.8-3.2, y and z 1.0-2.0: 1 m2) holds the same area whatever the
    /// grid offset: the same box on four grids shifted by 0, 1/4, 1/2 and 3/4 of a half cell (the faces' wall-surface crossings are recorded the way the voxeliser records them) holds
    /// 16 faces (+-1) on the drawn side and none facing the other side. RU-07: two identical Surface boxes over one wall: the earlier one holds nothing at the end, says so, and is
    /// left out of the dT scaling.</summary>
    static void SurfaceGridOffsetCheck()
    {
        const float dx = 0.25f, slabLo = 3.0f, slabHi = 3.5f;
        const int nx = 24, ny = 16, nz = 16;
        var counts = new List<int>(); int wrongSide = 0;
        foreach (float off in new[] { 0f, 0.0625f, 0.125f, 0.1875f })
        {
            float Cx(int x) => -off + (x + 0.5f) * dx;
            var (g, m) = ShellGrid(nx, ny, nz, (x, y, z) => Cx(x) >= slabLo && Cx(x) < slabHi);
            g.Origin = new Vector3(-off, -off, -off);
            int n = g.Count;
            g.WallLinks = new byte[19 * n];
            for (int i = 0; i < n; i++)
            {
                if (g.Cell[i] == VoxelGrid.Solid) continue;
                int x = i % nx;
                // +x neighbour in the slab: the surface at slabLo, the link the cell pulls from it is q = 2 (-x); -x neighbour in the slab: surface slabHi, q = 1
                if (x + 1 < nx && g.Cell[i + 1] == VoxelGrid.Solid && Cx(x + 1) >= slabLo && Cx(x + 1) < slabHi)
                    g.WallLinks[2 * n + i] = (byte)(1 + (int)MathF.Round(254 * Math.Clamp((slabLo - Cx(x)) / dx, 1f / 254, 1f)));
                if (x - 1 >= 0 && g.Cell[i - 1] == VoxelGrid.Solid && Cx(x - 1) >= slabLo && Cx(x - 1) < slabHi)
                    g.WallLinks[1 * n + i] = (byte)(1 + (int)MathF.Round(254 * Math.Clamp((Cx(x) - slabHi) / dx, 1f / 254, 1f)));
            }
            m.Heat.Add(new HeatSource { Id = "p", Name = "panel", Kind = HeatKind.Surface, Center = new Vector3(3.0f, 1.5f, 1.5f), Width = 0.4f, Depth = 1.0f, Height = 1.0f, SurfaceTemperature = 40 });
            var fixedMask = (int[])BuildThermalProbe(g, m, false)[2]!;
            int facing = 0;
            for (int i = 0; i < fixedMask.Length; i++)
            {
                if (fixedMask[i] == 0) continue;
                if ((fixedMask[i] & 63) == 1) facing++; else wrongSide++;
            }
            counts.Add(facing);
        }
        Check("RU-06 a Surface box drawn across one side of a wall holds the same area (1 m2 = 16 cell faces +-1) on four grid offsets, on the drawn side only",
            counts.All(c => Math.Abs(c - 16) <= 1) && wrongSide == 0,
            $"faces at offsets 0, 1/16, 1/8, 3/16 m: {string.Join(", ", counts)} (nominal 16) | faces on the other side or other bits {wrongSide}");

        var (g7, m7) = ShellGrid(10, 10, 10, (x, y, z) => false);
        var first = new HeatSource { Id = "w1", Name = "wall one", Kind = HeatKind.Surface, Center = new Vector3(0.15f, 1.25f, 1.25f), Width = 0.3f, Depth = 2.5f, Height = 2.5f, SurfaceTemperature = 40 };
        var second = new HeatSource { Id = "w2", Name = "wall two", Kind = HeatKind.Surface, Center = first.Center, Width = 0.3f, Depth = 2.5f, Height = 2.5f, SurfaceTemperature = 35 };
        m7.Heat.Add(first); m7.Heat.Add(second);
        var r7 = BuildThermalProbe(g7, m7, false);
        var unheld = (HashSet<HeatSource>)r7[5]!;
        int notes = g7.Warnings.Count(w => w.StartsWith("Surface wall one:") && w.Contains("hold all of its faces"));
        Check("RU-07 a Surface whose faces all went to a later box over the same wall says so and is left out of the dT scaling; the later box still holds them",
            unheld.Contains(first) && !unheld.Contains(second) && notes == 1,
            $"first unheld {unheld.Contains(first)}, second unheld {unheld.Contains(second)}, notes {notes}");
    }

    const int SurfaceStrideProbe = 13;   // LbmSolver.SurfaceStride (private)

    /// <summary>Independent host form of the correlation (double precision, Math.Pow; the solver uses float Newton roots).</summary>
    static double CorrelationH(double dT, int orient, double uTan, double height, double dh)
    {
        double d = Math.Abs(dT), hn;
        if (orient == 0) hn = Math.Pow(Math.Pow(1.5 * Math.Pow(d / height, 0.25), 6) + Math.Pow(1.23 * Math.Cbrt(d), 6), 1.0 / 6);
        else if ((orient == 2) == (dT > 0)) hn = Math.Pow(Math.Pow(1.4 * Math.Pow(d / dh, 0.25), 6) + Math.Pow(1.63 * Math.Cbrt(d), 6), 1.0 / 6);
        else hn = 0.6 * Math.Pow(d / (dh * dh), 0.2);
        double hf = 3.8 * uTan;
        return Math.Cbrt(hn * hn * hn + hf * hf * hf);
    }

    /// <summary>6e: a 2 x 2 x 3 m sealed adiabatic room at 20 C (Dx 0.25, CPU) whose x = 0 wall is a 40 C Surface over its full height
    /// (P14 T2's test: a 20 K vertical surface, natural convection about 3.4 W/m2K for H = 3 m). The T2 method: h = heat flux / (T_s - T of
    /// the first fluid cell), with the flux from the air's energy rise. Gate 3-4 W/m2K. Energy: the air's energy rise equals the time
    /// integral of sum h A (T_s - T_cell) evaluated on the host from the field every 10 steps, within 2 %.</summary>
    static void SurfaceHeatTransfer(ThermalScheme scheme)
    {
        const float dx = 0.25f, tAir = 20f, tWall = 40f;
        const int nx = 10, ny = 10, nz = 14;   // 8 x 8 x 12 air cells inside a one-cell solid shell
        var g = new VoxelGrid { Nx = nx, Ny = ny, Nz = nz, Dx = dx, Origin = Vector3.Zero, Cell = new byte[nx * ny * nz] };
        for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            g.Cell[g.Index(x, y, z)] = x == 0 || x == nx - 1 || y == 0 || y == ny - 1 || z == 0 || z == nz - 1 ? VoxelGrid.Solid : VoxelGrid.Air;
        var m = new BuildingModel
        {
            Name = "surface-h", GroundZ = 0,
            Storeys = { new Storey { Id = "L1", Name = "L1", Elevation = 0, Height = 3f, Analysed = true } },
            Env = new Environment { IndoorTemperature = tAir, OutdoorTemperature = tAir },
        };
        m.Obstacles.Add(new Mesh { Id = "dummy", Vertices = [new(0, 0, 0), new(nx * dx, 0, 0), new(nx * dx, ny * dx, 0), new(0, ny * dx, 0)], Indices = [0, 1, 2, 0, 2, 3] });
        // the box holds the wall cells (centre x 0.125) and the first air layer (0.375), the interior in y and z only
        m.Heat.Add(new HeatSource { Id = "w", Name = "warm wall", Kind = HeatKind.Surface, Center = new Vector3(0.25f, 1.25f, 1.75f),
            Width = 0.5f, Depth = 2f, Height = 3f, SurfaceTemperature = tWall });
        var opts = new RunOptions { Backend = Backend.Cpu, Thermal = true, ThermalScheme = scheme };
        using var s = LbmSolver.Start(g, m, Vector2.Zero, opts);
        int count = g.Count;
        var faceCells = new List<int>();
        for (int z = 1; z < nz - 1; z++) for (int y = 1; y < ny - 1; y++) faceCells.Add(g.Index(1, y, z));
        double area = faceCells.Count * dx * dx, cellV = dx * dx * dx, rhoCp = 1.2 * 1005, dt = s.TimeStepSeconds;
        var t = new float[count]; var ux = new float[count]; var uy = new float[count]; var uz = new float[count];
        double Energy()
        {
            s.ReadTemperature(t);
            if (scheme == ThermalScheme.Hybrid) return (s.HybridEnergy(_ => true) - tAir * s.HybridMassSum(_ => true)) * rhoCp * cellV;
            double e = 0; for (int i = 0; i < count; i++) if (g.Cell[i] == VoxelGrid.Air) e += t[i] - tAir;
            return e * rhoCp * cellV;
        }
        (double Watts, double MeanDt) Sample()
        {
            s.ReadTemperature(t); s.ReadVelocity(ux, uy, uz);
            double w = 0, d = 0;
            foreach (int i in faceCells)
            {
                double dT = tWall - t[i];
                w += CorrelationH(dT, 0, Math.Sqrt(uy[i] * uy[i] + uz[i] * uz[i]), 3.0, 2.0 * 0.5 * 2.0 / 2.5) * dx * dx * dT;
                d += dT;
            }
            return (w, d / faceCells.Count);
        }
        const int every = 10, samples = 60;
        double e0 = Energy(), integral = 0, dtSum = 0;
        var (wPrev, dPrev) = Sample();
        for (int k = 0; k < samples; k++)
        {
            s.Step(every);
            var (w, d) = Sample();
            integral += 0.5 * (wPrev + w) * every * dt; dtSum += 0.5 * (dPrev + d) * every * dt;
            wPrev = w; dPrev = d;
        }
        double rise = Energy() - e0, seconds = samples * every * dt;
        double h = rise / seconds / (area * (dtSum / seconds));
        double err = Math.Abs(rise - integral) / integral;
        Check($"6e Surface wall h by correlation, {scheme}: 20 K vertical wall (H 3 m) in quiescent air, T2 method h in [3, 4] W/m2K; air energy rise = integral of sum h A dT within 2 %",
            h >= 3 && h <= 4 && err <= 0.02,
            $"h={h:F3} W/m2K over {seconds:F1} s (mean T_s - T_cell {dtSum / seconds:F2} K, {faceCells.Count} faces = {area:F2} m2) | energy rise {rise:F1} J vs host sum h A dT {integral:F1} J ({err * 100:F2} %) | dt {dt * 1000:F2} ms");
    }

    /// <summary>RY-07: a 0.2 m slab at Dx 0.25 placed so that no cell centre falls in it (z 2.15-2.35; centres 2.125, 2.375) leaves no Solid
    /// cell; a chilled-ceiling Surface box drawn across its underside (z 2.1-2.3) holds the cut-link faces under it (the +z face of the
    /// 12 x 12 cells below, none above), and a box that stops short (z 1.9-2.1) holds none and says the surface is thinner than a cell.</summary>
    static void ThinSlabSurfaceCheck()
    {
        var model = new BuildingModel { Name = "thin-slab" };
        model.Obstacles.Add(Box(new Vector3(1, 1, 2.15f), new Vector3(5, 5, 2.35f)));
        var grid = Voxelizer.Build(model, 6912, 1, new DomainBox(new Vector3(0, 0, 0), new Vector3(6, 6, 3)), sealUnreachable: false);
        var thermal = typeof(LbmSolver).GetMethod("BuildThermal", BindingFlags.NonPublic | BindingFlags.Static)!;
        (int[] Fixed, List<string> Notes) Probe(float zLo, float zHi)
        {
            var view = grid.LbmView();
            var m = new BuildingModel { Obstacles = model.Obstacles };
            m.Heat.Add(new HeatSource { Id = "c", Name = "chilled", Kind = HeatKind.Surface, Center = new Vector3(3, 3, 0.5f * (zLo + zHi)),
                Width = 3, Depth = 3, Height = zHi - zLo, SurfaceTemperature = 16 });
            var r = (System.Runtime.CompilerServices.ITuple)thermal.Invoke(null, [view, m, false])!;
            return ((int[])r[2]!, view.Warnings.Where(w => w.StartsWith("Surface ")).ToList());
        }
        int centresIn = 0;
        for (int z = 0; z < grid.Nz; z++) { float cz = grid.Origin.Z + (z + 0.5f) * grid.Dx; if (cz > 2.15f && cz < 2.35f) centresIn++; }
        var (held, notes) = Probe(2.1f, 2.3f);
        int under = 0, other = 0;
        for (int i = 0; i < held.Length; i++)
        {
            if (held[i] == 0) continue;
            float cz = grid.Origin.Z + (i / (grid.Nx * grid.Ny) + 0.5f) * grid.Dx;
            if ((held[i] & 63) == 1 << 4 && MathF.Abs(cz - 2.125f) < 1e-3f) under++; else other++;
        }
        var (shortHeld, shortNotes) = Probe(1.9f, 2.1f);
        bool shortOk = shortHeld.All(v => v == 0) && shortNotes.Count == 1 && shortNotes[0].Contains("nothing is held at 16") && shortNotes[0].Contains("thinner than a cell");
        Check("RY-07 Surface box on a 0.2 m slab with no cell centre in it (Dx 0.25) holds the slab's underside by cut links; a box stopping short holds none and says the surface is thinner than a cell",
            grid.Dx == 0.25f && centresIn == 0 && under == 144 && other == 0 && notes.Count == 0 && shortOk,
            $"Dx={grid.Dx} centres in slab {centresIn} | held +z faces under the slab {under} (expected 144), other faces {other}, notes {notes.Count} | short box: held {shortHeld.Count(v => v != 0)}, note: {(shortNotes.Count > 0 ? shortNotes[0] : "none")}");
    }
}
