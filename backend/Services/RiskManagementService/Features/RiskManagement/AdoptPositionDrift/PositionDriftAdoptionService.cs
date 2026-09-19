using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement.ClosePosition;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.AdoptPositionDrift;

// FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350: 台帳とブローカーの乖離を、**利用者の承認つきで**台帳へ取り込む。
//
// 乖離の検知（IADR-0118）は是正しない——台帳は約定でしか動かず、観測を権威にしない。その原則は変えない。
// 本サービスは観測の購読経路からは呼ばれず、OwnerOnly の API からだけ呼ばれる。足したのは
// 「システム外の売買で台帳が実態から離れたとき、利用者が確認して観測値へ合わせる」受け皿である。
// これが無いと、実在しない建玉が段階資金と保有建玉数の枠を占有し続け、新規建てが 1 本も出なくなる（#849 の実測）。
//
// 🔴 **目標数量は利用者が入力しない。** 最新の観測から決まる。観測が無い・古い・台帳に対して古いときは拒否する。
// 🔴 **実現損益は記録しない**（決定 3）。システム外の売買は約定価格が分からない。取り込み行は数量だけを運び、
//    射影が平均取得単価で在庫を減らす（実現損益 0 を構造的に保証）。現在値からの推定は監査イベントに
//    「推定・未記録」として載せるだけで、台帳のどの数値にも入れない。
// 🔴 **減らす方向に限る。** 台帳に無い建玉（BrokerOnly）・数量の増加・方向の反転は、取得単価も損切りも無い建玉を
//    台帳へ作ることになるため拒否する（システムが守れない建玉を「管理下」に見せない）。
public sealed class PositionDriftAdoptionService(
    IPortfolioLedgerStore ledger,
    IBrokerPositionObservationStore observations,
    PositionDriftTracker driftTracker,
    ICurrentPriceSource priceSource,
    IClock clock,
    TimeSpan? maxObservationAge = null,
    TimeSpan? inFlightWindow = null)
{
    /// <summary>
    /// 取り込みに使える観測の最大経過時間。**発注執行の建玉照会が許す最大間隔（60 分）**と同値にする
    /// （PositionReconciliationOptions のクランプ上限。IADR-0153 決定 3 と同じ決め方＝健全な巡回は必ず更新できる）。
    /// 構成キーは持たせない（運用で触る値ではなく、広げれば古い観測へ台帳を合わせる窓が広がるだけのため）。
    /// </summary>
    public static readonly TimeSpan DefaultMaxObservationAge = TimeSpan.FromMinutes(60);

    private readonly TimeSpan _maxObservationAge = maxObservationAge ?? DefaultMaxObservationAge;

    // 「処理中の決済」の遡り窓は手仕舞い（PositionCloseService）と同じ値を使う（同じ事実を同じ窓で数える）。
    private readonly TimeSpan _inFlightWindow = inFlightWindow ?? PositionCloseService.DefaultInFlightWindow;

    public PositionDriftAdoptionOutcome Adopt(PositionDriftAdoptionCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (string.IsNullOrWhiteSpace(command.Reason))
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.ReasonRequired);

        var now = clock.UtcNow;

        // ---- 観測: 無い（不明）・古い → 拒否（fail-safe） ----
        if (observations.GetLatest() is not { } observation)
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.ObservationUnavailable);
        if (now - observation.ObservedAt > _maxObservationAge)
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.ObservationStale);

        // ---- 乖離: 検知と**同じ純関数**で、いまの台帳と最新の観測を突き合わせる ----
        var fills = ledger.GetFills();
        var ledgerPositions = PortfolioProjection.ProjectOpenPositions(fills);
        var drift = PositionDriftDetector.Detect(ledgerPositions, observation.Positions)
            .FirstOrDefault(d => d.Symbol == command.Symbol && d.Market == command.Market);
        if (drift is null)
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.NoDrift);

        // ---- 減らす方向に限る（同符号のまま絶対値が小さくなる、または 0 になる） ----
        var before = drift.LedgerQuantity;
        var target = drift.BrokerQuantity;
        var reduces = before != 0
            && (target == 0 || Math.Sign(target) == Math.Sign(before))
            && Math.Abs(target) < Math.Abs(before);
        if (!reduces)
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.UnsupportedDirection);

        // （報告済みかの判定より先に見る——台帳が動けば乖離の内容も変わり「未報告」に見えるが、利用者に要るのは
        //   「次の観測を待つ」という案内である。）
        // ---- 観測より後に台帳が動いていないこと（観測が台帳に対して古くないこと） ----
        if (fills.Any(f => f.Symbol == command.Symbol && f.Market == command.Market
                        && f.ExecutedAt > observation.ObservedAt))
        {
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.LedgerMovedAfterObservation);
        }

        // ---- 報告済みの乖離に限る（連続観測条件を満たし、利用者へ通知が出たもの） ----
        if (!driftTracker.IsReported(drift))
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.DriftNotReported);

        // ---- 処理中の決済が無いこと（約定が後から届くと二重に減る） ----
        if (ledger.GetInFlightCloseQuantity(command.Symbol, command.Market, now - _inFlightWindow) > 0)
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.CloseInFlight);

        var position = ledgerPositions.First(p => p.Symbol == command.Symbol && p.Market == command.Market);
        var quantity = Math.Abs(before) - Math.Abs(target);
        var adoption = new LedgerDriftAdoption(
            Guid.NewGuid(),
            command.Symbol,
            command.Market,
            // 減少の方向＝建玉方向の反対（ロングの減少は Sell・ショートの減少は Buy）。
            position.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            quantity,
            position.AverageEntryPrice,
            position.FxRateToBase,
            before,
            target,
            observation.ObservedAt,
            actor,
            command.Reason,
            now);

        // 並行の二重投入は冪等キーで 1 件に絞る。負けた側は「既に取り込み済み」＝乖離なしとして返す。
        if (!ledger.AppendDriftAdoption(adoption))
            return PositionDriftAdoptionOutcome.Reject(PositionDriftAdoptionRejection.NoDrift);

        var (referencePrice, estimatedPnl) = EstimateForReference(position, quantity);

        return new PositionDriftAdoptionOutcome(
            PositionDriftAdoptionRejection.None,
            new PositionDriftAdopted(
                adoption.Id,
                adoption.Symbol,
                adoption.Market,
                LedgerQuantityBefore: before,
                LedgerQuantityAfter: target,
                BrokerQuantity: target,
                observation.ObservedAt,
                adoption.CostBasisPrice,
                // 決定 3: 実現損益は台帳へ記録していない（受け手が「損益 0 の決済」と読まないよう明示する）。
                RealizedPnlRecorded: false,
                referencePrice,
                estimatedPnl,
                actor,
                command.Reason,
                now));
    }

    // 🔴 **推定であり、台帳へは記録しない。** 取り込み時点の現在値で、減らした数量を評価した参考値
    // （基準通貨 USD。含み損益と同じ式・IADR-0107）。実際の決済価格ではない。現在値が取れなければ両方 null
    // ——0 で埋めると「損益 0 と推定した」に読める。
    private (decimal? ReferencePrice, decimal? EstimatedPnlInBase) EstimateForReference(OpenPosition position, int quantity)
    {
        var prices = priceSource.GetCurrentPrices([position]);
        if (!prices.TryGetValue((position.Symbol, position.Market), out var price) || price <= 0m)
            return (null, null);

        var perUnit = position.Side == TradeSide.Buy
            ? price - position.AverageEntryPrice
            : position.AverageEntryPrice - price;
        return (price, perUnit * quantity * position.FxRateToBase);
    }
}
