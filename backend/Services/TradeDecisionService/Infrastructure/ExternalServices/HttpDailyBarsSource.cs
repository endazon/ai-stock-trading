using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-15, ADR-0048 決定 2, #1118, IADR-0467 決定 2: 日足（前復権の OHLCV）を発注執行の
// GET /order-execution/daily-bars?symbol&market&from&to（OwnerOrService・読み取り専用）から同期照会する
// （OpenD の接続先と鍵は発注執行の Pod にだけある。IADR-0464）。
//
// 🔴 **どの失敗も null（取得できない）へ倒す**: 非 2xx・例外・タイムアウト・本文の読み違い・状態が Available でない・
// 要求と違う銘柄／市場の答え・足の欄の欠け。判断はそのとき出来高を「未提供」と書き、判断は止めない（ADR-0048 決定 2）。
// 🔴 **受け手の DTO は全項目 nullable**（IADR-0408 と同じ規律）。送り手の項目名が変わった版だけが先に配備されても、欠落を既定値
// （列挙の 0・日付の最小値・出来高 0）で読まない。欠けた足を黙って捨てると本数が減って比の分母が変わるため、**1 本でも欠けたら全体を null**。
public sealed class HttpDailyBarsSource(
    HttpClient httpClient,
    ILogger<HttpDailyBarsSource> logger)
    : IDailyBarsSource
{
    // 送り手 DailyBarsStatus の数値（web 既定で列挙は数値）。送り手の型は参照しない（別サービス）。
    private const int StatusAvailable = 1;

    public async Task<IReadOnlyList<DailyBar>?> FetchAsync(
        string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        try
        {
            using var response = await httpClient
                .GetAsync(
                    $"/order-execution/daily-bars?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}"
                        + $"&from={Format(from)}&to={Format(to)}",
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("日足の照会に失敗（{Status}）。出来高は未提供として扱います。", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<DailyBarsDto>(cancellationToken).ConfigureAwait(false);
            return Interpret(dto, symbol, market, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("日足の照会がタイムアウト。出来高は未提供として扱います。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "日足の照会で例外。出来高は未提供として扱います。");
            return null;
        }
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IReadOnlyList<DailyBar>? Interpret(DailyBarsDto? dto, string symbol, Market market, ILogger logger)
    {
        if (dto is not { Symbol: { } answeredSymbol, Market: { } answeredMarket, Status: { } status })
        {
            logger.LogError("日足の応答に銘柄・市場・状態のいずれかが無い。送り手との契約の食い違いとみなし、出来高は未提供として扱います。");
            return null;
        }

        if (!string.Equals(answeredSymbol, symbol, StringComparison.Ordinal) || answeredMarket != market)
        {
            logger.LogError(
                "日足の応答が要求と別の銘柄・市場を答えた（要求 {Symbol}/{Market}・応答 {AnsweredSymbol}/{AnsweredMarket}）。出来高は未提供として扱います。",
                symbol, market, answeredSymbol, answeredMarket);
            return null;
        }

        if (status != StatusAvailable)
        {
            logger.LogInformation(
                "日足を取得できない（状態 {Status}・理由 {Reason}）。出来高は未提供として扱います: {Symbol}",
                status, dto.UnavailableReason ?? "-", symbol);
            return null;
        }

        if (dto.Bars is null)
        {
            logger.LogError("日足の応答に足の一覧が無い。送り手との契約の食い違いとみなし、出来高は未提供として扱います。");
            return null;
        }

        var bars = new List<DailyBar>(dto.Bars.Count);
        foreach (var b in dto.Bars)
        {
            if (b is not { Date: { } date, Open: { } open, High: { } high, Low: { } low, Close: { } close, Volume: { } volume })
            {
                logger.LogError("日足の応答に欄の欠けた足がある。送り手との契約の食い違いとみなし、出来高は未提供として扱います。");
                return null;
            }

            bars.Add(new DailyBar(date, open, high, low, close, volume));
        }

        return bars;
    }

    // 送り手 DailyBarsView の受け皿。欠落を既定値と区別するため全項目 nullable。From・To は読まない（判定に使わない）。
    private sealed record DailyBarsDto(
        string? Symbol, Market? Market, int? Status, string? UnavailableReason, IReadOnlyList<DailyBarDto?>? Bars);

    private sealed record DailyBarDto(
        DateOnly? Date, decimal? Open, decimal? High, decimal? Low, decimal? Close, long? Volume);
}
