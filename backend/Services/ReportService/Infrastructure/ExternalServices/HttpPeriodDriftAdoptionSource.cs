using System.Net.Http.Json;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2: 期間の乖離の取り込みを権威源
//（リスク管理 #12 の取引台帳）から s2s 同期照会する（GET /risk-controls/drift-adoptions・OwnerOrService）。
// HttpPeriodFillSource と同型の作法だが、**倒す向きが違う**。
//
// 🔴 **供給不達（非 2xx・timeout・例外・不正応答）は `null`（＝照会できていない）へ倒す。空列へ倒さない。**
// 空列は「該当なし」であり、取り込みは本番で実際に起き得る。空列へ倒すと日報 §2-b が「該当なし」と嘘をつき、
// さらに在庫の畳み込みからも黙って落ちて**実在しない建玉の評価損益**が出る（#859 の主訴そのもの）。
public sealed class HttpPeriodDriftAdoptionSource(HttpClient httpClient, ILogger<HttpPeriodDriftAdoptionSource> logger)
    : IPeriodDriftAdoptionSource
{
    public async Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var path = $"/risk-controls/drift-adoptions?from={fromInclusive:yyyy-MM-dd}&to={toInclusive:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "手動売買の取り込みの照会に失敗しました（{Status}・{From}〜{To}）。未供給として報告書を続けます。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var rows = await response.Content
                .ReadFromJsonAsync<List<DriftAdoptionDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (rows is null)
            {
                logger.LogWarning("手動売買の取り込みの応答が不正（null）でした。未供給として報告書を続けます。");
                return null;
            }

            return Interpret(rows);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("手動売買の取り込みの照会がタイムアウトしました（{From}〜{To}）。未供給として報告書を続けます。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "手動売買の取り込みの照会で例外が発生しました（{From}〜{To}）。未供給として報告書を続けます。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // NFR, IADR-0427 決定 5, #997: 応答の解釈（銘柄の無い行を落として写す）。輸送に依らず 1 つ
    // （GrpcPeriodDriftAdoptionSource も proto を同じ行へ写してから呼ぶ）。中身は切り出す前と同じ式である。
    internal static IReadOnlyList<PeriodDriftAdoption> Interpret(IEnumerable<DriftAdoptionDto> rows) =>
        [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).Select(ToAdoption)];

    internal static PeriodDriftAdoption ToAdoption(DriftAdoptionDto r) => new(
        r.AdoptionId, r.Symbol, r.Market, r.Side, r.Quantity,
        r.LedgerQuantityBefore, r.BrokerQuantity, r.ObservedAt, r.Actor ?? string.Empty, r.Reason ?? string.Empty,
        r.AdoptedAt);

    // 権威源の DriftAdoptionView と同形（camelCase・列挙は数値で往復する）。
    // 🔴 **価格を持たない。** 権威源も返さない——取り込み行の単価は「取り込み時点の平均取得単価」であって
    // 約定価格ではなく、運ぶと受け手が約定単価として扱い得る（IADR-0360 決定 2）。
    internal sealed record DriftAdoptionDto(
        Guid AdoptionId,
        string Symbol,
        Market Market,
        TradeSide Side,
        int Quantity,
        int LedgerQuantityBefore,
        int BrokerQuantity,
        DateTimeOffset ObservedAt,
        string? Actor,
        string? Reason,
        DateTimeOffset AdoptedAt);
}
