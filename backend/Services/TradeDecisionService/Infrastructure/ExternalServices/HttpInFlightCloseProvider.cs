using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 FR-04, FR-10, #1338, IADR-0535 決定2・3: 処理中の決済が建玉を覆うかを、発注執行の
// GET /order-execution/in-flight-closes?symbol&market&closeSide（OwnerOrService・読み取り専用）から同期照会する
// （処理中の決済の記録と OpenD の接続は発注執行の Pod にだけある。cross-service の読み取りはアダプタに置く＝#1324 の前例）。
//
// 🔴 **どの失敗も null（不明）へ倒す**: 非 2xx・例外・タイムアウト・本文の読み違い・状態が Unknown・要求と違う銘柄／市場／方向の答え・
// 未知の状態。不明のとき判断は LLM を呼ぶ（見送らない）。「覆う」を読み違えて**見送る**向きの誤りを必ず不明へ倒す
// ——見送る向きの誤りは決済（損切りを含む）の判断を止める（IADR-0535 決定3）。
// 🔴 受け手の DTO は全項目 nullable（IADR-0408 と同じ規律）。項目名が変わった版が先に配備されても、欠落を既定値で読まない。
public sealed class HttpInFlightCloseProvider(
    HttpClient httpClient,
    ILogger<HttpInFlightCloseProvider> logger)
    : IInFlightCloseProvider
{
    // 送り手 InFlightClosesStatus の数値（web 既定で列挙は数値）。送り手の型は参照しない（別サービス）。
    // NFR, IADR-0559 決定 3, #1391: gRPC の実装（GrpcInFlightCloseProvider）が線上の状態をこの数値へ写す（可視性を広げただけ）。
    internal const int StatusUnknown = 0;
    internal const int StatusCovered = 1;
    internal const int StatusNotCovered = 2;

    public async Task<InFlightCloseCoverage?> GetAsync(
        string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        try
        {
            using var response = await httpClient
                .GetAsync(
                    $"/order-execution/in-flight-closes?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}"
                        + $"&closeSide={(int)closeSide}",
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("処理中の決済の照会に失敗（{Status}）。不明として扱います（LLM を呼びます）。", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<InFlightClosesDto>(cancellationToken).ConfigureAwait(false);
            return Interpret(dto, symbol, market, closeSide, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("処理中の決済の照会がタイムアウト。不明として扱います（LLM を呼びます）。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "処理中の決済の照会で例外。不明として扱います（LLM を呼びます）。");
            return null;
        }
    }

    // 🔴 規則（数え方）は持たない。持つのは「応答が問いに答えているか」の検証だけである。
    internal static InFlightCloseCoverage? Interpret(
        InFlightClosesDto? dto, string symbol, Market market, TradeSide closeSide, ILogger logger)
    {
        if (dto is not { Symbol: { } answeredSymbol, Market: { } answeredMarket, CloseSide: { } answeredSide, Status: { } status })
        {
            logger.LogWarning("処理中の決済の応答に銘柄・市場・方向・状態のいずれかが無い。不明として扱います。");
            return null;
        }

        if (!string.Equals(answeredSymbol, symbol, StringComparison.Ordinal) || answeredMarket != market || answeredSide != closeSide)
        {
            logger.LogWarning(
                "処理中の決済の応答が問いと一致しません（要求 {Symbol}/{Market}/{Side}・応答 {AnsweredSymbol}/{AnsweredMarket}/{AnsweredSide}）。不明として扱います。",
                symbol, market, closeSide, answeredSymbol, answeredMarket, answeredSide);
            return null;
        }

        // 🔴 #1434, IADR-0572 決定2: ブローカーの決済方向の建玉。負（契約外）は不明（null）へ倒す——0 と読むと LLM を省く向きへ誤る。
        int? closable = dto.ClosableQuantity is >= 0 ? dto.ClosableQuantity : null;
        switch (status)
        {
            case StatusCovered when dto is { InFlightQuantity: > 0 and var quantity, OrderIds: { } orderIds }:
                return new InFlightCloseCoverage(true, quantity, [.. orderIds.Where(id => id is not null).Select(id => id!)], closable);
            case StatusNotCovered:
                return new InFlightCloseCoverage(false, dto.InFlightQuantity ?? 0, [], closable);
            default:
                // Unknown（0）・未知の状態・覆うのに処理中の株数や注文 ID が欠けた応答。
                logger.LogInformation(
                    "処理中の決済は不明（状態 {Status}・理由 {Reason}）。LLM を呼びます: {Symbol}", status, dto.UnknownReason ?? "-", symbol);
                return null;
        }
    }

    // 送り手 InFlightClosesView の受け皿。欠落を既定値と区別するため全項目 nullable。DecisionIds は判定に使わない。
    // #1434, IADR-0572 決定2: ClosableQuantity（ブローカーの決済方向の建玉）は読む。
    internal sealed record InFlightClosesDto(
        string? Symbol, Market? Market, TradeSide? CloseSide, int? Status, string? UnknownReason,
        int? ClosableQuantity, int? InFlightQuantity, IReadOnlyList<string?>? OrderIds, IReadOnlyList<Guid?>? DecisionIds);
}
