namespace AiStockTrading.Backtest.Domain;

// FR-15, IADR-0038: 標準正規分布の CDF / 逆 CDF（外部依存なしの純実装）。DSR の算出に用いる。
// CDF は Abramowitz-Stegun 7.1.26 の erf 近似（誤差 ~1.5e-7）、逆 CDF は Acklam のアルゴリズム（相対誤差 ~1.15e-9）。
public static class NormalDistribution
{
    // 標準正規 CDF: Φ(z) = 0.5 (1 + erf(z/√2))。
    public static double Cdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    private static double Erf(double x)
    {
        // A&S 7.1.26。x<0 は erf(x) = −erf(−x)。
        var sign = Math.Sign(x);
        var ax = Math.Abs(x);
        const double p = 0.3275911;
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        var t = 1.0 / (1.0 + p * ax);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-ax * ax);
        return sign * y;
    }

    // 標準正規逆 CDF（分位点）。定義域は (0,1) の開区間。
    public static double InverseCdf(double p)
    {
        if (p <= 0.0 || p >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(p), p, "p は (0,1) の開区間である必要があります。");

        // Acklam の有理近似。
        const double a1 = -3.969683028665376e+01;
        const double a2 = 2.209460984245205e+02;
        const double a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02;
        const double a5 = -3.066479806614716e+01;
        const double a6 = 2.506628277459239e+00;

        const double b1 = -5.447609879822406e+01;
        const double b2 = 1.615858368580409e+02;
        const double b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01;
        const double b5 = -1.328068155288572e+01;

        const double c1 = -7.784894002430293e-03;
        const double c2 = -3.223964580411365e-01;
        const double c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00;
        const double c5 = 4.374664141464968e+00;
        const double c6 = 2.938163982698783e+00;

        const double d1 = 7.784695709041462e-03;
        const double d2 = 3.224671290700398e-01;
        const double d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        double q, r, x;
        if (p < pLow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(p));
            x = (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }
        else if (p <= pHigh)
        {
            q = p - 0.5;
            r = q * q;
            x = (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
        }
        else
        {
            q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            x = -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }

        // 1 回のハレー法で精緻化（A&S erf 経由の CDF で補正）。
        var e = Cdf(x) - p;
        var u = e * Math.Sqrt(2.0 * Math.PI) * Math.Exp(x * x / 2.0);
        x -= u / (1.0 + x * u / 2.0);
        return x;
    }
}
