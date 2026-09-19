using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Options;

namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// #141, FR-05, IADR-0074: 滞留した Reserved 予約（IADR-0057 の「発注済みか不明」の窓）をブローカ照会で
// 自動解消する。二重発注を絶対に起こさない fail-safe を最優先する（IADR-0057 の at-most-once を破らない）。
//
// 解消の優先順位:
//   1. executed_orders に記録あり → phase-4 断絶（Save 成功・MarkCompleted 失敗）の**自己修復**。ブローカ照会不要。
//   2. 記録なし → プローブ照会（IReservationBrokerProbe）:
//        Placed        → 記録を保存し確定（MarkCompleted）＋ OrderExecuted 発行対象に載せる。
//        NotPlaced     → 🔴 **解放の門（Reconciliation:ReleaseOnNotPlaced）が開いているときだけ**予約を解放（Release）。
//                        閉じているあいだは据え置き、DecisionId を HeldNotPlaced に載せる（#856 / IADR-0362）。
//        Indeterminate → 据え置き（人手/`_error` の現行安全側を壊さない）。門の開閉に依らない。
//
// 🔴 FR-05, #856, IADR-0362: **突合で Placed と確定したエントリーに保護逆指値は張らない**（#853 の 2 番）。
// したがって無保護の建玉が台帳へ載り得る。**黙って通り過ぎさせない**ため、突合で終端化した予約は
// ProbeTerminalized に載せて返し、Worker 層が 1 件ずつ Critical でログする。保護レグを張るか否かの裁定は #853 が持つ。
//
// 発行（OrderExecuted の Publish）は Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
//
// FR-20, #386, IADR-0149 決定1: 発行する OrderExecuted には**実際に発注したアダプタの発注先**を載せる。
// 本リコンサイラが扱うのは自プロセスが出した（または出しかけた）注文だけであり、broker は発注時と同一である。
public sealed class OrderReservationReconciler(
    IOrderReservationStore reservations,
    IExecutedOrderStore executedOrders,
    IReservationBrokerProbe probe,
    IBrokerAdapter broker,
    IClock clock,
    IOptions<ReconciliationOptions>? options = null)
{
    // 🔴 #856, IADR-0362: 構成が無いときは**門を閉じた側**へ倒す（未登録＝解放してよい、にしない）。
    private readonly ReconciliationOptions _options = options?.Value ?? new ReconciliationOptions();

    /// <summary>
    /// <paramref name="stallCutoff"/> より古い滞留 Reserved を最大 <paramref name="batchSize"/> 件リコンサイルする。
    /// 終端化した予約に対して発行すべき <see cref="OrderExecuted"/> を結果に載せて返す（発行は呼び出し側＝Worker）。
    /// </summary>
    public async Task<ReservationReconciliationResult> ReconcileAsync(
        DateTimeOffset stallCutoff, int batchSize, CancellationToken cancellationToken = default)
    {
        var stalled = reservations.FindStalledReserved(stallCutoff, batchSize);

        var executed = new List<OrderExecuted>();
        var probeTerminalized = new List<ReservationReconciliationFinding>();
        var heldNotPlaced = new List<Guid>();
        var terminalized = 0;
        var released = 0;
        var indeterminate = 0;
        var failed = 0;

        foreach (var reservation in stalled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 各予約は独立して処理する。1 件の失敗（照会例外・保存例外等）でバッチ全体を止めない
            // （最大 batchSize 件の巻き添えを避ける）。失敗は件数のみ集計し、Worker がログして次回巡回で再試行する。
            // 未処理のまま残る予約は Reserved のまま＝据え置き（fail-safe）で、二重発注は起きない。
            try
            {
                var decisionId = reservation.DecisionId;

                // 1. 記録あり: phase-4 断絶の自己修復。ブローカに照会せず既存記録で確定する。
                var record = executedOrders.FindByDecisionId(decisionId);
                if (record is not null)
                {
                    reservations.MarkCompleted(decisionId, record.OrderId, clock.UtcNow);
                    executed.Add(ToOrderExecuted(record));
                    terminalized++;
                    continue;
                }

                // 2. 記録なし: ブローカへ実状態を照会する。実照会は履歴窓を予約の ReservedAt で覆う必要があるため
                //    予約そのものを渡す（IADR-0092）。DecisionId 単体では健全な NotPlaced 判定ができない。
                var result = await probe.ProbeAsync(reservation, cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case ReservationProbeOutcome.Placed:
                        var order = result.Order
                            ?? throw new InvalidOperationException("Placed は BrokerOrder を伴わなければならない。");

                        // 照会（ProbeAsync）は実装次第で有意な待ち時間を持つ非同期になり得る。その待機中に通常フロー
                        // （OrderApprovedHandler）が同一 DecisionId を確定していないか、Save の直前に再確認する
                        // （TOCTOU 対策）。確定済みなら二重 Save（executed_orders 主キー競合）を避け自己修復に倒す。
                        var raced = executedOrders.FindByDecisionId(decisionId);
                        ExecutionRecord confirmed;
                        if (raced is not null)
                        {
                            reservations.MarkCompleted(decisionId, raced.OrderId, clock.UtcNow);
                            confirmed = raced;
                        }
                        else
                        {
                            // 発注済みが確定 → 記録を保存し確定する。OrderExecuted は既存イベント
                            // （監査済み・Risk/Notification が冪等消費）を再利用する。
                            confirmed = BuildRecord(decisionId, order, clock.UtcNow);
                            executedOrders.Save(confirmed);
                            reservations.MarkCompleted(decisionId, order.OrderId, clock.UtcNow);
                        }

                        executed.Add(ToOrderExecuted(confirmed));
                        // 🔴 #856, IADR-0362: **ブローカ照会で確定した**終端化だけを載せる（phase-4 自己修復は載せない
                        // ——自己修復は通常フローが作った記録の追認であり、保護レグの有無も通常フローが決めている）。
                        probeTerminalized.Add(new ReservationReconciliationFinding(
                            confirmed.DecisionId, confirmed.OrderId, confirmed.Symbol,
                            confirmed.Quantity, confirmed.Status));
                        terminalized++;
                        break;

                    case ReservationProbeOutcome.NotPlaced:
                        // 🔴 #856, IADR-0362: 未発注が確定 → 予約を解放する（＝再発注を許可する）。
                        // **解放の門が閉じているあいだは行わない。** 照会が「未発注」と答える根拠は remark 突合であり、
                        // remark が往復しなければ発注済みの注文も「一致ゼロ」に見える＝全件解放＝二重発注になる。
                        // 門を開けてよいのは実機で偽陽性が無いことを示した後だけである（#856 の受け入れ基準）。
                        if (!_options.ReleaseOnNotPlaced)
                        {
                            // 据え置くが**無音にしない**。Worker 層が警告でログし、運用が門を開ける判断の入力にする。
                            heldNotPlaced.Add(decisionId);
                            break;
                        }

                        // 解放後は元の OrderApproved 再配送が改めて予約→発注できる。
                        if (reservations.Release(decisionId))
                            released++;
                        break;

                    default:
                        // Indeterminate（照会不達・判定不能）: 二重発注を招かないため据え置く（fail-safe）。
                        indeterminate++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new ReservationReconciliationResult(
            stalled.Count, terminalized, released, indeterminate, failed, executed,
            probeTerminalized, heldNotPlaced);
    }

    // ブローカ照会結果（BrokerOrder）から発注結果記録を組み立てる。Intent はブローカが持つ注文実体に由来する。
    private static ExecutionRecord BuildRecord(Guid decisionId, BrokerOrder order, DateTimeOffset now)
    {
        var intent = order.Intent;
        var slippage = SlippageCalculator.Compute(intent.Price, order.AveragePrice, intent.Side);

        // 約定時刻はブローカが持つ完了/発注時刻を優先し、いずれも無ければリコンサイル時刻へフォールバックする。
        var executedAt = order.CompletedAt
            ?? (order.PlacedAt == default ? now : order.PlacedAt);

        return new ExecutionRecord(
            decisionId,
            order.OrderId,
            intent.Symbol,
            intent.Market,
            intent.Side,
            intent.ProductType,
            intent.PositionEffect,
            intent.Quantity,
            intent.Price,
            order.FilledQuantity,
            order.AveragePrice,
            order.Status,
            slippage,
            executedAt);
    }

    private OrderExecuted ToOrderExecuted(ExecutionRecord record) =>
        new(record.DecisionId, record.OrderId, record.Status, record.FilledQuantity, record.AveragePrice,
            record.ExecutedAt, broker.Provider);
}

// #141, IADR-0074: 1 巡回のリコンサイル結果。件数サマリ（可観測性）と、発行すべき OrderExecuted の一覧を持つ。
// Failed は当該巡回で例外により処理できなかった件数（据え置き＝次回巡回で再試行）。
//
// #856, IADR-0362: 件数だけでは「何が起きたか」を人が追えないため、**人が見なければならない 2 つ**を明細で持つ。
//   ProbeTerminalized —— 突合で発注済みと確定して終端化した注文（🔴 **保護レグは張られていない**。#853）。
//   HeldNotPlaced     —— 照会が未発注と答えたが、解放の門が閉じているため据え置いた予約。
public sealed record ReservationReconciliationResult(
    int Scanned,
    int Terminalized,
    int Released,
    int Indeterminate,
    int Failed,
    IReadOnlyList<OrderExecuted> Executed,
    IReadOnlyList<ReservationReconciliationFinding> ProbeTerminalized,
    IReadOnlyList<Guid> HeldNotPlaced);

// #856, IADR-0362: 突合で確定した 1 件の要約（ログ・運用手順で人が追える最小限）。
// 銘柄・数量・状態まで持つのは、運用者が証券会社の画面で突き合わせるのに要るためである。
public sealed record ReservationReconciliationFinding(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    int Quantity,
    OrderStatus Status);
