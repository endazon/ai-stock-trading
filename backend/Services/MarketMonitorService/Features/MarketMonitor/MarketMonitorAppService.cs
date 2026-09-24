using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03, UC-02, ADR-0003, IADR-0014: 市場監視の 1 巡回オーケストレーション。
// (1) 保有銘柄の損切りライン到達を検知して StopLossTriggered を、(2) 監視銘柄の変動閾値超過（クールダウン外）を
// 検知して PriceMovementDetected を生成する。損切りはクールダウン・変動判定と独立に常に評価する（フェイルセーフ）。
// 価格取得失敗（null）の銘柄はスキップして監視を継続する。発行（メッセージング）は Worker（Slice B）が担う。
//
// 🔴 FR-03, FR-01, #909, IADR-0380 決定2: **閉場している市場の銘柄は 1 件も照会しない。** 閉場中の価格は終値で
// 凍っていて新しい情報が無く、終値がラインを割った日は閉場中ずっと到達が成立し続ける。そこで出した成行は
// 翌寄りまで約定しない（寄り値は終値と乖離し得る）。照会そのものが FR-01 の費用でもある。
// 「到達だけ止めて評価は回す」形は採らない —— 費用が残り、得られる情報はゼロだからである。
//
// 🔴 FR-03, FR-10, #957, IADR-0399 決定3: **市況の照会は銘柄ごとに閉じる。** 1 銘柄の照会の例外（呼び出し側の停止要求以外の
// 打ち切りを含む）はその銘柄の「価格が取れない」として扱い、次の銘柄へ進む。以前は例外が巡回全体を落とし、全建玉の
// 損切り検知・変動検知・生存要約が止まっていた（実運用の市況源 Finnhub は銘柄 null で ArgumentNullException を投げる）。
public sealed class MarketMonitorAppService(
    IMonitoredSymbolStore settingsStore,
    IPositionStore positionStore,
    IPriceBaselineStore baselineStore,
    ICooldownStore cooldownStore,
    IMarketDataSource marketData,
    IMarketSchedule schedule,
    IClock clock,
    ILogger<MarketMonitorAppService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<MarketMonitorAppService>.Instance;

    public async Task<MonitorRoundResult> EvaluateRoundAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetSettings();
        var now = clock.UtcNow;

        var stopLosses = new List<StopLossTriggered>();
        var movements = new List<PriceMovementDetected>();
        var evaluations = new List<StopLossEvaluation>();
        var closedMarketPositions = new List<StopLossEvaluation>();

        // (1) 損切りライン検知（保有銘柄）。変動判定・クールダウンと独立に常に評価する（フェイルセーフ）。
        // 保有ポジションはリスク管理（#63 台帳）を同期照会する（IADR-0030）。照会失敗は空列（＝検知対象なし）。
        var openPositions = await positionStore.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var position in openPositions)
        {
            // #909, IADR-0380 決定2: 閉場している市場は照会も判定もしない。**黙って飛ばさず**、
            // 保護の空白として記録へ残す（決定3。StopLossLivenessReporter が 1 回だけ声に出す）。
            if (!schedule.IsOpen(position.Market, now))
            {
                closedMarketPositions.Add(new StopLossEvaluation(
                    position.Symbol, position.Market, position.Side, position.Quantity,
                    position.StopLossPrice, null, now)
                {
                    StopLossApproximated = position.StopLossApproximated,
                });
                continue;
            }

            var quote = await GetQuoteOrNullAsync(position.Symbol, position.Market, cancellationToken).ConfigureAwait(false);

            // FR-10, #902, IADR-0365 決定1: 評価の記録を残す（価格欠落も含む）。判定・発行は下の従来の経路のまま。
            evaluations.Add(new StopLossEvaluation(
                position.Symbol, position.Market, position.Side, position.Quantity,
                position.StopLossPrice, quote?.Price, now)
            {
                StopLossApproximated = position.StopLossApproximated,
            });

            if (quote is null)
            {
                continue; // 取得失敗はスキップ（監視継続）。欠落の継続は StopLossLivenessReporter が Warning にする
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
            if (!schedule.IsOpen(monitored.Market, now))
            {
                continue; // #909: 閉場中の変動判定は終値同士の比較にしかならない（照会もしない）
            }

            var quote = await GetQuoteOrNullAsync(monitored.Symbol, monitored.Market, cancellationToken).ConfigureAwait(false);
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

        return new MonitorRoundResult(stopLosses, movements)
        {
            StopLossEvaluations = evaluations,
            ClosedMarketPositions = closedMarketPositions,
        };
    }

    // #957, IADR-0399 決定3: 1 銘柄の照会の失敗をその銘柄に閉じる。呼び出し側の停止要求だけは伝える（監視の停止）。
    // 打ち切り（HttpClient の上限など、呼び出し側のトークンではないもの）は「価格が取れない」側に倒す。
    private async Task<Quote?> GetQuoteOrNullAsync(string symbol, Market market, CancellationToken cancellationToken)
    {
        try
        {
            return await marketData.GetLatestQuoteAsync(symbol, market, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "現在値の照会で例外（{Symbol}/{Market}）。この銘柄は価格が取れないものとして扱い、他の銘柄の評価を続けます。",
                symbol, market);
            return null;
        }
    }

    private bool IsInCooldown(string symbol, Market market, DateTimeOffset now, TimeSpan cooldown)
    {
        var last = cooldownStore.GetLastTriggered(symbol, market);
        return last is not null && now - last.Value < cooldown;
    }
}
