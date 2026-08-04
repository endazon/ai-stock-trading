namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画書で確定した既定値テーブル（FR-10, FR-17, FR-19, FR-20）。
/// 出典は 06_technical/05_trading-assumptions の §5（リスク統制・取引ガード）・§1（税制）・§4（計算方針）・
/// §6（運用費用上限）と ADR-0008 / ADR-0016 / ADR-0018。
/// <para>
/// 本テーブルが**計画側の単一情報源**である。実装値をここへ写してはならない（実装のスナップショットを
/// 固定するだけになり、計画との乖離を永久に検知できなくなる — #306 の再発）。
/// </para>
/// <para>
/// 収録するのは**実装から機械的に抽出できる値**（定数・設定フィールド・enum・型の有無）に限る。
/// 「厳しい方が効く」「kill switch 中でも手仕舞いは止めない」のような**振る舞いの規則**は値ではないため
/// 本表では扱わず、テスト仕様書（<c>docs/tests/</c>）の 3 点セット（境界値・プロパティベース・否定形）で
/// 担当 issue が検証する（IADR-0127 決定4）。
/// </para>
/// </summary>
public static class PlanRiskDefaults
{
    private const string Assumptions1 = "05_trading-assumptions §1";
    private const string Assumptions4 = "05_trading-assumptions §4";
    private const string Assumptions5 = "05_trading-assumptions §5";
    private const string Assumptions6 = "05_trading-assumptions §6";

    public static IReadOnlyList<PlanDefault> All { get; } =
    [
        // --- 資金と金額系の統制上限 3 値（equity 比で保持し、固定額では持たない） ---
        new("Capital.Initial", "USD 3000", Assumptions5),
        new("RiskLimits.MaxOrderAmount", "equity ratio 0.25", Assumptions5),
        new("RiskLimits.MaxDailyOrderAmount", "equity ratio 1.50 per day", Assumptions5),
        new("RiskLimits.MaxOpenPositions", "3", $"{Assumptions5} / ADR-0016 決定9"),

        // --- 損失・サイジング系（ADR-0018 で確定単一値へ同期） ---
        new("RiskLimits.DailyLossLimitRatio", "equity ratio 0.02", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.PerTradeRiskRatio", "equity ratio 0.01", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.MaxDrawdownRatio", "equity ratio 0.10", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.LosingStreakThreshold", "5", $"{Assumptions5} / ADR-0018"),
        new("RiskLimits.LosingStreakSizeFactor", "0.5", $"{Assumptions5} / ADR-0018"),

        // --- 取引ガード（FR-19。商品種別は 3 値で独立制御） ---
        new("ProductType.Values", "Cash, MarginLong, ShortSell", $"{Assumptions5} / ADR-0016 決定8"),
        new("Market.Values", "Japan, UnitedStates", Assumptions5),
        // 本行は「設定の既定値（いずれも現物のみ有効）」であり、段階別の可否
        // （Stage 1＝3 種／Stage 2＝現物のみ／Stage 3＝条件付き全種）は**別の規則**である。
        // 段階×商品種別が結線される時点（#332 / #333 / #334）で、既定値と段階別強制を
        // 混同しないこと。段階別の強制は値ではなく振る舞いのため、3 点セットのテストで検証する。
        new("Guard.EnabledProductTypes", "Cash", Assumptions5),
        new("Guard.EnabledMarkets", "Japan, UnitedStates", Assumptions5),
        // 比較は序数順に正規化するため、登録順ではなく昇順で記す。
        new("Guard.BannedSymbols", "6457/Japan, 6502/Japan, 6902/Japan", Assumptions5),
        new("Guard.PreventSameDayReentry", "True", $"{Assumptions5}（適用範囲は日本株現物。FR-19）"),
        new("Guard.ProhibitManipulativeOrderPatterns", "True", Assumptions5),

        // --- 空売り専用統制（ADR-0016）。値の集合は専用型 ShortSellingLimits が保持する想定 ---
        new(
            "ShortSell.Limits",
            "type ShortSellingLimits with members: BorrowRateCapAnnual, BuyInBanDurationDays, "
                + "ExposureRatioCap, MaintenanceMarginThreshold, MaintenanceRecoveryTargetOffset, "
                + "PerSymbolCapRatio, PriceFloorUsd",
            "ADR-0016 決定2,3,4,7,9 / UC-06"),
        // 2026-08-04 改訂: 7 種 → 9 種（`StopOrderRequired` の追認・`BuyInBanned` の新設）。
        // `BuyInBanned` を `BorrowUnavailable` へ写像してはならない（決定10 の 2026-08-04 追記）。
        new(
            "RejectionReason.ShortSellReasons",
            "BorrowCostExceeded, BorrowUnavailable, BuyInBanned, DividendRecordDateNear, "
                + "MaintenanceMarginBreach, ShortExposureExceeded, ShortPriceFloorBreach, "
                + "ShortSellDisabled, StopOrderRequired",
            "ADR-0016 決定10（9 種。いずれもクラス A。2026-08-04 に 7 種から改訂）"),

        // --- 段階ゲートと発注先（FR-20。段階と発注先は独立した 2 軸） ---
        new("BrokerProvider.Values", "InternalPaper, MoomooReal, MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Values", "Stage0Verification, Stage1Simulate, Stage2MinimalLive, Stage3ScaledLive", "FR-20"),
        new("Stage.Initial", "Stage0Verification", Assumptions5),
        new("Stage.Stage1BrokerProvider", "MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Stage2OrderableCapRatio", "total funds ratio 0.30", Assumptions5),
        new("Stage.WithdrawalDrawdownMultiple", "1.5", "ADR-0008"),
        // ADR-0018 決定2 が名指しするのは Stage 0 合格判定の許容値（Stage0GateCriteria）であり、
        // 運用の DD 停止ライン（RiskLimits.MaxDrawdownRatio）とは**別のフィールド**である。
        // 両者を取り違えると「たまたま計画と一致している別の値」を見て逸脱を見逃す。
        new("Stage0GateCriteria.MaxDrawdownTolerance", "ratio 0.10", "ADR-0018 決定2"),

        // --- 全体前提条件の確定値（FR-17）。§5 と同じく利用者決定であり、実装は
        //     TradingAssumptionsDefaults.Create() が保持する。§2/§3 は「要確認」のため確定値を持たず対象外。 ---
        new(
            "Assumptions.CapitalGainsTaxRate",
            "realized gain ratio 0.20315",
            $"{Assumptions1}（20.315% ＝ 所得税 15.315% ＋ 住民税 5%）"),
        // 倍率と**基準**の両方を正規化に含める。基準を含めないと「倍率だけ 2 へ直し、税を基準に
        // 含めないまま」という中途半端な追随を素通しする（計画は「往復費用**＋税**の 2 倍」）。
        new(
            "Assumptions.MinimumExpectedProfitMultiple",
            "2x of (round-trip cost + tax)",
            $"{Assumptions4}（利用者決定 2026-07-23）"),
        // 月次費用上限は円建ての「月あたりの上限額」であり、割合でも一回限りの金額でもない。
        new("CostLimits.Total", "JPY 20000 per month", $"{Assumptions6}（LLM＋インフラ＋データの合計）"),
        new("CostLimits.Llm", "JPY 15000 per month", $"{Assumptions6}（対象は取引判断サイクルのみ。§6.1）"),
        new("CostLimits.Infrastructure", "JPY 5000 per month", Assumptions6),
        new("CostLimits.Data", "JPY 0 per month", $"{Assumptions6}（有料情報源の導入時に総枠内で配分）"),
    ];

    public static IReadOnlyDictionary<string, PlanDefault> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
