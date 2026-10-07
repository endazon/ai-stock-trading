namespace RiskManagementService.Domain;

// FR-10, #1176, IADR-0495 決定1・2: 新規建ての**最小の名目額**（equity × しきい値。既定 1%＝TradingDefaults.MinEntryNotionalRatio）の判定。
// サイジング（PositionSizer）と同じ置き場に置き、取引判断サービス（サイジングの実行者）が同じ関数を呼ぶ。純関数（時計・I/O なし）。
//
//   - IsBelow: サイジングの結果の名目額（数量 × 参照価格・基準通貨）が最小に**満たない**か。ちょうど等しいときは通す。
//   - CapacityCannotReach: 新規建てに使える金額の上限（1 注文上限と、段階残枠・日次残枠の小さい方の、さらに小さい方）が最小に届かないか。
//     名目額は CalculateCappedQuantity の金額キャップによりこの上限を超えないため、真なら LLM の結論に依らず必ず IsBelow になる
//     （LLM の前に分かる下界）。
//   - しきい値 0 は「統制を外す」（どちらも常に false）。
public static class MinimumEntryNotional
{
    /// <summary>しきい値の上限（1 注文あたりの上限の既定＝equity の 25%）。これを超えると新規建てが構造的に成立しない。</summary>
    public const decimal MaxRatio = 0.25m;

    /// <summary>しきい値の妥当性（0 以上・<see cref="MaxRatio"/> 以下）。外れれば例外（起動を止める側の検査）。</summary>
    public static decimal Validate(decimal ratio) =>
        ratio is >= 0m and <= MaxRatio
            ? ratio
            : throw new ArgumentOutOfRangeException(
                nameof(ratio), ratio, $"最小の名目額のしきい値（equity 比）は 0 以上 {MaxRatio} 以下でなければならない");

    /// <summary>最小の名目額（基準通貨）。</summary>
    public static decimal MinimumFor(decimal equity, decimal ratio) => equity * ratio;

    /// <summary>名目額（基準通貨）が最小に満たないか（しきい値 0 なら常に false）。</summary>
    public static bool IsBelow(decimal notional, decimal equity, decimal ratio) =>
        ratio > 0m && notional < MinimumFor(equity, ratio);

    /// <summary>新規建てに使える金額の上限が最小に届かないか（しきい値 0 なら常に false）。</summary>
    public static bool CapacityCannotReach(decimal equity, decimal maxOrderAmount, decimal availableCapital, decimal ratio) =>
        IsBelow(Math.Min(maxOrderAmount, availableCapital), equity, ratio);
}
