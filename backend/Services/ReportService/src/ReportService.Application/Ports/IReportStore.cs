using ReportService.Application.State;
using ReportService.Domain;

namespace ReportService.Application.Ports;

// FR-06/07, IADR-0012/0024: 報告書ストア（PeriodKey ごとに 1 行＋Version 楽観排他）。確定済みは不変。
public interface IReportStore
{
    VersionedReport? Get(string periodKey);

    IReadOnlyList<TradingReport> List();

    /// <summary>
    /// ドラフトを作成/更新する。新規は expectedVersion=0。既存ドラフトは expectedVersion 一致時のみ更新し Version を +1 する。
    /// 確定済み報告書は不変のため更新しようとすると例外（呼び出し側で防ぐ）。不一致は <see cref="ReportConcurrencyException"/>。
    /// </summary>
    int UpsertDraft(TradingReport report, int expectedVersion);

    /// <summary>
    /// 確定する（Draft→Confirmed）。既に確定済みなら冪等（Transitioned=false・状態変化なし）。版不一致は例外。
    /// 対象が存在しなければ null を返す。
    /// </summary>
    ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt);

    /// <summary>指定種別の最新の確定済み報告書（PeriodStart 最大）。無ければ null。</summary>
    VersionedReport? GetLatestConfirmed(ReportKind kind);

    /// <summary>FR-07, IADR-0042/0071: 対話的確定のレビュー局面を取得する。対象が無ければ null。</summary>
    ReportReview? GetReview(string periodKey);

    /// <summary>
    /// FR-07, IADR-0042/0071: レビュー操作（提示・差し戻し）を <see cref="ReportReviewStateMachine"/> で検証し、遷移を永続化する。
    /// 提示・差し戻しは内容不変のため Version を上げない。拒否時は状態不変で理由を含む決定を返す。対象が無ければ null。
    /// </summary>
    ReviewDecision? ApplyReview(string periodKey, ReviewCommand command);
}
