using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-05, FR-16: 発注結果（注文実体＋スリッページ）の永続化。実運用では PostgreSQL（Slice B）。月報・射影のデータ源。
public interface IExecutedOrderStore
{
    void Save(ExecutionRecord record);

    /// <summary>記録済みの発注結果を新しい順で返す（照会・射影用）。</summary>
    IReadOnlyList<ExecutionRecord> GetAll();

    /// <summary>DecisionId に対応する発注結果を返す（冪等性チェック用。無ければ null）。</summary>
    ExecutionRecord? FindByDecisionId(Guid decisionId);

    /// <summary>
    /// #270, IADR-0113: 非終端（<see cref="OrderStatusLifecycle.IsPending"/>）の記録を古い順に最大
    /// <paramref name="batchSize"/> 件返す（約定状態の追跡対象）。<paramref name="since"/> より古い記録は
    /// 追跡上限を過ぎたものとして除外する（以降は滞留＝リコンサイル／人手の領分）。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize);

    /// <summary>
    /// FR-10, #958, IADR-0406 決定1: 指定した注文 ID のうち非終端（<see cref="OrderStatusLifecycle.IsPending"/>）の記録を
    /// 古い順に返す。<b>追跡上限は見ない</b>——呼び出し側（約定追跡）が対象を Active な S0 の逆指値レグに絞る。
    /// 空集合なら何も読まずに空を返す。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds);

    /// <summary>
    /// 🔴 FR-10, #1105, IADR-0461 決定1: 非終端（<see cref="OrderStatusLifecycle.IsPending"/>）の<b>決済（Close）</b>の記録のうち、
    /// 銘柄・市場・決済の方向（<paramref name="closeSide"/>）が一致するものを古い順に返す（決済の数量から処理中の決済を引くため）。
    /// <b>追跡上限は見ない</b>——生きているかは呼び出し側がブローカーへ確かめる。保護レグ（S0 / S3）も含まれるので、呼び出し側が除く。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから絞る。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindPendingCloses(string symbol, Market market, TradeSide closeSide) =>
        GetAll()
            .Where(r => r.PositionEffect == PositionEffect.Close && OrderStatusLifecycle.IsPending(r.Status)
                && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
            .OrderBy(r => r.ExecutedAt)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, ADR-0050 決定1, #1121, IADR-0466 決定2: <b>新規建て（Open）</b>の記録のうち、銘柄・市場・エントリーの方向
    /// （<paramref name="entrySide"/>）が一致するものを<b>状態を問わず</b>、新しい順に最大 <paramref name="limit"/> 件返す。
    /// <para>
    /// 用途は、保護逆指値を張れなかったエントリーの成行手仕舞い（DecisionId＝エントリーから決定的に導出。保護記録を持たないことがある）を
    /// 保護の機構が出した決済として見分けることだけである。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから絞る。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// </para>
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindRecentOpens(string symbol, Market market, TradeSide entrySide, int limit) =>
        GetAll()
            .Where(r => r.PositionEffect == PositionEffect.Open
                && r.Symbol == symbol && r.Market == market && r.Side == entrySide)
            .OrderByDescending(r => r.ExecutedAt)
            .Take(limit)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, #1330, IADR-0534 決定2: <b>承認経路の決済</b>（<see cref="PositionEffect.Close"/> かつ
    /// <see cref="ExecutionRecord.ApprovalOrigin"/> が null でない）のうち、銘柄・市場・決済の方向が一致し、
    /// <b>まだ保護記録の縮小へ帰属させていない約定</b>（<c>FilledQuantity &gt; ProtectionReleasedQuantity</c>）を持つものを
    /// 古い順に最大 <paramref name="limit"/> 件返す。状態は問わない（部分約定のまま生きている決済の約定も含む）。
    /// <para>
    /// 用途は保護の突き合わせ（<c>ProtectiveStopNetting</c>）が確定した減少を「承認済みの決済で説明できる分」と
    /// 「外部要因」に分けることだけである。保護の機構の決済レグ（S0 / S3 の逆指値・S1 の決済・成行手仕舞い）は出どころを持たないので入らない。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから絞る。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// </para>
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindApprovedClosesWithUnreleasedFills(
        string symbol, Market market, TradeSide closeSide, int limit) =>
        GetAll()
            .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin is not null
                && r.FilledQuantity > r.ProtectionReleasedQuantity
                && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, #1330, IADR-0534 決定2: 記録の「保護の縮小へ帰属させた株数」を <paramref name="releasedQuantity"/> へ進める
    /// （<b>その 1 列だけ</b>を書く。単調——今の値以下なら書かない——で、約定数量を上限に切り詰める）。書いたら true。
    /// 記録が無い・進まないなら何もせず false。既定の実装（試験用の包み型のためのもの）は何もしない
    /// （帰属の印が残らないだけで、保護の数量には影響しない）。
    /// </summary>
    bool MarkProtectionReleased(string orderId, int releasedQuantity) => false;

    /// <summary>
    /// FR-10, #958, IADR-0406 決定3: 非終端の記録の<b>追跡の起点</b>（<see cref="ExecutionRecord.ExecutedAt"/>）を
    /// <paramref name="trackedFrom"/> へ進める（約定追跡の窓へ戻す）。<b>時刻の列だけ</b>を書き、状態・数量・価格には触れない
    /// （並行に約定追跡が記録を終端にしていても、古い状態で上書きしない）。記録が無い・終端・起点が既に同じか新しいなら
    /// 何もせず false を返す。
    /// </summary>
    bool RenewTracking(string orderId, DateTimeOffset trackedFrom);

    /// <summary>
    /// 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: <b>追跡上限を過ぎた</b>（<see cref="ExecutionRecord.ExecutedAt"/> が
    /// <paramref name="before"/> より古い）非終端の記録のうち、<b>その追跡の起点でまだ打ち切りを記録していないもの</b>を
    /// 古い順に最大 <paramref name="batchSize"/> 件返す（約定追跡の打ち切りを監査へ残す対象）。
    /// <para>
    /// 打ち切りの印は「打ち切った追跡の起点」（<see cref="MarkTrackingAbandoned"/>）であり、起点が後から進められて
    /// （<see cref="RenewTracking"/>）再び期限を過ぎた記録は、印と起点が違うため<b>改めて</b>返る。
    /// 既定の実装（試験用の包み型のためのもの）は何も返さない。本番のストア（EF・インメモリ）は印つきの問い合わせで上書きする。
    /// </para>
    /// <para>
    /// <paramref name="excludedOrderIds"/> の注文は問い合わせの段で除く（件数の上限の前に除くので、除かれる記録が
    /// 古い側に溜まっても上限を占めない。Active な S0 の逆指値レグ＝追跡上限の対象外。#1048 独立監査）。
    /// </para>
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindTrackingExpired(
        DateTimeOffset before, int batchSize, IReadOnlyCollection<string>? excludedOrderIds = null) => [];

    /// <summary>
    /// 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 記録に「起点 <paramref name="trackedFrom"/> の追跡を打ち切ったことを記録済み」の印を書く
    /// （<b>印の列だけ</b>を書く。状態・数量・価格・起点には触れない）。記録が無い・起点が既に進んでいる（別の追跡になった）なら
    /// 何もせず false を返す。<b>発行の後に呼ぶ</b>（先に印を書くと、発行に失敗した打ち切りが二度と記録されない）。
    /// 既定の実装（試験用の包み型のためのもの）は何もしない。
    /// </summary>
    bool MarkTrackingAbandoned(string orderId, DateTimeOffset trackedFrom) => false;

    /// <summary>
    /// 🔴 FR-10, FR-05, #1339, IADR-0536 決定2: <b>追跡を打ち切った</b>（今の追跡の起点で打ち切りの印がある）非終端の
    /// <b>承認経路の決済</b>（<see cref="PositionEffect.Close"/> かつ <see cref="ExecutionRecord.ApprovalOrigin"/> が null でない）を
    /// 古い順に最大 <paramref name="batchSize"/> 件返す（ブローカーの終端を確かめて解放するための照会し直しの対象）。
    /// 保護の機構の決済レグ（S0 / S3 の逆指値・S1 の決済・成行手仕舞い）は出どころを持たないので入らない。
    /// 既定の実装（試験用の包み型のためのもの）は何も返さない。本番のストア（EF・インメモリ）は印つきの問い合わせで上書きする。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindAbandonedApprovedCloses(int batchSize) => [];

    /// <summary>
    /// FR-10, FR-11, #1339, IADR-0536 決定4: 発注（<see cref="ExecutionRecord.ExecutedAt"/>。非終端では発注時刻のまま）が
    /// <paramref name="placedBefore"/> より古い、非終端の<b>承認経路の決済</b>を古い順に最大 <paramref name="limit"/> 件返す
    /// （未約定の決済の定期ログの対象。窓の両端＝発注から一定時間 かつ まだ非終端）。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから絞る。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindStaleApprovedCloses(DateTimeOffset placedBefore, int limit) =>
        GetAll()
            .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin is not null
                && OrderStatusLifecycle.IsPending(r.Status) && r.ExecutedAt < placedBefore)
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, ADR-0067 決定1・4, #1339, IADR-0539: 発注（<see cref="ExecutionRecord.ExecutedAt"/>。非終端では発注時刻のまま）が
    /// <paramref name="placedAtOrBefore"/> 以前で、まだ非終端の<b>判断由来の決済</b>（<see cref="PositionEffect.Close"/> かつ
    /// <see cref="ExecutionRecord.ApprovalOrigin"/> が <see cref="OrderApprovalOrigin.TradeDecision"/>）のうち、系がまだ取消を求めていない
    /// （<see cref="ExecutionRecord.UnfilledCancelRequestedAt"/> が null）ものを古い順に最大 <paramref name="limit"/> 件返す（N 分未約定の取消の対象）。
    /// 利用者の手仕舞い・維持率割れの自動縮小・出どころ不明（<see cref="OrderApprovalOrigin.Unknown"/>）・出どころなしは入らない。
    /// <paramref name="placedOnOrAfter"/> より前に発注した記録は除く（当日の一覧に無い古い非終端の記録＝IADR-0536 残余 1 が、
    /// 取消を送れないまま回数の枠と件数の上限を占めないように。呼び出し側が市場の現地取引日の始まりから作る）。
    /// <paramref name="excludedOrderIds"/> の注文（回数の上限で取り消さないと決めたもの）は件数の上限の前に除く
    /// （古い側に溜まっても上限を占めず、後から来た対象を締め出さない）。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから絞る。本番のストア（EF・インメモリ）は条件つきの問い合わせで上書きする。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindUnfilledDecisionCloses(
        DateTimeOffset placedAtOrBefore, int limit, IReadOnlyCollection<string>? excludedOrderIds = null,
        DateTimeOffset? placedOnOrAfter = null) =>
        GetAll()
            .Where(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
                && OrderStatusLifecycle.IsPending(r.Status) && r.UnfilledCancelRequestedAt is null
                && r.ExecutedAt <= placedAtOrBefore && (placedOnOrAfter == null || r.ExecutedAt >= placedOnOrAfter)
                && (excludedOrderIds == null || !excludedOrderIds.Contains(r.OrderId)))
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .ToList();

    /// <summary>
    /// 🔴 FR-10, ADR-0067 決定2, #1339, IADR-0539: 同じ建玉（銘柄・市場・決済の方向）の判断由来の決済のうち、系が取消を求めた時刻
    /// （<see cref="ExecutionRecord.UnfilledCancelRequestedAt"/>）が [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>) にある件数
    /// （1 建玉 1 日の回数の上限の数え。範囲は呼び出し側が<b>市場の現地取引日</b>から作る）。状態は問わない（取り消した後に終端した記録も数える）。
    /// 既定の実装（試験用の包み型のためのもの）は全件を読んでから数える。
    /// </summary>
    int CountUnfilledCancelRequests(
        string symbol, Market market, TradeSide closeSide, DateTimeOffset fromInclusive, DateTimeOffset toExclusive) =>
        GetAll().Count(r => r.PositionEffect == PositionEffect.Close && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
            && r.Symbol == symbol && r.Market == market && r.Side == closeSide
            && r.UnfilledCancelRequestedAt is { } at && at >= fromInclusive && at < toExclusive);

    /// <summary>
    /// 🔴 FR-10, ADR-0067 決定1, #1339, IADR-0539: 記録に「系が取消を求めた」印（<paramref name="requestedAt"/>）を書く（<b>その 1 列だけ</b>）。
    /// <b>取消を送る前に呼ぶ</b>——送った後に落ちても、同じ注文を二度取り消さず、回数にも数えられる（少ない側へ倒す）。
    /// 記録が無い・既に印がある・終端なら何もせず false。既定の実装（試験用の包み型のためのもの）は何もしない（＝取り消さない側）。
    /// </summary>
    bool MarkUnfilledCancelRequested(string orderId, DateTimeOffset requestedAt) => false;

    // #820 の 4 巡目監査, IADR-0344 追記(4): FindClosesSince（建玉照会がまだ映していない決済の走査）は撤去した。
    // 持ち分を毎巡回引き直す方式そのものをやめ、保護記録が残保護数量を状態として持つ形へ作り直したため、
    // 決済レグの記録を持ち分の計算に使わない（完了済み S0 行の取消済みレグで持ち分が食われる事故も構造的に消える）。

    /// <summary>
    /// #270, IADR-0113: 追跡で観測した最新のブローカ状態を既存記録へ反映する（<paramref name="orderId"/> で特定）。
    /// 記録が無ければ何もせず false を返す（新規に作らない＝DecisionId 1:1 の不変を壊さない）。
    /// </summary>
    bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt);
}
