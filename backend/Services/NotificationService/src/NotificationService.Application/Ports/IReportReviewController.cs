namespace NotificationService.Application.Ports;

// FR-14, FR-07, UC-03〜05, ADR-0003, IADR-0240: 報告書サービス（#14）のレビュー操作エンドポイントの抽象。
// 通知サービスは報告書の状態を持たない（権威は報告書サービス側）。kill switch / pause / 段階ゲートと同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）のため、実装は Bot 専用の owner マップ機密クライアントの
// client_credentials トークンを付与する（trading-service トークンでは 403）。
//
// **版番号（version）だけを射影する。** 報告書サービスの `ReviewState`（enum）は数値/文字列いずれの JSON
// 表現も取り得るため読まない（IADR-0081 決定1 と同型の representation-agnostic）。Bot が要るのは
// 「確定要求に添える版番号」だけである（詳細設計07 §二重実行防止）。
//
// **報告書の本文・要約は取得しない**（IADR-0240 決定4）。要約は ReportDraftPresented 通知が届けており、
// その経路は発行側でサニタイズ済みである（IADR-0116 決定3/4）。Bot が生本文を直接取ると、そのサニタイズを
// 迂回する経路を新設することになる。
public interface IReportReviewController
{
    // 現在のレビュー局面（版番号）を照会する（表示専用・副作用なし）。
    Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default);

    // 確定（版番号付き冪等）。報告書サービスが版番号を検証し、遷移時のみ ReportConfirmed を発行する。
    Task<ReportConfirmResult> ConfirmAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default);

    // 差し戻し（修正指示）。PendingApproval → ChangesRequested。版番号付き楽観排他。
    Task<ReportReviewResult> RequestChangesAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default);
}

// FR-14, UC-03〜05: レビュー照会・差し戻しの結果。Message は利用者へ表示する整形済みテキスト。
//
// **Succeeded=false は報告書サービス呼び出し自体が失敗したこと**（HTTP エラー・タイムアウト・解釈不能）を
// 意味する。失敗を成功に見せない（kill switch / 段階ゲートと同じ方針）。
// Version は照会に成功したときのみ意味を持つ（失敗時は 0）。
public sealed record ReportReviewResult(bool Succeeded, int Version, string Message);

// FR-14, FR-07: 確定要求の結果。Confirmed は報告書サービスが確定を受理したか（版不一致の 409 では false）。
// Succeeded=false は呼び出し自体の失敗であり、Confirmed とは区別する。
public sealed record ReportConfirmResult(bool Succeeded, bool Confirmed, string Message);
