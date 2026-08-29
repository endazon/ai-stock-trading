using System.Net.Http.Json;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR（費用）, IADR-0031: 費用統制（#23）の GET /costs/state を同期照会して統制ゲートに写像する。
// 未取得・非 2xx・例外・タイムアウト・不正応答は Normal（停止せず・1×）の安全既定に倒す。
// 注意: Normal は「間隔延長/停止を止められない側」の縮退だが、月次予算は緩変で短時間の不達では超過しにくく、
// 費用統制の一時障害で取引サイクル全体を止める（Halt）のは過大なため（IADR-0031）。
// 認証（IADR-0051・#76 完了）: /costs/state は OwnerOrService へ分離済みで、本呼び出しは HttpClient に付与された
// client_credentials サービストークン（trading-service）で認証される（Program.cs の AddAiStockTradingServiceToken）。
// ServiceAuth:ClientId/ClientSecret 未設定なら従来どおり認証ヘッダなし＝401 → Normal の安全既定に倒れる。
public sealed class HttpCostControlGate(
    HttpClient httpClient,
    ILogger<HttpCostControlGate> logger)
    : ICostControlGate
{
    // CostControlDecision（費用統制）の JSON 受け皿。CostControlService.Domain を参照せず isHalted/intervalMultiplier で疎結合に読む。
    private sealed record CostStateDto(bool IsHalted, decimal IntervalMultiplier);

    public async Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/costs/state", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("費用統制の照会に失敗（{Status}）。Normal（停止せず）に倒します。", (int)response.StatusCode);
                return CostControlGate.Normal;
            }

            var dto = await response.Content.ReadFromJsonAsync<CostStateDto>(cancellationToken).ConfigureAwait(false);
            if (dto is null)
                return CostControlGate.Normal;

            return new CostControlGate(dto.IsHalted, dto.IntervalMultiplier);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("費用統制の照会がタイムアウト。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "費用統制の照会で例外。Normal（停止せず）に倒します。");
            return CostControlGate.Normal;
        }
    }
}
