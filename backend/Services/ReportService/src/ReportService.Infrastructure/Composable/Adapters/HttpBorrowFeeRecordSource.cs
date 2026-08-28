using System.Net.Http.Json;
using System.Text.Json;
using ReportService.Application.Ports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.Adapters;

// FR-06, FR-11, #338, ADR-0016 決定15, ADR-0027 決定1・決定4, 04_report-templates 月報 §6.1 / 日報 §4, IADR-0254:
// 当期間の借株料の記録を**監査台帳**から引く（GET /audit/events/by-type・OwnerOrService）。
//
// 🔴 **計上できた日（BorrowFeeAccrued）と、料率が取れず未計上だった日（BorrowFeeAccrualUnavailable）を
// 別の列で受ける**（ADR-0027 決定4）。1 つへ畳むと、未計上ぶんが 0 円として合計へ混ざり
// **借株コストが実際より安く見える**。契約が 2 つのイベントに分かれているのは、まさにその混同を防ぐためである。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。**
internal sealed class HttpBorrowFeeRecordSource(
    HttpClient httpClient,
    ILogger<HttpBorrowFeeRecordSource> logger)
    : IBorrowFeeRecordSource
{
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    private static readonly string[] WantedTypes =
    [
        nameof(BorrowFeeAccrued),
        nameof(BorrowFeeAccrualUnavailable),
    ];

    public async Task<BorrowFeeRecord?> GetBorrowFeesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = AuditPeriodRange.JstHalfOpen(fromInclusive, toInclusive);
        var path = "/audit/events/by-type"
            + $"?from={Uri.EscapeDataString(from.ToString("o"))}"
            + $"&to={Uri.EscapeDataString(to.ToString("o"))}"
            + $"&types={string.Join(",", WantedTypes)}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "借株料の記録の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（0 USD とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditEntryDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("借株料の記録の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "借株料の記録の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "借株料の記録の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    private BorrowFeeRecord Build(IReadOnlyList<AuditEntryDto> entries)
    {
        var accruals = new List<BorrowFeeAccrued>();
        var unavailable = new List<BorrowFeeAccrualUnavailable>();

        foreach (var e in entries)
        {
            switch (e.EventType)
            {
                case nameof(BorrowFeeAccrued):
                    Add(accruals, e);
                    break;
                case nameof(BorrowFeeAccrualUnavailable):
                    Add(unavailable, e);
                    break;
                default:
                    logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                    break;
            }
        }

        return new BorrowFeeRecord(accruals, unavailable);
    }

    private void Add<T>(List<T> into, AuditEntryDto entry)
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(entry.Detail, DetailOptions) is { } value)
            {
                into.Add(value);
                return;
            }

            logger.LogWarning("監査記録の本文が空でした（{EventType} / {Id}）。当該 1 件を無視します。", entry.EventType, entry.Id);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "監査記録の本文を復元できませんでした（{EventType} / {Id}）。当該 1 件を無視します。",
                entry.EventType, entry.Id);
        }
    }

    private sealed record AuditEntryDto(Guid Id, string EventType, string Detail);
}
