using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-10, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定4: 発注執行の損切りの実行機構の解決結果を**監査台帳**から引く
// （GET /audit/events/by-type・OwnerOrService。承認の供給 HttpStopLossMethodUsageSource と同型）。
//
// 🔴 **照会の窓は報告期間の前後 1 日を含める。** 解決結果は承認に属し（DecisionId で照合）、承認の日付で数える。
// 承認と解決は別サービスの時計で刻まれ、日付の境界（JST 0 時）を跨ぎ得る——期間ぴったりで引くと、
// 境界際の承認の解決結果を「記録が見つからない」と誤って書く。期間外の解決結果は照合で捨てられる。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 空の記録と混ぜない。
public sealed class HttpStopLossMethodResolutionSource(
    HttpClient httpClient,
    ILogger<HttpStopLossMethodResolutionSource> logger)
    : IStopLossMethodResolutionSource
{
    /// <summary>照会の窓を報告期間の前後へ広げる日数。</summary>
    public const int WindowMarginDays = 1;

    // 🔴 **書き手と同じ 1 つの定義を使う**（`AuditDetailJson`・IADR-0199 決定6。列挙は文字列で書かれている）。
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // NFR, IADR-0445 決定 4, #1059 (#753): 照会の窓・引く種別・記録の解釈は gRPC 実装（Grpc*）と共有する（`internal static`）。
    // 輸送を差し替えても引く範囲と読み方が変わらないように、ここを唯一の定義にする。
    internal static readonly string[] WantedTypes = [nameof(StopLossMethodResolved)];

    /// <summary>照会の窓（報告期間の前後 <see cref="WindowMarginDays"/> 日を含む JST の半開区間）。</summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Window(DateOnly fromInclusive, DateOnly toInclusive) =>
        AuditPeriodRange.JstHalfOpen(fromInclusive.AddDays(-WindowMarginDays), toInclusive.AddDays(WindowMarginDays));

    public async Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
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
                    "損切りの実行機構の解決結果の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（「記録なし」とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditLedgerEntry>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("解決結果の記録の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "解決結果の記録の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "解決結果の記録の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // **壊れた 1 件で期間全体を落とさない**——読めなかった記録は除き、その数を別に返す（報告書が件数を書く）。
    internal static StopLossMethodResolutionFeed Build(IReadOnlyList<AuditLedgerEntry> entries, ILogger logger)
    {
        var resolutions = new List<StopLossMethodResolved>();
        var unreadable = 0;

        foreach (var e in entries)
        {
            if (e.EventType != nameof(StopLossMethodResolved))
            {
                // 要求していない種別が返った＝台帳側の絞り込みが効いていない。混ぜずに落とす（数えもしない）。
                logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<StopLossMethodResolved>(e.Detail, DetailOptions) is { Symbol: not null } resolved)
                {
                    resolutions.Add(resolved);
                    continue;
                }

                logger.LogWarning("解決結果の記録の本文が空でした（{Id}）。当該 1 件を除きます。", e.Id);
                unreadable++;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "解決結果の記録の本文を復元できませんでした（{Id}）。当該 1 件を除きます。", e.Id);
                unreadable++;
            }
        }

        return new StopLossMethodResolutionFeed(resolutions, unreadable);
    }
}
