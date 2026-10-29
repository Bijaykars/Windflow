namespace WindFlow.Validation;

/// <summary>Validation metrics for paired observations O (measured) and predictions P (simulated).
/// <para>Sources:</para>
/// <list type="bullet">
/// <item>FAC2, FB, NMSE: Chang, J.C. &amp; Hanna, S.R. (2004), "Air quality model performance evaluation",
/// Meteorology and Atmospheric Physics 87, 167-196. Acceptance used here (their section 5.3):
/// FAC2 &gt;= 0.5, |FB| &lt;= 0.3, NMSE &lt;= 1.5.</item>
/// <item>Near-zero handling for FAC2 (a pair counts as a hit when both |O| and |P| are below the
/// threshold W): Hanna, S.R. &amp; Chang, J.C. (2012), "Acceptance criteria for urban dispersion model
/// evaluation", Meteorology and Atmospheric Physics 116, 133-146. R2-03: that rule is NOT part of the published
/// indoor-CFD FAC2 (van Hooff et al. 2017 eq. 7), so <see cref="Result.Fac2"/> is the plain 0.5 &lt;= P/O &lt;= 2
/// fraction and the band is judged on it; <see cref="Result.Fac2W"/> keeps the W-rule version as a second figure.</item>
/// <item>Hit rate q: VDI 3783 Part 9 (2017), "Environmental meteorology - Prognostic microscale wind
/// field models - Evaluation for flow around buildings and obstacles", and COST Action 732 (Schatzmann
/// et al. 2010, "Model evaluation guidance and protocol document"): a pair is a hit when
/// |P - O| / |O| &lt;= D or |P - O| &lt;= W. D = 0.25; acceptance q &gt;= 0.66.</item>
/// </list>
/// <para>Signed velocity components: FB and NMSE were defined for positive quantities (concentrations),
/// and a profile that runs through a recirculation has a mean near zero, which makes the textbook forms
/// blow up. We therefore compute FB on magnitudes, FB = 2(mean|O| - mean|P|)/(mean|O| + mean|P|), and
/// NMSE = mean((O - P)^2) / (mean|O| * mean|P|) with the SIGNED difference in the numerator, so a sign
/// error is still penalised in full. FAC2 and q use the signed values: opposite signs are a miss unless
/// both values are inside W. Stated in VALIDATION.md next to the table.</para></summary>
public static class Metrics
{
    public const double D = 0.25;   // VDI 3783-9 relative tolerance

    public sealed record Result(int N, double Fac2, double Fb, double Nmse, double Q, double W, double Fac2W);

    public static Result Compute(IReadOnlyList<double> obs, IReadOnlyList<double> pred, double w)
    {
        if (obs.Count != pred.Count) throw new ArgumentException("obs and pred differ in length");
        int n = obs.Count;
        if (n == 0) return new Result(0, double.NaN, double.NaN, double.NaN, double.NaN, w, double.NaN);
        int fac2 = 0, fac2W = 0, hits = 0;
        double sumAbsO = 0, sumAbsP = 0, sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double o = obs[i], p = pred[i];
            sumAbsO += Math.Abs(o); sumAbsP += Math.Abs(p);
            sumSq += (o - p) * (o - p);

            bool factor2 = o != 0 && p / o >= 0.5 && p / o <= 2.0;   // negative ratio (sign flip) = miss
            if (factor2) fac2++;                                      // R2-03: the literature definition, no free hit below W
            if (factor2 || (Math.Abs(o) < w && Math.Abs(p) < w)) fac2W++;   // Hanna & Chang 2012 threshold

            double diff = Math.Abs(p - o);
            if (diff <= w || (o != 0 && diff / Math.Abs(o) <= D)) hits++;
        }
        double mo = sumAbsO / n, mp = sumAbsP / n;
        double fb = mo + mp > 0 ? 2 * (mo - mp) / (mo + mp) : 0;
        double nmse = mo * mp > 0 ? sumSq / n / (mo * mp) : double.NaN;
        return new Result(n, (double)fac2 / n, fb, nmse, (double)hits / n, w, (double)fac2W / n);
    }

    public static bool Passes(Result r) => r.Fac2 >= 0.5 && Math.Abs(r.Fb) <= 0.3 && r.Nmse <= 1.5 && r.Q >= 0.66;

    /// <summary>Splits the q (hit-rate) misses of a pair set into sign errors (opposite-sign O and P, outside W)
    /// and magnitude errors (same sign, or one of them ~0, but |P-O| too large): same hit test as Compute's q.</summary>
    public static (int N, int Miss, int SignMiss, int MagMiss) QBreakdown(IReadOnlyList<double> obs, IReadOnlyList<double> pred, double w)
    {
        int n = obs.Count, miss = 0, sign = 0, mag = 0;
        for (int i = 0; i < n; i++)
        {
            double o = obs[i], p = pred[i];
            double diff = Math.Abs(p - o);
            bool hit = diff <= w || (o != 0 && diff / Math.Abs(o) <= D);
            if (hit) continue;
            miss++;
            if (o != 0 && p != 0 && Math.Sign(o) != Math.Sign(p)) sign++; else mag++;
        }
        return (n, miss, sign, mag);
    }

    public const string Band = "FAC2 >= 0.5, |FB| <= 0.3, NMSE <= 1.5, q >= 0.66";

    /// <summary>Hand-computed synthetic cases: any change to a formula above makes one of these fail.</summary>
    public static int SelfTest()
    {
        int fails = 0;
        void Check(string name, double got, double want)
        {
            bool ok = Math.Abs(got - want) < 1e-12 || (double.IsNaN(got) && double.IsNaN(want));
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}: got {got:R}, want {want:R}");
            if (!ok) fails++;
        }

        // 1. Perfect prediction.
        var a = Compute([1, 2, 3, 4], [1, 2, 3, 4], 0.05);
        Check("perfect FAC2", a.Fac2, 1); Check("perfect FB", a.Fb, 0); Check("perfect NMSE", a.Nmse, 0); Check("perfect q", a.Q, 1);

        // 2. P = 2 O. FAC2 boundary is inclusive (ratio exactly 2 is a hit).
        //    FB = 2(2.5 - 5)/(2.5 + 5) = -2/3. NMSE = mean(O^2)/(2.5*5) = 7.5/12.5 = 0.6. q: every |P-O|/O = 1 > D -> 0.
        var b = Compute([1, 2, 3, 4], [2, 4, 6, 8], 0.05);
        Check("2x FAC2", b.Fac2, 1); Check("2x FB", b.Fb, -2.0 / 3.0); Check("2x NMSE", b.Nmse, 0.6); Check("2x q", b.Q, 0);

        // 3. Near-zero observations: both inside W -> hit for FAC2 and q, whatever the ratio or sign.
        //    R2-03: FAC2 itself has no near-zero rule any more (ratios 3 and -1 miss); the W-rule figure is Fac2W.
        var c = Compute([0.01, -0.02], [0.03, 0.02], 0.05);
        Check("near-zero FAC2 (literature, no W rule)", c.Fac2, 0); Check("near-zero FAC2 with the W rule", c.Fac2W, 1); Check("near-zero q", c.Q, 1);

        // 4. Sign flip outside W: miss for both.
        var d = Compute([0.5], [-0.5], 0.05);
        Check("sign-flip FAC2", d.Fac2, 0); Check("sign-flip q", d.Q, 0);
        //    NMSE = (1.0)^2 / (0.5 * 0.5) = 4; FB on magnitudes = 0.
        Check("sign-flip NMSE", d.Nmse, 4); Check("sign-flip FB", d.Fb, 0);

        // 5. Relative tolerance D = 0.25 is inclusive; 0.26 misses (W small enough not to rescue it).
        var e = Compute([1, 1], [1.25, 1.26], 0.001);
        Check("D boundary q", e.Q, 0.5);

        // 6. Absolute tolerance rescues a large relative error: O = 0.1, P = 0.14 -> rel 0.4 but |diff| 0.04 <= W.
        var f = Compute([0.1], [0.14], 0.05);
        Check("W rescue q", f.Q, 1);

        // 7. FAC2 lower bound inclusive (ratio 0.5), just below misses.
        var g = Compute([1, 1], [0.5, 0.49], 0.001);
        Check("FAC2 lower bound", g.Fac2, 0.5);

        var reversal = new System.Numerics.Vector3[] { new(0,1,0), new(0,-1,0) };
        Check("speed is magnitude of mean vector", BlockVelocity.Mean(reversal, v=>v.Length()), 0);
        double speedSe = BlockVelocity.StandardError(reversal, v=>v.Length());
        Check("reversing-vector bootstrap retains uncertainty", speedSe > 0.1 && speedSe < 0.6 ? 1 : 0, 1);
        Check("linear component averaging unchanged", BlockVelocity.Mean([new(2,0,0),new(4,0,0)],v=>v.X), 3);

        // ValidationCase's running field mean: the first block's field is the accumulator and later blocks fold into it
        // with FlowFieldMean.Add. The folded-in block must be left untouched and the result must be the mean.
        {
            WindFlow.Core.FlowField Block(float u) => new() { Ux = [u, u], Uy = [0, 0], Uz = [0, 0], P = [0, 0], AverageSamples = 10, Grid = new() { Nx = 2, Ny = 1, Nz = 1, Dx = 1, Cell = new byte[2] } };
            var acc = Block(1); var b2 = Block(3); var b3 = Block(8);
            WindFlow.Core.FlowFieldMean.Add(acc, b2); WindFlow.Core.FlowFieldMean.Add(acc, b3);
            Check("running block mean equals the arithmetic mean of three equal blocks", acc.Ux[0], 4);
            Check("running block mean leaves the folded-in block untouched", b2.Ux[0] == 3 && b3.Ux[1] == 8 && b2.AverageSamples == 10 ? 1 : 0, 1);
        }

        // MINIBAT n=5: two float-rounded cell centres both used to fall inside (-Dx/2, Dx/2].
        // Exercise the actual translated coordinates, both axes and both normal signs.
        foreach (int nozzleCells in new[] { 4, 5, 6 })
        foreach (bool alongX in new[] { false, true })
        {
            float dx = 0.1f / nozzleCells;
            int nx = 31 * nozzleCells + 4, ny = 34 * nozzleCells + 4, nz = 25 * nozzleCells + 3;
            var throatGrid = new WindFlow.Core.VoxelGrid { Nx = alongX ? ny : nx, Ny = alongX ? nx : ny,
                Nz = nz, Dx = dx, Origin = new(-2 * dx, -2 * dx, 0), Cell = new byte[nx * ny * nz] };
            var opening = new WindFlow.Core.Opening { IsOpen = true, Width = 0.2f, Height = 0.1f,
                Center = alongX ? new(3.1f + dx, 1.9f, 0.05f) : new(1.9f, 3.1f + dx, 0.05f),
                Normal = alongX ? System.Numerics.Vector3.UnitX : System.Numerics.Vector3.UnitY };
            var throat = WindFlow.Core.Analysis.ThroatCells(throatGrid, opening);
            int layers = throat.Select(i => alongX ? i % throatGrid.Nx : i / throatGrid.Nx % throatGrid.Ny).Distinct().Count();
            Check($"MINIBAT n{nozzleCells} {(alongX ? "X" : "Y")} throat has exactly one layer", layers, 1);
            Check($"MINIBAT n{nozzleCells} {(alongX ? "X" : "Y")} throat area cells", throat.Count, 2 * nozzleCells * nozzleCells);
            if (nozzleCells != 5)
                Check($"MINIBAT n{nozzleCells} {(alongX ? "X" : "Y")} retains the original throat layer", throat.All(i =>
                {
                    var position = throatGrid.CellCenter(i % throatGrid.Nx, i / throatGrid.Nx % throatGrid.Ny, i / (throatGrid.Nx * throatGrid.Ny));
                    float distance = alongX ? position.X - opening.Center.X : position.Y - opening.Center.Y;
                    return distance > -0.5f * dx && distance <= 0.5f * dx;
                }) ? 1 : 0, 1);
            opening.Normal = -opening.Normal;
            Check($"MINIBAT n{nozzleCells} {(alongX ? "X" : "Y")} throat normal reversal preserves cells",
                throat.SequenceEqual(WindFlow.Core.Analysis.ThroatCells(throatGrid, opening)) ? 1 : 0, 1);
        }

        SamplerSelfTest(Check);
        TracerTB16.SelfTest(Check);

        // MINIBAT scored/excluded split: every row is exactly one of ok / below0.1 / masked.
        var cnt = Minibat.CountStatuses(["ok", "ok", "below0.1", "masked", "masked", "masked"]);
        Check("minibat status counts (ok, below, masked)", cnt.Ok * 100 + cnt.Below * 10 + cnt.Masked, 213);

        Console.WriteLine(fails == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Sampler (trilinear on cell centres): an affine field is recovered exactly at any point between the first and
    /// last cell centres (faces and cell centres included); between the outermost centre and the domain face the edge cell
    /// is held constant and Audit says so; a stencil on a solid cell reads its share of 0 and Audit reports that share.</summary>
    static void SamplerSelfTest(Action<string, double, double> check)
    {
        var gr = new WindFlow.Core.VoxelGrid { Nx = 5, Ny = 4, Nz = 3, Dx = 0.5f, Origin = new(-1f, 2f, 10f), Cell = new byte[60] };
        var fl = new WindFlow.Core.FlowField { Grid = gr, Ux = new float[60], Uy = new float[60], Uz = new float[60] };
        float AU(System.Numerics.Vector3 c) => 1 + 2 * c.X + 3 * c.Y + 4 * c.Z;
        System.Numerics.Vector3 AV(System.Numerics.Vector3 c) => new(AU(c), -c.X + 0.5f * c.Z, c.Y);
        for (int z = 0; z < 3; z++) for (int y = 0; y < 4; y++) for (int x = 0; x < 5; x++)
        {
            var v = AV(gr.CellCenter(x, y, z)); int i = gr.Index(x, y, z);
            fl.Ux[i] = v.X; fl.Uy[i] = v.Y; fl.Uz[i] = v.Z;
        }
        var pts = new System.Numerics.Vector3[] { new(-0.75f, 2.25f, 10.25f), new(0f, 3f, 11f), new(0.3f, 2.9f, 10.6f), new(1.2f, 3.6f, 10.9f), new(1.25f, 3.75f, 11.25f), new(-0.2f, 2.5f, 10.5f) };
        bool exact = pts.All(p => (Sampler.Velocity(fl, p) - AV(p)).Length() < 1e-4f && !Sampler.Audit(gr, p).Clamped && Sampler.Audit(gr, p).SolidWeight == 0);
        check("sampler recovers an affine field exactly (centres, faces, interior, first/last centre)", exact ? 1 : 0, 1);
        var edge = new System.Numerics.Vector3(-0.9f, 2.25f, 10.25f);   // 0.1 m from the x = -1 face, centre of the first cell at -0.75
        var ae = Sampler.Audit(gr, edge);
        check("sampler holds the edge cell constant between the outermost centre and the domain face, and Audit says clamped",
            ae.Clamped && !ae.Outside && Math.Abs(Sampler.Velocity(fl, edge).X - AU(gr.CellCenter(0, 0, 0))) < 1e-4 ? 1 : 0, 1);
        check("Audit flags a point outside the domain", Sampler.Audit(gr, new(-1.2f, 2.25f, 10.25f)).Outside ? 1 : 0, 1);
        int sc = gr.Index(0, 1, 1);
        gr.Cell[sc] = WindFlow.Core.VoxelGrid.Solid; fl.Ux[sc] = 0; fl.Uy[sc] = 0; fl.Uz[sc] = 0;
        var half = (gr.CellCenter(0, 1, 1) + gr.CellCenter(1, 1, 1)) / 2;   // halfway between a solid cell centre and its air neighbour
        check("Audit reports half the stencil on a solid cell", Sampler.Audit(gr, half).SolidWeight, 0.5);
        check("a solid stencil reads its share of 0 (Velocity = half the air neighbour)", Math.Abs(Sampler.Velocity(fl, half).X - 0.5f * AU(gr.CellCenter(1, 1, 1))) < 1e-4 ? 1 : 0, 1);
    }
}
