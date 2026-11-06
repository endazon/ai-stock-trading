namespace NotificationService.Features.Notifications;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182:
// GFV 違反による停止の解除（リスク管理の OwnerOnly エンドポイント）を呼ぶだけのポート。
// 通知サービスは GFV の状態を持たない（権威は Risk 側）。kill switch / pause / 段階ゲートと同型。
//
// 返り値は**整形済み表示テキスト**（Message）を持つ。Risk の JSON 表現への結合は実装アダプタに閉じる
// （IADR-0081 決定1 と同じ representation-agnostic）。
public interface IGoodFaithViolationController
{
    // 解除を要求する。理由は必須（Risk 側が空を 400 で拒否する）。
    Task<GoodFaithViolationClearResult> ClearAsync(string reason, CancellationToken cancellationToken = default);

    // 解除の要求（確認コードつき）。
    //
    // NFR-06, FR-14, UC-06, #1552, IADR-0615: secondFactorCode は利用者が確認の画面に入れた認証アプリの 6 桁（任意・空は null）。
    // 提供側が確かめる（ボットは確かめず、ログ・応答に出さない）。既定の実装はコードを捨てて従来の要求を送る（試験の偽物を壊さない）。
    // 実装（Http*・Grpc*）は必ず上書きし、REST の本文・gRPC の要求の既存の欄で運ぶ。
    Task<GoodFaithViolationClearResult> ClearAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default) =>
        ClearAsync(reason, cancellationToken);
}

// FR-19, UC-06: 解除要求の結果。
//
// **Succeeded=false は Risk 呼び出し自体が失敗したこと**（HTTP エラー・タイムアウト・解釈不能）を意味し、
// **Cleared=false（受理されなかった＝解除対象が無い等）とは区別する** ——
// 「失敗を成功に見せない」ことが安全側になる（段階ゲートと同じ方針）。
public sealed record GoodFaithViolationClearResult(bool Succeeded, bool Cleared, string Message);
