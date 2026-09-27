using System.Net.Http.Json;
using System.Text.Json;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, FR-16, #338, #282, #347, ADR-0017 決定2・決定4, 04_report-templates 月報 §7, IADR-0254:
// 当期間の LLM 利用実績を**監査台帳**から引く（GET /audit/events/by-type・OwnerOrService）。
//
// 🔴 **権威源は監査台帳であり、費用統制サービスの月次カウンタではない。**
// あちらは **月次上限の対象ぶんしか積まない**（`LlmCostScope.IsGoverned` が false の用途は別カテゴリ）ため、
// **報告書生成の費用が引けない**——#282 で計上点を作ったのに月報へ出せない、という状態になる。
// 台帳はイベント全量を JSON で 7 年保持しており、用途別の実測値はここからしか復元できない。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 同居する HttpFxSourceStatusSource・
// HttpBuyInInferenceRecordSource と同じ向きであり、HttpPeriodFillSource（空列へ倒す）とは逆である。
// **揃えてはならない**——費用 0 円・スキップ 0 件と書けば、計上漏れが正常として読まれる。
public sealed class HttpLlmUsageRecordSource(
    HttpClient httpClient,
    ILogger<HttpLlmUsageRecordSource> logger)
    : ILlmUsageRecordSource
{
    // 書き手と同じ 1 つの定義を使う（AuditDetailJson・IADR-0199 決定6）。
    // 命名規約を片側だけ変えると JsonSerializer は例外を投げず既定値で埋めた record を黙って作る。
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // 引く種別。**イベント型名がそのまま台帳の EventType である**（AuditEntryFactory が nameof で書く）。
    //
    // 🔴 スクリーニング入力の縮退（分割 / 切り詰め・INDEX 決定44）の種別はまだ存在しない。
    // 発生源はスクリーニング層（取引判断サービス）にあり、本 PR の範囲外である。
    // **ここへ推測で種別名を足さない**——存在しない種別を要求すると台帳は 0 件を返し、
    // それは「縮退が無かった」ではなく「そもそも記録されていない」である。**未供給として描く。**
    internal static readonly string[] WantedTypes =
    [
        nameof(LlmCostIncurred),
        nameof(LlmFallbackFired),
        nameof(TradeDecisionSkipped),
    ];

    // NFR, IADR-0445 決定 4, #1059 (#753): 照会の窓・引く種別・記録の解釈は gRPC 実装（Grpc*）と共有する（`internal static`）。
    // 輸送を差し替えても引く範囲と読み方が変わらないように、ここを唯一の定義にする。
    /// <summary>照会の窓（JST の暦日 → 半開区間 [from 00:00 JST, to+1 日 00:00 JST)）。</summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Window(DateOnly fromInclusive, DateOnly toInclusive) =>
        AuditPeriodRange.JstHalfOpen(fromInclusive, toInclusive);

    public async Task<LlmUsageRecord?> GetUsageAsync(
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
                    "LLM 利用実績の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（費用 0 円・スキップ 0 件とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditLedgerEntry>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("LLM 利用実績の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "LLM 利用実績の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "LLM 利用実績の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 壊れた 1 件で期間全体を落とさない（読めなかった記録は捨ててログへ残す＝黙って捨てない）。
    internal static LlmUsageRecord Build(IReadOnlyList<AuditLedgerEntry> entries, ILogger logger)
    {
        var costs = new List<LlmCostIncurred>();
        var fallbacks = new List<LlmFallbackFired>();
        var skips = new List<TradeDecisionSkipped>();

        foreach (var e in entries)
        {
            switch (e.EventType)
            {
                case nameof(LlmCostIncurred):
                    Add(costs, e, logger);
                    break;
                case nameof(LlmFallbackFired):
                    Add(fallbacks, e, logger);
                    break;
                case nameof(TradeDecisionSkipped):
                    Add(skips, e, logger);
                    break;
                default:
                    logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                    break;
            }
        }

        // ScreeningDegradation は null のまま返す（供給元が未実装。**0 回 / 0 件と書かない**）。
        return new LlmUsageRecord(costs, fallbacks, skips, ScreeningDegradation: null);
    }

    private static void Add<T>(List<T> into, AuditLedgerEntry entry, ILogger logger)
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
}
