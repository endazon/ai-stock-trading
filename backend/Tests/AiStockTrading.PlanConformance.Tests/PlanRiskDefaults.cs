namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// 計画書で確定したリスク統制・取引ガード・段階ゲートの既定値テーブル（FR-10, FR-19, FR-20）。
/// 出典は 06_technical/05_trading-assumptions §5 と ADR-0008 / ADR-0016 / ADR-0018。
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
    private const string Assumptions5 = "05_trading-assumptions §5";

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
        new(
            "RejectionReason.ShortSellReasons",
            "BorrowCostExceeded, BorrowUnavailable, DividendRecordDateNear, MaintenanceMarginBreach, "
                + "ShortExposureExceeded, ShortPriceFloorBreach, ShortSellDisabled",
            "ADR-0016 決定10（7 種。いずれもクラス A）"),

        // --- 段階ゲートと発注先（FR-20。段階と発注先は独立した 2 軸） ---
        new("BrokerProvider.Values", "InternalPaper, MoomooReal, MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Values", "Stage0Verification, Stage1Simulate, Stage2MinimalLive, Stage3ScaledLive", "FR-20"),
        new("Stage.Initial", "Stage0Verification", Assumptions5),
        new("Stage.Stage1BrokerProvider", "MoomooSimulate", $"{Assumptions5} / FR-20"),
        new("Stage.Stage2OrderableCapRatio", "total funds ratio 0.30", Assumptions5),
        new("Stage.WithdrawalDrawdownMultiple", "1.5", "ADR-0008"),
        new("Stage0.MaxDrawdownPassThreshold", "equity ratio 0.10", "ADR-0018"),
    ];

    public static IReadOnlyDictionary<string, PlanDefault> ByKey { get; } =
        All.ToDictionary(d => d.Key);
}
