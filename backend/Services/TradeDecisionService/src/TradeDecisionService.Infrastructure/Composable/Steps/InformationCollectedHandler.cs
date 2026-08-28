using System.Diagnostics;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Steps;

// FR-02, UC-01, IADR-0023: 定時系統の合流点。情報収集の完了（InformationCollected）を購読し、監視銘柄（watchlist）を巡回して
// 市場開場中のもののみ AI 判断を実行する。判断が成立（発注意図あり）した銘柄について TradeDecisionMade を発行する。
// 休場日（市場カレンダー閉場）の銘柄はサイクルを起動しない。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<InformationCollected> から Wolverine のハンドラへ移行した。
// ConsumeContext<T> は消え、メッセージ本体・IMessageBus・CancellationToken をメソッド引数で受け取る。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
//
// NFR-07, #287, IADR-0255: 定時系統の判断回数・内訳・レイテンシを銘柄ごとに計上する（銘柄はタグにしない。
// 系列のカーディナリティを業務量に比例させないため。銘柄単位の追跡はログ・トレースが担う）。
public sealed class InformationCollectedHandler(
    AppSvc decisionService,
    IWatchlistProvider watchlist,
    IMarketCalendar calendar,
    IClock clock,
    BusinessMetrics metrics,
    ILogger<InformationCollectedHandler> logger)
{
    public async Task Handle(InformationCollected message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var now = clock.UtcNow;

        // FR-02, IADR-0095: 権威源（市場監視 #10）から当該サイクルの監視銘柄を照会する（実装未接続/失敗時は構成ベースへ倒す）。
        var watchlistSymbols = await watchlist.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);

        foreach (var watched in watchlistSymbols)
        {
            if (!calendar.IsOpen(watched.Market, now))
            {
                // 休場日（週末・祝日）はサイクルを起動しない。
                continue;
            }

            // 1 銘柄の判断/発行失敗でサイクル全体を再配送させない（IADR-0023）。再配送すると発行済み銘柄も再ループされ、
            // TradeDecisionMade.DecisionId は都度新規採番のため下流（発注執行）の DecisionId 冪等をすり抜け重複発注し得る。
            // 失敗銘柄はログに残して次銘柄へ継続し、次回巡回で再評価する（キャンセルは伝播させる）。
            try
            {
                // NFR-07, #287: 判断の所要と結果は「成立したか」に関わらず計上する（見送りも 1 回の判断である）。
                var started = Stopwatch.GetTimestamp();
                var decision = await decisionService
                    .DecideAsync(DecisionTrigger.Scheduled(watched.Symbol, watched.Market), cancellationToken)
                    .ConfigureAwait(false);
                metrics.RecordTradeDecisionDuration(
                    BusinessMetrics.TriggerScheduled, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                metrics.RecordTradeDecision(BusinessMetrics.TriggerScheduled, decision?.Intent.Side);

                if (decision is null)
                    continue; // 方針なし・Hold・見送り

                logger.LogInformation(
                    "定時判断: DecisionId={DecisionId} {Symbol} {Side} 数量={Quantity}",
                    decision.DecisionId, decision.Intent.Symbol, decision.Intent.Side, decision.Intent.Quantity);
                await bus.PublishAsync(decision).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "定時サイクルの銘柄処理でエラー: {Symbol}。この銘柄をスキップし継続します。", watched.Symbol);
            }
        }
    }
}
