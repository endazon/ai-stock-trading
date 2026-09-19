using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉をリスク管理（#12・#63 台帳）の
// GET /risk-controls/open-positions（既存・OwnerOrService）から同期照会する。新規エンドポイントは作らない。
//
// fail-safe の要: 非 2xx・例外・タイムアウト・不正応答は **null（不明）**。空配列は **0（保有なし）**。
// 市場監視の HttpPositionStore は失敗を空列へ倒す（損切り検知対象なし＝そちらの安全側）が、ここで同じことをすると
// 「保有していない」と誤断定して裸の新規売りを通してしまうため、区別を厳格に保つ。
public sealed class HttpHeldPositionProvider(
    HttpClient httpClient,
    ILogger<HttpHeldPositionProvider> logger)
    : IHeldPositionProvider
{
    // #865, IADR-0358: 実結線。RiskManagement:BaseUrl が設定されたときだけ生成されるため常に true。
    // 以後の「不明」は**照会したが答えが得られなかった**ことを意味し、判断側は新規建てを見送る。
    public bool IsEnabled => true;

    public async Task<int?> GetSignedQuantityAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        (await GetPositionAsync(symbol, market, cancellationToken).ConfigureAwait(false))?.SignedQuantity;

    // FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1: 数量に加えて平均取得単価・記録上の損切りラインも読む
    // （同じ応答に既に載っている。従来は数量だけを読んでいた）。失敗＝null（不明）／一覧に無い＝保有なしの区別は不変。
    public async Task<HeldPosition?> GetPositionAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/open-positions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("保有建玉の照会に失敗（{Status}）。不明として扱います。", (int)response.StatusCode);
                return null;
            }

            var positions = await response.Content
                .ReadFromJsonAsync<List<OpenPositionDto>>(cancellationToken)
                .ConfigureAwait(false);
            if (positions is null)
            {
                logger.LogWarning("保有建玉の応答を解釈できません。不明として扱います。");
                return null;
            }

            // 一覧に無ければ保有なし（0）。台帳の射影は数量 0 の建玉を含めない（PortfolioProjection）。
            // 射影は (銘柄, 市場) ごとに 1 行のため、取得単価・損切りラインは一致した行の値をそのまま採る。
            var signed = 0;
            decimal? entryPrice = null;
            decimal? stopLossPrice = null;
            foreach (var p in positions)
            {
                if (!string.Equals(p.Symbol, symbol, StringComparison.Ordinal) || p.Market != market)
                    continue;
                signed += p.Side == TradeSide.Buy ? p.Quantity : -p.Quantity;
                entryPrice = p.EntryPrice;
                stopLossPrice = p.StopLossPrice;
            }

            if (signed == 0)
                return HeldPosition.None;

            // 🔴 応答に無い・正でない価格は「不明」（null）にする。0 で埋めると含み損益と損切り判定が偽の値になる。
            return new HeldPosition(
                signed,
                entryPrice is > 0m ? entryPrice : null,
                stopLossPrice is > 0m ? stopLossPrice : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("保有建玉の照会がタイムアウト。不明として扱います。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有建玉の照会で例外。不明として扱います。");
            return null;
        }
    }

    // OpenPositionView（RiskManagement）の必要フィールドのみ。camelCase・列挙は数値で往復する。
    // 価格 2 項目は nullable（項目を持たない応答を 0 と読まない）。
    private sealed record OpenPositionDto(
        string Symbol, Market Market, TradeSide Side, int Quantity, decimal? EntryPrice, decimal? StopLossPrice);
}
