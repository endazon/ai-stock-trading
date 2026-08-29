using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-07, FR-09, IADR-0116, #280: 報告書ドラフトを提示（承認待ちに）したことを利用者へ知らせるポート。
// #210（IADR-0096）の通知ポートと同型で、Application 層をメッセージ基盤（Wolverine・ADR-0013 / IADR-0129 / #354）へ依存させない。
//
// 既定は no-op。Worker 側でバス実装（MessageBusReportDraftPresentedNotifier）を選ぶと ReportDraftPresented が発行され、通知サービスが Discord へ
// 投稿する（Discord 未設定なら送信ポートが no-op に倒れる＝送信経路の追加のみ・IADR-0020/0062）。
public interface IReportDraftPresentedNotifier
{
    /// <summary>提示を通知する。通知は best-effort であり、失敗しても生成・提示は巻き戻さない。</summary>
    Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default);
}

// 提示の通知内容。Summary は投稿可能な形へサニタイズ済み（ReportSummary.Build の内側で適用・IADR-0116 決定4）。
// Version は提示時点の版番号で、利用者が確定する際の expectedVersion（版番号付き冪等・IADR-0024）になる。
public sealed record PresentedReportNotice(
    string PeriodKey,
    ReportKind Kind,
    string PeriodLabel,
    string Summary,
    int Version);
