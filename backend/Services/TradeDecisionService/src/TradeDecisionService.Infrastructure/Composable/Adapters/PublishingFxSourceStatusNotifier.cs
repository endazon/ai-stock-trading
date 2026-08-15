using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, FR-17, FR-09, FR-11, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の状態変化を publish する実装。
// AuditService（監査台帳）と NotificationService（Discord）が購読する。日報は期間で引く（供給ポート）。
//
// singleton として登録する。**状態（フォールバック中か・当日通知済みか）を巡回を跨いで共有する**ため。
// ADR-0013, IADR-0129, #354: Wolverine の IMessageBus は scoped であり singleton へ注入できない。
// singleton の IWolverineRuntime から MessageBus を作って発行する（PublishingDailyPolicyUnconfirmedNotifier と同じ形）。
internal sealed class PublishingFxSourceStatusNotifier(
    IWolverineRuntime runtime,
    IClock clock,
    ILogger<PublishingFxSourceStatusNotifier> logger) : IFxSourceStatusNotifier
{
    private readonly FxSourceStatusTracker _tracker = new();

    public Task ReportSourceUsedAsync(
        string quote, string sourceName, int rank, int totalSources, CancellationToken cancellationToken = default) =>
        PublishIfAny(quote, _tracker.OnSourceUsed(quote, sourceName, rank, totalSources, clock.UtcNow));

    public Task ReportStaleAsync(
        string quote,
        DateTimeOffset asOf,
        TimeSpan age,
        TimeSpan warnThreshold,
        TimeSpan maxAge,
        CancellationToken cancellationToken = default,
        bool entryBlocked = false) =>
        PublishIfAny(quote, _tracker.OnStale(quote, asOf, age, warnThreshold, maxAge, clock.UtcNow, entryBlocked));

    /// <summary>
    /// 🔴 <b>抑止しない。</b> これは「状態」ではなく「取引」であり、
    /// <b>1 件ずつ台帳に残さなければ後から件数も金額も復元できない</b>（IADR-0198 決定3）。
    /// </summary>
    public Task ReportClosedWithStaleRateAsync(
        string symbol,
        Market market,
        string quote,
        int quantity,
        decimal fxRateToBase,
        DateTimeOffset rateAsOf,
        TimeSpan age,
        CancellationToken cancellationToken = default) =>
        PublishIfAny(
            quote,
            new PositionClosedWithStaleFxRate(
                symbol, market, quote, quantity, fxRateToBase, rateAsOf,
                age.TotalDays, clock.UtcNow));

    /// <summary>
    /// 判定器が「発行すべき」と返したときだけ publish する。
    /// <para>
    /// 🔴 <b>発行に失敗したら状態を戻す。</b> 戻さないと「発行済み」として記録が残り、
    /// <b>劣化が続いているのに二度と通知されない</b>——「黙って劣化させない」（ADR-0022 決定2）に反する。
    /// </para>
    /// <para>
    /// <b>例外は握り潰さず伝播させる。</b> 可視化が落ちたことを呼び出し側の判断へ混ぜないよう、
    /// 呼び出し側（レート源）で fire-and-forget にはせず、失敗はログに残したうえで投げ直す。
    /// </para>
    /// </summary>
    private async Task PublishIfAny(string quote, object? evt)
    {
        if (evt is null)
        {
            return;
        }

        try
        {
            await new MessageBus(runtime).PublishAsync(evt).ConfigureAwait(false);
            logger.LogInformation(
                "為替の情報源の状態変化を発行しました（{Event} / {Quote}）。", evt.GetType().Name, quote);
        }
        catch
        {
            _tracker.Rollback(quote, evt);
            throw;
        }
    }
}
