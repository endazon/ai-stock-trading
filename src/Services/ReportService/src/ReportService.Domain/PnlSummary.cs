namespace AiStockTrading.Report.Domain;

// FR-16, 04_report-templates 数値定義: 期間の損益集計結果。数値はコードで集計する（LLM に計算させない）。
public sealed record PnlSummary(
    /// <summary>実現損益（税引前・費用前）＝約定代金差額の合計。</summary>
    decimal RealizedPnlGross,

    /// <summary>費用合計＝売買手数料＋取引諸費用＋為替スプレッド相当（CostCalculator）。</summary>
    decimal TotalCost,

    /// <summary>源泉徴収税額＝max(0, 実現損益(税引前) − 費用合計) × 譲渡益税率（利益にのみ課税）。</summary>
    decimal TaxWithheld,

    /// <summary>実現損益（税引後・費用込み）＝実現損益(税引前) − 費用合計 − 源泉徴収税額。</summary>
    decimal RealizedPnlNet,

    /// <summary>評価損益（税引前・参考）＝Σ 建玉 (現在値 − 平均取得単価)×数量。現在値の無い建玉は 0。</summary>
    decimal UnrealizedPnl,

    /// <summary>約定件数（fills 総数）。</summary>
    int TradeCount,

    /// <summary>決済（実現が発生した）件数。</summary>
    int RealizingTradeCount);
