namespace AiStockTrading.Backtest.Domain.Tests.Calibration;

// FR-15, ADR-0008, #208, IADR-0109: 閾値較正専用の決定論的な正規乱数源（splitmix64 ＋ Box-Muller）。
//
// `System.Random` を使わない理由: 種を固定しても系列がランタイム実装に依存するため、IADR に記録した較正値を
// 後から再現できる保証が無い。較正の根拠は「同じ種なら同じ数値」が成り立って初めて検証可能になる。
//
// 注意: Math.Log / Math.Cos の最終ビットはプラットフォーム間で完全一致が保証されないため、記録する統計量
// （偽陽性率など）は同一ランタイムでの再現を前提とする。テストの判定は厳密一致ではなく許容幅で行う。
internal sealed class DeterministicNormalSampler(ulong seed)
{
    private ulong _state = seed;
    private double? _spare;

    public double NextStandardNormal()
    {
        // Box-Muller は 1 回の計算で 2 つの独立な標準正規乱数を生むため、片方を次回へ持ち越す。
        if (_spare is { } spare)
        {
            _spare = null;
            return spare;
        }

        // u1 は 0 を避ける（Log(0) が -∞ になるため）。
        var u1 = NextUnitInterval();
        while (u1 <= double.Epsilon)
            u1 = NextUnitInterval();
        var u2 = NextUnitInterval();

        var radius = Math.Sqrt(-2.0 * Math.Log(u1));
        var angle = 2.0 * Math.PI * u2;

        _spare = radius * Math.Sin(angle);
        return radius * Math.Cos(angle);
    }

    /// <summary>指定長の標準正規標本を返す（1 候補ぶんの日次リターン列）。</summary>
    public double[] NextSeries(int length)
    {
        var series = new double[length];
        for (var i = 0; i < length; i++)
            series[i] = NextStandardNormal();
        return series;
    }

    // splitmix64（状態 64bit・定数は原論文どおり）。実装が閉じているためランタイムに依存しない。
    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // [0, 1) の一様乱数（上位 53bit を使う＝double の仮数部に合わせる）。
    private double NextUnitInterval() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
