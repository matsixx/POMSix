using System;
using System.Reflection;

// Ground-truth test of POMSix HeightGen's normal→height integration (the REAL compiled method, via
// reflection). Synthesize a known height field = big tile-scale bowl + small stone bumps, encode its
// normal map exactly like the game's _NormalSAO (A = nx, G = ny), integrate, and measure.
static class Program
{
    static int Main()
    {
        const int n = 512;
        var rng = new Random(1234);
        var bowl = new double[n * n];
        var bumps = new double[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                bowl[y * n + x] = 25.0 * Math.Cos(2 * Math.PI * x / n) * Math.Cos(2 * Math.PI * y / n);
        for (int s = 0; s < 400; s++)
        {
            double cx = rng.NextDouble() * n, cy = rng.NextDouble() * n, amp = 2 + rng.NextDouble() * 3, sig = 3 + rng.NextDouble() * 4;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    dx -= n * Math.Round(dx / n); dy -= n * Math.Round(dy / n); // periodic
                    bumps[y * n + x] += amp * Math.Exp(-(dx * dx + dy * dy) / (2 * sig * sig));
                }
        }
        var h = new double[n * n];
        for (int i = 0; i < h.Length; i++) h[i] = bowl[i] + bumps[i];

        // Normal map: n = normalize(-dh/dx, -dh/dy, 1), central differences, packed like DXT5nm.
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dhdx = (h[y * n + (x + 1) % n] - h[y * n + (x - 1 + n) % n]) * 0.5;
                double dhdy = (h[((y + 1) % n) * n + x] - h[((y - 1 + n) % n) * n + x]) * 0.5;
                double len = Math.Sqrt(dhdx * dhdx + dhdy * dhdy + 1);
                double nx = -dhdx / len, ny = -dhdy / len;
                int i = (y * n + x) * 4;
                rgba[i + 3] = (byte)Math.Round((nx * 0.5 + 0.5) * 255);
                rgba[i + 1] = (byte)Math.Round((ny * 0.5 + 0.5) * 255);
            }

        MethodInfo integrate = typeof(POMSix.HeightGen).GetMethod("IntegrateAndNormalize", BindingFlags.NonPublic | BindingFlags.Static);
        object[] args = { rgba, n, 0f };
        var outH = (ushort[])integrate.Invoke(null, args);
        float range = (float)args[2];

        var rec = new double[outH.Length];
        ushort mn = ushort.MaxValue, mx = 0;
        for (int i = 0; i < outH.Length; i++) { rec[i] = outH[i]; if (outH[i] < mn) mn = outH[i]; if (outH[i] > mx) mx = outH[i]; }

        double corrBumps = Corr(rec, bumps), corrBowl = Corr(rec, bowl), corrFull = Corr(rec, h);
        Console.WriteLine("output range  : " + mn + ".." + mx + "  (measured relief range " + range.ToString("0.00") + ")");
        Console.WriteLine("corr vs bumps : " + corrBumps.ToString("0.000") + "   (stone detail — want high & POSITIVE)");
        Console.WriteLine("corr vs bowl  : " + corrBowl.ToString("0.000") + "   (tile-scale bowl — want near 0: high-pass removed it)");
        Console.WriteLine("corr vs full  : " + corrFull.ToString("0.000") + "   (v1 behaviour would track the bowl here)");

        // Mip chains: sizes and max >= avg everywhere.
        MethodInfo chain = typeof(POMSix.HeightGen).GetMethod("BuildChain", BindingFlags.NonPublic | BindingFlags.Static);
        // BuildChain uses the Res const (1024) — feed a 1024² field built by tiling the 512² output.
        var big = new ushort[1024 * 1024];
        for (int y = 0; y < 1024; y++) for (int x = 0; x < 1024; x++) big[y * 1024 + x] = outH[(y % n) * n + (x % n)];
        var avg = (ushort[][])chain.Invoke(null, new object[] { big, false });
        var max = (ushort[][])chain.Invoke(null, new object[] { big, true });
        bool maxOk = true;
        for (int m = 0; m < avg.Length && maxOk; m++)
            for (int i = 0; i < avg[m].Length; i++) if (max[m][i] < avg[m][i]) { maxOk = false; break; }
        Console.WriteLine("mip chains    : " + avg.Length + " levels (1024 -> " + (int)Math.Sqrt(avg[avg.Length - 1].Length) + "), max>=avg everywhere: " + maxOk);

        // Quantitative filter check: regress the output (back in integrated units) on bowl and bumps
        // jointly. The bowl is the pure (1,1) mode, r² = 2 cycles², so the analytic second-order
        // transfer is (2/(2+9))² = 0.0331 — the bowl coefficient must match that, and the bump
        // coefficient must stay large (stone-scale content preserved).
        var outUnits = new double[rec.Length];
        for (int i = 0; i < rec.Length; i++) outUnits[i] = rec[i] / 65535.0 * range;
        Regress(outUnits, bowl, bumps, out double aBowl, out double bBumps);
        double expected = Math.Pow(2.0 / 11.0, 2);
        Console.WriteLine("bowl transfer : " + aBowl.ToString("0.0000") + "   (analytic " + expected.ToString("0.0000") + ")");
        Console.WriteLine("bump transfer : " + bBumps.ToString("0.000") + "   (stone content kept; <1 = broad bump bases partially filtered)");
        Console.WriteLine("baseline corr(bumps, bowl) in the synthetic field: " + Corr(bumps, bowl).ToString("0.000"));

        bool pass = corrBumps > 0.85 && Math.Abs(aBowl - expected) < 0.01 && bBumps > 0.6
                    && mx > 60000 && mn < 5000 && avg.Length == 11 && maxOk;
        Console.WriteLine(pass ? "HEIGHTGEN TEST PASSED" : "HEIGHTGEN TEST FAILED");
        return pass ? 0 : 1;
    }

    // Least-squares y ≈ a·x1 + b·x2 + c (all mean-removed).
    static void Regress(double[] y, double[] x1, double[] x2, out double a, out double b)
    {
        double my = 0, m1 = 0, m2 = 0;
        for (int i = 0; i < y.Length; i++) { my += y[i]; m1 += x1[i]; m2 += x2[i]; }
        my /= y.Length; m1 /= y.Length; m2 /= y.Length;
        double s11 = 0, s22 = 0, s12 = 0, s1y = 0, s2y = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double d1 = x1[i] - m1, d2 = x2[i] - m2, dy = y[i] - my;
            s11 += d1 * d1; s22 += d2 * d2; s12 += d1 * d2; s1y += d1 * dy; s2y += d2 * dy;
        }
        double det = s11 * s22 - s12 * s12;
        a = (s1y * s22 - s2y * s12) / det;
        b = (s2y * s11 - s1y * s12) / det;
    }

    static double Corr(double[] a, double[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { double da = a[i] - ma, db = b[i] - mb; sab += da * db; saa += da * da; sbb += db * db; }
        return sab / Math.Sqrt(saa * sbb);
    }
}
