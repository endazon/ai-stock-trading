using ReportService.Domain;

namespace ReportService.Infrastructure.Persistence;

// FR-07, FR-15, ADR-0068 決定2・3, #1373, IADR-0548 決定1: 確定した版の写し（確定のたびに 1 行。書き換えない）。
// reports の行（会話キーごとに 1 行）は現在の状態を持ち、こちらは「どの版がいつ確定したか」の履歴を持つ。
// 鍵は (Kind, Sequence)。Sequence は種別ごとの確定の通し番号（1 から）で、同じ種別の確定が並行すると鍵が衝突する
// （= 確定の順序を 1 保存操作の中で決める足場。段 B の「なお効いている」の判定と確定を 1 保存操作で行う根拠）。
public sealed class ReportConfirmedVersionRow
{
    public ReportKind Kind { get; set; }

    public int Sequence { get; set; }

    public string PeriodKey { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }

    // 確定後の行の版（確定は版を 1 進める。VersionedReport の版と同じ）。
    public int Version { get; set; }

    public DateTimeOffset ConfirmedAt { get; set; }

    public string? BasedOn { get; set; }

    public int AssumptionsVersion { get; set; }

    public string PolicySummary { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? UnsuppliedInputs { get; set; }
}
