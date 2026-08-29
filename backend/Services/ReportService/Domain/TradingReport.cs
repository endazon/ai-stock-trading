namespace ReportService.Domain;

// FR-06: 報告書の種別（日報・週報・月報）。階層は 月報→週報→日報（BasedOn で上位方針を参照）。
public enum ReportKind
{
    Daily,
    Weekly,
    Monthly,
}

// FR-07: 報告書の状態。確定（Confirmed）した方針のみ取引に適用される（確定前は不適用・ADR-0003）。
public enum ReportState
{
    Draft,
    Confirmed,
}

// FR-06/07/16/17: 報告書。PeriodKey（"daily-2026-07-10" 等）を自然キーとする。PolicySummary は翌期間の方針（確定で有効化）。
// 数値集計（損益・費用・税）は後続スライス（FR-16）で拡充する。AssumptionsVersion は適用した全体前提条件のバージョン（FR-17）。
public sealed record TradingReport
{
    public required string PeriodKey { get; init; }

    public required ReportKind Kind { get; init; }

    /// <summary>対象期間の開始日（日報=当日、週報=週初、月報=月初）。最新の確定済み日報の判定に用いる。</summary>
    public required DateOnly PeriodStart { get; init; }

    public ReportState State { get; init; } = ReportState.Draft;

    /// <summary>参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報）。</summary>
    public string? BasedOn { get; init; }

    /// <summary>適用した全体前提条件のバージョン（FR-17）。</summary>
    public int AssumptionsVersion { get; init; }

    /// <summary>翌期間の方針（確定で取引方針として有効化される）。</summary>
    public string PolicySummary { get; init; } = string.Empty;

    /// <summary>
    /// FR-06, IADR-0115, #280: 報告書の本文（Markdown・ReportRenderer の出力）。自動生成スケジューラが保存し、
    /// 利用者がレビューする実体。手動 upsert 経路（PUT /reports/{periodKey}）は本文を受け取らないため空のまま
    /// （既存 API 互換）。既存行は空文字として読み出す。
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>確定日時（確定時に記録）。</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }
}
