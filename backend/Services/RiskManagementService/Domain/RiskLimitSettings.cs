namespace RiskManagementService.Domain;

// FR-10: リスク統制の上限値。既定値は全体前提条件（05_trading-assumptions §5）に従う。
// 変更は方針確定プロセス（報告書）または利用者の設定変更のみ。生成AIは上書きできない。
//
// FR-10, #329, IADR-0130 決定1: 金額で表す上限は**固定額ではなく equity（自己資金）比**で保持する
// （計画 §5・利用者委任に基づく決定 2026-08-02）。割合で持つことで、資金の増減に応じて各上限が比例調整され、
// 「資金だけ増えて上限が据え置き」という書き換え漏れが構造的に起こらない。
// 比率から金額への解決は本レコードの解決メソッドだけを通す（呼び出し側で equity × 比率 と書かない。
// equity の定義＝どの時点の値かが呼び出し側ごとにぶれるため）。
public record RiskLimitSettings
{
    /// <summary>
    /// 1 注文あたりの発注金額上限（equity 比。既定 0.25 ＝ equity の 25%）。FR-10, 05_trading-assumptions §5。
    /// 解決は <see cref="MaxOrderAmountFor"/>。
    /// </summary>
    public required decimal MaxOrderAmountRatio { get; init; }

    /// <summary>
    /// 1 日あたりの発注金額上限（equity 比・日次。既定 1.50 ＝ equity の 150%/日）。FR-10, §5。
    /// **新規建ての発注代金の合計**で判定し、手仕舞い（決済）注文は算入しない（IADR-0130 決定4・#302）。
    /// 解決は <see cref="MaxDailyOrderAmountFor"/>。
    /// </summary>
    public required decimal MaxDailyOrderAmountRatio { get; init; }

    /// <summary>
    /// 保有**建玉**数上限（既定 3。ADR-0016 決定9）。同一銘柄で複数の建玉（現物と信用買いの併存・分割
    /// エントリー）を持ち得るため、「保有銘柄数」では数えない（計画 §5 が語の使用を禁じている）。
    /// </summary>
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

    /// <summary>
    /// FR-10, IADR-0130 決定1: 1 注文あたりの発注金額上限を equity から解決する。
    /// </summary>
    /// <param name="equity">
    /// 判定に用いる自己資金（equity）＝**前営業日終値時点の評価額**（計画 §5 注記）。
    /// 実装上は <c>PortfolioSnapshot.Capital</c>（当日中は不変）。
    /// </param>
    public decimal MaxOrderAmountFor(decimal equity) => equity * MaxOrderAmountRatio;

    /// <summary>
    /// FR-10, IADR-0130 決定1: 1 日あたりの発注金額上限を equity から解決する（新規建てのみが算入対象）。
    /// </summary>
    /// <param name="equity">1 注文上限と同じ equity（<see cref="MaxOrderAmountFor"/> 参照）。</param>
    public decimal MaxDailyOrderAmountFor(decimal equity) => equity * MaxDailyOrderAmountRatio;
}
