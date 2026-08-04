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
        // --- #329 リスク統制コア: 担当分の逸脱 6 件はすべて解消済み ---
        // 第 1 段階（IADR-0130）: Capital.Initial（→ USD 3000）/ RiskLimits.MaxOrderAmount
        // （→ equity ratio 0.25）/ RiskLimits.MaxDailyOrderAmount（→ equity ratio 1.50 per day）/
        // RiskLimits.LosingStreakThreshold（→ 5）。
        // 第 2 段階（IADR-0131）: ShortSell.Limits（→ 型 ShortSellingLimits・7 メンバ）/
        // RejectionReason.ShortSellReasons（→ 拒否理由 7 種。いずれもクラス A）。

        // --- #332 取引ガード: 担当分の逸脱 1 件は解消済み ---
        // IADR-0132: ProductType.Values（→ Cash, MarginLong, ShortSell の 3 値・独立制御）。
        // 既定「現物のみ有効」（Guard.EnabledProductTypes）は逸脱していなかったため登録が無い。

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
        new(
            "Stage0GateCriteria.MaxDrawdownTolerance",
            "ratio 0.15",
            333,
            "旧レンジ 10〜15% の上限側を採っている。ADR-0018 決定2 が運用の DD 停止ライン（10%）と同値へ"
                + "厳格化した。現状は Stage 0 が運用停止ラインより 5 ポイント緩い戦略を合格させ得る"
                + "（検証を通った戦略が運用開始と同時に停止条件へ抵触し得る）。"
                + "IADR-0045（0.15 採用の一次記録）・IADR-0110（凍結の記録）への追記も要る"),

        // --- #358 全体前提条件: §4 最小期待利益の計画追随漏れ ---
        new(
            "Assumptions.MinimumExpectedProfitMultiple",
            "1.5x of (round-trip cost)",
            358,
            "値 1.5 vs 2・基準に税を含まない、の 2 点乖離。計画は 2026-07-23 に「往復費用＋税の 2 倍」へ"
                + "確定したが（それ以前は未確定の <1.5 倍>）、2026-07-18 の実装が追随していない。"
                + "IADR-0021・docs/specs/20260718_trade-decision-profitability-gate.md・"
                + "docs/data/trading-assumptions.md の是正も #358 の範囲"),
    ];

    public static IReadOnlyDictionary<string, KnownDeviation> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
