namespace AiStockTrading.RiskManagement.Domain;

// FR-10: リスク統制の上限値。既定値は全体前提条件（05_trading-assumptions §5）に従う。
// 変更は方針確定プロセス（報告書）または利用者の設定変更のみ。生成AIは上書きできない。
public record RiskLimitSettings
{
    /// <summary>1注文あたり金額上限（基準通貨・円）。</summary>
    public required decimal MaxOrderAmount { get; init; }

    /// <summary>1日あたり発注金額上限（基準通貨・円）。</summary>
    public required decimal MaxDailyOrderAmount { get; init; }

    /// <summary>保有銘柄数上限。</summary>
    public required int MaxOpenPositions { get; init; }

    /// <summary>日次損失上限（資金比。到達で当日全停止）。</summary>
    public required decimal DailyLossLimitRatio { get; init; }

    /// <summary>1取引あたりリスク（資金比。ATR連動サイジングの基礎）。</summary>
    public required decimal PerTradeRiskRatio { get; init; }

    /// <summary>最大ドローダウン上限（到達で全停止・再検証）。</summary>
    public required decimal MaxDrawdownRatio { get; init; }

    /// <summary>連敗時縮小のしきい値（この連敗数でサイズ縮小）。</summary>
    public required int LosingStreakThreshold { get; init; }

    /// <summary>連敗時のサイズ縮小係数（既定: 半減）。</summary>
    public required decimal LosingStreakSizeFactor { get; init; }
}
