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

        // --- #333 段階ゲート / #334 段階×発注先の 2 軸分離: 担当分の逸脱はすべて解消済み ---
        // #333（IADR-0136 / 0137 / 0138 / 0139）:
        //   Stage.Values（→ Stage0Verification, Stage1Simulate, Stage2MinimalLive, Stage3ScaledLive）
        //   Stage.Stage2OrderableCapRatio（→ total funds ratio 0.30。計画 §5・IADR-0136）
        //   Stage0GateCriteria.MaxDrawdownTolerance（→ ratio 0.10。ADR-0018 決定2・IADR-0138）
        // #334（IADR-0140）:
        //   BrokerProvider.Values（→ InternalPaper, MoomooReal, MoomooSimulate。TradeMode を置換）
        //   Stage.Stage1BrokerProvider（→ MoomooSimulate。06_daytrading-review §4 表）

        // --- #381 為替レート源と鮮度: 担当分の逸脱 3 件はすべて解消済み ---
        // 本 3 件は「実装が先に決めていた値を計画が引き取った」型の逸脱であった（#358 と同型だが向きが逆で、
        // 計画側が動いた結果として生じた）。IADR-0134 決定3 の運用規律に従い、planning のピンを
        // d980a01 へ進めた同じ PR で計画側の表へ転記し、乖離をここへ登録していた（IADR-0135）。
        // PR #455（IADR-0174）: Fx.StaleRateWarningDays（→ 5 days）/ Fx.MaxRateAgeDays（→ 30 days）。
        // 本 PR（IADR-0194）: Fx.RateSourceProviders（→ boj, fred）。日銀アダプタを新設して第一とし、
        //   FRED を順位つきフォールバックにしたため、実装値が計画（ADR-0022 決定1・2）と一致した。
        //   **順位そのものは値ではないため本表は集合で比べる**——順位・切替の可視化は #381 の担当のまま残る
        //   （切替の日報／監査ログ記録と Discord 通知は #381 の第 2 層。IADR-0194 §残余リスク）。

    ];

    public static IReadOnlyDictionary<string, KnownDeviation> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
