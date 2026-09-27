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
//        NotPlaced     → 🔴 **その予約の取引環境の解放の門（Reconciliation:ReleaseOnNotPlaced:Simulate / :Real）が開いて
//                        いるときだけ**予約を解放（Release）。閉じているあいだは据え置き、DecisionId を HeldNotPlaced に載せる
//                        （#856 / IADR-0362。門の選び方は #1051 / IADR-0444 / ReleaseGatePolicy。不明は据え置く）。
//        Indeterminate → 据え置き（人手/`_error` の現行安全側を壊さない）。門の開閉に依らない。
//
// FR-05, #856, IADR-0362: 突合で終端化した予約は ProbeTerminalized に載せて返し、Worker 層が 1 件ずつ Critical でログする
// （確定した時点では、エントリーなら保護レグが無い。**黙って通り過ぎさせない**）。
// 🔴 FR-10, FR-12, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定4: **突合で確定したエントリーには、承認時の手法で保護レグを張る**
// （オーナー裁定 2026-09-25。旧: 「張らない」〔IADR-0362 決定 3〕）。張るのは発注執行（IReconciledEntryProtection）であり、
// 呼ぶのは**確定した 1 件の出口（sink.EmitAsync）の後**である——保護の処理（ブローカーへの発注を含む）が落ちても、
// 確定済みの OrderExecuted と所見は既に出ている（IADR-0371）。結果は sink.EmitProtectionAsync へ渡す。
// エントリーかどうかは保護記録の有無で判別する（予約・プローブの PositionEffect は当てにならない。IADR-0362 決定 3）。
//
// 発行（OrderExecuted の Publish）は Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
//
// 🔴 #890, IADR-0371: **記録と発行は「確定した 1 件」ごとに、その場（commit の直後）で行う。**
// 確定した予約は次の巡回の FindStalledReserved（State=Reserved のみ）に載らないため、
// 出口を巡回の末尾に置くと、巡回が途中で中断されただけで確定済みの所見と OrderExecuted が永久に失われる
// （#890。ローリングデプロイ・Pod 再起動が巡回に重なるだけで起きる）。出口は IReservationReconciliationSink。
//
// FR-20, #386, IADR-0149 決定1: 発行する OrderExecuted には**実際に発注したアダプタの発注先**を載せる。
// 本リコンサイラが扱うのは自プロセスが出した（または出しかけた）注文だけであり、broker は発注時と同一である。
public sealed class OrderReservationReconciler(
    IOrderReservationStore reservations,
    IExecutedOrderStore executedOrders,
    IReservationBrokerProbe probe,
    IBrokerAdapter broker,
    IClock clock,
    IOptions<ReconciliationOptions>? options = null,
    IReconciledEntryProtection? entryProtection = null)
{
    // 🔴 #856, IADR-0362: 構成が無いときは**門を閉じた側**へ倒す（未登録＝解放してよい、にしない）。
    private readonly ReconciliationOptions _options = options?.Value ?? new ReconciliationOptions();

    /// <summary>
    /// <paramref name="stallCutoff"/> より古い滞留 Reserved を最大 <paramref name="batchSize"/> 件リコンサイルする。
    ///
    /// 終端化した予約は、その予約の確定（<c>MarkCompleted</c>）を commit した**直後**に
    /// <paramref name="sink"/> へ 1 件ずつ渡す（#890 / IADR-0371。記録と発行は Worker 層が担う）。
    /// 結果にも同じ明細（<see cref="ReservationReconciliationResult.Executed"/> /
    /// <see cref="ReservationReconciliationResult.ProbeTerminalized"/>）を載せて返すが、
    /// 🔴 **それは巡回サマリと表明のためであり、呼び出し側が再度出力する口ではない**（二重に出る）。
    /// </summary>
    public async Task<ReservationReconciliationResult> ReconcileAsync(
        DateTimeOffset stallCutoff,
        int batchSize,
        IReservationReconciliationSink? sink = null,
        CancellationToken cancellationToken = default)
    {
        var stalled = reservations.FindStalledReserved(stallCutoff, batchSize);

        var executed = new List<OrderExecuted>();
        var probeTerminalized = new List<ReservationReconciliationFinding>();
        var heldNotPlaced = new List<Guid>();
        var verdicts = new List<ReservationReconciliationVerdict>();
        var protections = new List<ReconciledEntryProtectionEmission>();
        var terminalized = 0;
        var released = 0;
        var indeterminate = 0;
        var failed = 0;

        foreach (var reservation in stalled)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // #890, IADR-0371: この 1 件が確定したときの出口ぶん。確定しなかった予約（据え置き・不確定・例外）は
            // null のままであり、出口へは渡らない（＝予約は Reserved のままで次回巡回が拾い直せる）。
            ReservationTerminalizationEmission? emission = null;

            // 🔴 #853, IADR-0428 決定4: 確定した発注結果の記録（保護レグを張る対象の候補）。競合（通常フローが確定した）では持たない
            // ——通常フローが保護レグを張っている最中であり、ここで張ると同じエントリーに 2 つの経路が触る。
            ExecutionRecord? protectionTarget = null;

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
                    var selfHealed = ToOrderExecuted(record);
                    executed.Add(selfHealed);
                    terminalized++;
                    // 🔴 phase-4 自己修復は突合ではない（ブローカへ照会していない）。所見は載せない（IADR-0362 決定 3）。
                    emission = new ReservationTerminalizationEmission(
                        selfHealed, ProbeFinding: null, reservation.BrokerProvider);
                    // #853, IADR-0428 決定4: 記録の保存後・確定の前に通常フローが止まった場合、保護レグは張られていない
                    // （事前記録が AwaitingEntry のまま残る）。張るかどうかは保護記録が決める（Active なら何もしない）。
                    protectionTarget = record;
                }
                else
                {
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
                                protectionTarget = confirmed;
                            }

                            var placedExecuted = ToOrderExecuted(confirmed);
                            executed.Add(placedExecuted);
                            // 🔴 #856, IADR-0362: **ブローカ照会で確定した**終端化だけを載せる（phase-4 自己修復は載せない
                            // ——自己修復は通常フローが作った記録の追認であり、保護レグの有無も通常フローが決めている）。
                            var finding = new ReservationReconciliationFinding(
                                confirmed.DecisionId, confirmed.OrderId, confirmed.Symbol,
                                confirmed.Quantity, confirmed.Status);
                            probeTerminalized.Add(finding);
                            terminalized++;
                            emission = new ReservationTerminalizationEmission(
                                placedExecuted, finding, reservation.BrokerProvider);
                            break;

                        case ReservationProbeOutcome.NotPlaced:
                            // 🔴 #856, IADR-0362: 未発注が確定 → 予約を解放する（＝再発注を許可する）。
                            // **解放の門が閉じているあいだは行わない。** 照会が「未発注」と答える根拠は remark 突合であり、
                            // remark が往復しなければ発注済みの注文も「一致ゼロ」に見える＝全件解放＝二重発注になる。
                            // 門を開けてよいのは実機で偽陽性が無いことを示した後だけである（#856 の受け入れ基準）。
                            //
                            // 🔴 #890, IADR-0371: 本経路は**確定していない**（MarkCompleted を commit していない）。
                            // したがって出口（sink）へは渡さない —— 据え置いた予約は Reserved のまま次回巡回に載るため、
                            // 巡回が中断されても警告は失われない（失われるのは「確定済み」のものだけである）。
                            //
                            // 🔴 NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定3: **門はこの予約の取引環境で選ぶ**
                            // （プロセスに 1 つの真偽値ではない）。SIMULATE の門が開いていても、実弾の予約・取引環境が不明な予約・
                            // 照会先と取引環境が食い違う予約は解放しない。
                            if (!ReleaseGatePolicy.MayRelease(
                                    reservation.BrokerProvider, broker.Provider, _options.ReleaseOnNotPlaced))
                            {
                                // 据え置くが**無音にしない**。Worker 層が警告でログし、運用が門を開ける判断の入力にする。
                                heldNotPlaced.Add(decisionId);
                                verdicts.Add(new ReservationReconciliationVerdict(
                                    decisionId, reservation.BrokerProvider, ReservationReconciliationVerdictKind.HeldNotPlaced));
                                break;
                            }

                            // 解放後は元の OrderApproved 再配送が改めて予約→発注できる。
                            if (reservations.Release(decisionId))
                            {
                                released++;
                                verdicts.Add(new ReservationReconciliationVerdict(
                                    decisionId, reservation.BrokerProvider, ReservationReconciliationVerdictKind.Released));
                            }

                            break;

                        default:
                            // Indeterminate（照会不達・判定不能）: 二重発注を招かないため据え置く（fail-safe）。門の開閉に依らない。
                            indeterminate++;
                            verdicts.Add(new ReservationReconciliationVerdict(
                                decisionId, reservation.BrokerProvider, ReservationReconciliationVerdictKind.Indeterminate));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                verdicts.Add(new ReservationReconciliationVerdict(
                    reservation.DecisionId, reservation.BrokerProvider, ReservationReconciliationVerdictKind.Failed));
            }

            // 🔴 #890, IADR-0371: **出口は per-item の try/catch の外に置く。**
            // 中に入れると発行の失敗が failed++ に吸い込まれ、「発行の失敗は握り潰さない」（T-10-609）が壊れる。
            // しかも予約は既に Completed であり、failed（＝据え置き・次回巡回で再試行）に数えるのは事実として誤りである。
            // 出口が落ちたときの挙動は是正前と同じ（巡回はそこで止まり、常駐が拾って次回巡回で再試行する）。
            // 残りの滞留は Reserved のままなので拾い直せる。
            if (emission is not null && sink is not null)
                await sink.EmitAsync(emission).ConfigureAwait(false);

            // 🔴 FR-10, #853, IADR-0428 決定4: 確定済みの 1 件の出口を出した**後**に、承認時の手法で保護レグを張る。
            // 失敗は握り潰さずに記録として出口へ渡す（保護が無い建玉を無音にしない）が、巡回は止めない——残りの滞留の突合を
            // 1 件の保護の失敗で止めると、その分の OrderExecuted も遅れる。取消トークンは渡さない（確定後の後始末であり、
            // 逆指値の送信を途中で打ち切ると「予約だけがあり記録が無い」逆指値を自分で作る）。
            if (protectionTarget is not null && emission is not null)
            {
                var protection = await ProtectAsync(protectionTarget, probeConfirmed: emission.ProbeFinding is not null)
                    .ConfigureAwait(false);
                protections.Add(protection);
                if (sink is not null)
                    await sink.EmitProtectionAsync(protection).ConfigureAwait(false);
            }
        }

        return new ReservationReconciliationResult(
            stalled.Count, terminalized, released, indeterminate, failed, executed,
            probeTerminalized, heldNotPlaced, protections, verdicts);
    }

    // #853, IADR-0428 決定4: 保護の口を 1 件ぶん呼ぶ。口が無い構成は「張れない」として返す（黙って飛ばさない）。
    private async Task<ReconciledEntryProtectionEmission> ProtectAsync(ExecutionRecord confirmed, bool probeConfirmed)
    {
        if (entryProtection is null)
            return new ReconciledEntryProtectionEmission(confirmed, probeConfirmed, Outcome: null, Failure: null);

        try
        {
            var outcome = await entryProtection.ProtectAsync(confirmed, CancellationToken.None).ConfigureAwait(false);
            return new ReconciledEntryProtectionEmission(confirmed, probeConfirmed, outcome, Failure: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReconciledEntryProtectionEmission(confirmed, probeConfirmed, Outcome: null, Failure: ex);
        }
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
//   ProbeTerminalized —— 突合で発注済みと確定して終端化した注文（確定の時点では、エントリーなら保護レグが無い）。
//   HeldNotPlaced     —— 照会が未発注と答えたが、解放の門が閉じているため据え置いた予約。
// 🔴 #853, IADR-0428 決定4: Protections —— 確定した 1 件ごとの保護の結果（張った・据え置いた・張れない・要らない・失敗）。
// 🔴 #1051, IADR-0444 決定6: Verdicts —— 確定しなかった判定（据え置き・解放・不確定・失敗）を 1 件ずつ、**予約の取引環境つきで**持つ。
//   計数を取引環境ごとに分けるためである（ADR-0045 決定1 (b) の held-not-placed は取引環境ごとに数える）。
public sealed record ReservationReconciliationResult(
    int Scanned,
    int Terminalized,
    int Released,
    int Indeterminate,
    int Failed,
    IReadOnlyList<OrderExecuted> Executed,
    IReadOnlyList<ReservationReconciliationFinding> ProbeTerminalized,
    IReadOnlyList<Guid> HeldNotPlaced,
    IReadOnlyList<ReconciledEntryProtectionEmission> Protections,
    IReadOnlyList<ReservationReconciliationVerdict> Verdicts);

// #1051, IADR-0444 決定6: 確定しなかった判定の種類（巡回サマリの内訳のうち、確定した 1 件の出口を通らないもの）。
public enum ReservationReconciliationVerdictKind
{
    /// <summary>照会は未発注と答えたが、その予約の取引環境の門が閉じている（または取引環境が不明・照会先と食い違う）ため据え置いた。</summary>
    HeldNotPlaced,

    /// <summary>照会が未発注と答え、その予約の取引環境の門が開いていたので解放した（再発注の許可）。</summary>
    Released,

    /// <summary>照会不達・判定不能で据え置いた。門の開閉に依らない。</summary>
    Indeterminate,

    /// <summary>その 1 件の処理が例外で落ち、据え置いた（次の巡回で再試行）。</summary>
    Failed,
}

// #1051, IADR-0444 決定6: 確定しなかった判定 1 件。ReservationProvider は予約の取引環境（null は不明）。
public sealed record ReservationReconciliationVerdict(
    Guid DecisionId,
    BrokerProvider? ReservationProvider,
    ReservationReconciliationVerdictKind Kind);

// #856, IADR-0362: 突合で確定した 1 件の要約（ログ・運用手順で人が追える最小限）。
// 銘柄・数量・状態まで持つのは、運用者が証券会社の画面で突き合わせるのに要るためである。
public sealed record ReservationReconciliationFinding(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    int Quantity,
    OrderStatus Status);
