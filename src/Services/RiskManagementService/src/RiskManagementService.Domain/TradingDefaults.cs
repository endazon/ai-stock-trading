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

    /// <summary>
    /// FR-20, ADR-0008: 撤退基準の DD 倍率 1.5（実DD ≥ バックテスト最大DD × 1.5 で自動停止・再検証）。
    /// </summary>
    public const decimal WithdrawalDrawdownMultiple = 1.5m;

    /// <summary>
    /// FR-20, 06_daytrading-review §4: Stage 2（最小実弾）の資金上限。最小単元・最小資金の保守的な暫定既定
    /// （1 ポジション相当＝MaxOrderAmount）。実運用値は利用者が FR-17 設定で確定・変更する（IADR-0041）。
    /// </summary>
    public const decimal Stage2MinimalLiveCapitalCap = 35_000m;

    // FR-20, ADR-0008: 段階ゲート方針（4 段階の Mode/資金上限＋撤退倍率）。Stage 0/1＝ペーパー、Stage 2/3＝実弾。
    // 昇格・差し戻しは合格・撤退基準に基づき利用者が承認する（遷移ロジックは StageGate）。
    public static StageGatePolicy CreateStagePolicy() => new()
    {
        Definitions = new Dictionary<TradingStage, StageSettings>
        {
            // 検証: ペーパーのみ・資金上限は初期投入資金
            [TradingStage.Stage0Verification] = new(TradingStage.Stage0Verification, TradeMode.Paper, InitialCapital),
            // ペーパー: 検証と同条件（実装・運用・報告サイクルの検証）
            [TradingStage.Stage1Paper] = new(TradingStage.Stage1Paper, TradeMode.Paper, InitialCapital),
            // 最小実弾: 実弾モード・最小資金（保守的暫定既定）
            [TradingStage.Stage2MinimalLive] = new(TradingStage.Stage2MinimalLive, TradeMode.Live, Stage2MinimalLiveCapitalCap),
            // 段階増額: 実弾モード・初期投入資金まで（以降の増額は月報レビュー時に FR-17 設定で確定）
            [TradingStage.Stage3ScaledLive] = new(TradingStage.Stage3ScaledLive, TradeMode.Live, InitialCapital),
        },
        WithdrawalDrawdownMultiple = WithdrawalDrawdownMultiple,
    };

    public static RiskManagementSettings CreateSettings() =>
        new(CreateGuardSettings(), CreateRiskLimits(), CreateStageSettings());
}
