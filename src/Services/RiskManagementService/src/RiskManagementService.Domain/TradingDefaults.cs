using AiStockTrading.RiskManagement.Domain.Manipulation;
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

    /// <summary>
    /// 既定損切り幅比率 3%（前提条件 05_trading-assumptions §5 の「損切り幅3%」目安）。
    /// FR-03/FR-10, IADR-0030: 損切り価格の権威データ（取引判断の ATR 連動 stopLossDistancePerShare）が
    /// 発注/約定パイプラインに永続化されるまで、平均取得単価からの近似導出に用いる過渡的既定値。
    /// </summary>
    public const decimal DefaultStopLossRatio = 0.03m;

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

    // FR-19, IADR-0037: 相場操縦検知の既定しきい値。自己資金・低頻度（30 分判断サイクル）のリテール運用を前提に、
    // 正常なデイトレード（値動きに応じた建て直し・数件の取消）を誤検知せず濫用パターンだけを捕捉する保守側の初期値。
    // 各値の逆算根拠は IADR-0037。運用ログで誤検知/見逃しを評価して較正する。
    public static ManipulationDetectionSettings CreateManipulationDetectionSettings() => new()
    {
        // 見せ玉・レイヤリングは短時間の連続発注に現れる。判断サイクル（30 分）より十分短い突発窓。
        LookbackWindow = TimeSpan.FromMinutes(5),
        // これ未満は統計的に濫用と正常を区別できない（数件の取消は正常運用でも起こり得る）。
        MinimumSampleSize = 5,
        // 窓内の約定なし取消が発注の 7 割超は約定志向の運用として過剰。
        MaxCancellationRatio = 0.7m,
        // 1 発注あたり平均 3 回超の訂正反復は板操作的（正常な建て直しは通常 0〜1 回）。
        MaxAmendmentsPerOrder = 3.0m,
        // 窓内の約定/一部約定が発注の 1 割未満＝約定意思の希薄さ（見せ玉の兆候）。
        MinFillRatio = 0.1m,
        // 発注→即取消（2 秒以内）の反復は見せ玉の典型（人手・通常アルゴの反応より速い）。
        ShortLivedCancelThreshold = TimeSpan.FromSeconds(2),
        // 短命取消が 3 件以上で見せ玉パターンとみなす（低約定率と AND）。
        MaxShortLivedCancels = 3,
        // 同一方向・約定なし取消の同時生存が 3 本以上＝板に複数段を並べる見せ板の型。
        LayeringOrderCount = 3,
    };
}
