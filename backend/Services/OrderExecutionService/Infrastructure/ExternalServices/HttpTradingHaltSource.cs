using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// 🔴 FR-10, ADR-0009, NFR-04, IADR-0565 決定 2: 停止の状態を、リスク管理の GET /risk-controls/trading-halt（OwnerOrService・読み取り専用）から
// 同期照会する。s2s のトークンは "risk" HttpClient に付く（AddAiStockTradingServiceToken）。待ちの上限は RequestTimeout（1 試行・再試行しない）。
//
// 🔴 fail-closed: 非 2xx・例外・タイムアウト・本文の欠落・項目の欠落（null）は**不明**（null）。項目の欠落を false（＝停止していない）と読まない
// ——送り手の改名・旧い送り手で項目が消えると「停止していない」に見え、停止中に新規建てが通る向きの誤りになる。
// 呼び出し元の取り消しでも投げずに不明を返す（予約を取った後に呼ばれるため、投げると予約が Reserved のまま残る。IADR-0565 決定 4）。
public sealed class HttpTradingHaltSource(
    HttpClient httpClient,
    ILogger<HttpTradingHaltSource> logger)
    : ITradingHaltSource
{
    /// <summary>1 回の照会の待ちの上限（"risk" HttpClient.Timeout。IADR-0565 決定 4）。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    public async Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/trading-halt", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "停止の状態（kill switch・一時停止）の照会に失敗しました（{Status}）。不明として扱います（新規建てを送りません）。",
                    (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<TradingHaltDto>(cancellationToken)
                .ConfigureAwait(false);
            return Interpret(dto, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "停止の状態（kill switch・一時停止）の照会がタイムアウトしました（上限 {Timeout} 秒）。不明として扱います（新規建てを送りません）。",
                RequestTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // 呼び出し元の取り消しを含む（上の説明）。
            logger.LogWarning(ex, "停止の状態（kill switch・一時停止）の照会で例外が発生しました。不明として扱います（新規建てを送りません）。");
            return null;
        }
    }

    internal static TradingHaltState? Interpret(TradingHaltDto? dto, ILogger logger)
    {
        if (dto is not { KillSwitchEngaged: { } killSwitch, TradingPaused: { } paused })
        {
            logger.LogWarning("停止の状態の応答に項目の欠落があります。不明として扱います（新規建てを送りません）。");
            return null;
        }

        return new TradingHaltState(killSwitch, paused);
    }

    // 受け手の行（nullable。欠落と false を取り違えない）。送り手は RiskManagementService の TradingHaltView（web 既定）。
    internal sealed record TradingHaltDto(bool? KillSwitchEngaged, bool? TradingPaused);
}
