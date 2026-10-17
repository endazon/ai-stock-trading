using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-10, FR-15, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0467 決定 2, IADR-0559 決定 3・4, #1391 (#753):
// 日足（前復権の OHLCV）を **gRPC 生成クライアント**（`OrderExecutionRead/GetDailyBars`）で読む `IDailyBarsSource` の 2 つ目の実装。
// REST 実装（HttpDailyBarsSource）と並走する（**既定は REST**。`OrderExecution:Grpc` を宣言したときだけ選ばれる）。
// 使う箇所は REST と同じ 2 つ（判断の出来高・ATR(14) の共有口と、Stage 0 の記録の当時の価格）。
//
// 🔴 **応答の解釈は REST と同じ 1 つ**（`HttpDailyBarsSource.Interpret`）。gRPC は線上の応答を同じ nullable の DTO へ写すだけ
// （銘柄・欄の欠落は null、市場・状態の未指定・未知は null、日付・価格の読めない値も null ＝ 1 本でも欠ければ全体を null）。
// 🔴 **どの失敗も null（取得できない）へ倒す**（REST と同じ）。判断は出来高を「未提供」と書き、判断は止めない（ADR-0048 決定 2）。
public sealed class GrpcDailyBarsSource(
    OrderExecutionGrpcTransport transport,
    ILogger<GrpcDailyBarsSource> logger)
    : IDailyBarsSource
{
    public async Task<IReadOnlyList<DailyBar>?> FetchAsync(
        string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var response = await transport.CallAsync(
            "日足（daily-bars）",
            "出来高は未提供として扱います。",
            (client, options) => client.GetDailyBarsAsync(
                new Proto.GetDailyBarsRequest
                {
                    Symbol = symbol,
                    Market = OrderExecutionWire.ToProto(market),
                    From = OrderExecutionWire.Day(from),
                    To = OrderExecutionWire.Day(to),
                },
                options),
            cancellationToken).ConfigureAwait(false);

        return response is null ? null : HttpDailyBarsSource.Interpret(ToDto(response), symbol, market, logger);
    }

    internal static HttpDailyBarsSource.DailyBarsDto ToDto(Proto.GetDailyBarsResponse response) =>
        new(
            response.HasSymbol ? response.Symbol : null,
            OrderExecutionWire.Market(response.Market),
            OrderExecutionWire.Status(response.Status),
            response.HasUnavailableReason ? response.UnavailableReason : null,
            [.. response.Bars.Select(b => (HttpDailyBarsSource.DailyBarDto?)ToDto(b))]);

    internal static HttpDailyBarsSource.DailyBarDto ToDto(Proto.DailyBarRow bar) =>
        new(
            OrderExecutionWire.Day(bar.HasDate, bar.Date),
            OrderExecutionWire.Price(bar.HasOpen, bar.Open),
            OrderExecutionWire.Price(bar.HasHigh, bar.High),
            OrderExecutionWire.Price(bar.HasLow, bar.Low),
            OrderExecutionWire.Price(bar.HasClose, bar.Close),
            bar.HasVolume ? bar.Volume : null);
}
