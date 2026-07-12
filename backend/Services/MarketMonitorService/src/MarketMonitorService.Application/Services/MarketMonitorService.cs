using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Application.Services;

// FR-03, UC-02, ADR-0003, IADR-0014: 市場監視の 1 巡回オーケストレーション。
// (1) 保有銘柄の損切りライン到達を検知して StopLossTriggered を、(2) 監視銘柄の変動閾値超過（クールダウン外）を
// 検知して PriceMovementDetected を生成する。損切りはクールダウン・変動判定と独立に常に評価する（フェイルセーフ）。
// 価格取得失敗（null）の銘柄はスキップして監視を継続する。発行（MassTransit）は Worker（Slice B）が担う。
public sealed class MarketMonitorService(
    IMonitoredSymbolStore settingsStore,
    IPositionStore positionStore,
    IPriceBaselineStore baselineStore,
    ICooldownStore cooldownStore,
    IMarketDataSource marketData,
    IClock clock)
{
    public async Task<MonitorRoundResult> EvaluateRoundAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetSettings();
        var now = clock.UtcNow;

        var stopLosses = new List<StopLossTriggered>();
        var movements = new List<PriceMovementDetected>();

        // (1) 損切りライン検知（保有銘柄）。変動判定・クールダウンと独立に常に評価する（フェイルセーフ）。
        // 保有ポジションはリスク管理（#63 台帳）を同期照会する（IADR-0030）。照会失敗は空列（＝検知対象なし）。
        var openPositions = await positionStore.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var position in openPositions)
        {
            var quote = await marketData
                .GetLatestQuoteAsync(position.Symbol, position.Market, cancellationToken)
                .ConfigureAwait(false);
            if (quote is null)
            {
                continue; // 取得失敗はスキップ（監視継続）
            }

            if (StopLossEvaluator.IsTriggered(position, quote.Price))
            {
                stopLosses.Add(new StopLossTriggered(
                    Guid.NewGuid(), position.Symbol, position.Market, position.Side,
                    position.Quantity, quote.Price, position.StopLossPrice, now));
            }
        }

        // (2) 変動閾値検知（監視銘柄）。基準値比・閾値超過 かつ クールダウン外で発行する。
        foreach (var monitored in settings.MonitoredSymbols)
        {
            var quote = await marketData
                .GetLatestQuoteAsync(monitored.Symbol, monitored.Market, cancellationToken)
                .ConfigureAwait(false);
            if (quote is null)
            {
                continue;
            }

            var baseline = baselineStore.GetBaseline(monitored.Symbol, monitored.Market);
            if (baseline is null)
            {
                continue; // 基準値未確定（前回判断なし）は変動判定しない
            }

            var movement = PriceMovementEvaluator.Evaluate(quote.Price, baseline.Value, settings.MovementThresholdRatio);
            if (!movement.Exceeded)
            {
                continue;
            }

            if (IsInCooldown(monitored.Symbol, monitored.Market, now, settings.Cooldown))
            {
                continue; // クールダウン中は再トリガーしない
            }

            movements.Add(new PriceMovementDetected(
                Guid.NewGuid(), monitored.Symbol, monitored.Market,
                quote.Price, baseline.Value, movement.ChangeRatio, now));
            cooldownStore.SetLastTriggered(monitored.Symbol, monitored.Market, now);
        }

        return new MonitorRoundResult(stopLosses, movements);
    }

    private bool IsInCooldown(string symbol, Market market, DateTimeOffset now, TimeSpan cooldown)
    {
        var last = cooldownStore.GetLastTriggered(symbol, market);
        return last is not null && now - last.Value < cooldown;
    }
}
