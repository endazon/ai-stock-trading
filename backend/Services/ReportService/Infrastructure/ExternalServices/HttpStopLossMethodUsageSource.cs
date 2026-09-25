using System.Net.Http.Json;
using System.Text.Json;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-10, FR-11, ADR-0040 決定1, #823, IADR-0422 決定3: 日報 §4「損切りの実行機構（当日）」の供給。
// 当期間の承認（`OrderApproved`）を**監査台帳**から引く（GET /audit/events/by-type・OwnerOrService。IADR-0199 と同型）。
//
// 🔴 **権威源は承認そのもの（承認が運ぶ手法）であり、リスク管理の現在の設定値ではない。** 設定値は日報を作る時点の
// 1 値であり、日中に手法を変えた日を塗り潰す。台帳は `OrderApproved` をイベント全量 JSON で 7 年保持している。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 空の集計（承認 0 件）と混ぜない。
public sealed class HttpStopLossMethodUsageSource(
    HttpClient httpClient,
    ILogger<HttpStopLossMethodUsageSource> logger)
    : IStopLossMethodUsageSource
{
    // 🔴 **書き手と同じ 1 つの定義を使う**（`AuditDetailJson`・IADR-0199 決定6。列挙は文字列で書かれている）。
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // 引く種別。**イベント型名がそのまま台帳の EventType である**（AuditEntryFactory が nameof で書く）。
    private static readonly string[] WantedTypes = [nameof(OrderApproved)];

    public async Task<StopLossMethodUsage?> GetUsageAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        // 🔴 半開区間 [from 00:00 JST, to+1 日 00:00 JST)。作り方は AuditPeriodRange に集約してある。
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
                    "承認の記録（損切りの実行機構）の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（「承認なし」とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditEntryDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("承認の記録の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "承認の記録の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "承認の記録の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 台帳の記録を承認へ戻して数える。**壊れた 1 件で期間全体を落とさない**——読めなかった記録は件数から除き、
    // その数を別に返す（日報が「復元できなかった承認 N 件」と書く。**黙って落とさない**）。
    private StopLossMethodUsage Build(IReadOnlyList<AuditEntryDto> entries)
    {
        var approvals = new List<OrderApproved>();
        var unreadable = 0;

        foreach (var e in entries)
        {
            if (e.EventType != nameof(OrderApproved))
            {
                // 要求していない種別が返った＝台帳側の絞り込みが効いていない。混ぜずに落とす（数えもしない）。
                logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<OrderApproved>(e.Detail, DetailOptions) is { Intent: not null } approved)
                {
                    approvals.Add(approved);
                    continue;
                }

                logger.LogWarning("承認の記録の本文が空でした（{Id}）。当該 1 件を件数から除きます。", e.Id);
                unreadable++;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "承認の記録の本文を復元できませんでした（{Id}）。当該 1 件を件数から除きます。", e.Id);
                unreadable++;
            }
        }

        return StopLossMethodUsage.From(approvals, unreadable);
    }

    // 監査台帳の応答の受け皿。**必要な 3 項目だけ**を受ける。
    private sealed record AuditEntryDto(Guid Id, string EventType, string Detail);
}
