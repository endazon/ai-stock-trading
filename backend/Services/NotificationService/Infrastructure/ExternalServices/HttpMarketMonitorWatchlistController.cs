using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-13, FR-14, ADR-0042 決定 1, #1025, IADR-0433: 市場監視サービスの `GET /monitor/watchlist` と
// `POST /monitor/watchlist/proposal-apply` を呼ぶだけのアダプタ。名前付き HttpClient（`market-monitor-watchlist`）には
// Bot 専用の owner マップ機密クライアントのトークンを付与する（適用は OwnerOnly）。
//
// 🔴 原則 A: 照会の失敗は「空」ではなく失敗、適用のタイムアウト・例外は「適用されなかった」ではなく**不明**として返す。
// 市場監視の JSON は既定（列挙は数値）であり、市場は Market 列挙で送受信して名前の文字列へ写す。
public sealed class HttpMarketMonitorWatchlistController(
    HttpClient httpClient,
    ILogger<HttpMarketMonitorWatchlistController> logger)
    : IMarketMonitorWatchlistController
{
    public const string WatchlistPath = "/monitor/watchlist";
    public const string ApplyPath = "/monitor/watchlist/proposal-apply";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<WatchlistSnapshotResult> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(WatchlistPath, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("監視銘柄の照会に失敗しました（{Status}）。", (int)response.StatusCode);
                return new WatchlistSnapshotResult(false, [], $"監視銘柄を照会できませんでした（HTTP {(int)response.StatusCode}）");
            }

            var symbols = await response.Content.ReadFromJsonAsync<List<SymbolView?>>(Web, cancellationToken).ConfigureAwait(false);
            if (symbols is null || symbols.Any(s => s is null || string.IsNullOrWhiteSpace(s.Symbol) || s.Market is null))
                return new WatchlistSnapshotResult(false, [], "監視銘柄の応答を解釈できませんでした");

            return new WatchlistSnapshotResult(
                true, [.. symbols.Select(s => new WatchlistSnapshotItemView(s!.Symbol!, s.Market!.Value.ToString()))], "照会しました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "監視銘柄の照会で例外が発生しました。");
            return new WatchlistSnapshotResult(false, [], "監視銘柄を照会できませんでした（応答が届きませんでした）");
        }
    }

    public async Task<WatchlistApplyOutcome> ApplyProposalAsync(
        IReadOnlyList<WatchlistSnapshotItemView> expected,
        IReadOnlyList<WatchlistChangeSuggestionView> changes,
        string proposalRef,
        string onBehalfOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var body = new ApplyBody(
            [.. expected.Select(e => new SymbolView(e.Symbol, Enum.TryParse<Market>(e.Market, out var m) ? m : null))],
            [.. changes.Select(c => new ChangeBody(c.Action, c.Symbol, c.Reason))],
            proposalRef,
            onBehalfOf);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(ApplyPath, body, Web, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict)
                return new WatchlistApplyOutcome(WatchlistApplyStatus.Stale, [], null,
                    await ErrorOf(response, cancellationToken).ConfigureAwait(false)
                        ?? "案を作った後に監視銘柄が変わったため、入れ替えを 1 件も適用していません。");

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("入れ替え案の適用が受理されませんでした（{Status}）。", (int)response.StatusCode);
                return new WatchlistApplyOutcome(WatchlistApplyStatus.Rejected, [], null,
                    (await ErrorOf(response, cancellationToken).ConfigureAwait(false) ?? $"HTTP {(int)response.StatusCode}")
                    + "（入れ替えは 1 件も適用していません）");
            }

            var view = await response.Content.ReadFromJsonAsync<ApplyResponseView>(Web, cancellationToken).ConfigureAwait(false);
            if (view?.Items is null || view.Items.Any(i => i is null || i.Symbol is null || i.Action is null))
                return new WatchlistApplyOutcome(WatchlistApplyStatus.Indeterminate, [], null,
                    "入れ替えの適用の応答を解釈できませんでした（適用された可能性があります。設定画面で確認してください）");

            return new WatchlistApplyOutcome(
                WatchlistApplyStatus.Applied,
                [.. view.Items.Select(i => new WatchlistApplyItemView(i!.Action!, i.Symbol!, i.Applied, i.SkipReason))],
                view.Estimate is { } e ? new FinnhubEstimateView(e.EstimatedDailyRequests, e.ProvisionalDailyLimit, e.Exceeds) : null,
                "適用しました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "入れ替え案の適用で例外が発生しました（結果は不明）。");
            return new WatchlistApplyOutcome(WatchlistApplyStatus.Indeterminate, [], null,
                "入れ替えの適用の結果が分かりません（応答が届きませんでした。設定画面で監視銘柄を確認してください）");
        }
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = (await response.Content.ReadFromJsonAsync<ErrorView>(Web, cancellationToken).ConfigureAwait(false))?.Error;
            return string.IsNullOrWhiteSpace(error) ? null : error.Length <= 300 ? error : error[..299] + "…";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // 市場監視の MonitoredSymbol / WatchlistSymbolRef と同形（市場は列挙・数値表現）。
    private sealed record SymbolView(string? Symbol, Market? Market);

    // 市場監視の WatchlistProposalApplyRequest と同形（名前を変えてあるのは送り手の型の目印〔IADR-0420 の検査器〕と区別するため）。
    private sealed record ApplyBody(
        IReadOnlyList<SymbolView> ExpectedWatchlist,
        IReadOnlyList<ChangeBody> Changes,
        string ProposalRef,
        string OnBehalfOf);

    private sealed record ChangeBody(string Action, string Symbol, string Reason);

    private sealed record ApplyResponseView(IReadOnlyList<ItemView?>? Items, string? Actor, EstimateView? Estimate);

    private sealed record ItemView(string? Action, string? Symbol, bool Applied, string? SkipReason);

    private sealed record EstimateView(long EstimatedDailyRequests, int ProvisionalDailyLimit, bool Exceeds);

    private sealed record ErrorView(string? Error);
}
