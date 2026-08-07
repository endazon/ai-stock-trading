using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-05, FR-10, FR-11, #292, IADR-0118: 発注執行が観測したブローカ建玉を購読し、取引台帳の射影と突き合わせる。
//
// 乖離は検知・記録・通知のみで**是正しない**（自動で建玉を合わせにいく発注経路は作らない・IADR-0118）。
// 一過性の未反映（発注後〜約定が台帳へ届くまで）で鳴らないよう、報告可否は PositionDriftTracker が
// 連続観測条件とシグネチャ dedup で決める。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<BrokerPositionsObserved> から Wolverine のハンドラへ移行した。
public sealed class BrokerPositionsObservedHandler(
    IPortfolioLedgerStore ledger,
    PositionDriftTracker tracker,
    IClock clock,
    ILogger<BrokerPositionsObservedHandler> logger,
    // #419, IADR-0159: **必須依存とする。** Wolverine のハンドラは codegen で組み立てられ、省略可能引数を
    // 解決できない（実行時に UnResolvableVariableException で落ちる）。加えて、既定で null に倒せると
    // 「配線を忘れても静かに推定されない」状態が作れてしまう——推定の不在は「強制買戻しが起きていない」
    // ことを意味しないため、黙って落ちる経路を作らない。
    BuyInInferenceService buyInInference)
{
    public async Task Handle(BrokerPositionsObserved message, IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        // FR-10, FR-11, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159: 同じ観測から強制買戻しを事後推定する。
        // **イベント検知の供給元が無い**ため、建玉の消失を自らの決済指示（約定履歴・処理中の決済承認）と突合して
        // 推定する。**照会不能のとき観測自体が発行されない**（発注執行側の fail-safe）ので、ここへ来た観測は
        // 「照会できた結果」である——空列を「全建玉が消失した」と読む経路は存在しない。
        await InferBuyInsAsync(message, bus).ConfigureAwait(false);

        var ledgerPositions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());
        var drifts = PositionDriftDetector.Detect(ledgerPositions, message.Positions);

        if (!tracker.ShouldReport(drifts))
        {
            logger.LogDebug(
                "建玉突合: 乖離 {Count} 件（報告条件を満たさないため発行しません）。台帳 {Ledger} 件 / ブローカ {Broker} 件。",
                drifts.Count, ledgerPositions.Count, message.Positions.Count);
            return;
        }

        logger.LogWarning(
            "建玉の乖離を検知しました（{Count} 件）: {Summary}。是正は行いません（利用者の判断に委ねます）。",
            drifts.Count,
            string.Join(", ", drifts.Select(d => $"{d.Symbol}/{d.Market} 台帳{d.LedgerQuantity}≠ブローカ{d.BrokerQuantity}")));

        await bus.PublishAsync(new PositionReconciliationDrift(drifts, message.ObservedAt, clock.UtcNow))
            .ConfigureAwait(false);
    }

    // FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
    // 推定は**乖離報告の連続観測条件（PositionDriftTracker）に従わない**。乖離の通知は「一過性の未反映で鳴らない」
    // ことを重んじるが、推定は**処理中の決済承認を差し引いて未反映そのものを説明に使う**ため、待つ必要が無い。
    // 推定が遅れることは決定4 の目的（同じ銘柄で繰り返さない）を損なう側の誤りである。
    private async Task InferBuyInsAsync(BrokerPositionsObserved message, IMessageBus bus)
    {
        var inferred = buyInInference.Observe(message.Positions, message.ObservedAt);
        foreach (var e in inferred)
        {
            logger.LogWarning(
                "強制買戻しと**推定**しました（確定した事実ではありません）: {Symbol}/{Market} 台帳 {Ledger} 株に対し"
                    + "ブローカ {Broker} 株・処理中の決済 {InFlight} 株。説明できない消失 {Newly} 株を推定し、"
                    + "{BanUntil} まで当該銘柄の新規空売りを禁止します。",
                e.Symbol, e.Market, e.LedgerShortQuantity, e.BrokerShortQuantity, e.InFlightCloseQuantity,
                e.NewlyInferredQuantity, e.BanUntil);

            await bus.PublishAsync(e).ConfigureAwait(false);
        }
    }
}
