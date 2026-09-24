using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// FR-05, UC-01, UC-02, ADR-0002/0003: 承認済み注文（OrderApproved）をブローカへ発注し、
// 結果（約定/拒否/取消）を OrderExecuted として確定する。注文実体＋スリッページを永続化する（FR-16 の月報データ源）。
// ブローカ実装は IBrokerAdapter で差し替える（既定はペーパー・moomoo は SIMULATE 限定。IADR-0016/0056）。
//
// #131, IADR-0057: 発注は「予約 → 発注 → 確定」の3相で冪等化する。ブローカ発注の前に DecisionId の一意予約を
// コミットすることで、「ブローカ発注成功 → 永続化失敗」の窓でも再配送時に二重発注しない。
//
// FR-10, #331, IADR-0210: 損切りはブローカー側逆指値へ一本化した。エントリー（Open）には保護逆指値を
// **同時発注**し、逆指値を張れない Open では建玉を持たない（見送り／取消／成行手仕舞い＝fail-closed）。
// FR-05, #331, IADR-0211: OpenD へ確実に届いていない発注（BrokerUnavailableException）は Rejected へ丸めず、
// 「見送り」（OrderDispatchForgone）で正常終了する（キューイングしない）。
// 🔴 FR-05, FR-10, UC-06, #876, IADR-0398: 見送りは**発行する前に予約表へ Forgone として記録し**、同じ承認の再配送では
// 発注しない（予約の解放＝削除はやめた。削除すると再配送が予約を取り直し、台帳が押さえていない決済が生きる）。
//
// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342: 損切りの実行機構は承認が運ぶ（既定 S0）。解釈は
// StopLossMethodPolicy だけが行う。**S2（moomoo SIMULATE の新規買いに限る）では保護逆指値を発注せず建玉を保持し、
// 免除の事実（ProtectiveStopWaived）を発行する**——「逆指値なしの建玉を持たない」の例外はこの 1 分岐だけである。
// S0 以外が SIMULATE 以外へ届いたら発注しない（実弾を無防備にしない）。未知の手法は未実装のため S0 と同じ扱い。
//
// FR-10, FR-12, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: **S1（moomoo SIMULATE の新規買いに限る）では保護逆指値を
// ブローカーへ出さず、エントリーを送る前にソフトウェア逆指値を永続化する**。決済は損切りライン到達で SoftwareStopExecutor が行う。
//
// FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: **S3 は保護レグを代替注文種別（StopLimit / TrailingStop）で
// 発注し、種別と拒否理由（retType / retMsg）を AlternativeProtectiveStopAttempted として残す**。
// 結果の扱いは S0 と完全に同じ（受理＝保護レグの記録／拒否＝建玉を持たない）——分岐するのは「何で発注するか」だけである。
//
// 🔴 FR-10, FR-05, ADR-0016, UC-06, #864, IADR-0355: **決済（Close）はブローカーの実建玉と突き合わせてから送る。**
// 決済の数量の出所は台帳の射影であってブローカーの事実ではないため、台帳が乖離していると（#849）決済注文が
// **保有 0 からの売り＝裸の新規ショート**になる。突合の能力（IBrokerPositionSource）を持つ発注先でのみ行い、
// 内蔵 paper では従来どおり照合しない（依存が DI に現れない＝構造的な非干渉）。
public sealed class OrderExecutionAppService(
    IBrokerAdapter broker,
    IExecutedOrderStore store,
    IOrderReservationStore reservations,
    IClock clock,
    IProtectiveStopOrderStore? protectiveStops = null,
    ILogger<OrderExecutionAppService>? logger = null,
    IBrokerPositionSource? brokerPositions = null)
{
    // #820 の 8 巡目監査, IADR-0344 追記(8): 武装の前提条件（帰属不明の建玉が無いこと）を確かめるために
    // 見る Active 行の上限。保有建玉数上限（既定 3）に対して十分大きい。
    private const int ArmingScanLimit = 500;

    private readonly ILogger _logger = logger ?? NullLogger<OrderExecutionAppService>.Instance;

    // 建玉照会は実運用ではブローカーアダプタそのものが実装する（Program.cs の配線と同じ）。
    // 明示指定があればそれを使う（テスト・差し替え用）。
    private readonly IBrokerPositionSource? _positions = brokerPositions ?? broker as IBrokerPositionSource;

    public async Task<OrderDispatchResult> ExecuteAsync(OrderApproved approved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);

        // 相1（完了の権威）: 同一 DecisionId の発注結果が既にあれば、再発注せず既存結果を再発行する。メッセージングの
        // 再配送（UseAiStockTradingRabbitMq の共通再試行）で同一 OrderApproved が再処理されても二重発注・二重計上しない。DecisionId は
        // 取引判断/機械執行1件に対応する。
        // 予約表（相2）の導入前に既に存在する行にも効かせるため、この照合を完了判定の権威として残す（IADR-0057）。
        // 保護レグのイベントは再発行しない——逆指値レグは初回処理で記録済みであり、台帳の承認行（AppendApproval）も
        // DecisionId で冪等のため、再発行しなくても失われない。
        var existing = store.FindByDecisionId(approved.DecisionId);
        if (existing is not null)
        {
            // FR-20, #386, IADR-0149 決定1: 発注先は**現在のアダプタ**の値である。記録（ExecutionRecord）は
            // 発注先を保持しないため、再発行の時点で構成が変わっていれば当時と異なる値が載り得る。
            // 下流（Stage 1 の取引件数）は DecisionId で先着優先に記録するため、既に観測済みの注文は
            // 上書きされない。残余リスクは IADR-0149 に記録した。
            // NFR-01, NFR-02, #689, IADR-0307: 再発行でも起点は**今届いた承認**が運ぶものを載せる
            // （記録（ExecutionRecord）は起点を保持しない）。載せなければ記録完了の区間が閉じない。
            return OrderDispatchResult.FromExecuted(new OrderExecuted(
                existing.DecisionId, existing.OrderId, existing.Status,
                existing.FilledQuantity, existing.AveragePrice, existing.ExecutedAt, broker.Provider,
                approved.CycleTrigger, approved.CycleStartedAt));
        }

        // 🔴 FR-05, FR-10, UC-06, #876, IADR-0398: **見送った DecisionId は、再配送されても発注しない。**
        // 見送り（OrderDispatchForgone）を受けたリスク管理は取引台帳の承認を終端にし、在庫を戻す（IADR-0356）。
        // ここで同じ承認を送り直すと、その注文は台帳の「処理中の決済」に数えられず（台帳の終端は単調で戻らない）、
        // 利用者の 2 本目の手仕舞いが通る＝同じ株数に 2 本の決済が並ぶ。再発注は次の取引判断からのみ（IADR-0211 決定 3）。
        // **ブローカーには一切触れない**（建玉照会もしない）。見送りの理由は記録していないため、見送りイベントは再発行しない。
        if (reservations.Find(approved.DecisionId) is { State: OrderDispatchState.Forgone } forgoneEarlier)
        {
            _logger.LogWarning(
                "見送り済みの承認が再配送されました。発注しません（再発注は次の取引判断からのみ）: "
                + "DecisionId={DecisionId} 銘柄={Symbol} 数量={Quantity} 見送りを記録した時刻={ForgoneAt}。"
                + "見送りイベントは再発行しません（理由を記録していないため）。この承認の注文はブローカーへ送られていません。",
                approved.DecisionId, approved.Intent.Symbol, approved.Intent.Quantity, forgoneEarlier.CompletedAt);
            return OrderDispatchResult.FromForgoneReplaySuppressed();
        }

        var intent = approved.Intent;

        // 🔴 FR-10, FR-05, ADR-0016, UC-06, #864, IADR-0355: **決済（Close）はブローカーの実建玉と突き合わせてから送る。**
        // 決済の数量の出所は台帳の射影であってブローカーの事実ではないため（IADR-0119 決定1 / IADR-0351 決定6）、
        // 台帳が乖離していると（#849。台帳 3,381 株 / ブローカー 0 株を実測）ブローカー上では
        // **保有 0 からの売り＝裸の新規ショート**になる。**予約（相2）より前**に判定する
        //（送らないと決めたら発注に着手しない＝予約も取らない。逆指値を張れない Open の見送りと同じ位置）。
        // 能力の無い発注先（内蔵 paper）では brokerPositions が DI に現れないため、この分岐そのものが起きない。
        PositionReconciliationDrift? drift = null;
        if (intent.PositionEffect == PositionEffect.Close && brokerPositions is not null)
        {
            // ポートの契約は「照会不能は null（例外を投げない）」である（IBrokerPositionSource）。
            // #873 の監査 N2: それでも**例外は不明として扱う**——契約違反の実装が現れたときに
            // ExecuteAsync ごと落ちると、承認が再配送で撃ち直され（予約はまだ取っていない）、
            // 最後には error キューへ落ちる。落とすより「不明として送らない」方が本 IADR の向きと一致する。
            IReadOnlyList<BrokerPositionSnapshot>? snapshot;
            try
            {
                snapshot = await brokerPositions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ブローカーの建玉照会が例外で失敗しました（不明として扱います）。");
                snapshot = null;
            }

            var verdict = BrokerHeldPositionGate.Evaluate(intent, snapshot);
            switch (verdict.Outcome)
            {
                case BrokerHeldPositionOutcome.Indeterminate:
                    // 🔴 IADR-0355 決定3: **不明のまま決済を送らない。** 空列（建玉ゼロ）と null（不明）を
                    // 取り違えないという契約の下で、「分からない」を「持っている」へは倒さない。
                    // 選ばなかった側（不明でも送る）の害は、台帳が乖離していたときに裸のショートが出ること——
                    // 不可逆であり、買い戻すまで損失が限定されない。選んだ側の害（手仕舞いが出ない）は
                    // 建玉が残るだけで可逆であり、**損切りはブローカー側の逆指値が担う**ため本経路の見送りで消えない。
                    _logger.LogError(
                        "決済を見送りました: ブローカーの建玉を照会できません（不明）。台帳の建玉だけを根拠に売ると"
                        + "保有 0 からの売り（裸のショート）になり得るため送りません。証券会社の画面で建玉を確認してください: "
                        + "DecisionId={DecisionId} 銘柄={Symbol} 数量={Quantity}",
                        approved.DecisionId, intent.Symbol, intent.Quantity);
                    return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.BrokerPositionsIndeterminate);

                case BrokerHeldPositionOutcome.NoPosition:
                    // 🔴 IADR-0355 決定2: 決済方向の実建玉が 0。送れば**裸の新規ショート**である（1 株も送らない）。
                    _logger.LogError(
                        "決済を見送りました: ブローカーに決済方向の建玉がありません"
                        + "（台帳 {Ledger} 株 / ブローカーのネット建玉 {Broker} 株）。"
                        + "送れば保有 0 からの売り（裸のショート）になります: DecisionId={DecisionId} 銘柄={Symbol}",
                        intent.Quantity, verdict.BrokerNetQuantity, approved.DecisionId, intent.Symbol);
                    return RecordForgoneBeforeReservation(
                        approved,
                        OrderDispatchForgoneReason.BrokerPositionAbsent,
                        DriftOf(intent, verdict.BrokerNetQuantity, verdict.ClosableQuantity));

                case BrokerHeldPositionOutcome.Reduce:
                    // IADR-0355 決定2: 実建玉の範囲へ縮めて送る（実在する建玉の手仕舞いまで塞がない）。
                    // **縮めた事実は必ず監査・通知に残す**（下の drift。黙って数量を変えない）。
                    _logger.LogWarning(
                        "決済の数量をブローカーの実建玉へ縮めました"
                        + "（台帳 {Ledger} 株 / ブローカーのネット建玉 {Broker} 株 → 決済方向で送れる {Sent} 株）: "
                        + "DecisionId={DecisionId} 銘柄={Symbol}",
                        intent.Quantity, verdict.BrokerNetQuantity, verdict.ClosableQuantity,
                        approved.DecisionId, intent.Symbol);
                    drift = DriftOf(intent, verdict.BrokerNetQuantity, verdict.ClosableQuantity);
                    intent = intent with { Quantity = verdict.ClosableQuantity };
                    break;
            }
        }

        // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 承認が運ぶ手法を解決する（Open にのみ効く）。
        // S0 は常に S0 であり、以降の分岐は 1 バイトも変わらない。
        var disposition = intent.PositionEffect == PositionEffect.Open
            ? ResolveStopLossMethod(approved)
            : StopLossMethodDisposition.BrokerStopOrder;
        if (disposition == StopLossMethodDisposition.Refused)
        {
            return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.StopLossMethodNotPermitted);
        }

        // FR-10, #331, IADR-0210 決定1: 逆指値を張れない Open は**発注せず**見送る（建玉を作らない側へ倒す）。
        // 予約の前に判定する（発注に着手しないため予約は要らない）。
        if (intent.PositionEffect == PositionEffect.Open)
        {
            if (intent.StopLossPrice is not { } stopLoss || stopLoss <= 0m)
            {
                return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.StopLossPriceMissing);
            }

            if (broker is not IProtectiveOrderBroker)
            {
                return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.StopOrderUnsupported);
            }

            // #820, IADR-0344 決定3: S1 はソフトウェア逆指値の記録先が要る（無ければ建玉を守れないため建てない）。
            if (disposition == StopLossMethodDisposition.SoftwareStop && protectiveStops is null)
            {
                _logger.LogError(
                    "損切りの実行機構 S1 の記録先（保護記録ストア）が構成されていないため発注しません（DecisionId={DecisionId}）。",
                    approved.DecisionId);
                return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.StopOrderUnsupported);
            }

            // 🔴 #820 の 8 巡目監査, IADR-0344 追記(8) 決定4: **S1 は帰属不明の建玉がある銘柄では武装しない。**
            // S1 の行が守る株数はブローカーの純額からしか測れず、他人の建玉（S2・人手・S0 の発注窓）と
            // 自分の建玉を区別できない。先に他人の建玉が在ると超過が一度も観測されないまま満額の主張が残り、
            // 到達でその建玉を S1 の損切りラインで売る（監査が実測。稼働中の S2 から S1 へ切り替えた直後そのもの）。
            // 「保護レグを張れない Open では建玉を持たない」（IADR-0210 決定1）に合わせ、**建玉を持たずに見送る**。
            if (disposition == StopLossMethodDisposition.SoftwareStop
                && protectiveStops!.Find(approved.DecisionId) is null
                && await HasUnattributedPositionAsync(approved, cancellationToken).ConfigureAwait(false))
            {
                return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.UnattributedPosition);
            }

            // FR-10, ADR-0040 決定1（S3）, #821, IADR-0347: S3 の能力が無い発注先へ S3 が届いたら**発注しない**
            // （S0 へ黙って読み替えない。上の「逆指値能力が無い Open は見送る」と同じ fail-closed）。
            if (disposition == StopLossMethodDisposition.AlternativeBrokerOrderType
                && broker is not IAlternativeProtectiveOrderBroker)
            {
                return RecordForgoneBeforeReservation(approved, OrderDispatchForgoneReason.StopOrderUnsupported);
            }
        }

        // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: 🔴 **エントリーを送る前に**ソフトウェア逆指値を Active で残す。
        // 送った後に保存すると「建玉はあるのにソフトウェア逆指値が無い」窓ができる（保存の失敗・プロセス停止）。
        // 既に行があれば触らない（再配送で到達の記録や試行数を巻き戻さない）。建玉が生じなければ下で完了にする。
        if (disposition == StopLossMethodDisposition.SoftwareStop && protectiveStops!.Find(approved.DecisionId) is null)
        {
            var armedAt = clock.UtcNow;
            protectiveStops.Save(new ProtectiveStopOrder(
                approved.DecisionId, ProtectiveStopIds.SoftwareStopId(approved.DecisionId), StopOrderId: string.Empty,
                intent.Symbol, intent.Market, intent.Side, intent.ProductType, intent.Mode, intent.Quantity,
                intent.StopLossPrice!.Value, intent.FxRateToBase, Attempt: 0, ProtectiveStopState.Active, armedAt, armedAt,
                Mechanism: StopLossExecutionMethod.SoftwareStop));
        }

        // 相2（発注着手の権威）: ブローカへ送る「前」に一意予約を確保する。確保できない＝予約済みで未確定であり、
        // 「未発注」と「発注済みだが記録できていない」を区別できない。実弾では二重発注（不可逆）の方が
        // 取りこぼし（可逆）より重いため、再発注せず拒否する（at-most-once・IADR-0057）。
        // 再試行を使い切ると _error キューへ送られ、ブローカ状態を確認するリコンサイルの対象になる。
        if (!reservations.TryReserve(approved.DecisionId, clock.UtcNow))
            throw new OrderDispatchReservationConflictException(approved.DecisionId);

        // 相3: ADR-0003: 承認済み注文のみ発注する。Close（owner 手仕舞い・自動縮小）も同一経路。
        // #141, IADR-0092: ブローカが client order id 伝播に対応していれば DecisionId を紐づけて発注する
        // （滞留 Reserved を後から DecisionId で照合＝実照会リコンサイルの前提）。非対応（paper 等）は従来経路。
        // 🔴 FR-10, UC-06, #847, IADR-0357: **成行の手仕舞いだけ**、既存の成行の口（IADR-0210 で入った
        // IProtectiveOrderBroker.PlaceMarketOrderAsync）へ送る。新しいブローカー呼び出しは 1 つも作らない。
        // 現在値の指値は下落局面で置いていかれ、手仕舞いが必要な場面でこそ効かない（稼働環境で実測・#847）。
        // 成行の能力が無い発注先では**指値で送る**（見送らない）——手仕舞いを止めないほうが重い（FR-10）。
        // 実在するアダプタ（内蔵 paper / moomoo）はどちらも能力を持つため、この退避は到達しない。
        var marketClose = intent is { PositionEffect: PositionEffect.Close, MarketOrder: true }
            && broker is IProtectiveOrderBroker;

        BrokerOrder brokerOrder;
        try
        {
            brokerOrder = marketClose
                ? await ((IProtectiveOrderBroker)broker)
                    .PlaceMarketOrderAsync(intent, approved.DecisionId, cancellationToken).ConfigureAwait(false)
                : broker is IClientOrderIdBroker correlating
                    ? await correlating.PlaceOrderAsync(intent, approved.DecisionId, cancellationToken).ConfigureAwait(false)
                    : await broker.PlaceOrderAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            // FR-05, ADR-0002（SPOF・再起動中は発注不可）, #331, IADR-0211: 接続確立の失敗＝**確実に未発注**。
            // キューイングせず見送りで正常終了する（Rejected へ丸めない）。
            // 🔴 #876, IADR-0398: 予約は**解放（削除）せず Forgone へ移す**。削除すると同じ承認の再配送が予約を取り直して
            // 発注できてしまい、見送りを受けて在庫を戻した台帳が押さえていない決済が生きる（IADR-0356 の残余リスク 3）。
            // 送信後の失敗（届いたか不明）は本例外の契約外であり、BrokerDispatchIndeterminateException として
            // 伝播する（次の catch。予約は Reserved のまま据え置く＝再配送で二重発注しない。#848・IADR-0117 改定 6）。
            var recorded = reservations.MarkReservationForgone(approved.DecisionId, clock.UtcNow);
            if (recorded is ForgoneRecordOutcome.Recorded or ForgoneRecordOutcome.AlreadyForgone)
                CompleteSoftwareStopWithoutPosition(approved, disposition);

            return ForgoneIfRecorded(approved, OrderDispatchForgoneReason.BrokerUnavailable, recorded);
        }
        catch (BrokerDispatchIndeterminateException ex)
        {
            // 🔴 FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
            // **送信後に結果を確認できなかった＝届いたか不明。** ここで行ってよいことは「何もしない」だけである。
            //   - 予約を**解放しない**（解放すると再配送で二重発注になる。BrokerUnavailable との決定的な違い）。
            //   - 予約を**確定しない**・結果を**保存しない**（実在しない注文 ID の終端記録を台帳へ残さない）。
            //   - 見送り（OrderDispatchForgone）にも**しない**——見送りは「発注していない」という主張であり、
            //     ここでそれを主張すると Rejected と同じ誤り（建玉が無いという仮定）になる。
            // 予約は Reserved のまま残る。**二重発注を防ぐのはこの予約であり、リコンサイルの有無に依らない。**
            // 滞留の解消は、client order id によるリコンサイル（IADR-0092 / IADR-0074）が
            // Placed / NotPlaced / Indeterminate に解決する。🔴 #856, IADR-0362: **アプリ既定は無効のままだが、
            // 配備（Helm values）では有効**である。ただし**解放（NotPlaced）の門は閉じている**
            // （Reconciliation:ReleaseOnNotPlaced=false）ので、自動で解決するのは Placed 側だけであり、
            // NotPlaced / Indeterminate は据え置かれて人が証券会社の画面で確認する
            //（docs/operations/broker-execution-paths-runbook.md）。**本経路は例外で終わるのが正しい。**
            // 再試行を使い切ったあと _error キューに残る例外は OrderDispatchReservationConflictException であり
            // 真因を指さない。**真因は初回のこの Error ログである。**
            _logger.LogError(ex,
                "発注の結果を確認できませんでした（送信済み・届いたか不明）: DecisionId={DecisionId} 銘柄={Symbol} 数量={Quantity}。"
                + "予約は Reserved のまま据え置きます（拒否へ畳まず・見送りにもしません）。自動リコンサイルが"
                + "「発注済み」と確定できなかった場合は、証券会社の画面で注文を確認してください。",
                approved.DecisionId, intent.Symbol, intent.Quantity);
            throw;
        }

        // FR-16: 実効スリッページを取引毎に算出・記録する。
        var slippage = SlippageCalculator.Compute(intent.Price, brokerOrder.AveragePrice, intent.Side);

        // 約定時刻はブローカ往復の「後」に取る。予約時刻を流用すると往復の実時間が記録から消え、
        // 監査・リコンサイル時に「予約時刻＝約定時刻」に見えてしまう。
        var now = clock.UtcNow;

        // 相4（確定）: 結果を保存してから予約を確定する。この順序により、Save 成功・確定失敗で落ちても
        // 再処理は相1で既存結果を再発行できる（逆順だと結果の無い Completed 予約が生じ、窓が残る）。
        store.Save(new ExecutionRecord(
            approved.DecisionId,
            brokerOrder.OrderId,
            intent.Symbol,
            intent.Market,
            intent.Side,
            intent.ProductType,
            intent.PositionEffect,
            intent.Quantity,
            intent.Price,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            brokerOrder.Status,
            slippage,
            now));

        reservations.MarkCompleted(approved.DecisionId, brokerOrder.OrderId, now);

        // FR-20, FR-12, #386, IADR-0149 決定1: **実際に発注したアダプタの発注先**を載せる。
        // 取引判断が運ぶ intent.Mode は「段階が定める既定の発注先」であって現在の発注先ではない（IADR-0140 決定3）。
        // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を記録側（監査）まで運ぶ。
        var executed = new OrderExecuted(
            approved.DecisionId,
            brokerOrder.OrderId,
            brokerOrder.Status,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            now,
            broker.Provider,
            approved.CycleTrigger,
            approved.CycleStartedAt);

        // FR-10, #331, IADR-0210 決定1/3: Open のエントリーが生きている（＝建玉になった・なり得る:
        // Accepted / PartiallyFilled / Filled）なら、保護逆指値を**同時発注**する。
        // 未受理なら建玉を持たない（未約定→取消／約定済み→成行手仕舞い）。
        // 終端失敗（Rejected / Cancelled / Expired）は建玉が生じないため保護レグ自体が不要である。
        var entryAlive = brokerOrder.Status
            is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled;
        if (intent.PositionEffect == PositionEffect.Open && entryAlive
            && disposition == StopLossMethodDisposition.ProtectiveStopWaived)
        {
            // FR-10, FR-12, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: 保護逆指値を発注せず建玉を保持する。
            // 取消・手仕舞いも行わない。逆指値レグの記録（protective_stop_orders）を作らないため、
            // ProtectiveStopGuard の巡回対象に入らない（失効扱いで手仕舞われることが構造的に無い）。
            var waived = new ProtectiveStopWaived(
                approved.DecisionId, intent.Symbol, intent.Market, intent.Side, intent.ProductType,
                intent.Quantity, intent.StopLossPrice, approved.StopLossMethod, broker.Provider, now);
            return OrderDispatchResult.FromExecuted(executed, stopWaived: waived);
        }

        if (intent.PositionEffect == PositionEffect.Open && disposition == StopLossMethodDisposition.SoftwareStop)
        {
            // FR-10, FR-12, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: ブローカーへ保護レグを出さない。
            // 建玉が生じ得る（生きている）ならソフトウェア逆指値の配置を発行し、生じないなら記録を完了にする。
            if (!entryAlive)
            {
                CompleteSoftwareStopWithoutPosition(approved, disposition);
                return OrderDispatchResult.FromExecuted(executed);
            }

            var armed = new SoftwareStopArmed(
                approved.DecisionId, intent.Symbol, intent.Market, intent.Side, intent.ProductType, intent.Quantity,
                intent.StopLossPrice!.Value, broker.Provider, now);
            return OrderDispatchResult.FromExecuted(executed, softwareStopArmed: armed);
        }

        if (intent.PositionEffect == PositionEffect.Open && entryAlive)
        {
            var useAlternative = disposition == StopLossMethodDisposition.AlternativeBrokerOrderType;
            var (stopPlaced, coverageLost, attempted) = await PlaceProtectiveStopAsync(
                    approved, brokerOrder, useAlternative, cancellationToken)
                .ConfigureAwait(false);
            return OrderDispatchResult.FromExecuted(executed, stopPlaced, coverageLost, stopAttempted: attempted);
        }

        // #864, IADR-0355 決定5: 数量を縮めた決済はここへ帰る（Open の 2 分岐は drift を持ち得ない）。
        // 監査（3 巡目）3: 乖離を添えるときは**実際に送った株数**も渡す（通知・ログで取り違えさせない）。
        return OrderDispatchResult.FromExecuted(
            executed, drift: drift, driftDispatchedQuantity: drift is null ? 0 : intent.Quantity);
    }

    // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4・決定7: 解決とログ。拒否は Error（実弾で S0 以外が
    // 有効＝設定側の関門が 2 方向とも破られた状態であり、放置してはならない）、未実装は Warning。
    private StopLossMethodDisposition ResolveStopLossMethod(OrderApproved approved)
    {
        var disposition = StopLossMethodPolicy.Resolve(approved.StopLossMethod, approved.Intent, broker.Provider);
        switch (disposition)
        {
            case StopLossMethodDisposition.Refused:
                _logger.LogError(
                    "損切りの実行機構 {Method} は moomoo SIMULATE でしか選べませんが、発注先は {Provider} です。"
                    + "発注しません（DecisionId={DecisionId}・ADR-0040 決定1）。利用者の設定で S0 へ戻してください。",
                    approved.StopLossMethod, broker.Provider, approved.DecisionId);
                break;
            case StopLossMethodDisposition.NotImplementedFallbackToBrokerStop:
                _logger.LogWarning(
                    "損切りの実行機構 {Method} は未実装のため S0（ブローカー側逆指値）と同じ扱いで発注します"
                    + "（DecisionId={DecisionId}）。",
                    approved.StopLossMethod, approved.DecisionId);
                break;
        }

        return disposition;
    }

    // 🔴 FR-05, FR-10, UC-06, #876, IADR-0398: 予約を取る**前**の見送り。**記録してから**結果を作る。
    // 予約前の見送りは従来、予約表に何も残さなかった——台帳は見送りを受けて在庫を戻すのに、照会が回復した後の
    // 再配送は同じ承認を送れてしまう（建玉照会の不明・建玉なしで実測）。記録は見送りの発行より先にコミットされる。
    // 🔴 見送りの結果（OrderDispatchResult.Forgone）は**本メソッドか ForgoneIfRecorded を通してだけ**作ること
    // （下の Forgone を直接呼ぶと記録が抜け、この穴が戻る。T-10-826 が理由ごとに固定する）。
    private OrderDispatchResult RecordForgoneBeforeReservation(
        OrderApproved approved, OrderDispatchForgoneReason reason, PositionReconciliationDrift? drift = null) =>
        ForgoneIfRecorded(approved, reason, reservations.TryRecordForgone(approved.DecisionId, clock.UtcNow), drift);

    // 🔴 FR-05, #876, IADR-0398: **見送りを主張してよいのは、この DecisionId を Forgone として記録できたときだけである。**
    // 予約（Reserved）や確定（Completed）がある DecisionId は、別の配送が送った・送ったかもしれない——
    // ここで見送りを発行すると台帳が生きている注文の在庫の押さえを解く（「確実に未発注」と「送ったか不明」を混ぜない）。
    private OrderDispatchResult ForgoneIfRecorded(
        OrderApproved approved, OrderDispatchForgoneReason reason, ForgoneRecordOutcome recorded,
        PositionReconciliationDrift? drift = null)
    {
        switch (recorded)
        {
            case ForgoneRecordOutcome.Recorded:
            case ForgoneRecordOutcome.AlreadyForgone:
                return Forgone(approved, reason, drift);

            case ForgoneRecordOutcome.HeldByReservation:
                // 並行した配送が発注に着手している（送ったか不明）。従来の予約競合と同じ扱いで再発注も見送りもしない
                // （再試行のあいだに相手が確定すれば相 1 が既存結果を再発行する）。
                _logger.LogWarning(
                    "見送りを発行しません: 同じ承認の別の配送が発注に着手しています（予約あり・送ったか不明）。"
                    + "DecisionId={DecisionId} 銘柄={Symbol} この配送での見送り理由={Reason}",
                    approved.DecisionId, approved.Intent.Symbol, reason);
                throw new OrderDispatchReservationConflictException(approved.DecisionId);

            default:
                // AlreadyCompleted（発注結果を確定済み＝発注済み）と未定義値。**見送りを主張しない**側へ倒す。
                throw new InvalidOperationException(
                    $"DecisionId={approved.DecisionId} は発注予約表で確定済み（または未知の状態 {recorded}）のため、"
                    + $"見送り（理由 {reason}）を主張しません。発注済みの注文を見送りと記録すると在庫の押さえが解けます。");
        }
    }

    // 🔴 FR-10, ADR-0040 決定1（S1）, #820 の 8 巡目監査, IADR-0344 追記(8) 決定4:
    // 同一銘柄・同方向に**帰属不明の建玉**（純額 − Active な保護記録の主張合計 > 0）があるか。
    // **確かめられない場合（建玉照会の能力が無い・照会不能）も「ある」側へ倒す**（fail-closed。
    // 「不明」を「無い」と取り違えると、他人の建玉を S1 の損切りラインで売る不可逆な事故になる）。
    private async Task<bool> HasUnattributedPositionAsync(OrderApproved approved, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        if (_positions is null)
        {
            _logger.LogError(
                "損切りの実行機構 S1 は建玉照会のできる発注先でしか武装できません（帰属不明の建玉を判別できないため）。"
                    + "発注しません（DecisionId={DecisionId}）。",
                approved.DecisionId);
            return true;
        }

        // 🔴 #820 の 10 巡目監査, IADR-0344 追記(9) 決定2: **主張を数える前にエントリーの約定を確定する。**
        // 終端になったエントリーの約定数量は、ガードが巡回するまで帳簿（RemainingProtected）へ書かれない。
        // 確定を待たずに数えると、直前に約定したばかりの自分の建玉が「帰属不明」に見え、同一銘柄・同方向への
        // 2 本目が見送られる（追記(8) の残る制約）。確定は建玉照会を要さない突き合わせである。
        //
        // 🔴 **#820 の 12 巡目監査, IADR-0344 追記(11) 決定1: 建玉照会の「前と後」の両方で主張を読み、小さい方を採る。**
        // 建玉照会は OpenD への RPC であり、その待ちのあいだに主張（claimed）は**両方向へ動く**。
        //   - **増える側**: OrderFillPollingService が先行エントリーの記録を終端化して確定が進む（11 巡目 PROBE4）。
        //   - **減る側**: SoftwareStopExecutor の決済（RemainingProtected→0 かつ Completed）・ガードによる外部要因の確定・
        //     PendingExternalReduction の計上（12 巡目 PROBE-A / PROBE-A3）。
        // 🔴 **どちらか一方の時点だけを採ると、逆側の窓で帰属不明が「過少」に読まれて武装する。**
        // 11 巡目は「確定 → 照会」に固定して増える側だけを塞ぎ、**減る側の鏡像を新設してしまった**
        // （追記(10) 決定 1 の「取り違えは必ず安全側へ倒れる」は偽であり、追記(11) で撤回した）。
        // **min(前, 後) なら、どちらへ動いても帰属不明を「過大」に読む側へ倒れる**——読みが 1 回増えるだけで
        // OpenD への往復は増えない（確定はローカルな突き合わせ、2 回目は保護記録の読み直しだけ）。
        var stops = ProtectiveStopNetting.ConfirmEntryFills(
            protectiveStops!.FindActive(ArmingScanLimit), protectiveStops, store, clock.UtcNow);
        var claimedBefore = ClaimedFor(intent, stops);

        var snapshot = await _positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            _logger.LogError(
                "建玉を照会できないため S1 を武装しません（帰属不明の建玉が無いことを確かめられない）。"
                    + "DecisionId={DecisionId} 銘柄={Symbol}",
                approved.DecisionId, intent.Symbol);
            return true;
        }

        // 照会の**後**の主張を読み直す（確定は上で済んでおり永続化されているため、ここでは読むだけでよい）。
        var claimedAfter = ClaimedFor(intent, protectiveStops.FindActive(ArmingScanLimit));

        var net = ProtectiveStopNetting.DirectionalNet(intent.Symbol, intent.Market, intent.Side, snapshot);
        var claimed = Math.Min(claimedBefore, claimedAfter);
        var unattributed = net - claimed;
        if (unattributed <= 0)
            return false;

        _logger.LogError(
            "同一銘柄・同方向に帰属不明の建玉が {Unattributed} 株あるため S1 を武装せず見送ります"
                + "（純額 {Net} 株・保護記録の主張 {Claimed} 株＝照会の前 {Before} 株と後 {After} 株の小さい方）。"
                + "その建玉を S1 の損切りラインで決済しないための前提条件です"
                + "（先に手仕舞ってから切り替えてください）。DecisionId={DecisionId} 銘柄={Symbol}",
            unattributed, net, claimed, claimedBefore, claimedAfter, approved.DecisionId, intent.Symbol);
        return true;
    }

    // 🔴 #820 の 10 巡目監査, IADR-0344 追記(9) 決定1: **帳簿の主張ではなく「その巡回で実際に動かせる株数」で数える。**
    // 帳簿の主張（ProtectedQuantity）で数えると、未確定の観測を抱えた幽霊行——実際には 1 株も動かせない行——が
    // 他人の建玉を「帰属済み」に見せ、帰属不明が 0 と読まれて新しい S1 が武装される（監査の P6(1)・実測 SOLD=20）。
    // 実効数量なら幽霊行は 0 株しか主張せず、帰属不明が正しく見えて**安全側（見送り）へ倒れる**。
    private static int ClaimedFor(OrderIntent intent, IEnumerable<ProtectiveStopOrder> stops) =>
        stops
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == intent.Symbol && s.Market == intent.Market && s.EntrySide == intent.Side)
            .Sum(s => s.EffectiveProtectedQuantity);

    // #820, IADR-0344 決定3: 建玉が生じなかった S1 のエントリー（見送り・終端失敗）の記録を完了にする。
    private void CompleteSoftwareStopWithoutPosition(OrderApproved approved, StopLossMethodDisposition disposition)
    {
        if (disposition != StopLossMethodDisposition.SoftwareStop || protectiveStops?.Find(approved.DecisionId) is not { } stop)
            return;

        protectiveStops.Save(stop with { State = ProtectiveStopState.Completed, UpdatedAt = clock.UtcNow });
    }

    private OrderDispatchResult Forgone(
        OrderApproved approved, OrderDispatchForgoneReason reason, PositionReconciliationDrift? drift = null) =>
        OrderDispatchResult.FromForgone(
            new OrderDispatchForgone(approved.DecisionId, approved.Intent, reason, clock.UtcNow), drift);

    // #864, IADR-0355 決定5: 乖離は**既存の検知（IADR-0118）と同じイベント**で人へ知らせる（新しい経路を作らない）。
    // 観測時刻は照会した今である（発注執行は台帳を持たないため、台帳側の数量はこの決済が消そうとした数量を載せる）。
    private PositionReconciliationDrift DriftOf(OrderIntent intent, int brokerNetQuantity, int closableQuantity)
    {
        var now = clock.UtcNow;
        return new PositionReconciliationDrift(
            [BrokerHeldPositionGate.DriftOf(intent, brokerNetQuantity, closableQuantity)], now, now);
    }

    // FR-10, UC-02, #331, IADR-0210 決定1/3: 保護逆指値の同時発注と、未受理時の建玉解消の全分岐。
    // FR-10, #821, IADR-0347: useAlternative（S3）のときだけ代替注文種別で発注し、試行の記録を返す。
    // **未受理・受理の後段の扱いは分岐しない**（S0 と同じ 1 本の経路）。
    private async Task<(ProtectiveStopPlaced? StopPlaced, ProtectiveStopCoverageLost? CoverageLost,
        AlternativeProtectiveStopAttempted? StopAttempted)>
        PlaceProtectiveStopAsync(
            OrderApproved approved, BrokerOrder entryOrder, bool useAlternative, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var protective = (IProtectiveOrderBroker)broker; // 事前検証済み（未実装なら見送りで到達しない）
        var triggerPrice = intent.StopLossPrice!.Value;   // 事前検証済み（null/非正なら見送りで到達しない）
        const int attempt = 1;

        var stopDecisionId = ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt);
        var closeIntent = BuildCloseIntent(intent, intent.Quantity, triggerPrice);

        BrokerOrder? stopOrder = null;
        AlternativeProtectiveStopAttempted? attempted = null;
        try
        {
            if (useAlternative)
            {
                // 事前検証済み（能力が無ければ見送りで到達しない）。
                var alternative = (IAlternativeProtectiveOrderBroker)broker;
                var placement = await alternative
                    .PlaceAlternativeStopOrderAsync(
                        closeIntent, triggerPrice, intent.Price, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
                stopOrder = placement.Order;
                attempted = Attempted(
                    approved, stopDecisionId, placement.OrderType, placement.Order.Status,
                    placement.Order.OrderId, placement.RejectReasonCode, placement.RejectReasonMessage);
            }
            else
            {
                stopOrder = await protective
                    .PlaceStopOrderAsync(closeIntent, triggerPrice, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 逆指値の発注失敗（接続断含む）＝未受理と同じ分岐（建玉を持たない）。原因は解消側の結果に現れる。
            // #848, IADR-0117（改定 7）の走査: 「届いたか不明」（BrokerDispatchIndeterminateException）も
            // ここへ落ちる。単発であり撃ち直しはしない。分岐は改定 6 の前後で同一（前は偽 ID の Rejected が
            // 返って同じ分岐へ落ちていた）。逆指値が生きていた場合に孤立する件は #853 で扱う。
            stopOrder = null;
            if (useAlternative)
            {
                // #821, IADR-0347: 例外で落ちても「何の種別で試したか」は残す（種別は発注前に知れる）。
                attempted = Attempted(
                    approved, stopDecisionId, ((IAlternativeProtectiveOrderBroker)broker).AlternativeProtectiveOrderType,
                    OrderStatus.Rejected, brokerOrderId: null, rejectReasonCode: null, rejectReasonMessage: ex.Message);
            }
        }

        var now = clock.UtcNow;

        if (stopOrder is not null && stopOrder.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
        {
            // 受理: 逆指値レグを ExecutionRecord として保存し、既存の約定追跡ポーリング（IADR-0113）に載せる。
            // 逆指値がブローカー側で約定（＝損切り成立）すると OrderExecuted が台帳の建玉を減らす（IADR-0210 決定2）。
            store.Save(new ExecutionRecord(
                stopDecisionId, stopOrder.OrderId, intent.Symbol, intent.Market, closeIntent.Side,
                intent.ProductType, PositionEffect.Close, intent.Quantity, triggerPrice,
                stopOrder.FilledQuantity, stopOrder.AveragePrice, stopOrder.Status,
                SlippageRatio: 0m, now));

            protectiveStops?.Save(new ProtectiveStopOrder(
                approved.DecisionId, stopDecisionId, stopOrder.OrderId, intent.Symbol, intent.Market,
                intent.Side, intent.ProductType, intent.Mode, intent.Quantity, triggerPrice,
                intent.FxRateToBase, attempt, ProtectiveStopState.Active, now, now));

            return (new ProtectiveStopPlaced(
                    approved.DecisionId, stopDecisionId, stopOrder.OrderId, closeIntent, triggerPrice, attempt, now),
                null, attempted);
        }

        // 未受理: 逆指値なしの建玉を持たない（業務フロー 02 の表）。
        var coverageLost = await ResolveUnprotectedEntryAsync(approved, entryOrder, cancellationToken)
            .ConfigureAwait(false);
        return (null, coverageLost, attempted);
    }

    // FR-10, FR-11, FR-12, #821, IADR-0347: S3 の試行の記録（受理・拒否のどちらでも 1 件）。
    private AlternativeProtectiveStopAttempted Attempted(
        OrderApproved approved,
        Guid stopDecisionId,
        AlternativeProtectiveOrderType orderType,
        OrderStatus status,
        string? brokerOrderId,
        int? rejectReasonCode,
        string? rejectReasonMessage) =>
        new(approved.DecisionId, stopDecisionId, approved.Intent.Symbol, approved.Intent.Market,
            orderType, status, brokerOrderId, rejectReasonCode, rejectReasonMessage,
            approved.StopLossMethod, broker.Provider, clock.UtcNow);

    // 未受理時の建玉解消: 未約定なら取消、約定済みなら成行手仕舞い、いずれも失敗なら None（Critical・人手対応）。
    private async Task<ProtectiveStopCoverageLost> ResolveUnprotectedEntryAsync(
        OrderApproved approved, BrokerOrder entryOrder, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var filled = entryOrder.FilledQuantity;

        if (filled == 0)
        {
            try
            {
                await broker.CancelOrderAsync(entryOrder.OrderId, cancellationToken).ConfigureAwait(false);
                return CoverageLost(approved, ProtectiveStopRemediation.EntryCancelled, intent.Quantity);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消失敗＝その間に約定した可能性。ブローカ状態を照会して約定分を手仕舞いへ回す。
                var snapshot = await TryGetOrderAsync(entryOrder.OrderId, cancellationToken).ConfigureAwait(false);
                filled = snapshot?.FilledQuantity ?? 0;
                if (filled == 0)
                {
                    // 取消も照会もできない: 状態不明のまま自動で注文を重ねない（人手対応・Critical）。
                    return CoverageLost(approved, ProtectiveStopRemediation.None, intent.Quantity);
                }
            }
        }

        return await CloseUnprotectedPositionAsync(approved, filled, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProtectiveStopCoverageLost> CloseUnprotectedPositionAsync(
        OrderApproved approved, int quantity, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var protective = (IProtectiveOrderBroker)broker;
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1);
        // 参照価格は判断時点の価格（intent.Price）。成行手仕舞いの実約定はブローカ側で決まる。
        var closeIntent = BuildCloseIntent(intent, quantity, intent.Price);

        // 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）: 成行手仕舞いもエントリーと同じ
        // 予約 → 発注 → 確定の 3 相（IADR-0057）で送る。「送ったかもしれない」を予約に残し、
        // **届いたか不明を「解消に失敗した（＝未発注）」と取り違えない**。
        if (!reservations.TryReserve(closeDecisionId, clock.UtcNow))
        {
            // 予約済み＝送信中か成否不明。重ねて送らない。
            return IndeterminateClose(approved, quantity, closeDecisionId, closeIntent, cause: null);
        }

        BrokerOrder closeOrder;
        try
        {
            closeOrder = await protective
                .PlaceMarketOrderAsync(closeIntent, closeDecisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            // 接続確立の失敗＝**確実に未発注**。予約を解放してよいのはこの型だけである（IADR-0211 決定 1）。
            reservations.Release(closeDecisionId);
            return CoverageLost(approved, ProtectiveStopRemediation.None, quantity);
        }
        catch (BrokerDispatchIndeterminateException ex)
        {
            // 送信済み・**届いたか不明**。None（解消に失敗）にしない —— None は手仕舞いレグを運ばないため
            // 取引台帳が押さえず、利用者の手仕舞い要求が通って同じ株数に 2 本の決済が並ぶ。
            return IndeterminateClose(approved, quantity, closeDecisionId, closeIntent, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 分類できない失敗は未発注と言い切れない。「届いたか不明」の側へ倒す。
            return IndeterminateClose(approved, quantity, closeDecisionId, closeIntent, ex);
        }

        var now = clock.UtcNow;

        // 手仕舞いレグも ExecutionRecord に載せ、約定追跡・台帳反映を既存経路で行う（IADR-0210 決定3）。
        store.Save(new ExecutionRecord(
            closeDecisionId, closeOrder.OrderId, intent.Symbol, intent.Market, closeIntent.Side,
            intent.ProductType, PositionEffect.Close, quantity, closeIntent.Price,
            closeOrder.FilledQuantity, closeOrder.AveragePrice, closeOrder.Status,
            SlippageCalculator.Compute(closeIntent.Price, closeOrder.AveragePrice, closeIntent.Side), now));
        reservations.MarkCompleted(closeDecisionId, closeOrder.OrderId, now);

        // 🔴 FR-10, FR-11, UC-06, #857, IADR-0369 決定1: **確認できた拒否**を「手仕舞い済み」と扱わない。
        // 未約定残を二度と約定させない終端（Rejected / Cancelled / Expired）が**返った**なら、建玉は残っている。
        // ここで PositionClosed を主張すると、通知が事実と逆になり（「建玉を成行で手仕舞いました」）、
        // 取引台帳には送られてもいない決済の承認行が足されて 30 分ぶんの在庫が押さえられる。
        // 🔴 CloseIntent は運ばない（生きていない成行を台帳に押さえさせない）。CloseDecisionId は相関のために載せる。
        if (OrderStatusLifecycle.AbandonsUnfilledRemainder(closeOrder.Status))
        {
            _logger.LogError(
                "保護逆指値を張れなかった建玉の成行手仕舞いが拒否されました（確認できた拒否・状態 {Status}）。"
                + "**建玉は残っています。**手仕舞い済みとしては扱いません。証券会社の画面で建玉を確認してください: "
                + "EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId} 銘柄={Symbol} 数量={Quantity}",
                closeOrder.Status, approved.DecisionId, closeDecisionId, intent.Symbol, quantity);

            return new ProtectiveStopCoverageLost(
                approved.DecisionId, intent.Symbol, intent.Market,
                ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.CloseRejected,
                quantity, closeDecisionId, CloseIntent: null, now);
        }

        return new ProtectiveStopCoverageLost(
            approved.DecisionId, intent.Symbol, intent.Market,
            ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.PositionClosed,
            quantity, closeDecisionId, closeIntent, now);
    }

    // 🔴 #848, IADR-0117（改定 7）: 成行手仕舞いを送ったが結果を確認できない。予約は Reserved のまま残し
    //（解放も確定もしない）、結果は保存しない（実在しない注文 ID の記録を作らない）。CloseIntent を運ぶので
    // 取引台帳は処理中の決済として押さえる。Critical の通知で人手の確認を求める（無音にしない）。
    private ProtectiveStopCoverageLost IndeterminateClose(
        OrderApproved approved, int quantity, Guid closeDecisionId, OrderIntent closeIntent, Exception? cause)
    {
        _logger.LogError(cause,
            "保護逆指値を張れなかった建玉の成行手仕舞いの結果を確認できませんでした（送信済み・届いたか不明）。"
            + "重ねて発注しません。予約は Reserved のまま据え置きます。証券会社の画面で注文と建玉を確認してください: "
            + "EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId} 銘柄={Symbol} 数量={Quantity}",
            approved.DecisionId, closeDecisionId, approved.Intent.Symbol, quantity);

        return new ProtectiveStopCoverageLost(
            approved.DecisionId, approved.Intent.Symbol, approved.Intent.Market,
            ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.CloseDispatchIndeterminate,
            quantity, closeDecisionId, closeIntent, clock.UtcNow);
    }

    private ProtectiveStopCoverageLost CoverageLost(
        OrderApproved approved, ProtectiveStopRemediation remediation, int quantity) =>
        new(approved.DecisionId, approved.Intent.Symbol, approved.Intent.Market,
            ProtectiveStopLossCause.RejectedAtEntry, remediation, quantity,
            CloseDecisionId: null, CloseIntent: null, clock.UtcNow);

    private async Task<BrokerOrder?> TryGetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        try
        {
            return await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // FR-17, IADR-0107: 決済レグはエントリーの換算レート（FxRateToBase）を必ず引き継ぐ。
    // 落とすと外貨建て決済だけが未換算（レート 1）で台帳へ積まれ、実現損益の基準通貨集計が桁で誤る。
    private static OrderIntent BuildCloseIntent(OrderIntent entry, int quantity, decimal referencePrice) =>
        new(entry.Symbol,
            entry.Market,
            entry.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            entry.ProductType,
            entry.Mode,
            quantity,
            referencePrice,
            PositionEffect.Close,
            StopLossPrice: null,
            entry.FxRateToBase);
}
