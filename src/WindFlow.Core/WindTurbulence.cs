namespace WindFlow.Core;

/// <summary>Synthetic approach-flow turbulence for the wind inflow ring (RunOptions.WindTurbulence; null = off).
/// Random Fourier modes (Kraichnan 1970; Smirnov, Shi &amp; Celik 2001) with a Gaussian energy spectrum of integral
/// scale <see cref="LengthScale"/>, convected with the mean wind by Taylor's frozen-turbulence hypothesis, plus a random
/// temporal frequency per mode (omega_m = g_m / tau, g_m ~ N(0,1), tau = L / sigma_u the eddy-turnover time at
/// <see cref="TimeRefHeight"/>) as in Smirnov et al. Without it a mode with k nearly perpendicular to the wind has a
/// near-zero frequency and the inlet signal never decorrelates. The fluctuation is scaled per component by sigma_i(z) = I_i(z) x U(z) and added to the imposed inflow velocity only.</summary>
public sealed class WindTurbulence
{
    /// <summary>Integral length scale, metres (model units).</summary>
    public float LengthScale = 1f;
    /// <summary>Streamwise turbulence intensity sigma_u / U(z) as a function of height above ground, metres. When null,
    /// <see cref="RefIu"/> x (z / <see cref="RefHeight"/>)^-<see cref="Exponent"/> is used.</summary>
    public Func<float, float>? Iu;
    public float RefIu, Exponent, RefHeight = 10f;
    /// <summary>sigma_v / sigma_u and sigma_w / sigma_u (standard ABL 0.8 / 0.5).</summary>
    public float IvRatio = 0.8f, IwRatio = 0.5f;
    /// <summary>Number of Fourier modes.</summary>
    public int Modes = 64;
    /// <summary>Height above ground, metres, at which tau = L / (Iu x U) is taken; the height L was derived at. 0 = <see cref="RefHeight"/>.</summary>
    public float TimeRefHeight;

    internal bool Active => LengthScale > 0f && (Iu != null || RefIu > 0f);

    internal float IuAt(float z) =>
        Iu != null ? Iu(z) : RefIu * MathF.Pow(MathF.Max(z, 0.05f * RefHeight) / RefHeight, -Exponent);
}

/// <summary>The mode set: drawn once from the seed, evaluated per ring cell per step. Lattice units throughout
/// (positions in cells, time in steps, wavenumbers in rad per cell).</summary>
internal sealed class WindTurbulenceField
{
    readonly int _n;
    readonly float[] _kx, _ky, _kz, _px, _py, _pz, _qx, _qy, _qz;
    readonly double[] _g;      // per-mode N(0,1) draw; omega_m = g_m / tau
    readonly double[] _om;     // rad per step; zero until SetTimeScale

    public WindTurbulenceField(int modes, float lengthCells, int seed)
    {
        _n = Math.Max(1, modes);
        _kx = new float[_n]; _ky = new float[_n]; _kz = new float[_n];
        _px = new float[_n]; _py = new float[_n]; _pz = new float[_n];
        _qx = new float[_n]; _qy = new float[_n]; _qz = new float[_n];
        var rng = new Random(seed);
        // Gaussian spectrum E(k) ~ k^4 exp(-2 k^2 / k0^2): |k|^2 = chi2(5 dof) k0^2 / 4. Its longitudinal integral
        // scale is L11 = sqrt(2 pi) / k0 (Pope 2000 s6.5, u'^2 = 2/3 int E), so L11 = L for k0 = sqrt(2 pi) / L (P13b-01).
        double k0 = Math.Sqrt(2.0 * Math.PI) / lengthCells;
        double c = Math.Sqrt(3.0 / (2.0 * _n));      // unit variance per component: E p_i^2 = 2/3
        for (int m = 0; m < _n; m++)
        {
            double chi = 0; for (int j = 0; j < 5; j++) { double g = Gauss(rng); chi += g * g; }
            double kmag = Math.Sqrt(chi) * k0 * 0.5;
            double dx, dy, dz, len;
            do { dx = Gauss(rng); dy = Gauss(rng); dz = Gauss(rng); len = Math.Sqrt(dx * dx + dy * dy + dz * dz); } while (len < 1e-6);
            dx /= len; dy /= len; dz /= len;
            _kx[m] = (float)(kmag * dx); _ky[m] = (float)(kmag * dy); _kz[m] = (float)(kmag * dz);
            Cross(rng, dx, dy, dz, c, out _px[m], out _py[m], out _pz[m]);   // p, q perpendicular to k: divergence-free
            Cross(rng, dx, dy, dz, c, out _qx[m], out _qy[m], out _qz[m]);
        }
        _g = new double[_n]; _om = new double[_n];
        for (int m = 0; m < _n; m++) _g[m] = Gauss(rng);   // after every other draw: k, p, q stay bit-identical
    }

    /// <summary>Sets tau (steps): omega_m = g_m / tau. tau &lt;= 0 leaves the pure frozen field.</summary>
    internal void SetTimeScale(double tauSteps)
    {
        for (int m = 0; m < _n; m++) _om[m] = tauSteps > 0 ? _g[m] / tauSteps : 0;
    }

    internal int Count => _n;

    /// <summary>The fixed mode tables for the GPU kernel, nine blocks of Count floats: kx ky kz px py pz qx qy qz.</summary>
    internal float[] PackModes()
    {
        var a = new float[9 * _n];
        float[][] src = [_kx, _ky, _kz, _px, _py, _pz, _qx, _qy, _qz];
        for (int b = 0; b < 9; b++) Array.Copy(src[b], 0, a, b * _n, _n);
        return a;
    }

    /// <summary>Per-mode time phase -(kx ux + ky uy) t + omega_m t, formed in double and reduced to (-pi, pi] so a late step does not
    /// lose float precision (the space part k.x is added in float by the kernel).</summary>
    internal void TimePhases(double ux, double uy, double t, float[] ph)
    {
        for (int m = 0; m < _n; m++)
            ph[m] = (float)Math.IEEERemainder((-((double)_kx[m] * ux + (double)_ky[m] * uy) + _om[m]) * t, 2.0 * Math.PI);
    }

    static double Gauss(Random r) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - r.NextDouble())) * Math.Cos(2.0 * Math.PI * r.NextDouble());

    static void Cross(Random r, double kx, double ky, double kz, double c, out float ox, out float oy, out float oz)
    {
        double zx = Gauss(r), zy = Gauss(r), zz = Gauss(r);
        ox = (float)(c * (zy * kz - zz * ky)); oy = (float)(c * (zz * kx - zx * kz)); oz = (float)(c * (zx * ky - zy * kx));
    }

    /// <summary>Unit-variance isotropic field at lattice position (x,y,z) cells and step t, convected by (ux,uy) cells/step.</summary>
    public void Eval(float x, float y, float z, float t, float ux, float uy, out float gx, out float gy, out float gz)
    {
        float xs = x - ux * t, ys = y - uy * t;
        float ax = 0, ay = 0, az = 0;
        for (int m = 0; m < _n; m++)
        {
            float ph = _kx[m] * xs + _ky[m] * ys + _kz[m] * z + (float)Math.IEEERemainder(_om[m] * t, 2.0 * Math.PI);
            float cs = MathF.Cos(ph), sn = MathF.Sin(ph);
            ax += _px[m] * cs + _qx[m] * sn; ay += _py[m] * cs + _qy[m] * sn; az += _pz[m] * cs + _qz[m] * sn;
        }
        gx = ax; gy = ay; gz = az;
    }
}
