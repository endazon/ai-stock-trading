using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-17, FR-10, FR-19, FR-20: 全体前提条件（計画書 06_technical/05_trading-assumptions §5、
// 利用者決定 2026-07-06/07）の既定値。FR-17 で一元管理し、FR-10（リスク上限）・FR-19（取引ガード）・
// FR-20（段階ゲート）の既定値を提供する。個々の数値の逆算根拠は IADR-0002 を参照。
// 実運用では設定ストア（PostgreSQL）から読み込む。本クラスは初期値と検証用の基準を提供する。
public static class TradingDefaults
{
    /// <summary>初期投入資金 100,000 円（利用者決定 2026-07-07）。</summary>
    public const decimal InitialCapital = 100_000m;

    public static RiskLimitSettings CreateRiskLimits() => new()
    {
        // 1取引リスク1%・損切り幅3%なら1ポジション約3.3万円（前提条件の目安）を上限とする
        MaxOrderAmount = 35_000m,
        // 1日の発注累計は初期資金を超えない
        MaxDailyOrderAmount = 100_000m,
        // 2〜3銘柄分散が目安（前提条件）
        MaxOpenPositions = 3,
        // 日次損失上限: 資金の2%到達で当日全停止
        DailyLossLimitRatio = 0.02m,
        // 1取引あたりリスク: 資金の0.5〜1%（上限側を既定とする）
        PerTradeRiskRatio = 0.01m,
        // 最大DD上限: 10〜15%（保守側を既定とする）
        MaxDrawdownRatio = 0.10m,
        // 連敗時縮小: 3〜5連敗でサイズ半減（保守側を既定とする）
        LosingStreakThreshold = 3,
        LosingStreakSizeFactor = 0.5m,
    };

    public static TradingGuardSettings CreateGuardSettings() => new()
    {
        // 現物のみ有効。信用は米国株信用の最低保証金 2,500 USD に初期資金が満たないため無効
        EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash },
        // 米国株: 主ターゲット / 日本株: 当面監視・検証用（有効のまま）
        EnabledMarkets = new HashSet<Market> { Market.Japan, Market.UnitedStates },
        // 取引禁止銘柄（利用者登録 2026-07-07）
        BannedSymbols =
        [
            new BannedSymbol("6457", Market.Japan, "利用者登録: グローリー（利益相反回避）", new DateOnly(2026, 7, 7)),
            new BannedSymbol("6902", Market.Japan, "利用者登録: デンソー（利益相反回避）", new DateOnly(2026, 7, 7)),
            new BannedSymbol("6502", Market.Japan, "利用者登録: 東芝（旧。2023年上場廃止中のため再上場時に適用）", new DateOnly(2026, 7, 7)),
        ],
        PreventSameDayReentry = true,
    };

    public static StageSettings CreateStageSettings() =>
        // Stage 0（検証）から開始。ペーパーのみ・資金上限は初期投入資金
        new(TradingStage.Stage0Verification, TradeMode.Paper, InitialCapital);

    public static RiskManagementSettings CreateSettings() =>
        new(CreateGuardSettings(), CreateRiskLimits(), CreateStageSettings());
}
