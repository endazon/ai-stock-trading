using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0425 決定4: 借株可否を発注執行の
// GET /order-execution/short-permit から同期照会する（moomoo の取引接続は発注執行だけが持つ）。
//
// 🔴 **どの失敗も「分からない」へ倒す**（許可へ倒す経路を持たない）: 非 2xx・例外・タイムアウト・本文の読み違い・
// 状態の欠落や未定義値・要求と違う銘柄／市場の答え。審査は「分からない」なら空売り文脈を組まず拒否する
// （照会できないなら空売りしない＝ADR-0016 決定3）。
// 🔴 **受け手の DTO は全項目 nullable**（IADR-0408 と同じ規律）。送り手の項目名が変わった版だけが先に配備されても、
// 欠落を既定値（列挙の 0）で読まず、契約の食い違いとして Error を出して「分からない」にする。
public sealed class HttpShortSellBorrowSource(
    HttpClient httpClient,
    ILogger<HttpShortSellBorrowSource> logger)
    : IShortSellBorrowSource
{
    // 送り手 ShortPermitStatus の数値（web 既定で列挙は数値）。送り手の型は参照しない（別サービス）。
    private const int StatusUnknown = 0;
    private const int StatusPermitted = 1;
    private const int StatusNotPermitted = 2;

    public async Task<ShortSellBorrowObservation> GetAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        try
        {
            using var response = await httpClient
                .GetAsync($"/order-execution/short-permit?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("借株可否の照会に失敗（{Status}）。借株可否は不明として扱います。", (int)response.StatusCode);
                return ShortSellBorrowObservation.Unknown($"http-{(int)response.StatusCode}");
            }

            var dto = await response.Content.ReadFromJsonAsync<ShortPermitDto>(cancellationToken).ConfigureAwait(false);
            if (dto is not { Symbol: { } answeredSymbol, Market: { } answeredMarket, Status: { } status })
            {
                logger.LogError("借株可否の応答に銘柄・市場・状態のいずれかが無い。送り手との契約の食い違いとみなし、借株可否は不明として扱います。");
                return ShortSellBorrowObservation.Unknown("contract-mismatch");
            }

            if (!string.Equals(answeredSymbol, symbol, StringComparison.Ordinal) || answeredMarket != market)
            {
                logger.LogError(
                    "借株可否の応答が要求と別の銘柄・市場を答えた（要求 {Symbol}/{Market}・応答 {AnsweredSymbol}/{AnsweredMarket}）。借株可否は不明として扱います。",
                    symbol, market, answeredSymbol, answeredMarket);
                return ShortSellBorrowObservation.Unknown("response-mismatch");
            }

            switch (status)
            {
                case StatusPermitted:
                    return ShortSellBorrowObservation.Permitted();
                case StatusNotPermitted:
                    return ShortSellBorrowObservation.NotPermitted();
                case StatusUnknown:
                    return ShortSellBorrowObservation.Unknown(dto.UnknownReason ?? "unknown");
                default:
                    logger.LogError("借株可否の応答の状態が未定義値 {Status}。借株可否は不明として扱います。", status);
                    return ShortSellBorrowObservation.Unknown("contract-mismatch");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("借株可否の照会がタイムアウト。借株可否は不明として扱います。");
            return ShortSellBorrowObservation.Unknown("timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "借株可否の照会で例外。借株可否は不明として扱います。");
            return ShortSellBorrowObservation.Unknown("request-failed");
        }
    }

    // 送り手 ShortPermitView の受け皿。欠落を既定値と区別するため全項目 nullable。ObservedAt は読まない（判定に使わない）。
    private sealed record ShortPermitDto(string? Symbol, Market? Market, int? Status, string? UnknownReason);
}
