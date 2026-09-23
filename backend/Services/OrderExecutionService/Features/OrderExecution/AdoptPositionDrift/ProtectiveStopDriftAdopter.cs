using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using AiStockTrading.Shared.Contracts.Events;
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
//     照会が不明（null）なら取り込みの観測に従う——何もしないと孤立した逆指値が残る（本 issue が閉じる穴そのもの）。
public sealed class ProtectiveStopDriftAdopter(
    IProtectiveStopOrderStore stops,
    OrderAmendmentService amendments,
    IClock clock,
    ILogger<ProtectiveStopDriftAdopter>? logger = null,
    IBrokerPositionSource? positions = null)
{
    // 群の走査上限。保有建玉数上限（既定 3）に対して十分大きい（ProtectiveStopNetting と同じ値）。
    private const int ScanLimit = 500;

    private readonly ILogger _logger = logger ?? NullLogger<ProtectiveStopDriftAdopter>.Instance;

    /// <summary>
    /// 取り込み 1 件を保護記録へ反映する。発行すべきイベントを返す（発行は呼び出し側＝ハンドラ）。
    /// </summary>
    public async Task<ProtectiveStopDriftAdoptionResult> ApplyAsync(
        PositionDriftAdopted adopted, CancellationToken cancellationToken = default)
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

        var target = await ResolveTargetAsync(adopted, entrySide, Math.Abs(after), cancellationToken)
            .ConfigureAwait(false);
        var claimed = group.Sum(s => s.ProtectedQuantity);
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
                ReduceBooks(row, take, adopted);
                events.Add(Reduced(row, take));
                reduced++;
            }
            else if (await TryCancelAsync(row, adopted, cancellationToken).ConfigureAwait(false) is { } cancelled)
            {
                // 実注文を持つ行（S0）: **全部か 0 か**でしか減らない（割り当ての規則）。ブローカーの逆指値を取り消す。
                ReduceBooks(row, take, adopted);
                events.Add(cancelled);
                events.Add(Reduced(row, take));
                reduced++;
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
    // **大きい方**を採る（保護を消しすぎない側へ倒す）。照会が不明（null）なら取り込みの観測に従う。
    private async Task<int> ResolveTargetAsync(
        PositionDriftAdopted adopted, TradeSide entrySide, int adoptedTarget, CancellationToken cancellationToken)
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
            snapshot = null;
            _logger.LogWarning(ex, "乖離の取り込みの追随で建玉を照会できませんでした（取り込みの観測に従います）。");
        }

        if (snapshot is null)
        {
            _logger.LogWarning(
                "建玉が不明なため、取り込みの観測（最大 60 分前・利用者が承認済み）を目標にします: 銘柄={Symbol}/{Market} 目標={Target}",
                adopted.Symbol, adopted.Market, adoptedTarget);
            return adoptedTarget;
        }

        var net = ProtectiveStopNetting.DirectionalNet(adopted.Symbol, adopted.Market, entrySide, snapshot);
        return Math.Max(adoptedTarget, net);
    }

    // 主張（残保護数量）を減らし、0 になった行を終端化する。
    // 🔴 IADR-0370 決定6: 未確定の観測は捨てる——取り込みで確定した減少と**同じ外部要因**であり、
    // 残すと次の巡回が同じ減少をもう一度数えて削る。
    private void ReduceBooks(ProtectiveStopOrder row, int take, PositionDriftAdopted adopted)
    {
        var now = clock.UtcNow;
        var remaining = Math.Max(0, row.ProtectedQuantity - take);
        stops.Save(row with
        {
            RemainingProtected = remaining,
            State = remaining == 0 ? ProtectiveStopState.Completed : ProtectiveStopState.Active,
            PendingExternalReduction = 0,
            ExternalReductionObservations = 0,
            ExternalReductionAbsences = 0,
            UpdatedAt = now,
        });

        _logger.LogWarning(
            "乖離の取り込みに追随して保護の主張を減らしました（決済は出していません）: EntryDecisionId={EntryDecisionId}"
            + " 銘柄={Symbol}/{Market} 減少={Take} 残り={Remaining} 取り込み={AdoptionId} 依頼者={Actor}",
            row.EntryDecisionId, row.Symbol, row.Market, take, remaining, adopted.AdoptionId, adopted.Actor);
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
