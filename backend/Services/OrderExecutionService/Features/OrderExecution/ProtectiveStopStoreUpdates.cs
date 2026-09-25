using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, #833 項目3, IADR-0396: 保護記録の「読み直してから書く」更新（楽観並行）。
//
// 保護記録（protective_stop_orders）は、到達ハンドラ・常駐ガード・約定追跡（再武装）・乖離の取り込みが
// **別々のスコープから並行に**書く。かつて Save は「古い写しの全列を書き戻す」last-writer-wins であり、
// await を跨いで持っていた写しで、並行に進んだ状態（完了・再武装・試行番号）を巻き戻し得た。
// 巻き戻った試行番号は同じ CloseDecisionId の記録を「送信後に行の更新だけが失われた窓」と読ませ、
// **同じ決済をもう一度帳簿から引いて ClosePlaced をもう一度出す**（#833 項目3 の実害）。
//
// Update は毎回**保存先の最新を読み直し**、変更を「最新の行に対する関数」として当て直して、版が一致したときだけ書く。
// 変更の条件（例: この試行はまだ誰も確定していない）は関数の中で最新の行に対して判定する——
// 古い写しで判定しない（不明な古さの写しで既知の新しい状態を上書きしない）。
public static class ProtectiveStopStoreUpdates
{
    /// <summary>衝突したときに読み直して当て直す最大回数。</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// 保存先の最新の行へ <paramref name="mutate"/> を当てて楽観並行で保存する。
    /// <list type="bullet">
    /// <item>行が無い・<paramref name="mutate"/> が null を返した（条件を満たさない）→ 何も書かず null。</item>
    /// <item><paramref name="mutate"/> が同じインスタンスを返した（変更なし）→ 書かずに最新の行を返す。</item>
    /// <item>保存できた → 保存後の行（版を 1 進めた写し）を返す。</item>
    /// <item><see cref="MaxAttempts"/> 回続けて衝突した → <see cref="ProtectiveStopConcurrencyException"/>（黙って諦めない）。</item>
    /// </list>
    /// </summary>
    public static ProtectiveStopOrder? Update(
        this IProtectiveStopOrderStore stops,
        Guid entryDecisionId,
        Func<ProtectiveStopOrder, ProtectiveStopOrder?> mutate)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(mutate);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = stops.Find(entryDecisionId);
            if (current is null)
                return null;

            var next = mutate(current);
            if (next is null)
                return null;
            if (ReferenceEquals(next, current))
                return current;

            // 🔴 版は書き換えない。mutate は current から作る（別の古い写しから作れば版が合わず、書かれない）。
            if (stops.TrySave(next))
                return next with { Version = current.Version + 1 };
        }

        throw new ProtectiveStopConcurrencyException(entryDecisionId, MaxAttempts);
    }
}

/// <summary>
/// FR-10, #833 項目3, IADR-0396: 保護記録の更新が並行更新と衝突し続けた（読み直して当て直しても版が合わなかった）。
/// 書いていない——古い写しで上書きしないことを優先した結果である。呼び出し側は エラーログ（LogError）を出して次の巡回に委ねる。
/// </summary>
public sealed class ProtectiveStopConcurrencyException(Guid entryDecisionId, int attempts)
    : Exception($"保護記録 {entryDecisionId} の更新が並行更新と {attempts} 回続けて衝突しました（古い写しでは上書きしていません）。")
{
    public Guid EntryDecisionId { get; } = entryDecisionId;
}
