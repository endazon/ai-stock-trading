using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-14, UC-06, IADR-0062 決定4: リスク管理（#12）の kill switch エンドポイントを呼ぶだけのアダプタ。
// 通知サービスは kill switch の状態を持たない（権威は Risk 側）。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、IADR-0051 の s2s トークン（trading-service）では
// 403 になる。本アダプタが使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを
// 付与する（DiscordOwnerAuthExtensions）。資格情報が未設定ならトークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 送信側（IADR-0020）と異なり**握り潰さない**。kill switch は利用者が結果を知る必要がある操作で
// あり、「失敗を成功に見せない」ことが安全側になる（停止したつもりで停止していない状態を作らない）。
// 例外・非 2xx・タイムアウトはすべて Succeeded=false として理由つきで返し、Gateway が利用者へ明示する。
internal sealed class HttpKillSwitchController(
    HttpClient httpClient,
    ILogger<HttpKillSwitchController> logger)
    : IKillSwitchController
{
    public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default) =>
        PostAsync("/risk-controls/kill-switch/engage", reason, "起動", cancellationToken);

    public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
        PostAsync("/risk-controls/kill-switch/disengage", reason, "解除", cancellationToken);

    private async Task<KillSwitchResult> PostAsync(
        string path, string reason, string operation, CancellationToken cancellationToken)
    {
        try
        {
            // FR-11・ADR-0003: 理由必須。actor は Risk 側が認証済みトークン（preferred_username）から採る。
            using var response = await httpClient
                .PostAsJsonAsync(path, new KillSwitchRequest(reason), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 は Bot の owner クライアント設定の不備を示すため、切り分けできる文言にする。
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
                    : string.Empty;
                logger.LogWarning(
                    "kill switch の{Operation}に失敗しました（{Status}）。{Hint}", operation, (int)response.StatusCode, hint);
                return new KillSwitchResult(false, false, $"kill switch の{operation}に失敗しました（HTTP {(int)response.StatusCode}）{hint}");
            }

            var state = await response.Content
                .ReadFromJsonAsync<KillSwitchStateView>(cancellationToken)
                .ConfigureAwait(false);

            // 2xx だが本文を解釈できない場合、状態を騙らず失敗として返す（停止したと誤認させない）。
            if (state is null)
            {
                logger.LogWarning("kill switch の{Operation}の応答を解釈できませんでした。", operation);
                return new KillSwitchResult(false, false, $"kill switch の{operation}の応答を解釈できませんでした");
            }

            return new KillSwitchResult(true, state.Engaged, $"kill switch を{operation}しました");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("kill switch の{Operation}がタイムアウトしました。", operation);
            return new KillSwitchResult(false, false, $"kill switch の{operation}がタイムアウトしました（状態は不明です）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "kill switch の{Operation}で例外が発生しました。", operation);
            return new KillSwitchResult(false, false, $"kill switch の{operation}に失敗しました（{ex.GetType().Name}）");
        }
    }

    // Risk 側 KillSwitchRequest と同形（理由必須）。
    private sealed record KillSwitchRequest(string Reason);

    // Risk 側 KillSwitchState の必要部分のみを受ける射影（Engaged のみ利用する）。
    private sealed record KillSwitchStateView(bool Engaged);
}
