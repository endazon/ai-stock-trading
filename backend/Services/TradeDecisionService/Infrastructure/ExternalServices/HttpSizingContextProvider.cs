extern alias RiskManagementWorker;

using System.Net.Http.Json;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-10, IADR-0029: サイジング文脈をリスク管理（#12）の GET /risk-controls/sizing-context から同期照会する。
// 未取得・非 2xx・例外・タイムアウト・不正応答は残枠 0 の安全既定（availableCapital 0 → 数量 0 → 見送り＝取引しない）に倒す。
//
// FR-04, FR-10, #957, IADR-0408: 🔴 **項目の欠落を既定値（0・InternalPaper・null）で読まない。** 送り手の項目名が変わった版だけが
// 先に配備されると、連敗数・DD 比率は 0（縮小係数が外れる＝上限側への fail-open）、動作モードは InternalPaper、上限は null
// （判断の中で NullReferenceException）に化ける。受け手の DTO を nullable にし、欠けていれば安全既定へ倒して Error を出す。
public sealed class HttpSizingContextProvider(
    HttpClient httpClient,
    ILogger<HttpSizingContextProvider> logger)
    : ISizingContextProvider
{
    public async Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/sizing-context", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("サイジング文脈の照会に失敗（{Status}）。残枠 0 の安全既定に倒します。", (int)response.StatusCode);
                return SafeDefault();
            }

            // SizingContextView（RiskManagement）と同形。camelCase・列挙は数値で往復する（契約は T-10-802）。
            var dto = await response.Content.ReadFromJsonAsync<SizingContextDto>(cancellationToken).ConfigureAwait(false);
            return Interpret(dto, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("サイジング文脈の照会がタイムアウト。残枠 0 の安全既定に倒します。");
            return SafeDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "サイジング文脈の照会で例外。残枠 0 の安全既定に倒します。");
            return SafeDefault();
        }
    }

    // NFR, IADR-0427 決定 5, #997: 応答の**解釈**。輸送に依らず 1 つであり、gRPC（GrpcSizingContextProvider）も proto を
    // 同じ nullable の DTO へ写してからここを呼ぶ。**中身は切り出す前と 1 文字も変えていない。**
    internal static SizingContext Interpret(SizingContextDto? dto, ILogger logger)
    {
        if (dto is not { ConsecutiveLosses: { } losses, DrawdownRatio: { } drawdown, Mode: { } mode, Limits: { } limits }
            || !Enum.IsDefined(mode))
        {
            // #957, IADR-0408: 契約の食い違い（非 2xx・例外の一過性の障害とは別）なので Error。
            logger.LogError(
                "サイジング文脈の応答に連敗数・DD 比率・動作モード・上限のいずれかが無い（または動作モードが未定義値）。"
                    + "送り手との契約の食い違いとみなし、残枠 0 の安全既定に倒します。");
            return SafeDefault();
        }

        // 損切りの実行機構の未定義値は「不明」（null）として読む（S0 と読まない。IADR-0351 決定1）。
        var stopLossMethod = dto.StopLossMethod is { } method && Enum.IsDefined(method) ? method : (StopLossExecutionMethod?)null;
        return new SizingContext(
            dto.Capital, dto.StageCapitalRemaining, dto.DailyOrderRemaining, losses, drawdown, mode, limits, stopLossMethod);
    }

    // フェイルセーフ既定: 段階/日次残枠 0 → availableCapital 0 → 数量 0 → 見送り（取引しない）。Limits は PositionSizer を動かせる既定値。
    // FR-10, #869, ADR-0041 決定2, IADR-0354: **資金は null（未供給）で倒す。** 照会できていないのに
    // 初期資金（定数）を名乗ると、プロンプトにも監査にも「その額の運用資金がある」と書かれてしまう。
    internal static SizingContext SafeDefault() => new(
        Capital: null,
        StageCapitalRemaining: 0m,
        DailyOrderRemaining: 0m,
        ConsecutiveLosses: 0,
        DrawdownRatio: 0m,
        Mode: BrokerProvider.InternalPaper,
        Limits: TradingDefaults.CreateRiskLimits());

    // #957, IADR-0408: 送り手 SizingContextView の受け皿。欠落を既定値と区別するため全項目 nullable。
    internal sealed record SizingContextDto(
        decimal? Capital,
        decimal? StageCapitalRemaining,
        decimal? DailyOrderRemaining,
        int? ConsecutiveLosses,
        decimal? DrawdownRatio,
        BrokerProvider? Mode,
        RiskLimitSettings? Limits,
        StopLossExecutionMethod? StopLossMethod);
}
