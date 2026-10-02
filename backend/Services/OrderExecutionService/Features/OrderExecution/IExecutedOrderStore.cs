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
