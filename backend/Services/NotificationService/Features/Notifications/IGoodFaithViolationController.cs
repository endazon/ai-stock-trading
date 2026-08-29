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
}

// FR-19, UC-06: 解除要求の結果。
//
// **Succeeded=false は Risk 呼び出し自体が失敗したこと**（HTTP エラー・タイムアウト・解釈不能）を意味し、
// **Cleared=false（受理されなかった＝解除対象が無い等）とは区別する** ——
// 「失敗を成功に見せない」ことが安全側になる（段階ゲートと同じ方針）。
public sealed record GoodFaithViolationClearResult(bool Succeeded, bool Cleared, string Message);
