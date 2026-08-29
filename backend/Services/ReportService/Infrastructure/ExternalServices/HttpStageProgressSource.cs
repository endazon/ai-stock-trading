using System.Net.Http.Json;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-15, FR-20, #569, 04_report-templates 月報 §5, IADR-0051, IADR-0271:
// 現在の運用段階を権威源（リスク管理 #12 の段階ゲート）から s2s 同期照会する
// （GET /risk-controls/stage-gate・OwnerOrService）。
//
// 🔴 **供給不達（未設定・非 2xx・timeout・例外・不正応答・未知の段階値）はすべて `null`（未供給）へ倒す。**
// 段階は三者比較の「空欄」と「0」を分ける鍵であり、誤った既定は**乖離の読み方を反転させる**。
public sealed class HttpStageProgressSource(HttpClient httpClient, ILogger<HttpStageProgressSource> logger)
    : IStageProgressSource
{
    public async Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/stage-gate", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "運用段階の照会に失敗しました（{Status}）。**未供給として扱います**（Stage 0 とは書きません）。",
                    (int)response.StatusCode);
                return null;
            }

            var view = await response.Content
                .ReadFromJsonAsync<StageGateDto>(cancellationToken)
                .ConfigureAwait(false);

            if (view?.CurrentStage is not { } stage)
            {
                logger.LogWarning("運用段階の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            // 🔴 **未定義の列挙値を素通ししない。** 権威源が段階を増やしたとき、
            // 未知の値を「到達済み」として比較へ流すと、走らせていない段の列が埋まる。
            if (!Enum.IsDefined(stage))
            {
                logger.LogWarning("未知の運用段階（{Stage}）が返りました。**未供給として扱います**。", (int)stage);
                return null;
            }

            return stage;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("運用段階の照会がタイムアウトしました。**未供給として扱います**。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "運用段階の照会で例外が発生しました。**未供給として扱います**。");
            return null;
        }
    }

    // 権威源の StageGateStatus のうち**本アダプタが必要とする 1 項目だけ**を受ける
    // （余分な項目を写すと、権威源の変更でここが壊れる面が増える）。
    private sealed record StageGateDto(TradingStage? CurrentStage);
}
