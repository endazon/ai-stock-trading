using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, ADR-0021 決定4-3, IADR-0165:
// Good Faith Violation（GFV）の発生回数の**自前計数**。
//
// ★★ この計数が何であるかを取り違えないこと（ADR-0025 §理由 が明記した限界） ★★
//
//   自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、**ブローカー側が独自に GFV と
//   判定した事象は捕捉できない**。したがって本計数は「**ブローカーの GFV カウンタの写し**」ではなく
//   「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**
//
//   - **ガードが正しく働けば発生回数は 0 のままである。** 記録されるのはガードをすり抜けた場合だけである。
//   - **ブローカーが先に 3 回目を計上して口座制限（90 日）が掛かる事態を、本計数では防げない。**
//   - それでも「3 回目の手前で止める」目的には足りる——0 でなくなった時点でガードの不具合が判明する。
//
// 供給元をブローカー照会（`BrokerAccountState`）に置かないのはこのためである。同じ欄に載せると
// 「ブローカーが報告した GFV 件数」と読まれ、上の限界が消える。

/// <summary>
/// FR-19, #425, ADR-0025 決定2, IADR-0165 決定2: <b>供給された</b>GFV 発生回数（自前計数）。
/// <para>
/// <b>この型の値が存在すること自体が「計数が供給された」ことを意味する。</b>
/// 未供給は <c>null</c> であり、<see cref="Observed"/> の 0（＝数えた結果 0 件だった）とは別物である
/// （[IADR-0148] の規律・#424 の表示規約と同じ区別）。両者を同じ <c>int</c> の 0 で表すと、
/// **供給元の不在が「違反なし」として読まれる**。
/// </para>
/// <para>
/// <b>計上単位は 1 注文につき 1 件</b>である（追記台帳の主キー <c>OrderId</c> が担保する）。
/// 部分約定の進行・メッセージ再送で二重計上しない。
/// </para>
/// </summary>
public sealed record GoodFaithViolationTally
{
    /// <param name="count">自前で記録した GFV 発生件数（0 以上）。</param>
    public GoodFaithViolationTally(int count)
    {
        // 負の件数は計数結果として成立しない（供給側の異常を黙って通さない）。
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
    }

    /// <summary>自前で記録した GFV 発生件数。<b>ブローカーが判定した件数ではない。</b></summary>
    public int Count { get; }

    /// <summary>
    /// 計数結果としての件数を作る。<b>0 件を主張するのは、実際に数えられたときだけである。</b>
    /// </summary>
    public static GoodFaithViolationTally Observed(int count) => new(count);

    /// <summary>
    /// FR-19, ADR-0021 決定4-3: 新規建ての**停止基準**（2 件）に達しているか。
    /// <para>
    /// 判定を型の側に持たせるのは、<c>count &gt;= 2</c> のような持ち上げ比較が
    /// <b>未供給（null）を黙って「違反なし」へ倒す</b>ためである（C# の持ち上げ比較は null で false を返す）。
    /// 未供給の扱いは <see cref="AccountTypePolicy.BlocksForGoodFaithViolations"/> が担う。
    /// </para>
    /// </summary>
    public bool BlocksNewEntry => Count >= AccountTypePolicy.GoodFaithViolationStopThreshold;

    /// <summary>
    /// FR-19, ADR-0021 決定4-3: **警告基準**（2 回目で警告）に達しているか。停止と同じ回数で立つ——
    /// 3 回目が起きた時点で 90 日の口座制限という不可逆な結果が確定するためである。
    /// </summary>
    public bool Warns => Count >= AccountTypePolicy.GoodFaithViolationStopThreshold;
}

/// <summary>
/// FR-19, FR-11, #425, ADR-0025 決定2, IADR-0165 決定1: 約定 1 件が
/// 「**未決済資金による買付**」に当たるかを判定する（純関数）。
/// <para>
/// <b>発注前のガードをすり抜けた買付だけが該当する。</b> ガードが正しく働けば、該当する約定は生じない。
/// </para>
/// </summary>
public static class GoodFaithViolationDetection
{
    /// <summary>
    /// その約定を GFV 発生 1 件として記録するか。
    /// </summary>
    /// <param name="account">
    /// 現在有効なブローカー口座の観測。<b><c>null</c>（未確認）・信用口座では記録しない。</b>
    /// GFV は現金口座の制度であり、口座種別を確認できていない状態の新規建ては
    /// <c>BrokerAccountTypeUnverified</c> が既に止めている（IADR-0153 決定1）。
    /// **不明を現金口座とみなして記録すると、信用口座の通常の回転売買が違反として積み上がり、
    /// 統制が 2 件で恒久的に止まる**（統制ではなく事故になる）。
    /// </param>
    /// <param name="side">約定の売買方向。<b>買付のみが対象である</b>——GFV は「未決済の売却代金で買った」ことで起きる。</param>
    /// <param name="positionEffect">建玉効果。<b>新規建てのみが対象である</b>（手仕舞いは止めない・数えない）。</param>
    /// <param name="purchaseAmountInBase">その約定の基準通貨（USD）建て金額。</param>
    public static bool CountsAsViolation(
        BrokerAccountState? account,
        TradeSide side,
        PositionEffect positionEffect,
        decimal purchaseAmountInBase)
    {
        if (account is null || account.AccountType != AccountType.Cash)
        {
            return false;
        }

        if (side != TradeSide.Buy || positionEffect != PositionEffect.Open)
        {
            return false;
        }

        // **発注前のガードと同じ述語を呼ぶ**（ADR-0025 決定2「計数の対象は統制2 が拒否しようとする事象と同じ」）。
        // 式を再実装すると「拒否はするが数えない」「数えるが拒否しない」がすり抜ける。
        return AccountTypePolicy.ExceedsSettledCash(account.SettledCashInBase, purchaseAmountInBase);
    }
}
