namespace AiStockTrading.Report.Domain;

// FR-06/16, 04_report-templates, IADR-0032: 報告書 Markdown 生成の入力（日報/週報/月報 共通）。数値は PnlSummary
// （コード集計値）、Narrative は LLM ドラフトの散文。ReportRenderer が Kind に応じて決定的に Markdown を組み立てる。
public sealed record ReportView
{
    /// <summary>報告書種別（Daily/Weekly/Monthly）。テンプレート・見出し・サマリ項目を切り替える。</summary>
    public required ReportKind Kind { get; init; }

    /// <summary>自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"）。</summary>
    public required string PeriodKey { get; init; }

    /// <summary>フロントマター period・タイトルの期間表記（ReportPeriod.Label）。</summary>
    public required string PeriodLabel { get; init; }

    /// <summary>対象市場（"JP"/"US" 等）。フロントマター markets。</summary>
    public IReadOnlyList<string> Markets { get; init; } = [];

    /// <summary>適用した全体前提条件バージョン（FR-17）。</summary>
    public int AssumptionsVersion { get; init; }

    /// <summary>参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報）。</summary>
    public string? BasedOn { get; init; }

    /// <summary>確定日時（未確定は null＝status: draft）。</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>コード集計した損益サマリ（数値は LLM に計算させない・FR-16）。</summary>
    public required PnlSummary Pnl { get; init; }

    /// <summary>買い約定件数（取引回数の内訳）。</summary>
    public int BuyCount { get; init; }

    /// <summary>売り約定件数（取引回数の内訳）。</summary>
    public int SellCount { get; init; }

    /// <summary>翌期間の方針（確定で有効化される方針テキスト）。</summary>
    public string PolicySummary { get; init; } = string.Empty;

    /// <summary>LLM ドラフトの散文（市況・振り返り・評価等）。数値は含めない。</summary>
    public string Narrative { get; init; } = string.Empty;
}
