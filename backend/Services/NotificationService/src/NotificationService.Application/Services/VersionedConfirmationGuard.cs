namespace AiStockTrading.Notification.Application.Services;

// FR-14, IADR-0063 決定6: 詳細設計07「二重実行防止（確定・kill switch の冪等性）」の楽観ロック機構。
//
// 確定要求は `対象ID＋版番号` を必須とし:
// - 未確定の最新版         → Accepted（呼び出し側が実際の確定を行う）
// - 同一 対象ID＋版番号 の再要求 → AlreadyConfirmed（2回目以降は副作用なし）
// - 確定済みより古い版      → Stale（「最新ドラフトを確認してください」）
//
// チャットUIと Discord が同時に確定しても一度しか有効にならないことを保証する。
//
// 本 PR ではこの純粋機構と全数テストのみを提供する。報告書ドラフトへの結線（ReportReviewStateMachine の
// 駆動）は #14 側で行う（交差回避・IADR-0063 決定6）。
//
// スレッド安全性: Bot は複数の Discord イベントを並行処理し得るため lock で直列化する。
// 永続化は行わない（Bot はステートレス＝詳細設計07。確定の権威は報告書サービス側の冪等 API が持ち、
// 本機構は窓口での多重押下を弾く前段のガード）。
public sealed class VersionedConfirmationGuard
{
    private readonly Lock _gate = new();

    // 対象ID → 確定済みの最新版番号。
    private readonly Dictionary<string, int> _confirmed = new(StringComparer.Ordinal);

    public ConfirmationOutcome TryConfirm(string targetId, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        lock (_gate)
        {
            if (!_confirmed.TryGetValue(targetId, out var confirmedVersion))
            {
                _confirmed[targetId] = version;
                return ConfirmationOutcome.Accepted;
            }

            // 同一版の再要求は冪等に「確定済み」を返すのみ（副作用なし）。
            if (confirmedVersion == version)
                return ConfirmationOutcome.AlreadyConfirmed;

            // 確定済みより古い版は拒否する（楽観ロック）。
            if (version < confirmedVersion)
                return ConfirmationOutcome.Stale;

            // より新しい版の確定は受け付け、確定済み版を進める（ドラフト更新後の再確定）。
            _confirmed[targetId] = version;
            return ConfirmationOutcome.Accepted;
        }
    }
}

// FR-14: 版番号付き確定の判定結果。
public enum ConfirmationOutcome
{
    // 確定を実行してよい（この版は未確定）。
    Accepted,

    // 同一 対象ID＋版番号 で確定済み。副作用なしで「確定済み」と応答する。
    AlreadyConfirmed,

    // 確定済みより古い版。「最新ドラフトを確認してください」と応答する。
    Stale,
}
