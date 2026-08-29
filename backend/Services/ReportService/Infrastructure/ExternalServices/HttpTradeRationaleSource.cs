using System.Net.Http.Json;
using System.Text.Json;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-16, FR-11, #563, IADR-0269: 日報 §2「判断根拠（要約）」を**監査台帳**から期間で引く
// （GET /audit/events/by-type・OwnerOrService。IADR-0199 と同型の作法）。
//
// 🔴 **権威源は監査台帳であり、取引判断サービスではない。** 判断サービスは根拠を**ログにしか残さず**、
// 保持もプロセス内である。台帳は `TradeDecisionMade` をイベント全量 JSON で 7 年保持しており、
// 期間の明細はここからしか復元できない（`HttpFxSourceStatusSource` と同じ理由）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 空の辞書（引けたが 1 件も無い）と混ぜない——
// 「根拠を引けなかった」を「根拠が無かった」と書けば、説明責任が果たされていない状態が正常に見える。
public sealed class HttpTradeRationaleSource(
    HttpClient httpClient,
    ILogger<HttpTradeRationaleSource> logger)
    : ITradeRationaleSource
{
    // 🔴 **書き手と同じ 1 つの定義を使う**（`AuditDetailJson`・IADR-0199 決定6）。
    // 設定を書き写すと、命名規約を片側だけ変えたときに `JsonSerializer` が例外を投げず**既定値で埋めた
    // record を黙って作る**——空の根拠が「記録が無い」と同じ顔で明細に載る。
    private static JsonSerializerOptions DetailOptions => AuditDetailJson.Options;

    // 引く種別。**イベント型名がそのまま台帳の EventType である**（AuditEntryFactory が nameof で書く）。
    private static readonly string[] WantedTypes = [nameof(TradeDecisionMade)];

    public async Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
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
                    "取引判断の根拠の照会に失敗しました（{Status}・{From}〜{To}）。"
                        + "**未供給として扱います**（「根拠なし」とは書きません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<IReadOnlyList<AuditEntryDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (entries is null)
            {
                logger.LogWarning("取引判断の根拠の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            return Build(entries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "取引判断の根拠の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "取引判断の根拠の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 台帳の記録を DecisionId 引きの辞書へ戻す。**壊れた 1 件で期間全体を落とさない**——
    // 読めなかった記録は捨ててログへ残す（**黙って捨てない**）。当該約定の根拠だけが未供給になる。
    private Dictionary<Guid, string> Build(IReadOnlyList<AuditEntryDto> entries)
    {
        var rationales = new Dictionary<Guid, string>();

        foreach (var e in entries)
        {
            if (e.EventType != nameof(TradeDecisionMade))
            {
                // 要求していない種別が返った＝台帳側の絞り込みが効いていない。混ぜずに落とす。
                logger.LogWarning("要求していない監査種別が返りました（{EventType}）。無視します。", e.EventType);
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<TradeDecisionMade>(e.Detail, DetailOptions) is not { } made)
                {
                    logger.LogWarning("監査記録の本文が空でした（{Id}）。当該 1 件を無視します。", e.Id);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(made.Rationale))
                    continue; // 根拠が空の記録は載せない（空文字を根拠として提示しない）。

                // 同一 DecisionId が複数回記録されることは無い想定だが、**先勝ちで固定**して描画を決定的にする。
                rationales.TryAdd(made.DecisionId, made.Rationale);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "監査記録の本文を復元できませんでした（{EventType} / {Id}）。当該 1 件を無視します。",
                    e.EventType, e.Id);
            }
        }

        return rationales;
    }

    // 監査台帳の応答の受け皿。**必要な 3 項目だけ**を受ける（残りは報告書が使わない）。
    private sealed record AuditEntryDto(Guid Id, string EventType, string Detail);
}
