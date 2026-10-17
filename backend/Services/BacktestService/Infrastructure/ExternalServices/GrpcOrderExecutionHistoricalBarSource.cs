using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using BacktestService.Features.Backtest;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace BacktestService.Infrastructure.ExternalServices;

// NFR, FR-15, ADR-0023 決定5, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 2）, IADR-0528 決定8, IADR-0559 決定 3・4, #1391 (#753):
// 発注執行の日足を **gRPC 生成クライアント**（`OrderExecutionRead/GetDailyBars`）で読む過去データ源 `order-execution` の 2 つ目の実装。
// REST 実装（HttpOrderExecutionHistoricalBarSource）と並走する（**既定は REST**。`OrderExecution:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **銘柄ごとの照会・期間の分割・欠測の作り方・応答の解釈は REST と同じ 1 つ**（`HttpOrderExecutionHistoricalBarSource.LoadAsync` / `Interpret`）。
// gRPC は 1 回の照会の答えを同じ nullable の DTO へ写すだけである。
// 🔴 **失敗の分け方も REST と同じ**: 送り手が答えた失敗（REST の非 2xx に当たる `INVALID_ARGUMENT`・`UNAUTHENTICATED`・`PERMISSION_DENIED` 等）は
// その銘柄の欠測、送り手まで届かない・待ちきれない（REST の通信例外・タイムアウトに当たる `UNAVAILABLE`・`DEADLINE_EXCEEDED`）と
// 呼び出し元のキャンセルは送出する（握りつぶさない。定時駆動がその巡回を失敗として記録する）。
public sealed class GrpcOrderExecutionHistoricalBarSource(
    OrderExecutionGrpcTransport transport,
    IRateLimiter rateLimiter,
    ILogger<GrpcOrderExecutionHistoricalBarSource> logger) : IHistoricalBarSource
{
    public Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        HttpOrderExecutionHistoricalBarSource.LoadAsync(symbols, from, to, rateLimiter, FetchChunkAsync, logger, cancellationToken);

    private async Task<HttpOrderExecutionHistoricalBarSource.ChunkAnswer> FetchChunkAsync(
        string symbol, Market market, DateOnly chunkFrom, DateOnly chunkTo, CancellationToken cancellationToken)
    {
        try
        {
            var response = await transport.CallAsync(
                "発注執行の日足（daily-bars）",
                (client, options) => client.GetDailyBarsAsync(
                    new Proto.GetDailyBarsRequest
                    {
                        Symbol = symbol,
                        Market = OrderExecutionWire.ToProto(market),
                        From = OrderExecutionWire.Day(chunkFrom),
                        To = OrderExecutionWire.Day(chunkTo),
                    },
                    options),
                cancellationToken).ConfigureAwait(false);
            return HttpOrderExecutionHistoricalBarSource.ChunkAnswer.Answered(ToDto(response));
        }
        catch (RpcException ex) when (!OrderExecutionGrpcTransport.IsTransportFailure(ex.StatusCode))
        {
            return HttpOrderExecutionHistoricalBarSource.ChunkAnswer.Failed(
                $"発注執行の日足の照会が gRPC {ex.StatusCode} を返した（{OrderExecutionWire.Day(chunkFrom)}〜{OrderExecutionWire.Day(chunkTo)}）");
        }
    }

    internal static HttpOrderExecutionHistoricalBarSource.DailyBarsDto ToDto(Proto.GetDailyBarsResponse response) =>
        new(
            response.HasSymbol ? response.Symbol : null,
            OrderExecutionWire.MarketNumber(response.Market),
            OrderExecutionWire.Status(response.Status),
            response.HasUnavailableReason ? response.UnavailableReason : null,
            [.. response.Bars.Select(b => (HttpOrderExecutionHistoricalBarSource.DailyBarDto?)new HttpOrderExecutionHistoricalBarSource.DailyBarDto(
                OrderExecutionWire.Day(b.HasDate, b.Date),
                OrderExecutionWire.Price(b.HasOpen, b.Open),
                OrderExecutionWire.Price(b.HasHigh, b.High),
                OrderExecutionWire.Price(b.HasLow, b.Low),
                OrderExecutionWire.Price(b.HasClose, b.Close),
                b.HasVolume ? b.Volume : null))]);
}
