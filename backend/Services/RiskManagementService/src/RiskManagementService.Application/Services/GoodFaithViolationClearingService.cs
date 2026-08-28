using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Services;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定1/決定2, IADR-0182:
// GFV 違反による停止の**解除**（利用者の明示的な操作）。
//
// 🔴 **解除は記録を消さない。** ADR-0028 決定1 が「GFV 違反記録は失効させない」と定めている。
// 本サービスは**解除行を追記する**だけであり、違反記録そのものは台帳に残り続ける
// （`GetRecordedBetween` は解除の有無に関わらず全件を返す＝監査の証跡は消えない）。
//
// **未供給時の fail-closed は解けない。** ADR-0028 が明記するとおり、決定2 の解除は
// **記録が積まれた場合**の解除であり、**供給が無い場合の拒否を解除する手段ではない**。
// 本サービスは件数（`GetTally`）にしか作用せず、`GetTally()` が `null` になる経路
// （＝ストアが判定コアへ結線されていない）には触れない——構造的にそうなっている。
public sealed class GoodFaithViolationClearingService(IGoodFaithViolationStore store, IClock clock)
{
    /// <summary>
    /// 現在解除されていない違反記録を**すべて**解除する（原因の是正が済んだという利用者の申告）。
    /// <para>
    /// **対象を利用者に選ばせない。** ADR-0028 決定2 の解除は「原因を調べて是正した」ことの表明であり、
    /// 一部だけ解いて残りを止めたままにする運用上の意味が無い。**どの記録を解除したかは記録する**
    /// （決定2 の「どの記録に対して」）。
    /// </para>
    /// </summary>
    public GoodFaithViolationClearingOutcome Clear(string actor, string reason)
    {
        // 誰が・なぜ解いたか不明な解除を作らない（監査の粒度が決定2 の要求そのものである）。
        if (string.IsNullOrWhiteSpace(actor))
        {
            return GoodFaithViolationClearingOutcome.Rejected(GoodFaithViolationClearingRejection.ActorRequired);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return GoodFaithViolationClearingOutcome.Rejected(GoodFaithViolationClearingRejection.ReasonRequired);
        }

        var targets = store.GetUncleared();
        if (targets.Count == 0)
        {
            // 解除するものが無い。**成功に見せない**——「解除した」と記録すると、実際には何も起きていない
            // 操作が監査上の事実になる。停止していない状態で押された可能性が高く、利用者へその旨を返す。
            return GoodFaithViolationClearingOutcome.Rejected(GoodFaithViolationClearingRejection.NothingToClear);
        }

        var clearedAt = clock.UtcNow;
        foreach (var target in targets)
        {
            store.AppendClearance(new GoodFaithViolationClearance(target.OrderId, actor, reason, clearedAt));
        }

        // **解除後になお残る件数を返す。** 解除の最中に新たな違反が計上され得るため 0 とは限らない。
        // 「解除したのに止まったまま」を監査と利用者応答の双方から説明できるようにする。
        return GoodFaithViolationClearingOutcome.Cleared(
            [.. targets.Select(t => t.OrderId)], clearedAt, store.GetTally().Count);
    }
}

// #464: 解除要求を受理しない理由。**受理しない場合は解除行を 1 件も書かない。**
public enum GoodFaithViolationClearingRejection
{
    None = 0,

    /// <summary>解除者が空（誰が解いたか不明な解除を作らない）。</summary>
    ActorRequired = 1,

    /// <summary>理由が空（原因の是正の確認の記録が残らない）。</summary>
    ReasonRequired = 2,

    /// <summary>解除対象が 1 件も無い（停止していない）。**成功に見せない。**</summary>
    NothingToClear = 3,
}

// #464: 解除の結果。受理時は解除した記録の一覧と、解除後になお残る件数を返す。
public sealed record GoodFaithViolationClearingOutcome(
    bool Accepted,
    GoodFaithViolationClearingRejection Rejection,
    IReadOnlyList<string> ClearedOrderIds,
    DateTimeOffset? ClearedAt,
    int RemainingCount)
{
    public static GoodFaithViolationClearingOutcome Rejected(GoodFaithViolationClearingRejection rejection) =>
        new(false, rejection, [], null, RemainingCount: 0);

    public static GoodFaithViolationClearingOutcome Cleared(
        IReadOnlyList<string> clearedOrderIds, DateTimeOffset clearedAt, int remainingCount) =>
        new(true, GoodFaithViolationClearingRejection.None, clearedOrderIds, clearedAt, remainingCount);
}
