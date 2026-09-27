using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 3: 当時の監視銘柄を市場監視の
// GET /monitor/watchlist/as-of?at=（OwnerOrService・trading-service の s2s）から読む。
//
// 🔴 **読めなかったことを空の一覧へ倒さない**（原則 A）。非 2xx・打ち切り・例外・null・`reconstructed` の欠落・再構成できたのに一覧が無い・
// 欠けた行（銘柄が空・市場の欠落／値域外）・null の行は、すべて「再構成できない」（理由つき）にする。その記録は合否から外れる。
// 呼び出し側のキャンセルは伝播する（IADR-0440 F6 と同じ）。
public sealed class HttpAsOfWatchlistSource(HttpClient httpClient, ILogger<HttpAsOfWatchlistSource> logger)
    : IAsOfWatchlistSource
{
    public async Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        // 時刻は UTC の「Z」付きで送る（`+` を含むオフセットはクエリで空白に化け得る）。
        var query = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        try
        {
            using var response = await httpClient
                .GetAsync($"/monitor/watchlist/as-of?at={Uri.EscapeDataString(query)}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Unavailable($"市場監視の照会に失敗しました（{(int)response.StatusCode}）。");

            var body = await response.Content
                .ReadFromJsonAsync<AsOfBody>(cancellationToken)
                .ConfigureAwait(false);

            if (body?.Reconstructed is not { } reconstructed)
                return Unavailable("市場監視の応答に再構成の可否（reconstructed）がありません。");

            if (!reconstructed)
                return AsOfWatchlist.NotReconstructable(body.Reason ?? "市場監視が再構成できないと答えました（理由なし）。");

            if (body.Symbols is not { } rows
                || rows.Any(r => r is null || string.IsNullOrWhiteSpace(r.Symbol) || r.Market is not { } m || !Enum.IsDefined(m)))
            {
                return Unavailable("市場監視の応答の一覧が無いか、欠けた行（銘柄が空・市場の欠落または値域外）があります。");
            }

            return AsOfWatchlist.Reconstructed([.. rows.Select(r => new WatchedSymbol(r!.Symbol!, r.Market!.Value))]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("市場監視の照会がタイムアウトしました。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "当時の監視銘柄の照会で例外。");
            return Unavailable("市場監視の照会で例外が起きました。");
        }
    }

    private AsOfWatchlist Unavailable(string reason)
    {
        logger.LogWarning("当時の監視銘柄を読めません: {Reason}", reason);
        return AsOfWatchlist.NotReconstructable(reason);
    }

    // 応答（市場監視の WatchlistAsOfResponse と同形。camelCase・列挙は数値）。欠落を検出するため項目は nullable で受ける。
    private sealed record AsOfBody(bool? Reconstructed, List<AsOfRow?>? Symbols, string? Reason);

    private sealed record AsOfRow(string? Symbol, Market? Market);
}
