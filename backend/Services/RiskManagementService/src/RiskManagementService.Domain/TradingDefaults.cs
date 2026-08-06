using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-17, FR-10, FR-19, FR-20: 全体前提条件（計画書 06_technical/05_trading-assumptions §5、
// 利用者決定 2026-07-06/07）の既定値。FR-17 で一元管理し、FR-10（リスク上限）・FR-19（取引ガード）・
// FR-20（段階ゲート）の既定値を提供する。個々の数値の逆算根拠は IADR-0002 を参照。
// 実運用では設定ストア（PostgreSQL）から読み込む。本クラスは初期値と検証用の基準を提供する。
public static class TradingDefaults
{
    /// <summary>
    /// FR-10, FR-17, #329, IADR-0130 決定3: 初期投入資金 **$3,000**（利用者決定 2026-07-31・
    /// 05_trading-assumptions §5）。信用取引の解禁に伴い旧 100,000 円から増資した確定値である。
    /// **判定に用いる自己資金（equity）の権威値**であり、計画適合検査（IADR-0127）はここを抽出する。
    /// </summary>
    public const decimal InitialEquityUsd = 3_000m;

    /// <summary>
    /// FR-10, #329, #364: 統制判定に用いる自己資金（equity）の通貨。計画 §3 は判定の基準通貨を **USD**
    /// （表示は JPY）と定める（利用者決定 2026-07-31）。単位の取り違え（USD / JPY）を検知可能にするため、
    /// 金額そのものと通貨を対にして保持する。IADR-0152 決定1 により <c>MarketCurrency.Base</c> と一致する。
    /// </summary>
    public const Currency EquityCurrency = Currency.Usd;

    /// <summary>
    /// 基準通貨建ての初期投入資金。
    /// <para>
    /// #364, IADR-0152 決定3: 基準通貨が USD になったため、**<see cref="InitialEquityUsd"/> そのもの**である。
    /// 旧実装は計画 §5 記載の参照レート（1 USD ≈ 163.7 円）で JPY 基準のパイプラインへ 1 点換算していたが
    /// （IADR-0130 決定3）、供給先が USD になった以上その換算は不要であり、参照レート定数ごと削除した。
    /// </para>
    /// <para>
    /// 統制の実効は equity に対する比率であるため、equity と注文金額を同一通貨で評価する限り判定結果は
    /// 通貨に依存しない（IADR-0130 決定3。プロパティベーステストで固定）。本移行で比率は 1 つも変わっていない。
    /// </para>
    /// </summary>
    public const decimal InitialCapital = InitialEquityUsd;

    /// <summary>
    /// 既定損切り幅比率 3%（前提条件 05_trading-assumptions §5 の「損切り幅3%」目安）。
    /// FR-03/FR-10, IADR-0030: 損切り価格の権威データ（取引判断の ATR 連動 stopLossDistancePerShare）が
    /// 発注/約定パイプラインに永続化されるまで、平均取得単価からの近似導出に用いる過渡的既定値。
    /// </summary>
    public const decimal DefaultStopLossRatio = 0.03m;

    // FR-10, #329, ADR-0018, IADR-0130: 既定値はすべて計画の**確定単一値**である（レンジ表記は用いない）。
    // 金額系 3 値は equity 比で保持し、固定額では持たない（05_trading-assumptions §5 注記）。
    public static RiskLimitSettings CreateRiskLimits() => new()
    {
        // 1 注文あたりの発注金額上限: equity の 25%（$3,000 で $750）。単一建玉への集中上限。
        // 1 取引リスク 1% と併用し厳しい方が効く（本上限が効くのはストップ幅が 4% より狭い場合）。
        MaxOrderAmountRatio = 0.25m,
        // 1 日あたりの発注金額上限: equity の 150%/日（$3,000 で $4,500）。目的は暴走の遮断であり、
        // 損失の統制は日次損失上限（2%）が担う。新規建てのみ算入し決済は算入しない（#302 の裁定）。
        MaxDailyOrderAmountRatio = 1.50m,
        // 保有建玉数上限 3（ADR-0016 決定9）。「保有銘柄数」では数えない。
        MaxOpenPositions = 3,
        // 日次損失上限: 資金の 2% 到達で当日全停止・翌営業日までロックアウト
        DailyLossLimitRatio = 0.02m,
        // 1 取引あたりリスク: 資金の 1%（ATR 連動サイジングの基礎。ADR-0018 決定1）
        PerTradeRiskRatio = 0.01m,
        // 最大 DD 上限: 10% 到達で全停止・再検証（ADR-0018 決定1）
        MaxDrawdownRatio = 0.10m,
        // 連敗時縮小: 5 連敗でサイズ半減（ADR-0018 決定1。旧レンジ「3〜5」の保守側 3 からの是正）
        LosingStreakThreshold = 5,
        LosingStreakSizeFactor = 0.5m,
    };

    // FR-10, UC-06, ADR-0016 決定2,3,4,5,7,9, #329 第 2 段階: 空売り専用統制の既定値。
    // 空売りの有効・無効は取引ガードの商品種別が持つ（#332・IADR-0132 決定2。既定は現物のみ有効＝空売りは無効）。
    // 統制値は無効時も保持し、段階解禁（Stage 3・自己資金 $5,000 以上）で有効化した瞬間から
    // 計画どおりの上限が効くようにする。
    public static ShortSellSettings CreateShortSellSettings() => new()
    {
        Limits = new ShortSellingLimits
        {
            // 1 銘柄あたりの空売り建玉: equity の 10%（$3,000 で $300）。決定2(a)
            PerSymbolCapRatio = 0.10m,
            // 借株料: 年率 20% 超は拒否。照会不可なら空売りしない。決定3
            BorrowRateCapAnnual = 0.20m,
            // 維持率: 自前 40%。実効値は規制要求（max($5.00 ÷ 株価, 30%)）との厳しい方。決定7
            MaintenanceMarginThreshold = 0.40m,
            // 維持率割れによる自動縮小の回復目標: 適用される閾値 + 5 ポイント（§5・#90 第 10 回）
            MaintenanceRecoveryTargetOffset = 0.05m,
            // 空売り対象の株価下限: USD 5.00（未満は対象外）。決定7
            PriceFloorUsd = 5.00m,
            // 空売り比率: 建玉総額の 50% を超えない。決定9
            ExposureRatioCap = 0.50m,
            // 強制買戻し検知銘柄の空売り禁止期間: 30 日。決定4
            BuyInBanDurationDays = 30,
        },
    };

    // FR-19, ADR-0007, ADR-0016 決定1, #332: 取引ガードの既定値（計画 §5・FR-19 本文）。
    public static TradingGuardSettings CreateGuardSettings() => new()
    {
        // 商品種別は 3 値（現物 / 信用買い / 空売り）をそれぞれ独立に制御する。
        // **既定はいずれも「現物のみ有効」**（ADR-0016 決定1）。信用買い・空売りの実弾解禁は Stage 3 であり
        // （決定8）、空売りはさらに自己資金 $5,000 以上を要する。
        EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash },
        // 米国株: 主ターゲット / 日本株: 当面監視・検証用（有効のまま）
        EnabledMarkets = new HashSet<Market> { Market.Japan, Market.UnitedStates },
        // 取引禁止銘柄（利用者登録 2026-07-07。INDEX 決定 20）。理由と登録日を伴って記録する
        BannedSymbols =
        [
            new BannedSymbol("6457", Market.Japan, "利用者登録: グローリー（利益相反回避）", new DateOnly(2026, 7, 7)),
            new BannedSymbol("6902", Market.Japan, "利用者登録: デンソー（利益相反回避）", new DateOnly(2026, 7, 7)),
            new BannedSymbol("6502", Market.Japan, "利用者登録: 東芝（旧。2023年上場廃止中のため再上場時に適用）", new DateOnly(2026, 7, 7)),
        ],
        PreventSameDayReentry = true,
    };

    public static StageSettings CreateStageSettings() =>
        // Stage 0（検証）から開始。既定の発注先は内蔵 paper（外部へ発注しない）・発注可能額は総資金の全額
        // （段階としての絞りは無い）
        new(TradingStage.Stage0Verification, BrokerProvider.InternalPaper, FullCapitalCapRatio);

    /// <summary>
    /// FR-20, ADR-0008: 撤退基準の DD 倍率 1.5（実DD ≥ バックテスト最大DD × 1.5 で自動停止・再検証）。
    /// </summary>
    public const decimal WithdrawalDrawdownMultiple = 1.5m;

    /// <summary>
    /// FR-20, #333: 段階として絞りを掛けない発注可能額の比率（総資金の 100%）。
    /// Stage 0 / Stage 1 は実弾を撃たず（既定の発注先が <see cref="BrokerProvider.MoomooReal"/> ではない）、
    /// Stage 3 は計画上「最大 100%」まで
    /// 段階的に増額する（05_trading-assumptions §5）。いずれも段階としての金額の絞りは無く、実効的な上限は
    /// FR-10 の統制上限（1 注文 25% / 1 日 150%）が担う。
    /// </summary>
    public const decimal FullCapitalCapRatio = 1.00m;

    /// <summary>
    /// FR-20, 05_trading-assumptions §5「運用段階（Stage）」, #333: Stage 2（最小実弾）の発注可能額
    /// ＝**総資金の 30%**（$3,000 で $900）。
    /// <para>
    /// 計画は「口座には総資金 $3,000 を入れ、**発注可能額をシステム側の統制で 30% に制限する**
    /// （口座への入金額は制限しない）」と定める。旧実装は固定額 35,000 円であり、これは旧資金 100,000 円を
    /// 前提とした値であって増資後の $3,000 とは整合しなかった（KnownPlanDeviations
    /// `Stage.Stage2OrderableCapRatio`。#333 で解消）。
    /// </para>
    /// <para>
    /// **段階制約と FR-10 の統制上限は両方を満たす必要がある**（計画 §5 注記）。equity $3,000 では
    /// 1 注文上限 $750（25%）が本段階の発注可能額 $900 の 83% にあたるため、保有建玉数上限 3 を満たすには
    /// 1 建玉あたり $300 が実効上限になる。常に厳しい方が効く。
    /// </para>
    /// </summary>
    public const decimal Stage2MinimalLiveCapitalCapRatio = 0.30m;

    // FR-20, ADR-0008, #334, IADR-0140: 段階ゲート方針（4 段階の既定発注先／発注可能額＋撤退倍率）。
    // **ここに置く発注先は「段階が定める既定の組み合わせ」にすぎない**（FR-20）。現在の発注先は
    // RiskManagementSettings.BrokerProvider が独立に保持し、段階を変えても自動では追随しない。
    // Stage 0＝内蔵 paper（外部へ発注しない）、Stage 1＝moomoo SIMULATE、Stage 2/3＝moomoo REAL（実弾）。
    // 昇格・差し戻しは合格・撤退基準に基づき利用者が承認する（遷移ロジックは StageGate）。
    public static StageGatePolicy CreateStagePolicy() => new()
    {
        Definitions = new Dictionary<TradingStage, StageSettings>
        {
            // 検証: 既定は内蔵 paper（擬似約定・外部へ発注しない）。段階としての金額の絞りは無い
            [TradingStage.Stage0Verification] =
                new(TradingStage.Stage0Verification, BrokerProvider.InternalPaper, FullCapitalCapRatio),
            // SIMULATE: moomoo `SIMULATE`（OpenD 経由のデモ環境）による 3 か月の検証（06_daytrading-review §4 表）。
            // #334, IADR-0140: 既定の発注先は **MoomooSimulate**。内蔵 `paper`（擬似約定）はデバッグ用であり
            // 本段階の検証手段としない——既定を内蔵 paper のままにすると、60 営業日・100 件という合格証跡が
            // 外部へ一度も発注していない擬似約定で積み上がる（FR-20）。
            [TradingStage.Stage1Simulate] =
                new(TradingStage.Stage1Simulate, BrokerProvider.MoomooSimulate, FullCapitalCapRatio),
            // 最小実弾: 既定は実弾（moomoo REAL）・発注可能額は総資金の 30%（計画 §5・#333）
            [TradingStage.Stage2MinimalLive] =
                new(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, Stage2MinimalLiveCapitalCapRatio),
            // 段階増額: 既定は実弾（moomoo REAL）・最大 100% まで（増額は月報レビュー時に FR-17 設定で確定）
            [TradingStage.Stage3ScaledLive] =
                new(TradingStage.Stage3ScaledLive, BrokerProvider.MoomooReal, FullCapitalCapRatio),
        },
        WithdrawalDrawdownMultiple = WithdrawalDrawdownMultiple,
    };

    public static RiskManagementSettings CreateSettings() =>
        new(CreateGuardSettings(), CreateRiskLimits(), CreateStageSettings())
        {
            ShortSell = CreateShortSellSettings(),
        };

    // FR-19, IADR-0040: 相場操縦検知の既定しきい値。自己資金・低頻度（30 分判断サイクル）のリテール運用を前提に、
    // 正常なデイトレード（値動きに応じた建て直し・数件の取消）を誤検知せず濫用パターンだけを捕捉する保守側の初期値。
    // 各値の逆算根拠は IADR-0040。運用ログで誤検知/見逃しを評価して較正する。
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
