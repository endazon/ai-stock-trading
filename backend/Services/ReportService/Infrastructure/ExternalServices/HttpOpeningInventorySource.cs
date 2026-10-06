using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #1181, IADR-0493 決定 2・4: 期間開始時点の在庫を権威源（リスク管理の取引台帳）から s2s 同期照会する
// （GET /risk-controls/opening-inventory?market&before・OwnerOrService）。HttpPeriodDriftAdoptionSource と同型の作法・同じ向き。
//
// 🔴 **供給不達（非 2xx・timeout・例外・不正応答）は `null`（照会できていない）へ倒す。空列へ倒さない。**
// 🔴 **応答の行に必須の項目（銘柄・市場・向き・数量・平均取得単価・未記録の数）が欠けていれば応答全体を読めない**（null）。
// 既定値（日本・買い・0）で在庫を作ると、存在しない建玉の取得原価で実現損益を算定することになる（原則 A。IADR-0427 決定 3）。
public sealed class HttpOpeningInventorySource(HttpClient httpClient, ILogger<HttpOpeningInventorySource> logger)
    : IOpeningInventorySource
{
    public async Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
        Market market, DateOnly beforeTradingDay, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"/risk-controls/opening-inventory?market={market}&before={beforeTradingDay:yyyy-MM-dd}");

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "期間開始時点の在庫の照会に失敗しました（{Status}・{Market}・{Before} より前）。未供給として報告書を続けます。",
                    (int)response.StatusCode, market, beforeTradingDay);
                return null;
            }

            var rows = await response.Content
                .ReadFromJsonAsync<List<OpeningInventoryDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (rows is null || Interpret(rows, market) is not { } lots)
            {
                logger.LogWarning(
                    "期間開始時点の在庫の応答が不正でした（null・必須項目の欠落・市場の食い違い）。未供給として報告書を続けます。");
                return null;
            }

            return lots;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("期間開始時点の在庫の照会がタイムアウトしました（{Market}・{Before} より前）。未供給として報告書を続けます。",
                market, beforeTradingDay);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "期間開始時点の在庫の照会で例外が発生しました（{Market}・{Before} より前）。未供給として報告書を続けます。",
                market, beforeTradingDay);
            return null;
        }
    }

    /// <summary>
    /// NFR, IADR-0427 決定 5, IADR-0493: 応答の解釈（輸送に依らず 1 つ。gRPC も proto を同じ行へ写してから呼ぶ）。
    /// 1 行でも読めなければ <c>null</c>（応答全体を読めない）。数量 0 の行は返らない契約なので、0 以下も読めない行とする。
    /// </summary>
    internal static IReadOnlyList<OpeningLot>? Interpret(IEnumerable<OpeningInventoryDto> rows, Market requested)
    {
        var lots = new List<OpeningLot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Symbol)
                || r.Market is not { } market || market != requested
                || r.Side is not { } side || !Enum.IsDefined(side)
                || r.Quantity is not > 0
                || r.AverageCostInBase is not { } averageCost || averageCost < 0m
                || r.UnrecordedFxRateFillCount is not { } unrecorded || unrecorded < 0
                // #1181（独立監査 🟢1）: 送り手の不変条件「レートが無い ⇔ 未記録の行がある」と「レートは正」を受け手でも確かめる。
                || (r.AverageFxRateBaseToDisplay is null) != (unrecorded > 0)
                || r.AverageFxRateBaseToDisplay is <= 0m
                // 同じ (銘柄, 市場) は 1 行の契約（重複は後勝ちで黙って畳まない）。市場は requested に揃っているので銘柄で見る。
                || !seen.Add(r.Symbol))
            {
                return null;
            }

            var quantity = r.Quantity.Value;
            lots.Add(new OpeningLot(
                r.Symbol, market, side == TradeSide.Buy ? quantity : -quantity, averageCost,
                r.AverageFxRateBaseToDisplay, unrecorded));
        }

        return lots;
    }

    // 権威源の OpeningInventoryView と同形（camelCase・列挙は数値で往復する）。🔴 **全項目を nullable で受ける**
    // ——送り手の改名・欠落を既定値（日本・買い・0）で読まない（#943 / #957 と同じ作法）。
    internal sealed record OpeningInventoryDto(
        string? Symbol,
        Market? Market,
        TradeSide? Side,
        int? Quantity,
        decimal? AverageCostInBase,
        decimal? AverageFxRateBaseToDisplay,
        int? UnrecordedFxRateFillCount);
}
