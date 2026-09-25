using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;

// 🔴 FR-10, FR-05, FR-11, UC-02, UC-06, #858, IADR-0370, IADR-0350 決定5:
// **利用者が承認した乖離の取り込み（PositionDriftAdopted）に、発注執行側の保護記録とブローカー側の保護注文を追随させる。**
//
// 取り込みはリスク管理側の取引台帳だけを観測値へ合わせる。追随しないと、
//   - 保護記録が Active のまま残り、ガードが実在しない建玉の保護レグを巡回し続ける
//   - 🔴 **建玉が無いのにブローカーへ売りの逆指値が残り、発火すると意図しないショートが建つ**（#853 と同じ帰結）
//
// 規律:
//   - 🔴 **建玉を売る操作は 1 つも行わない。** 行うのは保護注文の取消と帳簿の更新だけである。
//   - 🔴 **目標は絶対値**（取り込み後の数量）であって差分ではない。再送しても二重に取り消さない（IADR-0350 決定2 と同じ理由）。
//   - 🔴 **割り当ての規則は巡回と同じもの**（ProtectiveStopNetting.AllocateReduction）を使う。同じ問いに 2 つの答えを持たない。
//   - 🔴 **取り消せたと確認できたときだけ終端化する**（IADR-0357）。確認できなければ Active のまま残し Critical を出す
//     ——「取り消せた」と誤って主張すると、生きた逆指値が誰の巡回からも外れる。
//   - 🔴 **取り消す前に新しい建玉照会で裏を取る**（取り込みが許す観測は最大 60 分古い。IADR-0350 決定1）。
//     照会できるなら「その純額と取り込みの目標の**大きい方**」を目標に採り、**保護を消しすぎない側**へ倒す。
//   - 🔴 **照会が不明（null）・例外なら、帳簿もブローカーも 1 つも変えない**（IADR-0370 2026-09-24 追記 / PR #918 監査）。
//     Critical をログし ProtectiveStopDriftPositionsUnknownException を投げて、メッセージングの再試行に照会をやり直させる。
//     ——保護の取消は「建玉が消えたと確かめられたとき」にしか許さない。最大 60 分古い観測は確かめたことにならない。
//     空の一覧（照会は成功・0 株）は「確かめた」であり、信じる。建玉照会を持たない構成（null 注入）は従来どおり観測に従う。
//
// 🔴 #942, IADR-0395: 打ち切りが**最後の配送**（再試行を使い切り、この失敗で _error へ送られる）なら、業務メトリクス
// ast.order.drift_adoption_followup_abandoned を理由つきで 1 増やす（PrometheusRule AstDriftAdoptionFollowUpAbandoned が引く）。
// それまでは Critical ログと _error キューの滞留にしか現れず、人が見ていないあいだは無音だった。
// 途中の配送では数えない（再試行で回復し得る。数えると一過性の照会失敗 1 回で鳴るルールになる）。
public sealed class ProtectiveStopDriftAdopter(
    IProtectiveStopOrderStore stops,
    OrderAmendmentService amendments,
    IClock clock,
    ILogger<ProtectiveStopDriftAdopter>? logger = null,
    IBrokerPositionSource? positions = null,
    BusinessMetrics? metrics = null)
{
    // 群の走査上限。保有建玉数上限（既定 3）に対して十分大きい（ProtectiveStopNetting と同じ値）。
    private const int ScanLimit = 500;

    private readonly ILogger _logger = logger ?? NullLogger<ProtectiveStopDriftAdopter>.Instance;

    // 🔴 #942, IADR-0395: 省略可能だが、本番（Program.cs）は必ず DI のシングルトンを渡す。渡さないと打ち切りを数えず、
    // アラートは**エラーを出さずに永久に鳴らない**。T-10-785 が Program.cs そのものを組んで保持を確かめる（フィールド名を変えない）。
    private readonly BusinessMetrics? _metrics = metrics;

    /// <summary>
    /// 取り込み 1 件を保護記録へ反映する。発行すべきイベントを返す（発行は呼び出し側＝ハンドラ）。
    /// 最後の配送かどうかを知らない呼び出し（試験・手動の再実行）用であり、打ち切っても計上しない。
    /// </summary>
    public Task<ProtectiveStopDriftAdoptionResult> ApplyAsync(
        PositionDriftAdopted adopted, CancellationToken cancellationToken = default) =>
        ApplyAsync(adopted, finalDeliveryAttempt: false, cancellationToken);

    /// <summary>
    /// 取り込み 1 件を保護記録へ反映する。発行すべきイベントを返す（発行は呼び出し側＝ハンドラ）。
    /// </summary>
    /// <param name="adopted">取り込み。</param>
    /// <param name="finalDeliveryAttempt">
    /// #942, IADR-0395: この配送が最後か（ここで投げるとメッセージが <c>_error</c> へ送られるか）。ハンドラが
    /// Wolverine の配送回数から決める。<c>true</c> のときだけ、建玉照会の不明・失敗による打ち切りを業務メトリクスへ数える。
    /// </param>
    /// <param name="cancellationToken">停止要求。</param>
    public async Task<ProtectiveStopDriftAdoptionResult> ApplyAsync(
        PositionDriftAdopted adopted, bool finalDeliveryAttempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adopted);

        var events = new List<object>();

        // 取り込みは「同符号のまま絶対値が小さくなる」操作だけを通す（リスク管理側で検証済み・IADR-0350 決定2）。
        // ここでも確かめる——増える方向・方向の反転で保護を削らない（受け取った値を無条件に信じない）。
        var before = adopted.LedgerQuantityBefore;
        var after = adopted.LedgerQuantityAfter;
        if (before == 0 || Math.Abs(after) >= Math.Abs(before)
            || (after != 0 && Math.Sign(after) != Math.Sign(before)))
        {
            _logger.LogWarning(
                "乖離の取り込みが減少ではないため、保護記録を追随させません: 銘柄={Symbol}/{Market} 取り込み前={Before} 取り込み後={After}",
                adopted.Symbol, adopted.Market, before, after);
            return new ProtectiveStopDriftAdoptionResult(0, 0, 0, events);
        }

        var entrySide = before > 0 ? TradeSide.Buy : TradeSide.Sell;
        var group = stops.FindActive(ScanLimit)
            .Where(s => s.State == ProtectiveStopState.Active
                && s.Symbol == adopted.Symbol && s.Market == adopted.Market && s.EntrySide == entrySide)
            .ToList();
        if (group.Count == 0)
            return new ProtectiveStopDriftAdoptionResult(0, 0, 0, events);

        var claimed = group.Sum(s => s.ProtectedQuantity);
        var adoptedTarget = Math.Abs(after);
        if (claimed <= adoptedTarget)
        {
            // 目標は照会の純額と取り込みの目標の大きい方なので、主張が取り込みの目標以下なら照会の答えに依らず
            // 減らすものは無い。照会せずに終える（照会が落ちているときに無用な再試行・_error を作らない）。
            _logger.LogInformation(
                "乖離の取り込みで減らす保護はありません（主張 {Claimed} ≦ 目標 {Target}）: 銘柄={Symbol}/{Market}",
                claimed, adoptedTarget, adopted.Symbol, adopted.Market);
            return new ProtectiveStopDriftAdoptionResult(group.Count, 0, 0, events);
        }

        var target = await ResolveTargetAsync(adopted, entrySide, adoptedTarget, finalDeliveryAttempt, cancellationToken)
            .ConfigureAwait(false);
        var budget = claimed - target;
        if (budget <= 0)
        {
            // 主張が既に目標以下（再送・他の経路が先に収束させた・建玉が戻っている）。触らない。
            _logger.LogInformation(
                "乖離の取り込みで減らす保護はありません（主張 {Claimed} ≦ 目標 {Target}）: 銘柄={Symbol}/{Market}",
                claimed, target, adopted.Symbol, adopted.Market);
            return new ProtectiveStopDriftAdoptionResult(group.Count, 0, 0, events);
        }

        var takes = ProtectiveStopNetting.AllocateReduction(group, budget);
        var reduced = 0;
        var unconfirmed = 0;

        foreach (var row in group
            .Where(s => takes.GetValueOrDefault(s.EntryDecisionId) > 0)
            .OrderBy(s => s.IsSoftwareStop ? 0 : 1)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.EntryDecisionId))
        {
            var take = takes[row.EntryDecisionId];
            if (row.IsSoftwareStop)
            {
                // 帳簿だけの行（S1）: ブローカーに取り消す注文が無い。主張をその場で減らす（取り込みは確定である）。
                // 🔴 #833 項目3, IADR-0396: 並行更新と衝突したら減らさない（下の ReduceBooks）。減らしていない主張を「減らした」と言わない。
                if (ReduceBooks(row, take, adopted))
                {
                    events.Add(Reduced(row, take));
                    reduced++;
                }
            }
            else if (await TryCancelAsync(row, adopted, cancellationToken).ConfigureAwait(false) is { } cancelled)
            {
                // 実注文を持つ行（S0）: **全部か 0 か**でしか減らない（割り当ての規則）。ブローカーの逆指値を取り消す。
                // 取消は起きた事実なので必ず残す。帳簿の減算は楽観並行で、衝突したら次の巡回（逆指値の失効検知）に委ねる。
                events.Add(cancelled);
                if (ReduceBooks(row, take, adopted))
                {
                    events.Add(Reduced(row, take));
                    reduced++;
                }
            }
            else
            {
                unconfirmed++;
                events.Add(new SoftwareStopExecuted(
                    row.EntryDecisionId, row.Symbol, row.Market, SoftwareStopOutcome.StopCancelUnconfirmed,
                    row.ProtectedQuantity, row.TriggerPrice, row.TriggerPrice, row.Attempt,
                    CloseDecisionId: null, CloseOrderId: row.StopOrderId, CloseIntent: null, clock.UtcNow));
            }

            // 🔴 停止要求は**行の処理を終えてから**見る（PR #918 の自動レビューの指摘）。
            // 行の先頭で投げると、直前の行で**既に保存した帳簿・既にブローカーへ送った取消**に対応するイベントが
            // events ごと握り潰され（発行は呼び出し側）、記録の状態と監査・通知が食い違う無音の穴になる。
            // 打ち切った残りの行は Active のままであり、次の観測・ガードの巡回・次の取り込みが引き続き見る。
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "停止要求のため乖離の取り込みの追随を打ち切ります（ここまでの結果は発行します）: "
                    + "銘柄={Symbol}/{Market} 減らした={Reduced} 取消を確認できない={Unconfirmed}",
                    adopted.Symbol, adopted.Market, reduced, unconfirmed);
                break;
            }
        }

        return new ProtectiveStopDriftAdoptionResult(group.Count, reduced, unconfirmed, events);
    }

    // 🔴 IADR-0370 決定3: 取り込みの観測は最大 60 分古い。新しい建玉照会が使えるなら、その純額と取り込みの目標の
    // **大きい方**を採る（保護を消しすぎない側へ倒す）。
    // 🔴 IADR-0370（2026-09-24 追記 / PR #918 監査）: 照会が不明（null）・例外なら**何も変えずに投げる**
    // （帳簿にもブローカーにも触る前にここを通る）。建玉照会を持たない構成（positions が null）だけは観測に従う。
    private async Task<int> ResolveTargetAsync(
        PositionDriftAdopted adopted, TradeSide entrySide, int adoptedTarget, bool finalDeliveryAttempt,
        CancellationToken cancellationToken)
    {
        if (positions is null)
            return adoptedTarget;

        IReadOnlyList<BrokerPositionSnapshot>? snapshot;
        try
        {
            snapshot = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw PositionsUnknown(adopted, ex, finalDeliveryAttempt);
        }

        // 🔴 null（不明）と空の一覧（照会は成功・0 株）を混ぜない。後者は「確かめた」であり、下の純額 0 として追随が進む。
        if (snapshot is null)
            throw PositionsUnknown(adopted, innerException: null, finalDeliveryAttempt);

        var net = ProtectiveStopNetting.DirectionalNet(adopted.Symbol, adopted.Market, entrySide, snapshot);
        return Math.Max(adoptedTarget, net);
    }

    // 🔴 無音にしない: Critical をログしてから投げる。発行（PublishAsync）では知らせない ——
    // Wolverine はハンドラが投げた時点で、その処理中に発行したメッセージを捨てる（Executor の失敗経路の ClearAllAsync）。
    // 再試行を使い切ったメッセージは _error キューに残る（OrderDispatchReservationConflictException と同じ作法）。
    // 🔴 #942, IADR-0395: 最後の配送なら業務メトリクスへ理由つきで 1 件数える（アラートが引く系列）。**発行と違い、
    // 計器への記録はハンドラが投げても捨てられない**（プロセス内の Meter へ即時に積まれる）。
    private ProtectiveStopDriftPositionsUnknownException PositionsUnknown(
        PositionDriftAdopted adopted, Exception? innerException, bool finalDeliveryAttempt)
    {
        if (finalDeliveryAttempt)
        {
            _metrics?.RecordDriftAdoptionFollowUpAbandoned(innerException is null
                ? BusinessMetrics.DriftFollowUpPositionsUnknown
                : BusinessMetrics.DriftFollowUpPositionsQueryFailed);
            _logger.LogCritical(innerException,
                "乖離の取り込みの追随で建玉を照会できませんでした（不明または失敗）。**再試行を使い切ったため、"
                + "このメッセージは _error キューへ送られます。**保護記録もブローカー側の保護注文も変えていません。"
                + "保護逆指値は残ったままです——建玉が本当に無いなら発火で意図しないショートが建ち得るので、"
                + "証券会社の画面で建玉と未約定の逆指値を確認し、照会の回復後に _error から元のキューへ戻してください:"
                + " 取り込み={AdoptionId} 銘柄={Symbol}/{Market} 台帳 {Before}→{After} 依頼者={Actor}",
                adopted.AdoptionId, adopted.Symbol, adopted.Market,
                adopted.LedgerQuantityBefore, adopted.LedgerQuantityAfter, adopted.Actor);
        }
        else
        {
            _logger.LogCritical(innerException,
                "乖離の取り込みの追随で建玉を照会できませんでした（不明または失敗）。**建玉が消えたと確かめられないため、"
                + "保護記録もブローカー側の保護注文も変えません。**再試行で照会をやり直します（使い切ると _error キュー）。"
                + "照会が回復しないあいだ、保護逆指値は残ったままです——建玉が本当に無いなら発火で意図しないショートが建ち得るので、"
                + "証券会社の画面で建玉と未約定の逆指値を確認してください: 取り込み={AdoptionId} 銘柄={Symbol}/{Market}"
                + " 台帳 {Before}→{After} 依頼者={Actor}",
                adopted.AdoptionId, adopted.Symbol, adopted.Market,
                adopted.LedgerQuantityBefore, adopted.LedgerQuantityAfter, adopted.Actor);
        }

        return new ProtectiveStopDriftPositionsUnknownException(adopted.AdoptionId, adopted.Symbol, innerException);
    }

    // 主張（残保護数量）を減らし、0 になった行を終端化する。
    // 🔴 IADR-0370 決定6: 未確定の観測は捨てる——取り込みで確定した減少と**同じ外部要因**であり、
    // 残すと次の巡回が同じ減少をもう一度数えて削る。
    //
    // 🔴 #833 項目3, IADR-0396: 群は建玉照会・逆指値の取消を await で跨いで持っていた写しである。**楽観並行で書く**
    // ——その間に決済が確定して行が完了・試行番号が進んでいたら、古い写しで Active と古い試行番号を書き戻してしまい、
    // 次の決済経路が同じ試行を「記録済み」と読んで帳簿を二度減らし ClosePlaced を二度出す。衝突したら**減らさない**
    // （割り当ては最新の行で計算し直す必要があり、ここで当て直すと減らし過ぎ得る）。減らさない側は保護が残る側であり、
    // 取り込みで消えた建玉は次の巡回の外部要因の観測（2 巡回で確定）が同じ規則で削る。
    private bool ReduceBooks(ProtectiveStopOrder row, int take, PositionDriftAdopted adopted)
    {
        var now = clock.UtcNow;
        var remaining = Math.Max(0, row.ProtectedQuantity - take);
        if (!stops.TrySave(row with
        {
            RemainingProtected = remaining,
            State = remaining == 0 ? ProtectiveStopState.Completed : ProtectiveStopState.Active,
            PendingExternalReduction = 0,
            ExternalReductionObservations = 0,
            ExternalReductionAbsences = 0,
            UpdatedAt = now,
        }))
        {
            _logger.LogWarning(
                "乖離の取り込みの追随で、保護記録が並行に更新されていたため主張を減らしませんでした（古い写しでは上書きしません）。"
                + "次の巡回の観測が同じ規則で扱います: EntryDecisionId={EntryDecisionId} 銘柄={Symbol}/{Market} 減少={Take}"
                + " 取り込み={AdoptionId}",
                row.EntryDecisionId, row.Symbol, row.Market, take, adopted.AdoptionId);
            return false;
        }

        _logger.LogWarning(
            "乖離の取り込みに追随して保護の主張を減らしました（決済は出していません）: EntryDecisionId={EntryDecisionId}"
            + " 銘柄={Symbol}/{Market} 減少={Take} 残り={Remaining} 取り込み={AdoptionId} 依頼者={Actor}",
            row.EntryDecisionId, row.Symbol, row.Market, take, remaining, adopted.AdoptionId, adopted.Actor);
        return true;
    }

    // ブローカーの保護注文を取り消す。**取り消せたと確認できたときだけ** OrderCancelled を返す（IADR-0357）。
    private async Task<OrderCancelled?> TryCancelAsync(
        ProtectiveStopOrder row, PositionDriftAdopted adopted, CancellationToken cancellationToken)
    {
        var reason = $"乖離の取り込みで建玉が消えたため保護逆指値を取消"
            + $"（取り込み {adopted.AdoptionId}・依頼者 {adopted.Actor}・理由 {adopted.Reason}）";
        try
        {
            var outcome = await amendments.CancelAsync(row.StopDecisionId, reason, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Confirmed)
                return outcome.Event;

            _logger.LogError(
                "乖離の取り込みの追随で保護逆指値を取り消せたと確認できませんでした（照会した状態={Observed}）。"
                + "**建玉が無いのに逆指値が生きていると、発火して意図しないショートが建ちます。**"
                + "記録は Active のまま残し、巡回を続けます: EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} 銘柄={Symbol}",
                outcome.ObservedStatus, row.EntryDecisionId, row.StopOrderId, row.Symbol);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "乖離の取り込みの追随で保護逆指値の取消に失敗しました。**建玉が無いのに逆指値が生きている可能性があります。**"
                + "記録は Active のまま残し、巡回を続けます: EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} 銘柄={Symbol}",
                row.EntryDecisionId, row.StopOrderId, row.Symbol);
            return null;
        }
    }

    // #820, IADR-0344 追記(5): 「外部要因で保護対象を減らした（決済は出していない）」の既存イベントを使う
    // ——取り込みは**利用者がシステム外で売買した**という、まさにその外部要因の確定である。
    private SoftwareStopExecuted Reduced(ProtectiveStopOrder row, int take) =>
        new(row.EntryDecisionId, row.Symbol, row.Market, SoftwareStopOutcome.ProtectionReduced,
            take, row.TriggerPrice, row.TriggerPrice, row.Attempt,
            CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, clock.UtcNow);
}

/// <summary>
/// #858, IADR-0370: 取り込み 1 件の追随の結果（走査した群の件数・減らした行・取消を確認できなかった行）と、
/// 発行すべきイベント。
/// </summary>
public sealed record ProtectiveStopDriftAdoptionResult(
    int Scanned,
    int Reduced,
    int CancelUnconfirmed,
    IReadOnlyList<object> Events);
