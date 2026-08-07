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

        // --- #381 為替レート源と鮮度: ADR-0022（2026-08-04 新設）への追随待ち ---
        // 本 3 件は「実装が先に決めていた値を計画が引き取った」型の逸脱である（#358 と同型だが向きが逆で、
        // 計画側が動いた結果として生じた）。IADR-0134 決定3 の運用規律に従い、planning のピンを
        // d980a01 へ進めた同じ PR で計画側の表へ転記し、乖離をここへ登録した（IADR-0135）。
        new(
            "Fx.RateSourceProviders",
            "fred",
            381,
            "為替レート源が FRED 単独。計画は日銀「外国為替市況（日次）」を第一・FRED をフォールバックとする"
                + "2 源を求める（ADR-0022 決定1・2）。DEXJPUS は系列こそ営業日次だが**公表は週次**であり、"
                + "裁定「為替レートを日次化する」（2026-07-30）を満たさない。日銀アダプタの新設・"
                + "フォールバック切り替えの日報／監査ログ記録と Discord 通知は #381 の担当。"
                + "順位そのものは値ではないため本表は集合で比べる（順位・通知は #381 のテスト仕様で担保）"),

    ];

    public static IReadOnlyDictionary<string, KnownDeviation> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
