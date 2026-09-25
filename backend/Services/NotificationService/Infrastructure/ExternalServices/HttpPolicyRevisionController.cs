using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: 報告書サービスの `POST /reports/policy-revisions` を呼ぶだけのアダプタ。
// 当該エンドポイントは OwnerOnly のため、名前付き HttpClient（`report-policy-revision`）に Bot 専用の owner マップ機密
// クライアントのトークンを付与する（HttpReportReviewController と同じ）。上限は LLM の所要時間を見込んで長く取る
// （既存の 5 秒のクライアントは変えない）。
//
// 🔴 **原則 A（不明・無し・有りを混ぜない）**:
//   - 200 → 案あり（保存・提示された）。
//   - 4xx / 5xx → 案なし（報告書サービスは 200 以外で何も保存しない契約）。本文の `error`（報告書サービスの定数文）を見せる。
//   - タイムアウト・伝送の例外 → **不明**（報告書サービスは保存したかもしれない）。「失敗」とは言わず `/report show` へ誘導する。
public sealed class HttpPolicyRevisionController(
    HttpClient httpClient,
    ILogger<HttpPolicyRevisionController> logger)
    : IPolicyRevisionController
{
    public const string Path = "/reports/policy-revisions";

    // 表示する `error` の上限（報告書サービスの定数文と会話キーだけのはずだが、想定外の長文で投稿を壊さない）。
    private const int MaxErrorLength = 300;

    public async Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey, string instruction, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(Path, new RevisePolicyBody(instruction, periodKey, onBehalfOf), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ErrorOf(response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("方針の改訂が受理されませんでした（{Status}）。", (int)response.StatusCode);
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
                    : string.Empty;
                return new PolicyRevisionCommandOutcome(
                    false, false,
                    error is null
                        ? $"方針の改訂に失敗しました（HTTP {(int)response.StatusCode}）{hint}。方針は変わっていません。"
                        : $"{error}{hint}");
            }

            var view = await response.Content
                .ReadFromJsonAsync<PolicyRevisionResponseView>(cancellationToken)
                .ConfigureAwait(false);

            // 2xx だが解釈できない＝保存はされている可能性がある（200 は保存・提示の後に返る）。不明として扱う。
            if (view is null || string.IsNullOrWhiteSpace(view.PeriodKey) || view.Version < 1 || view.PolicySummary is null)
            {
                logger.LogWarning("方針の改訂の応答を解釈できませんでした。");
                return new PolicyRevisionCommandOutcome(
                    false, true, "方針の改訂の応答を解釈できませんでした（案が保存された可能性があります。/report show で確認してください）。");
            }

            return new PolicyRevisionCommandOutcome(
                true, false, view.Message ?? string.Empty,
                new PolicyRevisionProposalView(
                    view.PeriodKey!,
                    view.Version,
                    view.Created,
                    view.Presented,
                    view.AutoGenerationSkipped,
                    view.Message ?? string.Empty,
                    view.PolicySummary,
                    [.. (view.WatchlistChanges ?? [])
                        .Where(c => c is not null)
                        .Select(c => new WatchlistChangeSuggestionView(c!.Action ?? "?", c.Symbol ?? "?", c.Reason ?? string.Empty))],
                    view.Rationale));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException)
                logger.LogWarning("方針の改訂がタイムアウトしました（結果は不明）。");
            else
                logger.LogWarning(ex, "方針の改訂で例外が発生しました（結果は不明）。");

            return new PolicyRevisionCommandOutcome(
                false, true,
                "方針の改訂の結果が分かりません（応答が届きませんでした）。案が保存された可能性があるため、/report show で確認してください。");
        }
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorView>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Error))
                return null;

            var error = body.Error.Trim();
            return error.Length <= MaxErrorLength ? error : error[..(MaxErrorLength - 1)] + "…";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // 報告書サービス側 RevisePolicyRequest と同形（名前を変えてあるのは、送り手の型の目印〔IADR-0420 の検査器〕と区別するため）。
    private sealed record RevisePolicyBody(string Instruction, string? PeriodKey, string OnBehalfOf);

    // 報告書サービス側 PolicyRevisionResponse の必要部分の射影（欠落は null＝下で不明へ倒す）。
    private sealed record PolicyRevisionResponseView(
        string? PeriodKey,
        int Version,
        bool Created,
        bool Presented,
        bool AutoGenerationSkipped,
        string? Message,
        string? PolicySummary,
        IReadOnlyList<WatchlistChangeItem?>? WatchlistChanges,
        string? Rationale);

    private sealed record WatchlistChangeItem(string? Action, string? Symbol, string? Reason);

    private sealed record ErrorView(string? Error);
}
