using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 定時サイクルの監視銘柄を権威源（市場監視 #10 MarketMonitor）の
// GET /monitor/watchlist（OwnerOrService・IADR-0051）から s2s 同期照会する。SizingContext（IADR-0029）と同型の作法。
// 照会成功なら SC-02/API で変更された最新 watchlist を返し、以後の定時サイクルへ反映する。
// 供給不達（非 2xx・timeout・例外・不正応答）は fallback（構成ベース＝既定 watchlist・IADR-0095）へ委譲する fail-safe。
// FR-04, #1034, IADR-0440 決定 2: 判断のプロンプト用の口（GetAuthoritativeWatchlistAsync）は同じ照会を使い、
// 供給不達を fallback へ倒さず null（不明）で返す。
public sealed class HttpWatchlistProvider(
    HttpClient httpClient,
    IWatchlistProvider fallback,
    ILogger<HttpWatchlistProvider> logger)
    : IWatchlistProvider
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var rows = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (rows is not null)
        {
            // 従来どおりの寛容な読み: 銘柄が空の行は除外し、市場が欠けた行は列挙の既定値で読む（定時サイクルの判断対象）。
            return [.. rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
                .Select(r => new WatchedSymbol(r.Symbol!, r.Market ?? default))];
        }

        logger.LogWarning("監視銘柄（watchlist）を権威源から読めないため、既定 watchlist（構成）へフォールバックします。");
        return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
    }

    // #1034, IADR-0440 決定 2: 読めなければ null（不明）。構成の固定リストへは倒さない。
    // 🔴 PR #1041 の監査 F1: **1 行でも欠けていれば一覧ごと不明**にする（銘柄が空・null、市場が欠落・値域外）。
    // 上の定時サイクル用の寛容な読み（空の行を黙って落とす・欠けた市場を既定値で読む）をここで使うと、200 の応答が
    // 「0 件」や「別の市場の銘柄」へ化け、プロンプトが「この銘柄は対象外」と事実でないことを書く（原則 A）。
    public async Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var rows = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (rows is null)
            return null;

        if (rows.Any(r => string.IsNullOrWhiteSpace(r.Symbol) || r.Market is not { } m || !Enum.IsDefined(m)))
        {
            logger.LogWarning("監視銘柄（watchlist）の応答に欠けた行（銘柄が空・市場が欠落または値域外）があるため、判断のプロンプトには不明と書きます。");
            return null;
        }

        return [.. rows.Select(r => new WatchedSymbol(r.Symbol!, r.Market!.Value))];
    }

    // 権威源の照会。読めた行（空の配列を含む）か、供給不達・null 応答・null の行を含む応答なら null。
    // 行の検証（空の銘柄・市場の欠落）は口ごとに違うため、ここでは行を加工しない。キャンセル（呼び出し側の停止要求）は伝播する。
    private async Task<IReadOnlyList<WatchlistRow>?> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/monitor/watchlist", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("監視銘柄（watchlist）の照会に失敗（{Status}）。", (int)response.StatusCode);
                return null;
            }

            // MonitoredSymbol（MarketMonitorService.Domain）と同形。camelCase・列挙は数値で往復する。
            // 行の項目は nullable で受ける（欠落を列挙の既定値〔Japan〕と区別するため。#1041 監査 F1）。
            var rows = await response.Content
                .ReadFromJsonAsync<List<WatchlistRow?>>(cancellationToken)
                .ConfigureAwait(false);

            if (rows is null || rows.Any(r => r is null))
            {
                logger.LogWarning("監視銘柄（watchlist）の応答が不正（null）。");
                return null;
            }

            return [.. rows.Select(r => r!)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("監視銘柄（watchlist）の照会がタイムアウト。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "監視銘柄（watchlist）の照会で例外。");
            return null;
        }
    }

    // 応答の 1 行（MonitoredSymbol と同形）。欠落を検出するため項目は nullable。
    private sealed record WatchlistRow(string? Symbol, Market? Market);
}
