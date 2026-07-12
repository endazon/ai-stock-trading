namespace AiStockTrading.Report.Domain;

// FR-07, UC-03〜05, 07_discord-bot-design, IADR-0042: 対話的確定のレビュー状態。
// 既存の永続 ReportState.Draft/Confirmed の Draft 局面を、提示・差し戻し・改訂・承認の対話ライフサイクルへ精緻化する。
// Confirmed は ReportState.Confirmed に対応し、それ以外は ReportState.Draft に対応する。
public enum ReviewState
{
    /// <summary>ドラフト作成/改訂中（未提示）。</summary>
    Drafting,

    /// <summary>ドラフト提示済み・利用者の承認待ち。</summary>
    PendingApproval,

    /// <summary>差し戻し（修正指示を受領し改訂待ち）。</summary>
    ChangesRequested,

    /// <summary>確定済み（終端・不変）。</summary>
    Confirmed,
}

// 対話的確定の操作（07_discord-bot-design のシーケンス: 提示→差し戻し→改訂→承認）。
public enum ReviewAction
{
    /// <summary>提示（ドラフトを承認待ちにする）。</summary>
    Present,

    /// <summary>差し戻し（修正指示・改訂を促す）。</summary>
    RequestChanges,

    /// <summary>改訂（新ドラフト・版+1）。</summary>
    Revise,

    /// <summary>承認（確定）。利用者のみ。</summary>
    Approve,
}

// 拒否理由。拒否時は状態を変えずに返す。
public enum ReviewRejectionReason
{
    /// <summary>操作者未指定（OwnerOnly・ADR-0003: 方針の確定には利用者との対話を要する）。</summary>
    ActorRequired,

    /// <summary>版番号不一致（二重実行防止・「最新ドラフトを確認してください」）。</summary>
    VersionConflict,

    /// <summary>現在状態から許されない遷移。</summary>
    InvalidTransition,

    /// <summary>確定済みは不変（ADR-0003）。</summary>
    AlreadyConfirmed,
}

// レビュー対象のスナップショット（不変）。Version は楽観排他の版番号。
public sealed record ReportReview(string PeriodKey, ReviewState State, int Version);

// 操作コマンド。Actor は操作者（OwnerOnly）、ExpectedVersion は楽観排他の版番号。
public sealed record ReviewCommand(ReviewAction Action, string Actor, int ExpectedVersion);

// 遷移結果。Rejection が非 null なら拒否（状態不変）。Transitioned=false かつ Rejection=null は冪等（状態変化なし）。
public sealed record ReviewDecision(ReportReview Review, bool Transitioned, ReviewRejectionReason? Rejection)
{
    /// <summary>拒否されていない（遷移または冪等成功）。</summary>
    public bool Accepted => Rejection is null;
}

// FR-07, UC-03〜05, 07_discord-bot-design, IADR-0042: 対話的確定の状態遷移（純関数・決定的・副作用なし）。
// 版番号付き冪等確定（IADR-0024）を状態機械へ一般化する。HTTP/Discord/永続はこの決定を駆動する薄い層として後続で結線する。
public static class ReportReviewStateMachine
{
    /// <summary>現在のレビューとコマンドから次状態を決める。拒否時は状態不変で理由を返す。</summary>
    public static ReviewDecision Decide(ReportReview review, ReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(command);

        // OwnerOnly: 操作者必須（ADR-0003: 方針の確定には利用者との対話を要し完全無人での方針変更は行わない）。実認可（未認証 401・ロール無し 403）は HTTP 層の後続結線で担う。
        if (string.IsNullOrWhiteSpace(command.Actor))
            return Reject(review, ReviewRejectionReason.ActorRequired);

        // 確定済みの特例（IADR-0024 踏襲）: 再 Approve は版非依存で冪等（二重確定の副作用なし）。改訂系は不変のため拒否。
        if (review.State == ReviewState.Confirmed)
        {
            return command.Action == ReviewAction.Approve
                ? Idempotent(review)
                : Reject(review, ReviewRejectionReason.AlreadyConfirmed);
        }

        // 未確定局面は版番号の楽観排他を要求（二重実行防止・古い版は拒否）。
        if (command.ExpectedVersion != review.Version)
            return Reject(review, ReviewRejectionReason.VersionConflict);

        return (review.State, command.Action) switch
        {
            // Drafting: 提示のみ可（未提示のドラフトは承認・差し戻しの対象にならない）。
            (ReviewState.Drafting, ReviewAction.Present) => Transition(review, ReviewState.PendingApproval, bumpVersion: false),

            // PendingApproval: 承認・差し戻し・改訂が可。再提示は冪等。
            (ReviewState.PendingApproval, ReviewAction.Present) => Idempotent(review),
            (ReviewState.PendingApproval, ReviewAction.RequestChanges) => Transition(review, ReviewState.ChangesRequested, bumpVersion: false),
            (ReviewState.PendingApproval, ReviewAction.Revise) => Transition(review, ReviewState.Drafting, bumpVersion: true),
            (ReviewState.PendingApproval, ReviewAction.Approve) => Transition(review, ReviewState.Confirmed, bumpVersion: true),

            // ChangesRequested: 改訂（新ドラフト）または再提示が可。再差し戻しは冪等。承認は不可（改訂を経る）。
            (ReviewState.ChangesRequested, ReviewAction.Revise) => Transition(review, ReviewState.Drafting, bumpVersion: true),
            (ReviewState.ChangesRequested, ReviewAction.Present) => Transition(review, ReviewState.PendingApproval, bumpVersion: false),
            (ReviewState.ChangesRequested, ReviewAction.RequestChanges) => Idempotent(review),

            _ => Reject(review, ReviewRejectionReason.InvalidTransition),
        };
    }

    private static ReviewDecision Reject(ReportReview review, ReviewRejectionReason reason) =>
        new(review, Transitioned: false, reason);

    // 冪等（既に目標状態）: 状態変化なし・拒否ではない。
    private static ReviewDecision Idempotent(ReportReview review) =>
        new(review, Transitioned: false, Rejection: null);

    // 遷移: 状態を変え、内容が変わる操作（改訂・確定）でのみ版番号を上げる。
    private static ReviewDecision Transition(ReportReview review, ReviewState next, bool bumpVersion) =>
        new(review with { State = next, Version = bumpVersion ? review.Version + 1 : review.Version }, Transitioned: true, Rejection: null);
}
