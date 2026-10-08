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
    internal static readonly string[] WantedTypes = [nameof(OrderApproved)];

    // NFR, IADR-0445 決定 4, #1059 (#753): 照会の窓・引く種別・記録の解釈は gRPC 実装（Grpc*）と共有する（`internal static`）。
    // 輸送を差し替えても引く範囲と読み方が変わらないように、ここを唯一の定義にする。
    /// <summary>照会の窓（JST の暦日 → 半開区間 [from 00:00 JST, to+1 日 00:00 JST)）。</summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Window(DateOnly fromInclusive, DateOnly toInclusive) =>
        AuditPeriodRange.JstHalfOpen(fromInclusive, toInclusive);

    public async Task<StopLossMethodUsage?> GetUsageAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        // 🔴 半開区間 [from 00:00 JST, to+1 日 00:00 JST)。作り方は AuditPeriodRange に集約してある。
        var (from, to) = Window(fromInclusive, toInclusive);
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
                .ReadFromJsonAsync<IReadOnlyList<AuditLedgerEntry>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("承認の記録の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries, logger);
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
    // FR-06, IADR-0516（2026-10-08 追記）, #1255: 読めなかった記録ごとの台帳の記録時刻も返す（報告書のセッションの窓で 1 回だけ数えるため。
    // 時刻を運ばない旧版の台帳では null＝従来どおり照会の範囲で数える）。
    internal static StopLossMethodUsage Build(IReadOnlyList<AuditLedgerEntry> entries, ILogger logger)
    {
        var approvals = new List<OrderApproved>();
        var unreadable = new List<DateTimeOffset?>();

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
                unreadable.Add(e.OccurredAt);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "承認の記録の本文を復元できませんでした（{Id}）。当該 1 件を件数から除きます。", e.Id);
                unreadable.Add(e.OccurredAt);
            }
        }

        return StopLossMethodUsage.From(approvals, unreadable.Count) with { UnreadableOccurredAt = unreadable };
    }
}
