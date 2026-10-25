using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// 🔴 FR-01, FR-02, #1363, IADR-0547 決定 1: 保有中の銘柄をリスク管理の GET /risk-controls/open-positions（既存・OwnerOrService）から
// s2s（trading-service）で読む。取引判断の HttpHeldPositionProvider.GetHeldSymbolsAsync（IADR-0521 決定 1）と同じ口・同じ解釈の規則。
//
// 🔴 **原則 A: どの失敗も「分からない」（null）で返す。空の一覧へ倒さない。** 非 2xx・タイムアウト・例外・読めない本文に加え、
// 銘柄・市場・方向・数量の欠けた行、数量が正でない行が 1 つでもあれば一覧ごと不明とする（#943・#854 の規則）。欠けた行を飛ばすと、
// その行の銘柄が黙って収集の対象から落ちる。
public sealed class HttpRiskManagementHeldSymbolsProvider(
    HttpClient httpClient,
    ILogger<HttpRiskManagementHeldSymbolsProvider> logger)
    : IHeldSymbolsProvider
{
    public const string OpenPositionsPath = "/risk-controls/open-positions";

    public async Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync(OpenPositionsPath, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("保有銘柄（open-positions）の照会に失敗しました（{Status}）。", (int)response.StatusCode);
                return null;
            }

            var rows = await response.Content
                .ReadFromJsonAsync<List<OpenPositionRow?>>(cancellationToken)
                .ConfigureAwait(false);
            if (rows is null)
            {
                logger.LogWarning("保有銘柄（open-positions）の応答が不正（null）です。");
                return null;
            }

            return Interpret(rows, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("保有銘柄（open-positions）の照会がタイムアウトしました。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有銘柄（open-positions）の照会で例外が発生しました。");
            return null;
        }
    }

    // 応答の全行から保有中の (銘柄, 市場) を導く。規則は取引判断の HttpHeldPositionProvider.InterpretHeldSymbols と同じ
    // （1 行でも欠けていれば一覧ごと不明。(銘柄, 市場) ごとに符号付きで合計し、0 でないものだけを応答の順で残す）。
    internal static IReadOnlyList<WatchedSymbol>? Interpret(IReadOnlyList<OpenPositionRow?> rows, ILogger logger)
    {
        if (rows.Any(r => r is null
                          || string.IsNullOrWhiteSpace(r.Symbol)
                          || r.Market is not { } market
                          || !Enum.IsDefined(market)
                          || r.Side is not { } side
                          || !Enum.IsDefined(side)
                          || r.Quantity is not > 0))
        {
            logger.LogWarning(
                "保有銘柄（open-positions）の応答に銘柄・市場・方向・数量の欠けた、または数量が正でない行があるため、一覧ごと不明として扱います。");
            return null;
        }

        return rows
            .GroupBy(r => (Symbol: r!.Symbol!.Trim(), Market: r.Market!.Value))
            .Where(g => g.Sum(r => r!.Side == TradeSide.Buy ? r.Quantity!.Value : -r.Quantity!.Value) != 0)
            .Select(g => new WatchedSymbol(g.Key.Symbol, g.Key.Market))
            .ToList();
    }

    // OpenPositionView（RiskManagement）の必要な項目だけ（web 既定＝camelCase・列挙は数値）。
    // 欠落を既定値（銘柄 null・市場 0＝日本・方向 0＝買い・数量 0）と区別するため nullable で受ける。
    internal sealed record OpenPositionRow(string? Symbol, Market? Market, TradeSide? Side, int? Quantity);
}
