namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画確定値からの既知の逸脱の登録簿（全面再実装 #344 の途上で一時的に受容するもの）。
/// <para>
/// <b>登録簿へ行を足してよいのは、担当 issue が決まっている逸脱だけ</b>である。
/// 担当 issue が値を修正すると <see cref="KnownDeviation.ActualValue"/> が実際値と食い違い、
/// <see cref="PlanConformanceTests"/> の「登録済み逸脱は実際に逸脱している」検査が失敗する。
/// これにより逸脱の解消が登録簿へ反映されることが機械的に保証される（IADR-0127）。
/// </para>
/// </summary>
public static class KnownPlanDeviations
{
    public static IReadOnlyList<KnownDeviation> All { get; } =
    [
        // --- #329 リスク統制コア: 金額系 3 値の equity 割合化・空売り統制・拒否理由 7 種 ---
        new(
            "Capital.Initial",
            "JPY 100000 (fixed amount)",
            329,
            "旧資金 100,000 円のまま。計画は増資後の $3,000（USD 建て）を確定値とする"),
        new(
            "RiskLimits.MaxOrderAmount",
            "JPY 35000 (fixed amount)",
            329,
            "固定額で保持している。計画は equity 比 25% での保持を求める（資金増減に比例調整させるため）"),
        new(
            "RiskLimits.MaxDailyOrderAmount",
            "JPY 100000 (fixed amount)",
            329,
            "固定額で保持している。計画は equity 比 150%/日（新規建てのみ算入）"),
        new(
            "RiskLimits.LosingStreakThreshold",
            "3",
            329,
            "旧レンジ 3〜5 の保守側を採っていた。ADR-0018 が確定単一値 5 へ同期した"),
        new(
            "ShortSell.Limits",
            "(type ShortSellingLimits not found)",
            329,
            "空売り専用統制（ADR-0016 決定2,3,4,7,9）が未実装。専用型の追加が必要"),
        new(
            "RejectionReason.ShortSellReasons",
            "(none of the RejectionReason members defined)",
            329,
            "空売り固有の拒否理由 7 種（ADR-0016 決定10）が未定義。いずれもクラス A として扱う"),

        // --- #332 取引ガード: 商品種別の 3 値化 ---
        new(
            "ProductType.Values",
            "Cash, Margin",
            332,
            "信用を 1 値でまとめている。計画は現物 / 信用買い / 空売り の 3 値で独立に制御する"),

        // --- #333 段階ゲート / #334 段階×発注先の 2 軸分離 ---
        new(
            "BrokerProvider.Values",
            "(type BrokerProvider not found)",
            334,
            "発注先が TradeMode（Paper/Live）に融合している。計画は段階と独立した 3 値の軸を求める"),
        new(
            "Stage.Values",
            "Stage0Verification, Stage1Paper, Stage2MinimalLive, Stage3ScaledLive",
            333,
            "Stage 1 の呼称が Paper。計画では Stage 1 は moomoo SIMULATE であり内蔵 paper とは別物"),
        new(
            "Stage.Stage1BrokerProvider",
            "Paper",
            334,
            "Stage 1 が内蔵ペーパーを指している。計画は moomoo SIMULATE（3 か月）"),
        new(
            "Stage.Stage2OrderableCapRatio",
            "JPY 35000 (fixed amount)",
            333,
            "固定額で保持している。計画は総資金比 30%（$900）を発注可能額としてシステム側で制限する"),
    ];

    public static IReadOnlyDictionary<string, KnownDeviation> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
