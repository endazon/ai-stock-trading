using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-14, UC-06, UC-07, ADR-0009, IADR-0075: リスク管理（#12）の一時停止/再開・状態照会エンドポイントを呼ぶだけの
// アダプタ。通知サービスは pause の状態を持たない（権威は Risk 側）。kill switch（HttpKillSwitchController）と同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。本アダプタが
// 使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。資格情報が未設定なら
// トークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 送信側（IADR-0020）と異なり握り潰さない。利用者が結果を知る必要がある操作であり、
// 「失敗を成功に見せない」ことが安全側になる（停止したつもりで停止していない状態を作らない）。
internal sealed class HttpPauseController(
    HttpClient httpClient,
    ILogger<HttpPauseController> logger)
    : IPauseController
{
    public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default) =>
        PostAsync("/risk-controls/pause", reason, "一時停止", cancellationToken);

    public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) =>
        PostAsync("/risk-controls/resume", reason, "再開", cancellationToken);

    private async Task<PauseResult> PostAsync(
        string path, string reason, string operation, CancellationToken cancellationToken)
    {
        try
        {
            // FR-11・ADR-0009: 理由必須。actor は Risk 側が認証済みトークン（preferred_username）から採る。
            using var response = await httpClient
                .PostAsJsonAsync(path, new PauseRequest(reason), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
                    : string.Empty;
                logger.LogWarning(
                    "取引の{Operation}に失敗しました（{Status}）。{Hint}", operation, (int)response.StatusCode, hint);
                return new PauseResult(false, false, $"取引の{operation}に失敗しました（HTTP {(int)response.StatusCode}）{hint}");
            }

            var state = await response.Content
                .ReadFromJsonAsync<PauseStateView>(cancellationToken)
                .ConfigureAwait(false);

            // 2xx だが本文を解釈できない場合、状態を騙らず失敗として返す（停止したと誤認させない）。
            if (state is null)
            {
                logger.LogWarning("取引の{Operation}の応答を解釈できませんでした。", operation);
                return new PauseResult(false, false, $"取引の{operation}の応答を解釈できませんでした");
            }

            return new PauseResult(true, state.Paused, $"取引を{operation}しました");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("取引の{Operation}がタイムアウトしました。", operation);
            return new PauseResult(false, false, $"取引の{operation}がタイムアウトしました（状態は不明です）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "取引の{Operation}で例外が発生しました。", operation);
            return new PauseResult(false, false, $"取引の{operation}に失敗しました（{ex.GetType().Name}）");
        }
    }

    public async Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/status", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
                    : string.Empty;
                logger.LogWarning("稼働状態の照会に失敗しました（{Status}）。{Hint}", (int)response.StatusCode, hint);
                return new RiskStatusResult(false, $"稼働状態の照会に失敗しました（HTTP {(int)response.StatusCode}）{hint}");
            }

            var view = await response.Content
                .ReadFromJsonAsync<RiskStatusView>(cancellationToken)
                .ConfigureAwait(false);

            if (view is null)
            {
                logger.LogWarning("稼働状態の応答を解釈できませんでした。");
                return new RiskStatusResult(false, "稼働状態の応答を解釈できませんでした");
            }

            return new RiskStatusResult(true, Format(view));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("稼働状態の照会がタイムアウトしました。");
            return new RiskStatusResult(false, "稼働状態の照会がタイムアウトしました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "稼働状態の照会で例外が発生しました。");
            return new RiskStatusResult(false, $"稼働状態の照会に失敗しました（{ex.GetType().Name}）");
        }
    }

    // ADR-0009: 3 統制の状態を優先順位（kill switch > 日次損失ロックアウト > 一時停止）で示す表示テキストを組む。
    // enum の数値表現に依存せず真偽値から導出する（Risk 側の JSON 表現に結合しない）。
    internal static string Format(RiskStatusView v)
    {
        var control = v.KillSwitchEngaged
            ? "kill switch（緊急停止）"
            : v.DailyLossLockoutActive
                ? $"日次損失ロックアウト（解除日 {v.LockoutReleaseOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未定"}）"
                : v.TradingPaused
                    ? "一時停止（pause）"
                    : "なし";

        var entries = v.NewEntriesBlocked ? "停止中" : "可能";

        return string.Join('\n',
            $"稼働状態: 新規建て={entries}（成立中の統制: {control}）",
            $"統制: kill switch={OnOff(v.KillSwitchEngaged)} / 日次損失ロックアウト={OnOff(v.DailyLossLockoutActive)} / 一時停止={OnOff(v.TradingPaused)}",
            $"段階: Stage {v.Stage}",
            $"当日損益: {v.DailyPnl:N0} 円（実現 {v.DailyRealizedPnl:N0} / 含み {v.UnrealizedPnl:N0}）",
            $"上限使用率: 日次発注 {v.DailyOrderedAmount:N0}/{v.MaxDailyOrderAmount:N0} 円 / DD {v.DrawdownRatio:P1}（上限 {v.MaxDrawdownRatio:P1}）",
            $"ポジション: {v.OpenPositionCount}/{v.MaxOpenPositions} 件");
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    // Risk 側 PauseRequest と同形（理由必須）。
    private sealed record PauseRequest(string Reason);

    // Risk 側 PauseState の必要部分のみを受ける射影（Paused のみ利用する）。
    private sealed record PauseStateView(bool Paused);

    // Risk 側 RiskStatusView の必要部分。ActiveControl（enum）は真偽値から導出するため受けない
    // （数値/文字列いずれの JSON 表現にも結合しないため）。Stage は数値でそのまま受ける。
    internal sealed record RiskStatusView(
        bool KillSwitchEngaged,
        bool DailyLossLockoutActive,
        DateOnly? LockoutReleaseOn,
        bool TradingPaused,
        bool NewEntriesBlocked,
        int Stage,
        decimal DailyRealizedPnl,
        decimal UnrealizedPnl,
        decimal DailyPnl,
        decimal DailyOrderedAmount,
        decimal MaxDailyOrderAmount,
        decimal DrawdownRatio,
        decimal MaxDrawdownRatio,
        int OpenPositionCount,
        int MaxOpenPositions);
}
