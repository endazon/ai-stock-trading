using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Worker.Composable.Steps;

// FR-02, UC-01, IADR-0023: 定時系統の合流点。情報収集の完了（InformationCollected）を購読し、監視銘柄（watchlist）を巡回して
// 市場開場中のもののみ AI 判断を実行する。判断が成立（発注意図あり）した銘柄について TradeDecisionMade を発行する。
// 休場日（市場カレンダー閉場）の銘柄はサイクルを起動しない。
internal sealed class InformationCollectedConsumer(
    AppSvc decisionService,
    IWatchlistProvider watchlist,
    IMarketCalendar calendar,
    IClock clock,
    ILogger<InformationCollectedConsumer> logger) : IConsumer<InformationCollected>
{
    public async Task Consume(ConsumeContext<InformationCollected> context)
    {
        var now = clock.UtcNow;

        foreach (var watched in watchlist.GetWatchlist())
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
                var decision = await decisionService
                    .DecideAsync(DecisionTrigger.Scheduled(watched.Symbol, watched.Market), context.CancellationToken)
                    .ConfigureAwait(false);

                if (decision is null)
                    continue; // 方針なし・Hold・見送り

                logger.LogInformation(
                    "定時判断: DecisionId={DecisionId} {Symbol} {Side} 数量={Quantity}",
                    decision.DecisionId, decision.Intent.Symbol, decision.Intent.Side, decision.Intent.Quantity);
                await context.Publish(decision, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.CancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "定時サイクルの銘柄処理でエラー: {Symbol}。この銘柄をスキップし継続します。", watched.Symbol);
            }
        }
    }
}
