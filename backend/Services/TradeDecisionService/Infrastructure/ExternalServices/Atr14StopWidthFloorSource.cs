using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 FR-10, ADR-0049 決定2, ADR-0048 決定3, #1122, IADR-0486 決定1・決定3・決定4: 損切り幅の下限＝1.0 × ATR(14, 日足) の供給口。
// - 日足は #1118 の口（IDailyBarsProvider。前復権・前営業日までの確定足・銘柄 × 取引日のキャッシュ）を**出来高と同じ singleton で**読む
//   （同じ銘柄の同じ取引日の取得は 1 回。取得枠を増やさない）。
// - 計算は純関数 AverageTrueRange（直近 14 本の True Range の単純平均・最後の足が前営業日・15 本未満は null）。
// - 得られない（null・例外）ときは null を返す（取引判断が参照価格の 2% へ退避する。判断は止めない）。キャンセルは伝える。
// - Stage 0（GetFloorAsOfAsync）は判断時点の前営業日までの確定足（GetConfirmedBarsAsOfAsync。本番のキャッシュに触れない）から同じ計算をする。
// 本番の既定（StopWidthFloor:Atr14:Enabled=false）では登録されない（NoAtrStopWidthFloorSource）。
public sealed class Atr14StopWidthFloorSource(IDailyBarsProvider dailyBars, ILogger<Atr14StopWidthFloorSource> logger)
    : IStopWidthFloorSource
{
    /// <summary>日足の口（組み立ての試験が出来高と同じ singleton かを読む）。</summary>
    public IDailyBarsProvider DailyBars => dailyBars;

    public bool IsEnabled => true;

    public async ValueTask<StopWidthFloor?> GetFloorAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ConfirmedDailyBars? bars;
        try
        {
            bars = await dailyBars.GetConfirmedBarsAsync(symbol, market, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ATR(14) の日足の照会に失敗しました（損切り幅の下限は参照価格の 2% にします）: {Symbol}", symbol);
            return null;
        }

        return Floor(symbol, bars, asOf: null);
    }

    public async ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
    {
        ConfirmedDailyBars? bars;
        try
        {
            bars = await dailyBars.GetConfirmedBarsAsOfAsync(symbol, market, tradingDay, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Stage 0 の ATR(14) の日足の照会に失敗しました（損切り幅の下限は参照価格の 2% にします）: {Symbol} asOf={AsOf}",
                symbol, tradingDay);
            return null;
        }

        return Floor(symbol, bars, tradingDay);
    }

    private StopWidthFloor? Floor(string symbol, ConfirmedDailyBars? bars, DateOnly? asOf)
    {
        var floor = StopWidthFloorPolicy.FromAtr(AverageTrueRange.Compute(bars));
        if (floor is null)
        {
            logger.LogInformation(
                "ATR(14) が得られない（足が足りない・前営業日の足が無い・壊れた足・取得できない）。下限は参照価格の 2% にします: "
                    + "{Symbol} asOf={AsOf} bars={Bars} last={Last} expected={Expected}",
                symbol, (object?)asOf ?? "now", bars?.Bars.Count, bars is { Bars.Count: > 0 } ? bars.Bars[^1].Date : null,
                bars?.ExpectedPreviousTradingDay);
        }

        return floor;
    }
}
