using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Notification.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Infrastructure.Composable.Adapters;

// FR-14, FR-07, UC-03〜05, ADR-0003, IADR-0240: 報告書サービス（#14）のレビュー・確定エンドポイントを
// 呼ぶだけのアダプタ。通知サービスは報告書の状態を持たない（権威は報告書サービス側）。
// kill switch / pause / 段階ゲート（HttpKillSwitchController ほか）と同型。
//
// 当該エンドポイントは OwnerOnly（trading-owner）であり、s2s トークン（trading-service）では 403 になる。本アダプタが
// 使う名前付き HttpClient には Bot 専用の owner マップ機密クライアントのトークンを付与する。資格情報が未設定なら
// トークン無し＝401 となり操作は失敗する（安全側）。
//
// 失敗時の方針: 握り潰さない。**「確定したつもりで確定していない」状態を作らない**（kill switch と同じ）。
internal sealed class HttpReportReviewController(
    HttpClient httpClient,
    ILogger<HttpReportReviewController> logger)
    : IReportReviewController
{
    public async Task<ReportReviewResult> GetReviewAsync(
        string periodKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        try
        {
            using var response = await httpClient
                .GetAsync($"/reports/{periodKey}/review", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ReportReviewResult(false, 0, FailureMessage("レビュー局面の照会", response.StatusCode));

            var view = await response.Content
                .ReadFromJsonAsync<ReviewView>(cancellationToken)
                .ConfigureAwait(false);

            // 2xx だが本文を解釈できない場合、版番号を騙らず失敗として返す（誤った版で確定させない）。
            if (view is null)
            {
                logger.LogWarning("レビュー局面の応答を解釈できませんでした（PeriodKey={PeriodKey}）。", periodKey);
                return new ReportReviewResult(false, 0, "レビュー局面の応答を解釈できませんでした");
            }

            return new ReportReviewResult(true, view.Version, $"報告書 {periodKey}: 版 {view.Version}");
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportReviewResult(false, 0, ExceptionMessage("レビュー局面の照会", ex, cancellationToken));
        }
    }

    // FR-07, ADR-0003, 詳細設計07 §二重実行防止: 版番号付き冪等の確定。
    // 409（版不一致・確定済み変更）は Succeeded=true / Confirmed=false ではなく、**呼び出しの失敗として扱わない**
    // ——サーバは正しく応答している。受理されなかったことを Confirmed=false で表す。
    public async Task<ReportConfirmResult> ConfirmAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(
                    $"/reports/{periodKey}/confirm", new ConfirmRequest(expectedVersion), cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                logger.LogWarning(
                    "報告書の確定が版不一致で拒否されました（PeriodKey={PeriodKey}・版={Version}）。",
                    periodKey, expectedVersion);
                return new ReportConfirmResult(
                    true, false, "版番号が一致しません。最新のドラフトを確認してください。");
            }

            if (!response.IsSuccessStatusCode)
                return new ReportConfirmResult(false, false, FailureMessage("報告書の確定", response.StatusCode));

            return new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）を確定しました。");
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportConfirmResult(false, false, ExceptionMessage("報告書の確定", ex, cancellationToken));
        }
    }

    public async Task<ReportReviewResult> RequestChangesAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(
                    $"/reports/{periodKey}/request-changes",
                    new ReviewCommandRequest(expectedVersion),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ReportReviewResult(false, 0, FailureMessage("報告書の差し戻し", response.StatusCode));

            var view = await response.Content
                .ReadFromJsonAsync<ReviewView>(cancellationToken)
                .ConfigureAwait(false);

            return view is null
                ? new ReportReviewResult(false, 0, "報告書の差し戻しの応答を解釈できませんでした")
                : new ReportReviewResult(
                    true, view.Version, $"報告書 {periodKey}（版 {view.Version}）を差し戻しました。");
        }
        catch (Exception ex) when (Handled(ex, cancellationToken))
        {
            return new ReportReviewResult(false, 0, ExceptionMessage("報告書の差し戻し", ex, cancellationToken));
        }
    }

    private string FailureMessage(string operation, HttpStatusCode status)
    {
        var hint = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
            : string.Empty;
        logger.LogWarning("{Operation}に失敗しました（{Status}）。{Hint}", operation, (int)status, hint);
        return $"{operation}に失敗しました（HTTP {(int)status}）{hint}";
    }

    private string ExceptionMessage(string operation, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Operation}がタイムアウトしました。", operation);
            return $"{operation}がタイムアウトしました（結果は不明です）";
        }

        logger.LogWarning(ex, "{Operation}で例外が発生しました。", operation);
        return $"{operation}に失敗しました（{ex.GetType().Name}）";
    }

    // 呼び出し側のキャンセルだけは伝播させる（タイムアウトは HttpClient 由来の OperationCanceledException）。
    private static bool Handled(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    // 報告書サービス側 ReportReview の必要部分のみを受ける射影。
    // **State（enum）は受けない**——数値/文字列いずれの JSON 表現にも結合しないため（IADR-0240 決定5）。
    private sealed record ReviewView(int Version);

    // 報告書サービス側 ConfirmReportRequest / ReviewCommandRequest と同形（版番号付き）。
    private sealed record ConfirmRequest(int ExpectedVersion);

    private sealed record ReviewCommandRequest(int ExpectedVersion);
}
