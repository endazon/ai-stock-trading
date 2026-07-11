namespace AiStockTrading.Report.Domain;

// FR-06/16, 04_report-templates, IADR-0032: 日報 Markdown 生成の入力。数値は PnlSummary（コード集計値）、
// Narrative は LLM ドラフトの散文。ReportRenderer が本ビューから決定的に Markdown を組み立てる。
public sealed record DailyReportView
{
    /// <summary>自然キー（"daily-2026-07-10" 等）。フロントマターの period 導出にも用いる。</summary>
    public required string PeriodKey { get; init; }

    /// <summary>対象日（日報の対象営業日）。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>対象市場（"JP"/"US" 等）。フロントマター markets。</summary>
    public IReadOnlyList<string> Markets { get; init; } = [];

    /// <summary>適用した全体前提条件バージョン（FR-17）。</summary>
    public int AssumptionsVersion { get; init; }

    /// <summary>参照した上位方針の PeriodKey（daily→週報）。</summary>
    public string? BasedOn { get; init; }

    /// <summary>確定日時（未確定は null＝status: draft）。</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>コード集計した損益サマリ（数値は LLM に計算させない・FR-16）。</summary>
    public required PnlSummary Pnl { get; init; }

    /// <summary>当日の買い約定件数（取引回数の内訳）。</summary>
    public int BuyCount { get; init; }

    /// <summary>当日の売り約定件数（取引回数の内訳）。</summary>
    public int SellCount { get; init; }

    /// <summary>翌営業日の方針（確定で有効化される方針テキスト）。</summary>
    public string PolicySummary { get; init; } = string.Empty;

    /// <summary>LLM ドラフトの散文（市況・振り返り等）。数値は含めない。</summary>
    public string Narrative { get; init; } = string.Empty;
}
